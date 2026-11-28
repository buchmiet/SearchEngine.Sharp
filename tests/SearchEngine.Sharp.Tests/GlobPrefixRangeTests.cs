using System.Text;
using SearchEngine.Pooling;
using SearchEngine.Query;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// A glob with a literal prefix visits only the tokens that start with it (IndexSegment.WordsByText, QueryMatcher.MatchGlob).
// The matcher still makes the final decision, so the result must equal the scan of every token, whatever the tokenization.
// A small alphabet makes prefixes collide; the alphabet also has upper-case letters, accents, a combining mark and a
// surrogate pair, so a prefix can end in the middle of a code point or a grapheme cluster.
public class GlobPrefixRangeTests
{
    private static readonly string[] Alphabet = ["a", "b", "c", "A", "B", "é", "é", "\U0001F600", "́"];
    private static readonly string[] Joiners = ["-", ".", "_", " "];

    private static readonly Dictionary<string, SearchTokenization> Presets = new()
    {
        ["Default"] = SearchTokenization.Default,
        ["FileMask"] = SearchTokenization.FileMask,
        ["DefaultCaseFold"] = SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.CaseFold),
        ["DefaultGrapheme"] = SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.GraphemeBoundaries),
        ["DefaultCaseFoldGrapheme"] = SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.CaseFold | UnicodeMatching.GraphemeBoundaries),
        ["DefaultComposeCaseFold"] = SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.Compose | UnicodeMatching.CaseFold),
        ["DefaultRemoveDiacriticsGrapheme"] = SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.RemoveDiacritics | UnicodeMatching.GraphemeBoundaries),
        ["FileMaskCaseFold"] = SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.CaseFold),
    };

    public static IEnumerable<(string Preset, int Seed)> Cases()
    {
        foreach (string preset in Presets.Keys)
        foreach (int seed in new[] { 1, 2 })
            yield return (preset, seed);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task The_prefix_range_finds_what_the_scan_finds_in_one_segment(string presetName, int seed)
    {
        var tokenization = Presets[presetName];
        var random = new Random(seed);
        var entries = new Dictionary<int, string>();
        for (int id = 0; id < 3000; id++)
            entries[id] = RandomName(random);

        var snapshot = IndexSnapshotBuilder.Build(entries, tokenization);

        string? mismatch = Compare(snapshot, random, entries.Values);
        await Assert.That(mismatch).IsNull();
        await Assert.That(snapshot.Segments[0].WordsByTextBuildCount).IsEqualTo(1);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task The_prefix_range_finds_what_the_scan_finds_across_segments_with_dead_documents(string presetName, int seed)
    {
        var tokenization = Presets[presetName];
        var random = new Random(seed);
        var (_, updater, provider) = TestIndex.Create(tokenization);
        var live = new Dictionary<int, string>();
        int nextId = 0;
        var initial = new Dictionary<int, string>();
        for (int i = 0; i < 1500; i++)
            initial[nextId++] = RandomName(random);
        updater.RebuildFrom(initial);
        foreach (var (id, name) in initial)
            live[id] = name;

        string? mismatch = null;
        int multiSegment = 0;
        int withDead = 0;
        for (int step = 0; step < 120 && mismatch is null; step++)
        {
            for (int i = 0; i < 12; i++)
            {
                int roll = random.Next(10);
                if (roll < 5 || live.Count == 0)
                {
                    string name = RandomName(random);
                    updater.AddEntry(nextId, name);
                    live[nextId++] = name;
                }
                else if (roll < 8)
                {
                    int id = live.Keys.ElementAt(random.Next(live.Count));
                    string name = RandomName(random);
                    updater.RefreshEntry(id, name);
                    live[id] = name;
                }
                else
                {
                    int id = live.Keys.ElementAt(random.Next(live.Count));
                    updater.RemoveEntry(id);
                    live.Remove(id);
                }
            }

            var snapshot = provider.Current;
            if (snapshot.Segments.Length > 1)
                multiSegment++;
            if (snapshot.Dead.Any(d => d is not null))
                withDead++;
            if (step % 10 == 0)
                mismatch = Compare(snapshot, random, live.Values) is { } m ? $"step {step}: {m}" : null;
        }

        await Assert.That(mismatch).IsNull();
        await Assert.That(multiSegment).IsPositive();
        await Assert.That(withDead).IsPositive();
    }

    // A segment below the size limit is scanned without building the text order.
    [Test]
    public async Task A_small_segment_is_scanned_and_builds_no_text_order()
    {
        var snapshot = IndexSnapshotBuilder.Build(new Dictionary<int, string> { [1] = "report", [2] = "repeat", [3] = "log" });

        using var qc = new QueryContext(snapshot.OrdinalCount);
        var result = QueryMatcher.MatchGlob("rep*", qc, snapshot);

        await Assert.That(result.GetTrueCount()).IsEqualTo(2);
        await Assert.That(snapshot.Segments[0].WordsByTextBuildCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_pattern_without_a_literal_prefix_builds_no_text_order()
    {
        var entries = new Dictionary<int, string>();
        for (int id = 0; id < 2000; id++)
            entries[id] = $"name{id}x";
        var snapshot = IndexSnapshotBuilder.Build(entries);

        using var qc = new QueryContext(snapshot.OrdinalCount);
        QueryMatcher.MatchGlob("*x", qc, snapshot);
        QueryMatcher.MatchGlob("?ame1*", qc, snapshot);

        await Assert.That(snapshot.Segments[0].WordsByTextBuildCount).IsEqualTo(0);
    }

    // Tokens "name0x" .. "name1999x": the run of "name1" holds more than a fifth of them, so it is scanned.
    [Test]
    public async Task A_run_that_holds_many_tokens_gives_the_same_hits()
    {
        var entries = new Dictionary<int, string>();
        for (int id = 0; id < 2000; id++)
            entries[id] = $"name{id}x";
        var snapshot = IndexSnapshotBuilder.Build(entries);

        using var qc = new QueryContext(snapshot.OrdinalCount);
        var result = QueryMatcher.MatchGlob("name1*", qc, snapshot);

        await Assert.That(result.GetTrueCount()).IsEqualTo(entries.Values.Count(n => n.StartsWith("name1", StringComparison.Ordinal)));
        await Assert.That(snapshot.Segments[0].WordsByTextBuildCount).IsEqualTo(1);
    }

    [Test]
    public async Task PrefixRange_bounds_the_tokens_that_start_with_the_prefix()
    {
        var entries = new Dictionary<int, string>
        {
            [1] = "a", [2] = "ab", [3] = "abc", [4] = "abd", [5] = "b", [6] = "\U0001F600x", [7] = "\U0001F600y",
        };
        var segment = IndexSnapshotBuilder.Build(entries).Segments[0];

        string[] Run(string prefix)
        {
            var (start, end) = segment.PrefixRange(prefix);
            return [.. segment.WordsByText.AsSpan(start, end - start).ToArray().Select(i => segment.WordAt(i).ToString()).Order(StringComparer.Ordinal)];
        }

        await Assert.That(Run("ab")).IsEquivalentTo(new[] { "ab", "abc", "abd" }, CollectionOrdering.Matching);
        await Assert.That(Run("a")).IsEquivalentTo(new[] { "a", "ab", "abc", "abd" }, CollectionOrdering.Matching);
        await Assert.That(Run("abcd")).IsEmpty();
        await Assert.That(Run("c")).IsEmpty();
        await Assert.That(Run("\U0001F600")).IsEquivalentTo(new[] { "\U0001F600x", "\U0001F600y" }, CollectionOrdering.Matching);
        await Assert.That(Run("\ud83d")).IsEquivalentTo(new[] { "\U0001F600x", "\U0001F600y" }, CollectionOrdering.Matching);
    }

    private static string RandomName(Random random)
    {
        var name = new StringBuilder();
        int parts = random.Next(1, 4);
        for (int p = 0; p < parts; p++)
        {
            if (p > 0)
                name.Append(Joiners[random.Next(Joiners.Length)]);
            int length = random.Next(1, 7);
            for (int i = 0; i < length; i++)
                name.Append(Alphabet[random.Next(Alphabet.Length)]);
        }

        return name.ToString();
    }

    // Patterns of every kind: prefixes cut from real names (also in the middle of a surrogate pair), a prefix longer
    // than any token, prefixes that no token has, and no literal prefix at all.
    private static string? Compare(IndexSnapshot snapshot, Random random, IEnumerable<string> names)
    {
        var sample = names.Take(200).ToArray();
        var patterns = new List<string>
        {
            "a*", "A*", "b?", "ab?", "a*b", "?b*", "?*", "*a", "*a*", "a?*", "a??", "a*c*", "é*", "e*", "é*", "\U0001F600*",
            "\ud83d*", "\ud83d?", "\U0001F600?", "e?", "abcabcabcabcabc*", "zzz*", "zz?", "́*", "á*", "ca*", "cab*c",
        };
        for (int i = 0; i < 60 && sample.Length > 0; i++)
        {
            string name = sample[random.Next(sample.Length)];
            int cut = random.Next(1, Math.Min(name.Length, 5) + 1);
            string head = name[..cut];
            patterns.Add(head + "*");
            patterns.Add(head + "?");
            patterns.Add(head + "*" + name[^1]);
            patterns.Add(head + "?*");
            patterns.Add(head + "??");
        }

        using var qc = new QueryContext(snapshot.OrdinalCount);
        foreach (string pattern in patterns)
        {
            if (GlobMatcher.MatchesEveryDocument(pattern))
                continue;

            var scan = QueryMatcher.MatchGlob(pattern, qc, snapshot, useTextOrder: false);
            var ranged = QueryMatcher.MatchGlob(pattern, qc, snapshot, useTextOrder: true);
            for (int ordinal = 0; ordinal < snapshot.OrdinalCount; ordinal++)
            {
                if (scan.Get(ordinal) != ranged.Get(ordinal))
                    return $"pattern '{pattern}' ordinal {ordinal}: scan {scan.Get(ordinal)}, range {ranged.Get(ordinal)}";
            }
        }

        return null;
    }
}
