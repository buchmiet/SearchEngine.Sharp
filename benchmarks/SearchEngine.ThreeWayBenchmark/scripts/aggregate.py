#!/usr/bin/env python3
"""Merges results/*.json (median across repeat runs) into a Markdown report."""
import json, glob, sys, statistics as st, collections, os
d = sys.argv[1] if len(sys.argv) > 1 else "results"
runs = collections.defaultdict(list)  # (engine,n) -> list of row lists
for f in sorted(glob.glob(f"{d}/*.json")):
    rows = json.load(open(f)); runs[(rows[0]["engine"], rows[0]["n"])].append(rows)
ENG = ["SearchEngine.Sharp", "Lucene.NET 4.8", "TantivyDotNet 1.0.0-rc.2"]
SHORT = {ENG[0]: "Sharp", ENG[1]: "Lucene", ENG[2]: "Tantivy"}
def med(vals):
    vals = [v for v in vals if v is not None]
    return st.median(vals) if vals else None
def agg(engine, n, kind, id_, phase, key):
    vals = []
    for rows in runs.get((engine, n), []):
        for r in rows:
            if r["kind"] == kind and r["id"] == id_ and r.get("phase") == phase and r.get("supported", True): vals.append(r.get(key))
    return med(vals)
def fmt_us(v):
    if v is None: return "–"
    if v < 10: return f"{v:.2f} µs"
    if v < 1000: return f"{v:.0f} µs" if v >= 100 else f"{v:.1f} µs"
    if v < 1e6: return f"{v/1000:.2f} ms" if v < 100000 else f"{v/1000:.0f} ms"
    return f"{v/1e6:.2f} s"
ids = []
for rows in next(iter(runs.values())):
    for r in rows:
        if r["kind"] == "query" and r["id"] not in [i for i, _ in ids]: ids.append((r["id"], r["desc"]))
out = []
ns = sorted({n for _, n in runs})
reps = {k: len(v) for k, v in runs.items()}
out.append(f"Repeats per (engine, size): {dict((f'{SHORT[e]}@{n}', c) for (e, n), c in sorted(reps.items()))}. Every cell is the median over repeat runs of the per-run median.\n")
# hit-count agreement
out.append("## Hit counts (must agree)\n")
out.append("| Query | " + " | ".join(f"{SHORT[e]} @{n:,}" for n in ns for e in ENG) + " |")
out.append("|---|" + "---:|" * (len(ns) * 3))
for id_, desc in ids:
    cells = []
    for n in ns:
        for e in ENG:
            h = agg(e, n, "query", id_, "merged", "hits")
            cells.append("n/a" if h is None else f"{int(h):,}")
    out.append(f"| {desc} | " + " | ".join(cells) + " |")
for n in ns:
    out.append(f"\n## Query latency, {n:,} names (merged to one segment)\n")
    for key, title in [("count_us", "Count only (no ids)"), ("top10_us", "Top 10 hits with ids"), ("all_us", "All matching ids")]:
        out.append(f"### {title}\n")
        out.append("| Query | hits | Sharp | Lucene | Tantivy | Sharp vs Lucene | Sharp vs Tantivy |")
        out.append("|---|---:|---:|---:|---:|---:|---:|")
        for id_, desc in ids:
            v = {SHORT[e]: agg(e, n, "query", id_, "merged", key) for e in ENG}
            hits = agg(ENG[1], n, "query", id_, "merged", "hits")
            def ratio(o):
                if v["Sharp"] is None or v[o] is None: return "–"
                r = v[o] / v["Sharp"]
                return f"{r:.1f}× faster" if r >= 1.05 else (f"{1/r:.1f}× slower" if r <= 0.95 else "≈ equal")
            out.append(f"| {desc} | {int(hits):,} | {fmt_us(v['Sharp'])} | {fmt_us(v['Lucene'])} | {fmt_us(v['Tantivy'])} | {ratio('Lucene')} | {ratio('Tantivy')} |")
        out.append("")
    alt = [(id_, desc) for id_, desc in ids if agg(ENG[2], n, "query", id_, "merged", "all_stored_us") is not None]
    out.append("### Tantivy, all ids read from stored fields instead of the fast column\n")
    out.append("| Query | stored-field path |"); out.append("|---|---:|")
    for id_, desc in alt: out.append(f"| {desc} | {fmt_us(agg(ENG[2], n, 'query', id_, 'merged', 'all_stored_us'))} |")
    out.append("")
out.append("## Build, update and footprint\n")
out.append("| Metric | " + " | ".join(f"{SHORT[e]} @{n:,}" for n in ns for e in ENG) + " |")
out.append("|---|" + "---:|" * (len(ns) * 3))
def b(e, n, kind, id_, key):
    vals = [r.get(key) for rows in runs.get((e, n), []) for r in rows if r["kind"] == kind and r["id"] == id_]
    return med(vals)
def cell(f): 
    return f
metrics = [
    ("Index build, 1 thread, in memory (s)", "build", "build_inmem", "sec_median", lambda v: f"{v:.2f}"),
    ("Merge to one segment (s)", "build", "force_merge", "sec", lambda v: f"{v:.2f}"),
    ("Process RSS growth after build (MB)", "build", "build_inmem", "rss_delta_mb", lambda v: f"{v:.0f}"),
    ("Managed heap growth after build (MB)", "build", "build_inmem", "managed_heap_delta_mb", lambda v: f"{v:.0f}"),
    ("Add 1 doc → visible to a query, median", "update", "add_one_visible", "median_us", fmt_us),
    ("Add 1 doc → visible to a query, p95", "update", "add_one_visible", "p95_us", fmt_us),
    ("Build to disk (s)", "disk", "disk_build", "build_sec", lambda v: f"{v:.2f}"),
    ("Index on disk (MiB)", "disk", "disk_build", "bytes", lambda v: f"{v/1048576:.1f}"),
    ("Open from disk + first query (ms)", "disk", "disk_build", "cold_open_first_query_sec", lambda v: f"{v*1000:.1f}"),
]
for name, kind, id_, key, f in metrics:
    cells = []
    for n in ns:
        for e in ENG:
            v = b(e, n, kind, id_, key); cells.append("–" if v is None else f(v))
    out.append(f"| {name} | " + " | ".join(cells) + " |")
print("\n".join(out))
