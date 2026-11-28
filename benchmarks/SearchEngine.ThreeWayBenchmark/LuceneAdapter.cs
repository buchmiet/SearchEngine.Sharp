using System.Diagnostics;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.Core;
using Lucene.Net.Analysis.Util;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Directory = Lucene.Net.Store.Directory;

namespace ThreeWay;

// ---------------------------------------------------------------- Lucene.NET
public sealed class SepAnalyzer : Analyzer
{
    sealed class Tok(LuceneVersion v, TextReader r) : CharTokenizer(v, r) { protected override bool IsTokenChar(int c) => !Sep.IsSep(c); }
    protected override TokenStreamComponents CreateComponents(string fieldName, TextReader reader)
    {
        var t = new Tok(LuceneVersion.LUCENE_48, reader);
        return new TokenStreamComponents(t, new LowerCaseFilter(LuceneVersion.LUCENE_48, t));
    }
}

public sealed class LuceneEngine : IEngine
{
    public string Name => "Lucene.NET 4.8";
    Directory _dir = null!;
    IndexWriter? _writer;
    DirectoryReader? _reader;
    IndexSearcher _searcher = null!;
    readonly Analyzer _an = new SepAnalyzer();

    sealed class IdCollector : ICollector
    {
        public readonly List<int> Ids = new();
        int _base;
        public void SetScorer(Scorer scorer) { }
        public void Collect(int doc) => Ids.Add(_base + doc);
        public void SetNextReader(AtomicReaderContext c) => _base = c.DocBase;
        public bool AcceptsDocsOutOfOrder => true;
    }

    IndexWriter MakeWriter(Directory d, OpenMode mode)
    {
        var cfg = new IndexWriterConfig(LuceneVersion.LUCENE_48, _an) { OpenMode = mode, RAMBufferSizeMB = 256 };
        return new IndexWriter(d, cfg);
    }

    long Fill(IndexWriter w, string[] names)
    {
        var doc = new Document();
        var idF = new StoredField("id", 0);
        var nameF = new TextField("name", "", Field.Store.YES);
        doc.Add(idF); doc.Add(nameF);
        for (int i = 0; i < names.Length; i++) { idF.SetInt32Value(i); nameF.SetStringValue(names[i]); w.AddDocument(doc); }
        w.Commit();
        return names.Length;
    }

    public void Build(string[] names)
    {
        _reader?.Dispose(); _writer?.Dispose(); _dir?.Dispose();
        _dir = new RAMDirectory();
        _writer = MakeWriter(_dir, OpenMode.CREATE);
        Fill(_writer, names);
        _reader = DirectoryReader.Open(_writer, true);
        _searcher = new IndexSearcher(_reader);
    }

    public void ForceMerge()
    {
        _writer!.ForceMerge(1); _writer.Commit();
        _reader?.Dispose();
        _reader = DirectoryReader.Open(_writer, true);
        _searcher = new IndexSearcher(_reader);
    }

    public bool Supports(QuerySpec q) => q.Lucene is not null;
    readonly Dictionary<string, Query> _qc = new();
    Query Q(QuerySpec q) { if (!_qc.TryGetValue(q.Id, out var x)) _qc[q.Id] = x = q.Lucene!(); return x; }

    public long Count(QuerySpec q) { var c = new TotalHitCountCollector(); _searcher.Search(Q(q), c); return c.TotalHits; }
    public long All(QuerySpec q)
    {
        var c = new IdCollector(); _searcher.Search(Q(q), c);
        long sum = c.Ids.Count; foreach (var i in c.Ids) sum += i; return sum;
    }
    public long Top10(QuerySpec q)
    {
        var td = _searcher.Search(Q(q), 10);
        long sum = td.TotalHits;
        foreach (var sd in td.ScoreDocs) sum += _searcher.Doc(sd.Doc).GetField("id").GetInt32Value().GetValueOrDefault();
        return sum;
    }
    public long AddOneAndSearch(int id, string name, string tok)
    {
        var doc = new Document { new StoredField("id", id), new TextField("name", name, Field.Store.YES) };
        _writer!.AddDocument(doc);
        var nr = DirectoryReader.OpenIfChanged(_reader!, _writer, true);
        if (nr is not null) { _reader!.Dispose(); _reader = nr; _searcher = new IndexSearcher(_reader); }
        var c = new TotalHitCountCollector();
        _searcher.Search(new TermQuery(new Term("name", tok)), c);
        return c.TotalHits;
    }
    public (double, long, double)? DiskBuild(string[] names, string path)
    {
        System.IO.Directory.CreateDirectory(path);
        var sw = Stopwatch.StartNew();
        using (var d = FSDirectory.Open(path))
        using (var w = MakeWriter(d, OpenMode.CREATE)) { Fill(w, names); }
        double build = sw.Elapsed.TotalSeconds;
        long bytes = Proc.DirBytes(path);
        sw.Restart();
        using (var d = FSDirectory.Open(path)) using (var r = DirectoryReader.Open(d)) { new IndexSearcher(r).Search(new TermQuery(new Term("name", "report")), 10); }
        return (build, bytes, sw.Elapsed.TotalSeconds);
    }
    public void Dispose() { _reader?.Dispose(); _writer?.Dispose(); _dir?.Dispose(); }
}

