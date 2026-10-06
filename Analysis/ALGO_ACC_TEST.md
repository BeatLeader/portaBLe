# Test: algorithmic (ML-free) acc rating in portaBLe

Branches: `portaBLe/algo-acc-test`, `RatingAPI/algo-acc` (submodule). Nothing changes unless you opt in — the default acc source is still the ONNX model and the default curve is still `Curve2`.

## What it is

`AccRating` is unchanged in form — `15.5 / Curve(predictedAcc + 0.0022) × LowNoteNerf` — only the **predicted accuracy** behind it
comes from a documented linear model instead of `model_sleep_bl.onnx`:

1. `AccDifficultyFeatures.Compute` (RatingAPI/Controllers/AccDifficultyModel.cs) turns the analyzer's own output into 76 map features:
   mean / p50 / p90 / p99 of ten per-swing quantities (swing speed, frequency, angle strain, repositioning, rotation, hit distance,
   stress, `SwingDiff`, `SwingTech`, NJS), rolling peaks (8/32/128 swings) of `SwingDiff`, speed, tech and frequency, pattern
   fractions (parity, dots, chains, multi-note, streams, linear, bomb avoidance, forehand, hand balance, frequency > 4/6/8 Hz),
   counts/length/density/BPM and the analyzer's pass, tech, low-note nerf, linear %, multi %, parity errors.
   Time-based features are in *played* time (the analyzer scales swing speed and NJS by the speed modifier but not cube times),
   so the same code rates SS / FS / SF / BFS / BSF.
2. `acc_model.json` (embedded in RatingAPI) holds the standardized ridge weights. The model predicts the **score-implied map
   difficulty** `d` — the map term of `log(1 − acc) = d_map − skill_player` fitted on all modifier-free scores — and converts it:
   `predictedAcc = 1 − exp(d − reference_skill)`.
3. Level and spread: `d' = center + difficulty_scale · (d − center)`, then `predictedAcc = 1 − exp(d' − reference_skill)`.
   The embedded model uses the **full spread** (`difficulty_scale` 1) and `reference_skill` 4.178, which keeps the top-1000
   players' PP unchanged. The full spread is ~1.2× wider than the ML's, and that turns out to be the central policy question —
   see **Calibration** below. Alternative calibrations live in separate files (`Analysis/models/acc_model_cal.json`), selected
   with `AccModelPath` / `--acc-model`; they never change the ordering of maps, so the R² comparisons below hold for all of them.
4. Speed modifiers: the raw feature response overstates the *average* difficulty change, so modded difficulty is
   `d_base + k(timescale) · (d_mod − d_base)` with `k` = 0.90 (SS), 0.42 (FS), 0.68 (SF), fitted on 221 k SS/FS/SF scores.
   (FS's low `k` partly reflects that players pick FS on maps they find comfortable — treat it as provisional.)

Optional, separate switch: **power-law acc curve** (`CurveMode.PowerLaw` in portaBLe `ReplayUtils`):
`curve(acc) = ((1 − acc + 0.0016) / 0.0516)^−0.601` for both the rating and the PP curve (best constant-exponent fit to `Curve2`),
acc PP scaled ×1.052 to keep the top-1000 players' PP level. The report (§4.2, §5) explains why the algorithm should ship *with* it.
`--gamma` / `--acc-scale` override the exponent and level (option B below uses 0.500 / 1.000).

## How it was fitted (fresh dump, Oct 2026)

`python Analysis/py/fit_acc_model.py --data <dir> --features <dir>/ratings/features_feat.csv` (features produced by
`RatingsDump --no-ai --no-swings`, i.e. by the same C# code that later predicts, so there is no train/serve skew — verified: C# vs
Python predictions agree to 1.5e‑8 on all 3 872 maps).

| | value |
|---|---|
| training target | 3 542 647 modifier-free scores, 32 761 players (≥ 15 scores), 3 872 Standard maps (≥ 40 scores) |
| ML predicted acc → score-implied difficulty | R² 0.883 |
| **this model, 76 features, song-grouped 5-fold CV** | **R² 0.958** (residual SD 0.095 log-error) |
| 3-term closed form (pass, tech, ln swings) | R² 0.901 |
| lasso-selected 44 features | R² 0.958 (weights are not fragile) |

**Model card — what carries the signal** (song-grouped CV R², fresh dump). Individual ridge weights on correlated features are not meaningful
on their own (e.g. `stress_mean` gets −0.43 and `swing_diff_mean` +0.29 because they move together), so groups are ablated instead:

| feature group | alone | all others without it (drop from 0.958) |
|---|---|---|
| `SwingDiff` mean / quantiles / peaks (7) | 0.829 | 0.956 (−0.002) |
| swing speed & frequency (18) | 0.884 | 0.956 (−0.002) |
| tech parts: angle strain, repositioning, rotation, `SwingTech` (19) | 0.666 | 0.953 (**−0.005**) |
| stress & hit distance (8) | 0.569 | 0.955 (−0.003) |
| NJS (4) | 0.619 | 0.953 (**−0.005**) |
| pattern fractions (9) | 0.257 | 0.957 (−0.001) |
| counts, length, density, BPM (5) | 0.772 | 0.956 (−0.002) |
| analyzer ratings: pass, tech, nerf, linear %, multi %, parity errors (6) | 0.902 | 0.958 (0.000) |

Every group is largely redundant with the rest; speed/frequency alone already matches the ML (0.884) and the published ratings alone
reach 0.902. The model is therefore robust to any single feature family drifting (e.g. analyzer changes to parity handling), and could be
pruned considerably if a smaller model is preferred (the lasso subset keeps 44 features at the same R²).

Validation (`python Analysis/py/validate_algo.py --data <dir>`):

| check | ML | algorithm |
|---|---|---|
| speed modifiers, R² of the real per-map difficulty shift — SS / FS / SF | 0.003 / 0.041 / 0.008 | **0.209 / 0.153 / 0.602** |
| mean shift vs real (SS −0.187, FS +0.093, SF +0.243) | −0.082 / +0.111 / +0.254 | −0.194 / +0.085 / +0.228 |
| acc‑PP bias between maps at equal skill, power‑law curve (SD of log acc PP, players at 0.90 … 0.98) | 0.105 … 0.095 | **0.060 … 0.053** (out-of-fold) |
| same with `Curve2` | 0.110 … 0.098 | 0.098 … 0.065 (worse for 0.93–0.97 players — ship with the curve) |
| player rankings | — | see the DB comparison below (Spearman 0.998–0.999 vs the ML build) |

Robustness to *which* scores define difficulty: re-estimating it from only the scores that count (top 40 per player), only the last
12 months, or a random half of the players correlates 0.983–0.9995 with the all-score estimate, and the algorithm explains it with
R² 0.954–0.962 in every case (ML 0.862–0.882).

## Running it

```bash
# RatingAPI service: "AccSource": "Algorithm" in appsettings (default ML). Responses now also carry
#   acc_source, ml_predicted_acc, algo_predicted_acc (both are always computed).
#   "AccModelPath": optional alternative acc_model.json (e.g. Analysis/models/acc_model_cal.json).

# portaBLe: scripted DB builds (new --db/--steps/--acc-source/--acc-model/--curve/--gamma/--acc-scale/--exit options; web UI unchanged)
Analysis/scripts/build_test_dbs.sh       # -> wwwroot/test-{ml,algo,algo-power}.db from wwwroot/dump.zip (skips existing files)
VARIANTS="ml-power:PowerLaw:ML algo-power-flat:PowerLaw:Algorithm::0.5001:1.0004 \
  algo-cal:Classic:Algorithm:Analysis/models/acc_model_cal.json" Analysis/scripts/build_test_dbs.sh   # the other test DBs
python Analysis/py/compare_dbs.py wwwroot/test-algo-power-flat.db wwwroot/test-ml.db --md out.md   # tables below

# compare in the UI (DatabaseComparison page): current DB vs comparison DB
dotnet bin/Release/net9.0/portaBLe.dll --db wwwroot/test-algo-power.db --comparison wwwroot/test-ml.db
```

Re-fitting after analyzer changes: rebuild `RatingsDump` (`Analysis/scripts/build_tools.sh win`), dump features with
`--no-ai --no-swings --tag feat`, run `fit_acc_model.py` (writes `RatingAPI/acc_model.json`), dump again with `--tag algo`, run
`validate_algo.py --write-speed-scale` (adds the speed calibration), rebuild RatingAPI.

## Calibration: the one real decision

Swapping the source of predicted accuracy is easy; deciding **how PP should grow with skill** is not, because the ML ratings
quietly answer that question for you. The ML compresses map difficulty to ~0.76 of the score-implied spread (the algorithm: 0.96), so easy maps are
over-rated relative to hard ones. Players mostly play maps near their own level, so this over-reward lands on lower-ranked players
and flattens today's PP-versus-skill profile. A rating that is fair between maps removes that subsidy, and something has to replace it.

Why it cannot be had for free: with full-spread ratings and a power-law curve, a score's acc PP is
`∝ (exp(d − a))^−γ / (exp(d − a_ref))^−γ = exp(γ (a − a_ref))`. The map term cancels, which is the fairness, and γ, the exponent
that rewards accuracy *within* a map, is also the exponent of PP *across* skill. With compressed ratings (`d' = c + s (d − c)`), a
factor `exp(−γ (1 − s) d)` survives, so harder maps pay less at equal skill, and a flatter profile is bought with map unfairness.

| option | acc source / curve | map fairness: SD of log acc PP at equal skill (players at 0.90 / 0.95 / 0.98) | PP-vs-skill profile | within-map acc reward |
|---|---|---|---|---|
| today | ML / `Curve2` | 0.110 / 0.094 / 0.098 | — | — |
| **A** fair | algorithm (full spread) / power-law γ 0.601 | **0.060 / 0.058 / 0.053** | top 100 gain, ranks > 1000 lose (table below) | unchanged |
| **B** fair, profile kept | algorithm (full spread) / power-law, γ re-solved to **0.500** (acc scale 1.000) | **0.050 / 0.048 / 0.044** | kept: median PP of ranks 1–1000 and 10 001–50 000 unchanged | flatter: 96 → 97 % is worth +15 % acc PP instead of +18 % |
| **C** no curve change | algorithm, spread compressed to **0.610** (`reference_skill` 3.946) / `Curve2` | 0.081 / **0.051** / 0.120 (hard maps underpaid for top players) | kept | unchanged |

(Out-of-fold algorithm predictions, maps with ≥ 100 scores; `python Analysis/py/fairness_options.py [--scale --center
--reference-skill --gamma]`. The SD scales with γ, so A and B are equally fair relative to their own acc reward, and both are about
half of today's. The ML under a power-law curve stays unfair at any γ: 0.105 / 0.102 / 0.095 at 0.601, 0.088 / 0.085 / 0.079
at 0.500.)

**Recommendation: B.** It is fair at every skill level and keeps today's PP levels by rank; the price is a flatter accuracy reward,
which is a visible, explainable parameter rather than a hidden subsidy (side effect: +10 % for players below rank 50 000, see below). A is the same ratings with today's acc reward; choose it
if a 10–13 % PP drop below rank 1000 is acceptable (rank *order* barely moves). C avoids a curve change but needs the spread squeezed
to 0.61, even more than the ML's 0.76. That fixes the mid-skill bias but underpays hard maps for the 0.98 players, worse than today.
Solvers: `calibrate_scale.py --mode gamma` (B) and `calibrate_scale.py --model <copy> --write` (C).

## portaBLe DB comparison (fresh dump)

Every DB is the same import (144 722 players with PP, 3 960 ranked leaderboards), re-rated through RatingAPI and fully recomputed
(scores → players → stats) by `build_test_dbs.sh`. The baseline is `test-ml.db` (ONNX model, `Curve2`), so production reweights
don't blur the comparison. Median player PP change by baseline rank:

| DB | what changes | Spearman (player PP) | top-100 overlap | rank 1–100 | 101–1 000 | 1 001–10 000 | 10 001–50 000 | 50 001+ | top-1000 \|rank change\| median | stars \|Δ\| median |
|---|---|---|---|---|---|---|---|---|---|---|
| `test-ml-power` | curve only | 0.9993 | 85 | +3.6 % | +0.1 % | −4.8 % | −2.5 % | +2.8 % | 61 | 0.53 |
| `test-algo` | algorithm, full spread, `Curve2` | 0.9986 | 83 | +1.9 % | +0.1 % | −7.9 % | −15.3 % | −16.3 % | 68 | 0.95 |
| `test-algo-power` | **A**: algorithm, full spread, power-law | 0.9985 | 88 | +5.1 % | −0.6 % | −11.3 % | −13.5 % | −10.1 % | 34 | 0.40 |
| `test-algo-power-flat` | **B**: algorithm, full spread, power-law γ 0.500 | 0.9977 | 88 | +2.4 % | −0.0 % | −4.4 % | −0.0 % | +9.6 % | 36 | 0.40 |
| `test-algo-cal` | **C**: algorithm, spread 0.610, `Curve2` | 0.9987 | 85 | +1.7 % | −0.1 % | −2.1 % | −0.0 % | +4.6 % | 37 | 0.39 |

* `test-algo`, B and C agree with the Python simulation used to calibrate them (`calibrate_scale.py`) to within 0.5 points per band.
* Two calibration bands cannot pin the whole profile. B and C hold ranks 1–1 000 and 10 001–50 000 exactly, while ranks 1 001–10 000
  dip 2–4 % and the 50 001+ tail gains: B +9.6 % (p90 +34 %), because a flatter γ pays low-accuracy scores relatively more; C +4.6 %.
  Adding a third band (or tuning the curve's ε) is the next lever if that matters.
* Rank churn in the top 1000 (median 34–37 places for A/B/C) is about half of the uncalibrated `Curve2` build (68) and below the
  curve change alone (61). The ratings fix and the power-law curve partly cancel each other's reshuffles.
* Stars move by a median 0.4★ in A, B and C. Per-variant details (biggest map and player movers, Megametric, PP composition) are in
  `Analysis/out/compare_*_vs_ml.md`.

### Where the stars move most — who is right?

On the maps where the ML and the algorithm disagree most, the score-implied difficulty decides, using the algorithm's
**out-of-fold** prediction so it cannot have memorised the map:

| largest disagreements | algorithm closer | median \|error\| ML / algorithm | algorithm off by > ½ SD | ML off by > ½ SD |
|---|---|---|---|---|
| top 20 | 90 % | 0.53 / 0.09 | 10 % | 90 % |
| top 50 | 88 % | 0.44 / 0.10 | 14 % | 86 % |
| top 250 | 85 % | 0.29 / 0.09 | 11 % | 67 % |

Examples: *Speedcore Paradise* and *Unwelcome School* rise to 17–19★ because players score far below what the ML predicts
(score-implied difficulty +1.15 / +1.33; ML implies +0.22 / +0.85; algorithm out-of-fold +1.33 / +1.37). *Holdin' On (Expert)*
falls from 9.6★ to 6–7★ for the opposite reason. A real algorithm miss: *My Album Is Out On Dance Corps…* (out-of-fold +0.80 vs
+0.27 score-implied, ~1.2 SD too hard). Three of the twenty largest movers in `test-algo-power` (ten up, ten down) are **OneSaber** maps (not in the training set);
on all 68 OneSaber maps with ≥ 40 scores the algorithm still explains score-implied difficulty with R² 0.89 (ML 0.45), from a
joint skill fit across modes.

Raw top-10 accuracy per map is *not* a fair judge here (it mostly reflects which players a map attracts): against it both
correlate about equally (ML 0.911, algorithm 0.900).

## Field test (portaBLe Hub, Oct 6)

**Speed modifiers in A/B looked broken: they were extrapolating.** On maps that people play with speed modifiers
(≥ 15 scores) the algorithm's shift matches real scores bin by bin (SF: predicted +0.11 … +0.47, real +0.11 … +0.57, while the ML
predicts a flat +0.23 … +0.27). But on maps nobody plays at that speed the linear model extrapolates far beyond anything observed
(SF shift up to +1.09 on 12–17★ maps, 169 maps beyond the largest observed value), and the power-law curve turns that
exponentially into 25–34★ SF ratings. The shift is now limited to the range real scores confirm (`speed_shift_limit`: SS 0.241,
FS 0.173, SF 0.585; 99th percentile over maps with speed scores; `validate_algo.py --write-speed-scale` writes it). The few maps with
data beyond the limit show real shifts as large as predicted (SF +0.70 vs +0.66), so the limit is a safety margin, not a correction.

**The worst offender is one map, not modifiers in general.** *My Album Is Out On Dance Corps…* (E+) is the algorithm's worst miss
(+0.42 harder than its 1 619 scores imply; the next worst ranked map is +0.37), and its SF shift is also overestimated (+0.80
predicted, +0.50 real). Both errors compound to 1 100–1 200 PP SF scores where the ML gives ~700. Across all 105 k FS/SF scores
worth > 300 PP, the median PP change matches unmodified scores, so modifiers are not systematically overpaid.
`out/algo_vs_scores_outliers.md` lists the 20 worst misses in each direction: 47 ranked maps (1.3 %) are off by more than 0.25,
versus 353 for the ML. A guard such as "with ≥ 200 scores, keep the rating within ±0.25 of the score-implied difficulty" would catch
exactly these; it is not implemented because it makes ratings depend on scores (see below).

**C over-rates easy maps for top players, as expected.** Example `dda51` (*Let Mom Sleep*, Hard): Bizzy's SF 98.8 % pays 828 PP
with the ML, 783 in A, 727 in B and 938 in C, where it becomes his #1 score. (Stars are not comparable across curves: B shows 6.1★
against the ML's 5.0★ yet pays less.) The algorithm agrees with the scores here (predicted −0.17, score-
implied −0.15), and A/B rate the map *easier* than the ML (predicted accuracy 0.9869 vs 0.9860). C's compressed spread pulls easy
maps towards the middle. C is not a viable option.

**Do maps nobody plays look too hard from scores?** Only weakly. Top-2 % players score as expected on the least-played maps
(mean residual −0.002) and slightly *worse* than expected on the most-played ones (+0.046): if anything, farmed maps look a little
easier than they are. On `dda51`, 24 top-player scores beat the expectation by 0.09 (≈ 5 % PP). The effort question is still
open and needs the attempts data.

## Known limitations

* Weights are fitted on today's ranked Standard pool; maps far outside it (gimmicks, extreme speeds, other characteristics) rely
  on extrapolation of a linear model (it held up on OneSaber, above). Predictions are clamped to 0.5 … 0.9995.
* Calibration and validation use all clean scores. Effort is the remaining confound: a map that players grind gets a better best
  score than one they try twice. The attempts data (REPORT §7) is the way to correct for it.
* `LowNoteNerf` is still applied on top (production policy). The score-implied target already contains the farm-ability of short maps,
  so this is a deliberate double penalty you may want to revisit.
* The per-note graph endpoints still use the ONNX model.
* The bulk-insert dependency (`Z.EntityFramework.Extensions.EFCore`) was bumped 7.105.4 → 7.105.8.1 because the monthly trial of the old
  version had expired; nothing else in portaBLe depends on the version.
