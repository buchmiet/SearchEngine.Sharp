#!/usr/bin/env bash
set -euo pipefail

OUTPUT_ROOT="${1:-}"
REPO_ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
BENCH_ROOT="$REPO_ROOT/benchmarks/SearchEngine.CompetitorBenchmarks"
WORKTREES_ROOT="$BENCH_ROOT/.worktrees"
if [[ -z "$OUTPUT_ROOT" ]]; then
  OUTPUT_ROOT="$BENCH_ROOT/results/$(uname -s | tr '[:upper:]' '[:lower:]')-$(uname -m)"
fi
mkdir -p "$OUTPUT_ROOT"

run_ref() {
  local ref="$1"
  local impl="$2"
  local no_facet="${3:-0}"
  local no_glob="${4:-0}"
  local sha
  sha="$(git -C "$REPO_ROOT" rev-parse "$ref")"
  local safe="${ref//[\\/:]/-}"
  local worktree="$WORKTREES_ROOT/$safe"
  if [[ ! -d "$worktree" ]]; then
    mkdir -p "$WORKTREES_ROOT"
    git -C "$REPO_ROOT" worktree add "$worktree" "$sha" --detach
  fi
  local args=(run -c Release --project "$BENCH_ROOT/csharp" "/p:SharpSourceRoot=$worktree" -- --implementation "$impl" --output "$OUTPUT_ROOT" --git-sha "$sha")
  [[ "$no_facet" == "1" ]] && args+=(--no-facet)
  [[ "$no_glob" == "1" ]] && args+=(--no-glob)
  echo "=== $impl @ $sha ==="
  dotnet "${args[@]}"
}

run_ref "1bd312c" "sharp-0.5.0-initial" 1 1
run_ref "v0.5.5" "sharp-0.5.5" 0 0

echo "Historical Sharp results in $OUTPUT_ROOT"
