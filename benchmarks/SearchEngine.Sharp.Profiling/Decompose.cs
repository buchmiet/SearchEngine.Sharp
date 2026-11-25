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
        res["fseNamesOnlyMs"] = Best(reps, () => new FileSystemEnumerable<int>(root, (ref FileSystemEntry e) => e.FileName.Length, fseOpts) { ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory, ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0 }.Sum(x => (long)x));
        res["fseWithMetadataMs"] = Best(reps, () => new FileSystemEnumerable<long>(root, (ref FileSystemEntry e) => e.Length ^ e.LastWriteTimeUtc.UtcTicks ^ (long)e.Attributes, fseOpts) { ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory, ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0 }.Aggregate(0L, (x, y) => x ^ y));
        res["fsePathsWithMetadataMs"] = Best(reps, () => new FileSystemEnumerable<string>(root, (ref FileSystemEntry e) => { _ = e.Length ^ e.LastWriteTimeUtc.UtcTicks ^ (long)e.Attributes; return e.ToFullPath(); }, fseOpts) { ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory, ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0 }.LongCount());

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
            var provider = new Bench.Fixed(snap);
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

        // One full FileMask build of all N entries through the real builder.
        foreach (var (k, v) in BuildCost.Measure(indexed)) res["full_" + k] = v;
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
}

/// <summary>
/// Cost of one full FileMask build, measured by calling the real <see cref="IndexSnapshotBuilder"/>: build time and
/// allocations, the tokenization pass alone, and the first Within query, which pays for the lazy bigram index.
/// </summary>
static class BuildCost
{
    public static Dictionary<string, double> Measure(IReadOnlyList<KeyValuePair<int, IndexedEntry>> entries)
    {
        var tok = SearchTokenization.FileMask;
        var t = new Dictionary<string, double>();

        var unique = new HashSet<string>(StringComparer.Ordinal);
        var separators = System.Buffers.SearchValues.Create(tok.IndexSeparators);
        var sw = Stopwatch.StartNew();
        int words = 0;
        foreach (var (_, e) in entries)
            TextNormalizer.ForEachUniqueWord(e.SearchText, unique, separators, ref words, static (ref int n, string _) => n++);
        t["tokenizeOnlyMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);

        long a0 = GC.GetTotalAllocatedBytes(true);
        sw.Restart();
        var snapshot = IndexSnapshotBuilder.Build(entries, tok);
        t["buildMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        t["allocMB"] = Math.Round((GC.GetTotalAllocatedBytes(true) - a0) / 1048576.0, 1);
        t["uniqueWords"] = snapshot.UniqueWordCount;

        var engine = new SearchEngineSharp(new Bench.Fixed(snapshot));
        sw.Restart();
        engine.Find("ab", WordMatchMethod.Within);
        t["firstWithinMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        sw.Restart();
        engine.Find("ab", WordMatchMethod.Within);
        t["secondWithinMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        return t;
    }
}
