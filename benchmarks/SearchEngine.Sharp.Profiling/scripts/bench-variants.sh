#!/usr/bin/env bash
# Before/after matrix across library variants (one harness build per variant).
# Usage: bench-variants.sh VARIANTS_DIR ROOT LABEL OUT.jsonl [REPS=2] [CPUSET=] [QUERYSET=full|short|pdf] [A_VARIANTS="base lazy facets glob ingest combined"]
#   VARIANTS_DIR contains one folder per variant with SearchEngine.Sharp.Profiling.dll
set -u
VD=$1; ROOT=$2; LABEL=$3; OUT=$4; REPS=${5:-2}; CPUSET=${6:-}; QSET=${7:-short}; AV=${8:-"base lazy facets glob ingest combined"}
PIN=""; [ -n "$CPUSET" ] && PIN="taskset -c $CPUSET"
DOTNET=${DOTNET:-dotnet}
run() { local v=$1; shift; timeout 600 $PIN $DOTNET "$VD/$v/SearchEngine.Sharp.Profiling.dll" find --root "$ROOT" --label "$LABEL:$v" "$@" >> "$OUT" 2>>"$OUT.err" || echo "{\"failed\":true,\"label\":\"$LABEL:$v\",\"args\":\"$*\"}" >> "$OUT"; }
case "$QSET" in
  short) QUERIES=("*.pdf" "*report*" "*") ;;
  pdf) QUERIES=("*.pdf") ;;
  *) QUERIES=("*.pdf" "*report*" "img_*" "package.json" "*") ;;
esac
$DOTNET "$VD/base/SearchEngine.Sharp.Profiling.dll" find --variant C --root "$ROOT" --query zzzz-warmup > /dev/null 2>&1
for q in "${QUERIES[@]}"; do
  # Hit-set validation: hashes of sorted hit paths must agree across variants.
  for var in A E; do run combined --variant $var --query "$q" --hash; done
  run base --variant E --query "$q" --hash
  run base --variant C --query "$q" --hash
  for r in $(seq 1 "$REPS"); do
    for v in $AV; do run $v --variant A --instrument --query "$q"; done
    for v in base combined; do run $v --variant E --instrument --query "$q"; done
    run combined --variant E --policy single --query "$q"
    run base --variant C --query "$q"
    run base --variant D --threads 4 --query "$q"
  done
done
# Regex with hits (alternation needs the space-free form).
run base --variant A --regex --query '\.(jpg|png)$' --hash
run combined --variant A --regex --query '\.(jpg|png)$' --hash
run base --variant C --regex --query '\.(jpg|png)$' --hash
