#!/usr/bin/env bash
# Runs every engine in its own process, one after the other, so no engine shares a heap or a CPU with another.
# usage: scripts/run.sh [outdir]   (REPS_100K / REPS_1M override the repeat counts)
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="${1:-results}"; mkdir -p "$OUT"
dotnet build -c Release -v q --nologo >/dev/null
DLL=bin/Release/net11.0/SearchEngine.ThreeWayBenchmark.dll
for n in 100000 1000000; do
  reps=${REPS_100K:-3}; [ "$n" = 1000000 ] && reps=${REPS_1M:-2}
  for r in $(seq 1 "$reps"); do
    for e in sharp lucene tantivy; do
      echo "== $e n=$n run $r" >&2
      dotnet "$DLL" --engine "$e" --n "$n" --builds 3 --out "$OUT/$e-$n-run$r.json" 2> "$OUT/$e-$n-run$r.log"
    done
  done
done
