# Query semantics reference

Precise behavior of tokenization, term matching, boolean expressions, glob patterns, and
facet filters. The test suite (`tests/SearchEngine.Sharp.Tests`) encodes these rules;
where this document and the tests disagree, the tests win.

## Pipeline overview

**Indexing:** `SearchText` → split on the snapshot's **index-side** separators → lowercase
→ unique tokens per document → inverted index (token → posting list of documents).

**Querying:** expression → split on the snapshot's **query-side** separators → each term
resolves to a document set → sets combined with boolean algebra → optional facet filter
intersected → results materialized in the requested sort order.

Both separator sets are stored in each `IndexSnapshot` as a `SearchTokenization` preset
so queries always match the index that built them.

## Tokenization presets

`SearchTokenization` (namespace `SearchEngine`) carries the index-side and query-side
separator character sets. Pass it to `IndexUpdater` and `IndexSnapshotBuilder`; queries
read it from `snapshot.Tokenization`.

| Preset | Index separators | Query separators | Use case |
|---|---|---|---|
| `SearchTokenization.Default` | ` .,;:/\` + CR LF TAB + `\|-#[]{}()~£$€` | same as index except **no TAB** | token-level search (pre-0.5.3 behavior) |
| `SearchTokenization.FileMask` | **none** (whole `SearchText` = one token) | whitespace only: space, CR, LF | classic file-manager masks on whole names |
| `SearchTokenization.Create(...)` | caller-defined | caller-defined | custom rules |

Factory:

```csharp
var custom = SearchTokenization.Create(indexSeparators: "-", querySeparators: " ");
```

### FileMask semantics

With `SearchTokenization.FileMask`:

- A bare term such as `system` matches the **whole file name** (case-insensitive exact
  token equality), not a substring inside a longer token. `my-system-backup.zip` does
  **not** match `system`.
- `*.pdf`, `system*`, and `*system*` are anchored globs on the whole name; `.` is **not**
  a separator, so `*.pdf` means "name ends with `.pdf`".
- `WordMatchMethod.Within` still matches substrings of the whole name (e.g. `port`
  matches `report-final.pdf`).
- Boolean operators (`AND` / `OR` / `NOT`) still work; only **space** splits query terms
  (implicit AND between adjacent terms still applies).

Worked examples, given an index of names `report-final.pdf`, `System`,
`my-system-backup.zip`, `notes.txt` (queries with `WordMatchMethod.Exact`,
`enableOperators: true`; encoded in `FileMaskTokenizationTests`):

| Expression | Matches | Why |
|---|---|---|
| `system` | `System` | whole-name equality, case-insensitive |
| `system*` | `System` | prefix glob on the whole name |
| `*system*` | `System`, `my-system-backup.zip` | contains glob |
| `*.pdf` | `report-final.pdf` | end-anchored: `.` is not a separator |
| `*.pdf OR *.txt` | `report-final.pdf`, `notes.txt` | boolean OR over whole-name globs |
| `* AND NOT *.zip` | all except `my-system-backup.zip` | `*` = all, minus suffix glob |
| `port` (`Within`) | `report-final.pdf` | substring of the whole name |

FileMask behavioral notes (by design — not bugs):

1. **Parentheses are not query separators** in FileMask, so parenthesis grouping is
   unavailable. Unbalanced-paren degradation in the evaluator does not apply because `(`
   and `)` are ordinary name characters, not separators — a literal `(` or `)` in a name
   **can** be matched (`report(1).pdf` matches the mask `report(1).pdf`).
2. A **space in a file name** cannot be typed literally in a mask expression — use `?`
   or `*` wildcards instead.
3. `*.*` means "name contains a dot", not "match everything". Applications wanting DOS
   `*.*` behavior should rewrite `*.*` → `*` in the UI layer.
4. With `enableOperators: true`, a file named exactly `and`, `or`, or `not` cannot be
   searched by its bare name (the term parses as an operator). Query it with operators
   disabled, or with a wildcard that keeps the term out of operator form (`and*` also
   matches longer names, so prefer disabling operators). This collision exists in every
   preset; it is simply more visible when whole names are single terms.

Switching presets requires a full `RebuildFrom` from the application's file table; the
preset is fixed per snapshot.

## Index-side tokenization (Default preset)

Separator characters (`SearchTokenization.Default.IndexSeparators`):

```
space . , ; : / \ CR LF TAB | - # [ ] { } ( ) ~ £ $ €
```

Every maximal run of non-separator characters is one token. All other characters —
including `_ ' " * ? & @ % + = !` — are token characters.

- Tokens are lowercased (`char.ToLowerInvariant`; ASCII fast path).
- Duplicate tokens within one document are indexed once.
- Example: `"Report-2024_final.PDF"` → tokens `report`, `2024_final`, `pdf`
  (`-` and `.` are separators; `_` is not).

`NameTokenizer.TokenizeName` is an optional pre-processing helper with a stricter rule:
it keeps only letter/digit runs (so `_` also splits) and joins the result with `-`.
If you pre-process indexed text with it, do **not** run query strings through it — it
strips `*` and `?`. See the [file search guide](file-search-guide.md) for guidance.

## Query parsing (Default preset)

Separator characters (`SearchTokenization.Default.QuerySeparators`):

```
space . , ; : / \ CR LF | - # [ ] { } ( ) ~ £ $ €
```

(Same set as indexing except TAB, which is a separator only on the index side.)

Rules:

1. The expression splits into terms on separators. Terms are lowercased.
2. **Adjacent terms are combined with an implicit AND**, regardless of
   `enableOperators`: `report pdf` means `report AND pdf`.
3. With `enableOperators: true`:
   - The words `and`, `or`, `not` (ASCII case-insensitive) are operators, not search terms.
   - `not term` matches all documents in the snapshot except those matching `term`.
   - Precedence: `NOT` binds tighter than `AND`, which binds tighter than `OR`.
     Same-precedence operators evaluate left to right.
   - Parentheses group subexpressions, but only when balanced across the whole
     expression. If unbalanced, every parenthesis degrades to a plain separator.
4. With `enableOperators: false` (the default), `and` / `or` / `not` are ordinary
   search terms and parentheses are plain separators.
5. Malformed expressions are corrected, not rejected:
   - Leading `AND` / `OR` and all trailing operators are dropped.
   - `AND AND` collapses to a single `AND`; `NOT NOT` cancels out;
     any other adjacent binary-operator pair collapses to `AND`.
6. An expression that is empty, whitespace, or reduces to nothing after correction
   returns no results — unless a non-empty facet filter is supplied
   (see [filter-only queries](#filter-only-queries)).

## Term matching

Each term resolves to a set of documents. The resolution method depends on the term's
content and on the `WordMatchMethod` argument:

| Term shape | Resolution |
|---|---|
| contains `*` or `?` | Glob against whole tokens (`WordMatchMethod` is ignored for that term) |
| plain, `WordMatchMethod.Exact` | Whole-token equality |
| plain, `WordMatchMethod.Within` | Substring occurring anywhere inside an indexed token |

Matching is case-insensitive in all modes. Methods apply per term, so in a single
expression a plain term uses the requested method while a wildcard term uses glob.

## Regular expressions (`WordMatchMethod.Regex`)

The **entire** `expression` is one .NET regular expression. The boolean tokenizer is
**not** used — separators, `AND` / `OR` / `NOT`, and parentheses in the pattern are
regex syntax, not query operators. `enableOperators` is ignored.

Matching scans every unique indexed token (same loop as glob), using
`RegexOptions.IgnoreCase | NonBacktracking` with implicit anchoring on the whole token
(the engine compiles `^(?:pattern)$`). Invalid patterns or constructs unsupported by
`NonBacktracking` yield an empty result set (no exception).

**Token-level semantics (Default preset):** patterns run against normalized lowercase
tokens, not the original `SearchText`. A pattern that spans index separators (e.g.
`report.*\.pdf`) cannot match because no single token contains both `report` and
`pdf`. Use **`SearchTokenization.FileMask`** when the indexed token is the full file
name, or combine regex hits with boolean/glob terms on Default.

Examples on Default index of `report-final.pdf` (tokens `report`, `final`, `pdf`):

| Expression | Matches tokens | Documents |
|---|---|---|
| `report.*` | `report` only | docs with token `report` |
| `reporting` | `reporting` if present | — |
| `pdf` | `pdf` | docs with token `pdf` |
| `final\|pdf` | `final`, `pdf` | union of posting lists |

On **FileMask** index, token `report-final.pdf` can match `report-final\.pdf` or
`.*\.pdf$` (suffix patterns need a leading `.*` because matching is anchored on the
whole token).

## Glob patterns

A term containing `*` or `?` is matched as a glob against whole indexed tokens
(anchored at both ends):

- `*` — zero or more characters; consecutive `*` collapse into one
- `?` — exactly one character
- any other character — literal (after lowercasing)

Examples against an index containing tokens `report`, `reporting`, `log`:

| Pattern | Matches |
|---|---|
| `report*` | `report`, `reporting` |
| `*port*` | `report`, `reporting` |
| `l?g` | `log` |
| `?*?` | any token of length ≥ 2 |
| `*` | every document |
| `?` | any single-character token |

Limits and edge cases:

- No character classes or escapes. `[` and `]` are query separators, so `[abc]`
  never reaches the matcher as one term.
- There is no way to match a literal `*` or `?`: on the query side they are always
  wildcards. Indexed tokens *can* contain them (they are not index separators) but are
  then only reachable through wildcard patterns or `Within` substrings.
- A pattern containing separators splits into terms first: `ga-1*` becomes
  `ga AND 1*`, and `*.txt` becomes `* AND txt` — that is, "has token `txt`", not
  "name ends with `.txt`". For anchored extension filtering use a facet
  (see the [file search guide](file-search-guide.md#file-extensions)).
- Cost: one scan over *unique tokens* (not documents) with an early exit on token
  length; posting lists of all matching tokens are unioned. Measurements:
  [glob-and-facets-report.md](glob-and-facets-report.md).

## Facet filters

A document may carry named numeric values (facets), supplied as the optional third
parameter of `IndexedEntry` and stored in the snapshot as columns parallel to the
index. A `FacetFilter` restricts results to documents whose facet values satisfy
**all** of its predicates. The filter applies after the text expression is fully
evaluated, including `NOT`.

```csharp
var entry = new IndexedEntry(
    "report-final.pdf",
    "report-final.pdf",
    FacetValues.FromDictionary(new Dictionary<string, long>
    {
        ["size"] = 48_128,
        ["modified"] = new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc).Ticks,
        ["attrs"] = 0x20, // FileAttributes.Archive
    }));

var filter = FacetFilter.Combine(
    FacetFilter.Range("size", 1024, 1_048_576),
    FacetFilter.Mask("attrs", mustHave: 0x20, mustNot: 0x2));

var ids = engine.Find("report*", WordMatchMethod.Within, enableOperators: true,
    SearchSortMode.NaturalSortAscending, filter);
```

Predicates:

| Predicate | Semantics |
|---|---|
| `FacetFilter.Range(facet, min, max)` | `min <= value && value <= max` (both ends inclusive) |
| `FacetFilter.Mask(facet, mustHave, mustNot)` | `(value & mustHave) == mustHave && (value & mustNot) == 0` |
| `FacetFilter.Combine(filters...)` | AND of all contained predicates |

- Equality is a degenerate range: `Range(f, x, x)`.
- There is no OR between facet predicates. If you need one, run two queries and
  union the results on the caller side.
- Facet names are case-sensitive (ordinal comparison).
- A document without a value for a facet has `0` in that column. A range that
  includes `0`, or a mask with `mustHave: 0`, therefore matches such documents.
  If that is not intended, attach the facet to every document.
- A filter naming a facet that no indexed document carries throws `ArgumentException`.
- `FacetFilter.None` (or a `Combine` of nothing) imposes no constraints.

Recommended encodings:

| Data | Encoding |
|---|---|
| Sizes | bytes |
| Timestamps | `DateTime.Ticks`, consistently UTC |
| Flags / attributes | bitmask packed into the `long` |
| Enumerations (e.g. file extension) | caller-assigned integer id |

### Filter-only queries

An empty or whitespace expression combined with a non-empty filter matches all
documents that pass the filter. An empty expression with no filter (or an empty one)
returns nothing.

### Performance note

Filter evaluation is a single pass over all documents followed by one bitset
intersection. The `Find` / `CountMatches` overloads that take a filter always run the
full evaluation pipeline — when you have no facet constraints, call the overloads
*without* the filter parameter to keep the exact single-term fast path.

## Result ordering

| `SearchSortMode` | Order |
|---|---|
| `SnapshotOrder` | Document order within the current snapshot |
| `NaturalSortAscending` | Natural sort of `SortText`: digit runs compare numerically (`file2` < `file10`), text case-insensitively; the permutation is computed once per snapshot and cached |

## Worked examples

Index (id → `SearchText`):

```
1 → "report-final.pdf"    tokens: report, final, pdf
2 → "report-draft.pdf"    tokens: report, draft, pdf
3 → "notes.txt"           tokens: notes, txt
4 → "archive-report.log"  tokens: archive, report, log
```

Queries with `WordMatchMethod.Within`, `enableOperators: true`:

| Expression | Result ids | Why |
|---|---|---|
| `report` | 1, 2, 4 | substring inside a token |
| `report pdf` | 1, 2 | implicit AND |
| `report AND NOT draft` | 1, 4 | boolean operators |
| `rep*` | 1, 2, 4 | glob, prefix-anchored on whole tokens |
| `*.pdf` | 1, 2 | splits into `*` AND `pdf` → "has token pdf" |
| `fin?l` | 1 | `?` = exactly one character |
| `(draft OR final) pdf` | 1, 2 | grouping plus implicit AND |
| `""` + `Range("size", 10_240, long.MaxValue)` | size-dependent | filter-only query |
