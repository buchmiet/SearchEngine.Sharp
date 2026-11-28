# Contributing

Build and test commands are in the [README](README.md#building-and-testing). This page sets the rules for comments
and documentation, which apply to people and coding agents alike.

## Comments

### Two forms, two readers

- `///` XML documents the public contract for the library user: behavior, defaults, exceptions, thread safety, and
  any cost the caller can act on. It never describes fields, internal types or how the work is done.
- `//` is for the maintainer and explains why: invariants, preconditions, the reason for a non-obvious choice.
  Internal and private types and members use `//` only.
- Use `<inheritdoc/>` for interface implementations and for overloads that add nothing; do not copy text between
  overloads. Property summaries start with "Gets". Elements go in this order: summary, typeparam, param, returns,
  exception, remarks, example. Refer to an overloaded member with a signature cref, not `<c>Name</c>`.

### Type-level blocks

An internal type that needs explaining gets one `//` block directly above it. When the block covers two or more
topics, each starts with a short marker, chosen from Data layout, Thread safety, Atomicity, Cost and Invariants:

```csharp
// The words, postings and facet columns of one part of a snapshot.
//
// ── Data layout ──
// ...
//
// ── Thread safety ──
// ...
```

Member-group markers (`// ── Batch operations ──`) belong in interfaces only, not in the classes that implement them.

### One home per rule

A rule is explained once, next to the code that would have to change with it. Everywhere else, point to it:
`(IndexSegment, Data layout)` or `(IndexSnapshot.SegmentOf)` in `//`, `<see cref>` in XML. For public behavior the
user-facing explanation lives in `docs/`, and the XML states the rule in one sentence. Before writing a comment that
explains a rule, search for the place that already explains it.

### Measurements

A number that was measured names the benchmark that measures it:
`(FastBitSetBenchmark.GetTrueCount, 10k to 1M bits: 10-20% slower without unrolling)`. Without a benchmark, keep the
reason and drop the number. A derived number names the constants it comes from (341 = 1024 / 3).

### Not in comments

- History: "as before", "now", "no longer", traces of a removed variant. Git and the CHANGELOG keep history.
- Issue numbers, and test names in `src/`. A member that exists for tests says "(read by tests)". A test comment
  may name the test class that covers a neighbouring rule.
- A restatement of the next line or of what the API name says.
- Lists of callers; they go stale when a caller is added.
- Promotion and hedges: "zero-allocation", "recommended" without a reason, "simply", "in practice".
- British spelling (write "serialized"), and lines over 120 columns.

## Terms

| Concept | Public text (XML, docs) | Internal comments |
|---|---|---|
| An indexed item | document | document |
| The caller's id | id | record id |
| Position of a document in a snapshot | | ordinal (dead ones included); `ordinal - FirstOrdinal` inside a segment |
| Position of a token in a segment | | token index |
| An indexed piece of text | token | token (also where identifiers say `Word`, such as `WordAt`) |
| A piece of a query | term | term |
| Building the whole index | full build (`RebuildFrom` or a merge) | merge for the threshold event; rebuild for `RebuildFrom` only |
| Making a snapshot current | publish | publish |
| The structure behind `NaturalSortAscending` | sort permutation | sort permutation |
| Natural sort strategies | | sort the hits; walk the permutation |
| Hits as a share of the documents | | hit fraction |
| Ingestion roles | scan | scan (the caller's enumerable), scanner, publisher, batch |

## Documentation

`docs/` is written for a person who uses the library. Each fact has one page that owns it, and the others link
there. Write plain sentences: no bold for emphasis, no em or en dashes, no closing summary that repeats the section.
A claim about behavior is checked against the code, and a number against the benchmark that produced it.
