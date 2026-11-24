using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SearchEngine.Ingestion;
using SearchEngine.Profiling;

var a = new Args(args);
string mode = args.Length > 0 ? args[0] : "help";
switch (mode)
{
    case "gen-tree":
    {
        var sw = Stopwatch.StartNew();
        var (files, dirs) = Corpus.Materialize(a.Str("root"), a.Int("count", 100_000), a.Int("seed", 2026), !a.Has("no-meta"));
        Console.WriteLine(JsonSerializer.Serialize(new { mode, files, dirs, seconds = sw.Elapsed.TotalSeconds }));
        break;
    }
    case "find":
        await FindRun.RunAsync(a);
        break;
    case "decompose":
        Decompose.Run(a);
        break;
    case "querypaths":
        QueryPaths.Run(a);
        break;
    case "scale":
        Scale.Run(a);
        break;
    case "persistent":
        Persistent.Run(a);
        break;
    case "unicode":
        UnicodeProbe.Run(a);
        break;
    case "concurrency":
        Concurrency.Run(a);
        break;
    default:
        Console.WriteLine("modes: gen-tree | find | decompose | querypaths | scale | persistent | unicode | concurrency");
        break;
}

/// <summary>Minimal --key value parser.</summary>
sealed class Args(string[] argv)
{
    public bool Has(string k) => Array.IndexOf(argv, "--" + k) >= 0;
    public string? Opt(string k) { int i = Array.IndexOf(argv, "--" + k); return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null; }
    public string Str(string k, string? def = null) => Opt(k) ?? def ?? throw new ArgumentException($"--{k} required");
    public int Int(string k, int def) => Opt(k) is { } s ? int.Parse(s) : def;
    public long? Long(string k) => Opt(k) is { } s ? long.Parse(s) : null;
}

static class Proc
{
    [DllImport("libc", SetLastError = true)]
    static extern int getrusage(int who, long[] usage);

    /// <summary>Peak resident set size of this process in bytes (getrusage ru_maxrss).</summary>
    public static long PeakRss()
    {
        var buf = new long[18];
        if (getrusage(0, buf) != 0) return -1;
        long v = buf[4];
        return OperatingSystem.IsMacOS() ? v : v * 1024;
    }

    public static (long Alloc, int G0, int G1, int G2, TimeSpan Pause) GcNow()
        => (GC.GetTotalAllocatedBytes(true), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());
}

static class FindRun
{
    public static async Task RunAsync(Args a)
    {
        string variant = a.Str("variant");
        var req = new FindRequest(
            a.Str("root"),
            a.Opt("query"),
            a.Has("regex"),
            a.Long("size-min"),
            a.Long("size-max"),
            a.Opt("days") is { } d ? int.Parse(d) : null);
        bool instrument = a.Has("instrument");
        var opts = BuildOptions(a);

        var g0 = Proc.GcNow();
        long rss0 = Proc.PeakRss();
        var sw = Stopwatch.StartNew();
        double firstHitMs = -1;
        int hits = 0;
        CommanderPattern? cp = null;
        var hitPaths = a.Opt("dump-hits") is not null || a.Has("hash") ? new List<string>() : null;

        void OnHit(string path)
        {
            if (hits++ == 0) firstHitMs = sw.Elapsed.TotalMilliseconds;
            hitPaths?.Add(path);
        }

        switch (variant)
        {
            case "A":
            case "B":
            case "E":
                cp = new CommanderPattern(variant == "A" ? null : opts, instrument, fseScan: variant == "E");
                await foreach (var (path, _) in cp.FindAsync(req)) OnHit(path);
                break;
            case "C":
                foreach (var path in StreamingScan.Find(req)) OnHit(path);
                break;
            case "F":
                foreach (var path in StreamingScan.FindWithNameMatcher(req)) OnHit(path);
                break;
            case "D":
                await foreach (var path in StreamingScan.FindParallel(req, a.Int("threads", Math.Min(8, Environment.ProcessorCount))))
                    OnHit(path);
                break;
            default:
                throw new ArgumentException(variant);
        }

        double totalMs = sw.Elapsed.TotalMilliseconds;
        var g1 = Proc.GcNow();
        string? hitsHash = null;
        if (hitPaths is not null)
        {
            hitPaths.Sort(StringComparer.Ordinal);
            if (a.Opt("dump-hits") is { } dump) File.WriteAllLines(dump, hitPaths);
            hitsHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', hitPaths))))[..16];
        }

        var result = new Dictionary<string, object?>
        {
            ["mode"] = "find",
            ["label"] = a.Opt("label"),
            ["variant"] = variant,
            ["policy"] = variant is "B" or "E" ? a.Opt("policy") ?? "adaptive" : null,
            ["batch"] = variant is "B" or "E" ? opts.FixedBatchSize : null,
            ["query"] = req.NamePattern,
            ["regex"] = req.UseRegex,
            ["hits"] = hits,
            ["hitsHash"] = hitsHash,
            ["firstHitMs"] = Math.Round(firstHitMs, 2),
            ["totalMs"] = Math.Round(totalMs, 2),
            ["allocMB"] = Math.Round((g1.Alloc - g0.Alloc) / 1048576.0, 2),
            ["gc0"] = g1.G0 - g0.G0,
            ["gc1"] = g1.G1 - g0.G1,
            ["gc2"] = g1.G2 - g0.G2,
            ["gcPauseMs"] = Math.Round((g1.Pause - g0.Pause).TotalMilliseconds, 2),
            ["peakRssMB"] = Math.Round(Proc.PeakRss() / 1048576.0, 1),
            ["startRssMB"] = Math.Round(rss0 / 1048576.0, 1),
            ["tiered"] = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
            ["os"] = RuntimeInformation.OSDescription,
            ["arch"] = RuntimeInformation.OSArchitecture.ToString(),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
        };
        if (cp is not null && instrument)
        {
            var b = cp.B;
            result["files"] = b.Files;
            result["dirs"] = b.Dirs;
            result["enumMs"] = Math.Round(Breakdown.Ms(b.EnumTicks), 1);
            result["existsMs"] = Math.Round(Breakdown.Ms(b.ExistsTicks), 1);
            result["statMs"] = Math.Round(Breakdown.Ms(b.StatTicks), 1);
            result["facetEntryMs"] = Math.Round(Breakdown.Ms(b.FacetTicks), 1);
            result["queryMs"] = Math.Round(Breakdown.Ms(b.QueryTicks), 1);
            result["queryRuns"] = b.QueryRuns;
            result["dedupMs"] = Math.Round(Breakdown.Ms(b.DedupTicks), 1);
            result["publishes"] = b.Publishes.Count;
            result["rebuildMs"] = Math.Round(b.Publishes.Sum(p => p.RebuildMs), 1);
            result["publishDocs"] = string.Join(",", b.Publishes.Select(p => p.Docs));
            result["rebuildMsEach"] = string.Join(",", b.Publishes.Select(p => p.RebuildMs.ToString("F1")));
            result["queryMsEach"] = string.Join(",", b.Queries.Select(q => q.QueryMs.ToString("F2")));
            result["queryIdsEach"] = string.Join(",", b.Queries.Select(q => q.Ids));
        }
        Console.WriteLine(JsonSerializer.Serialize(result));
    }

    static IngestPublishOptions BuildOptions(Args a)
    {
        int batch = a.Int("batch", 2_000);
        return (a.Opt("policy") ?? "adaptive") switch
        {
            "adaptive" => new IngestPublishOptions { FixedBatchSize = batch },
            "fixed" => new IngestPublishOptions { Policy = IngestPublishPolicy.FixedBatch, FixedBatchSize = batch },
            "debounce" => new IngestPublishOptions
            {
                Policy = IngestPublishPolicy.TimeDebounce, FixedBatchSize = batch,
                MinInterval = TimeSpan.FromMilliseconds(a.Int("interval", 250)),
                MaxStaleness = TimeSpan.FromMilliseconds(Math.Max(a.Int("interval", 250) * 2, 1000)),
            },
            // One publish at the end: no live results, minimum rebuild work.
            "single" => new IngestPublishOptions
            {
                Policy = IngestPublishPolicy.FixedBatch, FixedBatchSize = int.MaxValue, MaxStaleness = TimeSpan.FromDays(1),
                ChannelCapacity = 10_000,
            },
            var p => throw new ArgumentException(p),
        };
    }
}
