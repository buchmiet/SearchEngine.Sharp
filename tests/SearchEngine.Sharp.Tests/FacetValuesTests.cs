using System.Collections;
using System.Collections.Concurrent;
using SearchEngine.Filters;

namespace SearchEngine.Sharp.Tests;

public class FacetValuesTests
{
    [Test]
    public async Task FromDictionary_ReturnsNullWhenEmpty()
    {
        await Assert.That(FacetValues.FromDictionary(null)).IsNull();
        await Assert.That(FacetValues.FromDictionary(new Dictionary<string, long>())).IsNull();
    }

    [Test]
    public async Task FromDictionary_RejectsNullName()
    {
        var values = new PairList([new(null!, 1L), new("size", 2L)]);

        await Assert.That(() => FacetValues.FromDictionary(values)).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task FromDictionary_RejectsDuplicateNames()
    {
        var values = new PairList([new("size", 1L), new("size", 2L)]);

        await Assert.That(() => FacetValues.FromDictionary(values)).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task FromDictionary_RejectsNamesThatOnlyAReferenceComparerKeepsApart()
    {
        // Two distinct string instances with equal content are distinct keys for a reference comparer.
        var first = new string("size".AsSpan());
        var second = new string("size".AsSpan());
        var values = new Dictionary<string, long>(new ReferenceStringComparer()) { [first] = 1, [second] = 2 };

        await Assert.That(() => FacetValues.FromDictionary(values)).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task FromDictionary_KeepsEveryPair()
    {
        var facets = FacetValues.FromDictionary(new Dictionary<string, long> { ["size"] = 7, ["modified"] = 9, ["attrs"] = 3 });

        await Assert.That(facets).IsNotNull();
        var seen = new Dictionary<string, long>();
        for (int i = 0; i < facets!.Count; i++)
            seen[facets.NameAt(i)] = facets.ValueAt(i);
        await Assert.That(seen).IsEquivalentTo(new Dictionary<string, long> { ["size"] = 7, ["modified"] = 9, ["attrs"] = 3 });
    }

    [Test]
    public async Task FromDictionary_WithMoreEntriesThanOnePoolBucket_KeepsEveryPair()
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
    public async Task FromDictionary_IsSafeWhileTheSourceIsMutated()
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

    private sealed class ReferenceStringComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => ReferenceEquals(x, y);

        public int GetHashCode(string obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    // Minimal read-only dictionary that can hold what a Dictionary cannot: a null key.
    private sealed class PairList(KeyValuePair<string, long>[] pairs) : IReadOnlyDictionary<string, long>
    {
        public int Count => pairs.Length;

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
