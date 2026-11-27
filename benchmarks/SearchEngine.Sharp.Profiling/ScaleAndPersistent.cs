using System.Diagnostics;
using System.Text.Json;
using SearchEngine.Filters;
using SearchEngine.Sharp.Tests;
using SearchEngine.Snapshots;

namespace SearchEngine.Profiling;

/// <summary>Build time, bytes per entry, GC behaviour and peak RSS at one N (run one process per N).</summary>
static class Scale
{
    public static void Run(Args a)
    {
        int n = a.Int("n", 100_000);
        bool facets = a.Has("facets");
        var preset = a.Opt("preset") == "Default" ? SearchTokenization.Default : SearchTokenization.FileMask;
        if (a.Int("unicode", 0) is var u and not 0) preset = preset.WithUnicodeMatching((UnicodeMatching)u);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long heap0 = GC.GetTotalMemory(true);
        var entries = Bench.Entries(n, a.Int("seed", 2026), facets);
        long heapEntries = GC.GetTotalMemory(true) - heap0;

        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider, preset);
        var g0 = Proc.GcNow();
        var sw = Stopwatch.StartNew();
        updater.RebuildFrom(entries);
        double buildMs = sw.Elapsed.TotalMilliseconds;
        var g1 = Proc.GcNow();
        long heapAfter = GC.GetTotalMemory(true) - heap0;
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        var full = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        // updater keeps its own Dictionary copy of the entries; snapshot = rest.
        var engine = new SearchEngineSharp(provider);
        sw.Restart();
        int hits = engine.Find("*.pdf", WordMatchMethod.Exact, true).Count;
        double firstQueryMs = sw.Elapsed.TotalMilliseconds;

        // Second rebuild over the same entries: the steady-state cost of one O(N) mutation.
        var g2 = Proc.GcNow();
        sw.Restart();
        updater.AddEntry(n + 1, new IndexedEntry("new-file.txt", "new-file.txt", facets ? FacetValues.Create("size", 1) : null));
        double addEntryMs = sw.Elapsed.TotalMilliseconds;
        var g3 = Proc.GcNow();

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "scale", label = a.Opt("label"), unicode = a.Int("unicode", 0), n, preset = a.Opt("preset") ?? "FileMask", facets,
            buildMs = Math.Round(buildMs, 1),
            buildAllocMB = Math.Round((g1.Alloc - g0.Alloc) / 1048576.0, 1),
            gc0 = g1.G0 - g0.G0, gc1 = g1.G1 - g0.G1, gc2 = g1.G2 - g0.G2,
            gcPauseMs = Math.Round((g1.Pause - g0.Pause).TotalMilliseconds, 1),
            lastFullGcPauseMs = full.PauseDurations.Length > 0 ? Math.Round(full.PauseDurations[0].TotalMilliseconds, 1) : 0,
            entriesMB = Math.Round(heapEntries / 1048576.0, 1),
            heapWithIndexMB = Math.Round(heapAfter / 1048576.0, 1),
            indexBytesPerEntry = Math.Round((heapAfter - heapEntries) / (double)n, 1),
            lohMB = Math.Round(info.GenerationInfo[3].SizeAfterBytes / 1048576.0, 1),
            words = provider.Current.UniqueWordCount,
            firstQueryMs = Math.Round(firstQueryMs, 2), hits,
            addEntryMs = Math.Round(addEntryMs, 1),
            addEntryAllocMB = Math.Round((g3.Alloc - g2.Alloc) / 1048576.0, 1),
            addEntryGc2 = g3.G2 - g2.G2,
            peakRssMB = Math.Round(Proc.PeakRss() / 1048576.0, 1),
        }));
    }
}

/// <summary>Persistent index: build, query and the cost of keeping it current through IndexUpdater.</summary>
static class Persistent
{
    public static void Run(Args a)
    {
        int n = a.Int("n", 100_000);
        int mutations = a.Int("mutations", 200);
        var entries = Bench.Entries(n, a.Int("seed", 2026), facets: true);
        var tok = SearchTokenization.FileMask;
        var filter = FacetFilter.Mask("attrs", 0, (long)FileAttributes.Directory);
        var rng = new Random(7);
        var res = new Dictionary<string, object?> { ["mode"] = "persistent", ["label"] = a.Opt("label"), ["n"] = n };

        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider, tok);
        var sw = Stopwatch.StartNew();
        updater.RebuildFrom(entries);
        res["buildMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        var engine = new SearchEngineSharp(provider);
        res["query_suffix_us"] = Bench.Measure(() => engine.Find("*.pdf", WordMatchMethod.Exact, true, SearchSortMode.SnapshotOrder, filter).Count).MedUs;
        res["query_exact_us"] = Bench.Measure(() => engine.Find("package.json", WordMatchMethod.Exact, true, SearchSortMode.SnapshotOrder, filter).Count).MedUs;
        res["query_infix_us"] = Bench.Measure(() => engine.Find("*report*", WordMatchMethod.Exact, true, SearchSortMode.SnapshotOrder, filter).Count).MedUs;

        var one = new IndexedEntry("brand-new-file.pdf", "brand-new-file.pdf", FacetValues.Create("attrs", 0));
        res["addEntry_ms"] = MedianMs(3, i => updater.AddEntry(n + 10 + i, one));
        res["refreshEntry_ms"] = MedianMs(3, i => updater.RefreshEntry(i, one));
        res["removeEntry_ms"] = MedianMs(3, i => updater.RemoveEntry(n + 10 + i));
        res["batch100_ms"] = MedianMs(3, i => updater.AddOrUpdateEntries(Enumerable.Range(n + 1000 + i * 100, 100).Select(id => KeyValuePair.Create(id, one))));

        // A stream of single changes (add, replace, remove), merges included: the updater rebuilds the base when its
        // delta and dead ordinals pass IndexUpdater.MergeThreshold.
        var times = new List<double>();
        var total = Stopwatch.StartNew();
        for (int i = 0; i < mutations; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            int kind = rng.Next(3);
            if (kind == 0) updater.AddEntry(n + 100_000 + i, one);
            else if (kind == 1) updater.RefreshEntry(rng.Next(n), one);
            else updater.RemoveEntry(rng.Next(n));
            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        times.Sort();
        res["mutation_median_ms"] = Math.Round(times[times.Count / 2], 3);
        res["mutation_p99_ms"] = Math.Round(times[(int)(times.Count * 0.99)], 2);
        res["mutation_mean_ms"] = Math.Round(total.Elapsed.TotalMilliseconds / mutations, 3);
        res["mutation_query_suffix_us"] = Bench.Measure(() => engine.Find("*.pdf", WordMatchMethod.Exact, true, SearchSortMode.SnapshotOrder, filter).Count).MedUs;
        res["mutation_query_exact_us"] = Bench.Measure(() => engine.Find("package.json", WordMatchMethod.Exact, true, SearchSortMode.SnapshotOrder, filter).Count).MedUs;
        Console.WriteLine(JsonSerializer.Serialize(res));
    }

    static double MedianMs(int reps, Action<int> f)
    {
        var t = new List<double>();
        for (int i = 0; i < reps; i++) { var sw = Stopwatch.StartNew(); f(i); t.Add(sw.Elapsed.TotalMilliseconds); }
        t.Sort();
        return Math.Round(t[t.Count / 2], 1);
    }
}
