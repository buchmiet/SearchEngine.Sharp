using System.Collections;
using System.Collections.Concurrent;
using SearchEngine.Filters;

namespace SearchEngine.Sharp.Tests;

public class FacetValuesTests
{
    [Fact]
    public void FromDictionary_ReturnsNullWhenEmpty()
    {
        Assert.Null(FacetValues.FromDictionary(null));
        Assert.Null(FacetValues.FromDictionary(new Dictionary<string, long>()));
    }

    [Fact]
    public void FromDictionary_RejectsNullName()
    {
        var values = new PairList([new(null!, 1L), new("size", 2L)]);

        Assert.Throws<ArgumentException>(() => FacetValues.FromDictionary(values));
    }

    [Fact]
    public void FromDictionary_RejectsDuplicateNames()
    {
        var values = new PairList([new("size", 1L), new("size", 2L)]);

        Assert.Throws<ArgumentException>(() => FacetValues.FromDictionary(values));
    }

    [Fact]
    public void FromDictionary_RejectsNamesThatOnlyAReferenceComparerKeepsApart()
    {
        // Two distinct string instances with equal content are distinct keys for a reference comparer.
        var first = new string("size".AsSpan());
        var second = new string("size".AsSpan());
        var values = new Dictionary<string, long>(new ReferenceStringComparer()) { [first] = 1, [second] = 2 };

        Assert.Throws<ArgumentException>(() => FacetValues.FromDictionary(values));
    }

    [Fact]
    public void FromDictionary_KeepsEveryPair()
    {
        var facets = FacetValues.FromDictionary(new Dictionary<string, long> { ["size"] = 7, ["modified"] = 9, ["attrs"] = 3 });

        Assert.NotNull(facets);
        var seen = new Dictionary<string, long>();
        for (int i = 0; i < facets.Count; i++)
            seen[facets.NameAt(i)] = facets.ValueAt(i);
        Assert.Equal(new Dictionary<string, long> { ["size"] = 7, ["modified"] = 9, ["attrs"] = 3 }, seen);
    }

    [Fact]
    public void FromDictionary_WithMoreEntriesThanOnePoolBucket_KeepsEveryPair()
    {
        var source = new Dictionary<string, long>();
        for (int i = 0; i < 300; i++)
            source[$"facet{i}"] = i;

        var facets = FacetValues.FromDictionary(source);

        Assert.NotNull(facets);
        Assert.Equal(300, facets.Count);
        for (int i = 0; i < facets.Count; i++)
            Assert.Equal(source[facets.NameAt(i)], facets.ValueAt(i));
    }

    [Fact]
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

        try
        {
            for (int call = 0; call < 20_000; call++)
            {
                var facets = FacetValues.FromDictionary(source);
                Assert.NotNull(facets);
                var names = new HashSet<string>();
                for (int i = 0; i < facets.Count; i++)
                    Assert.True(names.Add(facets.NameAt(i) ?? throw new InvalidOperationException("null facet name")));
            }
        }
        finally
        {
            await stop.CancelAsync();
            await mutator;
        }
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
