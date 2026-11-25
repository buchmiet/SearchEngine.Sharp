# Benchmarks

Four projects measure the library. Run them with `-c Release` on an otherwise idle machine; on Linux, pin a run to
one CPU with `taskset -c N` when you compare single-thread numbers. Measured results and what they mean for users
are in [docs/performance.md](../docs/performance.md).

| Project | Measures | In the solution |
|---|---|---|
| `SearchEngine.Sharp.Benchmarks` | Query throughput on synthetic documents; publish policies of progressive ingestion | yes |
| `SearchEngine.Sharp.MicroBenchmarks` | Single operations with BenchmarkDotNet | yes |
| [`SearchEngine.Sharp.Profiling`](SearchEngine.Sharp.Profiling/README.md) | File search end to end on a generated tree, latency of every query path, build cost and memory | no |
| [`SearchEngine.CompetitorBenchmarks`](SearchEngine.CompetitorBenchmarks/README.md) | The same file-name workloads in Tantivy, MiniSearch and Bleve | no |

## SearchEngine.Sharp.Benchmarks

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --facet
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --ingestion-policy --ingestion-count 100000
```

Without options it builds three synthetic indexes (10,000, 100,000 and 250,000 documents of 32 tokens) and runs
500 queries of each kind: `Exact`, `Within`, glob and boolean. Each line prints queries per second and the P50 and
P95 latency of one query. `--facet` adds the same queries with facet filters, and a filter-only query, on the two
larger indexes.

`--ingestion-policy` feeds `--ingestion-count` synthetic paths (`SyntheticPathFeed`) through
`ProgressiveIndexIngestion` once per policy (fixed batch, time debounce, adaptive, adaptive without the growing
batch cap; per entry only up to 2,000 paths). The columns are the scan time, the total rebuild time, the number of
publishes, the longest observed staleness, and `Overhead×`: the total rebuild time divided by the time of one full build of
the same paths. `--ingestion-scan-delay-ms N` simulates a slow scan.

Other options: `--warmup N` (default 3), `--iterations N` (default 10), `--seed N` (default 1337).

## SearchEngine.Sharp.MicroBenchmarks

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter '*ExactFacet*'
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --list flat
```

A BenchmarkDotNet suite that runs in process, with the memory diagnoser. The arguments are BenchmarkDotNet's
(`--filter`, `--list`, `--warmupCount`, `--iterationCount` and so on; do not pass `--job`, it replaces the
in-process toolchain this suite needs). Results go to `benchmarks/SearchEngine.Sharp.MicroBenchmarks/BenchmarkDotNet.Artifacts`
as Markdown, CSV and HTML. `--fingerprint [DIR]` writes a JSON description of the machine and runtime instead of
running benchmarks.

| Benchmark | Measures |
|---|---|
| `ExactFacetBenchmark` | an exact term with and without a facet filter |
| `OperatorsOnBenchmark` | a single term with `enableOperators` on and off |
| `FileSearchBenchmark` | typical file-search queries on a realistic file-name corpus |
| `FileMaskGlobBenchmark` | `*.pdf` on whole names compared with the token preset |
| `NaturalSortBenchmark`, `ProgressiveNaturalSortBenchmark` | natural sort on a new snapshot and after each publish of a progressive scan |
| `NaturalSortSelectivityBenchmark` | natural sort by sorting only the hits compared with walking the permutation of all documents, over result sizes (the basis of `SelectivityThresholds.NaturalSortSortKMaxFraction`) |
| `FacetSelectivityBenchmark` | a facet filter on the text hits only compared with a scan of all documents, over result sizes (the basis of `SelectivityThresholds.FacetOnHitsMaxFraction`) |
| `SelectivityPipelineBenchmark` | a query with no hits, and a `Within` query with a facet filter, with natural sort and in snapshot order |
| `BitSetMaterializationBenchmark` | turning a result set into a list of ids, over result sizes |
| `SnapshotBuildAllocationBenchmark` | time and allocations of a snapshot build |
