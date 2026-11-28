using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SearchEngine.Profiling;

namespace ThreeWay;

/// <summary>
/// The sustained incremental indexing benchmark: start from n documents, add documents (one at a time or in batches)
/// and time every call until the new documents are visible to queries. One engine per process.
/// </summary>
public static class Sustained
{
    public static int Run(string[] args)
    {
        string engineName = "sharp", label = "", outPath = "sustained.json", tmp = Path.Combine(Path.GetTempPath(), "threeway-sustained");
        int n = 1_000_000, ops = 20_000, batch = 1, seed = 2026;
        bool reader = false, durable = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--engine": engineName = args[++i]; break;
                case "--label": label = args[++i]; break;
                case "--n": n = int.Parse(args[++i]); break;
                case "--ops": ops = int.Parse(args[++i]); break;
                case "--batch": batch = int.Parse(args[++i]); break;
                case "--reader": reader = args[++i] == "1"; break;
                case "--durable": durable = args[++i] == "1"; break;
                case "--out": outPath = args[++i]; break;
                case "--tmp": tmp = args[++i]; break;
            }
        }

        ops -= ops % batch; // whole batches only
        using ISustained eng = engineName switch
        {
            "sharp" => new SustainedSharp(label.Length > 0 ? label : "SearchEngine.Sharp"),
            "lucene" => new SustainedLucene(),
            "tantivy" => new SustainedTantivy(),
            _ => throw new ArgumentException(engineName),
        };

        var (_, files) = Corpus.Generate(n + ops, seed);
        var baseNames = new string[n];
        for (int i = 0; i < n; i++)
            baseNames[i] = files[i].Name;
        // The added documents carry a token every one of them has ("freshdoc") and a token of their own, so a query can
        // find all of them, and each one alone.
        var added = new string[ops];
        for (int i = 0; i < ops; i++)
            added[i] = $"{files[n + i].Name} freshdoc zqtok{n + i}x";

        string? dir = null;
        if (durable)
        {
            dir = Path.Combine(tmp, $"{engineName}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
        }

        Console.Error.WriteLine($"# {eng.Name}  n={n:N0} ops={ops:N0} batch={batch} reader={reader} durable={durable}");
        var buildWatch = Stopwatch.StartNew();
        try { eng.Build(baseNames, dir); }
        catch (NotSupportedException e) { Console.Error.WriteLine(e.Message); return 3; }
        double buildSeconds = buildWatch.Elapsed.TotalSeconds;
        for (int i = 0; i < 200; i++)
            eng.ReaderQuery(i); // warm up the query paths

        int batches = ops / batch;
        var latency = new double[batches];
        var layoutOp = new List<int>();
        var layoutSegments = new List<int>();
        var layoutDelta = new List<long>();
        var layoutLatency = new List<double>();
        int errors = 0, idErrors = 0;

        // Concurrent reader: one thread cycling through three queries, timing each.
        var readerLatency = new List<double>(1 << 16);
        var readerStamp = new List<double>(1 << 16);
        bool stop = false;
        Thread? readerThread = null;
        long runStart = 0;
        if (reader)
        {
            readerThread = new Thread(() =>
            {
                int k = 0;
                while (!Volatile.Read(ref stop))
                {
                    long a = Stopwatch.GetTimestamp();
                    eng.ReaderQuery(k++);
                    long b = Stopwatch.GetTimestamp();
                    readerLatency.Add((b - a) * 1e6 / Stopwatch.Frequency);
                    readerStamp.Add((a - runStart) / (double)Stopwatch.Frequency);
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
        }

        int sampleEvery = Math.Max(1, batches / 400);
        var (seg0, delta0) = eng.Layout();
        layoutOp.Add(0); layoutSegments.Add(seg0); layoutDelta.Add(delta0); layoutLatency.Add(0);

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long rss0 = Proc.RssBytes();
        long alloc0 = GC.GetTotalAllocatedBytes(true);
        TimeSpan pause0 = GC.GetTotalPauseDuration();
        int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
        using var rssSampler = new RssSampler();
        runStart = Stopwatch.GetTimestamp();
        readerThread?.Start();
        long wall0 = Stopwatch.GetTimestamp();
        for (int b = 0; b < batches; b++)
        {
            int first = b * batch;
            var names = new string[batch];
            Array.Copy(added, first, names, 0, batch);
            string probe = $"zqtok{n + first + batch - 1}x";
            long t0 = Stopwatch.GetTimestamp();
            long hits = eng.AddVisible(n + first, names, probe);
            long t1 = Stopwatch.GetTimestamp();
            latency[b] = (t1 - t0) * 1e6 / Stopwatch.Frequency;
            if (hits != 1)
                errors++;
            if (b % sampleEvery == 0)
            {
                var (seg, delta) = eng.Layout();
                layoutOp.Add(b + 1); layoutSegments.Add(seg); layoutDelta.Add(delta); layoutLatency.Add(latency[b]);
            }

            // Untimed: the hit really is the document that was just added, not only some document.
            if (b % 97 == 0 && eng.TopId(probe) != n + first + batch - 1)
                idErrors++;
        }

        double wallSeconds = (Stopwatch.GetTimestamp() - wall0) / (double)Stopwatch.Frequency;
        Volatile.Write(ref stop, true);
        readerThread?.Join();
        long peakRss = rssSampler.PeakBytes;
        long allocated = GC.GetTotalAllocatedBytes(true) - alloc0;
        TimeSpan pause = GC.GetTotalPauseDuration() - pause0;
        int gen0Count = GC.CollectionCount(0) - gen0, gen2Count = GC.CollectionCount(2) - gen2;
        var layoutEnd = eng.Layout();
        var mergesDuring = eng.Merges();
        eng.Settle();
        var layoutSettled = eng.Layout();

        // Validation by id, not by count: the sets of ids for four queries (one of them every added document).
        var verification = new Dictionary<string, object>();
        var checks = new (string Id, QuerySpec Spec)[]
        {
            ("term_final", Queries.All[1]),
            ("report_and_pdf", Queries.All[3]),
            ("prefix_rep", Queries.All[6]),
            ("freshdoc", new QuerySpec("freshdoc", "every added document", ("freshdoc", SearchEngine.WordMatchMethod.Exact, false),
                () => new Lucene.Net.Search.TermQuery(new Lucene.Net.Index.Term("name", "freshdoc")),
                () => TantivyDotNet.Query.Term("name", "freshdoc"))),
        };
        foreach (var (id, spec) in checks)
        {
            int[] ids = eng.FindIds(spec);
            byte[] bytes = new byte[ids.Length * 4];
            Buffer.BlockCopy(ids, 0, bytes, 0, bytes.Length);
            verification[id] = new { count = ids.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
        }

        double[] sorted = [.. latency.Order()];
        double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
        var readerSorted = readerLatency.Order().ToArray();
        double RP(double q) => readerSorted.Length == 0 ? double.NaN : readerSorted[Math.Min(readerSorted.Length - 1, (int)(q * readerSorted.Length))];

        var result = new
        {
            engine = eng.Name,
            n, ops, batch, reader, durable,
            buildSeconds,
            wallSeconds,
            documentsPerSecond = ops / wallSeconds,
            callsPerSecond = batches / wallSeconds,
            latencyMicroseconds = new
            {
                mean = latency.Average(), p50 = P(.5), p95 = P(.95), p99 = P(.99), p999 = P(.999), max = sorted[^1], min = sorted[0],
                over10ms = latency.Count(x => x > 10_000), over100ms = latency.Count(x => x > 100_000), over1s = latency.Count(x => x > 1_000_000),
                slowestCallShareOfTotalPercent = sorted.Skip((int)(sorted.Length * 0.99)).Sum() / latency.Sum() * 100,
            },
            memory = new
            {
                rssBeforeMiB = rss0 / 1048576.0, peakRssMiB = peakRss / 1048576.0, peakGrowthMiB = (peakRss - rss0) / 1048576.0,
                managedAllocatedMiB = allocated / 1048576.0,
                gcPauseMilliseconds = pause.TotalMilliseconds, gen0Collections = gen0Count, gen2Collections = gen2Count,
            },
            merges = new { mergesDuring.Source, mergesDuring.Merges, mergesDuring.DocumentsWritten, mergesDuring.MergeSeconds, mergesDuring.Note,
                writeAmplification = mergesDuring.DocumentsWritten > 0 ? mergesDuring.DocumentsWritten / (double)ops : (double?)null },
            layout = new { start = new { seg0, delta0 }, end = new { layoutEnd.Segments, layoutEnd.DeltaDocuments }, settled = new { layoutSettled.Segments, layoutSettled.DeltaDocuments } },
            concurrentReader = reader ? new { queries = readerLatency.Count, p50 = RP(.5), p95 = RP(.95), p99 = RP(.99), p999 = RP(.999), max = readerSorted[^1] } : null,
            readerSeries = reader ? new { seconds = readerStamp, microseconds = readerLatency } : null,
            errors = new { visibility = errors, idMismatch = idErrors },
            verification,
            samples = new { op = layoutOp, segments = layoutSegments, deltaDocuments = layoutDelta, latencyMicroseconds = layoutLatency },
            latencySeries = latency,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false }));
        Console.Error.WriteLine($"  {batches:N0} calls in {wallSeconds:F2}s  p50 {P(.5):F0}µs p99 {P(.99):F0}µs max {sorted[^1] / 1000:F1}ms  segments {seg0}->{layoutEnd.Segments}->{layoutSettled.Segments}  peakRSS +{(peakRss - rss0) / 1048576.0:F0}MiB  GC pause {pause.TotalMilliseconds:F0}ms  errors vis={errors} id={idErrors}");
        // Close the engine before its directory goes: closing flushes and merges there.
        eng.Dispose();
        if (dir is not null && Directory.Exists(dir))
            Directory.Delete(dir, true);
        return errors == 0 && idErrors == 0 ? 0 : 2;
    }
}
