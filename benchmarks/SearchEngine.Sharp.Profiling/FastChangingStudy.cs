using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using SearchEngine.Filters;

namespace SearchEngine.Profiling;

/// <summary>Facet columns shared by the study engines (size, modified ticks, attributes).</summary>
public readonly record struct NameFacets(long Size, long Modified, long Attrs);

/// <summary>Query of the study: a literal over the sentinel-delimited lowercase buffer, or a general matcher.</summary>
public sealed record StudyQuery(string Name, string Expression, WordMatchMethod Method, string? Literal, Func<NameFacets, bool>? Facet = null);

/// <summary>Common surface of the engines compared in the fast-changing-data study.</summary>
public interface IStudyEngine
{
    string Kind { get; }
    int Add(string name, NameFacets facets);
    void Remove(int id);
    int Rename(int id, string newName);
    int Count(StudyQuery q, int threads);
}

/// <summary>
/// (b) Flat resident name buffer: lowercase names in one contiguous char array, each preceded and followed by '\0',
/// an offsets array, facet columns, a tombstone bitset, append-only updates and compaction when a quarter is dead.
/// Literal queries (exact, prefix, suffix, infix) are one vectorized IndexOf sweep over the buffer; everything else is
/// evaluated per entry with NameMatcher, optionally partitioned over threads. One writer at a time (lock);
/// readers take the published immutable State and never block.
/// </summary>
public sealed class FlatNameBuffer : IStudyEngine
{
    sealed record State(char[] Buffer, int Length, int[] Start, int Count, ulong[] Dead, NameFacets[] Facets, int DeadCount);

    readonly Lock _gate = new();
    State _state;
    public int Compactions;

    public FlatNameBuffer(int capacity)
    {
        var buf = new char[Math.Max(16, capacity * 24)];
        buf[0] = '\0';
        _state = new State(buf, 1, new int[Math.Max(4, capacity)], 0, new ulong[(Math.Max(4, capacity) + 63) / 64], new NameFacets[Math.Max(4, capacity)], 0);
    }

    public string Kind => "flat";

    public int Add(string name, NameFacets facets)
    {
        lock (_gate)
        {
            var s = _state;
            char[] buf = s.Buffer;
            int need = s.Length + name.Length + 1;
            if (need > buf.Length) { Array.Resize(ref buf, Math.Max(need, buf.Length * 2)); }
            for (int i = 0; i < name.Length; i++) buf[s.Length + i] = char.ToLowerInvariant(name[i]);
            buf[s.Length + name.Length] = '\0';
            int[] start = s.Start; NameFacets[] fac = s.Facets; ulong[] dead = s.Dead;
            if (s.Count == start.Length)
            {
                Array.Resize(ref start, start.Length * 2);
                Array.Resize(ref fac, fac.Length * 2);
                Array.Resize(ref dead, (start.Length + 63) / 64);
            }
            start[s.Count] = s.Length;
            fac[s.Count] = facets;
            Volatile.Write(ref _state, s with { Buffer = buf, Length = need, Start = start, Count = s.Count + 1, Facets = fac, Dead = dead });
            return s.Count;
        }
    }

    public void Remove(int id)
    {
        lock (_gate)
        {
            var s = _state;
            if ((s.Dead[id >> 6] & (1UL << id)) != 0) return;
            // The tombstone bit is set in place: a concurrent reader sees the removal either before or after it.
            var dead = s.Dead;
            Interlocked.Or(ref dead[id >> 6], 1UL << id);
            Volatile.Write(ref _state, s with { DeadCount = s.DeadCount + 1 });
            if (s.DeadCount + 1 > s.Count / 4 && s.Count > 1024) Compact();
        }
    }

    public int Rename(int id, string newName)
    {
        var f = Volatile.Read(ref _state).Facets[id];
        Remove(id);
        return Add(newName, f);
    }

    /// <summary>Rewrites the buffer without dead entries (ids change; a real engine keeps an external id map).</summary>
    public void Compact()
    {
        lock (_gate)
        {
            var s = _state;
            var buf = new char[s.Buffer.Length];
            var start = new int[s.Start.Length];
            var fac = new NameFacets[s.Facets.Length];
            int len = 1, n = 0;
            for (int i = 0; i < s.Count; i++)
            {
                if ((s.Dead[i >> 6] & (1UL << i)) != 0) continue;
                int st = s.Start[i];
                int end = Array.IndexOf(s.Buffer, '\0', st);
                s.Buffer.AsSpan(st, end - st + 1).CopyTo(buf.AsSpan(len));
                start[n] = len; fac[n] = s.Facets[i];
                len += end - st + 1; n++;
            }
            Volatile.Write(ref _state, new State(buf, len, start, n, new ulong[s.Dead.Length], fac, 0));
            Compactions++;
        }
    }

    public int Count(StudyQuery q, int threads)
    {
        var s = Volatile.Read(ref _state);
        return q.Literal is not null ? CountLiteral(s, q, threads) : CountPredicate(s, q, threads);
    }

    static int EntryAt(State s, int pos)
    {
        int i = Array.BinarySearch(s.Start, 0, s.Count, pos);
        return i >= 0 ? i : ~i - 1;
    }

    static int CountLiteral(State s, StudyQuery q, int threads)
    {
        string lit = q.Literal!;
        if (threads <= 1) return CountLiteralRange(s, q, lit, 0, s.Length);
        int total = 0;
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            int a = (int)((long)s.Length * t / threads), b = (int)((long)s.Length * (t + 1) / threads);
            // Overlap by the literal length so a match crossing a partition border is seen exactly once.
            int c = CountLiteralRange(s, q, lit, a, Math.Min(s.Length, b + lit.Length - 1), b);
            Interlocked.Add(ref total, c);
        });
        return total;
    }

    static int CountLiteralRange(State s, StudyQuery q, string lit, int from, int to, int startLimit = int.MaxValue)
    {
        var span = s.Buffer.AsSpan(0, to);
        int count = 0, pos = from, lastEntry = -1;
        while (true)
        {
            int i = span[pos..].IndexOf(lit);
            if (i < 0) break;
            int at = pos + i;
            if (at >= startLimit) break;
            pos = at + 1;
            int e = EntryAt(s, lit[0] == '\0' ? at + 1 : at);
            if (e == lastEntry || e < 0) continue; // several hits in one name
            lastEntry = e;
            if ((s.Dead[e >> 6] & (1UL << e)) != 0) continue;
            if (q.Facet is not null && !q.Facet(s.Facets[e])) continue;
            count++;
        }
        return count;
    }

    static int CountPredicate(State s, StudyQuery q, int threads)
    {
        var m = NameMatcher.Create(q.Expression, q.Method, true, SearchTokenization.FileMask);
        int Range(int a, int b)
        {
            int c = 0;
            for (int e = a; e < b; e++)
            {
                if ((s.Dead[e >> 6] & (1UL << e)) != 0) continue;
                int st = s.Start[e];
                int end = e + 1 < s.Count ? s.Start[e + 1] - 1 : s.Length - 1;
                if (m.IsMatch(s.Buffer.AsSpan(st, end - st)) && (q.Facet is null || q.Facet(s.Facets[e]))) c++;
            }
            return c;
        }
        if (threads <= 1) return Range(0, s.Count);
        int total = 0;
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            Interlocked.Add(ref total, Range((int)((long)s.Count * t / threads), (int)((long)s.Count * (t + 1) / threads))));
        return total;
    }
}

/// <summary>
/// (c) Trigram index over the sentinel-delimited lowercase name: append-only posting lists per trigram
/// (ids ascend, so lists stay sorted), a tombstone bitset, candidate intersection of the two rarest lists
/// and verification against the name. General expressions fall back to a per-entry scan.
/// </summary>
public sealed class TrigramNameIndex : IStudyEngine
{
    sealed class Postings { public int[] Ids = new int[2]; public int Count; }

    readonly Lock _gate = new();
    readonly Dictionary<long, Postings> _post = new();
    string[] _names;
    NameFacets[] _facets;
    ulong[] _dead;
    int _count;

    public TrigramNameIndex(int capacity)
    {
        _names = new string[Math.Max(4, capacity)];
        _facets = new NameFacets[_names.Length];
        _dead = new ulong[(_names.Length + 63) / 64];
    }

    public string Kind => "trigram";

    static long Key(ReadOnlySpan<char> s, int i) => ((long)s[i] << 32) | ((long)s[i + 1] << 16) | s[i + 2];

    public int Add(string name, NameFacets facets)
    {
        string lower = "\0" + name.ToLowerInvariant() + "\0";
        lock (_gate)
        {
            if (_count == _names.Length)
            {
                Array.Resize(ref _names, _names.Length * 2);
                Array.Resize(ref _facets, _names.Length);
                Array.Resize(ref _dead, (_names.Length + 63) / 64);
            }
            int id = _count;
            Span<long> seen = stackalloc long[Math.Min(lower.Length, 256)];
            int nSeen = 0;
            for (int i = 0; i + 2 < lower.Length; i++)
            {
                long k = Key(lower, i);
                if (seen[..Math.Min(nSeen, seen.Length)].Contains(k)) continue;
                if (nSeen < seen.Length) seen[nSeen++] = k;
                if (!_post.TryGetValue(k, out var p)) _post[k] = p = new Postings();
                if (p.Count > 0 && p.Ids[p.Count - 1] == id) continue;
                if (p.Count == p.Ids.Length) { var g = new int[p.Ids.Length * 2]; p.Ids.AsSpan(0, p.Count).CopyTo(g); Volatile.Write(ref p.Ids, g); }
                p.Ids[p.Count] = id;
                Volatile.Write(ref p.Count, p.Count + 1);
            }
            _names[id] = lower;
            _facets[id] = facets;
            Volatile.Write(ref _count, id + 1);
            return id;
        }
    }

    public void Remove(int id)
    {
        lock (_gate) Interlocked.Or(ref _dead[id >> 6], 1UL << id);
    }

    public int Rename(int id, string newName)
    {
        var f = _facets[id];
        Remove(id);
        return Add(newName, f);
    }

    public int Count(StudyQuery q, int threads)
    {
        int n = Volatile.Read(ref _count);
        var names = Volatile.Read(ref _names);
        var dead = Volatile.Read(ref _dead);
        if (q.Literal is null || q.Literal.Length < 3) return Scan(q, n, names, dead);
        string lit = q.Literal;
        // Two rarest trigram lists.
        (int[] Ids, int Count) best = (Array.Empty<int>(), int.MaxValue), second = best;
        for (int i = 0; i + 2 < lit.Length; i++)
        {
            if (!_post.TryGetValue(Key(lit, i), out var p)) return 0;
            var ids = Volatile.Read(ref p.Ids);
            var c = Math.Min(Volatile.Read(ref p.Count), ids.Length);
            if (c < best.Count) { second = best; best = (ids, c); }
            else if (c < second.Count) second = (ids, c);
        }
        int count = 0;
        var a = best.Ids.AsSpan(0, best.Count);
        var b = second.Count == int.MaxValue ? default : second.Ids.AsSpan(0, second.Count);
        int j = 0;
        foreach (int id in a)
        {
            if (!b.IsEmpty)
            {
                while (j < b.Length && b[j] < id) j++;
                if (j >= b.Length || b[j] != id) continue;
            }
            if (id >= n || (dead[id >> 6] & (1UL << id)) != 0) continue;
            if (!names[id].AsSpan().Contains(lit, StringComparison.Ordinal)) continue;
            if (q.Facet is not null && !q.Facet(_facets[id])) continue;
            count++;
        }
        return count;
    }

    int Scan(StudyQuery q, int n, string[] names, ulong[] dead)
    {
        var m = NameMatcher.Create(q.Expression, q.Method, true, SearchTokenization.FileMask);
        int c = 0;
        for (int e = 0; e < n; e++)
        {
            if ((dead[e >> 6] & (1UL << e)) != 0) continue;
            var s = names[e].AsSpan(1, names[e].Length - 2);
            if (m.IsMatch(s) && (q.Facet is null || q.Facet(_facets[e]))) c++;
        }
        return c;
    }
}

/// <summary>(a) The package's IndexUpdater behind the same surface.</summary>
public sealed class UpdaterStudyEngine : IStudyEngine
{
    readonly IndexUpdater _updater;
    readonly SearchEngineSharp _engine;
    int _next;

    public UpdaterStudyEngine(Dictionary<int, IndexedEntry> entries)
    {
        var provider = new IndexSnapshotProvider();
        _updater = new IndexUpdater(provider, SearchTokenization.FileMask);
        _updater.RebuildFrom(entries);
        _engine = new SearchEngineSharp(provider);
        _next = entries.Count == 0 ? 0 : entries.Keys.Max() + 1;
    }

    public string Kind => "updater";
    static IndexedEntry E(string n, NameFacets f) => new(n, n, FacetValues.Create([new("size", f.Size), new("modified", f.Modified), new("attrs", f.Attrs)]));
    public int Add(string name, NameFacets facets) { int id = _next++; _updater.AddEntry(id, E(name, facets)); return id; }
    public void Remove(int id) => _updater.RemoveEntry(id);
    public int Rename(int id, string newName) { _updater.AddEntry(id, E(newName, default)); return id; }
    public int Count(StudyQuery q, int threads)
    {
        FacetFilter? f = q.Name switch
        {
            "pdf+size>1MB" => FacetFilter.Range("size", 1 << 20, long.MaxValue),
            _ => null,
        };
        return _engine.CountMatches(q.Expression, q.Method, q.Method != WordMatchMethod.Regex, f);
    }
}

static class FastChangingStudy
{
    public static readonly StudyQuery[] Queries =
    [
        new("exact", "package.json", WordMatchMethod.Exact, "\0package.json\0"),
        new("infix *report*", "*report*", WordMatchMethod.Exact, "report"),
        new("suffix *.pdf", "*.pdf", WordMatchMethod.Exact, ".pdf\0"),
        new("prefix img_*", "img_*", WordMatchMethod.Exact, "\0img_"),
        new("OR", "*.jpg OR *.png", WordMatchMethod.Exact, null),
        new("AND NOT", "*.cs AND NOT *tests*", WordMatchMethod.Exact, null),
        new("pdf+size>1MB", "*.pdf", WordMatchMethod.Exact, ".pdf\0", f => f.Size >= 1 << 20),
        new("regex", @"\.(jpg|png)$", WordMatchMethod.Regex, null),
    ];

    public static void Run(Args a)
    {
        int n = a.Int("n", 100_000);
        string kind = a.Str("engine");
        int maxThreads = a.Int("threads", 1);
        var (_, files) = Corpus.Generate(n + 20_000, a.Int("seed", 2026));
        var res = new Dictionary<string, object?> { ["mode"] = "study", ["engine"] = kind, ["n"] = n, ["label"] = a.Opt("label") };
        static NameFacets F(SynthFile f) => new(f.Size, f.ModifiedTicks, f.Name.StartsWith('.') ? 2 : 128);

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long heap0 = GC.GetTotalMemory(true);
        var g0 = Proc.GcNow();
        var sw = Stopwatch.StartNew();
        IStudyEngine engine;
        if (kind == "updater")
        {
            var d = new Dictionary<int, IndexedEntry>(n);
            for (int i = 0; i < n; i++) d[i] = new(files[i].Name, files[i].Name, FacetValues.Create([new("size", files[i].Size), new("modified", files[i].ModifiedTicks), new("attrs", F(files[i]).Attrs)]));
            engine = new UpdaterStudyEngine(d);
        }
        else
        {
            engine = kind == "flat" ? new FlatNameBuffer(n) : new TrigramNameIndex(n);
            for (int i = 0; i < n; i++) engine.Add(files[i].Name, F(files[i]));
        }
        res["buildMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        var g1 = Proc.GcNow();
        res["buildAllocMB"] = Math.Round((g1.Alloc - g0.Alloc) / 1048576.0, 1);
        res["bytesPerEntry"] = Math.Round((GC.GetTotalMemory(true) - heap0) / (double)n, 1);

        // Query latency (median), single thread and, for flat, partitioned over threads.
        foreach (var q in Queries)
        {
            foreach (int t in maxThreads > 1 ? new[] { 1, maxThreads } : new[] { 1 })
            {
                if (t > 1 && kind != "flat") continue;
                var m = Bench.Measure(() => engine.Count(q, t), budgetMs: 400, maxIters: 500);
                res[$"q[{q.Name}]t{t}_us"] = m.MedUs;
                res[$"q[{q.Name}]_hits"] = m.Result;
            }
        }

        // Updates: single add / remove / rename latency.
        var lat = new List<double>();
        int next = n;
        var ids = new List<int>();
        for (int i = 0; i < 1000; i++) { long t0 = Stopwatch.GetTimestamp(); ids.Add(engine.Add(files[next++].Name, F(files[next - 1]))); lat.Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds); }
        res["add_med_us"] = Med(lat); res["add_p99_us"] = P99(lat); lat.Clear();
        for (int i = 0; i < 1000; i++) { long t0 = Stopwatch.GetTimestamp(); engine.Remove(i * 7 % n); lat.Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds); }
        res["remove_med_us"] = Med(lat); res["remove_p99_us"] = P99(lat); lat.Clear();
        for (int i = 0; i < 1000; i++) { long t0 = Stopwatch.GetTimestamp(); engine.Rename(ids[i], "renamed-" + i + ".pdf"); lat.Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds); }
        res["rename_med_us"] = Med(lat); res["rename_p99_us"] = P99(lat); lat.Clear();

        // Burst of 10,000 adds.
        sw.Restart();
        for (int i = 0; i < 10_000 && next < files.Length; i++) engine.Add(files[next++].Name, F(files[next - 1]));
        res["burst10k_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);

        // Steady 1,000 updates per second for 3 s while a reader loops "*.pdf" queries.
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        int updates = 0;
        var writer = Task.Run(() =>
        {
            var tick = Stopwatch.StartNew();
            while (!stop.IsCancellationRequested)
            {
                long due = (long)(tick.Elapsed.TotalMilliseconds); // one update per millisecond
                while (updates < due && !stop.IsCancellationRequested)
                {
                    if (updates % 2 == 0) engine.Add("live-" + updates + ".pdf", default); else engine.Remove(updates % n);
                    updates++;
                }
                Thread.Sleep(1);
            }
        });
        var rl = new List<double>();
        var q0 = Queries[2];
        while (!stop.IsCancellationRequested) { long t0 = Stopwatch.GetTimestamp(); engine.Count(q0, 1); rl.Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds); }
        writer.Wait();
        res["steady_updates_per_s"] = Math.Round(updates / 3.0);
        res["steady_reader_med_us"] = Med(rl); res["steady_reader_p99_us"] = P99(rl); res["steady_reader_queries"] = rl.Count;
        if (engine is FlatNameBuffer fb) { sw.Restart(); fb.Compact(); res["compact_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1); res["compactions"] = fb.Compactions; }
        var g2 = Proc.GcNow();
        res["gc0"] = g2.G0 - g0.G0; res["gc1"] = g2.G1 - g0.G1; res["gc2"] = g2.G2 - g0.G2;
        res["peakRssMB"] = Math.Round(Proc.PeakRss() / 1048576.0, 1);
        Console.WriteLine(JsonSerializer.Serialize(res));
    }

    static double Med(List<double> l) { l.Sort(); return Math.Round(l[l.Count / 2], 2); }
    static double P99(List<double> l) { l.Sort(); return Math.Round(l[(int)(l.Count * 0.99)], 2); }
}
