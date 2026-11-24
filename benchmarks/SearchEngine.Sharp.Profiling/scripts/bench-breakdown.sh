#!/usr/bin/env bash
# Cost breakdown of one Commander-style search: instrumented variant A + sequential decomposition.
# Usage: bench-breakdown.sh BIN ROOT LABEL OUT.jsonl [CPUSET=]
set -u
BIN=$1; ROOT=$2; LABEL=$3; OUT=$4; CPUSET=${5:-}
PIN=""; [ -n "$CPUSET" ] && PIN="taskset -c $CPUSET"
"$BIN" find --variant C --root "$ROOT" --query zzzz-warmup > /dev/null 2>&1
for q in "*.pdf" "*"; do
  for r in 1 2; do timeout 300 $PIN "$BIN" find --variant A --instrument --root "$ROOT" --label "$LABEL" --query "$q" >> "$OUT" 2>>"$OUT.err"; done
  timeout 600 $PIN "$BIN" decompose --root "$ROOT" --label "$LABEL" --query "$q" --reps 2 >> "$OUT" 2>>"$OUT.err"
done
