using System.Text.Json;

namespace SearchEngine.CompetitorBenchmarks;

internal static class Program
{
    private static int Main(string[] args)
    {
        string root = FindRoot();
        string outputDir = GetArg(args, "--output") ?? Path.Combine(root, "results", Environment.MachineName.ToLowerInvariant());
        string implementation = GetArg(args, "--implementation") ?? "sharp-current";
        bool supportsFacet = !args.Contains("--no-facet");
        bool supportsGlob = !args.Contains("--no-glob");
        string? gitSha = GetArg(args, "--git-sha");

        string corpusPath = CorpusExporter.ExportIfMissing(root);
        var corpus = CorpusExporter.Load(corpusPath);
        string workloadsPath = Path.Combine(root, "workloads.json");
        var config = JsonSerializer.Deserialize<WorkloadsFile>(File.ReadAllText(workloadsPath), JsonConfig.Options)
            ?? throw new InvalidOperationException("Invalid workloads.json");

        Directory.CreateDirectory(outputDir);
        var rows = BenchmarkHarness.RunSharp(implementation, corpus, config, supportsFacet, supportsGlob);

        string csvPath = Path.Combine(outputDir, $"{Sanitize(implementation)}-library-benchmark.csv");
        BenchmarkHarness.WriteCsv(csvPath, rows);
        BenchmarkHarness.WriteEnvironment(Path.Combine(outputDir, $"{Sanitize(implementation)}-environment.json"), implementation, gitSha);

        Console.WriteLine($"Wrote {csvPath}");
        foreach (var row in rows)
        {
            Console.WriteLine($"{row.Implementation,-24} {row.Workload,-32} hits={row.HitCount,6} median={row.MedianNs / 1000.0,10:F1} µs  ({row.Notes})");
        }

        return 0;
    }

    private static string FindRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "workloads.json")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName ?? string.Empty;
        }

        throw new InvalidOperationException("Could not locate CompetitorBenchmarks root (workloads.json).");
    }

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static string Sanitize(string value)
        => value.Replace('/', '-').Replace('\\', '-').Replace(':', '-');
}
