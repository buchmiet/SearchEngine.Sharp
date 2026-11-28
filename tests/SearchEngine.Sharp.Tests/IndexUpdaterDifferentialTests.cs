using System.Text;
using SearchEngine.Filters;
using SearchEngine.Snapshots;

namespace SearchEngine.Sharp.Tests;

// After every change of a random sequence, the snapshot the updater published must answer every query like a
// snapshot built from scratch with the same entries (IndexSnapshotBuilder). Small words over a small alphabet make
// tokens, bigrams and natural sort keys collide; enough changes run for the updater to merge several times, once
// with merges on a background thread and once with merges at points the test chooses.
public class IndexUpdaterDifferentialTests
{
    private static readonly string[] Words = ["a", "b", "ab", "ba", "abc", "cab", "bca", "a1", "a2", "b10", "aab", "abab"];
    private static readonly string[] Joiners = [" ", "-", ".", "_"];

    private static readonly string[] Queries =
    [
        "a", "b", "ab", "ba", "abc", "cab", "a1", "b10", "zz",
        "*", "a*", "*b", "*a*", "a?", "?b*", "a*c", "ab*", "*.ab",
        "ab ba", "ab OR ba", "NOT ab", "ab NOT a1", "a1 OR b10 OR cab",
    ];

    private static readonly string[] FilteredQueries = ["a", "ab", "*", "*b", "ab OR ba", "NOT ab"];

    private static readonly string[] Regexes = ["^a", "b$", "^[ab]+$", "1"];

    private static readonly FacetFilter[] Filters =
    [
        FacetFilter.Range("size", 1, 3),
        FacetFilter.Range("size", 0, 0),
        FacetFilter.Mask("flag", 1),
        FacetFilter.Mask("flag", 0, 2),
        FacetFilter.Range("rare", 1, 10),
        FacetFilter.Combine(FacetFilter.Range("size", 0, 4), FacetFilter.Mask("flag", 0, 1)),
        FacetFilter.Range("missing", 0, 0),
        FacetFilter.Range("missing", 1, 1),
    ];

    private const int InitialCount = 400;

    // A small fan-out and segment limit make merges frequent. "manual" merges only where the test calls MergeNow, so
    // every layout between two merges is checked.
    public static IEnumerable<(string Preset, int Seed, string Merging)> Cases()
    {
        foreach (string preset in new[] { "Default", "FileMask", "DefaultCaseFold" })
        foreach (int seed in new[] { 1, 2 })
        foreach (string merging in new[] { "background", "manual" })
            yield return (preset, seed, merging);
    }

    private static MergePolicy PolicyFor(string merging)
        => new() { FanOut = 3, MaxSegments = 12, Background = merging == "background" };

    private static SearchTokenization Preset(string name) => name switch
    {
        "Default" => SearchTokenization.Default,
        "FileMask" => SearchTokenization.FileMask,
        _ => SearchTokenization.Default.WithUnicodeMatching(UnicodeMatching.CaseFold),
    };

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Every_published_snapshot_answers_like_a_full_build(string presetName, int seed, string merging)
    {
        var tokenization = Preset(presetName);
        var random = new Random(seed);
        var (engine, updater, provider) = TestIndex.Create(tokenization, PolicyFor(merging));
        var expected = new Dictionary<int, IndexedEntry>();
        int nextId = 0;
        // SnapshotOrder is specified only while the entries were added and never removed or replaced.
        bool orderSpecified = true;

        var initial = new Dictionary<int, IndexedEntry>();
        for (int i = 0; i < InitialCount; i++)
            initial[nextId++] = RandomEntry(random);
        updater.RebuildFrom(initial);
        foreach (var (id, entry) in initial)
            expected[id] = entry;

        string? mismatch = null;
        int merges = 0;
        for (int step = 0; step < 700 && mismatch is null; step++)
        {
            int segmentsBefore = updater.SegmentCount;
            string operation = ApplyRandomChange(random, updater, expected, ref nextId, ref orderSpecified);
            if (merging == "manual" && random.Next(6) == 0)
            {
                updater.MergeNow();
                operation += "+merge";
            }

            int segmentsAfter = updater.SegmentCount;
            // A change adds at most one segment, so fewer segments than before means a merge ran (or a rebuild).
            if (segmentsAfter < segmentsBefore && !operation.StartsWith("rebuild", StringComparison.Ordinal) && !operation.StartsWith("clear", StringComparison.Ordinal))
                merges++;

            var after = provider.Current;
            mismatch = CheckSegments(after, expected) ?? Compare(engine, after, expected, tokenization, orderSpecified);
            if (mismatch is not null)
                mismatch = $"step {step} ({operation}): {mismatch}";
        }

        if (merging == "background")
            updater.WaitForMerges();
        await Assert.That(updater.MergeFault).IsNull();
        mismatch ??= CheckSegments(provider.Current, expected) ?? Compare(engine, provider.Current, expected, tokenization, orderSpecified);
        await Assert.That(mismatch).IsNull();
        if (merging == "manual")
            await Assert.That(merges).IsPositive();
    }

    // The layout every snapshot keeps: segments start on multiples of 64 and do not overlap, a dead set lies inside its
    // segment and leaves a live document unless a merge still reads the segment, and the live documents carry
    // exactly the ids the test expects, each once.
    private static string? CheckSegments(IndexSnapshot snapshot, Dictionary<int, IndexedEntry> expected)
    {
        var live = new HashSet<int>();
        int end = 0;
        for (int s = 0; s < snapshot.Segments.Length; s++)
        {
            var segment = snapshot.Segments[s];
            if (snapshot.Offsets[s] % 64 != 0 || snapshot.Offsets[s] < end)
                return $"segment {s} starts at {snapshot.Offsets[s]} after {end}";
            end = snapshot.Offsets[s] + segment.DocumentCount;
            if (segment.DocumentCount == 0)
                return $"segment {s} is empty";

            var dead = snapshot.Dead[s];
            for (int local = 0; local < segment.DocumentCount; local++)
            {
                if (dead is not null && dead.Contains(local))
                    continue;
                if (!live.Add(segment.RecordIds[local]))
                    return $"id {segment.RecordIds[local]} is live twice";
            }
        }

        if (live.Count != expected.Count || !live.SetEquals(expected.Keys))
            return $"live ids differ: {live.Count} in the snapshot, {expected.Count} expected";
        if (snapshot.DocumentCount != expected.Count)
            return $"DocumentCount {snapshot.DocumentCount}, expected {expected.Count}";

        return null;
    }

    private static string ApplyRandomChange(
        Random random,
        IndexUpdater updater,
        Dictionary<int, IndexedEntry> expected,
        ref int nextId,
        ref bool orderSpecified)
    {
        int roll = random.Next(1000);
        if (roll < 300 || expected.Count == 0)
        {
            int id = nextId++;
            var entry = RandomEntry(random);
            updater.AddEntry(id, entry);
            expected[id] = entry;
            return $"add {id}";
        }

        if (roll < 520)
        {
            int id = ExistingId(random, expected);
            var entry = RandomEntry(random);
            updater.RefreshEntry(id, entry);
            expected[id] = entry;
            orderSpecified = false;
            return $"replace {id}";
        }

        if (roll < 720)
        {
            int id = ExistingId(random, expected);
            updater.RemoveEntry(id);
            expected.Remove(id);
            orderSpecified = false;
            return $"remove {id}";
        }

        if (roll < 740)
        {
            updater.RemoveEntry(nextId + 1000);
            return "remove a missing id";
        }

        if (roll < 880)
        {
            var batch = new List<KeyValuePair<int, IndexedEntry>>();
            int count = random.Next(1, 30);
            for (int i = 0; i < count; i++)
            {
                bool replace = expected.Count > 0 && random.Next(2) == 0;
                int id = replace ? ExistingId(random, expected) : nextId++;
                batch.Add(new(id, RandomEntry(random)));
                orderSpecified &= !replace && !expected.ContainsKey(id);
            }

            updater.AddOrUpdateEntries(batch);
            foreach (var (id, entry) in batch)
                expected[id] = entry;
            return $"batch of {count}";
        }

        if (roll < 994)
        {
            var ids = new List<int>();
            int count = random.Next(1, 20);
            for (int i = 0; i < count && expected.Count > 0; i++)
                ids.Add(random.Next(4) == 0 ? nextId + 2000 + i : ExistingId(random, expected));

            updater.RemoveEntries(ids);
            foreach (int id in ids)
                orderSpecified &= !expected.Remove(id);
            return $"remove {count} ids";
        }

        if (roll < 997)
        {
            var next = new Dictionary<int, IndexedEntry>();
            int count = random.Next(0, InitialCount);
            for (int i = 0; i < count; i++)
                next[nextId++] = RandomEntry(random);

            updater.RebuildFrom(next);
            expected.Clear();
            foreach (var (id, entry) in next)
                expected[id] = entry;
            orderSpecified = true;
            return $"rebuild with {count}";
        }

        updater.Clear();
        expected.Clear();
        orderSpecified = true;
        return "clear";
    }

    private static int ExistingId(Random random, Dictionary<int, IndexedEntry> entries)
        => entries.Keys.ElementAt(random.Next(entries.Count));

    private static IndexedEntry RandomEntry(Random random)
    {
        var text = new StringBuilder(Words[random.Next(Words.Length)]);
        int extra = random.Next(3);
        for (int i = 0; i < extra; i++)
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

    private static string? Compare(
        SearchEngineSharp actual,
        IndexSnapshot published,
        Dictionary<int, IndexedEntry> entries,
        SearchTokenization tokenization,
        bool orderSpecified)
    {
        var reference = new SearchEngineSharp(new FixedSnapshotProvider(IndexSnapshotBuilder.Build(entries, tokenization)));
        if (actual.DocumentCount != reference.DocumentCount)
            return $"DocumentCount {actual.DocumentCount}, expected {reference.DocumentCount}";
        if (actual.UniqueWordCount != reference.UniqueWordCount)
            return $"UniqueWordCount {actual.UniqueWordCount}, expected {reference.UniqueWordCount}";
        if (!ReferenceEquals(published.Tokenization, tokenization))
            return "the snapshot lost the preset";

        foreach (string query in Queries)
        foreach (var method in new[] { WordMatchMethod.Exact, WordMatchMethod.Within })
        foreach (bool operators in new[] { false, true })
        {
            if (CompareQuery(actual, reference, query, method, operators, filter: null, orderSpecified) is { } difference)
                return difference;
        }

        foreach (string regex in Regexes)
        {
            if (CompareQuery(actual, reference, regex, WordMatchMethod.Regex, false, filter: null, orderSpecified) is { } difference)
                return difference;
        }

        foreach (var filter in Filters)
        {
            if (CompareQuery(actual, reference, "", WordMatchMethod.Exact, false, filter, orderSpecified) is { } filterOnly)
                return filterOnly;

            foreach (string query in FilteredQueries)
            {
                if (CompareQuery(actual, reference, query, WordMatchMethod.Exact, true, filter, orderSpecified) is { } difference)
                    return difference;
            }
        }

        return null;
    }

    private static string? CompareQuery(
        SearchEngineSharp actual,
        SearchEngineSharp reference,
        string query,
        WordMatchMethod method,
        bool operators,
        FacetFilter? filter,
        bool orderSpecified)
    {
        string what = $"'{query}' {method} operators={operators} filter={(filter is null ? "none" : string.Join(",", filter.Predicates.ToArray().Select(p => p.Facet)))}";

        string snapshotOrder = Outcome(e => filter is null
            ? e.Find(query, method, operators, SearchSortMode.SnapshotOrder)
            : e.Find(query, method, operators, SearchSortMode.SnapshotOrder, filter), actual, reference, ordered: orderSpecified);
        if (snapshotOrder.Length > 0)
            return $"{what} SnapshotOrder: {snapshotOrder}";

        string natural = Outcome(e => filter is null
            ? e.Find(query, method, operators, SearchSortMode.NaturalSortAscending)
            : e.Find(query, method, operators, SearchSortMode.NaturalSortAscending, filter), actual, reference, ordered: true);
        if (natural.Length > 0)
            return $"{what} NaturalSortAscending: {natural}";

        string count = Outcome(e => [filter is null
            ? e.CountMatches(query, method, operators)
            : e.CountMatches(query, method, operators, filter)], actual, reference, ordered: true);
        if (count.Length > 0)
            return $"{what} CountMatches: {count}";

        return null;
    }

    // Empty when both engines give the same ids (in the same order when ordered) or throw the same exception type.
    private static string Outcome(Func<SearchEngineSharp, List<int>> query, SearchEngineSharp actual, SearchEngineSharp reference, bool ordered)
    {
        string got = Run(query, actual, ordered);
        string want = Run(query, reference, ordered);
        return got == want ? "" : $"got {got}, expected {want}";
    }

    private static string Run(Func<SearchEngineSharp, List<int>> query, SearchEngineSharp engine, bool ordered)
    {
        try
        {
            var ids = query(engine);
            return "[" + string.Join(",", ordered ? ids : ids.Order()) + "]";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }
}
