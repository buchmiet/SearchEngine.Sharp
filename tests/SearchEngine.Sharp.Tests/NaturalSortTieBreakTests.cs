using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Query;
using SearchEngine.Snapshots;
using SearchEngine.Sorting;

namespace SearchEngine.Sharp.Tests;

// Entries with equal natural sort keys come in ascending id order on every materialization path.
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
        var ids = snapshot.RecordIds;
        var sortTexts = snapshot.SortTextArray;
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
        await Assert.That(snapshot.SortedPermutationBuildCount).IsEqualTo(1);
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

        await Assert.That(results).IsEquivalentTo(Expected(snapshot, id => id % 2 == 1 && id >= 20 && id <= 170), CollectionOrdering.Matching);
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

        var sortedHits = ResultMaterializer.MaterializeNaturalSortKOnly(snapshot, hits, hitCount, snapshot.RecordIds);
        var permutation = ResultMaterializer.MaterializeNaturalSortFullPermutation(snapshot, hits, hitCount, snapshot.RecordIds);

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
}
