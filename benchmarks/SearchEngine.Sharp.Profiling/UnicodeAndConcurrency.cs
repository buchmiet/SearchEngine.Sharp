using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SearchEngine.DependencyInjection;
using SearchEngine.Filters;
using SearchEngine.Ingestion;
using SearchEngine.Sharp.Tests;
using SearchEngine.Snapshots;

namespace SearchEngine.Profiling;

/// <summary>
/// Unicode behaviour: names written to the file system in NFC or NFD, read back by enumeration,
/// indexed with both presets and searched with queries typed in NFC, NFD or another case form.
/// </summary>
static class UnicodeProbe
{
    // (stem as stored on disk, form on disk, query, what the user expects)
    static readonly (string Stem, NormalizationForm? Disk, string Query, string Case)[] Cases =
    [
        ("café", NormalizationForm.FormD, "café", "NFD on disk, NFC query"),
        ("résumé", NormalizationForm.FormC, "résumé", "NFC on disk, NFD query"),
        ("Zażółć", NormalizationForm.FormD, "zażółć", "Polish NFD on disk, NFC query"),
        ("Straße", NormalizationForm.FormC, "strasse", "sharp s vs ss"),
        ("STRASSE", NormalizationForm.FormC, "straße", "ss vs sharp s"),
        ("İstanbul", NormalizationForm.FormC, "istanbul", "Turkish dotted capital I"),
        ("ıssız", NormalizationForm.FormC, "issiz", "Turkish dotless i (expected miss)"),
        ("Οδυσσευς", NormalizationForm.FormC, "ΟΔΥΣΣΕΥΣ", "Greek final sigma, upper query"),
        ("한국어", NormalizationForm.FormD, "한국어", "Hangul syllables stored as jamo (NFD)"),
        ("cafe", NormalizationForm.FormC, "café", "accent folding (expected miss without it)"),
        ("👨‍👩‍👧family", NormalizationForm.FormC, "👨‍👩‍👧family", "emoji ZWJ sequence"),
        ("𝔘𝔫𝔦𝔠𝔬𝔡𝔢", NormalizationForm.FormC, "𝔘𝔫𝔦𝔠𝔬𝔡𝔢", "surrogate pairs (math fraktur)"),
        ("ǅemal", NormalizationForm.FormC, "ǆemal", "titlecase digraph"),
        ("ﬁle", NormalizationForm.FormC, "file", "ligature fi (compatibility, expected miss)"),
    ];

    /// <summary>The only directory the mode touches: a fresh subdirectory of <c>--dir</c>.</summary>
    public const string ProbeDirectoryName = "leith-unicode-probe";

    public static void Run(Args a)
    {
        string dir = Path.Combine(Path.GetFullPath(a.Str("dir")), ProbeDirectoryName);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new InvalidOperationException($"'{dir}' already exists and is not empty; remove it or choose another --dir.");

        // The directory is empty or created here, so deleting it removes only what this run wrote.
        Directory.CreateDirectory(dir);
        try
        {
            Probe(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    static void Probe(string dir)
    {
        foreach (var c in Cases)
        {
            string name = (c.Disk is { } form ? c.Stem.Normalize(form) : c.Stem) + ".txt";
            File.WriteAllBytes(Path.Combine(dir, name), []);
        }

        var onDisk = Directory.EnumerateFiles(dir).Select(Path.GetFileName).ToList();
        var entries = new Dictionary<int, IndexedEntry>();
        for (int i = 0; i < onDisk.Count; i++) entries[i] = new IndexedEntry(onDisk[i]!, onDisk[i]!);
        var def = new SearchEngineSharp(new FixedSnapshotProvider(IndexSnapshotBuilder.Build(entries, SearchTokenization.Default)));
        var fm = new SearchEngineSharp(new FixedSnapshotProvider(IndexSnapshotBuilder.Build(entries, SearchTokenization.FileMask)));

        foreach (var c in Cases)
        {
            string expectedNfc = (c.Stem + ".txt").Normalize(NormalizationForm.FormC);
            int idx = onDisk.FindIndex(n => n!.Normalize(NormalizationForm.FormC) == expectedNfc);
            string? stored = idx >= 0 ? onDisk[idx] : null;
            bool Hit(List<int> ids) => idx >= 0 && ids.Contains(idx);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                mode = "unicode",
                @case = c.Case,
                diskFormRequested = c.Disk?.ToString(),
                storedIsNfc = stored?.IsNormalized(NormalizationForm.FormC),
                storedIsNfd = stored?.IsNormalized(NormalizationForm.FormD),
                storedUtf16Len = stored?.Length,
                exactDefault = Hit(def.Find(c.Query, WordMatchMethod.Exact)),
                withinDefault = Hit(def.Find(c.Query, WordMatchMethod.Within)),
                globFileMask = Hit(fm.Find(c.Query + "*", WordMatchMethod.Exact, true)),
                exactFileMask = Hit(fm.Find(c.Query + ".txt", WordMatchMethod.Exact, true)),
                regexFileMask = Hit(fm.Find("^" + System.Text.RegularExpressions.Regex.Escape(c.Query), WordMatchMethod.Regex)),
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }

        // Grapheme boundary: substring matches that split a grapheme cluster.
        var g = new Dictionary<int, IndexedEntry>
        {
            [0] = new("café", "x"),            // NFD é
            [1] = new("👨‍👩‍👧", "x"),               // ZWJ family
            [2] = new("ıx ix", "x"),                 // both tokens in one document
        };
        var ge = new SearchEngineSharp(new FixedSnapshotProvider(IndexSnapshotBuilder.Build(g, SearchTokenization.Default)));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "unicode-grapheme",
            withinE_matchesNfdE_doc0 = ge.Find("cafe", WordMatchMethod.Within).Contains(0),
            withinMan_matchesInsideFamily_doc1 = ge.Find("👨", WordMatchMethod.Within).Contains(1),
            exactIx_foundInDocWithBothDotlessAndDotted_doc2 = ge.Find("ix", WordMatchMethod.Exact).Contains(2),
            exactDotlessIx_doc2 = ge.Find("ıx", WordMatchMethod.Exact).Contains(2),
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}

/// <summary>Concurrent queries during updates, cancellation of ingestion, DI start-up, regex cache contention.</summary>
static class Concurrency
{
    public static void Run(Args a)
    {
        int n = a.Int("n", 100_000);
        var entries = Bench.Entries(n, 2026, facets: true);
        var res = new Dictionary<string, object?> { ["mode"] = "concurrency", ["n"] = n };
        var filter = FacetFilter.Mask("attrs", 0, (long)FileAttributes.Directory);

        // 1. Readers while a writer keeps rebuilding.
        var provider = new IndexSnapshotProvider();
        var updater = new IndexUpdater(provider, SearchTokenization.FileMask);
        updater.RebuildFrom(entries);
        var engine = new SearchEngineSharp(provider);
        int readers = a.Int("readers", 4);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(a.Int("seconds", 5)));
        long errors = 0, queries = 0, rebuilds = 0;
        var lat = new List<double>[readers];
        var threads = new List<Thread>();
        for (int r = 0; r < readers; r++)
        {
            int rr = r;
            lat[r] = [];
            var t = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    try
                    {
                        var ids = engine.Find(rr % 2 == 0 ? "*.pdf" : "*report*", WordMatchMethod.Exact, true, SearchSortMode.SnapshotOrder, filter);
                        if (ids.Count == 0) Interlocked.Increment(ref errors);
                    }
                    catch { Interlocked.Increment(ref errors); }
                    lat[rr].Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
                    Interlocked.Increment(ref queries);
                }
            });
            t.Start();
            threads.Add(t);
        }
        var writer = new Thread(() =>
        {
            int i = 0;
            while (!stop.IsCancellationRequested)
            {
                updater.AddEntry(n + 1 + (i++ % 10), new IndexedEntry("concurrent.pdf", "concurrent.pdf", FacetValues.Create("attrs", 0)));
                Interlocked.Increment(ref rebuilds);
            }
        });
        writer.Start();
        threads.ForEach(t => t.Join());
        writer.Join();
        var all = lat.SelectMany(x => x).OrderBy(x => x).ToArray();
        res["rw_queries"] = queries;
        res["rw_rebuilds"] = rebuilds;
        res["rw_errors"] = errors;
        res["rw_p50_us"] = Math.Round(all[all.Length / 2], 1);
        res["rw_p99_us"] = Math.Round(all[(int)(all.Length * 0.99)], 1);
        res["rw_max_us"] = Math.Round(all[^1], 1);

        // Baseline: same readers without the writer.
        using (var stop2 = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            var l2 = new List<double>();
            while (!stop2.IsCancellationRequested)
            {
                long t0 = Stopwatch.GetTimestamp();
                engine.Find("*.pdf", WordMatchMethod.Exact, true, SearchSortMode.SnapshotOrder, filter);
                l2.Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
            }
            l2.Sort();
            res["solo_p50_us"] = Math.Round(l2[l2.Count / 2], 1);
            res["solo_p99_us"] = Math.Round(l2[(int)(l2.Count * 0.99)], 1);
        }

        // 2. Regex cache: parallel readers with distinct patterns (more than the 8-entry LRU) vs one pattern.
        foreach (var distinct in new[] { 1, 16 })
        {
            var sw = Stopwatch.StartNew();
            Parallel.For(0, 4000, new ParallelOptions { MaxDegreeOfParallelism = readers }, i =>
                engine.Find(@"\.pd" + (i % distinct == 0 ? "f" : new string('f', 1) + "{1," + (i % distinct) + "}") + "$", WordMatchMethod.Regex));
            res[$"regex_parallel_{distinct}patterns_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        }

        // 3. Cancellation of progressive ingestion mid-scan.
        var p2 = new IndexSnapshotProvider();
        var u2 = new IndexUpdater(p2, SearchTokenization.FileMask);
        var ing = new ProgressiveIndexIngestion(u2);
        using var cts = new CancellationTokenSource();
        var sw2 = Stopwatch.StartNew();
        var task = ing.IngestAsync(Feed(entries, cts), cancellationToken: cts.Token);
        IngestionResult? result = null;
        string? exception = null;
        try { result = task.GetAwaiter().GetResult(); }
        catch (Exception ex) { exception = ex.GetType().Name; }
        res["cancel_elapsed_ms"] = Math.Round(sw2.Elapsed.TotalMilliseconds, 1);
        res["cancel_exception"] = exception;
        res["cancel_wasCancelled"] = result?.WasCancelled;
        res["cancel_accepted"] = result?.EntriesAccepted;
        res["cancel_published"] = result?.EntriesPublished;
        res["cancel_indexed"] = p2.Current.DocumentCount;

        // 4. DI start-up.
        var sw3 = Stopwatch.StartNew();
        var services = new ServiceCollection();
        services.AddSearchEngine(SearchTokenization.FileMask);
        using var sp = services.BuildServiceProvider();
        var e3 = sp.GetRequiredService<ISearchEngine>();
        _ = sp.GetRequiredService<IIndexUpdater>();
        res["di_first_resolve_ms"] = Math.Round(sw3.Elapsed.TotalMilliseconds, 2);
        var sw4 = Stopwatch.StartNew();
        var p4 = new IndexSnapshotProvider();
        _ = new IndexUpdater(p4, SearchTokenization.FileMask);
        _ = new SearchEngineSharp(p4);
        res["direct_construct_ms"] = Math.Round(sw4.Elapsed.TotalMilliseconds, 3);

        Console.WriteLine(JsonSerializer.Serialize(res));
    }

    static async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> Feed(Dictionary<int, IndexedEntry> entries, CancellationTokenSource cts)
    {
        int i = 0;
        foreach (var kv in entries)
        {
            if (++i == entries.Count / 2) cts.Cancel();
            if (i % 1000 == 0) await Task.Yield();
            yield return kv;
        }
    }
}
