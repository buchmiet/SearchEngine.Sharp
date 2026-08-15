using System.Text.Json;

namespace SearchEngine.CompetitorBenchmarks;

internal static class CorpusExporter
{
    internal static string ExportIfMissing(string rootDir)
    {
        string corpusPath = Path.Combine(rootDir, "corpus", "file-search-100k-seed2026.json");
        if (File.Exists(corpusPath))
            return corpusPath;

        Directory.CreateDirectory(Path.GetDirectoryName(corpusPath)!);
        var corpus = CorpusGenerator.Create(documentCount: 100_000, seed: 2026);
        File.WriteAllText(corpusPath, JsonSerializer.Serialize(corpus, JsonConfig.Options));
        Console.WriteLine($"Exported corpus: {corpusPath} ({corpus.Documents.Count:N0} docs)");
        return corpusPath;
    }

    internal static CorpusFile Load(string corpusPath)
    {
        string json = File.ReadAllText(corpusPath);
        return JsonSerializer.Deserialize<CorpusFile>(json, JsonConfig.Options)
            ?? throw new InvalidOperationException($"Failed to parse corpus: {corpusPath}");
    }
}

/// <summary>Same data as <c>FileSearchDataFactory</c> — inlined so historical Sharp worktrees build without the benchmarks project.</summary>
internal static class CorpusGenerator
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

    internal static CorpusFile Create(int documentCount, int seed)
    {
        var rng = new Random(seed);
        var documents = new List<CorpusDocument>(documentCount);
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
            documents.Add(new CorpusDocument(id, name, sizeBytes, modifiedTicks));
        }

        return new CorpusFile(
            documentCount,
            seed,
            WithinQuery: "report",
            ExactQuery: documents[1].Name,
            GlobQuery: "*.pdf",
            ZeroHitQuery: "zzzznotfound999",
            FacetMinSize: 1_024,
            FacetMaxSize: 1_048_576,
            documents);
    }
}
