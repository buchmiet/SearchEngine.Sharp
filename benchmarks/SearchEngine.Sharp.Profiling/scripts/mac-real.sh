#!/usr/bin/env bash
# Mac real trees: A (0.5.6 on .NET 11), A on combined, E on combined, C, D; hit hashes; 2 reps.
set -u
V=$HOME/Codex-dump/se/variants; OUT=$HOME/Codex-dump/se/logs-mac/real.jsonl
export DOTNET_GCHeapHardLimit=0x100000000
for root in /usr /opt/homebrew "$HOME/source/repos/Leith" /Applications; do
  lab=$(basename "$root")
  for q in "*.pdf" "*report*" "*"; do
    for r in 1 2; do
      H=""; [ $r = 1 ] && H="--hash"
      timeout 600 $V/base/SearchEngine.Sharp.Profiling find --variant A --root "$root" --query "$q" --label "mac-$lab:base" $H >> $OUT 2>/dev/null
      timeout 600 $V/combined/SearchEngine.Sharp.Profiling find --variant A --root "$root" --query "$q" --label "mac-$lab:combined" $H >> $OUT 2>/dev/null
      timeout 600 $V/combined/SearchEngine.Sharp.Profiling find --variant E --root "$root" --query "$q" --label "mac-$lab:combined" $H >> $OUT 2>/dev/null
      timeout 600 $V/base/SearchEngine.Sharp.Profiling find --variant C --root "$root" --query "$q" --label "mac-$lab:base" $H >> $OUT 2>/dev/null
      timeout 600 $V/base/SearchEngine.Sharp.Profiling find --variant D --threads 4 --root "$root" --query "$q" --label "mac-$lab:base" $H >> $OUT 2>/dev/null
    done
  done
done
