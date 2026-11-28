using System.Collections;
using System.Collections.Concurrent;
using SearchEngine.Filters;

namespace SearchEngine.Sharp.Tests;

// FacetValues is the facet bag of an entry (src/Filters/FacetValues.cs). FromDictionary and Create reject null and
// duplicate names, compared ordinally whatever the comparer of the source, and FromDictionary reads its source once, so
// a dictionary that another thread changes during the call still gives a valid bag. A broken bag would make the next
// snapshot build throw for the whole index.
public class FacetValuesTests
{
    [Test]
    public async Task A_null_or_empty_dictionary_gives_no_bag()
    {
        await Assert.That(FacetValues.FromDictionary(null)).IsNull();
        await Assert.That(FacetValues.FromDictionary(new Dictionary<string, long>())).IsNull();
    }

    [Test]
    public async Task A_null_name_is_rejected()
    {
        var values = new PairList([new(null!, 1L), new("size", 2L)]);

        await Assert.That(() => FacetValues.FromDictionary(values)).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task A_duplicate_name_is_rejected()
    {
        var values = new PairList([new("size", 1L), new("size", 2L)]);

        await Assert.That(() => FacetValues.FromDictionary(values)).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task Names_that_only_a_reference_comparer_keeps_apart_are_duplicates()
    {
        // Two distinct string instances with equal content are distinct keys for a reference comparer.
        var first = new string("size".AsSpan());
        var second = new string("size".AsSpan());
        var values = new Dictionary<string, long>(new ReferenceStringComparer()) { [first] = 1, [second] = 2 };

        await Assert.That(() => FacetValues.FromDictionary(values)).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task Every_pair_of_the_dictionary_is_kept()
    {
        var facets = FacetValues.FromDictionary(new Dictionary<string, long> { ["size"] = 7, ["modified"] = 9, ["attrs"] = 3 });

        await Assert.That(facets).IsNotNull();
        var seen = new Dictionary<string, long>();
        for (int i = 0; i < facets!.Count; i++)
            seen[facets.NameAt(i)] = facets.ValueAt(i);
        await Assert.That(seen).IsEquivalentTo(new Dictionary<string, long> { ["size"] = 7, ["modified"] = 9, ["attrs"] = 3 });
    }

    [Test]
    public async Task Every_pair_is_kept_beyond_one_pool_bucket()
    {
        var source = new Dictionary<string, long>();
        for (int i = 0; i < 300; i++)
            source[$"facet{i}"] = i;

        var facets = FacetValues.FromDictionary(source);

        await Assert.That(facets).IsNotNull();
        await Assert.That(facets!.Count).IsEqualTo(300);
        for (int i = 0; i < facets.Count; i++)
            await Assert.That(facets.ValueAt(i)).IsEqualTo(source[facets.NameAt(i)]);
    }

    [Test]
    public async Task A_dictionary_changed_during_the_call_gives_a_valid_bag()
    {
        var source = new ConcurrentDictionary<string, long>();
        for (int i = 0; i < 4; i++)
            source[$"base{i}"] = i;

        using var stop = new CancellationTokenSource();
        var mutator = Task.Run(() =>
        {
            int n = 0;
            while (!stop.IsCancellationRequested)
            {
                source[$"extra{n % 64}"] = n;
                source.TryRemove($"extra{(n + 32) % 64}", out _);
                n++;
            }
        });

        // Counted here and asserted once after the mutator stops: an awaited assertion per name would slow the
        // loop that races the mutator.
        int nullBags = 0;
        int duplicateNames = 0;
        try
        {
            for (int call = 0; call < 20_000; call++)
            {
                var facets = FacetValues.FromDictionary(source);
                if (facets is null)
                {
                    nullBags++;
                    continue;
                }

                var names = new HashSet<string>();
                for (int i = 0; i < facets.Count; i++)
                {
                    if (!names.Add(facets.NameAt(i) ?? throw new InvalidOperationException("null facet name")))
                        duplicateNames++;
                }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await mutator;
        }

        await Assert.That(nullBags).IsEqualTo(0);
        await Assert.That(duplicateNames).IsEqualTo(0);
    }

    // The count can change between two reads (a concurrent dictionary whose only key is removed and added again):
    // only the enumeration decides what is copied.
    [Test]
    public async Task The_pairs_are_copied_when_the_count_drops_to_zero_after_the_first_read()
    {
        var values = new PairList([new("size", 7L)], new Queue<int>([1, 0]));

        var facets = FacetValues.FromDictionary(values);

        await Assert.That(facets).IsNotNull();
        await Assert.That(facets!.Count).IsEqualTo(1);
        await Assert.That(facets.NameAt(0)).IsEqualTo("size");
        await Assert.That(facets.ValueAt(0)).IsEqualTo(7L);
    }

    [Test]
    public async Task The_bag_never_holds_a_wrong_name_while_the_only_key_is_removed_and_added_again()
    {
        var source = new ConcurrentDictionary<string, long>();
        using var stop = new CancellationTokenSource();
        var mutator = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                source["size"] = 1;
                source.TryRemove("size", out _);
            }
        });

        // Counted here and asserted once after the mutator stops, as in the dictionary test above.
        int wrongNames = 0;
        try
        {
            for (int call = 0; call < 200_000; call++)
            {
                var facets = FacetValues.FromDictionary(source);
                if (facets is not null && facets.NameAt(0) != "size")
                    wrongNames++;
            }
        }
        finally
        {
            await stop.CancelAsync();
            await mutator;
        }

        await Assert.That(wrongNames).IsEqualTo(0);
    }

    [Test]
    public async Task A_bag_from_pairs_filters_like_a_bag_from_a_dictionary()
    {
        var (engine, updater, _) = TestIndex.Create();
        updater.RebuildFrom(new Dictionary<int, IndexedEntry>
        {
            [1] = new("a.pdf", "a.pdf", FacetValues.Create([new("size", 10L), new("attrs", 0x1L)])),
            [2] = new("b.pdf", "b.pdf", FacetValues.FromDictionary(new Dictionary<string, long> { ["size"] = 20, ["attrs"] = 0x2 })),
            [3] = new("c.pdf", "c.pdf", FacetValues.Create("size", 30)),
        });

        var small = engine.Find("pdf", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, FacetFilter.Range("size", 0, 25));
        var flagged = engine.Find("pdf", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder, FacetFilter.Mask("attrs", 0x2));

        await Assert.That(small).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
        await Assert.That(flagged).IsEquivalentTo([2], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Pairs_with_a_duplicate_name_are_rejected_and_no_pairs_give_the_empty_bag()
    {
        await Assert.That(() => FacetValues.Create([new("size", 1L), new("size", 2L)])).ThrowsExactly<ArgumentException>();
        await Assert.That(FacetValues.Create(ReadOnlySpan<KeyValuePair<string, long>>.Empty)).IsSameReferenceAs(FacetValues.Empty);
    }

    private sealed class ReferenceStringComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => ReferenceEquals(x, y);

        public int GetHashCode(string obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    // Minimal read-only dictionary that can hold what a Dictionary cannot: a null key.
    // countReads, when given, answers Count instead of the pairs: a concurrent dictionary can report a different
    // count on every read.
    private sealed class PairList(KeyValuePair<string, long>[] pairs, Queue<int>? countReads = null) : IReadOnlyDictionary<string, long>
    {
        public int Count => countReads is { Count: > 0 } ? countReads.Dequeue() : pairs.Length;

        public IEnumerable<string> Keys => pairs.Select(p => p.Key);

        public IEnumerable<long> Values => pairs.Select(p => p.Value);

        public long this[string key] => pairs.First(p => p.Key == key).Value;

        public bool ContainsKey(string key) => pairs.Any(p => p.Key == key);

        public bool TryGetValue(string key, out long value)
        {
            foreach (var p in pairs)
            {
                if (p.Key == key)
                {
                    value = p.Value;
                    return true;
                }
            }

            value = 0;
            return false;
        }

        public IEnumerator<KeyValuePair<string, long>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, long>>)pairs).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
