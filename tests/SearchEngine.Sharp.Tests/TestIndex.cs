using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// An index for one test: a snapshot provider, an updater that publishes to it and an engine that reads it.
internal static class TestIndex
{
    public static (SearchEngineSharp Engine, IndexUpdater Updater, IndexSnapshotProvider Provider) Create(SearchTokenization? tokenization = null)
    {
        var provider = new IndexSnapshotProvider();
        return (new SearchEngineSharp(provider), new IndexUpdater(provider, tokenization), provider);
    }

    // The ids a Within query finds, in ascending order: the check most tests make after a change to the index.
    public static int[] Ids(ISearchEngine engine, string query)
        => [.. engine.Find(query, WordMatchMethod.Within).Order()];
}
