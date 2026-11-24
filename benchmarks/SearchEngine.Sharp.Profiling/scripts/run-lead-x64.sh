#!/usr/bin/env bash
# Second x64 batch (run after the find matrix): before/after variants, query paths, Unicode cost,
# scaling, persistent index, concurrency. Usage: run-lead-x64.sh SE_DIR (contains variants/, trees/, logs/)
set -u
SE=$1; CPU=${2:-2}; CPU4=${3:-2-5}; SKIP4=${4:-0}; V=$SE/variants; L=$SE/logs; T=$SE/trees
export DOTNET=/usr/share/dotnet/dotnet DOTNET_ROOT=/usr/share/dotnet DOTNET_GCHeapHardLimit=0x100000000
P="taskset -c $CPU"
step() { echo "$(date +%T) $*" >> $L/lead-steps.txt; }
step start
# 1. before/after on synthetic trees (pinned), hit hashes included
bash $SE/scripts2/bench-variants.sh $V $T/s100k s100k $L/variants.jsonl 2 $CPU full
bash $SE/scripts2/bench-variants.sh $V $T/s1m s1m $L/variants.jsonl 1 $CPU pdf "base ingest combined"
step variants-pinned
[ "$SKIP4" = 1 ] || bash $SE/scripts2/bench-variants.sh $V $T/s1m s1m-4cpu $L/variants-4cpu.jsonl 1 $CPU4 pdf "base combined"
[ "$SKIP4" = 1 ] || bash $SE/scripts2/bench-variants.sh $V $T/s100k s100k-4cpu $L/variants-4cpu.jsonl 2 $CPU4 short "base combined"
step variants-4cpu
# 2. query paths, pinned, TC=0 and default, base vs combined
for v in base combined; do
  DOTNET_TieredCompilation=0 timeout 1500 $P $DOTNET $V/$v/SearchEngine.Sharp.Profiling.dll querypaths --sizes 10000,100000,1000000 --label x64tc0:$v >> $L/querypaths.jsonl 2>>$L/querypaths.err
  timeout 1500 $P $DOTNET $V/$v/SearchEngine.Sharp.Profiling.dll querypaths --sizes 100000,1000000 --label x64:$v >> $L/querypaths.jsonl 2>>$L/querypaths.err
done
step querypaths
# 3. Unicode option cost (FileMask + Default build and queries), flags 0, 1, 3, 7, 15
for u in 0 1 3 7 15; do timeout 900 $P $DOTNET $V/unicode/SearchEngine.Sharp.Profiling.dll querypaths --sizes 100000,1000000 --unicode $u --label x64uni:$u >> $L/unicode-cost.jsonl 2>>$L/unicode-cost.err; done
step unicode
# 4. scaling, one process per N (peak RSS per run)
for v in base combined; do for n in 10000 100000 1000000; do for preset in FileMask Default; do
  timeout 900 $P $DOTNET $V/$v/SearchEngine.Sharp.Profiling.dll scale --n $n --preset $preset --facets --label x64:$v >> $L/scale.jsonl 2>>$L/scale.err
done; done; done
# 10^7: no facets, 10 GB heap cap (machine has 14 GB, swap must not be used)
for v in base combined; do
  DOTNET_GCHeapHardLimit=0x280000000 timeout 1500 $P $DOTNET $V/$v/SearchEngine.Sharp.Profiling.dll scale --n 10000000 --preset FileMask --label x64:$v >> $L/scale.jsonl 2>>$L/scale.err
done
step scale
# 5. persistent index + tiered prototype
for v in base combined; do for n in 10000 100000 1000000; do
  timeout 1200 $P $DOTNET $V/$v/SearchEngine.Sharp.Profiling.dll persistent --n $n --mutations 2000 --label x64:$v >> $L/persistent.jsonl 2>>$L/persistent.err
done; done
step persistent
# 6. concurrency (4 readers + writer), unpinned within CPUs 2-7
for v in base combined; do timeout 300 taskset -c $CPU4 $DOTNET $V/$v/SearchEngine.Sharp.Profiling.dll concurrency --n 100000 --readers 4 --seconds 5 >> $L/concurrency.jsonl 2>>$L/concurrency.err; done
step done
