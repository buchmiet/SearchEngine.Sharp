using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using TantivyDotNet;

namespace ThreeWay;

// ---------------------------------------------------------------- TantivyDotNet
public sealed class TantivyEngine : IEngine
{
    public string Name => "TantivyDotNet 1.0.0-rc.2";
    SearchIndex _index = null!;
    Searcher _searcher = null!;

    static Schema MakeSchema() => new SchemaBuilder()
        .AddAnalyzer("sep", Analyzer.Regex(Sep.TantivyRegex).LowerCase())
        .AddU64Field("id", stored: true, indexed: false, fast: true)
        .AddTextField("name", stored: true, tokenizer: "sep")
        .Build();

    static void Fill(SearchIndex idx, string[] names, IndexWriterOptions opts, bool forceMerge)
    {
        using var w = idx.CreateWriter(opts);
        var buf = new ArrayBufferWriter<byte>(256);
        using var jw = new Utf8JsonWriter(buf);
        for (int i = 0; i < names.Length; i++)
        {
            buf.Clear(); jw.Reset(buf);
            jw.WriteStartObject(); jw.WriteNumber("id", (ulong)i); jw.WriteString("name", names[i]); jw.WriteEndObject(); jw.Flush();
            w.AddDocument(buf.WrittenSpan);
        }
        w.Commit();
        if (forceMerge) w.ForceMerge(1);
    }

    public void Build(string[] names)
    {
        _searcher?.Dispose(); _index?.Dispose();
        _index = SearchIndex.CreateInRam(MakeSchema());
        Fill(_index, names, new IndexWriterOptions { HeapBytes = 256_000_000, ThreadCount = 1 }, forceMerge: false);
        _searcher = _index.CreateSearcher();
    }
    public void ForceMerge()
    {
        using (var w = _index.CreateWriter(new IndexWriterOptions { HeapBytes = 256_000_000, ThreadCount = 1 })) { w.ForceMerge(1); }
        _searcher.Dispose(); _searcher = _index.CreateSearcher();
    }

    public bool Supports(QuerySpec q) => q.Tantivy is not null;
    readonly Dictionary<string, TantivyDotNet.Query> _qc = new();
    TantivyDotNet.Query Q(QuerySpec q) { if (!_qc.TryGetValue(q.Id, out var x)) _qc[q.Id] = x = q.Tantivy!(); return x; }

    public long Count(QuerySpec q) => _searcher.Count(Q(q));
    public long Top10(QuerySpec q)
    {
        var r = _searcher.Search(Q(q), new SearchOptions { Limit = 10, Fields = ["id"] });
        long sum = r.TotalCount;
        foreach (var h in r) sum += (long)(h.Document.GetU64("id") ?? 0);
        return sum;
    }
    // Two ways to get every id out of Tantivy: read the stored field of each hit (document-store reads, JSON over the
    // FFI), or ask for no stored fields and sort by the fast "id" column, which returns the ids as sort values.
    // The harness reports the faster of the two as "all" and the stored-field variant as "all_stored".
    public long? AllAlt(QuerySpec q) => AllStored(q);
    public long AllStored(QuerySpec q)
    {
        int n = (int)_searcher.Count(Q(q));
        if (n == 0) return 0;
        var r = _searcher.Search(Q(q), new SearchOptions { Limit = n, Fields = ["id"] });
        long sum = r.Count;
        foreach (var h in r) sum += (long)(h.Document.GetU64("id") ?? 0);
        return sum;
    }
    public long All(QuerySpec q)
    {
        int n = (int)_searcher.Count(Q(q));
        if (n == 0) return 0;
        var r = _searcher.Search(Q(q), new SearchOptions { Limit = n, Fields = [], OrderBy = [SortBy.Field("id")] });
        long sum = r.Count;
        foreach (var h in r) sum += Convert.ToInt64(h.SortValues[0]);
        return sum;
    }
    public long AddOneAndSearch(int id, string name, string tok)
    {
        using (var w = _index.CreateWriter(new IndexWriterOptions { HeapBytes = 50_000_000, ThreadCount = 1 }))
        {
            w.AddDocument(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["id"] = (ulong)id, ["name"] = name }));
            w.Commit();
        }
        _searcher.Dispose(); _searcher = _index.CreateSearcher();
        return _searcher.Count(TantivyDotNet.Query.Term("name", tok));
    }
    public (double, long, double)? DiskBuild(string[] names, string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var sw = Stopwatch.StartNew();
        using (var idx = SearchIndex.Create(dir, MakeSchema()))
            Fill(idx, names, new IndexWriterOptions { HeapBytes = 256_000_000, ThreadCount = 1 }, forceMerge: false);
        double build = sw.Elapsed.TotalSeconds;
        long bytes = Proc.DirBytes(dir);
        sw.Restart();
        using (var idx = SearchIndex.Open(dir)) using (var s = idx.CreateSearcher()) { s.Count(TantivyDotNet.Query.Term("name", "report")); }
        return (build, bytes, sw.Elapsed.TotalSeconds);
    }
    public void Dispose() { _searcher?.Dispose(); _index?.Dispose(); }
}
