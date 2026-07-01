using SearchEngine;

namespace SearchEngine.Sharp.Tests;

public class SearchEngineNaturalSortTests
{
    private static (SearchEngineSharp engine, IIndexUpdater updater) CreateEngine()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        var engine = new SearchEngineSharp(provider);
        return (engine, updater);
    }

    [Fact]
    public void Find_NaturalSortAscending_ReturnsCorrectOrder()
    {
        // Arrange
        var (engine, updater) = CreateEngine();

        var entries = new Dictionary<int, IndexedEntry>
        {
            [1] = new("GA-1000 watch", "GA-1000"),
            [2] = new("GA-100A watch", "GA-100A"),
            [3] = new("GA-100-1B watch", "GA-100-1B"),
            [4] = new("GA-10 watch", "GA-10"),
            [5] = new("GA-100 watch", "GA-100"),
            [6] = new("GA-100-1A watch", "GA-100-1A"),
            [7] = new("GA-2 watch", "GA-2"),
            [8] = new("GA-100-1 watch", "GA-100-1"),
        };

        updater.RebuildFrom(entries);

        // Act
        var results = engine.Find("ga", WordMatchMethod.Within, false, SearchSortMode.NaturalSortAscending);

        // Assert - should be in natural order
        var expectedOrder = new[] { 7, 4, 5, 8, 6, 3, 2, 1 };
        Assert.Equal(expectedOrder, results);
    }

    [Fact]
    public void Find_SnapshotOrder_DoesNotSort()
    {
        // Arrange
        var (engine, updater) = CreateEngine();

        var entries = new Dictionary<int, IndexedEntry>
        {
            [1] = new("GA-1000 watch", "GA-1000"),
            [2] = new("GA-100 watch", "GA-100"),
        };

        updater.RebuildFrom(entries);

        // Act - default sort mode
        var results = engine.Find("ga", WordMatchMethod.Within);

        // Assert - both results found (order depends on snapshot)
        Assert.Equal(2, results.Count);
        Assert.Contains(1, results);
        Assert.Contains(2, results);
    }

    [Fact]
    public void Find_NaturalSort_SearchTextSeparateFromSortText()
    {
        // Arrange
        var (engine, updater) = CreateEngine();

        // SearchText is rich (with synonyms), SortText is clean model name
        var entries = new Dictionary<int, IndexedEntry>
        {
            [10] = new("GA-100 G-Shock digital watch", "GA-100"),
            [20] = new("GA-2 G-Shock analog watch", "GA-2"),
        };

        updater.RebuildFrom(entries);

        // Act - search by synonym
        var resultsByShock = engine.Find("shock", WordMatchMethod.Within, false, SearchSortMode.NaturalSortAscending);

        // Assert - sorted by SortText naturally
        Assert.Equal(new[] { 20, 10 }, resultsByShock);
    }

    [Fact]
    public void Find_ExactMatch_SameHitSet_WithAndWithoutSort()
    {
        // Arrange
        var (engine, updater) = CreateEngine();

        var entries = new Dictionary<int, IndexedEntry>
        {
            [1] = new("GA-100", "GA-100"),
            [2] = new("GA-1000", "GA-1000"),
            [3] = new("GA-100-1", "GA-100-1"),
        };

        updater.RebuildFrom(entries);

        // Act
        var unsorted = engine.Find("ga", WordMatchMethod.Exact);
        var sorted = engine.Find("ga", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending);

        // Assert - same set of IDs
        Assert.Equal(unsorted.OrderBy(x => x), sorted.OrderBy(x => x));
    }

    [Fact]
    public void Find_WithinMatch_SameHitSet_WithAndWithoutSort()
    {
        // Arrange
        var (engine, updater) = CreateEngine();

        var entries = new Dictionary<int, IndexedEntry>
        {
            [1] = new("GA-100 model", "GA-100"),
            [2] = new("GA-1000 model", "GA-1000"),
            [3] = new("GA-100-1 model", "GA-100-1"),
        };

        updater.RebuildFrom(entries);

        // Act
        var unsorted = engine.Find("model", WordMatchMethod.Within);
        var sorted = engine.Find("model", WordMatchMethod.Within, false, SearchSortMode.NaturalSortAscending);

        // Assert - same set of IDs
        Assert.Equal(unsorted.OrderBy(x => x), sorted.OrderBy(x => x));
    }

    [Fact]
    public void Find_StringEntries_WorksAfterIncrementalUpdates()
    {
        // Arrange
        var (engine, updater) = CreateEngine();

        // Use string entries via AddEntry
        updater.AddEntry(1, "hello world");
        updater.AddEntry(2, "hello there");

        // Act
        var results = engine.Find("hello", WordMatchMethod.Exact);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Contains(1, results);
        Assert.Contains(2, results);
    }

    [Fact]
    public void Find_NaturalSort_DuplicateSortKeys_TiebreakByRecordId()
    {
        var (engine, updater) = CreateEngine();

        // Same SortText → same NaturalSortKey → tiebreak by record ID
        var entries = new Dictionary<int, IndexedEntry>
        {
            [30] = new("GA-100 variant C", "GA-100"),
            [10] = new("GA-100 variant A", "GA-100"),
            [20] = new("GA-100 variant B", "GA-100"),
        };

        updater.RebuildFrom(entries);

        var results = engine.Find("ga", WordMatchMethod.Within, false,
            SearchSortMode.NaturalSortAscending);

        // Same sort key → stable order by record ID ascending
        Assert.Equal(new[] { 10, 20, 30 }, results);
    }

    [Fact]
    public void Find_NaturalSort_PartialMatch_OnlySortedSubset()
    {
        var (engine, updater) = CreateEngine();

        var entries = new Dictionary<int, IndexedEntry>
        {
            [1] = new("GA-1000 digital", "GA-1000"),
            [2] = new("GA-100 digital", "GA-100"),
            [3] = new("GA-10 analog", "GA-10"),
            [4] = new("DW-5600 digital", "DW-5600"),
        };

        updater.RebuildFrom(entries);

        // "digital" matches [1,2,4] but NOT [3]
        var results = engine.Find("digital", WordMatchMethod.Exact, false,
            SearchSortMode.NaturalSortAscending);

        // Sorted naturally: DW-5600 < GA-100 < GA-1000
        Assert.Equal(3, results.Count);
        Assert.Equal(new[] { 4, 2, 1 }, results);
    }

    [Fact]
    public void Find_NaturalSort_LargeDataset_CorrectOrder()
    {
        var (engine, updater) = CreateEngine();

        // Generate 200 entries with varying model numbers
        var entries = new Dictionary<int, IndexedEntry>();
        for (int i = 1; i <= 200; i++)
        {
            entries[i] = new($"GA-{i} watch", $"GA-{i}");
        }

        updater.RebuildFrom(entries);

        var results = engine.Find("watch", WordMatchMethod.Exact, false,
            SearchSortMode.NaturalSortAscending);

        Assert.Equal(200, results.Count);

        // Verify natural order: GA-1, GA-2, ..., GA-10, GA-11, ..., GA-200
        // (NOT lexicographic: GA-1, GA-10, GA-100, ...)
        Assert.Equal(1, results[0]);     // GA-1
        Assert.Equal(2, results[1]);     // GA-2
        Assert.Equal(10, results[9]);    // GA-10
        Assert.Equal(100, results[99]);  // GA-100
        Assert.Equal(200, results[199]); // GA-200
    }

    [Fact]
    public void Find_NaturalSort_AfterAddEntry_StillCorrect()
    {
        var (engine, updater) = CreateEngine();

        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("GA-100 watch", "GA-100"),
            [3] = new("GA-1000 watch", "GA-1000"),
        });

        // Add entry between existing ones
        updater.AddEntry(2, new IndexedEntry("GA-50 watch", "GA-50"));

        var results = engine.Find("watch", WordMatchMethod.Exact, false,
            SearchSortMode.NaturalSortAscending);

        // GA-50 < GA-100 < GA-1000
        Assert.Equal(new[] { 2, 1, 3 }, results);
    }

    [Fact]
    public void Find_NaturalSort_AfterRemoveEntry_StillCorrect()
    {
        var (engine, updater) = CreateEngine();

        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("GA-100 watch", "GA-100"),
            [2] = new("GA-50 watch", "GA-50"),
            [3] = new("GA-1000 watch", "GA-1000"),
        });

        updater.RemoveEntry(2); // remove GA-50

        var results = engine.Find("watch", WordMatchMethod.Exact, false,
            SearchSortMode.NaturalSortAscending);

        Assert.Equal(new[] { 1, 3 }, results);
    }

    [Fact]
    public void Find_NaturalSort_MixedPrefixes_SortedCorrectly()
    {
        var (engine, updater) = CreateEngine();

        var entries = new Dictionary<int, IndexedEntry>
        {
            [1] = new("PRW-3000 watch", "PRW-3000"),
            [2] = new("DW-5600 watch", "DW-5600"),
            [3] = new("GA-100 watch", "GA-100"),
            [4] = new("GA-2 watch", "GA-2"),
            [5] = new("DW-6900 watch", "DW-6900"),
        };

        updater.RebuildFrom(entries);

        var results = engine.Find("watch", WordMatchMethod.Exact, false,
            SearchSortMode.NaturalSortAscending);

        // DW < GA < PRW (alphabetic), then numeric within prefix
        Assert.Equal(new[] { 2, 5, 4, 3, 1 }, results);
    }
}
