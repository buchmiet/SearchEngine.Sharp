using SearchEngine.Filters;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// A FacetFilter (Range, Mask, Combine) keeps the hits whose facet values pass every predicate (docs/query-semantics.md,
// facet filters; src/Query/FacetFilterEvaluator.cs). An empty expression with a filter returns every entry the filter
// passes. A facet an entry does not carry reads as 0, whether no entry carries it, only later entries carry it (a facet
// column that exists only in the delta segment) or the entry has no bag at all: a filter on a facet that is missing
// must not throw and must treat the entry like a 0. Changes through IndexUpdater must change what the filter sees. Find
// and CountMatches agree. Natural sort with a filter is in NaturalSortTieBreakTests, the single-term shortcut with a
// filter in SingleTermFastPathTests.
public class FacetFilterTests
{
    // 1: report-final.pdf, 2: report-draft.pdf, 3: tmp-cache.bin, 4: log.txt, 5: GA-100 watch, 6: GA-10 watch
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
    public async Task A_range_includes_both_bounds()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 1000, 2000);

        var withText = engine.Find("report", WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, filter);
        var filterOnly = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);

        await Assert.That(withText).IsEquivalentTo(new[] { 1 }, CollectionOrdering.Matching);
        await Assert.That(filterOnly.Order()).IsEquivalentTo(new[] { 1, 4 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_mask_needs_the_must_have_bits_and_none_of_the_must_not_bits()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Mask("attrs", mustHave: 0x2, mustNot: 0x1);

        var results = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);

        await Assert.That(results).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Combined_predicates_must_all_pass()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Combine(
            FacetFilter.Range("size", 1000, 10_000),
            FacetFilter.Range("modified", 100, 250));

        var results = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);

        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2, 4 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_facet_no_entry_carries_reads_as_zero()
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
    public async Task An_empty_expression_with_a_filter_returns_every_entry_the_filter_passes()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 5000, long.MaxValue);

        var results = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);

        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 2, 5 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Counting_with_a_filter_agrees_with_the_filtered_results()
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
    public async Task A_glob_query_is_intersected_with_the_filter()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 1000, long.MaxValue);

        var results = engine.Find("report*", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);

        await Assert.That(results.OrderBy(x => x)).IsEquivalentTo(new[] { 1, 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Added_entries_are_filtered_by_their_facets()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.AddEntry(1, new IndexedEntry("alpha.txt", "alpha.txt", Facets(100, 1, 0)));
        updater.AddEntry(2, new IndexedEntry("beta.txt", "beta.txt", Facets(200, 2, 0)));

        var filter = FacetFilter.Range("size", 150, long.MaxValue);
        await Assert.That(engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter)).IsEquivalentTo(new[] { 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_replaced_entry_is_filtered_by_its_new_facets()
    {
        var (engine, updater) = CreateFacetIndex();
        updater.AddOrUpdateEntries([
            new KeyValuePair<int, IndexedEntry>(2, new("report-draft.pdf", "report-draft.pdf", Facets(9000, 200, 0x2))),
        ]);

        var filter = FacetFilter.Range("size", 8000, long.MaxValue);
        await Assert.That(engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter).OrderBy(x => x)).IsEquivalentTo(new[] { 2, 5 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_removed_entry_no_longer_passes_a_filter()
    {
        var (engine, updater) = CreateFacetIndex();
        await Assert.That(updater.RemoveEntry(2)).IsTrue();

        var filter = FacetFilter.Range("size", 5000, long.MaxValue);
        await Assert.That(engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter)).IsEquivalentTo(new[] { 5 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_rebuild_replaces_every_facet()
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
    public async Task An_entry_without_a_facet_bag_reads_every_facet_as_zero()
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
    public async Task Exact_and_Within_hits_are_filtered_alike()
    {
        var (engine, _) = CreateFacetIndex();
        var filter = FacetFilter.Range("size", 1000, 5000);

        var within = engine.Find("report-final", WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, filter);
        var exact = engine.Find("report-final", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter);

        await Assert.That(exact.OrderBy(x => x)).IsEquivalentTo(within.OrderBy(x => x), CollectionOrdering.Matching);
        await Assert.That(engine.CountMatches("report-final", WordMatchMethod.Exact, false, filter)).IsEqualTo(exact.Count);
    }

    [Test]
    public async Task The_builder_fills_the_facet_columns_alike_whether_it_knows_the_entry_count_or_not()
    {
        var entries = new Dictionary<int, IndexedEntry>();
        for (int id = 0; id < 500; id++)
            entries[id * 3] = new($"f{id}.pdf", $"f{id}.pdf", id % 7 == 0 ? null : FacetValues.Create([new("size", id * 10L), new("attrs", id % 4)]));

        var known = new SearchEngineSharp(new FixedSnapshotProvider(IndexSnapshotBuilder.Build(entries)));
        var streamed = new SearchEngineSharp(new FixedSnapshotProvider(IndexSnapshotBuilder.Build(entries.Select(e => e))));
        var filter = FacetFilter.Combine(FacetFilter.Range("size", 1000, 3000), FacetFilter.Mask("attrs", 0x1));

        var expected = entries.Where(e => e.Value.Facets is not null && e.Key / 3 * 10 is >= 1000 and <= 3000 && (e.Key / 3 % 4 & 1) == 1).Select(e => e.Key).Order();
        await Assert.That(known.Find("pdf", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter).Order()).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(streamed.Find("pdf", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, filter).Order()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }
}
