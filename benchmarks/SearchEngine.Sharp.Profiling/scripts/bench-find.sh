#!/usr/bin/env bash
# Commander-style search matrix: variants A/B/C/D x queries x repetitions, one process per run.
# Usage: bench-find.sh BIN ROOT LABEL OUT.jsonl [REPS=3] [CPUSET=] [QUERYSET=full|short]
#   BIN     path to SearchEngine.Sharp.Profiling executable
#   CPUSET  e.g. "2" (one logical CPU) or "2-5"; empty = no pinning (taskset needed on Linux)
# Each run is capped at 300 s. Output: one JSON object per line.
set -u
BIN=$1; ROOT=$2; LABEL=$3; OUT=$4; REPS=${5:-3}; CPUSET=${6:-}; QSET=${7:-full}
PIN=""; [ -n "$CPUSET" ] && PIN="taskset -c $CPUSET"
run() { timeout 300 $PIN "$BIN" find --root "$ROOT" --label "$LABEL" "$@" >> "$OUT" 2>>"$OUT.err" || echo "{\"failed\":true,\"label\":\"$LABEL\",\"args\":\"$*\"}" >> "$OUT"; }
QUERIES_FULL=(
  "--query *"
  "--query *.pdf"
  "--query *report*"
  "--query img_*"
  "--query package.json"
  "--query *.jpg|OR|*.png"
  "--query *.cs|AND|NOT|*tests*"
  "--regex --query \\.(jpg|png)\$"
  "--query *.log --size-min 1048576"
  "--query * --days 30"
  "--query *.pdf --size-min 1048576 --days 365"
)
QUERIES_SHORT=("${QUERIES_FULL[1]}" "${QUERIES_FULL[2]}" "${QUERIES_FULL[5]}" "${QUERIES_FULL[7]}" "${QUERIES_FULL[10]}")
if [ "$QSET" = short ]; then QUERIES=("${QUERIES_SHORT[@]}"); else QUERIES=("${QUERIES_FULL[@]}"); fi
# Warm the cache once.
"$BIN" find --variant C --root "$ROOT" --query zzzz-warmup > /dev/null 2>&1
for r in $(seq 1 "$REPS"); do
  for q in "${QUERIES[@]}"; do
    # "|" inside a query stands for a space within the pattern.
    read -r -a parts <<< "$q"
    for i in "${!parts[@]}"; do parts[$i]=${parts[$i]//|/ }; done
    run --variant A "${parts[@]}"
    run --variant B --policy adaptive --batch 20000 "${parts[@]}"
    run --variant B --policy debounce --interval 250 --batch 1000000 "${parts[@]}"
    run --variant B --policy single "${parts[@]}"
    run --variant C "${parts[@]}"
    run --variant D --threads 4 "${parts[@]}"
  done
done
