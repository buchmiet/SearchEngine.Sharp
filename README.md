# SearchEngine.Sharp

In-memory inverted index for .NET. Supports exact token match, substring (within-word) search, glob patterns (`*`, `?`), boolean expressions (AND / OR / NOT), numeric facet filters (ranges and bitmasks over dates, sizes, flags), natural sort ordering, and progressive indexing during long scans.

Target: .NET 10 (C# 14). NuGet: [`SearchEngine.Sharp`](https://www.nuget.org/packages/SearchEngine.Sharp) (publish on tag `v*`).

**Tokenization presets:** `SearchTokenization.Default` (token-level search) and
`SearchTokenization.FileMask` (whole-file-name classic masks — `*.pdf` is end-anchored).
Pass to `IndexUpdater`; details in [docs/query-semantics.md](docs/query-semantics.md#tokenization-presets).

## Documentation

| Document | Contents |
|----------|----------|
| [docs/query-semantics.md](docs/query-semantics.md) | Exact rules: tokenization, match methods, boolean operators, globs, facet filters, edge cases |
| [docs/file-search-guide.md](docs/file-search-guide.md) | End-to-end recipe: file search with live results, size/date/attribute criteria |
| [docs/api.md](docs/api.md) | Full public API reference with signatures |
| [docs/ingestion-policy-report.md](docs/ingestion-policy-report.md) | Measured publish-policy comparison for progressive ingestion |
| [docs/glob-and-facets-report.md](docs/glob-and-facets-report.md) | Measured glob and facet filter throughput |

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build

```bash
dotnet build SearchEngine.Sharp.sln -c Release
```

## Tests

```bash
dotnet test SearchEngine.Sharp.sln -c Release
```

## Benchmarks

Console app on synthetic data. Reports query throughput (queries/s) and P50/P95 latency.

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks
```

Optional parallel infix experiment (not part of the library API):

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --parallel
```

Flags: `--warmup N`, `--iterations N`, `--seed N`.

## Usage

### Without DI

```csharp
using SearchEngine;
using SearchEngine.Snapshots;

var provider = new IndexSnapshotProvider();
var updater = new IndexUpdater(provider);

updater.RebuildFrom(new Dictionary<int, string>
{
    [1] = "GA-100 digital watch",
    [2] = "GA-200 analog watch",
});

var engine = new SearchEngineSharp(provider);

// Exact token match
var exact = engine.Find("digital", WordMatchMethod.Exact);

// Substring match (within indexed words)
var within = engine.Find("ana", WordMatchMethod.Within);

// Boolean expression
var filtered = engine.Find("digital AND watch", WordMatchMethod.Exact, enableOperators: true);

// Glob token match (* and ?) — auto-routed when a token contains metacharacters
var reports = engine.Find("report*", WordMatchMethod.Exact);
var filteredGlob = engine.Find("report* AND NOT *tmp", WordMatchMethod.Exact, enableOperators: true);

// Natural sort by model name (requires IndexedEntry with separate SortText)
var sorted = engine.Find("watch", WordMatchMethod.Exact, enableOperators: false,
    sortMode: SearchSortMode.NaturalSortAscending);
```

### With DI

```csharp
using Microsoft.Extensions.DependencyInjection;
using SearchEngine;
using SearchEngine.DependencyInjection;

var services = new ServiceCollection();
services.AddSearchEngine();
var sp = services.BuildServiceProvider();

var updater = sp.GetRequiredService<IIndexUpdater>();
var engine = sp.GetRequiredService<ISearchEngine>();

updater.AddEntry(1, "example text");
var ids = engine.Find("example", WordMatchMethod.Exact);
```

`AddSearchEngine` overloads accept an initial snapshot, a keyed index, or a custom snapshot factory. See `ServiceCollectionExtensions`.

### IndexedEntry

When search text and display/sort text differ:

```csharp
updater.RebuildFrom(new Dictionary<int, IndexedEntry>
{
    [10] = new("GA-100 G-Shock digital", "GA-100"),
});
```

`SearchText` is tokenized and matched. `SortText` drives `SearchSortMode.NaturalSortAscending`.

Optional facet values attach numeric columns for post-query filtering:

```csharp
using SearchEngine.Filters;

updater.RebuildFrom(new Dictionary<int, IndexedEntry>
{
    [10] = new(
        "GA-100 G-Shock digital",
        "GA-100",
        FacetValues.FromDictionary(new Dictionary<string, long>
        {
            ["size"] = 12_345,              // bytes
            ["modified"] = DateTime.UtcNow.Ticks,
            ["attrs"] = 0x1 | 0x4,          // bit flags
        })),
});

var filter = FacetFilter.Combine(
    FacetFilter.Range("size", 1024, 1_048_576),
    FacetFilter.Mask("attrs", mustHave: 0x1, mustNot: 0x2));

var hits = engine.Find("g shock", WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, filter);
var allLarge = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder,
    FacetFilter.Range("size", 1_000_000, long.MaxValue));
```

Missing facet values default to `0` in the snapshot column. Unknown facet names in a filter throw `ArgumentException`. An empty expression with a non-empty filter matches all documents through the filter. Full rules: [docs/query-semantics.md](docs/query-semantics.md#facet-filters).

### Glob matching

Query tokens containing `*` or `?` are matched as glob patterns against whole indexed tokens (anchored at both ends), regardless of `WordMatchMethod`:

- `*` — zero or more characters
- `?` — exactly one character

Notes:

- There is no escape syntax: `*` and `?` in a query are always wildcards.
- `[` and `]` are query separators and are not part of glob syntax.
- A pattern spanning token boundaries (e.g. `ga-1*`) is split into multiple tokens joined by implicit AND; `*.txt` therefore means "has token `txt`", not "ends with `.txt`" — see [docs/file-search-guide.md](docs/file-search-guide.md#file-extensions) for anchored extension filtering.

Full rules and worked examples: [docs/query-semantics.md](docs/query-semantics.md#glob-patterns).

### Regular expressions

Pass `WordMatchMethod.Regex` to match the **whole expression** as one regex against
indexed tokens (anchored, case-insensitive, non-backtracking):

```csharp
var hits = engine.Find(@"report.*|log", WordMatchMethod.Regex);
```

Patterns run on **normalized tokens**, not raw names — under `SearchTokenization.Default`
a cross-separator pattern such as `report.*\.pdf` will not match. Use
`SearchTokenization.FileMask` when the token is the full file name. Invalid patterns
return no matches. See [docs/query-semantics.md](docs/query-semantics.md#regular-expressions-wordmatchmethodregex).

## API summary

Full signatures: [docs/api.md](docs/api.md).

| Type | Role |
|------|------|
| `ISearchEngine` | Query execution (`Find`, `CountMatches`) |
| `IIndexUpdater` | Index mutations (rebuild, add, remove) |
| `ProgressiveIndexIngestion` | Batched progressive ingestion during long scans |
| `IIndexSnapshotProvider` | Current immutable snapshot |
| `SearchEngineSharp` | Default `ISearchEngine` implementation |
| `IndexSnapshotBuilder` | Build snapshot without DI |
| `WordMatchMethod.Exact` | Whole-token match |
| `WordMatchMethod.Within` | Substring match inside indexed tokens |
| `WordMatchMethod.Regex` | Whole-expression regex on indexed tokens |
| `SearchSortMode.SnapshotOrder` | Result order follows internal document ordinals |
| `SearchSortMode.NaturalSortAscending` | Sort by natural key derived from `SortText` |
| `FacetValues` | Optional per-document facet bag (`long` values) |
| `FacetFilter` | Post-query AND filter (range / bitmask) |
| `SearchTokenization` | Index/query separator presets (`Default`, `FileMask`) |

## Index updates

Each mutation rebuilds the full index from the in-memory entry dictionary. Batch methods (`AddOrUpdateEntries`, `RemoveEntries`) perform one rebuild per call. Async rebuild methods consume input outside the update lock.

Queries read the current snapshot via `Volatile.Read`; updates publish a new snapshot with `Interlocked.Exchange`. Snapshots are immutable.

## Live indexing during scans

For large directory scans (100k+ files), do **not** call `AddEntry` per file — each call rebuilds the entire index and total work grows **O(N²)**.

Use `ProgressiveIndexIngestion` instead. It buffers scan results and publishes batches through `AddOrUpdateEntries`:

```csharp
using SearchEngine.Ingestion;

var ingestion = new ProgressiveIndexIngestion(updater);

var result = await ingestion.IngestAsync(
    ScanTokenizedPathsAsync(root, cancellationToken),
    new IngestPublishOptions
    {
        Policy = IngestPublishPolicy.Adaptive, // default
        FixedBatchSize = 2_000,
        MaxStaleness = TimeSpan.FromSeconds(1),
    },
    onPublished: _ => RefreshResultList());

// result.PublishCount, result.WorstCaseStaleness, result.TotalRebuildCpu
```

Defaults target **&lt; 1 s staleness** and ~50 progressive updates per 100k files on typical hardware. See [docs/ingestion-policy-report.md](docs/ingestion-policy-report.md) for measured policy comparison.

**Demo:**

```bash
dotnet run -c Release --project demos/ProgressiveIngestion.Demo -- --count 100000 --scan-delay-ms 0
```

**Policy benchmark:**

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --ingestion-policy --ingestion-count 100000
```

**Facet filter benchmark:**

```bash
dotnet run -c Release --project benchmarks/SearchEngine.Sharp.Benchmarks -- --facet
```

See [docs/glob-and-facets-report.md](docs/glob-and-facets-report.md) for glob and facet throughput measurements.


## Platform notes

Runs on any .NET 10-supported OS and CPU architecture.

`FastBitSet` set operations use runtime CPU detection:

- x64 with AVX2: 256-bit SIMD path
- ARM64 with AdvSimd: 128-bit SIMD path
- otherwise: scalar fallback

Infix search in the library is single-threaded. A parallel variant exists only under `benchmarks/` for measurement.

## Publishing (maintainers)

NuGet.org uses [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (OIDC), not long-lived API keys.

1. **nuget.org** → Account → Trusted Publishing → add policy:
   - Provider: GitHub Actions
   - Owner: `buchmiet`
   - Repository: `SearchEngine.Sharp`
   - Workflow: `publish-nuget.yml`
   - Environment: `production` (must match the workflow job `environment:`)
2. **GitHub** → repo Settings → Environments → ensure `production` exists (created automatically on first run).
3. **GitHub** → Secrets → Actions → `NUGET_USER` = nuget.org **profile username** (policy creator; see your profile URL, not display name).
4. Push a version tag, e.g. `git tag v0.5.0 && git push origin v0.5.0`.

The workflow builds, tests, packs, exchanges an OIDC token for a short-lived push key, then publishes.

## Project layout

```
src/SearchEngine.Sharp/          Library
tests/SearchEngine.Sharp.Tests/  xUnit tests
benchmarks/SearchEngine.Sharp.Benchmarks/  Throughput console app
demos/ProgressiveIngestion.Demo/           Progressive ingestion demo
docs/query-semantics.md                    Tokenization, operators, globs, facets — exact rules
docs/file-search-guide.md                  File search recipe (live results, facet criteria)
docs/api.md                                Public API reference
docs/ingestion-policy-report.md            Policy comparison measurements
docs/glob-and-facets-report.md             Glob and facet filter measurements
```

## License

MIT. Copyright (c) 2026 Lukasz Buchmiet.
