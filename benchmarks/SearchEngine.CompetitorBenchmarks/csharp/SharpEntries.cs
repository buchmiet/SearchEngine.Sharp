using SearchEngine;

namespace SearchEngine.CompetitorBenchmarks;

internal static class SharpEntries
{
    internal static Dictionary<int, IndexedEntry> Build(CorpusFile corpus, bool withFacets)
    {
#if SHARP_HAS_FACET
        if (withFacets)
            return BuildWithFacets(corpus);
#endif
        return BuildTextOnly(corpus);
    }

    private static Dictionary<int, IndexedEntry> BuildTextOnly(CorpusFile corpus)
    {
        var map = new Dictionary<int, IndexedEntry>(corpus.Documents.Count);
        foreach (var doc in corpus.Documents)
            map[doc.Id] = new IndexedEntry(doc.Name, doc.Name);
        return map;
    }

#if SHARP_HAS_FACET
    private static Dictionary<int, IndexedEntry> BuildWithFacets(CorpusFile corpus)
    {
        var map = new Dictionary<int, IndexedEntry>(corpus.Documents.Count);
        foreach (var doc in corpus.Documents)
        {
            map[doc.Id] = new IndexedEntry(
                doc.Name,
                doc.Name,
                SearchEngine.Filters.FacetValues.FromDictionary(new Dictionary<string, long>
                {
                    ["size"] = doc.SizeBytes,
                    ["modified"] = doc.ModifiedTicks,
                }));
        }

        return map;
    }
#endif
}
