using BenchmarkDotNet.Attributes;
using SearchEngine;
using SearchEngine.Sharp.Benchmarks;
using SearchEngine.Tokenizer;

namespace SearchEngine.Sharp.MicroBenchmarks;

[MemoryDiagnoser]
[WarmupCount(1)]
[IterationCount(3)]
public class SnapshotBuildAllocationBenchmark
{
    private Dictionary<int, IndexedEntry> _entries100k = null!;
    private Dictionary<int, IndexedEntry> _entries250k = null!;
    private List<string> _rawTokenSpans = null!;

    [GlobalSetup]
    public void Setup()
    {
        _entries100k = FileSearchDataFactory.ToIndexedEntries(
            FileSearchDataFactory.Create(100_000, seed: 2026).Documents);
        _entries250k = FileSearchDataFactory.ToIndexedEntries(
            FileSearchDataFactory.Create(250_000, seed: 2026).Documents);
        _rawTokenSpans = BuildRawUniqueTokenSpans(_entries100k);
    }

    [Benchmark(Baseline = true, Description = "Full rebuild 100k — current WordStringPool path")]
    public int Rebuild_Current_100k()
    {
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(_entries100k);
        return provider.Current.DocumentCount;
    }

    [Benchmark(Description = "Full rebuild 250k — current WordStringPool path")]
    public int Rebuild_Current_250k()
    {
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(_entries250k);
        return provider.Current.DocumentCount;
    }

    [Benchmark(Description = "Token canonicalization loop — current CreateNormalizedWord then pool")]
    public int Canonicalize_CurrentPool()
    {
        var pool = new WordStringPoolProbe();
        int canonicalized = 0;
        foreach (string rawToken in _rawTokenSpans)
        {
            string normalized = TextNormalizer.CreateNormalizedWord(rawToken);
            _ = pool.CanonicalizeCurrent(normalized);
            canonicalized++;
        }

        return canonicalized;
    }

    [Benchmark(Description = "Token canonicalization loop — span lookup, allocate only if new")]
    public int Canonicalize_SpanAwarePool()
    {
        var pool = new SpanAwareWordStringPoolProbe();
        int canonicalized = 0;
        foreach (string rawToken in _rawTokenSpans)
        {
            _ = pool.Canonicalize(rawToken.AsSpan());
            canonicalized++;
        }

        return canonicalized;
    }

    private static List<string> BuildRawUniqueTokenSpans(Dictionary<int, IndexedEntry> entries)
    {
        var spans = new List<string>(entries.Count * 4);
        var uniquePerDocument = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var separators = SearchTokenization.Default.IndexSeparatorValues;

        foreach (var entry in entries.Values)
        {
            uniquePerDocument.Clear();
            CollectUniqueRawTokens(entry.SearchText, separators, uniquePerDocument, spans);
        }

        return spans;
    }

    private static void CollectUniqueRawTokens(
        string text,
        System.Buffers.SearchValues<char> separators,
        HashSet<string> uniquePerDocument,
        List<string> output)
    {
        if (string.IsNullOrEmpty(text))
            return;

        ReadOnlySpan<char> span = text.AsSpan();
        int wordStart = -1;

        for (int i = 0; i < span.Length; i++)
        {
            if (separators.Contains(span[i]))
            {
                AddRawWord(span, wordStart, i, uniquePerDocument, output);
                wordStart = -1;
            }
            else if (wordStart < 0)
            {
                wordStart = i;
            }
        }

        AddRawWord(span, wordStart, span.Length, uniquePerDocument, output);
    }

    private static void AddRawWord(
        ReadOnlySpan<char> text,
        int wordStart,
        int wordEnd,
        HashSet<string> uniquePerDocument,
        List<string> output)
    {
        if (wordStart < 0)
            return;

        string raw = text[wordStart..wordEnd].ToString();
        if (uniquePerDocument.Add(raw))
            output.Add(raw);
    }
}

internal sealed class WordStringPoolProbe
{
    private readonly Dictionary<string, string> _pool = new(StringComparer.Ordinal);

    public string CanonicalizeCurrent(string word)
    {
        if (_pool.TryGetValue(word, out var existing))
            return existing;

        _pool[word] = word;
        return word;
    }
}

internal sealed class SpanAwareWordStringPoolProbe
{
    private readonly Dictionary<string, string> _pool = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

    public SpanAwareWordStringPoolProbe()
        => _lookup = _pool.GetAlternateLookup<ReadOnlySpan<char>>();

    public string Canonicalize(ReadOnlySpan<char> wordSpan)
    {
        if (_lookup.TryGetValue(wordSpan, out var existing))
            return existing;

        string word = TextNormalizer.CreateNormalizedWord(wordSpan);
        _pool[word] = word;
        return word;
    }
}
