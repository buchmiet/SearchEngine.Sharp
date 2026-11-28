using System.Text;
using SearchEngine.Filters;
using SearchEngine.Index;
using SearchEngine.Query;
using SearchEngine.Snapshots;
using SearchEngine.Sorting;

namespace SearchEngine.Sharp.Tests;

// Find with maxResults returns the first maxResults ids of the unlimited Find, in every sort mode. The reference is the
// unlimited Find on a second, identical snapshot, so the limited engine sees only limited calls and its sort
// permutation is built only when the strategy rules build it; "warm" builds it first, to cover the walk of a built
// permutation. Small alphabets make sort keys collide, so the tie rule (record id) decides many positions.
public class LimitedFindTests
{
    private static readonly string[] Words = ["a", "b", "ab", "ba", "abc", "cab", "bca", "a1", "a2", "b10", "aab", "abab"];
    private static readonly string[] Joiners = [" ", "-", ".", "_"];

    private static readonly string[] Queries =
    [
        "a", "b", "ab", "abc", "a1", "b10", "zz",
        "*", "a*", "*b", "*a*", "a?", "?b*", "ab*",
        "ab ba", "ab OR ba", "NOT ab", "ab NOT a1", "a1 OR b10 OR cab", "(a OR b) AND NOT ab",
    ];

    private static readonly string[] Regexes = ["^a", "b$", "^[ab]+$", "1"];

    private static readonly FacetFilter[] Filters =
    [
        FacetFilter.Range("size", 1, 3),
        FacetFilter.Range("size", 0, 0),
        FacetFilter.Mask("flag", 1),
        FacetFilter.Range("rare", 1, 10),
        FacetFilter.Range("missing", 1, 1),
    ];

    private static readonly string[] FilteredQueries = ["", "a", "ab", "*", "NOT ab", "ab OR ba"];

    private static readonly SearchSortMode[] SortModes = [SearchSortMode.SnapshotOrder, SearchSortMode.NaturalSortAscending];

    private const int InitialCount = 1500;

    public static IEnumerable<(string Layout, string Preset, bool Warm)> Cases()
    {
        foreach (string layout in new[] { "one segment", "many segments" })
        foreach (string preset in new[] { "Default", "FileMask" })
        foreach (bool warm in new[] { false, true })
            yield return (layout, preset, warm);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Find_with_a_limit_is_the_start_of_the_unlimited_Find(string layout, string presetName, bool warm)
    {
        var tokenization = presetName == "Default" ? SearchTokenization.Default : SearchTokenization.FileMask;
        var limitedProvider = Build(layout, tokenization);
        var referenceProvider = Build(layout, tokenization);
        if (layout == "many segments")
            await Assert.That(limitedProvider.Current.Segments.Length).IsGreaterThan(2);
        if (warm)
            limitedProvider.Current.Primary.GetSortedPermutation();

        var limited = new SearchEngineSharp(limitedProvider);
        var reference = new SearchEngineSharp(referenceProvider);

        string? mismatch = null;
        foreach (string query in Queries)
        foreach (var method in new[] { WordMatchMethod.Exact, WordMatchMethod.Within })
        foreach (bool operators in new[] { false, true })
            mismatch ??= CompareQuery(limited, reference, query, method, operators, null);

        foreach (string regex in Regexes)
            mismatch ??= CompareQuery(limited, reference, regex, WordMatchMethod.Regex, false, null);

        foreach (var filter in Filters)
        foreach (string query in FilteredQueries)
            mismatch ??= CompareQuery(limited, reference, query, WordMatchMethod.Exact, true, filter);

        await Assert.That(mismatch).IsNull();
    }

    private static string? CompareQuery(
        SearchEngineSharp limited,
        SearchEngineSharp reference,
        string query,
        WordMatchMethod method,
        bool operators,
        FacetFilter? filter)
    {
        foreach (var sortMode in SortModes)
        {
            var all = reference.Find(query, method, operators, sortMode, filter);
            var limits = new SortedSet<int> { 0, 1, 2, 3, 10, 300, all.Count / 2, all.Count - 1, all.Count, all.Count + 1, int.MaxValue };
            foreach (int limit in limits)
            {
                if (limit < 0)
                    continue;

                var actual = limited.Find(query, method, operators, sortMode, filter, limit);
                var expected = all.Take(limit);
                if (!actual.SequenceEqual(expected))
                {
                    return $"'{query}' {method} operators={operators} filter={filter is not null} {sortMode} limit={limit} of {all.Count}: "
                        + $"got [{string.Join(",", actual.Take(12))}], expected [{string.Join(",", expected.Take(12))}]";
                }
            }
        }

        return null;
    }

    // The snapshot is the same on every call with the same arguments.
    private static IndexSnapshotProvider Build(string layout, SearchTokenization tokenization)
    {
        var random = new Random(7);
        var initial = new Dictionary<int, IndexedEntry>();
        int nextId = 1;
        for (int i = 0; i < InitialCount; i++)
            initial[nextId++] = RandomEntry(random);

        if (layout == "one segment")
            return new IndexSnapshotProvider(IndexSnapshotBuilder.Build(initial, tokenization));

        // A policy that does not merge: every change leaves its own segment, with dead documents in the older ones.
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider, tokenization, new MergePolicy { Background = false, FanOut = 1000, MaxSegments = 1000, DeadRatio = 1.1 });
        updater.RebuildFrom(initial);
        var live = initial.Keys.ToList();
        for (int step = 0; step < 150; step++)
        {
            int roll = random.Next(10);
            if (roll < 4)
            {
                updater.AddEntry(nextId, RandomEntry(random));
                live.Add(nextId++);
            }
            else if (roll < 6)
            {
                updater.RefreshEntry(live[random.Next(live.Count)], RandomEntry(random));
            }
            else if (roll < 8)
            {
                int at = random.Next(live.Count);
                updater.RemoveEntry(live[at]);
                live.RemoveAt(at);
            }
            else
            {
                var batch = new List<KeyValuePair<int, IndexedEntry>>();
                for (int i = random.Next(1, 40); i > 0; i--)
                {
                    batch.Add(new(nextId, RandomEntry(random)));
                    live.Add(nextId++);
                }

                updater.AddOrUpdateEntries(batch);
            }
        }

        return provider;
    }

    private static IndexedEntry RandomEntry(Random random)
    {
        var text = new StringBuilder(Words[random.Next(Words.Length)]);
        for (int i = random.Next(3); i > 0; i--)
            text.Append(Joiners[random.Next(Joiners.Length)]).Append(Words[random.Next(Words.Length)]);
        if (random.Next(5) == 0)
            text[0] = char.ToUpperInvariant(text[0]);

        var facets = new List<KeyValuePair<string, long>>();
        if (random.Next(2) == 0)
            facets.Add(new("size", random.Next(6)));
        if (random.Next(10) < 3)
            facets.Add(new("flag", random.Next(4)));
        if (random.Next(40) == 0)
            facets.Add(new("rare", random.Next(1, 4)));

        string name = text.ToString();
        return new IndexedEntry(name, name, facets.Count == 0 ? null : FacetValues.Create([.. facets]));
    }

    // ── The strategy for a limit ──

    [Test]
    public async Task A_limit_below_the_hit_count_selects_instead_of_walking_a_built_permutation_only_for_few_hits()
    {
        // 1,000,000 documents, 10 wanted: the walk reads about 10 * 1,000,000 / hits documents.
        await Assert.That(SelectivityThresholds.UseSortKPath(100, 1_000_000, permutationBuilt: true, limit: 10)).IsTrue();
        await Assert.That(SelectivityThresholds.UseSortKPath(100_000, 1_000_000, permutationBuilt: true, limit: 10)).IsFalse();
        // A limit that does not cut the hits leaves the rule for all hits.
        await Assert.That(SelectivityThresholds.UseSortKPath(100_000, 1_000_000, permutationBuilt: true, limit: 100_000)).IsFalse();
        await Assert.That(SelectivityThresholds.UseSortKPath(100_000, 1_000_000, permutationBuilt: true, limit: int.MaxValue)).IsFalse();
        // An unbuilt permutation is built under the rule for all hits, whatever the limit.
        await Assert.That(SelectivityThresholds.UseSortKPath(100_000, 1_000_000, permutationBuilt: false, limit: 10)).IsTrue();
        await Assert.That(SelectivityThresholds.UseSortKPath(900_000, 1_000_000, permutationBuilt: false, limit: 10)).IsFalse();
    }

    [Test]
    public async Task A_limited_natural_sort_does_not_build_the_permutation_of_a_cheap_query()
    {
        var snapshot = BuildTieHeavySnapshot(2000);
        var engine = new SearchEngineSharp(new FixedSnapshotProvider(snapshot));

        engine.Find("even", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending, null, 5);

        await Assert.That(snapshot.Primary.IsSortedPermutationBuilt).IsFalse();
    }

    private static IndexSnapshot BuildTieHeavySnapshot(int count)
    {
        var entries = new List<KeyValuePair<int, IndexedEntry>>(count);
        for (int id = count; id >= 1; id--)
            entries.Add(new(id, new IndexedEntry(id % 2 == 0 ? "all even" : "all odd", id % 3 == 0 ? "Item 7" : "item 07")));
        return IndexSnapshotBuilder.Build(entries);
    }

    // ── Arguments ──

    [Test]
    public async Task A_negative_limit_is_rejected_and_names_the_parameter()
    {
        var engine = new SearchEngineSharp(new FixedSnapshotProvider(BuildTieHeavySnapshot(10)));

        var exception = await Assert.That(() => engine.Find("even", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, null, -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(exception!.ParamName).IsEqualTo("maxResults");
        await Assert.That(() => engine.Find("even", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, null, int.MinValue))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task A_negative_limit_is_rejected_on_an_empty_index_too()
    {
        var engine = new SearchEngineSharp(new IndexSnapshotProvider());

        await Assert.That(() => engine.Find("x", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, null, -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task A_null_expression_is_rejected_before_the_limit_is_looked_at()
    {
        var engine = new SearchEngineSharp(new IndexSnapshotProvider());

        var exception = await Assert.That(() => engine.Find(null!, WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, null, 0))
            .ThrowsExactly<ArgumentNullException>();
        await Assert.That(exception!.ParamName).IsEqualTo("expression");
    }

    [Test]
    public async Task A_limit_of_zero_returns_an_empty_new_list()
    {
        var engine = new SearchEngineSharp(new FixedSnapshotProvider(BuildTieHeavySnapshot(10)));

        var first = engine.Find("even", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending, null, 0);
        var second = engine.Find("even", WordMatchMethod.Exact, false, SearchSortMode.NaturalSortAscending, null, 0);

        await Assert.That(first).IsEmpty();
        await Assert.That(first).IsNotSameReferenceAs(second);
    }

    [Test]
    public async Task The_limited_list_is_a_new_list_of_at_most_the_limit()
    {
        var engine = new SearchEngineSharp(new FixedSnapshotProvider(BuildTieHeavySnapshot(50)));

        var ids = engine.Find("all", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, null, 7);
        ids.Add(-1);

        await Assert.That(ids.Count).IsEqualTo(8);
        await Assert.That(engine.Find("all", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, null, 7).Count).IsEqualTo(7);
    }

    // ── The default implementation of the interface ──

    private sealed class FakeEngine(List<int> ids) : ISearchEngine
    {
        public int FindCalls { get; private set; }

        public int DocumentCount => ids.Count;

        public int UniqueWordCount => 0;

        public List<int> Find(string expression, WordMatchMethod method, bool enableOperators = false)
            => Find(expression, method, enableOperators, SearchSortMode.SnapshotOrder);

        public List<int> Find(string expression, WordMatchMethod method, bool enableOperators, SearchSortMode sortMode)
            => Find(expression, method, enableOperators, sortMode, null);

        public List<int> Find(string expression, WordMatchMethod method, bool enableOperators, SearchSortMode sortMode, FacetFilter? filter)
        {
            FindCalls++;
            return [.. ids];
        }

        public int CountMatches(string expression, WordMatchMethod method, bool enableOperators = false) => ids.Count;

        public int CountMatches(string expression, WordMatchMethod method, bool enableOperators, FacetFilter? filter) => ids.Count;
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(1, 1)]
    [Arguments(4, 4)]
    [Arguments(5, 5)]
    [Arguments(6, 5)]
    [Arguments(int.MaxValue, 5)]
    public async Task The_default_implementation_cuts_the_unlimited_list(int limit, int expectedCount)
    {
        ISearchEngine engine = new FakeEngine([5, 4, 3, 2, 1]);

        var ids = engine.Find("x", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, null, limit);

        await Assert.That(ids).IsEquivalentTo(new[] { 5, 4, 3, 2, 1 }.Take(expectedCount), CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_default_implementation_validates_its_arguments_and_skips_the_search_for_zero()
    {
        var fake = new FakeEngine([1, 2, 3]);
        ISearchEngine engine = fake;

        await Assert.That(() => engine.Find("x", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, null, -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => engine.Find(null!, WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, null, 1))
            .ThrowsExactly<ArgumentNullException>();
        await Assert.That(engine.Find("x", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, null, 0)).IsEmpty();
        await Assert.That(fake.FindCalls).IsEqualTo(0);
    }

    // ── FastBitSet and NaturalOrder ──

    [Test]
    [Arguments(0), Arguments(1), Arguments(63), Arguments(64), Arguments(65), Arguments(130), Arguments(1000)]
    public async Task CopyFirstSetBitOrdinals_and_EnumerateSetBits_match_CopySetBitOrdinals(int length)
    {
        foreach (double density in new[] { 0.0, 0.01, 0.3, 1.0 })
        {
            var random = new Random(length);
            // A wrapped buffer whose bits past the length are set: those ordinals must not appear.
            var words = new ulong[((length + 63) >> 6) + 2];
            var set = new FastBitSet(words, length);
            for (int i = 0; i < length; i++)
            {
                if (random.NextDouble() < density)
                    set.Add(i);
            }

            if ((length & 63) != 0)
                words[length >> 6] |= ~0UL << (length & 63);

            var all = new int[length];
            int count = set.CopySetBitOrdinals(all);
            var expected = all.Take(count).ToArray();

            var enumerated = new List<int>();
            foreach (int ordinal in set.EnumerateSetBits())
                enumerated.Add(ordinal);
            await Assert.That(enumerated).IsEquivalentTo(expected, CollectionOrdering.Matching);

            foreach (int take in new[] { 0, 1, 2, count - 1, count, count + 1, count + 70 })
            {
                if (take < 0)
                    continue;

                var destination = new int[take];
                int written = set.CopyFirstSetBitOrdinals(destination);
                await Assert.That(written).IsEqualTo(Math.Min(take, count));
                await Assert.That(destination.Take(written)).IsEquivalentTo(expected.Take(take), CollectionOrdering.Matching);
            }
        }
    }

    [Test]
    public async Task SelectFirst_returns_the_first_documents_in_natural_order_with_ties_by_id()
    {
        var snapshot = BuildTieHeavySnapshot(500);
        var set = new FastBitSet(snapshot.OrdinalCount);
        for (int ordinal = 0; ordinal < snapshot.OrdinalCount; ordinal += 2)
            set.Add(ordinal);

        var all = new List<int>();
        foreach (int ordinal in set.EnumerateSetBits())
            all.Add(ordinal);
        int[] sorted = [.. all];
        NaturalOrder.Sort(sorted, snapshot);

        foreach (int take in new[] { 1, 2, 5, 40, sorted.Length - 1, sorted.Length })
        {
            var selected = new int[take];
            NaturalOrder.SelectFirst(set, snapshot, selected);
            await Assert.That(selected).IsEquivalentTo(sorted.Take(take), CollectionOrdering.Matching);
        }
    }
}
