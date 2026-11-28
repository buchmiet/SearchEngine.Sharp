# File search guide

End-to-end recipe for a file-manager style "find file" feature: name patterns (globs and
boolean operators), size / date / attribute criteria, and live results while a directory
scan is still running. Content search is out of scope: the index holds names and numeric
metadata only.

Semantics referenced here are specified in [query-semantics.md](query-semantics.md).

## Component wiring

```csharp
using SearchEngine;
using SearchEngine.Ingestion;

var provider = new IndexSnapshotProvider();
var updater = new IndexUpdater(provider);
var engine = new SearchEngineSharp(provider);
var ingestion = new ProgressiveIndexIngestion(updater);
```

One index per search scope. Queries (`engine`) and updates (`updater` / `ingestion`) are
safe to run concurrently; queries always see a complete, immutable snapshot.

## Search modes

A search mode is the triple:

1. `SearchTokenization` preset: how names split into index/query tokens
2. `WordMatchMethod`: `Exact` (whole token) or `Within` (substring inside token/name)
3. `enableOperators`: boolean `AND` / `OR` / `NOT`

Pass the preset to the `IndexUpdater` constructor; every snapshot it publishes carries
that preset, and one snapshot never mixes two. Switching from token search to file masks
at runtime takes a new updater, described after the example.

```csharp
// Classic file mask — whole-name semantics; *.pdf is end-anchored
var provider = new IndexSnapshotProvider();
var updater = new IndexUpdater(provider, SearchTokenization.FileMask);
var engine = new SearchEngineSharp(provider);

updater.RebuildFrom(fileTable); // id → file name
var pdfs = engine.Find("*.pdf", WordMatchMethod.Exact, enableOperators: true);
var system = engine.Find("system", WordMatchMethod.Exact); // whole name only
```

Typical mask UI: `WordMatchMethod.Exact`, `enableOperators: true` for `*.pdf OR *.txt`
style input; `Within` for incremental substring search on the whole name.

To switch presets at runtime, create a new `IndexUpdater` with the other preset over the
same provider and call `RebuildFrom`. The published snapshot replaces the old one
atomically. Do not keep two updaters with different presets active against one provider;
whichever publishes last wins. For both modes side by side, register two independent
provider/updater/engine sets (keyed DI: `AddKeyedSearchEngine("mask",
SearchTokenization.FileMask)`).

## Data model

| File property | Where it goes | Notes |
|---|---|---|
| Row id | dictionary key (`int`) | `Find` returns these ids; keep an id → full path table on your side |
| File name | `IndexedEntry.SearchText` | feed the raw name; it is tokenized on `.` `-` space etc. |
| Display name | `IndexedEntry.SortText` | drives `SearchSortMode.NaturalSortAscending` |
| Size | facet `size` | bytes; `0` for directories |
| Modified time | facet `modified` | `LastWriteTimeUtc.Ticks` (keep all timestamps UTC) |
| Attributes | facet `attrs` | `(long)FileAttributes` bitmask |
| Extension (optional) | facet `ext` | caller-assigned integer id; see [File extensions](#file-extensions) |

Attach every facet to every document. A missing facet value reads as `0` and silently
satisfies any range that includes `0`.

## Index or plain scan?

Decide this first; it matters more than any tuning below.

| Situation | Use | Why |
|-----------|-----|-----|
| One-shot search of a tree ("find files named `*.pdf` under this folder"), result thrown away afterwards | Plain scan with `NameMatcher` | Every name has to be read from disk once anyway, and matching it while it is read costs less than indexing it. At 1 million files the scan takes about 1 s and a few tens of MB; scanning into an index takes over 10 s and close to 2 GB of allocations. |
| An index kept and queried many times over the same tree (filter as you type, a drive kept in memory) | `IndexUpdater` and `SearchEngineSharp` | A query over 1 million names takes 0.1 to 17 ms instead of a rescan. Building the index costs about 5 to 9 rescans, so it pays off after that many queries on a tree that does not change in between. |
| A kept index over files that change often | `IndexUpdater`, one call per change or per batch of changes | A change builds only the entries changed since the last full build of the index; the whole index (about 2 s at 1 million names) is built again once every 1,000 to 2,000 changes at that size. Changes that arrive together go in one `AddOrUpdateEntries` or `RemoveEntries` call. |

The measurements behind these numbers: [Index or plain scan](performance.md#index-or-plain-scan).

## Plain scan (one-shot search)

`NameMatcher` evaluates a query against one name with exactly the semantics of `Find` on an index built with the
same `SearchTokenization` (same boolean grammar, globs, regex, Unicode options), without building anything:

```csharp
using System.IO.Enumeration;
using SearchEngine;

static IEnumerable<string> FindFiles(string root, string query, long minSize, CancellationToken ct)
{
    var matcher = NameMatcher.Create(query, WordMatchMethod.Exact, enableOperators: true, SearchTokenization.FileMask);
    var options = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,              // skip folders we may not read
        AttributesToSkip = FileAttributes.None, // default skips Hidden|System
    };

    var files = new FileSystemEnumerable<string>(root, static (ref FileSystemEntry e) => e.ToFullPath(), options)
    {
        // Every entry passes through here, so cancellation is observed even when nothing matches.
        // Name first: entries that do not match never cost a stat call.
        ShouldIncludePredicate = (ref FileSystemEntry e) =>
        {
            ct.ThrowIfCancellationRequested();
            return !e.IsDirectory && matcher.IsMatch(e.FileName) && e.Length >= minSize;
        },
        // Do not follow directory symlinks / junctions: loops (/usr/bin/X11 -> .) and duplicates.
        ShouldRecursePredicate = static (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
    };

    return files;
}
```

- One enumeration pass: the directory listing already says whether an entry is a directory; size, time and
  attributes are read from the same entry (one `lstat` on Unix, none on Windows) and only for names that match.
  Do not call `Directory.Exists(path)` and `new FileInfo(path)` per entry: that is two extra system calls per
  file (100k files: 540k system calls instead of 53k), and `Directory.Exists` is true for a symlink to a directory.
- Recursive enumeration in .NET follows directory symlinks unless `ShouldRecursePredicate` says otherwise; on a
  stock Ubuntu `/usr` that never ends, on Homebrew it returns every file several times.
- A file that disappears during the scan does not throw from `FileSystemEntry` (measured: on Linux it reports size 0
  and time 1601-01-01, i.e. `DateTimeOffset.FromFileTime(0)`; on macOS the values read with the listing). Skip such
  entries if it matters; the `FileInfo` pattern instead throws from `Length` and, outside a `try`, ends the scan.
- For many cores, run one non-recursive `FileSystemEnumerable` per directory on a few workers (measured 1.8× on
  4 x64 cores, 2.5 to 4× on an 8-core Mac; slower on a single core).
- On macOS file names may be decomposed (NFD). Use
  `SearchTokenization.FileMask.WithUnicodeMatching(UnicodeMatching.Compose)` for the matcher (and the index) so a
  composed query finds them.

## Scanning into a persistent index

Do not call `AddEntry` per file: each call publishes a snapshot, and the whole index is built again every few
hundred to few thousand calls, so a long scan costs many full builds. Stream the scan through
`ProgressiveIndexIngestion` instead. The scanner owns the id → path table and hands
each path to the consumer through a thread-safe map; do not share a plain `List<string>` between the scanner
(which runs on the ingestion's producer task) and the code that reads results:

```csharp
using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using SearchEngine;
using SearchEngine.Filters;
using SearchEngine.Ingestion;

static async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> ScanAsync(
    string root,
    ConcurrentDictionary<int, string> paths, // id → full path; safe to read while the scan runs
    [EnumeratorCancellation] CancellationToken ct = default)
{
    await Task.Yield();

    var options = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.None,
    };

    var files = new FileSystemEnumerable<(string Path, int NameStart, long Size, long Modified, long Attrs)>(
        root,
        static (ref FileSystemEntry e) =>
        {
            string path = e.ToFullPath();
            return (path, path.Length - e.FileName.Length, e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.UtcTicks, (long)e.Attributes);
        },
        options)
    {
        // Files and directories are indexed; the `attrs` facet tells them apart.
        ShouldRecursePredicate = static (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
    };

    int id = paths.Count; // continue after the ids of earlier scans into the same scope
    foreach (var f in files)
    {
        ct.ThrowIfCancellationRequested();
        string name = f.Path[f.NameStart..];
        var facets = FacetValues.Create([new("size", f.Size), new("modified", f.Modified), new("attrs", f.Attrs)]);

        paths[id] = f.Path;
        yield return new(id++, new IndexedEntry(name, name, facets));
    }
}
```

```csharp
var paths = new ConcurrentDictionary<int, string>();

IngestionResult result = await ingestion.IngestAsync(
    ScanAsync(root, paths, ct),
    onPublished: _ => ScheduleRequery(),
    cancellationToken: ct);
```

The default options publish about 7 times for 100,000 files on a fast scan and 14 times for 1 million. An entry
becomes searchable within `MaxStaleness` (1 s) until one publish takes longer than about a third of that; for
larger indexes the delay grows with the publish time. The options and the exact bound:
[Tuning progressive ingestion](performance.md#tuning-progressive-ingestion).

Starting a new search scope: `updater.Clear()`, a new `paths` map, start a new
ingest. Ids must be unique within one scope, and reusing an id replaces the entry.

## Live requery loop

Re-run the query whenever the user edits the criteria (debounce 150 to 300 ms) and whenever
a publish lands (`onPublished` above). Both events converge on the same code path:

```csharp
void Requery()
{
    List<int> ids = Search(engine, criteria);
    // map ids → paths[id] (ConcurrentDictionary) and update the result list
}
```

- `onPublished` and the `Published` event run on the ingestion's publisher task, not on
  your UI thread. Marshal before touching UI state.
- `CountMatches` with the same arguments gives the total for a status bar without
  materializing the id list.

## Translating UI criteria into a query

```csharp
using SearchEngine.Filters;

sealed record SearchCriteria(
    string NameQuery,                       // e.g. "report* AND NOT draft"
    long? MinSize, long? MaxSize,           // bytes
    DateTime? ModifiedFromUtc, DateTime? ModifiedToUtc,
    FileAttributes MustHave, FileAttributes MustNot);

static FacetFilter BuildFilter(SearchCriteria c)
{
    var parts = new List<FacetFilter>();

    if (c.MinSize is not null || c.MaxSize is not null)
        parts.Add(FacetFilter.Range("size", c.MinSize ?? 0, c.MaxSize ?? long.MaxValue));

    if (c.ModifiedFromUtc is not null || c.ModifiedToUtc is not null)
        parts.Add(FacetFilter.Range(
            "modified",
            (c.ModifiedFromUtc ?? DateTime.MinValue).Ticks,
            (c.ModifiedToUtc ?? DateTime.MaxValue).Ticks));

    if (c.MustHave != 0 || c.MustNot != 0)
        parts.Add(FacetFilter.Mask("attrs", (long)c.MustHave, (long)c.MustNot));

    return FacetFilter.Combine(parts.ToArray());
}

static List<int> Search(ISearchEngine engine, SearchCriteria c)
{
    return engine.Find(c.NameQuery, WordMatchMethod.Within,
        enableOperators: true, SearchSortMode.NaturalSortAscending, BuildFilter(c));
}
```

Notes on the choices:

- `WordMatchMethod.Within` gives find-as-you-type behavior: a plain term `foo` matches
  like `*foo*`. Terms containing `*` / `?` are matched as anchored globs regardless of
  the method, so both styles coexist in one expression.
- An empty `NameQuery` with a non-empty filter is valid: it returns all documents that
  pass the filter (search by date/size alone, no name pattern).

### Common criteria recipes

| Criterion | Filter |
|---|---|
| At least 10 MB | `FacetFilter.Range("size", 10L << 20, long.MaxValue)` |
| Modified on a given day | `FacetFilter.Range("modified", day.Ticks, day.AddDays(1).Ticks - 1)` |
| Modified in the last 7 days | `FacetFilter.Range("modified", DateTime.UtcNow.AddDays(-7).Ticks, long.MaxValue)` |
| Files only | `FacetFilter.Mask("attrs", 0, (long)FileAttributes.Directory)` |
| Directories only | `FacetFilter.Mask("attrs", (long)FileAttributes.Directory)` |
| Exclude hidden and system | `FacetFilter.Mask("attrs", 0, (long)(FileAttributes.Hidden \| FileAttributes.System))` |
| Read-only archives | `FacetFilter.Mask("attrs", (long)(FileAttributes.ReadOnly \| FileAttributes.Archive))` |

### File extensions

Default preset: `*.txt` in a query splits on the `.` separator into `* AND txt`,
which means "name contains the token `txt`". This is usually good enough, but `notes.txt.bak`
also matches. For anchored extension filtering, encode the extension as an integer-id facet
(see below) or switch to FileMask mode.

FileMask preset: `*.pdf` is a single glob term anchored on the whole file name.
End-anchored extension filtering works without an `ext` facet. Example:

```csharp
var updater = new IndexUpdater(provider, SearchTokenization.FileMask);
updater.RebuildFrom(fileTable);
var pdfs = engine.Find("*.pdf", WordMatchMethod.Exact, enableOperators: true);
```

Default preset, ext facet workaround (only when staying on token semantics):

```csharp
// During the scan (ids start at 1 so 0 keeps meaning "none"):
var extIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
long GetExtId(string ext) =>
    ext.Length == 0 ? 0
    : extIds.TryGetValue(ext, out long id) ? id
    : extIds[ext] = extIds.Count + 1;

// facet: ["ext"] = GetExtId(info.Extension)
// filter (equality as a degenerate range):
long pdf = GetExtId(".pdf");
var onlyPdf = FacetFilter.Range("ext", pdf, pdf);
```

Intercept `*.ext`-shaped input in the UI layer and translate it to this filter instead
of passing it through as a text expression.

## Performance

Name queries other than regexes take less than 2 ms at 100,000 names and less than 20 ms at 1 million
([Query latency](performance.md#query-latency)). Two costs show up in a live result list:

- After a full build, the first `Within` query builds the bigram index, and the first natural sort of a result
  that holds more than 80% of the names builds the sort permutation. Publishes of small changes keep both
  ([Work deferred to the first query](performance.md#work-deferred-to-the-first-query)).
- Until the permutation exists, `NaturalSortAscending` sorts the hits of every query: about 4 ms for 10,000 hits
  and 65 ms for 100,000 hits. Once it exists, a result of more than 0.5% of the names walks it instead. For large
  result lists before that, show them in snapshot order first or page them.

## Pitfalls checklist

- Missing facet values read as `0`; attach every facet to every document.
- Facet names are case-sensitive; pick one spelling (`size`, not `Size`) and stick to it.
- A facet name that no document carries also reads as `0`, without an exception, so a
  misspelled name filters silently. Build filters only from facets your scanner writes.
- Keep all timestamp facets in UTC; mixing kinds shifts range boundaries.
- `*.txt` has token semantics under Default, not end-anchored semantics. Use
  FileMask or an `ext` facet; see [File extensions](#file-extensions).
- A literal `*` or `?` cannot be searched for; on the query side they are always
  wildcards.
- Do not pre-process query strings with `NameTokenizer.TokenizeName`: it strips
  `*` and `?`. Feed raw file names to the index and raw user input to `Find`.
