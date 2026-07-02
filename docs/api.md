# Public API reference

Complete public surface of `SearchEngine.Sharp`, grouped by namespace. Behavioral
details are specified in [query-semantics.md](query-semantics.md); a task-oriented
walkthrough is in [file-search-guide.md](file-search-guide.md).

## Namespace `SearchEngine`

### `ISearchEngine`

Query execution against the current snapshot. Stateless; thread-safe for concurrent
queries. Default implementation: `SearchEngineSharp(IIndexSnapshotProvider)`.

```csharp
List<int> Find(string expression, WordMatchMethod method, bool enableOperators = false);
List<int> Find(string expression, WordMatchMethod method, bool enableOperators, SearchSortMode sortMode);
List<int> Find(string expression, WordMatchMethod method, bool enableOperators, SearchSortMode sortMode, FacetFilter? filter);
int CountMatches(string expression, WordMatchMethod method, bool enableOperators = false);
int CountMatches(string expression, WordMatchMethod method, bool enableOperators, FacetFilter? filter);
int DocumentCount { get; }
int UniqueWordCount { get; }
```

- `Find` returns the caller-supplied ids of matching entries, ordered per `sortMode`.
- The `filter` overloads always run the full evaluation pipeline. When there are no
  facet constraints, call the overloads without `filter` — single-term exact queries
  then use a dictionary fast path.
- An empty `expression` returns nothing unless a non-empty `filter` is present, in
  which case all documents passing the filter are returned.
- A `filter` naming a facet that no indexed document carries throws `ArgumentException`.

### `SearchTokenization`

Configurable index-side and query-side separator sets. Stored in each `IndexSnapshot`;
queries always use `snapshot.Tokenization.QuerySeparators`.

```csharp
static SearchTokenization Default { get; }   // token-level (legacy behavior)
static SearchTokenization FileMask { get; }  // whole-name index; whitespace query split
static SearchTokenization Create(string indexSeparators, string querySeparators);

string IndexSeparators { get; }
string QuerySeparators { get; }
```

See [query-semantics.md](query-semantics.md#tokenization-presets) for preset tables and
FileMask behavioral notes.

### `IIndexUpdater`

Index mutations. All operations are serialized and publish a new immutable snapshot on
completion. Each mutation rebuilds the full index; prefer batch methods or
`ProgressiveIndexIngestion` over per-entry calls. Default implementation:
`IndexUpdater(IndexSnapshotProvider, SearchTokenization? tokenization = null)`.

```csharp
IndexUpdater(IndexSnapshotProvider provider, SearchTokenization? tokenization = null);

// Full rebuild
void RebuildFrom(IDictionary<int, string> entries);
void RebuildFrom(IDictionary<int, IndexedEntry> entries);
Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, string>> entries, IProgress<float>? progress = null, CancellationToken ct = default);
Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> entries, IProgress<float>? progress = null, CancellationToken ct = default);

// Single entry (one full rebuild per call)
void AddEntry(int id, string text);
void AddEntry(int id, IndexedEntry entry);
bool RemoveEntry(int id);
bool RefreshEntry(int id, string text);
bool RefreshEntry(int id, IndexedEntry entry);

// Batch (one rebuild per call)
void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, string>> entries);
void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, IndexedEntry>> entries);
int RemoveEntries(IEnumerable<int> ids);

// Utility
void Clear();
int EntryCount { get; }
bool ContainsEntry(int id);
```

### `IIndexSnapshotProvider` / `IndexSnapshotProvider`

Holds the current immutable snapshot. Reads are lock-free (`Volatile.Read`); the
updater swaps the reference atomically.

```csharp
IndexSnapshot Current { get; }
```

### `IndexedEntry`

```csharp
public sealed record IndexedEntry(string SearchText, string SortText, FacetValues? Facets = null);
```

- `SearchText` — tokenized and matched.
- `SortText` — input for the natural sort key (`SearchSortMode.NaturalSortAscending`).
- `Facets` — optional numeric columns for `FacetFilter` (see below).

### `WordMatchMethod`

| Value | Meaning |
|---|---|
| `Exact` | Whole-token equality |
| `Within` | Substring inside an indexed token |

Terms containing `*` or `?` are matched as anchored globs regardless of the method.

### `SearchSortMode`

| Value | Meaning |
|---|---|
| `SnapshotOrder` | Document order within the current snapshot |
| `NaturalSortAscending` | Natural sort of `SortText` (digit runs compare numerically) |

## Namespace `SearchEngine.Filters`

### `FacetValues`

Immutable per-document facet bag; values are `long`. Facet names compare ordinally
(case-sensitive).

```csharp
static FacetValues Empty { get; }
static FacetValues? FromDictionary(IReadOnlyDictionary<string, long>? values); // null when empty
static FacetValues Create(string facet, long value);
```

### `FacetFilter`

AND-combined predicates applied after the text expression (including `NOT`).
Documents lacking a facet have `0` in its column.

```csharp
static FacetFilter None { get; }
bool IsEmpty { get; }
static FacetFilter Range(string facet, long minInclusive, long maxInclusive);
static FacetFilter Mask(string facet, long mustHave, long mustNot = 0);
static FacetFilter Combine(params FacetFilter[] filters);
```

- `Range` — inclusive on both ends; equality is `Range(f, x, x)`.
- `Mask` — `(value & mustHave) == mustHave && (value & mustNot) == 0`.
- `Combine` — logical AND of all contained predicates. There is no OR across predicates.

## Namespace `SearchEngine.Snapshots`

### `IndexSnapshot`

Immutable index snapshot; safe for unlimited concurrent readers. `IndexSnapshot.Empty`
is the empty singleton.

```csharp
int DocumentCount { get; }
int UniqueWordCount { get; }
SearchTokenization Tokenization { get; }
```

### `IndexSnapshotBuilder`

Builds a snapshot directly, without an updater — for pre-built initial snapshots
(see the DI overloads below).

```csharp
static IndexSnapshot Build(IEnumerable<KeyValuePair<int, string>> entries);
static IndexSnapshot Build(IEnumerable<KeyValuePair<int, string>> entries, SearchTokenization tokenization);
static IndexSnapshot Build(IEnumerable<KeyValuePair<int, string>> entries, IProgress<float>? progress);
static IndexSnapshot Build(IEnumerable<KeyValuePair<int, string>> entries, SearchTokenization tokenization, IProgress<float>? progress);
static IndexSnapshot Build(IEnumerable<KeyValuePair<int, IndexedEntry>> entries);
static IndexSnapshot Build(IEnumerable<KeyValuePair<int, IndexedEntry>> entries, SearchTokenization tokenization);
static IndexSnapshot Build(IEnumerable<KeyValuePair<int, IndexedEntry>> entries, IProgress<float>? progress);
static IndexSnapshot Build(IEnumerable<KeyValuePair<int, IndexedEntry>> entries, SearchTokenization tokenization, IProgress<float>? progress);
```

## Namespace `SearchEngine.Ingestion`

### `ProgressiveIndexIngestion`

Buffers a streaming scan and publishes batches through `IIndexUpdater` so the index
stays queryable during long scans. One `IngestAsync` call per instance at a time;
concurrent queries are safe throughout. `onPublished` and the `Published` event run
on the publisher task, not the caller's synchronization context.

```csharp
ProgressiveIndexIngestion(IIndexUpdater updater);

event Action<IngestionPublishEvent>? Published;

Task<IngestionResult> IngestAsync(
    IAsyncEnumerable<KeyValuePair<int, string>> entries,
    IngestPublishOptions? options = null,
    IProgress<IngestionProgress>? progress = null,
    Action<IngestionPublishEvent>? onPublished = null,
    CancellationToken cancellationToken = default);

Task<IngestionResult> IngestAsync(
    IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> entries,
    IngestPublishOptions? options = null,
    IProgress<IngestionProgress>? progress = null,
    Action<IngestionPublishEvent>? onPublished = null,
    CancellationToken cancellationToken = default);
```

Cancellation: the scan stops accepting entries; buffered entries are flushed when
`IngestPublishOptions.FlushOnCancellation` is `true` (default), otherwise discarded.
Entries already published stay in the index.

### `IngestPublishPolicy`

| Value | Publishes |
|---|---|
| `PerEntry` | After every entry (baseline only; O(N²) total rebuild cost) |
| `FixedBatch` | When the buffer reaches `FixedBatchSize` |
| `TimeDebounce` | When the buffer is non-empty and `MinInterval` elapsed |
| `Adaptive` (default) | When elapsed ≥ `max(MinInterval, AdaptiveMultiplier × lastRebuildDuration)`, or the buffer reaches `FixedBatchSize` |

### `IngestPublishOptions`

| Property | Default | Meaning |
|---|---|---|
| `Policy` | `Adaptive` | Publish policy |
| `FixedBatchSize` | 2 000 | Buffer size that forces a publish |
| `MinInterval` | 100 ms | Floor between publishes (`TimeDebounce`, `Adaptive`) |
| `AdaptiveMultiplier` | 2.0 | `k` in the adaptive interval formula |
| `ChannelCapacity` | 10 000 | Scanner→publisher channel bound (backpressure when full) |
| `FlushOnCancellation` | `true` | Flush buffered entries on cancel |
| `MaxStaleness` | 1 s | Max time an accepted entry may stay unpublished |
| `MinTimerPublishBatch` | 200 | Min buffered entries for a timer-triggered adaptive publish |

### `IngestionProgress` (struct)

`EntriesAccepted`, `EntriesPublished`, `PublishCount`, `PendingCount`.

### `IngestionPublishEvent` (struct)

`EntryCount`, `IndexedDocumentCount`, `RebuildDuration`, `PublishSequence`.

### `IngestionResult`

`EntriesAccepted`, `EntriesPublished`, `PublishCount`, `TotalRebuildCpu`,
`WorstCaseStaleness`, `CompletedSuccessfully`, `WasCancelled`.

### `SyntheticPathFeed`

Synthetic path generator used by the demo and benchmarks.

```csharp
static IAsyncEnumerable<KeyValuePair<int, string>> EnumerateAsync(int count, int scanDelayMs = 0, int seed = 42, CancellationToken ct = default);
static Dictionary<int, string> CreateDictionary(int count, int seed = 42);
```

## Namespace `SearchEngine.Tokenizer`

### `NameTokenizer`

```csharp
static string TokenizeName(string? name, string joinCharacter = "-");
```

Keeps letter/digit runs only (splits on everything else, including `_`), lowercases,
deduplicates, joins with `joinCharacter`. Optional pre-processing for `SearchText`;
never apply it to query strings (it strips `*` and `?`).

### `TextNormalizer`

Low-level tokenization helpers used by the index builder.

```csharp
static bool IsSeparator(char ch);
static bool IsSeparator(char ch, SearchValues<char> separators);
static List<string> GetWordsFromString(string text);          // unique, length-descending
static List<string> GetWordsFromString(string text, SearchValues<char> separators);
static string CreateNormalizedWord(ReadOnlySpan<char> source); // lowercase copy
static char ToLowerInvariantFast(char ch);
static void ForEachUniqueWord<TState>(string text, HashSet<string> uniqueWords, ref TState state, UniqueWordAction<TState> action);
static void ForEachUniqueWord<TState>(string text, HashSet<string> uniqueWords, SearchValues<char> separators, ref TState state, UniqueWordAction<TState> action);
```

Overloads without a separator set use the `SearchTokenization.Default` index separators.
To use a preset's characters, create the set with
`SearchValues.Create(tokenization.IndexSeparators)`.

## Namespace `SearchEngine.DependencyInjection`

### `ServiceCollectionExtensions`

```csharp
static IServiceCollection AddSearchEngine(this IServiceCollection services);
static IServiceCollection AddSearchEngine(this IServiceCollection services, SearchTokenization tokenization);
static IServiceCollection AddSearchEngineTransient(this IServiceCollection services);
static IServiceCollection AddSearchEngineTransient(this IServiceCollection services, SearchTokenization tokenization);
static IServiceCollection AddSearchEngine(this IServiceCollection services, IDictionary<int, string> initialEntries);
static IServiceCollection AddSearchEngine(this IServiceCollection services, IDictionary<int, string> initialEntries, SearchTokenization tokenization);
static IServiceCollection AddSearchEngine(this IServiceCollection services, Func<IServiceProvider, IndexSnapshot> snapshotFactory);
static IServiceCollection AddKeyedSearchEngine(this IServiceCollection services, string key);
static IServiceCollection AddKeyedSearchEngine(this IServiceCollection services, string key, SearchTokenization tokenization);
static IServiceCollection AddKeyedSearchEngine(this IServiceCollection services, string key, IDictionary<int, string> initialEntries);
static IServiceCollection AddKeyedSearchEngine(this IServiceCollection services, string key, IDictionary<int, string> initialEntries, SearchTokenization tokenization);
```

Lifetimes: `IIndexSnapshotProvider` and `IIndexUpdater` singletons; `ISearchEngine`
scoped (`AddSearchEngineTransient` registers it transient). Keyed overloads register
independent indexes resolvable via `[FromKeyedServices("key")]`.
