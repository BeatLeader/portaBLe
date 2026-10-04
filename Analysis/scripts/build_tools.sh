#!/usr/bin/env bash
# Stage analyzer variants and build the analysis tools (RatingsDump, ReplayStudy).
#
#   [TOOLS="ReplayStudy"] [VARIANTS="prod"] Analysis/scripts/build_tools.sh [win|linux|both]    (default: both)
#
# Variants:
#   prod   - RatingAPI/Analyzer (the submodule portaBLe uses today)
#   corpus - RatingAPI-corpus/beatleader-analyzer (SwingCorpus-driven changes)
# Controllers (ML inference, acc/star curves) always come from the production RatingAPI.
# Outputs: Analysis/.stage/bin/<Tool>-<variant>-<rid>/
set -euo pipefail
cd "$(dirname "$0")/../.."
ROOT="$PWD"
STAGE="${STAGE:-$ROOT/Analysis/.stage}"
TARGET="${1:-both}"
VARIANTS="${VARIANTS:-prod corpus}"
TOOLS="${TOOLS:-RatingsDump ReplayStudy}"

copy_clean() { # src dst
  mkdir -p "$2"; (cd "$1" && tar cf - --exclude=bin --exclude=obj --exclude=.git .) | (cd "$2" && tar xf -)
}

mkdir -p "$STAGE"
if [ ! -d "$STAGE/deps/ReplayDecoder" ]; then
  git clone --depth 1 https://github.com/BeatLeader/ReplayDecoder.git "$STAGE/deps/ReplayDecoder"
fi

stage_variant() {
  local variant="$1" analyzer_src parser_src models_src
  rm -rf "$STAGE/$variant/analyzer" "$STAGE/$variant/parser" "$STAGE/$variant/models"
  if [ "$variant" = prod ]; then
    analyzer_src=RatingAPI/Analyzer/beatleader-analyzer
    parser_src=RatingAPI/Analyzer/Parser/beatleader-parser
    models_src=RatingAPI
  else
    # corpus / corpusadj: nested parser copy is byte-identical to prod's; the analyzer csproj's sibling-path reference does not exist here
    analyzer_src=RatingAPI-corpus/beatleader-analyzer/beatleader-analyzer
    parser_src=RatingAPI-corpus/beatleader-analyzer/Parser/beatleader-parser
    models_src=RatingAPI-corpus
  fi
  copy_clean "$analyzer_src" "$STAGE/$variant/analyzer"
  copy_clean "$parser_src" "$STAGE/$variant/parser"
  sed -i 's#<ProjectReference Include="[^"]*Parser.csproj" />#<ProjectReference Include="../parser/Parser.csproj" />#' "$STAGE/$variant/analyzer/Analyzer.csproj"
  mkdir -p "$STAGE/$variant/models"; cp "$models_src"/*.onnx "$STAGE/$variant/models/"
  if [ "$variant" = corpusadj ]; then
    # replay-driven adjustments of the corpus cost model (REPORT section 6.2):
    #  1) resets do not cost precision -> RESET_TECH_WEIGHT 0.5 -> 0
    #  2) repeats cost physical work (tip travel x1.6-1.7 for rolls AND resets) -> frequency x (1 + 0.65*(P(reset)+P(roll)))
    #  3) below 0.2 s flagged repeats are alternations -> no roll probability there
    local tc="$STAGE/$variant/analyzer/BeatmapScanner/Algorithm/TransitionCosts.cs" sc="$STAGE/$variant/analyzer/BeatmapScanner/Algorithm/SwingCreation.cs"
    sed -i 's/RESET_TECH_WEIGHT = 0.50;/RESET_TECH_WEIGHT = 0.0;/' "$tc"
    sed -i 's/if (deltaTime <= 0) return 0;/if (deltaTime <= 0.2) return 0;/' "$tc"
    sed -i 's/swing.SwingFrequency \*= 1f + (float)TransitionCosts.ResetProbability(deltaTime);/swing.SwingFrequency *= 1f + 0.65f * (float)(TransitionCosts.ResetProbability(deltaTime) + TransitionCosts.RollProbability(deltaTime));/' "$sc"
    grep -q "RESET_TECH_WEIGHT = 0.0" "$tc" && grep -q "0.65f" "$sc" && grep -q "deltaTime <= 0.2" "$tc" || { echo "corpusadj patch failed"; exit 1; }
  fi
}

build_one() { # tool variant rid
  local tool="$1" variant="$2" rid="$3"
  local out="$STAGE/bin/$tool-$variant-$rid"
  local src="$STAGE/$variant/tools/$tool"
  rm -rf "$out" 2>/dev/null || true
  rm -rf "$src"; copy_clean "Analysis/tools/$tool" "$src"   # per-variant copy => private obj/ folder
  dotnet publish "$src/$tool.csproj" -c Release -r "$rid" --self-contained false \
    -p:Variant="$variant" \
    -p:AnalyzerDir="$STAGE/$variant/analyzer" \
    -p:ParserDir="$STAGE/$variant/parser" \
    -p:ControllersDir="$ROOT/RatingAPI/Controllers" \
    -p:CorpusUtilsDir="$ROOT/RatingAPI-corpus/Utils" \
    -p:ModelsDir="$STAGE/$variant/models" \
    -p:ReplayDecoderDir="$STAGE/deps/ReplayDecoder/ReplayDecoder" \
    -o "$out" -v:q -nologo
  echo "built $out"
}

case "$TARGET" in
  linux) RIDS="linux-x64" ;;
  win)   RIDS="win-x64" ;;
  *)     RIDS="win-x64 linux-x64" ;;
esac

for variant in $VARIANTS; do
  stage_variant "$variant"
  for rid in $RIDS; do
    for tool in $TOOLS; do build_one "$tool" "$variant" "$rid"; done
  done
done
