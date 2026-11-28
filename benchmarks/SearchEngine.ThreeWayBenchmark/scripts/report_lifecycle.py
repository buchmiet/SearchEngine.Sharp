#!/usr/bin/env python3
"""Turns results/lifecycle/*.json into report.md and PNG plots.  usage: report_lifecycle.py [dir]

Files are named <engine>-<config>.json (engine: sharp | lucene | tantivy), for example sharp-main.json, lucene-small.json.
One section with its own plots per config.
"""
import json, glob, os, sys, collections, re
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

d = sys.argv[1] if len(sys.argv) > 1 else "results/lifecycle"
SLUGS = [("sharp", "SearchEngine.Sharp"), ("lucene", "Lucene.NET 4.8"), ("tantivy", "TantivyDotNet 1.0.0-rc.2")]
COLOR = {"sharp": "#1b7f3b", "lucene": "#2a5db0", "tantivy": "#a02c5a"}
KIND_COLOR = {"add": "#2a9d8f", "update": "#e9a100", "delete": "#c4452d"}
KINDS = ["add", "update", "delete"]

runs = {}  # (config, slug) -> result
for f in sorted(glob.glob(f"{d}/*.json")):
    base = os.path.basename(f)[:-5]
    m = re.match(r"(sharp|lucene|tantivy)-(.+)$", base)
    if m:
        runs[(m.group(2), m.group(1))] = json.load(open(f))
configs = sorted({c for c, _ in runs})


def us(v):
    if v is None or v != v: return "–"
    if v < 10: return f"{v:.1f} µs"
    if v < 1000: return f"{v:.0f} µs"
    if v < 1e6: return f"{v/1000:.2f} ms" if v < 1e5 else f"{v/1000:.0f} ms"
    return f"{v/1e6:.2f} s"


def rows_of(config):
    return [(s, n, runs[(config, s)]) for s, n in SLUGS if (config, s) in runs]


out = []
w = out.append
bad_total = 0

w("# Index lifecycle: additions, updates and deletions in SearchEngine.Sharp, Lucene.NET, TantivyDotNet\n")
w("Every run starts from an index of n documents (ids 0..n-1, the file-name corpus of the other three-way runs, seed 2026) and applies one long "
  "stream of single-document operations. Each call returns only when the change is visible to a query; every call is timed. "
  "One engine per process; the three engines receive the same stream of operations (same seed), so the ids touched are identical.\n")
w(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "lifecycle-notes.md")).read())

for config in configs:
    rows = rows_of(config)
    r0 = rows[0][2]
    mix = r0["mix"]
    w(f"## Configuration `{config}`: n = {r0['n']:,}, mix `{mix['text']}`, {mix['oldShare']:.0%} of updates and deletions on original documents\n")
    counts = ", ".join(f"{n}: {r['ops']:,} operations" for _, n, r in rows)
    w(f"Operations executed ({counts}). Updates and deletions target an original document that no operation has touched yet "
      f"(the large first segment) with probability {mix['oldShare']:.2f}, otherwise a live document among the last {mix['recentWindow']:,} writes. "
      + (f"Paced at {r0['rate']} calls per second with one reader thread. " if r0.get("rate") else "Writer as fast as it goes. ")
      + "Where engines ran different numbers of operations the shorter run is a prefix of the longer one (same seed).\n")

    # ---- latency by operation type
    w("### Latency by operation type\n")
    w("| Engine | Operation | Count | Mean | p50 | p95 | p99 | p99.9 | Max | Total time |")
    w("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|")
    for s, n, r in rows:
        for k in KINDS:
            st = r["latencyByOperation"][k]
            if st["count"] == 0: continue
            w(f"| {n} | {k} | {st['count']:,} | {us(st['mean'])} | {us(st['p50'])} | {us(st['p95'])} | {us(st['p99'])} | {us(st['p999'])} | {us(st['max'])} | {st['total']/1e6:.2f} s |")
        l = r["latencyMicroseconds"]
        w(f"| **{n}** | **all** | {r['ops']:,} | {us(l['mean'])} | {us(l['p50'])} | {us(l['p95'])} | {us(l['p99'])} | {us(l['p999'])} | {us(l['max'])} | {r['wallSeconds']:.1f} s elapsed |")
    w("")
    w("### Throughput and slow calls\n")
    w("| Engine | Operations | Elapsed | Operations/s | Calls >10 ms / >100 ms / >1 s | Slowest 1% of calls, share of all time |")
    w("|---|---:|---:|---:|---:|---:|")
    for s, n, r in rows:
        l = r["latencyMicroseconds"]
        w(f"| {n} | {r['ops']:,} | {r['wallSeconds']:.1f} s | {r['callsPerSecond']:,.0f} | {l['over10ms']} / {l['over100ms']} / {l['over1s']} | {l['slowestCallShareOfTotalPercent']:.0f}% |")
    w("")
    w(f"![latency of every call, coloured by operation](lifecycle-{config}-latency.png)\n")
    w("Every call, by operation number, on a log scale: green additions, amber updates, red deletions. Vertical lines mark the moments when a different segment became the largest one (the original segment merged or rewritten).\n")

    # ---- layout
    w("### Layout, dead documents and the largest segment\n")
    w("Document counts are physical (dead documents included). *Dead* are documents deleted or replaced but still stored until a merge drops them; *largest* is the biggest segment.\n")
    w("| Engine | Segments start → end → settled | Dead documents at the end | Dead in the largest segment (end) | Peak dead in the largest segment | Largest segment: start → end | Times the largest segment was replaced |")
    w("|---|---:|---:|---:|---:|---:|---:|")
    for s, n, r in rows:
        lay, smp = r["layout"], r["samples"]
        peak = max((d_ / l_ for d_, l_ in zip(smp["largestDead"], smp["largestDocuments"]) if l_), default=0)
        w(f"| {n} | {lay['start']['seg0']} → {lay['end']['Segments']} → {lay['settled']['Segments']} | {lay['end']['DeadDocuments']:,} | "
          f"{lay['end']['LargestDead']:,} ({lay['end']['LargestDead'] / max(1, lay['end']['LargestDocuments']):.1%}) | {peak:.1%} | "
          f"{lay['start']['LargestDocuments']:,} → {lay['end']['LargestDocuments']:,} | {len(r['largestSegmentEvents'])} |")
    w("")
    w(f"![segments and dead documents over the run](lifecycle-{config}-layout.png)\n")
    w("Top of each panel: dead documents in the whole index (line) and in the largest segment (dashed), and the segment count (grey, right axis). "
      "Bottom: the share of dead documents in the largest segment, with the 30% line at which SearchEngine.Sharp rewrites a segment (`MergePolicy.DeadRatio`). "
      "Red vertical lines: the largest segment was replaced.\n")
    ev_rows = [(n, e) for s, n, r in rows for e in r["largestSegmentEvents"]]
    if ev_rows:
        w("#### When the largest segment was replaced\n")
        w("The call during which the change was first observed (the layout is read after every call). For Sharp and Tantivy merges run in the background, so the call in which a replacement shows up is usually not slow; the \"around\" column is the slowest call among the three before and after it. "
          "Lucene merges are also in the background; its merge durations are in the merges table.\n")
        w("| Engine | Operation # | Seconds into the run | Largest segment before → after (documents) | Dead in it before → after | Segments before → after | That call | Slowest call within ±3 |")
        w("|---|---:|---:|---:|---:|---:|---:|---:|")
        for n, e in ev_rows[:60]:
            w(f"| {n} | {e['op']:,} | {e['seconds']:.1f} | {e['largestDocumentsBefore']:,} → {e['largestDocumentsAfter']:,} | {e['largestDeadBefore']:,} → {e['largestDeadAfter']:,} | "
              f"{e['segmentsBefore']} → {e['segmentsAfter']} | {us(e['latencyMicroseconds'])} | {us(e['maxLatencyAroundMicroseconds'])} |")
        if len(ev_rows) > 60: w(f"\n(first 60 of {len(ev_rows)} shown)")
        w("")

    # ---- merges
    w("### Merges\n")
    w("| Engine | How measured | Merges | Per 1,000 operations | Documents written by merges | Write amplification (per operation) | Merge time, total | Mean per merge | One merge: p50 / p95 / max | Note |")
    w("|---|---|---:|---:|---:|---:|---:|---:|---:|---|")
    for s, n, r in rows:
        m = r["merges"]
        wa = f"{m['writeAmplification']:.1f}×" if m.get("writeAmplification") else "–"
        per = f"{m['Merges'] * 1000 / r['ops']:.0f}" if m["Merges"] else "–"
        mean = us(m["MergeSeconds"] * 1e6 / m["Merges"]) if m["Merges"] and m["MergeSeconds"] else "–"
        dd = r.get("mergeDurationsMilliseconds")
        one = f"{us(dd['p50'] * 1000)} / {us(dd['p95'] * 1000)} / {us(dd['max'] * 1000)}" if dd else "not exposed"
        total = f"{m['MergeSeconds']:.2f} s" if m["MergeSeconds"] else "–"
        w(f"| {n} | {m['Source']} | {m['Merges']:,} | {per} | {m['DocumentsWritten']:,} | {wa} | {total} | {mean} | {one} | {m['Note']} |")
    w("")
    w(f"![merges over the run](lifecycle-{config}-merges.png)\n")
    w("Cumulative merges by operation number (Sharp and Lucene exact, Tantivy only as falls of the segment count, a lower bound).\n")

    # ---- memory
    w("### Memory and GC\n")
    w("| Engine | RSS before | Peak RSS (growth) | Managed allocations | GC pauses | gen0 / gen2 | Allocations per operation |")
    w("|---|---:|---:|---:|---:|---:|---:|")
    for s, n, r in rows:
        mm = r["memory"]
        w(f"| {n} | {mm['rssBeforeMiB']:.0f} MiB | {mm['rssBeforeMiB'] + mm['peakGrowthMiB']:.0f} MiB (+{mm['peakGrowthMiB']:.0f}) | {mm['managedAllocatedMiB']:,.0f} MiB | "
          f"{mm['gcPauseMilliseconds']:.0f} ms | {mm['gen0Collections']} / {mm['gen2Collections']} | {mm['managedAllocatedMiB'] * 1024 / r['ops']:.1f} KiB |")
    w("")

    # ---- reader
    rr = [(s, n, r) for s, n, r in rows if r.get("concurrentReader")]
    if rr:
        w("### Search during the lifecycle (paced reader)\n")
        w("One reader thread cycles `final`, `report AND pdf` and `rep*` (counts), 1 ms apart. Each query is tagged when it starts: a background merge running (where observable) and, after the run, whether it overlapped a write call over 10 ms.\n")
        w("| Engine | Writes/s reached | Queries | All: p50 / p99 / max | During a background merge: n, p50 / p99 / max | No merge running: p50 / p99 | Overlapping a write over 10 ms: n, p50 / max | Writer p50 / p99 / max |")
        w("|---|---:|---:|---:|---:|---:|---:|---:|")
        for s, n, r in rr:
            c, l = r["concurrentReader"], r["latencyMicroseconds"]
            dm, nm, ow = c.get("duringBackgroundMerge"), c.get("withoutBackgroundMerge"), c.get("overlappingSlowWrite")
            during = "not observable" if c.get("mergeNotObservable") else (f"{dm['queries']:,}, {us(dm['p50'])} / {us(dm['p99'])} / {us(dm['max'])}" if dm else "0")
            without = "–" if c.get("mergeNotObservable") or not nm else f"{us(nm['p50'])} / {us(nm['p99'])}"
            over = f"{ow['queries']:,}, {us(ow['p50'])} / {us(ow['max'])}" if ow else "0"
            w(f"| {n} | {r['callsPerSecond']:.0f} | {c['queries']:,} | {us(c['p50'])} / {us(c['p99'])} / {us(c['max'])} | {during} | {without} | {over} | {us(l['p50'])} / {us(l['p99'])} / {us(l['max'])} |")
        w("")
        w(f"![reader latency over time](lifecycle-{config}-reader.png)\n")
        w("Every reader query over time; red points ran while a background merge was running, black crosses overlapped a write call over 10 ms, vertical lines mark the replacement of the largest segment.\n")

    # ---- verification
    w("### Verification against the model\n")
    w("| Engine | Visibility errors | Id errors | Mismatches vs the model | Live documents (model / engine) | Latest-version tokens | Superseded tokens | Deleted tokens | Periodic checks |")
    w("|---|---:|---:|---:|---:|---:|---:|---:|---:|")
    for s, n, r in rows:
        v, e = r["validation"], r["errors"]
        bad_total += e["visibility"] + e["idMismatch"] + v["mismatches"]
        per = v["periodic"]
        w(f"| {n} | {e['visibility']} | {e['idMismatch']} | **{v['mismatches']}** | {v['expectedLive']:,} / {v['actualLive']:,} | "
          f"{v['latestVersionTokens']['checkedCount']} checked, {v['latestVersionTokens']['bad']} bad | {v['supersededTokens']['checkedCount']} checked, {v['supersededTokens']['bad']} bad | "
          f"{v['deletedTokens']['checkedCount']} checked, {v['deletedTokens']['bad']} bad | {len(per)} ({sum(p['mismatches'] for p in per)} mismatches) |")
    w("")
    w("Id sets compared with the model at the end of the run (missing + extra ids; all must be 0):\n")
    names = [c["name"] for c in rows[0][2]["validation"]["checks"]]
    w("| Engine | " + " | ".join(names) + " |")
    w("|---|" + "---:|" * len(names))
    for s, n, r in rows:
        cs = {c["name"]: c for c in r["validation"]["checks"]}
        w(f"| {n} | " + " | ".join(f"{cs[x]['actual']:,} / {cs[x]['expected']:,}" + (f" (**{cs[x]['missing']} missing, {cs[x]['extra']} extra**)" if cs[x]['missing'] or cs[x]['extra'] else "") if x in cs else "–" for x in names) + " |")
    w("")
    # cross-engine hash equality, only for engines that ran the same number of operations
    by_ops = collections.defaultdict(list)
    for s, n, r in rows: by_ops[r["ops"]].append((n, r))
    for ops, items in sorted(by_ops.items()):
        if len(items) < 2: continue
        hashes = collections.defaultdict(set)
        for n, r in items:
            for q, vv in r["verification"].items(): hashes[q].add((vv["count"], vv["sha256"]))
        ok = all(len(h) == 1 for h in hashes.values())
        if not ok: bad_total += 1
        w(f"After {ops:,} operations the sorted id lists of the {len(hashes)} result sets hash identically in {', '.join(n for n, _ in items)}: **{'yes' if ok else 'NO'}**.\n")

    # ---- plots
    # latency by operation
    fig, axes = plt.subplots(len(rows), 1, figsize=(11, 2.9 * len(rows)), sharex=False, squeeze=False)
    for ax, (s, n, r) in zip(axes[:, 0], rows):
        y, k = r["latencySeries"], r["operationSeries"]
        for ki, kn in enumerate(KINDS):
            idx = [i for i, v in enumerate(k) if v == ki]
            ax.scatter(idx, [y[i] for i in idx], s=2, color=KIND_COLOR[kn], alpha=.55, linewidths=0, label=kn)
        for e in r["largestSegmentEvents"]:
            ax.axvline(e["op"], color="#7a1fa2", linewidth=.7, alpha=.6)
        ax.set_yscale("log"); ax.set_ylabel("µs"); ax.grid(alpha=.3, which="both", linewidth=.4)
        ax.set_title(f"{n}: p50 {us(r['latencyMicroseconds']['p50'])}, p99 {us(r['latencyMicroseconds']['p99'])}, max {us(r['latencyMicroseconds']['max'])}; "
                     f"{len(r['largestSegmentEvents'])} replacements of the largest segment", fontsize=9, loc="left")
        ax.set_xlim(0, len(y))
    axes[0, 0].legend(markerscale=5, fontsize=8, ncol=3, loc="upper right")
    axes[-1, 0].set_xlabel("operation number (visible on return)")
    fig.tight_layout(); fig.savefig(f"{d}/lifecycle-{config}-latency.png", dpi=130); plt.close(fig)

    # layout
    fig, axes = plt.subplots(2, len(rows), figsize=(4.6 * len(rows), 6), squeeze=False, sharex="col", gridspec_kw={"height_ratios": [2, 1]})
    for j, (s, n, r) in enumerate(rows):
        smp = r["samples"]
        ax, bx = axes[0, j], axes[1, j]
        ax.plot(smp["op"], smp["deadDocuments"], color=COLOR[s], label="dead, all segments")
        ax.plot(smp["op"], smp["largestDead"], color=COLOR[s], linestyle="--", label="dead, largest segment")
        ax.set_ylabel("dead documents"); ax.set_title(n, fontsize=9, loc="left"); ax.grid(alpha=.3, linewidth=.4)
        ax2 = ax.twinx(); ax2.step(smp["op"], smp["segments"], color="#888", linewidth=.8, where="post"); ax2.set_ylabel("segments", color="#666")
        ratio = [100 * a / b if b else 0 for a, b in zip(smp["largestDead"], smp["largestDocuments"])]
        bx.plot(smp["op"], ratio, color=COLOR[s]); bx.axhline(30, color="#555", linestyle=":", linewidth=1)
        bx.set_ylabel("dead in largest, %"); bx.set_xlabel("operation number"); bx.grid(alpha=.3, linewidth=.4)
        for e in r["largestSegmentEvents"]:
            ax.axvline(e["op"], color="#d62728", linewidth=.8, alpha=.6); bx.axvline(e["op"], color="#d62728", linewidth=.8, alpha=.6)
        if j == 0: ax.legend(fontsize=7, loc="upper left")
    fig.tight_layout(); fig.savefig(f"{d}/lifecycle-{config}-layout.png", dpi=130); plt.close(fig)

    # merges
    fig, ax = plt.subplots(figsize=(8, 4))
    for s, n, r in rows:
        smp = r["samples"]
        if smp["merges"] and max(smp["merges"]) > 0:
            ax.plot(smp["op"], [m - smp["merges"][0] for m in smp["merges"]], label=n, color=COLOR[s])
    ax.set_ylabel("merges so far"); ax.set_xlabel("operation number"); ax.legend(fontsize=8); ax.grid(alpha=.3, linewidth=.4)
    fig.tight_layout(); fig.savefig(f"{d}/lifecycle-{config}-merges.png", dpi=130); plt.close(fig)

    # reader
    if rr:
        fig, axes = plt.subplots(len(rr), 1, figsize=(11, 2.4 * len(rr)), sharex=True, sharey=True, squeeze=False)
        for ax, (s, n, r) in zip(axes[:, 0], rr):
            rs = r["readerSeries"]
            t, y, m = rs["seconds"], rs["microseconds"], rs["merging"]
            ax.scatter([t[i] for i in range(len(t)) if m[i] != 1], [y[i] for i in range(len(t)) if m[i] != 1], s=2, color=COLOR[s], alpha=.5, linewidths=0)
            ax.scatter([t[i] for i in range(len(t)) if m[i] == 1], [y[i] for i in range(len(t)) if m[i] == 1], s=3, color="#d62728", alpha=.6, linewidths=0)
            slow = r.get("slowWrites") or []
            ov = [i for i in range(len(t)) if any(t[i] < w1 and t[i] + y[i] / 1e6 > w0 for w0, w1 in slow)]
            ax.scatter([t[i] for i in ov], [y[i] for i in ov], s=12, color="black", marker="x", linewidths=.7)
            for e in r["largestSegmentEvents"]:
                ax.axvline(e["seconds"], color="#7a1fa2", linewidth=.7, alpha=.6)
            ax.set_yscale("log"); ax.set_ylabel("µs"); ax.grid(alpha=.3, which="both", linewidth=.4)
            c = r["concurrentReader"]
            ax.set_title(f"{n}: reader p50 {us(c['p50'])}, p99 {us(c['p99'])}, max {us(c['max'])}", fontsize=9, loc="left")
        axes[-1, 0].set_xlabel("seconds into the run")
        fig.tight_layout(); fig.savefig(f"{d}/lifecycle-{config}-reader.png", dpi=130); plt.close(fig)

open(f"{d}/report.md", "w").write("\n".join(out))
print(f"wrote {d}/report.md for configs {configs}; verification problems: {bad_total}")
