using SearchEngine.Filters;

namespace SearchEngine.Sharp.Tests;

public class FacetFilterTests
{
    private static (SearchEngineSharp engine, IIndexUpdater updater) CreateFacetIndex()
    {
        var (engine, updater, _) = TestIndex.Create();
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

    [Test]
    public async Task Range_InclusiveBoundaries()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 1000, 2000);

        var results = engine.Find("report", WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, filter);
        await Assert.That(results).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Mask_MustHaveAndMustNot()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Mask("attrs", mustHave: 0x2, mustNot: 0x1);

        var results = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);
        await Assert.That(results).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Combine_AndsMultiplePredicates()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Combine(
            FacetFilter.Range("size", 1000, 10_000),
            FacetFilter.Range("modified", 100, 250));

        var results = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);
        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2, 4 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task UnknownFacet_ReadsAsZero()
    {
        var (engine, _) = CreateFacetIndex();

        var zeroIncluded = engine.Find("report", WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, FacetFilter.Range("missing", 0, 100));
        var zeroExcluded = engine.Find("report", WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, FacetFilter.Range("missing", 1, 100));
        var filterOnly = engine.CountMatches("", WordMatchMethod.Exact, false, FacetFilter.Mask("missing", mustHave: 0, mustNot: 0x1));

        await Assert.That(zeroIncluded).IsEquivalentTo(new[] { 1, 2 }, CollectionOrdering.Matching);
        await Assert.That(zeroExcluded).IsEmpty();
        await Assert.That(filterOnly).IsEqualTo(6);
    }

    [Test]
    public async Task Facet_carried_only_by_later_entries_reads_as_zero_on_the_others()
    {
        var (engine, updater) = CreateFacetIndex();
        updater.AddEntry(7, new IndexedEntry("report-new.pdf", "report-new.pdf", FacetValues.Create("rating", 5)));

        var unrated = engine.Find("report", WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, FacetFilter.Range("rating", 0, 0));
        var rated = engine.Find("report", WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, FacetFilter.Range("rating", 1, 10));
        var largeUnrated = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending,
            FacetFilter.Combine(FacetFilter.Range("rating", 0, 0), FacetFilter.Range("size", 2000, long.MaxValue)));

        await Assert.That(unrated).IsEquivalentTo(new[] { 1, 2 }, CollectionOrdering.Matching);
        await Assert.That(rated).IsEquivalentTo(new[] { 7 }, CollectionOrdering.Matching);
        await Assert.That(largeUnrated).IsEquivalentTo(new[] { 5, 4, 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task FilterOnly_EmptyExpression_ReturnsMatchingDocuments()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 5000, long.MaxValue);

        var results = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);
        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 2, 5 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task FilterWithNaturalSort_ReturnsSortedSubset()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 100, long.MaxValue);

        var results = engine.Find(
            "ga",
            WordMatchMethod.Within,
            false,
            SearchSortMode.NaturalSortAscending,
            filter);

        await Assert.That(results).IsEquivalentTo(new[] { 6, 5 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CountMatches_WithFilter_EqualsFindCount()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Combine(
            FacetFilter.Range("size", 500, 5000),
            FacetFilter.Mask("attrs", mustHave: 0x1));

        int count = engine.CountMatches("report", WordMatchMethod.Within, false, filter);
        var find = engine.Find("report", WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, filter);

        await Assert.That(count).IsEqualTo(find.Count);
        await Assert.That(find.OrderBy(x => x)).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task GlobWithFilter_IntersectsResults()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 1000, long.MaxValue);

        var results = engine.Find("report*", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);
        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AddEntry_PreservesFacets()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.AddEntry(1, new IndexedEntry("alpha.txt", "alpha.txt", Facets(100, 1, 0)));
        updater.AddEntry(2, new IndexedEntry("beta.txt", "beta.txt", Facets(200, 2, 0)));

        var filter = FacetFilter.Range("size", 150, long.MaxValue);
        await Assert.That(engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter)).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AddOrUpdateEntries_UpdatesFacetColumns()
    {
        var (engine, updater) = CreateFacetIndex();
        updater.AddOrUpdateEntries([
            new KeyValuePair<int, IndexedEntry>(2, new("report-draft.pdf", "report-draft.pdf", Facets(9000, 200, 0x2))),
        ]);

        var filter = FacetFilter.Range("size", 8000, long.MaxValue);
        await Assert.That(engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter).OrderBy(x => x)).IsEquivalentTo(new[] { 2, 5 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RemoveEntry_RemovesFacetValues()
    {
        var (engine, updater) = CreateFacetIndex();
        await Assert.That(updater.RemoveEntry(2)).IsTrue();

        var filter = FacetFilter.Range("size", 5000, long.MaxValue);
        await Assert.That(engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter)).IsEquivalentTo(new[] { 5 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RebuildFrom_ReplacesFacetColumns()
    {
        var (engine, updater) = CreateFacetIndex();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [10] = new("only.txt", "only.txt", Facets(42, 7, 0)),
        });

        var filter = FacetFilter.Range("size", 40, 50);
        await Assert.That(engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter)).IsEquivalentTo(new[] { 10 }, CollectionOrdering.Matching);
        await Assert.That(engine.DocumentCount).IsEqualTo(1);
    }

    [Test]
    public async Task MissingFacetValue_DefaultsToZero()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("a.txt", "a.txt", Facets(100, 0, 0)),
            [2] = new("b.txt", "b.txt", null),
        });

        var filter = FacetFilter.Range("size", 0, 0);
        await Assert.That(engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter)).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ExactSingleTerm_WithFacetFilter_MatchesWithinPlusFilter()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 1000, 5000);

        var within = engine.Find("report-final", WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, filter);
        var exact = engine.Find("report-final", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);

        await Assert.That(exact.OrderBy(x => x)).IsEquivalentTo(within.OrderBy(x => x), CollectionOrdering.Matching);
        await Assert.That(engine.CountMatches("report-final", WordMatchMethod.Exact, false, filter)).IsEqualTo(exact.Count);
    }

    [Test]
    public async Task ExactSingleTerm_WithFacetFilter_EnableOperatorsTrue_SameAsOff()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 1000, 5000);

        var off = engine.Find("log", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);
        var on = engine.Find("log", WordMatchMethod.Exact, true, SearchSortMode.SnapshotOrder, filter);

        await Assert.That(on.OrderBy(x => x)).IsEquivalentTo(off.OrderBy(x => x), CollectionOrdering.Matching);
        await Assert.That(engine.CountMatches("log", WordMatchMethod.Exact, false, filter))
            .IsEqualTo(engine.CountMatches("log", WordMatchMethod.Exact, true, filter));
    }

    [Test]
    public async Task ExactSingleTerm_EnableOperatorsTrue_SameAsOff()
    {
        var (engine, _) = CreateFacetIndex();

        var off = engine.Find("log", WordMatchMethod.Exact, false);
        var on = engine.Find("log", WordMatchMethod.Exact, true);

        await Assert.That(on.OrderBy(x => x)).IsEquivalentTo(off.OrderBy(x => x), CollectionOrdering.Matching);
    }

    [Test]
    public async Task CreateFromPairs_IndexesLikeFromDictionary()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("a.pdf", "a.pdf", FacetValues.Create([new("size", 10L), new("attrs", 0x1L)])),
            [2] = new("b.pdf", "b.pdf", FacetValues.FromDictionary(new Dictionary<string, long> { ["size"] = 20, ["attrs"] = 0x2 })),
            [3] = new("c.pdf", "c.pdf", FacetValues.Create("size", 30)),
        });

        await Assert.That(engine.Find("pdf", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, FacetFilter.Range("size", 0, 25))).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
        await Assert.That(engine.Find("pdf", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, FacetFilter.Mask("attrs", 0x2))).IsEquivalentTo([2], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CreateFromPairs_RejectsDuplicateNames_AndEmptyIsEmpty()
    {
        await Assert.That(() => FacetValues.Create([new("size", 1L), new("size", 2L)])).ThrowsExactly<ArgumentException>();
        await Assert.That(FacetValues.Create(ReadOnlySpan<KeyValuePair<string, long>>.Empty)).IsSameReferenceAs(FacetValues.Empty);
    }

    [Test]
    public async Task FacetColumns_SameWhetherEntryCountIsKnownUpFrontOrNot()
    {
        var entries = new Dictionary<int, IndexedEntry>();
        for (int id = 0; id < 500; id++)
            entries[id * 3] = new($"f{id}.pdf", $"f{id}.pdf", id % 7 == 0 ? null : FacetValues.Create([new("size", id * 10L), new("attrs", id % 4)]));

        var known = new SearchEngineSharp(new FixedSnapshotProvider(SearchEngine.Snapshots.IndexSnapshotBuilder.Build(entries)));
        var streamed = new SearchEngineSharp(new FixedSnapshotProvider(SearchEngine.Snapshots.IndexSnapshotBuilder.Build(entries.Select(e => e))));
        var filter = FacetFilter.Combine(FacetFilter.Range("size", 1000, 3000), FacetFilter.Mask("attrs", 0x1));

        var expected = entries.Where(e => e.Value.Facets is not null && e.Key / 3 * 10 is >= 1000 and <= 3000 && (e.Key / 3 % 4 & 1) == 1).Select(e => e.Key).Order();
        await Assert.That(known.Find("pdf", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter).Order()).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(streamed.Find("pdf", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter).Order()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }
}
