# Changelog

All notable changes to SearchEngine.Sharp. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [0.6.0] - 2026-11-24

Requires .NET 11. Code written for 0.5.6 may need changes: two members of `IngestionResult` are renamed, one
`AddSearchEngine` overload is replaced, and most methods now throw `ArgumentNullException` or `ArgumentException`
for invalid arguments. Every change to what shipped in 0.5.6 is listed under Changed.

### Added

- `NameMatcher` evaluates a query against a single name, with the same result as `Find` on an index that holds
  only that name (same tokenization preset, operators, globs, regex and Unicode options). Use it to filter names
  during a one-shot directory scan instead of building an index.
- `UnicodeMatching` and `SearchTokenization.WithUnicodeMatching(...)`: opt-in NFC composition, case folding
  (`ß`→`ss`, `ς`→`σ`, `İ`→`i` and more), accent removal and grapheme-cluster boundaries for `Within` and glob
  matches. The default behaviour is unchanged. `Compose` and `RemoveDiacritics` need Unicode normalization, so
  `WithUnicodeMatching` throws `PlatformNotSupportedException` for them in globalization-invariant mode.
  `RemoveDiacritics` strips only Latin, Greek and Cyrillic accents, and it folds `й` and `ў` into `и` and `у`, so
  it is unsuitable for Russian and Belarusian text. See
  [Unicode matching](docs/query-semantics.md#unicode-matching).
- `FacetValues.Create(params ReadOnlySpan<KeyValuePair<string, long>>)` builds a facet bag without an intermediate
  dictionary. A duplicate or null name throws `ArgumentException`.
- `IngestPublishPolicy.Adaptive.StrictMaxStaleness` (default `false`) brings back the staleness behaviour of 0.5.6
  (see Changed).

### Changed

- Lowercasing (tokens, query terms, `NameMatcher`, natural sort keys) and `UnicodeMatching.CaseFold` use Unicode
  17.0.0 tables built into the library instead of the casing data of the platform, so Windows NLS, every ICU
  version and globalization-invariant mode give the same tokens and the same sort order. Lowercase stays per
  UTF-16 code unit. `İ` (U+0130) now lowercases to `i`, as Unicode defines it (`char.ToLowerInvariant` kept it), so
  `İstanbul` matches `istanbul` without any option. Characters the platform cased differently from Unicode 17
  (for example letters added in recent Unicode versions on an older ICU) now lowercase the same everywhere;
  rebuild indexes that were built with 0.5.6 if they hold such names.
- `WordMatchMethod.Regex` takes its case-insensitivity from the same Unicode 17.0.0 tables instead of
  `RegexOptions.IgnoreCase`: the characters of the pattern get the casing of indexed tokens and the pattern runs
  case-sensitively. `İstanbul` now finds `istanbul`, and under `CaseFold` a pattern no longer has to be written
  folded: `Straße`, `σας` and `ﬃ` find `strasse`, `σασ` and `ffi`. See
  [Regular expressions](docs/query-semantics.md#regular-expressions-wordmatchmethodregex).
- `IngestionResult.TotalRebuildCpu` is renamed `TotalRebuildDuration` and `IngestionResult.WorstCaseStaleness` is
  renamed `MaxObservedStaleness`. The first never measured CPU time: it is the wall-clock time of the rebuilds,
  the sum of `IngestionPublishEvent.RebuildDuration`. The second is the longest staleness seen during the scan, not
  a bound. Neither value changed; code written for 0.5.6 must use the new names, and the old names no longer exist.
- The target framework is `net11.0` and the dependency `Microsoft.Extensions.DependencyInjection` is 11.0.0.
  Applications on .NET 10 stay on 0.5.6. Moving to .NET 11 needs no source changes.
- Progressive ingestion with the `Adaptive` policy: a publish triggered by `MaxStaleness` also waits
  `RebuildMultiplier` times the last rebuild duration after that rebuild. Large scans no longer degrade into
  back-to-back full rebuilds once one rebuild takes longer than `MaxStaleness` (a 1 million file scan made 57 to
  74 rebuilds and allocated about 30 GB; it now makes 13 to 18). The worst-case staleness is
  `max(MaxStaleness, (1 + RebuildMultiplier) × rebuild duration)`; nothing changes while a rebuild takes less
  than `MaxStaleness / (1 + RebuildMultiplier)`. `StrictMaxStaleness = true` restores the old schedule.
- `IngestPublishPolicy` is a closed record hierarchy instead of an enum, and each policy carries only its own
  settings: `IngestPublishPolicy.Adaptive`, `TimeDebounce`, `FixedBatch` and `PerEntry`. `IngestPublishOptions`
  keeps `Policy`, `ChannelCapacity` and `FlushOnCancellation`; the other properties moved to the policies.
  `FixedBatchSize` is `Adaptive.BatchCap` or `FixedBatch.BatchSize`, and `AdaptiveMultiplier` is
  `Adaptive.RebuildMultiplier`. `TimeDebounce` has no batch size: it never published by size, and setting
  `FixedBatchSize` for it did nothing. `PerEntry` has no settings. `IngestPublishOptions` and the policies are
  records, so `with` works on them, and every setting is checked when it is set: an invalid value throws
  `ArgumentOutOfRangeException` naming the property where it is set, not when `IngestAsync` starts. A `switch`
  over the four policies needs no default arm.

  ```csharp
  // 0.5.6
  var options = new IngestPublishOptions
  {
      Policy = IngestPublishPolicy.FixedBatch,
      FixedBatchSize = 400,
      MaxStaleness = TimeSpan.FromSeconds(2),
      ChannelCapacity = 1_000,
  };

  // 0.6.0
  var options = new IngestPublishOptions
  {
      Policy = new IngestPublishPolicy.FixedBatch { BatchSize = 400, MaxStaleness = TimeSpan.FromSeconds(2) },
      ChannelCapacity = 1_000,
  };
  ```
- The bigram index used by `WordMatchMethod.Within` is built on the first `Within` query against a snapshot
  instead of in every build. Builds are faster, and the first `Within` query after each publish pays the cost
  (0.1 to 0.5 s at 1 million names, depending on the number of distinct tokens). Concurrent first callers share one build.
- `TextNormalizer.ForEachUniqueWord` requires a set that compares ordinally and throws `ArgumentException`
  otherwise.
- `NaturalSortAscending` compares sort texts part by part: text parts with text parts, numbers by value at any
  length, and digits of other scripts with their value. It used to compare a text part with the separator that
  followed a shorter part, so `ab` sorted before `a 2`, `xa` before `x1` and `reports` before `report final`, and
  `a999999999999` sorted after `a1000000000000`. `NaturalSortKeyBuilder.Build` returns keys in a new, shorter
  layout; keys stored by an earlier version do not compare with new ones. The order is described in
  [Result ordering](docs/query-semantics.md#result-ordering).
- `NaturalSortAscending` returns entries with equal sort texts in ascending id order. They used to come in an
  unspecified order when the query matched at most 80% of the documents and in id order otherwise, so the same
  entries could come out in a different order from one query to the next.
- `SearchTokenization.Default` splits queries at a tab too, like indexed text. A query pasted with a tab
  between its words used to be one term that could never match. `FileMask` is unchanged: a tab stays part of the
  mask.
- The `IProgress<float>` of `IndexSnapshotBuilder.Build` and `IIndexUpdater.RebuildFromAsync` is called when the
  value reaches a new whole percent, at most 102 times per build, and the last report is 100. It used to be called
  once per entry and once per distinct token (about 209,000 calls for 100,000 file names), which flooded the UI
  thread when the handler was a `Progress<T>`. The values are cumulative, so nothing is lost between reports.
- Every public entry point throws `ArgumentNullException` naming the parameter when a reference argument that is
  not declared nullable is `null`, at the call. Before, most methods threw `NullReferenceException` from inside the
  library, and some failed later: `FacetFilter.Range(null, ...)` and `Mask(null, ...)` at query time,
  `new IndexedEntry(null, ...)` at the next rebuild, `RebuildFromAsync(null)` and `IngestAsync(null)` through the
  returned task. A `null` element in a collection argument (a `null` text or `IndexedEntry` in the entries) throws
  `ArgumentException` naming the collection, and an updater call that receives one leaves the index unchanged.
  Arguments that used to accept `null` as a synonym for an empty value now throw: the expression of `Find` and
  `CountMatches` (a `null` expression used to match nothing), the separators of `SearchTokenization.Create`, the
  text of `NaturalSortKeyBuilder.Build`, `TextNormalizer.GetWordsFromString` and `ForEachUniqueWord`, and the
  `joinCharacter` of `NameTokenizer.TokenizeName`. Pass an empty string instead. Parameters declared nullable keep
  their meaning. See [Argument checks](docs/api.md#argument-checks).
- `IIndexUpdater.RebuildFromAsync` no longer resumes on the caller's synchronization context after reading the
  stream, so the rebuild does not run on a UI thread.
- `IndexSnapshotBuilder.Build` throws `ArgumentException` naming the id when an id occurs more than once in the
  entries. Such an id used to become two documents, and queries returned it twice. Dictionaries are not checked,
  because their keys are unique. The updater's batch methods are unchanged: when their input repeats an id, the
  last value wins.
- `SyntheticPathFeed.EnumerateAsync` throws `ArgumentOutOfRangeException` naming `count` when the count is
  negative, at the call, like `CreateDictionary`. It used to yield nothing.
- `IngestionProgress.PendingCount` reports the entries accepted but not yet published, `EntriesAccepted` minus
  `EntriesPublished` (it was always 0).
- `AddSearchEngine(Func<IServiceProvider, IndexSnapshot>)` (0.5.6) is replaced by `AddSearchEngine(Func<IServiceProvider,
  IDictionary<int, string>>)` and a variant with a `SearchTokenization`. A snapshot keeps neither the search text
  nor the facet values of its entries, so the updater cannot start from one; the factory now returns the entries,
  and the index and the updater are both built from them. The index with initial entries is built when the
  provider is first resolved instead of when `AddSearchEngine` is called.
- With `enableOperators: true`, parentheses group terms only when both `(` and `)` are query separators of the
  preset. In a preset made by `SearchTokenization.Create` with only one of them, that one splits terms like a space
  and the other is part of its term. Such presets used to throw `ArgumentOutOfRangeException` for `(a) b` or leave
  `NOT` unapplied in `NOT (a b)`. `Default` and `FileMask` are unchanged.
- A glob `?` matches one code point instead of one UTF-16 code unit, so `?.txt` matches a name made of one emoji and
  `.txt`, which used to need `??.txt`. With `UnicodeMatching.GraphemeBoundaries`, `?` matches one grapheme cluster
  (`caf?.txt` matches a decomposed `café.txt`). A `*` and a `Within` match no longer start or end between the two
  halves of a surrogate pair.
- Creating an `IndexUpdater` publishes an empty snapshot with its preset, and the providers registered by
  `AddSearchEngine` and `AddKeyedSearchEngine` start with an empty snapshot of the registered preset. Until the
  first change, `IndexSnapshotProvider.Current` used to be `IndexSnapshot.Empty` with `SearchTokenization.Default`,
  whatever preset the index was configured with.

### Fixed

- `Find` and `CountMatches` with `enableOperators: true` threw `ArgumentOutOfRangeException` for expressions
  that start with `NOT AND` or `NOT OR`, and returned nothing for misplaced operators such as `a NOT AND b`,
  `(AND a)`, `(a OR) b` or `a () b`. Such expressions are now corrected by the
  [documented rules](docs/query-semantics.md#query-parsing-default-preset), also inside parentheses.
- A document whose tokens are equal only after lowercasing (`Straße STRAẞE`, `k` and the Kelvin sign) was
  returned twice by `Find` and counted twice by `CountMatches`. The second of two tokens that differ in lowercase
  but compare equal case-insensitively (`ς σ`, `µ μ`, `β ϐ`) was not indexed.
- `ProgressiveIndexIngestion.IngestAsync` never completed when the publisher failed (an exception from the
  updater, a `Published` or `onPublished` handler, or the progress callback) while the scanner was blocked on a
  full channel. It now cancels the scanner and faults with the original exception.
- A NaN, infinite or very large `AdaptiveMultiplier` was accepted and made the ingestion fault at its first timer
  publish. The multiplier, now `IngestPublishPolicy.Adaptive.RebuildMultiplier`, must be positive and at most
  `MaxRebuildMultiplier` (1,000) and throws `ArgumentOutOfRangeException` otherwise.
- A progress report of `ProgressiveIndexIngestion` could count one entry more in `EntriesPublished` than in
  `EntriesAccepted`: the scanner counted an entry after the publisher could already take and publish it. An entry
  is now counted before it enters the channel.
- Progressive ingestion could leave buffered entries unpublished while the scanner was idle. A scan error no
  longer skips the final flush, and a cancelled ingestion with `FlushOnCancellation` also publishes the entries
  still queued between scanner and publisher.
- `IndexUpdater` mutations are atomic: when building the new snapshot throws (for example for an invalid facet
  bag), the entries and the published snapshot stay unchanged. The batch methods read their whole input before
  they change anything, so an input that throws while it is read, even after it called the updater itself, leaves
  the index as it was. `Clear` and removing the last entry keep the updater's tokenization preset and Unicode
  options.
- `FacetValues.FromDictionary` reads the dictionary once, so a dictionary that changes during the call can no
  longer produce a broken bag or `IndexOutOfRangeException`. It rejects null and duplicate names with
  `ArgumentException`, like `FacetValues.Create`.
- `SyntheticPathFeed.CreateDictionary` names `count` in the `ArgumentOutOfRangeException` for a negative count. It
  used to name `capacity`, a parameter of the dictionary it creates.
- Concurrent first `NaturalSortAscending` queries on a new snapshot each built their own sort permutation; they
  now share one build.
- `AddSearchEngine(initialEntries, ...)` and `AddKeyedSearchEngine(key, initialEntries, ...)` published a first
  snapshot, but the registered `IIndexUpdater` did not hold those entries, so the first `AddEntry`, `RemoveEntry`
  or batch update published an index without them. The updater now starts from the same entries and the same
  tokenization as the first snapshot.
- `WordMatchMethod.Within` did not find a term that starts with two U+FFFF characters in a token that also starts
  with them.

### Performance

- A facet filter combined with a text query checks only the text hits when they are up to 75% of the index (it
  was 50% in 0.5.6). Between 50% and 75% such queries filter faster: 18% faster at 65% on 100,000 file names.
  Above 80%, scanning every document is still faster, and those queries keep the scan.
- Counting the hits of a result set is 22-33% faster on ARM64 (Apple M1) and up to 20% faster on x64.
- Natural sort walks the snapshot's sort permutation for every result above 0.5% of the documents once a query has
  built it, instead of sorting the hits up to 80%. On 100,000 names: 1% of the documents 304 us to 146 us, 10%
  3.4 ms to 0.25 ms, 50% 19.1 ms to 0.65 ms.
- At 1 million names on x64: suffix and infix globs 3 to 5 times faster (`*.pdf` on whole file names 35 ms to
  7 ms), `Within` 1.2 to 1.8 times faster for terms of three or more characters and about 4 times for one
  character, snapshot build allocations 812 MB to 343 MB and peak memory 1.3 GB to
  0.8 GB for file names with facets. Measurements and how to reproduce them: [docs/performance.md](docs/performance.md).

## [0.5.6] - 2026-08-16

### Added

- `IngestPublishOptions.GrowthAwareBatchCap` (default `true`): under the `Adaptive` policy the batch cap grows
  after each publish to `max(FixedBatchSize, indexed document count)`. A fast scan of 100,000 files publishes about
  7 times instead of 50. Set it to `false` for fixed batches of `FixedBatchSize`.

### Performance

- A single exact term with a facet filter checks the facet values of the matching documents only, also with
  `enableOperators: true`.
- A facet filter on a sparse result checks only the hits; `NaturalSortAscending` on a result below 80% of the
  documents sorts only the hits instead of building the full sort permutation.
- Results are collected from the result bitset without scanning every document.

## [0.5.5] - 2026-07-03

### Changed

- Breaking: `WordMatchMethod.Regex` uses unanchored `Regex.IsMatch` on indexed tokens; the pattern is compiled as
  written. Add `^...$` when you need a whole-token match (0.5.4 wrapped every pattern in `^(?:...)$`).
- Patterns that `NonBacktracking` does not support (lookarounds, backreferences) run on the backtracking engine
  with the same options and a 1 s match timeout instead of returning no results.
- A `RegexMatchTimeoutException` during matching returns an empty result instead of reaching the caller.

## [0.5.4] - 2026-07-03

### Added

- `WordMatchMethod.Regex`: the whole expression is one .NET regular expression matched against indexed tokens
  (`IgnoreCase | CultureInvariant | NonBacktracking`, 1 s match timeout). Boolean operators and query separators
  do not apply.
- The 8 most recently used regex patterns are cached in compiled form.
- Invalid patterns and constructs that `NonBacktracking` does not support return an empty result instead of
  throwing.

## [0.5.3] - 2026-07-01

### Added

- `SearchTokenization` presets (`Default`, `FileMask`, `Create`), stored per snapshot, with overloads on
  `IndexUpdater`, `IndexSnapshotBuilder` and `AddSearchEngine`.
- `SearchTokenization.FileMask`: a bare term matches the whole name, `*.pdf` is anchored at the end, and query
  terms split on whitespace only.

## [0.5.2] - 2026-07-01

### Added

- Glob terms: query terms with `*` or `?` match whole tokens as globs, also inside boolean expressions.
- Numeric facets on `IndexedEntry` and `FacetFilter` (inclusive ranges and bitmasks, combined with AND), with
  `Find` and `CountMatches` overloads that take a filter.

## [0.5.1] - 2026-07-01

### Added

- `ProgressiveIndexIngestion` indexes a long scan in batches without rebuilding the index for every entry.
- `IngestPublishPolicy` and `IngestPublishOptions`: fixed batch, time debounce and adaptive publish policies
  (default: adaptive, 2,000 entry batches, 1 s staleness cap).
- `SyntheticPathFeed`: synthetic tokenized paths for tests, benchmarks and demos.
- `demos/ProgressiveIngestion.Demo`, a console demo of match counts growing during a scan.

## [0.5.0] - 2026-06-24

### Added

- Initial release: in-memory inverted index with exact and substring token search, boolean operators, natural
  sort, immutable snapshots for concurrent reads, and dependency injection registration.

[Unreleased]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.6.0...HEAD
[0.6.0]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.6...v0.6.0
[0.5.6]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.5...v0.5.6
[0.5.5]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.4...v0.5.5
[0.5.4]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.3...v0.5.4
[0.5.3]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.2...v0.5.3
[0.5.2]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.1...v0.5.2
[0.5.1]: https://github.com/buchmiet/SearchEngine.Sharp/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/buchmiet/SearchEngine.Sharp/releases/tag/v0.5.0
