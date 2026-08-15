using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SearchEngine;

namespace SearchEngine.CompetitorBenchmarks;

internal static class BenchmarkHarness
{
    internal static IReadOnlyList<BenchResultRow> RunSharp(
        string implementation,
        CorpusFile corpus,
        WorkloadsFile config,
        bool supportsFacet,
        bool supportsGlob)
    {
        var entries = SharpEntries.Build(corpus, withFacets: supportsFacet);

        var rows = new List<BenchResultRow>();
        foreach (var workload in config.Workloads)
        {
            if (workload.CompetitorsOnly)
                continue;
            if (workload.RequiresFacet && !supportsFacet)
                continue;
            if (workload.Kind == "glob" && !supportsGlob)
                continue;

            string query = ResolveQuery(corpus, workload);
            rows.Add(MeasureSharp(implementation, entries, config, workload, query, supportsFacet));
        }

        return rows;
    }

    internal static IReadOnlyList<BenchResultRow> RunNaive(
        string implementation,
        CorpusFile corpus,
        WorkloadsFile config)
    {
        var rows = new List<BenchResultRow>();
        foreach (var workload in config.Workloads.Where(w => w.CompetitorsOnly))
        {
            string query = ResolveQuery(corpus, workload);
            rows.Add(MeasureNaive(implementation, corpus, config, workload, query));
        }

        return rows;
    }

    private static BenchResultRow MeasureSharp(
        string implementation,
        Dictionary<int, IndexedEntry> entries,
        WorkloadsFile config,
        WorkloadSpec workload,
        string query,
        bool supportsFacet)
    {
        var timings = new List<long>(config.MeasureIterations);
        int hitCount = 0;

        SearchEngineSharp? hotEngine = null;
        if (!workload.ColdIndex)
            hotEngine = BuildEngine(entries, workload);

        for (int i = 0; i < config.WarmupIterations + config.MeasureIterations; i++)
        {
            bool measure = i >= config.WarmupIterations;
            SearchEngineSharp engine = workload.ColdIndex
                ? BuildEngine(entries, workload)
                : hotEngine!;

            var sw = Stopwatch.StartNew();
            hitCount = ExecuteSharp(engine, workload, query, supportsFacet);
            sw.Stop();
            if (measure)
                timings.Add(sw.ElapsedTicks * 1_000_000_000L / Stopwatch.Frequency);
        }

        return ToRow(implementation, workload.Id, hitCount, timings, workload.ColdIndex ? "cold-index" : "hot-index");
    }

    private static BenchResultRow MeasureNaive(
        string implementation,
        CorpusFile corpus,
        WorkloadsFile config,
        WorkloadSpec workload,
        string query)
    {
        var timings = new List<long>(config.MeasureIterations);
        int hitCount = 0;

        for (int i = 0; i < config.WarmupIterations + config.MeasureIterations; i++)
        {
            bool measure = i >= config.WarmupIterations;
            var sw = Stopwatch.StartNew();
            hitCount = ExecuteNaive(corpus, workload, query);
            sw.Stop();
            if (measure)
                timings.Add(sw.ElapsedTicks * 1_000_000_000L / Stopwatch.Frequency);
        }

        return ToRow(implementation, workload.Id, hitCount, timings, "linear-scan");
    }

    private static int ExecuteSharp(
        SearchEngineSharp engine,
        WorkloadSpec workload,
        string query,
        bool supportsFacet)
    {
        var sort = workload.Sort == "natural"
            ? SearchSortMode.NaturalSortAscending
            : SearchSortMode.SnapshotOrder;

        if (workload.Kind is "within" or "within_facet")
        {
#if SHARP_HAS_FACET
            SearchEngine.Filters.FacetFilter? filter = workload.Kind == "within_facet" && supportsFacet
                ? SearchEngine.Filters.FacetFilter.Range("size", workload.FacetMinSize ?? 1_024, workload.FacetMaxSize ?? 1_048_576)
                : null;

            return engine.Find(query, WordMatchMethod.Within, enableOperators: true, sort, filter).Count;
#else
            return engine.Find(query, WordMatchMethod.Within, enableOperators: true, sort).Count;
#endif
        }

        if (workload.Kind == "exact")
            return engine.Find(query, WordMatchMethod.Exact, enableOperators: false, sort).Count;

        if (workload.Kind == "glob")
            return engine.Find(query, WordMatchMethod.Exact, enableOperators: false, sort).Count;

        throw new NotSupportedException(workload.Kind);
    }

    private static int ExecuteNaive(CorpusFile corpus, WorkloadSpec workload, string query)
    {
        if (workload.Kind == "naive_within")
        {
            int count = 0;
            foreach (var doc in corpus.Documents)
            {
                if (doc.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    count++;
            }

            return count;
        }

        if (workload.Kind == "naive_within_facet_natural")
        {
            long min = workload.FacetMinSize ?? corpus.FacetMinSize;
            long max = workload.FacetMaxSize ?? corpus.FacetMaxSize;
            var hits = new List<(int Id, string Name)>();
            foreach (var doc in corpus.Documents)
            {
                if (doc.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    && doc.SizeBytes >= min
                    && doc.SizeBytes <= max)
                {
                    hits.Add((doc.Id, doc.Name));
                }
            }

            hits.Sort((a, b) => string.Compare(
                NaturalSortKey.Build(a.Name),
                NaturalSortKey.Build(b.Name),
                StringComparison.Ordinal));
            return hits.Count;
        }

        throw new NotSupportedException(workload.Kind);
    }

    private static SearchEngineSharp BuildEngine(Dictionary<int, IndexedEntry> entries, WorkloadSpec workload)
    {
#if SHARP_HAS_FILEMASK
        var tokenization = workload.Kind == "glob" ? SearchTokenization.FileMask : SearchTokenization.Default;
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider, tokenization).RebuildFrom(entries);
#else
        var provider = new IndexSnapshotProvider();
        new IndexUpdater(provider).RebuildFrom(entries);
#endif
        return new SearchEngineSharp(provider);
    }

    private static string ResolveQuery(CorpusFile corpus, WorkloadSpec workload)
    {
        if (!string.IsNullOrEmpty(workload.Query))
            return workload.Query;
        return workload.QueryFromCorpus switch
        {
            "exactQuery" => corpus.ExactQuery,
            _ => throw new InvalidOperationException($"Unknown queryFromCorpus: {workload.QueryFromCorpus}"),
        };
    }

    private static BenchResultRow ToRow(string impl, string workload, int hitCount, List<long> timingsNs, string notes)
    {
        timingsNs.Sort();
        double median = timingsNs[timingsNs.Count / 2];
        double mean = timingsNs.Average();
        return new BenchResultRow(impl, workload, hitCount, median, mean, notes);
    }

    internal static void WriteCsv(string path, IEnumerable<BenchResultRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new StreamWriter(path);
        writer.WriteLine("implementation,workload,hit_count,median_ns,mean_ns,notes");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(',',
                Csv(row.Implementation),
                Csv(row.Workload),
                row.HitCount.ToString(CultureInfo.InvariantCulture),
                row.MedianNs.ToString("F0", CultureInfo.InvariantCulture),
                row.MeanNs.ToString("F0", CultureInfo.InvariantCulture),
                Csv(row.Notes)));
        }
    }

    internal static void WriteEnvironment(string path, string implementation, string? gitSha = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["capturedUtc"] = DateTime.UtcNow.ToString("O"),
            ["implementation"] = implementation,
            ["gitSha"] = gitSha,
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["rid"] = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            ["arch"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["processorCount"] = Environment.ProcessorCount,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Csv(string value)
    {
        if (value.Contains(',') || value.Contains('"'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
