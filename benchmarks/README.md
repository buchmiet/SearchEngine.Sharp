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
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --filter '*Selectivity*'
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.MicroBenchmarks -- --list flat
```

A BenchmarkDotNet suite that runs in process, with the memory diagnoser. The arguments are BenchmarkDotNet's
(`--filter`, `--list`, `--warmupCount`, `--iterationCount` and so on). Results go to
`benchmarks/SearchEngine.Sharp.MicroBenchmarks/BenchmarkDotNet.Artifacts` as Markdown, CSV and HTML.
`--fingerprint [DIR]` writes a JSON description of the machine and runtime instead of running benchmarks.

| Benchmark | Answers |
|---|---|
| `ExactFacetBenchmark` | What does a facet filter add to an exact term? |
| `OperatorsOnBenchmark` | What does `enableOperators` cost on a single term? |
| `FileSearchBenchmark` | How fast are typical file-search queries on a realistic file-name corpus? |
| `FileMaskGlobBenchmark` | How does `*.pdf` on whole names compare with the token preset? |
| `NaturalSortBenchmark`, `ProgressiveNaturalSortBenchmark` | What does natural sort cost on a new snapshot, and after each publish of a progressive scan? |
| `FastBitSetBenchmark` | How fast are AND, OR, AND NOT, the population count and the set-bit enumeration of a result set, from 1,000 to 1,000,000 bits? |
| `BitSetMaterializationBenchmark` | What does turning a result set into a list of ids in snapshot order cost, by number of hits? |
| `SnapshotBuildAllocationBenchmark` | How long does a snapshot build take and how much does it allocate? |

### Selectivity benchmarks

The query pipeline picks a facet strategy and a natural sort strategy from the share of documents the query matches,
using the fractions in `src/SearchEngine.Sharp/Query/SelectivityThresholds.cs`. These benchmarks check those
fractions. All of them run at the hit densities 0.1%, 1%, 10%, 25%, 50%, 65%, 80%, 90% and 100% of a 100,000-name
corpus (`SelectivityCorpus`: the `FileSearchDataFactory` names, one search word per density, a facet filter that
keeps half of the documents). The strategy benchmarks call the internal strategy methods directly, so each
strategy runs at every density, also where the pipeline would not choose it.

| Benchmark | Answers |
|---|---|
| `FacetSelectivityBenchmark` | Up to which density is filtering only the text hits (`FacetFilterEvaluator.ApplyOnHitsInPlace`) faster than testing every document and intersecting (`ApplyFullScan`)? At 100,000 and 1,000,000 documents. This is `FacetOnHitsMaxFraction`. |
| `NaturalSortSelectivityBenchmark` | Up to which density is sorting only the hits (`ResultMaterializer.MaterializeNaturalSortKOnly`) faster than walking a sort permutation that is already built (`MaterializeNaturalSortFullPermutation`)? |
| `NaturalSortColdSelectivityBenchmark` | The same on a new snapshot, where the full permutation strategy first builds the permutation. With the warm case, this is `NaturalSortSortKMaxFraction`. |
| `SelectivityPipelineBenchmark` | What does `Find` with the facet filter cost end to end, with the strategies the engine picks: a `Within` query in snapshot order, and an `Exact` query with natural sort? |
| `SelectivityPipelineColdBenchmark` | The natural sort query of `SelectivityPipelineBenchmark` as the first query on a new snapshot. |

The cold benchmarks build a new snapshot before each call and run one call per iteration, so they take longer
than the others. To read a crossover, compare the two strategy rows at each density: the threshold belongs between
the last density where the hits-only strategy is faster and the first where it is slower.
