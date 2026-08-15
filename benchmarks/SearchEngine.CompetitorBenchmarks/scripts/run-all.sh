#!/usr/bin/env bash
set -euo pipefail

export PATH="/opt/homebrew/bin:/usr/local/bin:/usr/local/share/dotnet:$HOME/.cargo/bin:$PATH"

REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
BENCH_ROOT="$REPO_ROOT/benchmarks/SearchEngine.CompetitorBenchmarks"
HOST="$(hostname | tr '[:upper:]' '[:lower:]')"
OUTPUT="${SE_BENCH_OUTPUT:-$BENCH_ROOT/results/$HOST}"

mkdir -p "$OUTPUT"
cd "$BENCH_ROOT"

echo "=== SearchEngine.Sharp current ==="
dotnet run -c Release --project csharp -- --implementation sharp-current --output "$OUTPUT"

if [[ "${SKIP_HISTORICAL:-0}" != "1" ]] && git -C "$REPO_ROOT" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  if command -v pwsh >/dev/null; then
    pwsh -File "$BENCH_ROOT/scripts/run-sharp-historical.ps1" -OutputRoot "$OUTPUT"
  else
    bash "$BENCH_ROOT/scripts/run-sharp-historical.sh" "$OUTPUT"
  fi
fi

echo "=== Tantivy ==="
(cd tantivy && SE_BENCH_OUTPUT="$OUTPUT" SE_BENCH_IMPL=tantivy cargo run --release)

echo "=== MiniSearch ==="
(cd minisearch && npm install --silent && SE_BENCH_OUTPUT="$OUTPUT" SE_BENCH_IMPL=minisearch node bench.mjs)

if command -v go >/dev/null; then
  echo "=== Bleve ==="
  (cd bleve && SE_BENCH_OUTPUT="$OUTPUT" SE_BENCH_IMPL=bleve go run .)
fi

if command -v pwsh >/dev/null; then
  pwsh -File "$BENCH_ROOT/scripts/validate-hits.ps1" -ResultsDir "$OUTPUT"
else
  echo "Skip validate-hits (pwsh not available)"
fi

echo "Results: $OUTPUT"
