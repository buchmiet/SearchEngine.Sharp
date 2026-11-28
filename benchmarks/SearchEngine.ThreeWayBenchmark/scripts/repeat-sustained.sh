#!/usr/bin/env bash
# Repeats the single-document scenario (no reader) so the tails can be compared across runs: scripts/repeat-sustained.sh [outdir] [reps]
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="${1:-results/sustained}"; REPS="${2:-2}"; N="${N:-1000000}"; OPS="${OPS:-20000}"
DLL=bin/Release/net11.0/SearchEngine.ThreeWayBenchmark.dll
for r in $(seq 2 $((REPS + 1))); do
  for spec in "sharp|SearchEngine.Sharp|sharp" "lucene|Lucene.NET 4.8|lucene" "tantivy|TantivyDotNet 1.0.0-rc.2|tantivy" ${PREV_DLL:+"sharp|SearchEngine.Sharp (previous: base + delta)|sharp-previous"}; do
    IFS='|' read -r engine label slug <<< "$spec"
    dll="$DLL"; [ "$slug" = sharp-previous ] && dll="$PREV_DLL"
    echo "== $slug rep $r" >&2
    dotnet "$dll" --mode sustained --engine "$engine" --label "$label" --n "$N" --ops "$OPS" --batch 1 --reader 0 --out "$OUT/$slug-single-rep$r.json" 2> "$OUT/$slug-single-rep$r.log" || echo "  exit $?" >&2
  done
done
