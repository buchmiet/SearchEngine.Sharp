using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SearchEngine.Profiling;

namespace ThreeWay;

/// <summary>
/// What the index must contain, kept as flat arrays (no object per document, so it adds nothing to the garbage collector's
/// work in the engine under test): per id the version of its text (0 = the original), the extension swapped in by the last
/// update and whether the document is live. The text of any document is derived from them.
/// </summary>
public sealed class LifecycleModel
{
    // The word swapped by an update: the extension, which is also a token the common queries look for.
    private static readonly string[] Exts = ["jpg", "png", "pdf", "txt", "docx", "cs"];

    private readonly string[] _names;
    private readonly int[] _ver;
    private readonly byte[] _ext;
    private readonly bool[] _live;
    private readonly int[] _oldPool; // original ids no operation has touched yet
    private int _oldCount;
    private readonly int[] _ring;   // the last writes (additions and updates), newest overwrite oldest
    private int _ringCount, _ringHead;
    private readonly Random _rng;

    public int N { get; }
    public int NextId { get; private set; }
    public int LiveCount { get; private set; }
    public int OldUntouched => _oldCount;
    public int Seq { get; private set; }

    public LifecycleModel(string[] names, int n, int recentWindow, int seed)
    {
        _names = names;
        N = n;
        _ver = new int[names.Length];
        _ext = new byte[names.Length];
        _live = new bool[names.Length];
        for (int i = 0; i < n; i++) _live[i] = true;
        _oldPool = new int[n];
        for (int i = 0; i < n; i++) _oldPool[i] = i;
        _oldCount = n;
        _ring = new int[Math.Max(1, recentWindow)];
        NextId = n;
        LiveCount = n;
        _rng = new Random(seed);
    }

    public bool IsLive(int id) => id < NextId && _live[id];
    public int Version(int id) => _ver[id];
    public static string VersionToken(int ver) => $"zqv{ver}x";

    public string Text(int id)
    {
        string name = _names[id];
        if (_ext[id] != 0)
        {
            int dot = name.LastIndexOf('.');
            name = (dot >= 0 ? name[..dot] : name) + "." + Exts[_ext[id] - 1];
        }

        var sb = new StringBuilder(name, name.Length + 32);
        sb.Append(" lcdoc");
        if (id >= N) sb.Append(" freshdoc");
        if (_ver[id] > 0) sb.Append(" lcver ").Append(VersionToken(_ver[id]));
        return sb.ToString();
    }

    private void Remember(int id)
    {
        _ring[_ringHead] = id;
        _ringHead = (_ringHead + 1) % _ring.Length;
        if (_ringCount < _ring.Length) _ringCount++;
    }

    private int PickRecent()
    {
        for (int t = 0; t < 32 && _ringCount > 0; t++)
        {
            int id = _ring[_rng.Next(_ringCount)];
            if (_live[id]) return id;
        }

        return -1;
    }

    private int PickOld()
    {
        if (_oldCount == 0) return -1;
        int k = _rng.Next(_oldCount);
        int id = _oldPool[k];
        _oldPool[k] = _oldPool[--_oldCount];
        return id;
    }

    /// <summary>A live document to update or delete: an untouched original one (the large first segment) with probability oldShare, else a recent write.</summary>
    public int PickTarget(double oldShare)
    {
        int id = _rng.NextDouble() < oldShare ? PickOld() : PickRecent();
        if (id < 0) id = PickOld();
        if (id < 0) id = PickRecent();
        return id;
    }

    public double NextDouble() => _rng.NextDouble();

    public int Add()
    {
        int id = NextId++;
        _live[id] = true;
        _ver[id] = ++Seq;
        LiveCount++;
        Remember(id);
        return id;
    }

    /// <summary>Gives the document new text; returns its previous version.</summary>
    public int Update(int id)
    {
        int old = _ver[id];
        _ver[id] = ++Seq;
        _ext[id] = (byte)(1 + _rng.Next(Exts.Length));
        Remember(id);
        return old;
    }

    public void Delete(int id)
    {
        ++Seq;
        _live[id] = false;
        LiveCount--;
    }

    public int[] LiveIds(Func<int, bool>? filter = null)
    {
        var list = new List<int>(LiveCount);
        for (int i = 0; i < NextId; i++)
            if (_live[i] && (filter is null || filter(i))) list.Add(i);
        return [.. list];
    }

    /// <summary>A few ASCII tokens of the document's text, lower case: a query that every document with this text matches.</summary>
    public string[] AndTokens(int id)
    {
        var tokens = new List<string>();
        foreach (string t in Tokens(Text(id)))
        {
            if (t.Length < 2 || t is "and" or "or" or "not" || !t.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9')) continue;
            if (!tokens.Contains(t)) tokens.Add(t);
            if (tokens.Count == 8) break;
        }

        return [.. tokens];
    }

    public static IEnumerable<string> Tokens(string text)
    {
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool sep = i == text.Length || Sep.IsSep(text[i]);
            if (!sep && start < 0) start = i;
            else if (sep && start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }
}

/// <summary>
/// The index-lifecycle benchmark: start from n documents, then a long mixed stream of additions, updates and deletions, one
/// call each, timed until the change is visible. The expected index lives in a <see cref="LifecycleModel"/> and every
/// engine is compared with it by ids. One engine per process.
/// </summary>
public static class Lifecycle
{
    private sealed record Check(string Name, QuerySpec Spec, int[] Expected);

    public static int Run(string[] args)
    {
        string engineName = "sharp", label = "", outPath = "lifecycle.json", mixText = "add:30,update:50,delete:20";
        int n = 200_000, ops = 100_000, seed = 2026, rate = 0, recentWindow = 2_000, checkEvery = 0, skipEvery = 0;
        double oldShare = 0.9;
        bool reader = false, fullIds = true;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--engine": engineName = args[++i]; break;
                case "--label": label = args[++i]; break;
                case "--n": n = int.Parse(args[++i]); break;
                case "--ops": ops = int.Parse(args[++i]); break;
                case "--mix": mixText = args[++i]; break;
                case "--old-share": oldShare = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--recent-window": recentWindow = int.Parse(args[++i]); break;
                case "--check-every": checkEvery = int.Parse(args[++i]); break; // untimed light check every K operations, 0 = none
                case "--full-ids": fullIds = args[++i] == "1"; break;          // compare the full id set of every live document at the end
                case "--reader": reader = args[++i] == "1"; break;
                case "--rate": rate = int.Parse(args[++i]); break;
                case "--self-test-skip": skipEvery = int.Parse(args[++i]); break; // validation self-test: do not send every K-th update to the engine, the checks must then fail
                case "--seed": seed = int.Parse(args[++i]); break;
                case "--out": outPath = args[++i]; break;
            }
        }

        double[] mix = [0, 0, 0];
        foreach (string part in mixText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':');
            int k = kv[0].ToLowerInvariant() switch { "add" => 0, "update" => 1, "delete" => 2, _ => throw new ArgumentException("--mix: " + part) };
            mix[k] = double.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture);
        }

        double mixTotal = mix.Sum();
        if (mixTotal <= 0) throw new ArgumentException("--mix is empty");

        using ISustained baseEng = engineName switch
        {
            "sharp" => new SustainedSharp(label.Length > 0 ? label : "SearchEngine.Sharp"),
            "lucene" => new SustainedLucene(),
            "tantivy" => new SustainedTantivy(),
            _ => throw new ArgumentException(engineName),
        };
        var eng = (ILifecycle)baseEng;

        var (_, files) = Corpus.Generate(n + ops, seed);
        var names = new string[n + ops];
        for (int i = 0; i < names.Length; i++)
            names[i] = files[i].Name;
        var model = new LifecycleModel(names, n, recentWindow, seed + 1);
        var baseTexts = new string[n];
        for (int i = 0; i < n; i++)
            baseTexts[i] = model.Text(i);

        Console.Error.WriteLine($"# {eng.Name} lifecycle n={n:N0} ops={ops:N0} mix={mixText} old-share={oldShare} reader={reader} rate={(rate == 0 ? "max" : rate + "/s")}");
        var buildWatch = Stopwatch.StartNew();
        eng.Build(baseTexts, null);
        double buildSeconds = buildWatch.Elapsed.TotalSeconds;
        baseTexts = null!;
        for (int i = 0; i < 200; i++)
            eng.ReaderQuery(i);

        var latency = new double[ops];
        var kinds = new byte[ops];
        int[] executed = [0, 0, 0];
        int visibilityErrors = 0, idErrors = 0;
        var slowWrites = new List<double[]>();
        var staleTokens = new List<string>();
        var deletedTokens = new List<string>();
        var rngSample = new Random(seed + 2);
        int staleSeen = 0, deletedSeen = 0;

        // Concurrent reader, as in the sustained benchmark.
        var readerLatency = new List<double>(1 << 16);
        var readerStamp = new List<double>(1 << 16);
        var readerMerge = new List<int>(1 << 16);
        bool stop = false, pauseReader = false;
        Thread? readerThread = null;
        long runStart = 0;
        if (reader)
        {
            readerThread = new Thread(() =>
            {
                int k = 0;
                while (!Volatile.Read(ref stop))
                {
                    if (Volatile.Read(ref pauseReader)) { Thread.Sleep(1); continue; }
                    bool? merging = eng.MergeActive;
                    long a = Stopwatch.GetTimestamp();
                    eng.ReaderQuery(k++);
                    long b = Stopwatch.GetTimestamp();
                    readerLatency.Add((b - a) * 1e6 / Stopwatch.Frequency);
                    readerMerge.Add(merging is null ? -1 : merging.Value ? 1 : 0);
                    readerStamp.Add((a - runStart) / (double)Stopwatch.Frequency);
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
        }

        int sampleEvery = Math.Max(1, ops / 400);
        var layout = eng.Detail();
        var sOp = new List<int> { 0 }; var sSeg = new List<int> { layout.Segments }; var sDelta = new List<long> { layout.DeltaDocuments };
        var sDead = new List<long> { layout.DeadDocuments }; var sLargest = new List<long> { layout.LargestDocuments }; var sLargestDead = new List<long> { layout.LargestDead };
        var sLatency = new List<double> { 0 }; var sMerges = new List<long> { eng.Merges().Merges }; var sLive = new List<int> { model.LiveCount };
        var layoutStart = layout;
        var largestEvents = new List<Dictionary<string, object>>();
        var periodic = new List<object>();

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long rss0 = Proc.RssBytes();
        long alloc0 = GC.GetTotalAllocatedBytes(true);
        TimeSpan pause0 = GC.GetTotalPauseDuration();
        int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
        using var rssSampler = new RssSampler();
        runStart = Stopwatch.GetTimestamp();
        readerThread?.Start();
        long wall0 = Stopwatch.GetTimestamp();
        long excluded = 0; // ticks spent in untimed periodic checks, left out of the wall time and of the pacing

        int done = 0;
        for (int b = 0; b < ops; b++)
        {
            // Plan the operation from the model (untimed).
            OpKind kind;
            double r = model.NextDouble() * mixTotal;
            kind = r < mix[0] ? OpKind.Add : r < mix[0] + mix[1] ? OpKind.Update : OpKind.Delete;
            int id;
            if (kind == OpKind.Add)
                id = -1;
            else
            {
                id = model.PickTarget(oldShare);
                if (id < 0) kind = OpKind.Add; // nothing left to update or delete
            }

            string? text = null, probe;
            string[]? andTokens = null;
            long before = 0;
            int oldVer = 0;
            long expectedProbe;
            if (kind == OpKind.Add)
            {
                id = model.Add();
                text = model.Text(id);
                probe = LifecycleModel.VersionToken(model.Version(id));
                expectedProbe = 1;
            }
            else if (kind == OpKind.Update)
            {
                oldVer = model.Update(id);
                text = model.Text(id);
                probe = LifecycleModel.VersionToken(model.Version(id));
                expectedProbe = 1;
            }
            else
            {
                oldVer = model.Version(id);
                andTokens = model.AndTokens(id);
                before = eng.CountAnd(andTokens);
                probe = LifecycleModel.VersionToken(oldVer); // version 0 is a token no document has
                expectedProbe = 0;
                model.Delete(id);
            }

            if (rate > 0)
            {
                long due = wall0 + excluded + (long)(b * (double)Stopwatch.Frequency / rate);
                while (Stopwatch.GetTimestamp() < due)
                    Thread.Sleep(0);
            }

            long t0 = Stopwatch.GetTimestamp();
            long hits = skipEvery > 0 && kind == OpKind.Update && b % skipEvery == 0 ? expectedProbe : eng.Apply(kind, id, text, probe);
            long t1 = Stopwatch.GetTimestamp();
            latency[b] = (t1 - t0) * 1e6 / Stopwatch.Frequency;
            kinds[b] = (byte)kind;
            executed[(int)kind]++;
            done = b + 1;
            if (hits != expectedProbe)
                visibilityErrors++;
            if (latency[b] > 10_000)
                slowWrites.Add([(t0 - runStart) / (double)Stopwatch.Frequency, (t1 - runStart) / (double)Stopwatch.Frequency]);

            // Untimed: the changed id is visible or gone, and a superseded version is gone.
            if (kind == OpKind.Delete)
            {
                if (eng.CountAnd(andTokens!) != before - 1 || before < 1) idErrors++;
                if (oldVer > 0 && eng.TopId(LifecycleModel.VersionToken(oldVer)) != -1) idErrors++;
                if (oldVer > 0) Reservoir(deletedTokens, LifecycleModel.VersionToken(oldVer), ++deletedSeen, rngSample);
            }
            else
            {
                if (eng.TopId(probe) != id) idErrors++;
                if (kind == OpKind.Update && oldVer > 0)
                {
                    string old = LifecycleModel.VersionToken(oldVer);
                    if (eng.TopId(old) != -1) idErrors++;
                    Reservoir(staleTokens, old, ++staleSeen, rngSample);
                }
            }

            var prevLayout = layout;
            layout = eng.Detail();
            if (layout.LargestKey != prevLayout.LargestKey)
            {
                // A different segment is the largest than before this call: the largest one was merged or rewritten (or overtaken).
                largestEvents.Add(new Dictionary<string, object>
                {
                    ["op"] = b + 1,
                    ["seconds"] = (t1 - runStart) / (double)Stopwatch.Frequency,
                    ["kind"] = prevLayout.LargestDead > 0 ? "rewrite" : "replaced",
                    ["segmentsBefore"] = prevLayout.Segments, ["segmentsAfter"] = layout.Segments,
                    ["largestDocumentsBefore"] = prevLayout.LargestDocuments, ["largestDocumentsAfter"] = layout.LargestDocuments,
                    ["largestDeadBefore"] = prevLayout.LargestDead, ["largestDeadAfter"] = layout.LargestDead,
                    ["deadBefore"] = prevLayout.DeadDocuments, ["deadAfter"] = layout.DeadDocuments,
                });
            }

            if (b % sampleEvery == 0)
                AddSample();

            if (checkEvery > 0 && (b + 1) % checkEvery == 0 && b + 1 < ops)
            {
                long c0 = Stopwatch.GetTimestamp();
                Volatile.Write(ref pauseReader, true);
                periodic.Add(LightCheck(eng, model, staleTokens, deletedTokens, rngSample, b + 1));
                Volatile.Write(ref pauseReader, false);
                excluded += Stopwatch.GetTimestamp() - c0;
            }
        }

        double wallSeconds = (Stopwatch.GetTimestamp() - wall0 - excluded) / (double)Stopwatch.Frequency;
        Volatile.Write(ref stop, true);
        readerThread?.Join();
        long peakRss = rssSampler.PeakBytes;
        long allocated = GC.GetTotalAllocatedBytes(true) - alloc0;
        TimeSpan pauseGc = GC.GetTotalPauseDuration() - pause0;
        int gen0Count = GC.CollectionCount(0) - gen0, gen2Count = GC.CollectionCount(2) - gen2;
        var layoutEnd = eng.Detail();
        var mergesDuring = eng.Merges();
        eng.Settle();
        var layoutSettled = eng.Detail();

        // ---------------- validation against the model, by ids (untimed)
        var checks = new List<Check>();
        int[] lcdoc = fullIds ? model.LiveIds() : [];
        if (fullIds)
            checks.Add(new("lcdoc", Term("lcdoc"), lcdoc));
        checks.Add(new("lcver", Term("lcver"), model.LiveIds(i => model.Version(i) > 0)));
        checks.Add(new("freshdoc", Term("freshdoc"), model.LiveIds(i => i >= n)));
        var scan = ScanModel(model);
        checks.Add(new("term_common", Queries.All[2], scan.Jpg));
        checks.Add(new("term_final", Queries.All[1], scan.Final));
        checks.Add(new("report_and_pdf", Queries.All[3], scan.ReportAndPdf));
        checks.Add(new("prefix_rep", Queries.All[6], scan.PrefixRep));
        var verification = new Dictionary<string, object>();
        var checkRows = new List<object>();
        long mismatches = 0;
        foreach (var c in checks)
        {
            int[] actual = eng.FindIds(c.Spec);
            var (missing, extra) = Diff(c.Expected, actual);
            mismatches += missing + extra;
            checkRows.Add(new { name = c.Name, expected = c.Expected.Length, actual = actual.Length, missing, extra });
            byte[] bytes = new byte[actual.Length * 4];
            Buffer.BlockCopy(actual, 0, bytes, 0, bytes.Length);
            verification[c.Name] = new { count = actual.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
        }

        // Tokens: the latest version token of live documents matches exactly its document; superseded and deleted ones match nothing.
        int liveTokenChecks = 0, liveTokenBad = 0, staleBad = 0, deletedBad = 0;
        foreach (int id in SampleLive(model, 300, rngSample))
        {
            liveTokenChecks++;
            if (eng.TopId(LifecycleModel.VersionToken(model.Version(id))) != id) liveTokenBad++;
        }

        foreach (string t in staleTokens) if (eng.TopId(t) != -1) staleBad++;
        foreach (string t in deletedTokens) if (eng.TopId(t) != -1) deletedBad++;
        mismatches += liveTokenBad + staleBad + deletedBad;
        long expectedLive = model.LiveCount;
        long actualLive = eng.CountAnd(["lcdoc"]);
        if (actualLive != expectedLive) mismatches += Math.Abs(actualLive - expectedLive);

        // ---------------- result
        double[] sorted = [.. latency.Take(done).Order()];
        double P(double[] a, double q) => a.Length == 0 ? double.NaN : a[Math.Min(a.Length - 1, (int)(q * a.Length))];
        object Stats(double[] a) => a.Length == 0 ? new { count = 0 } : (object)new
        {
            count = a.Length, mean = a.Average(), p50 = P(a, .5), p95 = P(a, .95), p99 = P(a, .99), p999 = P(a, .999), max = a[^1], total = a.Sum(),
        };
        var byKind = new Dictionary<string, object>();
        foreach (var k in new[] { OpKind.Add, OpKind.Update, OpKind.Delete })
            byKind[k.ToString().ToLowerInvariant()] = Stats([.. Enumerable.Range(0, done).Where(i => kinds[i] == (byte)k).Select(i => latency[i]).Order()]);

        var readerSorted = readerLatency.Order().ToArray();
        double RP(double q) => readerSorted.Length == 0 ? double.NaN : readerSorted[Math.Min(readerSorted.Length - 1, (int)(q * readerSorted.Length))];
        object? Split(Func<int, bool> keep)
        {
            var part = new List<double>();
            for (int i = 0; i < readerLatency.Count; i++)
                if (keep(i)) part.Add(readerLatency[i]);
            if (part.Count == 0) return null;
            part.Sort();
            double Q(double q) => part[Math.Min(part.Count - 1, (int)(q * part.Count))];
            return new { queries = part.Count, p50 = Q(.5), p95 = Q(.95), p99 = Q(.99), max = part[^1] };
        }

        bool OverlapsSlowWrite(int i)
        {
            double s = readerStamp[i], e = s + readerLatency[i] / 1e6;
            foreach (var w in slowWrites)
                if (s < w[1] && e > w[0]) return true;
            return false;
        }

        foreach (var ev in largestEvents)
        {
            int op = (int)ev["op"];
            double max = 0;
            for (int j = Math.Max(0, op - 1 - 3); j <= Math.Min(done - 1, op - 1 + 3); j++)
                max = Math.Max(max, latency[j]);
            ev["maxLatencyAroundMicroseconds"] = max;
            ev["latencyMicroseconds"] = latency[op - 1];
        }

        var events = eng.MergeEvents.Where(m => m.StartTimestamp >= runStart)
            .Select(m => new { start = (m.StartTimestamp - runStart) / (double)Stopwatch.Frequency, ms = m.Milliseconds, docs = m.Documents }).ToList();
        double[] eventMs = [.. events.Select(e => e.ms).Order()];

        var result = new
        {
            scenario = "lifecycle",
            engine = eng.Name,
            n, ops = done, batch = 1, reader, durable = false,
            mix = new { text = mixText, add = executed[0], update = executed[1], delete = executed[2], oldShare, recentWindow, seed },
            buildSeconds,
            wallSeconds,
            documentsPerSecond = done / wallSeconds,
            callsPerSecond = done / wallSeconds,
            latencyMicroseconds = new
            {
                mean = sorted.Average(), p50 = P(sorted, .5), p95 = P(sorted, .95), p99 = P(sorted, .99), p999 = P(sorted, .999), max = sorted[^1], min = sorted[0],
                over10ms = sorted.Count(x => x > 10_000), over100ms = sorted.Count(x => x > 100_000), over1s = sorted.Count(x => x > 1_000_000),
                slowestCallShareOfTotalPercent = sorted.Skip((int)(sorted.Length * 0.99)).Sum() / sorted.Sum() * 100,
            },
            latencyByOperation = byKind,
            memory = new
            {
                rssBeforeMiB = rss0 / 1048576.0, peakRssMiB = peakRss / 1048576.0, peakGrowthMiB = (peakRss - rss0) / 1048576.0,
                managedAllocatedMiB = allocated / 1048576.0,
                gcPauseMilliseconds = pauseGc.TotalMilliseconds, gen0Collections = gen0Count, gen2Collections = gen2Count,
            },
            rate,
            mergeEvents = events,
            mergeDurationsMilliseconds = eventMs.Length == 0 ? null : new { count = eventMs.Length, p50 = eventMs[eventMs.Length / 2], p95 = eventMs[(int)(eventMs.Length * .95)], max = eventMs[^1], total = eventMs.Sum() },
            slowWrites,
            merges = new { mergesDuring.Source, mergesDuring.Merges, mergesDuring.DocumentsWritten, mergesDuring.MergeSeconds, mergesDuring.Note,
                writeAmplification = mergesDuring.DocumentsWritten > 0 ? mergesDuring.DocumentsWritten / (double)done : (double?)null },
            layout = new
            {
                start = new { seg0 = layoutStart.Segments, delta0 = layoutStart.DeltaDocuments, layoutStart.DeadDocuments, layoutStart.LargestDocuments },
                end = new { layoutEnd.Segments, layoutEnd.DeltaDocuments, layoutEnd.DeadDocuments, layoutEnd.LargestDocuments, layoutEnd.LargestDead },
                settled = new { layoutSettled.Segments, layoutSettled.DeltaDocuments, layoutSettled.DeadDocuments, layoutSettled.LargestDocuments, layoutSettled.LargestDead },
            },
            largestSegmentEvents = largestEvents,
            concurrentReader = reader ? new
            {
                queries = readerLatency.Count, p50 = RP(.5), p95 = RP(.95), p99 = RP(.99), p999 = RP(.999), max = readerSorted.Length == 0 ? double.NaN : readerSorted[^1],
                duringBackgroundMerge = Split(i => readerMerge[i] == 1),
                withoutBackgroundMerge = Split(i => readerMerge[i] == 0),
                mergeNotObservable = readerMerge.All(m => m == -1),
                overlappingSlowWrite = Split(OverlapsSlowWrite),
                notOverlappingSlowWrite = Split(i => !OverlapsSlowWrite(i)),
            } : null,
            readerSeries = reader ? new { seconds = readerStamp, microseconds = readerLatency, merging = readerMerge } : null,
            errors = new { visibility = visibilityErrors, idMismatch = idErrors },
            validation = new
            {
                mismatches,
                expectedLive,
                actualLive,
                fullIds,
                checks = checkRows,
                latestVersionTokens = new { checkedCount = liveTokenChecks, bad = liveTokenBad },
                supersededTokens = new { checkedCount = staleTokens.Count, bad = staleBad },
                deletedTokens = new { checkedCount = deletedTokens.Count, bad = deletedBad },
                periodic,
            },
            verification,
            samples = new { op = sOp, segments = sSeg, deltaDocuments = sDelta, deadDocuments = sDead, largestDocuments = sLargest, largestDead = sLargestDead, live = sLive, latencyMicroseconds = sLatency, merges = sMerges },
            latencySeries = latency.Take(done).ToArray(),
            operationSeries = kinds.Take(done).Select(k => (int)k).ToArray(),
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = false }));
        Console.Error.WriteLine($"  {done:N0} calls in {wallSeconds:F2}s (add {executed[0]:N0}, update {executed[1]:N0}, delete {executed[2]:N0})  p50 {P(sorted, .5):F0}µs p99 {P(sorted, .99):F0}µs max {sorted[^1] / 1000:F1}ms  " +
            $"segments {layoutStart.Segments}->{layoutEnd.Segments}  dead {layoutEnd.DeadDocuments:N0}  largest-segment changes {largestEvents.Count}  peakRSS +{(peakRss - rss0) / 1048576.0:F0}MiB  GC pause {pauseGc.TotalMilliseconds:F0}ms  " +
            $"errors vis={visibilityErrors} id={idErrors}  validation mismatches={mismatches}");
        eng.Dispose();
        return visibilityErrors == 0 && idErrors == 0 && mismatches == 0 ? 0 : 2;

        void AddSample()
        {
            sOp.Add(done); sSeg.Add(layout.Segments); sDelta.Add(layout.DeltaDocuments); sDead.Add(layout.DeadDocuments);
            sLargest.Add(layout.LargestDocuments); sLargestDead.Add(layout.LargestDead); sLatency.Add(latency[done - 1]);
            sMerges.Add(eng.Merges().Merges); sLive.Add(model.LiveCount);
        }

        static QuerySpec Term(string t) => new(t, t, (t, SearchEngine.WordMatchMethod.Exact, false),
            () => new Lucene.Net.Search.TermQuery(new Lucene.Net.Index.Term("name", t)), () => TantivyDotNet.Query.Term("name", t));
    }

    private static bool Reservoir(List<string> list, string item, int seen, Random rng)
    {
        if (list.Count < 200) { list.Add(item); return true; }
        int j = rng.Next(seen);
        if (j < list.Count) { list[j] = item; return true; }
        return false;
    }

    private static IEnumerable<int> SampleLive(LifecycleModel model, int count, Random rng)
    {
        var picked = new HashSet<int>();
        for (int t = 0; t < count * 20 && picked.Count < count; t++)
        {
            int id = rng.Next(model.NextId);
            if (model.IsLive(id) && model.Version(id) > 0 && picked.Add(id)) yield return id;
        }
    }

    // Counts and a sample of tokens, cheap enough to run every few thousand operations.
    private static object LightCheck(ILifecycle eng, LifecycleModel model, List<string> stale, List<string> deleted, Random rng, int op)
    {
        long mism = 0;
        long live = eng.CountAnd(["lcdoc"]);
        if (live != model.LiveCount) mism += Math.Abs(live - model.LiveCount);
        long fresh = eng.CountAnd(["freshdoc"]);
        long expectedFresh = model.LiveIds(i => i >= model.N).Length;
        if (fresh != expectedFresh) mism += Math.Abs(fresh - expectedFresh);
        int bad = 0;
        foreach (int id in SampleLive(model, 60, rng))
            if (eng.TopId(LifecycleModel.VersionToken(model.Version(id))) != id) bad++;
        foreach (string t in stale.Take(60)) if (eng.TopId(t) != -1) bad++;
        foreach (string t in deleted.Take(60)) if (eng.TopId(t) != -1) bad++;
        mism += bad;
        return new { op, liveExpected = model.LiveCount, liveActual = live, freshExpected = expectedFresh, freshActual = fresh, tokenMismatches = bad, mismatches = mism };
    }

    private sealed record Scan(int[] Jpg, int[] Final, int[] ReportAndPdf, int[] PrefixRep);

    // The expected answers of the query checks, computed from the model's texts with the benchmark's own tokenizer
    // (split at the separators of Sharp's default preset, lower case): independent of every engine.
    private static Scan ScanModel(LifecycleModel model)
    {
        List<int> jpg = [], fin = [], rp = [], rep = [];
        for (int id = 0; id < model.NextId; id++)
        {
            if (!model.IsLive(id)) continue;
            bool j = false, f = false, r = false, p = false, pre = false;
            foreach (string t in LifecycleModel.Tokens(model.Text(id)))
            {
                if (t == "jpg") j = true;
                else if (t == "final") f = true;
                else if (t == "report") r = true;
                else if (t == "pdf") p = true;
                if (t.StartsWith("rep", StringComparison.Ordinal)) pre = true;
            }

            if (j) jpg.Add(id);
            if (f) fin.Add(id);
            if (r && p) rp.Add(id);
            if (pre) rep.Add(id);
        }

        return new([.. jpg], [.. fin], [.. rp], [.. rep]);
    }

    // (in expected but not in actual, in actual but not in expected); both arrays sorted ascending.
    private static (long Missing, long Extra) Diff(int[] expected, int[] actual)
    {
        long missing = 0, extra = 0;
        int i = 0, j = 0;
        while (i < expected.Length && j < actual.Length)
        {
            if (expected[i] == actual[j]) { i++; j++; }
            else if (expected[i] < actual[j]) { missing++; i++; }
            else { extra++; j++; }
        }

        return (missing + expected.Length - i, extra + actual.Length - j);
    }
}
