using SearchEngine.Sorting;

namespace SearchEngine.Sharp.Tests;

// The order of search results (docs/query-semantics.md#result-ordering). NaturalSortAscending compares the natural
// sort keys of IndexedEntry.SortText (NaturalSortKeyBuilder): one row per documented rule of the key, then the rules
// of what a search sorts. A user who sorts file names or model numbers relies on file2 before file10 and on the order
// not depending on culture, platform or the search text. SnapshotOrder keeps the order the entries were given in
// (SearchSortMode). Ties between equal keys and the agreement of the materialization paths are in
// NaturalSortTieBreakTests.
public class ResultOrderTests
{
    [Test]
    // A text part compares with a text part: a shorter part that is a prefix of the other comes first.
    [Arguments("a 2", "ab")]
    [Arguments("a", "a 2")]
    [Arguments("a2", "ab")]
    [Arguments("x1", "xa")]
    [Arguments("a-c", "ab")]
    [Arguments("a b", "ab")]
    [Arguments("a.b", "ab")]
    [Arguments("report final", "reports")]
    // Numbers compare by value.
    [Arguments("file2", "file10")]
    [Arguments("file9", "file10")]
    [Arguments("file 2", "file10")]
    [Arguments("0", "1")]
    // A number part comes before a text part at the same position.
    [Arguments("x 1", "x a")]
    [Arguments("1", "(1)")]
    // Numbers longer than 12 digits and longer than a long still compare by value.
    [Arguments("a999999999999", "a1000000000000")]
    [Arguments("a99999999999999999999", "a100000000000000000000")]
    [Arguments("a18446744073709551616", "a18446744073709551617")]
    // Leading zeros do not count; the parts after the number still decide.
    [Arguments("img_0012.jpg", "img_12a.jpg")]
    [Arguments("a007b", "a7c")]
    // Digits of other scripts compare by their value.
    [Arguments("a٢", "a10")]
    // Text parts compare by UTF-16 code unit after lowercasing, independent of the culture.
    [Arguments("z", "é")]
    [Arguments("Apple", "banana")]
    public async Task Natural_order_puts_the_first_text_before_the_second(string first, string second)
    {
        string firstKey = NaturalSortKeyBuilder.Build(first);
        string secondKey = NaturalSortKeyBuilder.Build(second);

        await Assert.That(string.CompareOrdinal(firstKey, secondKey)).IsLessThan(0).Because($"'{first}' should sort before '{second}'.");
        await Assert.That(string.CompareOrdinal(secondKey, firstKey)).IsGreaterThan(0).Because($"'{second}' should sort after '{first}'.");
    }

    [Test]
    [Arguments("File2", "file2")]
    [Arguments("ABC", "abc")]
    [Arguments("007", "7")]
    [Arguments("00", "0")]
    [Arguments("a_b", "a b")]
    [Arguments("a-b", "a/b")]
    [Arguments("a٣", "a3")]
    public async Task Natural_order_treats_both_texts_as_equal(string first, string second)
    {
        await Assert.That(NaturalSortKeyBuilder.Build(second)).IsEqualTo(NaturalSortKeyBuilder.Build(first));
    }

    [Test]
    [Arguments(65_534, 65_535)]
    [Arguments(65_535, 65_536)]
    [Arguments(70_000, 131_073)]
    public async Task Natural_order_compares_very_long_numbers_by_value(int shorterDigits, int longerDigits)
    {
        string smaller = new('9', shorterDigits);
        string larger = "1" + new string('0', longerDigits - 1);

        string[] sorted = new[] { "x" + larger, "x" + smaller, "x" + larger + "a", "x" + smaller + "a" }
            .OrderBy(NaturalSortKeyBuilder.Build, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(sorted).IsEquivalentTo(["x" + smaller, "x" + smaller + "a", "x" + larger, "x" + larger + "a"], CollectionOrdering.Matching);
    }

    // Model numbers combine every rule above: numbers by value, a number before a text part, fewer parts first.
    [Test]
    [Arguments("GA-2,GA-10,GA-100,GA-100-1,GA-100-1A,GA-100-1B,GA-100A,GA-1000")]
    [Arguments("GST-B400C-1A,GST-B400D-1A,GST-B400D-1B")]
    [Arguments("DW-5600BB-1,DW-5600BB-1A,DW-5600BB-2,DW-5600E-1")]
    public async Task Natural_order_sorts_model_numbers(string expectedOrderList)
    {
        string[] expectedOrder = expectedOrderList.Split(',');

        string[] sorted = expectedOrder.Reverse()
            .OrderBy(NaturalSortKeyBuilder.Build, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(sorted).IsEquivalentTo(expectedOrder, CollectionOrdering.Matching);
    }

    [Test]
    public async Task An_empty_sort_text_has_an_empty_key()
    {
        await Assert.That(NaturalSortKeyBuilder.Build("")).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task A_search_sorts_a_text_part_before_a_longer_text_part()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("item ab", "ab"),
            [2] = new("item a 2", "a 2"),
            [3] = new("item a", "a"),
            [4] = new("item a10", "a10"),
            [5] = new("item a2", "a2"),
        });

        var results = engine.Find("item", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending);

        await Assert.That(results).IsEquivalentTo([3, 2, 5, 4, 1], CollectionOrdering.Matching);
    }

    // The search text can carry synonyms and descriptions; only the sort text decides the order.
    [Test]
    public async Task Natural_sort_orders_by_the_sort_text_not_the_search_text()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [10] = new("GA-100 G-Shock digital watch", "GA-100"),
            [20] = new("GA-2 G-Shock analog watch", "GA-2"),
        });

        var results = engine.Find("shock", WordMatchMethod.Within, false, SearchSortMode.NaturalSortAscending);

        await Assert.That(results).IsEquivalentTo([20, 10], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(WordMatchMethod.Exact, "ga")]
    [Arguments(WordMatchMethod.Within, "model")]
    public async Task Sorting_does_not_change_which_entries_match(WordMatchMethod method, string query)
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("GA-100 model", "GA-100"),
            [2] = new("GA-1000 model", "GA-1000"),
            [3] = new("GA-100-1 model", "GA-100-1"),
        });

        var unsorted = engine.Find(query, method);
        var sorted = engine.Find(query, method, false, SearchSortMode.NaturalSortAscending);

        await Assert.That(sorted.Order()).IsEquivalentTo(unsorted.Order(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Snapshot_order_is_the_order_the_entries_were_given_in()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("GA-1000 watch", "GA-1000"),
            [2] = new("GA-100 watch", "GA-100"),
        });
        updater.AddEntry(0, new IndexedEntry("GA-10 watch", "GA-10"));

        var snapshotOrder = engine.Find("ga", WordMatchMethod.Within);
        var naturalOrder = engine.Find("ga", WordMatchMethod.Within, false, SearchSortMode.NaturalSortAscending);

        await Assert.That(snapshotOrder).IsEquivalentTo([1, 2, 0], CollectionOrdering.Matching);
        await Assert.That(naturalOrder).IsEquivalentTo([0, 2, 1], CollectionOrdering.Matching);
    }
}
