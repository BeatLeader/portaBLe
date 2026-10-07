# Analysis/

Study of how well the map-difficulty / PP stack models Beat Saber play, and feasibility of three improvements.
**Start with [REPORT.md](REPORT.md).**

```
REPORT.md              findings, feasibility verdicts, plan, server notes
ALGO_ACC_TEST.md       the implemented test: algorithmic acc rating, calibration options A/B/C, portaBLe DB comparison
models/                alternative acc_model.json calibrations (option C)
figures/               PNGs used in the report (py/figures_*.py)
out/                   small result tables/JSON written by the analyses (large intermediates are git-ignored)
py/                    analysis scripts (a* = score dump, r* = replays, ppmodel.py = numpy port of the C# PP model;
                       decode_attempts.py + a13/a14 = attempts export from /admin/attemptsexport)
scripts/build_tools.sh stages analyzer variants and builds the two .NET tools
scripts/build_test_dbs.sh  builds the wwwroot/test-*.db comparison databases (py/compare_dbs.py compares two)
py/score_correct.py        score-informed predicted accuracy (reference for portaBLe's ScoreCorrection / --steps correct)
py/permap_curve_sim.py     calibrates the per-map acc curve (--relative-epsilon) on a B + pass fade DB's stored PP components
py/pass_blend_sim.py       pass fade vs piecewise trade vs p-norm blend (--pass-blend): goals, monotonicity, re-solved curve
tools/RatingsDump      recompute ratings + per-swing table + per-note ML predictions for a list of maps (prod or corpus analyzer)
tools/ReplayStudy      SwingCorpus + stratified replay sampling + per-note observation table + tip-motion metrics
```

## Typical run

```bash
# 1. tools (needs .NET 10 SDK; clones BeatLeader/ReplayDecoder once)
Analysis/scripts/build_tools.sh both                 # -> Analysis/.stage/bin/<Tool>-<variant>-<rid>/

# 2. data
python Analysis/py/decode_dump.py wwwroot/dump.zip $ANALYSIS_DATA          # scores/maps/players parquet
RatingsDump --lbs lbs.csv --maps-dir maps --out $ANALYSIS_DATA/ratings --tag prod       # also: --tag corpusdot (corpus build)

# 3. replay crawl (server; resumable, seeded random order => any prefix is a random sample)
ReplayStudy --lbs lbs.csv --maps-dir /data/maps --output out/full --api http://127.0.0.1:5000 --top 8 --mid 6

# 4. analyses
python Analysis/py/a01_latent_difficulty.py prod ; python Analysis/py/a02_structure.py prod ; ...
python Analysis/py/r01_note_acc.py <replay dir> <ai_notes csv.gz> <tag> ; ...
python Analysis/py/figures_scores.py ; python Analysis/py/figures_replays.py <r01 tag> <r04 tag>
```

`lbs.csv` = `lb_id,hash,mode,difficulty` (3 635 ranked leaderboards from the dump). `ANALYSIS_DATA` points at the data folder (parquet + ratings/).

## Notes

* **Variants.** `prod` = `RatingAPI/Analyzer` (what portaBLe uses). `corpus` = `RatingAPI-corpus/beatleader-analyzer` built *without* the dot model; `corpusdot` = with `dot_direction.onnx` registered.
  Controllers (ML inference, curves) are always the production ones; `RatingAPI-corpus/Controllers` is stale and does not compile against the corpus analyzer.
* **SwingCorpus.** `tools/ReplayStudy` is a port of `SwingCorpus` to the parser version both analyzers use (the original expects a newer static `MapParser` that is not in this tree). `SwingCorpus/` itself was not modified.
* **Server.** Run jobs under `systemd-run` with memory/CPU caps (see REPORT §8); the box is the production host.
