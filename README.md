# SearchEngine.Sharp

An in-memory search index for .NET, built for names: file names, product codes, titles. It finds whole tokens,
substrings, glob patterns (`*`, `?`) and regular expressions, combines terms with `AND`, `OR` and `NOT`, filters
by numeric facets (sizes, dates, attribute bits), sorts results naturally (`file2` before `file10`), and stays
queryable while a long scan is still adding names. Queries run lock-free on immutable snapshots.

Requires .NET 11. Package: [`SearchEngine.Sharp`](https://www.nuget.org/packages/SearchEngine.Sharp).

```bash
dotnet add package SearchEngine.Sharp
```

## Documentation

| Question | Page |
|---|---|
| How do I build a file search with live results and size, date or attribute criteria? | [docs/file-search-guide.md](docs/file-search-guide.md) |
| Should I build an index or scan the directory each time? | [Index or plain scan](docs/file-search-guide.md#index-or-plain-scan) |
| What exactly does a query match: tokens, operators, globs, regex, Unicode options, facets, sort order? | [docs/query-semantics.md](docs/query-semantics.md) |
| What are the signatures, defaults and exceptions of a type? | [docs/api.md](docs/api.md) |
| How fast is it, how much memory does it take, and how do I tune progressive ingestion? | [docs/performance.md](docs/performance.md) |
| What changed between versions? | [CHANGELOG.md](CHANGELOG.md) |
| How do I run the benchmarks? | [benchmarks/README.md](benchmarks/README.md) |
| How should comments and documentation be written? | [CONTRIBUTING.md](CONTRIBUTING.md) |

## Quick start

```csharp
using SearchEngine;

var provider = new IndexSnapshotProvider();
var updater = new IndexUpdater(provider);
var engine = new SearchEngineSharp(provider);

updater.RebuildFrom(new Dictionary<int, string>
{
    [1] = "GA-100 digital watch",
    [2] = "GA-200 analog watch",
    [3] = "report-2024.pdf",
});

List<int> exact = engine.Find("digital", WordMatchMethod.Exact);                          // [1]
List<int> within = engine.Find("ana", WordMatchMethod.Within);                            // [2]
List<int> both = engine.Find("watch AND NOT analog", WordMatchMethod.Exact, enableOperators: true); // [1]
List<int> glob = engine.Find("rep*", WordMatchMethod.Exact);                              // [3]
List<int> sorted = engine.Find("watch", WordMatchMethod.Exact, enableOperators: false,
    SearchSortMode.NaturalSortAscending);                                                 // [1, 2]
int count = engine.CountMatches("watch", WordMatchMethod.Exact);                          // 2
```

`Find` returns the ids you indexed. The default preset, `SearchTokenization.Default`, splits text into tokens at
spaces and punctuation such as `.` and `-`, so `rep*` matches the token `report`. For file-manager masks, where
`*.pdf` means "the name ends with `.pdf`", create the updater with `SearchTokenization.FileMask`
([Tokenization presets](docs/query-semantics.md#tokenization-presets)).

### Facets and sort text

`IndexedEntry` separates the text that is searched from the text that is sorted, and carries optional numeric
facets for filtering:

```csharp
using SearchEngine;
using SearchEngine.Filters;

var provider = new IndexSnapshotProvider();
var updater = new IndexUpdater(provider);
var engine = new SearchEngineSharp(provider);

updater.RebuildFrom(new Dictionary<int, IndexedEntry>
{
    [10] = new("GA-100 G-Shock digital", "GA-100",
        FacetValues.Create([new("size", 12_345), new("modified", DateTime.UtcNow.Ticks), new("attrs", 0x1 | 0x4)])),
});

var filter = FacetFilter.Combine(
    FacetFilter.Range("size", 1024, 1_048_576),
    FacetFilter.Mask("attrs", mustHave: 0x1, mustNot: 0x2));

List<int> hits = engine.Find("g shock", WordMatchMethod.Within, false, SearchSortMode.SnapshotOrder, filter); // [10]
List<int> large = engine.Find("", WordMatchMethod.Exact, false, SearchSortMode.SnapshotOrder,
    FacetFilter.Range("size", 10_000, long.MaxValue));                                      // [10]
```

An empty expression with a filter returns every document that passes the filter. Rules for missing values and
unknown facet names: [Facet filters](docs/query-semantics.md#facet-filters).

### Dependency injection

```csharp
using Microsoft.Extensions.DependencyInjection;
using SearchEngine;
using SearchEngine.DependencyInjection;

var services = new ServiceCollection();
services.AddSearchEngine(SearchTokenization.FileMask);
using var sp = services.BuildServiceProvider();

var updater = sp.GetRequiredService<IIndexUpdater>();
var engine = sp.GetRequiredService<ISearchEngine>();

updater.AddEntry(1, "notes.txt");
List<int> ids = engine.Find("*.txt", WordMatchMethod.Exact); // [1]
```

`AddKeyedSearchEngine` registers several independent indexes; see
[ServiceCollectionExtensions](docs/api.md#servicecollectionextensions).

### Indexing a long scan

Every `IndexUpdater` call that changes an entry publishes a new snapshot with one more small segment, which a
background thread later merges with its neighbours. Calling `AddEntry` once per file therefore turns a long scan into
many segments and merges. Stream the
scan through `ProgressiveIndexIngestion` instead: it publishes growing batches, and the index answers queries
while the scan runs:

```csharp
using SearchEngine.Ingestion;

var ingestion = new ProgressiveIndexIngestion(updater);
IngestionResult result = await ingestion.IngestAsync(
    ScanAsync(root, cancellationToken),           // your IAsyncEnumerable<KeyValuePair<int, IndexedEntry>>
    onPublished: _ => RefreshResultList(),        // runs on the publisher task
    cancellationToken: cancellationToken);
```

A complete scanner and the live requery loop are in the [file search guide](docs/file-search-guide.md). For a
one-shot search, skip the index and filter names during the scan with `NameMatcher`, which applies the same query
language to one name at a time.

## How it works

- `IndexSnapshot` is an immutable index: the distinct tokens packed in one array sorted by length, a posting list
  per token, the sort texts and one column per facet. Any number of threads can query it. A snapshot published by
  `IndexUpdater` is a list of such segments, from one large one to the small ones that recent changes added, plus
  for each segment the set of its documents removed or replaced since it was built.
- `IndexSnapshotProvider` holds the current snapshot. Readers take it with a volatile read and never lock; a new
  snapshot replaces the reference atomically, and a query that already started finishes on the old one.
- `IndexUpdater` serializes changes and publishes a new snapshot for every change. A change builds one small
  segment from the entries it adds or replaces and marks the documents it removes as removed where they are; it
  never rebuilds the index. Neighbouring segments of similar size are merged on a background thread, so the number of
  segments stays small and a document is rewritten about once per size class, and a merge never blocks a change or a
  query. A change that fails leaves the published snapshot as it was.
- `SearchEngineSharp` parses the expression, resolves each term to a set of documents (a bitset), combines the
  sets with the boolean operators, applies the facet filter and turns the result into ids in the requested order.
  The bigram index for `Within` and the natural sort permutation are built on the first query that needs them.
- `ProgressiveIndexIngestion` sits between a scanner and an updater: a bounded channel, a publisher that decides
  when to publish, and progress callbacks.
- `NameMatcher` runs the same parser and matchers on a single name, without an index.

Set operations on bitsets use AVX2 on x64 and AdvSimd on ARM64 when available, and scalar code otherwise. A query
runs on the calling thread.

## Building and testing

```bash
dotnet build SearchEngine.Sharp.slnx -c Release
dotnet test --project tests/SearchEngine.Sharp.Tests -c Release
```

The tests use [TUnit](https://tunit.dev) on Microsoft.Testing.Platform, which `global.json` selects for `dotnet test`.
Tests that need Unicode normalization are skipped in globalization-invariant mode; run the suite once more with
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` to cover that mode.

Benchmarks: [benchmarks/README.md](benchmarks/README.md).

```
src/                             library
tests/SearchEngine.Sharp.Tests/  TUnit tests
benchmarks/                      benchmark and profiling projects
docs/                            documentation
```

## Publishing (maintainers)

NuGet.org uses [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (OIDC), not
long-lived API keys.

1. On nuget.org, under Account, Trusted Publishing, add a policy: provider GitHub Actions, owner `buchmiet`,
   repository `SearchEngine.Sharp`, workflow `publish-nuget.yml`, environment `production` (it must match the
   `environment:` of the workflow job).
2. On GitHub, under the repository's Settings, Environments, make sure `production` exists (the first run creates
   it).
3. On GitHub, under Secrets, Actions, set `NUGET_USER` to the nuget.org profile user name of the policy creator
   (as in the profile URL, not the display name).
4. Push a version tag, for example `git tag v0.6.1 && git push origin v0.6.1`.

The workflow builds, tests and packs, exchanges an OIDC token for a short-lived push key, and publishes.

## License

MIT. Copyright (c) 2026 Lukasz Buchmiet.
