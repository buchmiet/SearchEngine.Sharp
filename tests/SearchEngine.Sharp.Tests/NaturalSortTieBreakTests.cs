using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Query;
using SearchEngine.Snapshots;
using SearchEngine.Sorting;

namespace SearchEngine.Sharp.Tests;

// Entries with equal natural sort keys come in ascending id order (SearchSortMode.NaturalSortAscending), so the same
// query on the same entries always returns the same order, whichever way ResultMaterializer sorts: sort-K over the
// hits, the cached sort permutation of the base, a filter-only or text-and-filter query, or the permutation with the
// delta documents of an IndexUpdater snapshot placed into it (IndexSnapshot.OtherOrder). The tie rule is what makes
// the paths interchangeable, so the tests of which path a query takes (SelectivityThresholds.UseSortKPath) are here
// too: a cheap query must not build the permutation, a built one must replace sorting large hit sets.
public class NaturalSortTieBreakTests
{
    private const int DocumentCount = 200;

    // Ids are enumerated in descending order, so snapshot order is the reverse of id order. Five sort texts repeat
    // in forms that give equal keys (case, leading zeros, separators), so every key is shared by 40 documents.
    private static readonly string[] SortTextForms = ["Item 7", "item 07", "ITEM_7", "item-007", "Item/7"];
    private static readonly string[] SortTexts = ["b 2", "a 10", "a 2", "B 2", "a 02"];

    private static IndexSnapshot BuildSnapshot()
    {
        var entries = new List<KeyValuePair<int, IndexedEntry>>(DocumentCount);
        for (int id = DocumentCount; id >= 1; id--)
        {
            string sortText = id % 3 == 0 ? SortTextForms[id % SortTextForms.Length] : SortTexts[id % SortTexts.Length];
            string searchText = id % 2 == 0 ? "all even" : "all odd";
            entries.Add(new(id, new IndexedEntry(searchText, sortText, FacetValues.Create("n", id))));
        }

        return IndexSnapshotBuilder.Build(entries);
    }

    private static List<int> Expected(IndexSnapshot snapshot, Func<int, bool> isHit)
    {
        var ids = snapshot.Segments[0].RecordIds;
        var sortTexts = snapshot.Segments[0].SortTexts;
        return Enumerable.Range(0, ids.Length)
            .Where(ordinal => isHit(ids[ordinal]))
            .OrderBy(ordinal => NaturalSortKeyBuilder.Build(sortTexts[ordinal]), StringComparer.Ordinal)
            .ThenBy(ordinal => ids[ordinal])
            .Select(ordinal => ids[ordinal])
            .ToList();
    }

    [Test]
    public async Task Hits_below_the_sort_threshold_break_ties_by_id()
    {
        var snapshot = BuildSnapshot();
        var engine = new SearchEngineSharp(new FixedSnapshotProvider(snapshot));

        var results = engine.Find("even", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending);

        await Assert.That(results).IsEquivalentTo(Expected(snapshot, id => id % 2 == 0), CollectionOrdering.Matching);
    }

    [Test]
    public async Task All_documents_break_ties_by_id_through_the_cached_permutation()
    {
        var snapshot = BuildSnapshot();
        var engine = new SearchEngineSharp(new FixedSnapshotProvider(snapshot));

        var first = engine.Find("all", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending);
        var second = engine.Find("all", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending);

        await Assert.That(first).IsEquivalentTo(Expected(snapshot, _ => true), CollectionOrdering.Matching);
        await Assert.That(second).IsEquivalentTo(first, CollectionOrdering.Matching);
        await Assert.That(snapshot.Segments[0].SortedPermutationBuildCount).IsEqualTo(1);
    }

    [Test]
    public async Task Filter_only_queries_break_ties_by_id()
    {
        var snapshot = BuildSnapshot();
        var engine = new SearchEngineSharp(new FixedSnapshotProvider(snapshot));

        var results = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending, FacetFilter.Range("n", 1, 50));

        await Assert.That(results).IsEquivalentTo(Expected(snapshot, id => id <= 50), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Text_and_filter_queries_break_ties_by_id()
    {
        var snapshot = BuildSnapshot();
        var engine = new SearchEngineSharp(new FixedSnapshotProvider(snapshot));

        var results = engine.Find("odd", WordMatchMethod.Within, false, SearchSortMode.NaturalSortAscending, FacetFilter.Range("n", 20, 170));

        await Assert.That(results).IsEquivalentTo(Expected(snapshot, id => id % 2 == 1 && id is >= 20 and <= 170), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(2)]
    [Arguments(17)]
    [Arguments(100)]
    [Arguments(160)]
    [Arguments(199)]
    public async Task Both_sort_strategies_give_the_same_order(int hitCount)
    {
        var snapshot = BuildSnapshot();
        var hits = new FastBitSet(snapshot.DocumentCount);
        for (int ordinal = 0; ordinal < hitCount; ordinal++)
            hits.Add(ordinal * snapshot.DocumentCount / hitCount);

        var sortedHits = ResultMaterializer.MaterializeNaturalSortKOnly(snapshot, hits, hitCount);
        var permutation = ResultMaterializer.MaterializeNaturalSortFullPermutation(snapshot, hits, hitCount);

        await Assert.That(sortedHits).IsEquivalentTo(permutation, CollectionOrdering.Matching);
        var hitIds = new HashSet<int>(sortedHits);
        await Assert.That(sortedHits).IsEquivalentTo(Expected(snapshot, hitIds.Contains), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Repeated_queries_on_new_snapshots_return_the_same_order()
    {
        var expected = new SearchEngineSharp(new FixedSnapshotProvider(BuildSnapshot()))
            .Find("even", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending);

        for (int run = 0; run < 5; run++)
        {
            var engine = new SearchEngineSharp(new FixedSnapshotProvider(BuildSnapshot()));
            await Assert.That(engine.Find("even", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    // 4,000 documents. Word "pN" matches every N-th id, so its hit density is 1/N: 0.1%, 0.5%, 1%, 10%, 50%, 100%.
    // Sort texts repeat in 97 natural keys, written in forms that compare equal, so ties are common.
    private const int StrategyDocumentCount = 4_000;
    private static readonly int[] Periods = [1000, 200, 100, 10, 2, 1];

    private static IndexSnapshot BuildStrategySnapshot()
    {
        var entries = new List<KeyValuePair<int, IndexedEntry>>(StrategyDocumentCount);
        for (int id = StrategyDocumentCount; id >= 1; id--)
        {
            string searchText = string.Join(' ', Periods.Where(period => id % period == 0).Select(period => $"p{period}"));
            int key = id * 7919 % 97;
            string sortText = (id % 3) switch { 0 => $"File {key}", 1 => $"file 0{key}", _ => $"FILE_{key}" };
            entries.Add(new(id, new IndexedEntry(searchText, sortText, FacetValues.Create("n", id))));
        }

        return IndexSnapshotBuilder.Build(entries);
    }

    public static IEnumerable<(int Period, bool Filtered)> StrategyCases()
        => Periods.SelectMany(period => new[] { (period, false), (period, true) });

    [Test]
    [MethodDataSource(nameof(StrategyCases))]
    public async Task Cold_and_built_permutation_snapshots_give_the_reference_order(int period, bool filtered)
    {
        var cold = BuildStrategySnapshot();
        var warm = BuildStrategySnapshot();
        _ = warm.Segments[0].GetSortedPermutation();
        FacetFilter? filter = filtered ? FacetFilter.Range("n", 1, 3_000) : null;
        var expected = Expected(cold, id => id % period == 0 && (!filtered || id <= 3_000));

        var coldResults = new SearchEngineSharp(new FixedSnapshotProvider(cold))
            .Find($"p{period}", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending, filter);
        var warmResults = new SearchEngineSharp(new FixedSnapshotProvider(warm))
            .Find($"p{period}", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending, filter);

        await Assert.That(coldResults).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(warmResults).IsEquivalentTo(coldResults, CollectionOrdering.Matching);
    }

    // The documents of the smaller segments of an IndexUpdater snapshot are placed into the built permutation of the
    // largest one. Half the sort texts are longer than NaturalSortKeyBuilder builds on the stack (341 characters), and
    // documents of different segments share keys in forms that compare equal, so the placement breaks those ties by id.
    [Test]
    public async Task Documents_of_smaller_segments_take_their_place_in_the_largest_permutation()
    {
        string longText = new('x', 400);
        IndexedEntry Entry(int id) => new("all", (id % 4) switch
        {
            0 => $"Item {id % 13}",
            1 => $"item_0{id % 13}",
            2 => $"{longText} {id % 7}",
            _ => $"{longText.ToUpperInvariant()}-0{id % 7}",
        });

        var (engine, updater, provider) = TestIndex.Create(null, new MergePolicy { Background = false });
        var entries = new Dictionary<int, IndexedEntry>();
        for (int id = 2; id <= 400; id += 2)
            entries[id] = Entry(id);
        updater.RebuildFrom(entries);
        _ = provider.Current.Segments[0].GetSortedPermutation();

        // New odd ids fall between base ids of equal keys; replaced even ids leave a dead base document behind.
        var changes = new Dictionary<int, IndexedEntry>();
        for (int id = 1; id <= 401; id += 6)
            changes[id] = Entry(id);
        for (int id = 4; id <= 400; id += 20)
            changes[id] = Entry(id + 1);
        updater.AddOrUpdateEntries(changes);
        foreach (var (id, entry) in changes)
            entries[id] = entry;

        var snapshot = provider.Current;
        await Assert.That(snapshot.Segments.Length).IsEqualTo(2);
        await Assert.That(snapshot.Dead.Any(dead => dead is not null)).IsTrue();

        var results = engine.Find("all", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending);

        var expected = entries
            .OrderBy(entry => NaturalSortKeyBuilder.Build(entry.Value.SortText), StringComparer.Ordinal)
            .ThenBy(entry => entry.Key)
            .Select(entry => entry.Key)
            .ToList();
        await Assert.That(ResultMaterializer.UsesSortKPath(snapshot, results.Count)).IsFalse();
        await Assert.That(results).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    // A text query and a filter-only query with 10% of the documents as hits; the filter-only query takes its own path
    // through SearchEngineSharp but must choose its sort strategy the same way.
    [Test]
    [Arguments("p10", false)]
    [Arguments("", true)]
    public async Task Few_hits_on_a_cold_snapshot_do_not_build_the_permutation(string query, bool filterOnly)
    {
        var snapshot = BuildStrategySnapshot();
        var engine = new SearchEngineSharp(new FixedSnapshotProvider(snapshot));
        FacetFilter? filter = filterOnly ? FacetFilter.Range("n", 1, StrategyDocumentCount / 10) : null;

        var results = engine.Find(query, WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending, filter);

        var expected = Expected(snapshot, filterOnly ? id => id <= StrategyDocumentCount / 10 : id => id % 10 == 0);
        await Assert.That(results).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(snapshot.Segments[0].IsSortedPermutationBuilt).IsFalse();
        await Assert.That(snapshot.Segments[0].SortedPermutationBuildCount).IsEqualTo(0);
    }

    [Test]
    public async Task No_hits_return_nothing_without_building_the_permutation()
    {
        var (engine, updater, provider) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("alpha", "alpha"),
            [2] = new("beta", "beta"),
        });

        var results = engine.Find("zzzznotfound", WordMatchMethod.Within, enableOperators: true, SearchSortMode.NaturalSortAscending);

        await Assert.That(results).IsEmpty();
        await Assert.That(provider.Current.Segments[0].IsSortedPermutationBuilt).IsFalse();
    }

    [Test]
    public async Task The_built_permutation_replaces_sorting_the_hits_above_the_limit()
    {
        var snapshot = BuildStrategySnapshot();
        int aboveLimit = (int)(StrategyDocumentCount * SelectivityThresholds.NaturalSortBuiltPermutationSortKMaxFraction) + 1;

        await Assert.That(ResultMaterializer.UsesSortKPath(snapshot, aboveLimit)).IsTrue();
        _ = snapshot.Segments[0].GetSortedPermutation();

        await Assert.That(snapshot.Segments[0].IsSortedPermutationBuilt).IsTrue();
        await Assert.That(ResultMaterializer.UsesSortKPath(snapshot, aboveLimit)).IsFalse();
        await Assert.That(ResultMaterializer.UsesSortKPath(snapshot, aboveLimit - 1)).IsTrue();
    }
}
