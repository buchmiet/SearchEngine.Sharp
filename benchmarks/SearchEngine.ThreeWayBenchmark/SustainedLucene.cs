using System.Diagnostics;
using Lucene.Net.Analysis;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Directory = Lucene.Net.Store.Directory;

namespace ThreeWay;

/// <summary>Lucene.NET with a near-real-time searcher: visibility without a commit, as Lucene intends; durable mode commits to disk.</summary>
public sealed class SustainedLucene : ILifecycle
{
    public string Name => "Lucene.NET 4.8";

    // Times every merge the scheduler runs: exact merge counts, documents and time.
    private sealed class TimedMergeScheduler : ConcurrentMergeScheduler
    {
        private long _merges, _docs, _ticks;
        private int _active;
        private readonly List<MergeEvent> _events = [];
        public bool Active => Volatile.Read(ref _active) > 0;
        public IReadOnlyList<MergeEvent> Events { get { lock (_events) return [.. _events]; } }
        public long Merges => Interlocked.Read(ref _merges);
        public long Docs => Interlocked.Read(ref _docs);
        public TimeSpan Time => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

        protected override void DoMerge(MergePolicy.OneMerge merge)
        {
            long began = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _active);
            try { base.DoMerge(merge); }
            finally { Interlocked.Decrement(ref _active); }
            lock (_events) _events.Add(new MergeEvent(began, Stopwatch.GetElapsedTime(began).TotalMilliseconds, merge.TotalDocCount));
            Interlocked.Increment(ref _merges);
            Interlocked.Add(ref _docs, merge.TotalDocCount);
            Interlocked.Add(ref _ticks, Stopwatch.GetElapsedTime(began).Ticks);
        }
    }

    private Directory _dir = null!;
    private IndexWriter _writer = null!;
    private SearcherManager _manager = null!;
    private readonly TimedMergeScheduler _scheduler = new();
    private readonly Analyzer _analyzer = new SepAnalyzer();
    private bool _durable;
    private Query[] _readerQueries = [];

    public void Build(string[] names, string? durableDir)
    {
        _durable = durableDir is not null;
        if (_durable)
        {
            System.IO.Directory.CreateDirectory(durableDir!);
            _dir = FSDirectory.Open(durableDir!);
        }
        else
            _dir = new RAMDirectory();

        var cfg = new IndexWriterConfig(LuceneVersion.LUCENE_48, _analyzer) { OpenMode = OpenMode.CREATE, RAMBufferSizeMB = 256, MergeScheduler = _scheduler };
        _writer = new IndexWriter(_dir, cfg);
        var doc = new Document();
        var idF = new StoredField("id", 0);
        var keyF = new StringField("idk", "", Field.Store.NO); // the indexed id, so that a document can be replaced or deleted by term
        var nameF = new TextField("name", "", Field.Store.YES);
        doc.Add(idF); doc.Add(keyF); doc.Add(nameF);
        for (int i = 0; i < names.Length; i++) { idF.SetInt32Value(i); keyF.SetStringValue(IdKey(i)); nameF.SetStringValue(names[i]); _writer.AddDocument(doc); }
        _writer.Commit();
        _manager = new SearcherManager(_writer, true, null);
        _readerQueries = [.. ReaderQueries.Cycle.Select(q => q.Lucene!())];
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    private static string IdKey(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static Document MakeDoc(int id, string text)
        => new() { new StoredField("id", id), new StringField("idk", IdKey(id), Field.Store.NO), new TextField("name", text, Field.Store.YES) };

    public long Apply(OpKind kind, int id, string? text, string probeToken)
    {
        switch (kind)
        {
            case OpKind.Add: _writer.AddDocument(MakeDoc(id, text!)); break;
            case OpKind.Update: _writer.UpdateDocument(new Term("idk", IdKey(id)), MakeDoc(id, text!)); break;
            default: _writer.DeleteDocuments(new Term("idk", IdKey(id))); break;
        }

        if (_durable)
            _writer.Commit();
        _manager.MaybeRefreshBlocking();
        var searcher = _manager.Acquire();
        try
        {
            var c = new TotalHitCountCollector();
            searcher.Search(new TermQuery(new Term("name", probeToken)), c);
            return c.TotalHits;
        }
        finally { _manager.Release(searcher); }
    }

    public long CountAnd(string[] tokens)
    {
        var q = new BooleanQuery();
        foreach (string t in tokens)
            q.Add(new TermQuery(new Term("name", t)), Occur.MUST);
        var searcher = _manager.Acquire();
        try
        {
            var c = new TotalHitCountCollector();
            searcher.Search(q, c);
            return c.TotalHits;
        }
        finally { _manager.Release(searcher); }
    }

    private readonly Dictionary<string, long> _segmentKeys = [];

    public LayoutDetail Detail()
    {
        var searcher = _manager.Acquire();
        try
        {
            long total = 0, largest = -1, largestDead = 0, dead = 0, key = 0;
            int ordinal = 0;
            foreach (var leaf in searcher.IndexReader.Leaves)
            {
                long n = leaf.Reader.MaxDoc, d = leaf.Reader.NumDeletedDocs;
                total += n; dead += d;
                if (n > largest)
                {
                    largest = n; largestDead = d;
                    string name = (leaf.Reader as SegmentReader)?.SegmentName ?? "#" + ordinal;
                    if (!_segmentKeys.TryGetValue(name, out key))
                        _segmentKeys[name] = key = _segmentKeys.Count + 1;
                }

                ordinal++;
            }

            return new(searcher.IndexReader.Leaves.Count, total - largest, dead, largest, largestDead, key);
        }
        finally { _manager.Release(searcher); }
    }

    public long AddVisible(int firstId, string[] names, string probeToken)
    {
        for (int i = 0; i < names.Length; i++)
            _writer.AddDocument(MakeDoc(firstId + i, names[i]));
        if (_durable)
            _writer.Commit();
        _manager.MaybeRefreshBlocking();
        var searcher = _manager.Acquire();
        try
        {
            var c = new TotalHitCountCollector();
            searcher.Search(new TermQuery(new Term("name", probeToken)), c);
            return c.TotalHits;
        }
        finally { _manager.Release(searcher); }
    }

    public (int Segments, long DeltaDocuments) Layout()
    {
        var searcher = _manager.Acquire();
        try
        {
            long total = 0, largest = 0;
            foreach (var leaf in searcher.IndexReader.Leaves)
            {
                long n = leaf.Reader.MaxDoc;
                total += n;
                largest = Math.Max(largest, n);
            }

            return (searcher.IndexReader.Leaves.Count, total - largest);
        }
        finally { _manager.Release(searcher); }
    }

    public long ReaderQuery(int which)
    {
        var searcher = _manager.Acquire();
        try
        {
            var c = new TotalHitCountCollector();
            searcher.Search(_readerQueries[which % _readerQueries.Length], c);
            return c.TotalHits;
        }
        finally { _manager.Release(searcher); }
    }

    private sealed class DocIdCollector : ICollector
    {
        public readonly List<int> Docs = [];
        private int _base;
        public void SetScorer(Scorer scorer) { }
        public void Collect(int doc) => Docs.Add(_base + doc);
        public void SetNextReader(AtomicReaderContext context) => _base = context.DocBase;
        public bool AcceptsDocsOutOfOrder => true;
    }

    public int[] FindIds(QuerySpec query)
    {
        var searcher = _manager.Acquire();
        try
        {
            var c = new DocIdCollector();
            searcher.Search(query.Lucene!(), c);
            // Document ids change with merges, so the stored id is read for every hit.
            int[] ids = new int[c.Docs.Count];
            for (int i = 0; i < ids.Length; i++)
                ids[i] = searcher.Doc(c.Docs[i]).GetField("id").GetInt32Value().GetValueOrDefault();
            Array.Sort(ids);
            return ids;
        }
        finally { _manager.Release(searcher); }
    }

    public long TopId(string token)
    {
        var searcher = _manager.Acquire();
        try
        {
            var top = searcher.Search(new TermQuery(new Term("name", token)), 1);
            return top.TotalHits == 0 ? -1 : searcher.Doc(top.ScoreDocs[0].Doc).GetField("id").GetInt32Value().GetValueOrDefault();
        }
        finally { _manager.Release(searcher); }
    }

    public void Settle() => _writer.WaitForMerges();

    public MergeReport Merges() => new("exact (merge scheduler hook)", _scheduler.Merges, _scheduler.Docs, _scheduler.Time.TotalSeconds, "background merge threads");

    public bool? MergeActive => _scheduler.Active;
    public IReadOnlyList<MergeEvent> MergeEvents => _scheduler.Events;

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _manager?.Dispose();
        _writer?.Dispose();
        _dir?.Dispose();
    }
}
