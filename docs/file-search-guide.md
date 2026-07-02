# File search guide

End-to-end recipe for a file-manager style "find file" feature: name patterns (globs and
boolean operators), size / date / attribute criteria, and live results while a directory
scan is still running. Content search is out of scope — the index holds names and numeric
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

A **search mode** is the triple:

1. **`SearchTokenization` preset** — how names split into index/query tokens
2. **`WordMatchMethod`** — `Exact` (whole token) or `Within` (substring inside token/name)
3. **`enableOperators`** — boolean `AND` / `OR` / `NOT`

Configure the preset on `IndexUpdater` at construction; every rebuild publishes a snapshot
carrying the same preset. Switching modes (e.g. token search ↔ classic file mask) requires
one `RebuildFrom` from your file table — you cannot mix presets in one snapshot.

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
same provider and call `RebuildFrom` — the published snapshot replaces the old one
atomically. Do not keep two updaters with different presets active against one provider;
whichever rebuilds last wins. For both modes side by side, register two independent
provider/updater/engine sets (keyed DI: `AddKeyedSearchEngine("mask",
SearchTokenization.FileMask)`).

## Data model

| File property | Where it goes | Notes |
|---|---|---|
| Row id | dictionary key (`int`) | `Find` returns these ids; keep an id → full path table on your side |
| File name | `IndexedEntry.SearchText` | feed the raw name; it is tokenized on `.` `-` space etc. |
| Display name | `IndexedEntry.SortText` | drives `SearchSortMode.NaturalSortAscending` |
| Size | facet `size` | bytes; `0` for directories |
| Modified time | facet `modified` | `LastWriteTimeUtc.Ticks` — keep all timestamps UTC |
| Attributes | facet `attrs` | `(long)FileAttributes` bitmask |
| Extension (optional) | facet `ext` | caller-assigned integer id; see [File extensions](#file-extensions) |

Attach every facet to every document. A missing facet value reads as `0` and silently
satisfies any range that includes `0`.

## Scanning with live indexing

Do not call `AddEntry` per file — each call rebuilds the whole index and total work grows
O(N²). Stream the scan through `ProgressiveIndexIngestion`:

```csharp
using System.Runtime.CompilerServices;

static async IAsyncEnumerable<KeyValuePair<int, IndexedEntry>> ScanAsync(
    string root,
    List<string> paths, // id → full path, owned by the caller
    [EnumeratorCancellation] CancellationToken ct = default)
{
    await Task.Yield();

    var options = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.None, // default skips Hidden|System
    };

    foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", options))
    {
        ct.ThrowIfCancellationRequested();

        var info = new FileInfo(path);
        bool isDirectory = (info.Attributes & FileAttributes.Directory) != 0;

        var facets = FacetValues.FromDictionary(new Dictionary<string, long>
        {
            ["size"] = isDirectory ? 0 : info.Length,
            ["modified"] = info.LastWriteTimeUtc.Ticks,
            ["attrs"] = (long)info.Attributes,
        });

        int id = paths.Count;
        paths.Add(path);

        yield return new(id, new IndexedEntry(info.Name, info.Name, facets));
    }
}
```

```csharp
var paths = new List<string>();

IngestionResult result = await ingestion.IngestAsync(
    ScanAsync(root, paths, ct),
    onPublished: _ => ScheduleRequery(),
    cancellationToken: ct);
```

Defaults (`IngestPublishPolicy.Adaptive`) target under 1 s between a file being scanned
and becoming searchable, at roughly 50 index publishes per 100k files. Tunables:
[ingestion-policy-report.md](ingestion-policy-report.md).

Starting a new search scope: `updater.Clear()`, reset the `paths` table, start a new
ingest. Ids must be unique within one scope — reusing an id replaces the entry.

## Live requery loop

Re-run the query whenever the user edits the criteria (debounce 100–200 ms) and whenever
a publish lands (`onPublished` above). Both events converge on the same code path:

```csharp
void Requery()
{
    List<int> ids = Search(engine, criteria);
    // map ids → paths[id] and update the result list
}
```

Two things to know:

- `onPublished` and the `Published` event run on the ingestion's publisher task, not on
  your UI thread — marshal before touching UI state.
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
    FacetFilter filter = BuildFilter(c);

    // The filter overloads always run the full pipeline; keep the fast path
    // for pure text queries by calling the overload without a filter.
    return filter.IsEmpty
        ? engine.Find(c.NameQuery, WordMatchMethod.Within,
            enableOperators: true, SearchSortMode.NaturalSortAscending)
        : engine.Find(c.NameQuery, WordMatchMethod.Within,
            enableOperators: true, SearchSortMode.NaturalSortAscending, filter);
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

**Default preset:** `*.txt` in a query splits on the `.` separator into `* AND txt`,
which means "name contains the token `txt`" — usually good enough, but `notes.txt.bak`
also matches. For anchored extension filtering, encode the extension as an integer-id facet
(see below) or switch to **FileMask** mode.

**FileMask preset:** `*.pdf` is a single glob term anchored on the whole file name —
end-anchored extension filtering works without an `ext` facet. Example:

```csharp
var updater = new IndexUpdater(provider, SearchTokenization.FileMask);
updater.RebuildFrom(fileTable);
var pdfs = engine.Find("*.pdf", WordMatchMethod.Exact, enableOperators: true);
```

**Default preset — ext facet workaround** (only when staying on token semantics):

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

## Performance expectations

Measured numbers: [glob-and-facets-report.md](glob-and-facets-report.md). Orientation
points from that report (synthetic data, single thread):

- Text-only queries at 100k documents: thousands to hundreds of thousands of queries/s
  depending on match method.
- With a facet filter at 250k documents the slowest mode (filter-only, all documents
  considered) still completes in ~2.5 ms — comfortably per-keystroke territory.
- Facet filtering costs one pass over all documents per query; glob costs one pass over
  unique tokens. Both scale linearly.

Requerying on every ingestion publish (~50 publishes per 100k files) adds negligible
load at these latencies.

## Pitfalls checklist

- Missing facet values read as `0`; attach every facet to every document.
- Facet names are case-sensitive; pick one spelling (`size`, not `Size`) and stick to it.
- Filtering on a facet name that no document carries throws `ArgumentException` — build
  filters only from facets your scanner writes.
- Don't pass an empty filter to the filter overloads; call the filter-less overload to
  keep the exact-match fast path.
- Keep all timestamp facets in UTC; mixing kinds shifts range boundaries.
- `*.txt` has token semantics under **Default**, not end-anchored semantics — use
  **FileMask** or an `ext` facet; see [File extensions](#file-extensions).
- A literal `*` or `?` cannot be searched for; on the query side they are always
  wildcards.
- Do not pre-process query strings with `NameTokenizer.TokenizeName` — it strips
  `*` and `?`. Feed raw file names to the index and raw user input to `Find`.
