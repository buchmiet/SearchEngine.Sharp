using SearchEngine.Sorting;

namespace SearchEngine.Sharp.Tests;

// The natural order documented in docs/query-semantics.md#result-ordering, one row per rule.
public class NaturalSortOrderTests
{
    [Theory]
    // A text part compares with a text part: a shorter part that is a prefix of the other comes first.
    [InlineData("a 2", "ab")]
    [InlineData("a", "a 2")]
    [InlineData("a2", "ab")]
    [InlineData("x1", "xa")]
    [InlineData("a-c", "ab")]
    [InlineData("a b", "ab")]
    [InlineData("a.b", "ab")]
    [InlineData("report final", "reports")]
    // Numbers compare by value.
    [InlineData("file2", "file10")]
    [InlineData("file9", "file10")]
    [InlineData("file 2", "file10")]
    [InlineData("0", "1")]
    // A number part comes before a text part at the same position.
    [InlineData("x 1", "x a")]
    [InlineData("1", "(1)")]
    // Numbers longer than 12 digits and longer than a long still compare by value.
    [InlineData("a999999999999", "a1000000000000")]
    [InlineData("a99999999999999999999", "a100000000000000000000")]
    [InlineData("a18446744073709551616", "a18446744073709551617")]
    // Leading zeros do not count; the parts after the number still decide.
    [InlineData("img_0012.jpg", "img_12a.jpg")]
    [InlineData("a007b", "a7c")]
    // Digits of other scripts compare by their value.
    [InlineData("a٢", "a10")]
    // Text parts compare by UTF-16 code unit after lowercasing, independent of the culture.
    [InlineData("z", "é")]
    [InlineData("Apple", "banana")]
    public void Natural_order_puts_the_first_text_before_the_second(string first, string second)
    {
        string firstKey = NaturalSortKeyBuilder.Build(first);
        string secondKey = NaturalSortKeyBuilder.Build(second);

        Assert.True(string.CompareOrdinal(firstKey, secondKey) < 0, $"'{first}' should sort before '{second}'.");
        Assert.True(string.CompareOrdinal(secondKey, firstKey) > 0, $"'{second}' should sort after '{first}'.");
    }

    [Theory]
    [InlineData("File2", "file2")]
    [InlineData("ABC", "abc")]
    [InlineData("007", "7")]
    [InlineData("00", "0")]
    [InlineData("a_b", "a b")]
    [InlineData("a-b", "a/b")]
    [InlineData("a٣", "a3")]
    public void Natural_order_treats_both_texts_as_equal(string first, string second)
    {
        Assert.Equal(NaturalSortKeyBuilder.Build(first), NaturalSortKeyBuilder.Build(second));
    }

    [Theory]
    [InlineData(65_534, 65_535)]
    [InlineData(65_535, 65_536)]
    [InlineData(70_000, 131_073)]
    public void Natural_order_compares_very_long_numbers_by_value(int shorterDigits, int longerDigits)
    {
        string smaller = new('9', shorterDigits);
        string larger = "1" + new string('0', longerDigits - 1);

        string[] sorted = new[] { "x" + larger, "x" + smaller, "x" + larger + "a", "x" + smaller + "a" }
            .OrderBy(NaturalSortKeyBuilder.Build, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["x" + smaller, "x" + smaller + "a", "x" + larger, "x" + larger + "a"], sorted);
    }

    [Fact]
    public void Find_sorts_a_text_part_before_a_longer_text_part()
    {
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("item ab", "ab"),
            [2] = new("item a 2", "a 2"),
            [3] = new("item a", "a"),
            [4] = new("item a10", "a10"),
            [5] = new("item a2", "a2"),
        });

        var results = new SearchEngineSharp(provider).Find("item", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending);

        Assert.Equal([3, 2, 5, 4, 1], results);
    }
}
