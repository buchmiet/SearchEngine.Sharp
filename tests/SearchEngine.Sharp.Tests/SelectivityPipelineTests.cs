using SearchEngine.Filters;

namespace SearchEngine.Sharp.Tests;

public class SelectivityPipelineTests
{
    [Fact]
    public void Find_ZeroHits_NaturalSort_ReturnsEmptyWithoutBuildingPermutation()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("alpha", "alpha"),
            [2] = new("beta", "beta"),
        });
        var engine = new SearchEngineSharp(provider);

        var results = engine.Find(
            "zzzznotfound",
            WordMatchMethod.Within,
            enableOperators: true,
            SearchSortMode.NaturalSortAscending);

        Assert.Empty(results);
    }

    [Fact]
    public void Find_WithinFacet_NaturalSort_ReturnsSortedSparseResults()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [10] = new("report-z", "report-z", FacetValues.FromDictionary(new Dictionary<string, long> { ["size"] = 2048 })),
            [20] = new("report-a", "report-a", FacetValues.FromDictionary(new Dictionary<string, long> { ["size"] = 2048 })),
            [30] = new("report-m", "report-m", FacetValues.FromDictionary(new Dictionary<string, long> { ["size"] = 512 })),
            [40] = new("notes", "notes", FacetValues.FromDictionary(new Dictionary<string, long> { ["size"] = 2048 })),
        });
        var engine = new SearchEngineSharp(provider);
        var filter = FacetFilter.Range("size", 1024, 4096);

        var results = engine.Find(
            "report",
            WordMatchMethod.Within,
            enableOperators: true,
            SearchSortMode.NaturalSortAscending,
            filter);

        Assert.Equal([20, 10], results);
    }

    [Fact]
    public void Find_SnapshotOrder_UsesSetBitEnumeration()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [5] = new("needle", "needle"),
            [99] = new("other", "other"),
        });
        var engine = new SearchEngineSharp(provider);

        var results = engine.Find("needle", WordMatchMethod.Within, enableOperators: true);

        Assert.Equal([5], results);
    }
}
