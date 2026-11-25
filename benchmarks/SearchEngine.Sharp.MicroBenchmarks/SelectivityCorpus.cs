using SearchEngine.Filters;
using SearchEngine.Sharp.Benchmarks;

namespace SearchEngine.Sharp.MicroBenchmarks;

/// <summary>
/// The file-name corpus of the selectivity benchmarks, with one search word per density and a facet filter that
/// passes half of the documents.
/// </summary>
/// <remarks>
/// Each document keeps its file name as the sort text. Its search text adds the word of every density whose
/// random sample contains the document, so an Exact or Within query for <see cref="WordFor"/> of a density matches exactly
/// that share of the documents. The filter keeps the documents modified at or after the median time; which
/// documents pass is unrelated to the density words, so a query at density d keeps about d/2 after the filter.
/// </remarks>
internal sealed class SelectivityCorpus
{
    /// <summary>
    /// Hit densities of the selectivity benchmarks: 0.1%, 1%, 10%, 50%, 80% and 100%, and points between them
    /// around the thresholds of SelectivityThresholds.
    /// </summary>
    internal static double[] Densities { get; } = [0.001, 0.01, 0.1, 0.25, 0.5, 0.65, 0.8, 0.9, 1.0];

    private SelectivityCorpus(Dictionary<int, IndexedEntry> entries, FacetFilter filter)
    {
        Entries = entries;
        Filter = filter;
    }

    internal Dictionary<int, IndexedEntry> Entries { get; }

    internal FacetFilter Filter { get; }

    internal static SelectivityCorpus Create(int documentCount)
    {
        var documents = FileSearchDataFactory.Create(documentCount, seed: 2026).Documents;

        var words = new List<string>[documentCount];
        for (int i = 0; i < documentCount; i++)
            words[i] = [];

        for (int d = 0; d < Densities.Length; d++)
        {
            double density = Densities[d];
            foreach (int ordinal in HitSets.RandomOrdinals(documentCount, HitSets.CountFor(density, documentCount), seed: 100 + d))
                words[ordinal].Add(WordFor(density));
        }

        var entries = new Dictionary<int, IndexedEntry>(documentCount);
        for (int i = 0; i < documentCount; i++)
        {
            var document = documents[i];
            entries[document.Id] = new IndexedEntry(
                document.Text + " " + string.Join(' ', words[i]),
                document.Text,
                FacetValues.FromDictionary(new Dictionary<string, long>
                {
                    ["size"] = document.SizeBytes,
                    ["modified"] = document.ModifiedTicks,
                }));
        }

        long[] modified = documents.Select(static d => d.ModifiedTicks).Order().ToArray();
        var filter = FacetFilter.Range("modified", modified[modified.Length / 2], long.MaxValue);
        return new SelectivityCorpus(entries, filter);
    }

    /// <summary>
    /// The search word of a density: its per-mille value with the digits spelled as letters between "sel" and "q"
    /// (0.1% is "selbq", 100% is "selbaaaq"). No word contains another, so a Within query matches one density too.
    /// </summary>
    internal static string WordFor(double density)
        => "sel" + string.Concat(((int)Math.Round(density * 1000)).ToString().Select(static c => (char)('a' + (c - '0')))) + "q";
}
