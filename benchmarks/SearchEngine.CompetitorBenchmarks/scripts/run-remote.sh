#!/usr/bin/env bash
set -euo pipefail

REPO="${SE_REPO:-$HOME/searchengine-comp-bench}"
HOST="$(hostname | tr '[:upper:]' '[:lower:]')"
RID="$(uname -s | tr '[:upper:]' '[:lower:]')-$(uname -m)"
OUTPUT_SUFFIX="${RID}"

if [[ ! -d "$REPO/.git" ]]; then
  git clone --depth 1 git@github.com:buchmiet/SearchEngine.Sharp.git "$REPO"
fi

cd "$REPO"
git fetch --tags origin
git checkout main
git pull --ff-only origin main

BENCH="$REPO/benchmarks/SearchEngine.CompetitorBenchmarks"
bash "$BENCH/scripts/install-deps.sh"

export SE_BENCH_OUTPUT="$BENCH/results/$OUTPUT_SUFFIX"
mkdir -p "$SE_BENCH_OUTPUT"

cd "$BENCH"
bash scripts/run-all.sh

# Copy to a predictable artifact name
echo "Done: $SE_BENCH_OUTPUT"
