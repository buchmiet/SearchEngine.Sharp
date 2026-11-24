using SearchEngine.Filters;

namespace SearchEngine.Sharp.Tests;

public class FacetFilterTests
{
    private static (SearchEngineSharp engine, IIndexUpdater updater) CreateEngine()
    {
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider);
        var engine = new SearchEngineSharp(provider);
        return (engine, updater);
    }

    private static (SearchEngineSharp engine, IIndexUpdater updater) CreateFacetIndex()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("report-final.pdf", "report-final.pdf", Facets(1000, 100, 0x3)),
            [2] = new("report-draft.pdf", "report-draft.pdf", Facets(5000, 200, 0x2)),
            [3] = new("tmp-cache.bin", "tmp-cache.bin", Facets(500, 50, 0x1)),
            [4] = new("log.txt", "log.txt", Facets(2000, 150, 0x0)),
            [5] = new("GA-100 watch", "GA-100", Facets(8000, 300, 0x4)),
            [6] = new("GA-10 watch", "GA-10", Facets(100, 10, 0x5)),
        });
        return (engine, updater);
    }

    private static FacetValues? Facets(long size, long modified, long attrs)
        => FacetValues.FromDictionary(new Dictionary<string, long>
        {
            ["size"] = size,
            ["modified"] = modified,
            ["attrs"] = attrs,
        });

    [Fact]
    public void Range_InclusiveBoundaries()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 1000, 2000);

        var results = engine.Find("report", WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, filter);
        Assert.Equal(new[] { 1 }, results);
    }

    [Fact]
    public void Mask_MustHaveAndMustNot()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Mask("attrs", mustHave: 0x2, mustNot: 0x1);

        var results = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);
        Assert.Equal(new[] { 2 }, results);
    }

    [Fact]
    public void Combine_AndsMultiplePredicates()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Combine(
            FacetFilter.Range("size", 1000, 10_000),
            FacetFilter.Range("modified", 100, 250));

        var results = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);
        Assert.Equal(new[] { 1, 2, 4 }, results.OrderBy(x => x));
    }

    [Fact]
    public void UnknownFacet_ThrowsArgumentException()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("missing", 0, 100);

        var ex = Assert.Throws<ArgumentException>(() =>
            engine.Find("report", WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, filter));

        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterOnly_EmptyExpression_ReturnsMatchingDocuments()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 5000, long.MaxValue);

        var results = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);
        Assert.Equal(new[] { 2, 5 }, results.OrderBy(x => x));
    }

    [Fact]
    public void FilterWithNaturalSort_ReturnsSortedSubset()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 100, long.MaxValue);

        var results = engine.Find(
            "ga",
            WordMatchMethod.Within,
            false,
            SearchSortMode.NaturalSortAscending,
            filter);

        Assert.Equal(new[] { 6, 5 }, results);
    }

    [Fact]
    public void CountMatches_WithFilter_EqualsFindCount()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Combine(
            FacetFilter.Range("size", 500, 5000),
            FacetFilter.Mask("attrs", mustHave: 0x1));

        int count = engine.CountMatches("report", WordMatchMethod.Within, false, filter);
        var find = engine.Find("report", WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, filter);

        Assert.Equal(find.Count, count);
        Assert.Equal(new[] { 1 }, find.OrderBy(x => x));
    }

    [Fact]
    public void GlobWithFilter_IntersectsResults()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 1000, long.MaxValue);

        var results = engine.Find("report*", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);
        Assert.Equal(new[] { 1, 2 }, results.OrderBy(x => x));
    }

    [Fact]
    public void AddEntry_PreservesFacets()
    {
        var (engine, updater) = CreateEngine();
        updater.AddEntry(1, new IndexedEntry("alpha.txt", "alpha.txt", Facets(100, 1, 0)));
        updater.AddEntry(2, new IndexedEntry("beta.txt", "beta.txt", Facets(200, 2, 0)));

        var filter = FacetFilter.Range("size", 150, long.MaxValue);
        Assert.Equal(new[] { 2 }, engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter));
    }

    [Fact]
    public void AddOrUpdateEntries_UpdatesFacetColumns()
    {
        var (engine, updater) = CreateFacetIndex();
        updater.AddOrUpdateEntries([
            new KeyValuePair<int, IndexedEntry>(2, new("report-draft.pdf", "report-draft.pdf", Facets(9000, 200, 0x2))),
        ]);

        var filter = FacetFilter.Range("size", 8000, long.MaxValue);
        Assert.Equal(new[] { 2, 5 }, engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter).OrderBy(x => x));
    }

    [Fact]
    public void RemoveEntry_RemovesFacetValues()
    {
        var (engine, updater) = CreateFacetIndex();
        Assert.True(updater.RemoveEntry(2));

        var filter = FacetFilter.Range("size", 5000, long.MaxValue);
        Assert.Equal(new[] { 5 }, engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter));
    }

    [Fact]
    public void RebuildFrom_ReplacesFacetColumns()
    {
        var (engine, updater) = CreateFacetIndex();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [10] = new("only.txt", "only.txt", Facets(42, 7, 0)),
        });

        var filter = FacetFilter.Range("size", 40, 50);
        Assert.Equal(new[] { 10 }, engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter));
        Assert.Equal(1, engine.DocumentCount);
    }

    [Fact]
    public void MissingFacetValue_DefaultsToZero()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("a.txt", "a.txt", Facets(100, 0, 0)),
            [2] = new("b.txt", "b.txt", null),
        });

        var filter = FacetFilter.Range("size", 0, 0);
        Assert.Equal(new[] { 2 }, engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter));
    }

    [Fact]
    public void ExactSingleTerm_WithFacetFilter_MatchesWithinPlusFilter()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 1000, 5000);

        var within = engine.Find("report-final", WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, filter);
        var exact = engine.Find("report-final", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);

        Assert.Equal(within.OrderBy(x => x), exact.OrderBy(x => x));
        Assert.Equal(exact.Count, engine.CountMatches("report-final", WordMatchMethod.Exact, false, filter));
    }

    [Fact]
    public void ExactSingleTerm_WithFacetFilter_EnableOperatorsTrue_SameAsOff()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 1000, 5000);

        var off = engine.Find("log", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);
        var on = engine.Find("log", WordMatchMethod.Exact, true, SearchSortMode.SnapshotOrder, filter);

        Assert.Equal(off.OrderBy(x => x), on.OrderBy(x => x));
        Assert.Equal(
            engine.CountMatches("log", WordMatchMethod.Exact, true, filter),
            engine.CountMatches("log", WordMatchMethod.Exact, false, filter));
    }

    [Fact]
    public void ExactSingleTerm_EnableOperatorsTrue_SameAsOff()
    {
        var (engine, _) = CreateFacetIndex();

        var off = engine.Find("log", WordMatchMethod.Exact, false);
        var on = engine.Find("log", WordMatchMethod.Exact, true);

        Assert.Equal(off.OrderBy(x => x), on.OrderBy(x => x));
    }

    [Fact]
    public void CreateFromPairs_IndexesLikeFromDictionary()
    {
        var (engine, updater) = CreateEngine();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("a.pdf", "a.pdf", FacetValues.Create([new("size", 10L), new("attrs", 0x1L)])),
            [2] = new("b.pdf", "b.pdf", FacetValues.FromDictionary(new Dictionary<string, long> { ["size"] = 20, ["attrs"] = 0x2 })),
            [3] = new("c.pdf", "c.pdf", FacetValues.Create("size", 30)),
        });

        Assert.Equal([1, 2], engine.Find("pdf", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, FacetFilter.Range("size", 0, 25)));
        Assert.Equal([2], engine.Find("pdf", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, FacetFilter.Mask("attrs", 0x2)));
    }

    [Fact]
    public void CreateFromPairs_RejectsDuplicateNames_AndEmptyIsEmpty()
    {
        Assert.Throws<ArgumentException>(() => FacetValues.Create([new("size", 1L), new("size", 2L)]));
        Assert.Same(FacetValues.Empty, FacetValues.Create(ReadOnlySpan<KeyValuePair<string, long>>.Empty));
    }

    [Fact]
    public void FacetColumns_SameWhetherEntryCountIsKnownUpFrontOrNot()
    {
        var entries = new Dictionary<int, IndexedEntry>();
        for (int id = 0; id < 500; id++)
            entries[id * 3] = new($"f{id}.pdf", $"f{id}.pdf", id % 7 == 0 ? null : FacetValues.Create([new("size", id * 10L), new("attrs", id % 4)]));

        var known = new SearchEngineSharp(new FixedProvider(SearchEngine.Snapshots.IndexSnapshotBuilder.Build(entries)));
        var streamed = new SearchEngineSharp(new FixedProvider(SearchEngine.Snapshots.IndexSnapshotBuilder.Build(entries.Select(e => e))));
        var filter = FacetFilter.Combine(FacetFilter.Range("size", 1000, 3000), FacetFilter.Mask("attrs", 0x1));

        var expected = entries.Where(e => e.Value.Facets is not null && e.Key / 3 * 10 is >= 1000 and <= 3000 && (e.Key / 3 % 4 & 1) == 1).Select(e => e.Key).Order();
        Assert.Equal(expected, known.Find("pdf", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter).Order());
        Assert.Equal(expected, streamed.Find("pdf", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter).Order());
    }

    private sealed class FixedProvider(SearchEngine.Snapshots.IndexSnapshot snapshot) : IIndexSnapshotProvider
    {
        public SearchEngine.Snapshots.IndexSnapshot Current => snapshot;
    }
}
