#!/usr/bin/env python3
"""Turns results/sustained/*.json into report.md and PNG plots.  usage: report_sustained.py [dir]"""
import json, glob, os, sys, collections
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

d = sys.argv[1] if len(sys.argv) > 1 else "results/sustained"
runs = {}
for f in sorted(glob.glob(f"{d}/*.json")):
    runs[os.path.basename(f)[:-5]] = json.load(open(f))

SLUGS = [("sharp", "SearchEngine.Sharp"), ("sharp-previous", "Sharp, previous layout (base + delta)"),
         ("lucene", "Lucene.NET 4.8"), ("tantivy", "TantivyDotNet 1.0.0-rc.2")]
COLOR = {"sharp": "#1b7f3b", "sharp-previous": "#b5651d", "lucene": "#2a5db0", "tantivy": "#a02c5a"}

def us(v):
    if v is None: return "–"
    if v < 10: return f"{v:.1f} µs"
    if v < 1000: return f"{v:.0f} µs"
    if v < 1e6: return f"{v/1000:.2f} ms" if v < 1e5 else f"{v/1000:.0f} ms"
    return f"{v/1e6:.2f} s"

def get(slug, scen): return runs.get(f"{slug}-{scen}")
def variants(slug, scen):
    first = runs.get(f"{slug}-{scen}")
    return ([first] if first else []) + [runs[k] for k in sorted(runs) if k.startswith(f"{slug}-{scen}-rep")]
out = []
w = out.append

def table(scen, title, note=""):
    rows = [(s, n, get(s, scen)) for s, n in SLUGS if get(s, scen)]
    if not rows: return
    w(f"### {title}\n")
    if note: w(note + "\n")
    w("| Engine | Mean | p50 | p95 | p99 | p99.9 | Max | Calls >10 ms / >100 ms / >1 s | Elapsed | Docs/s |")
    w("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
    for s, n, r in rows:
        l = r["latencyMicroseconds"]
        w(f"| {n} | {us(l['mean'])} | {us(l['p50'])} | {us(l['p95'])} | {us(l['p99'])} | {us(l['p999'])} | {us(l['max'])} | "
          f"{l['over10ms']} / {l['over100ms']} / {l['over1s']} | {r['wallSeconds']:.1f} s | {r['documentsPerSecond']:,.0f} |")
    w("")

def memory(scen, title):
    rows = [(s, n, get(s, scen)) for s, n in SLUGS if get(s, scen)]
    if not rows: return
    w(f"### {title}\n")
    w("| Engine | RSS before | Peak RSS (growth) | Managed allocations | GC pauses | gen0 / gen2 | Segments start → end → settled | Docs outside the largest segment at the end |")
    w("|---|---:|---:|---:|---:|---:|---:|---:|")
    for s, n, r in rows:
        m, lay = r["memory"], r["layout"]
        w(f"| {n} | {m['rssBeforeMiB']:.0f} MiB | {m['rssBeforeMiB'] + m['peakGrowthMiB']:.0f} MiB (+{m['peakGrowthMiB']:.0f}) | {m['managedAllocatedMiB']:,.0f} MiB | {m['gcPauseMilliseconds']:.0f} ms | "
          f"{m['gen0Collections']} / {m['gen2Collections']} | {lay['start']['seg0']} → {lay['end']['Segments']} → {lay['settled']['Segments']} | {lay['end']['DeltaDocuments']:,} |")
    w("")

def merges(scen, title):
    rows = [(s, n, get(s, scen)) for s, n in SLUGS if get(s, scen)]
    if not rows: return
    w(f"### {title}\n")
    w("| Engine | How measured | Merges | Documents written by merges | Write amplification | Merge time | Note |")
    w("|---|---|---:|---:|---:|---:|---|")
    for s, n, r in rows:
        m = r["merges"]
        wa = f"{m['writeAmplification']:.1f}×" if m.get("writeAmplification") else "–"
        w(f"| {n} | {m['Source']} | {m['Merges']:,} | {m['DocumentsWritten']:,} | {wa} | {m['MergeSeconds']:.2f} s | {m['Note']} |")
    w("")

def reader(scen, title):
    rows = [(s, n, get(s, scen)) for s, n in SLUGS if get(s, scen) and get(s, scen).get("concurrentReader")]
    if not rows: return
    w(f"### {title}\n")
    w("One reader thread cycles `final`, `report AND pdf` and `rep*` (counts) while the writer adds one document at a time.\n")
    w("| Engine | Queries | p50 | p95 | p99 | p99.9 | Max | Writer p50 | Writer p99 | Writer max |")
    w("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|")
    for s, n, r in rows:
        c, l = r["concurrentReader"], r["latencyMicroseconds"]
        w(f"| {n} | {c['queries']:,} | {us(c['p50'])} | {us(c['p95'])} | {us(c['p99'])} | {us(c['p999'])} | {us(c['max'])} | {us(l['p50'])} | {us(l['p99'])} | {us(l['max'])} |")
    w("")

w("# Sustained incremental indexing: SearchEngine.Sharp, Lucene.NET, TantivyDotNet\n")
any_run = next(iter(runs.values()))
w(f"Starting index: {any_run['n']:,} documents (the file-name corpus of the other three-way runs, seed 2026), built in memory. "
  "Then documents are added and every call is timed until the new documents are visible to a query. One engine per process; the single-document run was repeated three times, the other cells ran once.\n")
w(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "sustained-notes.md")).read())

w("## Verification\n")
w("After each run, the sorted ids of four result sets are read from the engines' own stored ids and hashed: `final` (term), "
  "`report AND pdf`, `rep*` (prefix) and `freshdoc`, a token that every added document carries, so that set is exactly the added ids. "
  "Equal hashes mean equal ids, not only equal counts. Every call also checks that the probe token of the newest document has exactly one hit, "
  "and every 97th call that this hit is that document.\n")
groups = collections.defaultdict(list)
for k, r in runs.items():
    if "durable" in k: continue
    groups[(r["ops"], r["batch"])].append((k, r))
w("| Scenario (docs added, batch) | " + " | ".join(n for _, n in SLUGS) + " | Result |")
w("|---|" + "---|" * (len(SLUGS) + 1))
bad = 0
def slug_of(k):
    for slug, _ in sorted(SLUGS, key=lambda t: -len(t[0])):
        if k.startswith(slug + "-"): return slug
for (ops, batch), items in sorted(groups.items()):
    hashes = collections.defaultdict(set)
    errs = collections.defaultdict(lambda: [0, 0, 0])
    for k, r in items:
        sl = slug_of(k)
        errs[sl][0] += r["errors"]["visibility"]; errs[sl][1] += r["errors"]["idMismatch"]; errs[sl][2] += 1
        for q, v in r["verification"].items(): hashes[q].add((v["count"], v["sha256"]))
    cells = [f"vis {errs[sl][0]}, id {errs[sl][1]} ({errs[sl][2]} run{'s' if errs[sl][2] > 1 else ''})" if sl in errs else "–" for sl, _ in SLUGS]
    ok = all(len(h) == 1 for h in hashes.values())
    if not ok: bad += 1
    w(f"| {ops:,}, batch {batch} | " + " | ".join(cells) + f" | {'ids identical in every run' if ok else '**MISMATCH**'} (final {next(iter(hashes['term_final']))[0]:,}, freshdoc {next(iter(hashes['freshdoc']))[0]:,}) |")
w("")
w("The `vis` and `id` columns count calls whose probe had not exactly one hit and calls whose hit was another document; both must be 0.\n")

w("## Single-document additions, 20,000 in a row\n")
table("single", "Without a reader (clean latency)", "Latency of one call that adds a document and returns when a query can see it.")
rep_rows = [(sl, n, v) for sl, n in SLUGS for v in [variants(sl, "single")] if len(v) > 1]
if rep_rows:
    w("### Repeat runs of the same scenario\n")
    w("The table above is the first run. Tails move from run to run (a garbage collection or a merge lands in one call or in another), so every run is listed.\n")
    w("| Engine | Run | p50 | p99 | p99.9 | Max | Elapsed |")
    w("|---|---:|---:|---:|---:|---:|---:|")
    for sl, n, vs in rep_rows:
        for i, r in enumerate(vs, 1):
            l = r["latencyMicroseconds"]
            w(f"| {n} | {i} | {us(l['p50'])} | {us(l['p99'])} | {us(l['p999'])} | {us(l['max'])} | {r['wallSeconds']:.1f} s |")
    w("")
memory("single", "Memory, GC and layout, same runs")
merges("single", "Merges, same runs")
reader("single-reader", "With a concurrent reader")
table("single-reader", "Writer latency while the reader runs")
memory("single-reader", "Memory, GC and layout, with the reader")

w("## Batches\n")
w("Latency of one call that adds a batch and returns when its newest document is visible; the last column is documents per second over the whole run.\n")
for b in (10, 100, 1000, 10000):
    table(f"batch{b}", f"Batches of {b:,}")
for b in (10, 100, 1000, 10000):
    memory(f"batch{b}", f"Memory and layout, batches of {b:,}") if b in (1000, 10000) else None

w("## Durability costs, separately\n")
w("Same single-document test on a disk index with a commit that reaches the disk after every document (Lucene `Commit`, Tantivy `Commit` on a directory). "
  "SearchEngine.Sharp has no persistent index. The earlier tables are in-memory indexes with no durability.\n")
table("durable", "Disk index, commit per document", "Compare with the in-memory rows above to read the cost of durability.")

w("## Latency against operation number\n")
w("![latency of every call](latency-by-operation.png)\n")
w("Each point is one call, first run. Spikes are merges, rebuilds or garbage collections that land in the call. The previous layout of SearchEngine.Sharp shows the saw: the delta grows, every call rebuilds it, and the index is rebuilt every ~2,000 calls (the dots at 0.1 to 2 s).\n")
w("![latency against delta size](latency-vs-delta.png)\n")
w("Latency of about 400 sampled calls against the number of documents outside the largest segment at that moment (the delta; colour is the operation number, dark early, light late). The previous layout's latency follows its delta; the others do not.\n")
w("![batch latency](batch-latency.png)\n")
open(f"{d}/report.md", "w").write("\n".join(out))

# ---------- plots
avail = [(s, n) for s, n in SLUGS if get(s, "single")]
fig, axes = plt.subplots(len(avail), 1, figsize=(11, 2.6 * len(avail)), sharex=True, sharey=True)
if len(avail) == 1: axes = [axes]
for ax, (s, n) in zip(axes, avail):
    r = get(s, "single"); y = r["latencySeries"]
    ax.scatter(range(len(y)), y, s=2, color=COLOR[s], alpha=.6, linewidths=0)
    ax.set_yscale("log"); ax.set_ylabel("µs"); ax.set_title(f"{n}: p50 {us(r['latencyMicroseconds']['p50'])}, max {us(r['latencyMicroseconds']['max'])}", fontsize=9, loc="left")
    ax.grid(alpha=.3, which="both", linewidth=.4)
axes[-1].set_xlabel("operation number (one added document per call, visible on return)")
fig.tight_layout(); fig.savefig(f"{d}/latency-by-operation.png", dpi=130); plt.close(fig)

fig, axes = plt.subplots(1, len(avail), figsize=(3.6 * len(avail), 3.4), sharey=True)
if len(avail) == 1: axes = [axes]
for ax, (s, n) in zip(axes, avail):
    smp = get(s, "single")["samples"]
    sc = ax.scatter(smp["deltaDocuments"], smp["latencyMicroseconds"], c=smp["op"], s=8, cmap="viridis")
    ax.set_xscale("symlog", linthresh=10); ax.set_yscale("log"); ax.set_title(n, fontsize=8)
    ax.set_xlabel("docs outside the largest segment")
    ax.grid(alpha=.3, linewidth=.4)
axes[0].set_ylabel("latency of the sampled call, µs")
fig.tight_layout(); fig.savefig(f"{d}/latency-vs-delta.png", dpi=130); plt.close(fig)

fig, ax = plt.subplots(figsize=(8, 4))
for s, n in SLUGS:
    xs, ys = [], []
    for b in (10, 100, 1000, 10000):
        r = get(s, f"batch{b}")
        if r: xs.append(b); ys.append(r["documentsPerSecond"])
    if xs: ax.plot(xs, ys, marker="o", label=n, color=COLOR[s])
ax.set_xscale("log"); ax.set_yscale("log"); ax.set_xlabel("documents per batch"); ax.set_ylabel("documents per second (batch added and visible)")
ax.legend(fontsize=8); ax.grid(alpha=.3, which="both", linewidth=.4)
fig.tight_layout(); fig.savefig(f"{d}/batch-latency.png", dpi=130); plt.close(fig)
print(f"wrote {d}/report.md; verification mismatches: {bad}")
