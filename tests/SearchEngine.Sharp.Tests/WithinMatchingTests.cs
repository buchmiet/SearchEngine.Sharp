using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// WordMatchMethod.Within finds a term anywhere inside a token; Exact needs the whole token. Within narrows the
// candidate tokens with the bigram index of the segment (IndexSegment.BigramWordIndices, built on first use) and then
// checks the substring, so sharing bigrams is not enough to match. A user searching part of a name expects exactly the
// names a substring scan would give.
public class WithinMatchingTests
{
    [Test]
    public async Task Exact_matches_whole_tokens_and_Within_matches_inside_them()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = "alphabet",
            [2] = "alpha",
        });

        var exact = engine.Find("alph", WordMatchMethod.Exact, enableOperators: false);
        var within = engine.Find("alph", WordMatchMethod.Within, enableOperators: false);

        await Assert.That(exact).IsEmpty();
        await Assert.That(within.Order()).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
    }

    // Entry 1 holds every bigram of the query but not the query itself.
    [Test]
    [Arguments("abxba", "xxababyy", "abab", "abab")]
    [Arguments("aaxaax", "zaaaab", "aaaa", "aaaa")]
    public async Task Within_needs_the_whole_substring_not_only_its_bigrams(string bigramsOnly, string containsQuery, string isQuery, string query)
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, string>
        {
            [1] = bigramsOnly,
            [2] = containsQuery,
            [3] = isQuery,
        });

        var results = engine.Find(query, WordMatchMethod.Within, enableOperators: false);

        await Assert.That(results.Order()).IsEquivalentTo([2, 3], CollectionOrdering.Matching);
    }

    // The first Within queries on a new snapshot race to build the bigram index; each must still agree with a scan.
    [Test]
    public async Task Within_agrees_with_a_substring_scan_while_first_queries_race_to_build_the_bigram_index()
    {
        var rng = new Random(5);
        var entries = new Dictionary<int, string>();
        for (int id = 0; id < 2_000; id++)
        {
            var chars = new char[rng.Next(1, 12)];
            for (int c = 0; c < chars.Length; c++) chars[c] = "abcdeéx1"[rng.Next(8)];
            entries[id] = new string(chars) + " " + new string(chars, 0, chars.Length / 2 + 1);
        }

        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(entries);
        string[] queries = ["ab", "ba", "e", "éx", "x1", "abc", "dd", "1"];

        var results = new List<int>[queries.Length];
        Parallel.For(0, queries.Length, q => results[q] = engine.Find(queries[q], WordMatchMethod.Within));

        for (int q = 0; q < queries.Length; q++)
        {
            var expected = entries
                .Where(e => e.Value.Split(' ').Any(token => token.Contains(queries[q], StringComparison.Ordinal)))
                .Select(e => e.Key)
                .Order()
                .ToList();
            await Assert.That(results[q].Order().ToList()).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    // The packed bigram of U+FFFF U+FFFF is all ones, the same bits as -1: a token that starts with it must still be
    // registered under it, or Within misses it. NameMatcher scans the text and is the reference.
    [Test]
    public async Task Within_finds_a_token_that_starts_with_two_U_FFFF()
    {
        var (engine, updater, _) = TestIndex.Create();
        var documents = new Dictionary<int, string>
        {
            [1] = "\uFFFF\uFFFF doc",
            [2] = "other",
            [3] = "x \uFFFF\uFFFF y",
        };
        updater.RebuildFrom(documents);
        const string query = "\uFFFF\uFFFF";
        var matcher = NameMatcher.Create(query, WordMatchMethod.Within);
        int[] expected = [.. documents.Where(d => matcher.IsMatch(d.Value)).Select(d => d.Key)];

        var results = engine.Find(query, WordMatchMethod.Within);

        await Assert.That(expected).IsEquivalentTo([1, 3], CollectionOrdering.Matching);
        await Assert.That(results.Order()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    // White-box: a token with a repeated bigram (banana: an, na) is listed once under each bigram, so Within checks
    // each candidate token once.
    [Test]
    public async Task The_bigram_index_lists_a_token_once_per_bigram()
    {
        var (_, updater, provider) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("banana", "banana"),
        });
        IndexSnapshot snapshot = provider.Current;
        const int wordIndex = 0;
        int bigramAn = ('a' << 16) | 'n';
        int bigramNa = ('n' << 16) | 'a';

        bool hasAn = snapshot.Base.BigramWordIndices.TryGetValue(bigramAn, out int[]? anList);
        bool hasNa = snapshot.Base.BigramWordIndices.TryGetValue(bigramNa, out int[]? naList);

        await Assert.That(hasAn).IsTrue();
        await Assert.That(hasNa).IsTrue();
        await Assert.That(anList!.Count(i => i == wordIndex)).IsEqualTo(1);
        await Assert.That(naList!.Count(i => i == wordIndex)).IsEqualTo(1);
    }
}
