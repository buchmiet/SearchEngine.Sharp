# SearchEngine.Sharp

In-memory inverted index for .NET. Supports exact token match, substring (within-word) search, boolean expressions (AND / OR / NOT), and natural sort ordering of results.

Target: .NET 10 (C# 14). NuGet package: `SearchEngine.Sharp` (from tag `v*`, starting at `v0.5.0`).

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

## API summary

| Type | Role |
|------|------|
| `ISearchEngine` | Query execution (`Find`, `CountMatches`) |
| `IIndexUpdater` | Index mutations (rebuild, add, remove) |
| `IIndexSnapshotProvider` | Current immutable snapshot |
| `SearchEngineSharp` | Default `ISearchEngine` implementation |
| `IndexSnapshotBuilder` | Build snapshot without DI |
| `WordMatchMethod.Exact` | Whole-token match |
| `WordMatchMethod.Within` | Substring match inside indexed tokens |
| `SearchSortMode.SnapshotOrder` | Result order follows internal document ordinals |
| `SearchSortMode.NaturalSortAscending` | Sort by natural key derived from `SortText` |

## Index updates

Each mutation rebuilds the full index from the in-memory entry dictionary. Batch methods (`AddOrUpdateEntries`, `RemoveEntries`) perform one rebuild per call. Async rebuild methods consume input outside the update lock.

Queries read the current snapshot via `Volatile.Read`; updates publish a new snapshot with `Interlocked.Exchange`. Snapshots are immutable.

## Platform notes

Runs on any .NET 10-supported OS and CPU architecture.

`FastBitSet` set operations use runtime CPU detection:

- x64 with AVX2: 256-bit SIMD path
- ARM64 with AdvSimd: 128-bit SIMD path
- otherwise: scalar fallback

Infix search in the library is single-threaded. A parallel variant exists only under `benchmarks/` for measurement.

## Project layout

```
src/SearchEngine.Sharp/          Library
tests/SearchEngine.Sharp.Tests/  xUnit tests
benchmarks/SearchEngine.Sharp.Benchmarks/  Throughput console app
```

## License

MIT. Copyright (c) 2026 Lukasz Buchmiet.
