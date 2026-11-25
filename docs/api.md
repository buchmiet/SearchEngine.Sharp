# Public API reference

The public types of `SearchEngine.Sharp`, grouped by namespace. Matching rules are in
[query-semantics.md](query-semantics.md), costs and tuning in [performance.md](performance.md), and a worked file
search in [file-search-guide.md](file-search-guide.md).

## Namespace `SearchEngine`

### `ISearchEngine`, `SearchEngineSharp`

Runs queries against the current snapshot of an `IIndexSnapshotProvider`. The engine keeps no state of its own and
is safe to call from many threads at once. Each call reads the snapshot once, so it sees either the index before a
publish or the one after it, never a mix.

```csharp
SearchEngineSharp(IIndexSnapshotProvider snapshotProvider);

List<int> Find(string expression, WordMatchMethod method, bool enableOperators = false);
List<int> Find(string expression, WordMatchMethod method, bool enableOperators, SearchSortMode sortMode);
List<int> Find(string expression, WordMatchMethod method, bool enableOperators, SearchSortMode sortMode, FacetFilter? filter);
int CountMatches(string expression, WordMatchMethod method, bool enableOperators = false);
int CountMatches(string expression, WordMatchMethod method, bool enableOperators, FacetFilter? filter);
int DocumentCount { get; }
int UniqueWordCount { get; }
```

- `Find` returns the ids the caller indexed, in `sortMode` order (`SnapshotOrder` for the overloads without it).
  `CountMatches` returns the same number without building the list.
- An empty or whitespace `expression` matches nothing, unless a non-empty `filter` is given; then every document
  that passes the filter matches.
- `enableOperators` turns on `AND`, `OR`, `NOT` and parentheses. It is ignored for `WordMatchMethod.Regex`.
- Throws `ArgumentException` when `filter` names a facet that no indexed document carries (see
  [Facet filters](query-semantics.md#facet-filters) for when this is checked).
- Pass no filter, rather than `FacetFilter.None`, when there are no facet criteria; see
  [performance.md](performance.md#per-query).

### `NameMatcher`

Evaluates a query against one text (usually a file name) without an index. `IsMatch` gives the same answer as
`Find` on an index that holds only that text, built with the same `SearchTokenization`: the same operators, globs,
`Exact`/`Within`/`Regex` semantics and Unicode options. Facet criteria are not part of it; check them against the
metadata you already have.

```csharp
static NameMatcher Create(string expression, WordMatchMethod method, bool enableOperators = false, SearchTokenization? tokenization = null);
bool IsMatch(ReadOnlySpan<char> text);
```

- `Create` parses the expression once; `tokenization` defaults to `SearchTokenization.Default`. Throws
  `ArgumentNullException` when `expression` is null. An empty expression or an invalid regex gives a matcher that
  matches nothing.
- Instances are immutable and safe to share between threads.

### `SearchTokenization`

The separator characters used to split indexed text and queries, plus the Unicode options. Every snapshot stores
the preset it was built with, and queries always use the preset of the snapshot they run on.

```csharp
static SearchTokenization Default { get; }    // token-level search
static SearchTokenization FileMask { get; }   // the whole SearchText is one token; queries split on space, CR and LF
static SearchTokenization Create(string indexSeparators, string querySeparators);
SearchTokenization WithUnicodeMatching(UnicodeMatching options);
string IndexSeparators { get; }
string QuerySeparators { get; }
UnicodeMatching UnicodeMatching { get; }      // None for Default, FileMask and Create
```

- `Create` treats a null argument as an empty separator set.
- `WithUnicodeMatching` returns a preset with the same separators and the given options. It throws
  `PlatformNotSupportedException` for `Compose` or `RemoveDiacritics` when the process runs in
  globalization-invariant mode.

Separator tables and the FileMask rules: [Tokenization presets](query-semantics.md#tokenization-presets).

### `UnicodeMatching` (flags)

```csharp
None = 0, Compose = 1, CaseFold = 2, RemoveDiacritics = 4, GraphemeBoundaries = 8
```

`Compose` is NFC composition, `CaseFold` is Unicode case folding, `RemoveDiacritics` removes Latin, Greek and
Cyrillic accents (and implies `Compose`), and `GraphemeBoundaries` keeps `Within` and glob matches on
grapheme-cluster boundaries. Exact effects and caveats: [Unicode matching](query-semantics.md#unicode-matching).

### `IIndexUpdater`, `IndexUpdater`

Changes the index. Calls are serialized, and every successful call publishes a new snapshot through the provider.
A call that throws leaves the stored entries and the published snapshot unchanged. Every mutation rebuilds the
whole index, so prefer the batch methods, or `ProgressiveIndexIngestion` during a scan, over one call per entry.

```csharp
IndexUpdater(IndexSnapshotProvider provider, SearchTokenization? tokenization = null);

// Replace everything
void RebuildFrom(IDictionary<int, string> entries);
void RebuildFrom(IDictionary<int, IndexedEntry> entries);
Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, string>> entries, IProgress<float>? progress = null, CancellationToken ct = default);
Task RebuildFromAsync(IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> entries, IProgress<float>? progress = null, CancellationToken ct = default);

// One entry per call (one rebuild each)
void AddEntry(int id, string text);
void AddEntry(int id, IndexedEntry entry);
bool RemoveEntry(int id);
bool RefreshEntry(int id, string text);
bool RefreshEntry(int id, IndexedEntry entry);

// Many entries per call (one rebuild each)
void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, string>> entries);
void AddOrUpdateEntries(IEnumerable<KeyValuePair<int, IndexedEntry>> entries);
int RemoveEntries(IEnumerable<int> ids);

void Clear();
int EntryCount { get; }
bool ContainsEntry(int id);
```

- `tokenization` defaults to `SearchTokenization.Default` and applies to every snapshot this updater publishes,
  including the empty one after `Clear`.
- A `string` entry is indexed as `new IndexedEntry(text, text)`.
- `AddEntry`, `RefreshEntry` and `AddOrUpdateEntries` add the entry or replace the one with the same id. When the
  input of `AddOrUpdateEntries` or `RebuildFromAsync` holds an id more than once, the last value wins.
  `RefreshEntry` returns whether the id existed before (it adds the entry either way).
- `RemoveEntry` returns `false`, and `RemoveEntries` returns 0, when no id was present; neither rebuilds then.
  `RemoveEntries` returns the number of ids removed.
- The async methods read the whole input before taking the updater's lock.
- The updater keeps its own copy of every entry, and that copy is the source of each rebuild.

### `IIndexSnapshotProvider`, `IndexSnapshotProvider`

Holds the current snapshot. Reading `Current` takes no lock; the updater replaces the reference atomically.

```csharp
IndexSnapshot Current { get; }   // never null; IndexSnapshot.Empty until the first publish
```

### `IndexedEntry`

```csharp
public sealed record IndexedEntry(string SearchText, string SortText, FacetValues? Facets = null);
```

- `SearchText` is tokenized and matched.
- `SortText` is the input of `SearchSortMode.NaturalSortAscending`.
- `Facets` are the optional numeric values that a `FacetFilter` tests.

### `WordMatchMethod`

| Value | Meaning |
|---|---|
| `Exact` | A term matches a whole indexed token. |
| `Within` | A term matches a substring of an indexed token. |
| `Regex` | The whole expression is one .NET regular expression, matched against each indexed token with `Regex.IsMatch` (unanchored, case-insensitive). Operators and query separators do not apply. An invalid pattern or a match timeout returns no results. |

With `Exact` and `Within`, a term that contains `*` or `?` is a glob that must match a whole token. Details:
[Term matching](query-semantics.md#term-matching) and
[Regular expressions](query-semantics.md#regular-expressions-wordmatchmethodregex).

### `SearchSortMode`

| Value | Order |
|---|---|
| `SnapshotOrder` | Order of the documents in the snapshot |
| `NaturalSortAscending` | Natural order of `SortText` (`file2` before `file10`), see [Result ordering](query-semantics.md#result-ordering) |

## Namespace `SearchEngine.Filters`

### `FacetValues`

An immutable set of named `long` values for one document. Names compare ordinally (case-sensitive).

```csharp
static FacetValues Empty { get; }
static FacetValues Create(string facet, long value);
static FacetValues Create(params ReadOnlySpan<KeyValuePair<string, long>> values);
static FacetValues? FromDictionary(IReadOnlyDictionary<string, long>? values);
```

- `Create(span)` returns `Empty` for no values. `FromDictionary` returns `null` for a null or empty dictionary and
  reads the dictionary once, so it is safe with a dictionary that other threads change.
- A null or duplicate name throws `ArgumentException`; `Create(string, long)` throws `ArgumentNullException` for a
  null name.

### `FacetFilter`

Facet predicates combined with AND. The filter applies after the text expression, including `NOT`. A document
without a value for a facet has `0` in that facet.

```csharp
static FacetFilter None { get; }
bool IsEmpty { get; }
static FacetFilter Range(string facet, long minInclusive, long maxInclusive);
static FacetFilter Mask(string facet, long mustHave, long mustNot = 0);
static FacetFilter Combine(params FacetFilter[] filters);
```

- `Range` includes both ends; equality is `Range(f, x, x)`.
- `Mask` matches when `(value & mustHave) == mustHave && (value & mustNot) == 0`.
- `Combine` joins all predicates of its arguments with AND. There is no OR between predicates.

## Namespace `SearchEngine.Snapshots`

### `IndexSnapshot`

An immutable index. Any number of threads may query one snapshot at once.

```csharp
static readonly IndexSnapshot Empty;    // empty snapshot with SearchTokenization.Default
int DocumentCount { get; }
int UniqueWordCount { get; }
SearchTokenization Tokenization { get; }
```

### `IndexSnapshotBuilder`

Builds a snapshot without an updater. To query it, return it from your own `IIndexSnapshotProvider` and pass that
provider to `SearchEngineSharp`.

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

- The entries are enumerated once, in the order that becomes `SnapshotOrder`.
- An id that occurs twice throws `ArgumentException` naming the id. Dictionaries are not checked, because their
  keys are unique.

## Namespace `SearchEngine.Ingestion`

### `ProgressiveIndexIngestion`

Indexes a streaming scan in batches so the index can be queried while the scan runs. Publishing policies and
their tuning: [performance.md](performance.md#tuning-progressive-ingestion).

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

- The entries are added to what the updater already holds; an entry replaces the one with the same id.
- One `IngestAsync` call per instance at a time; the task of a second concurrent call faults with
  `InvalidOperationException`.
  Invalid options throw `ArgumentOutOfRangeException` before the scan starts.
- The scanner writes into a bounded channel and waits when it is full. Queries on the same provider are safe
  throughout.
- `Published`, `onPublished` and `progress` run on the publisher task, not on the caller's synchronization
  context. Marshal to the UI thread yourself.
- When the scan ends, the remaining buffer is published. If the scanner throws, the buffer is published first and
  then the task faults with the scanner's exception.
- Cancellation stops the scan. With `FlushOnCancellation` (default) every accepted entry that is not yet published,
  including entries still in the channel, is published before the task completes; otherwise they are discarded.
  Entries already published stay in the index. The task then completes with `WasCancelled = true`.
- When the updater, a `Published` or `onPublished` handler, or `progress` throws, the publisher stops, the scan is
  cancelled through the token passed to its enumerator, the enumerator is disposed, and the task faults with that
  exception (with the scanner's exception if both failed). Entries already published stay; buffered ones are not
  published.
- The task completes only after the scanner has stopped, so the scanner must observe the token it receives (an
  `async` iterator with an `[EnumeratorCancellation]` parameter does).

### `IngestPublishPolicy`

`Adaptive` (default), `TimeDebounce`, `FixedBatch`, `PerEntry`. When each one publishes:
[Policies](performance.md#policies).

### `IngestPublishOptions`

All properties are `init`-only.

| Property | Default | Meaning |
|---|---|---|
| `Policy` | `Adaptive` | Publish policy |
| `FixedBatchSize` | 2,000 | Batch size of `FixedBatch`; first batch cap of `Adaptive` |
| `GrowthAwareBatchCap` | `true` | `Adaptive`: after each publish the batch cap becomes `max(FixedBatchSize, indexed documents)` |
| `MinInterval` | 100 ms | Shortest time between timer publishes (`TimeDebounce`, `Adaptive`) |
| `AdaptiveMultiplier` | 2.0 | `Adaptive`: a timer publish waits at least this many times the last rebuild duration |
| `MinTimerPublishBatch` | 200 | `Adaptive`: fewest buffered entries for a timer publish; staleness overrides it |
| `MaxStaleness` | 1 s | Longest an accepted entry should wait for its publish to start; see [the worst case](performance.md#maxstaleness-and-the-worst-case) |
| `StrictMaxStaleness` | `false` | `Adaptive`: publish on staleness even right after a rebuild |
| `ChannelCapacity` | 10,000 | Size of the queue between scanner and publisher |
| `FlushOnCancellation` | `true` | Publish accepted entries when the scan is cancelled |

`FixedBatchSize`, `MinTimerPublishBatch` and `ChannelCapacity` must be at least 1, `MaxStaleness` and
`AdaptiveMultiplier` positive, and `MinInterval` not negative.

### `IngestionProgress` (struct)

Reported after each publish.

| Property | Meaning |
|---|---|
| `EntriesAccepted` | Entries taken from the scan so far |
| `EntriesPublished` | Entries published so far |
| `PublishCount` | Publishes so far |
| `PendingCount` | Entries accepted but not yet published (`EntriesAccepted − EntriesPublished`, never below 0) |

### `IngestionPublishEvent` (struct)

| Property | Meaning |
|---|---|
| `EntryCount` | Entries in this publish |
| `IndexedDocumentCount` | Documents in the index after this publish |
| `RebuildDuration` | Wall-clock time of this publish's rebuild |
| `PublishSequence` | Zero-based number of this publish |

### `IngestionResult`

| Property | Meaning |
|---|---|
| `EntriesAccepted` | Entries taken from the scan |
| `EntriesPublished` | Entries published (equals `EntriesAccepted` after a successful scan) |
| `PublishCount` | Number of publishes |
| `TotalRebuildDuration` | Wall-clock time spent in rebuilds, summed over all publishes |
| `MaxObservedStaleness` | Longest observed time between the scanner handing over an entry and the start of the publish that contains it |
| `CompletedSuccessfully` | `true` when the scan ran to its end |
| `WasCancelled` | `true` when cancellation ended the scan |

### `SyntheticPathFeed`

Deterministic synthetic paths (5 to 8 tokens each, joined with `-`) for tests, demos and benchmarks.

```csharp
static IAsyncEnumerable<KeyValuePair<int, string>> EnumerateAsync(int count, int seed = 42, TimeSpan delayPerEntry = default, CancellationToken cancellationToken = default);
static Dictionary<int, string> CreateDictionary(int count, int seed = 42);
```

## Namespace `SearchEngine.Sorting`

### `NaturalSortKeyBuilder`

```csharp
static string Build(string sortText);
```

Returns the key that `NaturalSortAscending` compares ordinally. The rules are in
[Result ordering](query-semantics.md#result-ordering).

## Namespace `SearchEngine.Tokenizer`

### `NameTokenizer`

```csharp
static string TokenizeName(string? name, string joinCharacter = "-");
```

Keeps runs of letters and digits (everything else, including `_`, splits), lowercases them, drops duplicates and
joins them with `joinCharacter`. It is an optional preprocessing step for `SearchText`. Never apply it to a query:
it removes `*` and `?`.

### `TextNormalizer`

The tokenization helpers used by the index builder.

```csharp
delegate void UniqueWordAction<TState>(ref TState state, string word);

static bool IsSeparator(char ch);
static bool IsSeparator(char ch, SearchValues<char> separators);
static List<string> GetWordsFromString(string text);                        // distinct lowercase tokens, longest first
static List<string> GetWordsFromString(string text, SearchValues<char> separators);
static string CreateNormalizedWord(ReadOnlySpan<char> source);              // lowercase copy
static char ToLowerInvariantFast(char ch);
static void ForEachUniqueWord<TState>(string text, HashSet<string> uniqueWords, ref TState state, UniqueWordAction<TState> action);
static void ForEachUniqueWord<TState>(string text, HashSet<string> uniqueWords, SearchValues<char> separators, ref TState state, UniqueWordAction<TState> action);
```

- The overloads without `separators` use the index separators of `SearchTokenization.Default`. For another preset
  pass `SearchValues.Create(tokenization.IndexSeparators)`.
- `ForEachUniqueWord` clears `uniqueWords` and calls `action` once for each distinct lowercase token.
  `uniqueWords` must compare ordinally (`StringComparer.Ordinal` or the default comparer); otherwise it throws
  `ArgumentException`.

## Namespace `SearchEngine.DependencyInjection`

### `ServiceCollectionExtensions`

```csharp
static IServiceCollection AddSearchEngine(this IServiceCollection services);
static IServiceCollection AddSearchEngine(this IServiceCollection services, SearchTokenization tokenization);
static IServiceCollection AddSearchEngine(this IServiceCollection services, IDictionary<int, string> initialEntries);
static IServiceCollection AddSearchEngine(this IServiceCollection services, IDictionary<int, string> initialEntries, SearchTokenization tokenization);
static IServiceCollection AddSearchEngine(this IServiceCollection services, Func<IServiceProvider, IDictionary<int, string>> entriesFactory);
static IServiceCollection AddSearchEngine(this IServiceCollection services, Func<IServiceProvider, IDictionary<int, string>> entriesFactory, SearchTokenization tokenization);
static IServiceCollection AddSearchEngineTransient(this IServiceCollection services);
static IServiceCollection AddSearchEngineTransient(this IServiceCollection services, SearchTokenization tokenization);
static IServiceCollection AddKeyedSearchEngine(this IServiceCollection services, string key);
static IServiceCollection AddKeyedSearchEngine(this IServiceCollection services, string key, SearchTokenization tokenization);
static IServiceCollection AddKeyedSearchEngine(this IServiceCollection services, string key, IDictionary<int, string> initialEntries);
static IServiceCollection AddKeyedSearchEngine(this IServiceCollection services, string key, IDictionary<int, string> initialEntries, SearchTokenization tokenization);
```

- `IndexSnapshotProvider`, `IIndexSnapshotProvider` and `IIndexUpdater` are singletons. `ISearchEngine` is scoped,
  or transient with `AddSearchEngineTransient`.
- The keyed overloads register independent indexes; inject them with `[FromKeyedServices("key")]`.
- The `initialEntries` overloads copy the dictionary when they are called. `entriesFactory` runs once, when the
  index or its updater is first resolved. In both cases the first snapshot is built from these entries with the
  given tokenization (`SearchTokenization.Default` when omitted), and the registered `IIndexUpdater` starts from
  the same entries and the same tokenization. A mutation of the updater therefore extends or changes the initial
  entries; it does not drop them.
- There is no overload that takes a ready-made `IndexSnapshot`. A snapshot keeps neither the search text nor the
  facet values of its entries, so an updater could not start from it.
