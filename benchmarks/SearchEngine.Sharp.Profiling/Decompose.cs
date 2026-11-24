using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text.Json;
using SearchEngine.Filters;
using SearchEngine.Snapshots;
using SearchEngine.Tokenizer;

namespace SearchEngine.Profiling;

/// <summary>
/// Sequential, phase-by-phase cost of one Commander-style search on a tree (warm cache):
/// enumeration, Directory.Exists, FileInfo stat, entry/facet construction, the snapshot builds of the
/// progressive publication series, the requery after each publication and de-duplication.
/// Also splits one full snapshot build into its internal phases (copy of IndexSnapshotBuilder logic).
/// </summary>
static class Decompose
{
    public static void Run(Args a)
    {
        string root = Path.GetFullPath(a.Str("root"));
        var req = new FindRequest(root, a.Opt("query"), a.Has("regex"), a.Long("size-min"), a.Long("size-max"),
            a.Opt("days") is { } d ? int.Parse(d) : null);
        var res = new Dictionary<string, object?> { ["mode"] = "decompose", ["label"] = a.Opt("label"), ["query"] = req.NamePattern };

        // Discover structure once (also warms the cache).
        var dirs = new List<string>();
        var files = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            dirs.Add(dir);
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); } catch { continue; }
            foreach (var e in entries)
                if (Directory.Exists(e)) stack.Push(e); else files.Add(e);
        }
        res["files"] = files.Count;
        res["dirs"] = dirs.Count;

        int reps = a.Int("reps", 3);
        res["enumOnlyMs"] = Best(reps, () => { long n = 0; foreach (var dir in dirs) { try { foreach (var _ in Directory.EnumerateFileSystemEntries(dir)) n++; } catch { } } return n; });
        res["existsAllEntriesMs"] = Best(reps, () => { long n = 0; foreach (var f in files) if (Directory.Exists(f)) n++; foreach (var dd in dirs) if (Directory.Exists(dd)) n++; return n; });
        res["fileInfoStatMs"] = Best(reps, () => { long n = 0; foreach (var f in files) { try { var fi = new FileInfo(f); n ^= fi.Length ^ fi.LastWriteTimeUtc.Ticks ^ (long)fi.Attributes ^ fi.Name.Length; } catch { } } return n; });
        var fseOpts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        res["fseNamesOnlyMs"] = Best(reps, () => new FileSystemEnumerable<int>(root, (ref FileSystemEntry e) => e.FileName.Length, fseOpts) { ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory }.Sum(x => (long)x));
        res["fseWithMetadataMs"] = Best(reps, () => new FileSystemEnumerable<long>(root, (ref FileSystemEntry e) => e.Length ^ e.LastWriteTimeUtc.UtcTicks ^ (long)e.Attributes, fseOpts) { ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory }.Aggregate(0L, (x, y) => x ^ y));
        res["fsePathsWithMetadataMs"] = Best(reps, () => new FileSystemEnumerable<string>(root, (ref FileSystemEntry e) => { _ = e.Length ^ e.LastWriteTimeUtc.UtcTicks ^ (long)e.Attributes; return e.ToFullPath(); }, fseOpts) { ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory }.LongCount());

        // Entry construction exactly as Commander (FacetValues dictionary per file).
        var stats = files.Select(f => { var fi = new FileInfo(f); try { return (Name: fi.Name, Length: fi.Length, Item3: fi.LastWriteTimeUtc.Ticks, Item4: (long)fi.Attributes); } catch { return (Name: fi.Name, Length: 0L, Item3: 0L, Item4: 0L); } }).ToArray();
        long al0 = GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        var indexed = new KeyValuePair<int, IndexedEntry>[stats.Length];
        for (int i = 0; i < stats.Length; i++)
        {
            var s = stats[i];
            var facets = FacetValues.FromDictionary(new Dictionary<string, long> { ["size"] = s.Length, ["modified"] = s.Item3, ["attrs"] = s.Item4 });
            indexed[i] = new(i, new IndexedEntry(s.Name, s.Name, facets));
        }
        res["entryConstructionMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        res["entryConstructionAllocMB"] = Math.Round((GC.GetTotalAllocatedBytes(true) - al0) / 1048576.0, 1);

        // Publication series of the default Adaptive policy on a fast scan: 2k, 4k, 8k, ..., N.
        var series = new List<int>();
        for (int n = 2_000; n < indexed.Length; n *= 2) series.Add(n);
        series.Add(indexed.Length);
        var (expr, method, ops) = CommanderPattern.BuildQuery(req);
        var filter = CommanderPattern.BuildFacetFilter(req);
        double buildTotal = 0, queryTotal = 0, dedupTotal = 0;
        long buildAlloc = 0;
        var buildEach = new List<string>();
        var queryEach = new List<string>();
        var yielded = new HashSet<int>();
        var dict = new Dictionary<int, IndexedEntry>();
        int prev = 0;
        foreach (int n in series)
        {
            for (int i = prev; i < n; i++) dict[indexed[i].Key] = indexed[i].Value;
            prev = n;
            long b0 = GC.GetTotalAllocatedBytes(true);
            sw.Restart();
            var snap = IndexSnapshotBuilder.Build(dict, SearchTokenization.FileMask);
            double bms = sw.Elapsed.TotalMilliseconds;
            buildAlloc += GC.GetTotalAllocatedBytes(true) - b0;
            buildTotal += bms;
            buildEach.Add(bms.ToString("F1"));
            var provider = new Fixed(snap);
            var engine = new SearchEngineSharp(provider);
            sw.Restart();
            var ids = engine.Find(expr, method, ops, SearchSortMode.SnapshotOrder, filter);
            double qms = sw.Elapsed.TotalMilliseconds;
            queryTotal += qms;
            queryEach.Add(qms.ToString("F2"));
            sw.Restart();
            foreach (var id in ids) if (yielded.Add(id)) _ = Path.GetFileName(files[id]);
            dedupTotal += sw.Elapsed.TotalMilliseconds;
        }
        res["publishSeries"] = string.Join(",", series);
        res["buildsMs"] = Math.Round(buildTotal, 1);
        res["buildsAllocMB"] = Math.Round(buildAlloc / 1048576.0, 1);
        res["buildEachMs"] = string.Join(",", buildEach);
        res["requeriesMs"] = Math.Round(queryTotal, 2);
        res["queryEachMs"] = string.Join(",", queryEach);
        res["dedupMs"] = Math.Round(dedupTotal, 2);
        res["hits"] = yielded.Count;

        // Internal phases of one full FileMask build of all N entries.
        foreach (var (k, v) in BuildPhases.Measure(indexed, SearchTokenization.FileMask)) res["full_" + k] = v;
        Console.WriteLine(JsonSerializer.Serialize(res));
    }

    static double Best(int reps, Func<long> f)
    {
        double best = double.MaxValue;
        for (int i = 0; i < reps; i++)
        {
            var sw = Stopwatch.StartNew();
            f();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return Math.Round(best, 1);
    }

    sealed class Fixed(IndexSnapshot s) : IIndexSnapshotProvider { public IndexSnapshot Current => s; }
}

/// <summary>
/// Copy of IndexSnapshotBuilder.BuildCore (0.5.6) with a timer per phase. Produces the same arrays
/// but does not construct IndexSnapshot (internal constructor). Used only to split build cost.
/// </summary>
static class BuildPhases
{
    public static Dictionary<string, double> Measure(IReadOnlyList<KeyValuePair<int, IndexedEntry>> entries, SearchTokenization tok)
    {
        var t = new Dictionary<string, double>();
        var sw = Stopwatch.StartNew();
        long a0 = GC.GetTotalAllocatedBytes(true);
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        var inverted = new Dictionary<string, List<int>>(entries.Count, StringComparer.Ordinal);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sortTexts = new List<string>(entries.Count);
        var facetKeys = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<(int, string, long)>();
        int totalLen = 0, totalPostings = 0;
        double tokenizeMs = 0;
        var separators = TokIndex(tok);
        for (int doc = 0; doc < entries.Count; doc++)
        {
            var e = entries[doc].Value;
            sortTexts.Add(e.SortText);
            var words = new List<string>(2);
            long ts = Stopwatch.GetTimestamp();
            TextNormalizer.ForEachUniqueWord(e.SearchText, unique, separators, ref words, static (ref List<string> l, string w) => l.Add(w));
            tokenizeMs += Stopwatch.GetElapsedTime(ts).TotalMilliseconds;
            foreach (var w0 in words)
            {
                string w = pool.TryGetValue(w0, out var ex) ? ex : (pool[w0] = w0);
                ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(inverted, w, out bool exists);
                if (!exists) { list = [doc]; totalLen += w.Length; } else list!.Add(doc);
                totalPostings++;
            }
            if (e.Facets is { } f)
                foreach (var name in new[] { "size", "modified", "attrs" }) { facetKeys.Add(name); pending.Add((doc, name, 0)); }
        }
        t["phase1_invertedMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        t["phase1_tokenizeOnlyMs"] = Math.Round(tokenizeMs, 1);
        sw.Restart();
        var desc = inverted.Select(kv => (Word: kv.Key, Ids: kv.Value)).ToList();
        CollectionsMarshal.AsSpan(desc).Sort(static (x, y) => y.Word.Length.CompareTo(x.Word.Length));
        t["phase2_sortMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        sw.Restart();
        var words2 = new char[totalLen];
        var postings = new int[totalPostings];
        var exact = new Dictionary<string, (int, int)>(desc.Count, StringComparer.Ordinal);
        int wo = 0, po = 0;
        foreach (var (w, ids) in desc)
        {
            w.AsSpan().CopyTo(words2.AsSpan(wo));
            exact[w] = (po, ids.Count);
            foreach (var id in ids) postings[po++] = id;
            wo += w.Length;
        }
        t["phase3_packMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        sw.Restart();
        var bigrams = new Dictionary<int, List<int>>();
        for (int i = 0; i < desc.Count; i++)
        {
            var w = desc[i].Word;
            int prev = -1;
            for (int j = 0; j + 1 < w.Length; j++)
            {
                int bg = (w[j] << 16) | w[j + 1];
                if (bg == prev) continue;
                prev = bg;
                if (!bigrams.TryGetValue(bg, out var l)) bigrams[bg] = l = [];
                if (l.Count == 0 || l[^1] != i) l.Add(i);
            }
        }
        var bigramArrays = bigrams.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        t["phase4_bigramMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        sw.Restart();
        var cols = facetKeys.ToDictionary(k => k, _ => new long[entries.Count]);
        foreach (var (doc, name, v) in pending) cols[name][doc] = v;
        t["phase5_facetsMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        t["allocMB"] = Math.Round((GC.GetTotalAllocatedBytes(true) - a0) / 1048576.0, 1);
        t["uniqueWords"] = desc.Count;
        t["bigramKeys"] = bigramArrays.Count;
        t["bigramPostings"] = bigramArrays.Sum(kv => (double)kv.Value.Length);
        GC.KeepAlive(words2); GC.KeepAlive(postings); GC.KeepAlive(exact);
        return t;
    }

    static System.Buffers.SearchValues<char> TokIndex(SearchTokenization tok) => System.Buffers.SearchValues.Create(tok.IndexSeparators);
}
