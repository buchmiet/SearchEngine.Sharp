using System.Buffers;
using System.Text.Json;
using TantivyDotNet;

namespace ThreeWay;

/// <summary>
/// TantivyDotNet with one writer that stays open for the whole run (the realistic use; the earlier one-shot benchmark
/// created a writer per call). Visibility needs a commit and a searcher refresh; commits go to a RAM index unless durable.
/// </summary>
public sealed class SustainedTantivy : ILifecycle
{
    public string Name => "TantivyDotNet 1.0.0-rc.2";

    private SearchIndex _index = null!;
    private TantivyDotNet.IndexWriter _writer = null!;
    private SearcherManager _manager = null!;
    private readonly ArrayBufferWriter<byte> _buffer = new(256);
    private Utf8JsonWriter? _json;
    private TantivyDotNet.Query[] _readerQueries = [];
    private int _lastSegments = -1;
    private long _segmentDrops;

    private static Schema MakeSchema() => new SchemaBuilder()
        .AddAnalyzer("sep", Analyzer.Regex(Sep.TantivyRegex).LowerCase())
        .AddU64Field("id", stored: true, indexed: true, fast: true) // indexed, so that a document can be replaced or deleted by term
        .AddTextField("name", stored: true, tokenizer: "sep")
        .Build();

    private void Write(int id, string name)
    {
        _buffer.Clear();
        _json!.Reset(_buffer);
        _json.WriteStartObject();
        _json.WriteNumber("id", (ulong)id);
        _json.WriteString("name", name);
        _json.WriteEndObject();
        _json.Flush();
    }

    private void Add(int id, string name)
    {
        Write(id, name);
        _writer.AddDocument(_buffer.WrittenSpan);
    }

    public void Build(string[] names, string? durableDir)
    {
        _json = new Utf8JsonWriter(_buffer);
        if (durableDir is null)
            _index = SearchIndex.CreateInRam(MakeSchema());
        else
        {
            System.IO.Directory.CreateDirectory(durableDir);
            _index = SearchIndex.Create(durableDir, MakeSchema());
        }

        _writer = _index.CreateWriter(new IndexWriterOptions { HeapBytes = 256_000_000, ThreadCount = 1 });
        for (int i = 0; i < names.Length; i++)
            Add(i, names[i]);
        _writer.Commit();
        // A large heap pays off for the bulk build and costs every small commit afterwards (3 to 4 times the latency at
        // 15 MB against 50 MB or more, measured). Dispose the bulk writer, which waits for its merges, and continue with
        // a writer sized for one-document commits.
        _writer.Dispose();
        _writer = _index.CreateWriter(new IndexWriterOptions
        {
            HeapBytes = long.Parse(Environment.GetEnvironmentVariable("TANTIVY_STEADY_HEAP") ?? "50000000"),
            ThreadCount = int.Parse(Environment.GetEnvironmentVariable("TANTIVY_STEADY_THREADS") ?? "1"),
        });
        _manager = _index.CreateSearcherManager(new SearcherManagerOptions { RefreshOnCommit = false });
        _readerQueries = [.. ReaderQueries.Cycle.Select(q => q.Tantivy!())];
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    public long AddVisible(int firstId, string[] names, string probeToken)
    {
        for (int i = 0; i < names.Length; i++)
            Add(firstId + i, names[i]);
        _writer.Commit();
        _manager.Refresh();
        using var lease = _manager.Acquire();
        return lease.Searcher.Count(TantivyDotNet.Query.Term("name", probeToken));
    }

    public long Apply(OpKind kind, int id, string? text, string probeToken)
    {
        switch (kind)
        {
            case OpKind.Add: Add(id, text!); break;
            case OpKind.Update:
                Write(id, text!);
                _writer.UpdateDocument(TantivyDotNet.Query.Term("id", (ulong)id), _buffer.WrittenSpan);
                break;
            default: _writer.DeleteTerm("id", (ulong)id); break;
        }

        _writer.Commit();
        _manager.Refresh();
        using var lease = _manager.Acquire();
        return lease.Searcher.Count(TantivyDotNet.Query.Term("name", probeToken));
    }

    public long CountAnd(string[] tokens)
    {
        using var lease = _manager.Acquire();
        TantivyDotNet.Query q = TantivyDotNet.Query.Term("name", tokens[0]);
        for (int i = 1; i < tokens.Length; i++)
            q = q & TantivyDotNet.Query.Term("name", tokens[i]);
        return lease.Searcher.Count(q);
    }

    private readonly Dictionary<string, long> _segmentKeys = [];

    // Document counts are physical (live + deleted), as in the other engines.
    public LayoutDetail Detail()
    {
        var stats = _index.GetStats();
        long total = 0, largest = -1, largestDead = 0, dead = 0, key = 0;
        foreach (var s in stats.Segments)
        {
            long n = s.DocumentCount + s.DeletedDocumentCount;
            total += n; dead += s.DeletedDocumentCount;
            if (n > largest)
            {
                largest = n; largestDead = s.DeletedDocumentCount;
                string id = s.Id.ToString()!;
                if (!_segmentKeys.TryGetValue(id, out key))
                    _segmentKeys[id] = key = _segmentKeys.Count + 1;
            }
        }

        int count = stats.SegmentCount;
        if (_lastSegments >= 0 && count < _lastSegments)
            _segmentDrops++;
        _lastSegments = count;
        return new(count, total - largest, dead, largest, largestDead, key);
    }

    public (int Segments, long DeltaDocuments) Layout()
    {
        var stats = _index.GetStats();
        long total = 0, largest = 0;
        foreach (var s in stats.Segments)
        {
            total += s.DocumentCount;
            largest = Math.Max(largest, s.DocumentCount);
        }

        int count = stats.SegmentCount;
        if (_lastSegments >= 0 && count < _lastSegments)
            _segmentDrops++;
        _lastSegments = count;
        return (count, total - largest);
    }

    public long ReaderQuery(int which)
    {
        using var lease = _manager.Acquire();
        return lease.Searcher.Count(_readerQueries[which % _readerQueries.Length]);
    }

    public int[] FindIds(QuerySpec query)
    {
        using var lease = _manager.Acquire();
        var q = query.Tantivy!();
        int n = (int)lease.Searcher.Count(q);
        if (n == 0)
            return [];
        var r = lease.Searcher.Search(q, new SearchOptions { Limit = n, Fields = ["id"] });
        int[] ids = new int[r.Count];
        int k = 0;
        foreach (var h in r)
            ids[k++] = (int)(h.Document.GetU64("id") ?? 0);
        Array.Sort(ids);
        return ids;
    }

    public long TopId(string token)
    {
        using var lease = _manager.Acquire();
        var r = lease.Searcher.Search(TantivyDotNet.Query.Term("name", token), new SearchOptions { Limit = 1, Fields = ["id"] });
        return r.Count == 0 ? -1 : (long)(r[0].Document.GetU64("id") ?? 0);
    }

    public void Settle() => Thread.Sleep(300); // Tantivy merges on its own threads and has no wait API

    public MergeReport Merges()
        => new("inferred", _segmentDrops, 0, 0, "Tantivy exposes no merge hook: this counts the samples at which the segment count fell, a lower bound");

    public bool? MergeActive => null;
    public IReadOnlyList<MergeEvent> MergeEvents => [];

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _manager?.Dispose();
        _writer?.Dispose();
        _json?.Dispose();
        _index?.Dispose();
    }
}
