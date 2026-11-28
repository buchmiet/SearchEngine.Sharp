#!/usr/bin/env bash
# Sustained incremental indexing: every engine, every scenario, one process each, one after the other.
#   scripts/run-sustained.sh [outdir]
# PREV_DLL=/path/to/ThreeWayBenchmark.dll also runs SearchEngine.Sharp built from the commit before segments.
# N (default 1000000), OPS (default 20000), DURABLE_OPS (default 2000) and PACED_RATE (default 120 calls/s) change the sizes.
# ONLY="single single-reader-paced" runs only those scenarios.
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="${1:-results/sustained}"; mkdir -p "$OUT"
N="${N:-1000000}"; OPS="${OPS:-20000}"; DURABLE_OPS="${DURABLE_OPS:-2000}"
dotnet build -c Release -v q --nologo >/dev/null
DLL=bin/Release/net11.0/SearchEngine.ThreeWayBenchmark.dll

run() { # dll engine label tag extra...
  local dll="$1" engine="$2" label="$3" tag="$4"; shift 4
  if [ -n "${ONLY:-}" ]; then
    local scen="${tag#"$SLUG"-}"
    [[ " $ONLY " == *" $scen "* ]] || return 0
  fi
  echo "== $label $tag" >&2
  dotnet "$dll" --mode sustained --engine "$engine" --label "$label" --n "$N" --out "$OUT/$tag.json" "$@" 2> "$OUT/$tag.log" || echo "   exit $? (see $OUT/$tag.log)" >&2
}

scenarios() { # dll engine label slug durable(0/1)
  local dll="$1" engine="$2" label="$3" slug="$4" durable="$5"
  SLUG="$slug"
  run "$dll" "$engine" "$label" "$slug-single"        --ops "$OPS" --batch 1 --reader 0
  run "$dll" "$engine" "$label" "$slug-single-reader" --ops "$OPS" --batch 1 --reader 1
  # The same writes at one rate every engine sustains, so every reader runs as long and under the same write load.
  run "$dll" "$engine" "$label" "$slug-single-reader-paced" --ops "$OPS" --batch 1 --reader 1 --rate "${PACED_RATE:-120}"
  for b in 10 100 1000 10000; do
    ops="$OPS"; [ "$b" = 10000 ] && ops=$((OPS * 5 / 2))
    run "$dll" "$engine" "$label" "$slug-batch$b" --ops "$ops" --batch "$b" --reader 0
  done
  if [ "$durable" = 1 ]; then
    run "$dll" "$engine" "$label" "$slug-durable" --ops "$DURABLE_OPS" --batch 1 --reader 0 --durable 1
  fi
}

scenarios "$DLL" sharp   "SearchEngine.Sharp"          sharp   0
scenarios "$DLL" lucene  "Lucene.NET 4.8"              lucene  1
scenarios "$DLL" tantivy "TantivyDotNet 1.0.0-rc.2"    tantivy 1
if [ -n "${PREV_DLL:-}" ]; then
  scenarios "$PREV_DLL" sharp "SearchEngine.Sharp (previous: base + delta)" sharp-previous 0
fi
