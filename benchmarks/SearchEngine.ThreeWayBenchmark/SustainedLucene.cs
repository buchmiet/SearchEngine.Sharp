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
public sealed class SustainedLucene : ISustained
{
    public string Name => "Lucene.NET 4.8";

    // Times every merge the scheduler runs: exact merge counts, documents and time.
    private sealed class TimedMergeScheduler : ConcurrentMergeScheduler
    {
        private long _merges, _docs, _ticks;
        public long Merges => Interlocked.Read(ref _merges);
        public long Docs => Interlocked.Read(ref _docs);
        public TimeSpan Time => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

        protected override void DoMerge(MergePolicy.OneMerge merge)
        {
            long began = Stopwatch.GetTimestamp();
            base.DoMerge(merge);
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
        var nameF = new TextField("name", "", Field.Store.YES);
        doc.Add(idF); doc.Add(nameF);
        for (int i = 0; i < names.Length; i++) { idF.SetInt32Value(i); nameF.SetStringValue(names[i]); _writer.AddDocument(doc); }
        _writer.Commit();
        _manager = new SearcherManager(_writer, true, null);
        _readerQueries = [.. ReaderQueries.Cycle.Select(q => q.Lucene!())];
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    public long AddVisible(int firstId, string[] names, string probeToken)
    {
        for (int i = 0; i < names.Length; i++)
            _writer.AddDocument(new Document { new StoredField("id", firstId + i), new TextField("name", names[i], Field.Store.YES) });
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
