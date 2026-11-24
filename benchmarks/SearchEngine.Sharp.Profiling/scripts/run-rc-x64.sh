#!/usr/bin/env bash
# 0.5.6 (net11) vs release candidate on buildx64, pinned to one logical CPU.
# Usage: run-rc-x64.sh SE_DIR [CPU=2]   (SE_DIR has variants/base, variants/rc; trees and logs are created)
set -u
SE=$1; CPU=${2:-2}; V=$SE/variants; L=$SE/logs-rc; T=$SE/trees
mkdir -p $L $T
export DOTNET=/usr/share/dotnet/dotnet DOTNET_ROOT=/usr/share/dotnet DOTNET_GCHeapHardLimit=0x100000000
P="taskset -c $CPU"; X() { timeout ${TO:-900} $P $DOTNET "$V/$1/SearchEngine.Sharp.Profiling.dll" "${@:2}"; }
step() { echo "$(date +%T) $*" >> $L/steps.txt; }
step start
for n in 10000:s10k 100000:s100k 1000000:s1m; do [ -d $T/${n#*:} ] || $DOTNET $V/base/SearchEngine.Sharp.Profiling.dll gen-tree --root $T/${n#*:} --count ${n%:*} --seed 2026 >> $L/gen.jsonl; done
step trees
for tree in s10k s100k; do X base find --variant C --root $T/$tree --query zz > /dev/null
  for q in "*.pdf" "*report*" "*"; do for r in 1 2 3; do for v in base rc; do
    X $v find --variant A --instrument --root $T/$tree --query "$q" --label "$tree:$v" >> $L/commander.jsonl 2>>$L/err.txt
  done; done; X base find --variant C --root $T/$tree --query "$q" --label "$tree:base" >> $L/commander.jsonl; done
done
X base find --variant C --root $T/s1m --query zz > /dev/null
X base find --variant A --instrument --root $T/s1m --query "*.pdf" --label "s1m:base" >> $L/commander.jsonl 2>>$L/err.txt
for r in 1 2; do X rc find --variant A --instrument --root $T/s1m --query "*.pdf" --label "s1m:rc" >> $L/commander.jsonl 2>>$L/err.txt; done
X base find --variant C --root $T/s1m --query "*.pdf" --label "s1m:base" >> $L/commander.jsonl
step commander
for v in base rc; do for n in 10000 100000 1000000; do for p in FileMask Default; do X $v scale --n $n --preset $p --facets --label "x64:$v" >> $L/scale.jsonl 2>>$L/err.txt; done; done
  DOTNET_GCHeapHardLimit=0x200000000 TO=1500 X $v scale --n 10000000 --preset FileMask --label "x64:$v" >> $L/scale.jsonl 2>>$L/err.txt; done
step scale
for v in base rc; do DOTNET_TieredCompilation=0 TO=1500 X $v querypaths --sizes 10000,100000,1000000 --label "x64tc0:$v" >> $L/querypaths.jsonl 2>>$L/err.txt; done
step querypaths
for v in base rc; do for n in 100000 1000000; do X $v persistent --n $n --mutations 2000 --label "x64:$v" >> $L/persistent.jsonl 2>>$L/err.txt; done; done
step done
