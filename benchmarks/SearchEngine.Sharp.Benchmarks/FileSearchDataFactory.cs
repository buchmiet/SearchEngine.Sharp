namespace SearchEngine.Sharp.Benchmarks;

using SearchEngine;
using SearchEngine.Filters;

/// <summary>
/// Realistic file-manager corpus: short file names, extensions, duplicates, and facet metadata.
/// </summary>
public static class FileSearchDataFactory
{
    private static readonly string[] NameStems =
    [
        "IMG_{0:yyyy-MM-dd}_{1:D4}",
        "Report Final ({1})",
        "System.Collections.Immutable",
        "invoice-{1:D5}",
        "ubuntu-24.04.3-desktop-amd64",
        "notes",
        "backup",
        "config",
        "readme",
        "temp",
    ];

    private static readonly string[] Extensions =
        [".jpg", ".pdf", ".dll", ".xlsx", ".iso", ".txt", ".json", ".cs", ".png", ".zip"];

    public static BenchData Create(int documentCount, int seed)
    {
        var rng = new Random(seed);
        var documents = new List<BenchDocument>(documentCount);
        var anchor = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc);
        long nowTicks = anchor.Ticks;
        var date = anchor;

        for (int id = 0; id < documentCount; id++)
        {
            string stem = NameStems[id % NameStems.Length];
            string name = string.Format(stem, date.AddDays(-(id % 90)), id % 10_000);
            if (id % 3 != 0)
                name += Extensions[id % Extensions.Length];

            long sizeBytes = id % 7 == 0 ? 0 : rng.Next(512, int.MaxValue);
            long modifiedTicks = nowTicks - rng.Next(0, 365) * TimeSpan.TicksPerDay;
            documents.Add(new BenchDocument(id, name, [name], sizeBytes, modifiedTicks));
        }

        string withinQuery = "report";
        string exactQuery = documents[1].Terms[0];
        string globQuery = "*.pdf";
        string booleanQuery = "report OR invoice";

        return new BenchData(
            documents,
            ExactQueries: [exactQuery, "IMG_2026-08-15_1234.jpg", "System.Collections.Immutable.dll"],
            InfixQueries: [withinQuery, "tion", "2026"],
            GlobQueries: [globQuery, "*.jpg", "invoice*"],
            BooleanQueries: [booleanQuery, "report AND NOT backup"]);
    }

    public static Dictionary<int, IndexedEntry> ToIndexedEntries(IReadOnlyList<BenchDocument> documents)
    {
        var map = new Dictionary<int, IndexedEntry>(documents.Count);
        foreach (var doc in documents)
        {
            map[doc.Id] = new IndexedEntry(
                doc.Text,
                doc.Text,
                FacetValues.FromDictionary(new Dictionary<string, long>
                {
                    ["size"] = doc.SizeBytes,
                    ["modified"] = doc.ModifiedTicks,
                }));
        }

        return map;
    }
}
