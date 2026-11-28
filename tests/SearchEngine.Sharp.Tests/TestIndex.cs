using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// An index for one test: a snapshot provider, an updater that publishes to it and an engine that reads it.
internal static class TestIndex
{
    public static (SearchEngineSharp Engine, IndexUpdater Updater, IndexSnapshotProvider Provider) Create(SearchTokenization? tokenization = null)
        => Create(tokenization, MergePolicy.Default);

    // A policy changes when segments merge: tests of the layout use one without a background thread, so a merge
    // happens only where the test calls IndexUpdater.MergeNow.
    public static (SearchEngineSharp Engine, IndexUpdater Updater, IndexSnapshotProvider Provider) Create(SearchTokenization? tokenization, MergePolicy policy)
    {
        var provider = new IndexSnapshotProvider();
        return (new SearchEngineSharp(provider), new IndexUpdater(provider, tokenization, policy), provider);
    }

    // The ids a Within query finds, in ascending order: the check most tests make after a change to the index.
    public static int[] Ids(ISearchEngine engine, string query)
        => [.. engine.Find(query, WordMatchMethod.Within).Order()];
}
