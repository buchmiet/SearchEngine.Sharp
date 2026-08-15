using System.Text.Json.Serialization;

namespace SearchEngine.CompetitorBenchmarks;

internal sealed record CorpusDocument(
    int Id,
    string Name,
    long SizeBytes,
    long ModifiedTicks);

internal sealed record CorpusFile(
    int DocumentCount,
    int Seed,
    string WithinQuery,
    string ExactQuery,
    string GlobQuery,
    string ZeroHitQuery,
    long FacetMinSize,
    long FacetMaxSize,
    IReadOnlyList<CorpusDocument> Documents);

internal sealed record WorkloadSpec(
    string Id,
    string Description,
    string Kind,
    string? Query,
    [property: JsonPropertyName("queryFromCorpus")] string? QueryFromCorpus,
    string Sort,
    long? FacetMinSize,
    long? FacetMaxSize,
    bool ColdIndex,
    bool RequiresFacet = false);

internal sealed record WorkloadsFile(
    string CorpusFile,
    int WarmupIterations,
    int MeasureIterations,
    IReadOnlyList<WorkloadSpec> Workloads);

internal sealed record BenchResultRow(
    string Implementation,
    string Workload,
    int HitCount,
    double MedianNs,
    double MeanNs,
    string Notes);
