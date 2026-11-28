#!/usr/bin/env bash
# Index lifecycle (additions, updates, deletions): every engine, every configuration, one process each, one after the other.
#   scripts/run-lifecycle.sh [outdir]          then:   python3 scripts/report_lifecycle.py [outdir]
#
# Configurations, all with mix add:20,update:60,delete:20 and 90% of the updates and deletions on untouched original documents:
#   main    n=200,000, 120,000 operations (Tantivy: OPS_TANTIVY=40,000, see below). About 86,000 original documents die, 43% of the largest segment, so
#           SearchEngine.Sharp rewrites it (at 30% dead, MergePolicy.DeadRatio) and the other engines show how they reclaim it.
#           Expected: Sharp < 1 min, Lucene.NET 5 to 8 min, Tantivy 5 to 6 min.
#   small   n=50,000, 60,000 operations, the same count in every engine: the largest segment is rewritten in every engine, Tantivy included
#           (60,000 x ~7 ms). Expected: Sharp < 1 min, Lucene.NET 2 to 3 min, Tantivy 7 to 8 min.
#   paced   n=50,000, 30,000 operations at PACED_RATE=100 calls per second (300 s each) with one reader thread. Tantivy needs ~5 ms per visible change
#           plus the untimed checks, so 100 per second is a rate all three sustain. Expected: 5 min per engine, 15 min in all.
#   big     n=1,000,000, 60,000 operations (Tantivy: BIG_OPS_TANTIVY=20,000): dead documents in a 1M-document segment and merges around it; the largest
#           segment is NOT rewritten (4% dead; that would take ~420,000 operations: BIG_OPS=450000, then Lucene.NET and Tantivy need over an hour each).
#           The id list of every live document (1M ids) is not compared in this configuration (--full-ids 0: Tantivy has no bulk id export and is
#           slow at it); the live count and every other id set are. Expected: Sharp 1 to 2 min, Lucene.NET 8 to 12 min, Tantivy 6 to 8 min (1M builds included).
#
# Tantivy makes a change visible only through a commit (4 to 5 ms each, ~7 ms with the untimed probes), so its operation counts are separate
# (OPS_TANTIVY, BIG_OPS_TANTIVY); its stream is a prefix of the others' (same seed). The whole matrix takes about 70 minutes on a quiet 4-vCPU machine.
# ONLY="main paced" runs only those configurations, ENGINES="sharp lucene" only those engines. N/ops/mix can be overridden through the variables below.
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="${1:-results/lifecycle}"; mkdir -p "$OUT"
MIX="${MIX:-add:20,update:60,delete:20}"; OLD="${OLD:-0.9}"
MAIN_N="${MAIN_N:-200000}"; MAIN_OPS="${MAIN_OPS:-120000}"; OPS_TANTIVY="${OPS_TANTIVY:-40000}"
SMALL_N="${SMALL_N:-50000}"; SMALL_OPS="${SMALL_OPS:-60000}"
PACED_N="${PACED_N:-50000}"; PACED_OPS="${PACED_OPS:-30000}"; PACED_RATE="${PACED_RATE:-100}"
BIG_N="${BIG_N:-1000000}"; BIG_OPS="${BIG_OPS:-60000}"; BIG_OPS_TANTIVY="${BIG_OPS_TANTIVY:-20000}"
ENGINES="${ENGINES:-sharp lucene tantivy}"
dotnet build -c Release -v q --nologo >/dev/null
DLL=bin/Release/net11.0/SearchEngine.ThreeWayBenchmark.dll

label_of() { case "$1" in sharp) echo "SearchEngine.Sharp";; lucene) echo "Lucene.NET 4.8";; tantivy) echo "TantivyDotNet 1.0.0-rc.2";; esac; }

run() { # engine config n ops extra...
  local engine="$1" config="$2" n="$3" ops="$4"; shift 4
  if [ -n "${ONLY:-}" ]; then [[ " $ONLY " == *" $config "* ]] || return 0; fi
  echo "== $engine $config (n=$n ops=$ops)" >&2
  dotnet "$DLL" --mode lifecycle --engine "$engine" --label "$(label_of "$engine")" --n "$n" --ops "$ops" --mix "$MIX" --old-share "$OLD" \
    --check-every $((ops / 5)) --out "$OUT/$engine-$config.json" "$@" 2> "$OUT/$engine-$config.log" || echo "   exit $? (see $OUT/$engine-$config.log)" >&2
}

for e in $ENGINES; do
  main_ops="$MAIN_OPS"; big_ops="$BIG_OPS"
  if [ "$e" = tantivy ]; then main_ops="$OPS_TANTIVY"; big_ops="$BIG_OPS_TANTIVY"; fi
  run "$e" main  "$MAIN_N"  "$main_ops"  --reader 0
  run "$e" small "$SMALL_N" "$SMALL_OPS" --reader 0
  run "$e" paced "$PACED_N" "$PACED_OPS" --reader 1 --rate "$PACED_RATE"
  run "$e" big   "$BIG_N"   "$big_ops"   --reader 0 --full-ids 0
done
