#!/usr/bin/env bash
# Build portaBLe comparison databases from wwwroot/dump.zip:
#   test-prod.db        ratings as exported from production (incl. reweights), PP recomputed
#   test-ml.db          re-rated through RatingAPI with the ONNX model, Curve2         (like-for-like baseline)
#   test-algo.db        re-rated with the algorithmic acc model (AccDifficultyModel), Curve2
#   test-algo-power.db  algorithmic acc model + power-law acc curve
#   test-ml-power.db    ONNX model + power-law acc curve (isolates the curve change)
# A variant is name:curve:source[:acc model json[:gamma[:acc scale]]] (power-law gamma / acc scale default to PpCurve's).
# Compare any two in the UI: copy one to wwwroot/Database.db and the other to wwwroot/Comparison.db, or start with
#   dotnet bin/Release/net9.0/portaBLe.dll --db wwwroot/test-algo-power.db --comparison wwwroot/test-ml.db
set -euo pipefail
cd "$(dirname "$0")/../.."
W=wwwroot
VARIANTS="${VARIANTS:-ml:Classic:ML algo:Classic:Algorithm algo-power:PowerLaw:Algorithm}"   # also: ml-power:PowerLaw:ML
WITH_PROD="${WITH_PROD:-0}"
# (the static web assets step hashes wwwroot/*.db: don't build while another process has a DB there open)
dotnet build portaBLe.csproj -c Release -v:q -nologo | grep -E "error|Error\(s\)|Elapsed" | sort -u | tail -6
# EF / bulk-extension command logging at Information level writes one log entry per updated row (GBs) -> keep it at Warning
export Logging__LogLevel__Default=Warning
run() { # app args...; fails unless the pipeline reports completion
  local out; out=$(dotnet bin/Release/net9.0/portaBLe.dll "$@" 2>&1 | tee /dev/stderr | tail -n 3)
  echo "$out" | grep -q "Pipeline done" || { echo "pipeline failed: $*" >&2; exit 1; }
}
APP=run

if [ ! -f "$W/test-import.db" ]; then
  $APP --db "$W/test-import.db" --steps import --exit
fi
if [ "$WITH_PROD" = 1 ] && [ ! -f "$W/test-prod.db" ]; then
  cp "$W/test-import.db" "$W/test-prod.db"
  $APP --db "$W/test-prod.db" --steps stars,scores,stats --exit
fi
for v in $VARIANTS; do
  IFS=: read -r name curve source model gamma accscale <<< "$v"
  [ -f "$W/test-$name.db" ] && { echo "skip test-$name.db (exists)"; continue; }
  extra=()
  [ -n "${model:-}" ] && extra+=(--acc-model "$model")
  [ -n "${gamma:-}" ] && extra+=(--gamma "$gamma")
  [ -n "${accscale:-}" ] && extra+=(--acc-scale "$accscale")
  cp "$W/test-import.db" "$W/test-$name.db.tmp"
  $APP --db "$W/test-$name.db.tmp" --steps rerate,scores,stats --acc-source "$source" --curve "$curve" ${extra[@]+"${extra[@]}"} --exit
  mv "$W/test-$name.db.tmp" "$W/test-$name.db"
done
ls -la $W/test-*.db
