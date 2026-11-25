using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

/// <summary>An index for one test: a snapshot provider, an updater that publishes to it and an engine that reads it.</summary>
internal static class TestIndex
{
    public static (SearchEngineSharp Engine, IndexUpdater Updater, IndexSnapshotProvider Provider) Create(SearchTokenization? tokenization = null)
    {
        var provider = new IndexSnapshotProvider();
        return (new SearchEngineSharp(provider), new IndexUpdater(provider, tokenization), provider);
    }
}

/// <summary>A provider that always returns one snapshot, for tests that build a snapshot directly.</summary>
internal sealed class FixedSnapshotProvider(IndexSnapshot snapshot) : IIndexSnapshotProvider
{
    public IndexSnapshot Current => snapshot;
}
