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
STEPS=rerate,correct,scores,stats VARIANTS="algo-b-permap:PowerLaw:Algorithm::0.7328:1.1307:--pass-fade+1,0.3,-2+--relative-epsilon+0.75" \
  Analysis/scripts/build_test_dbs.sh                                # B + per-map curve + score correction (algo-b-permap)
STEPS=rerate,correct,scores,stats VARIANTS="algo-b-blend:PowerLaw:Algorithm::0.6198:1.1301:--relative-epsilon+0.75+--pass-blend+1.5" \
  Analysis/scripts/build_test_dbs.sh                                # … with pass PP as a p-norm instead of the fade (algo-b-blend)
STEPS=rerate,scores,stats VARIANTS="algo-b-v3:PowerLaw:Algorithm::0.6198:1.1301:--relative-epsilon+0.75+--pass-blend+1.5" \
  Analysis/scripts/build_test_dbs.sh                                # 87-feature acc model, no score correction (algo-b-v3)
STEPS=rerate,scores,stats VARIANTS="algo-b-v4:PowerLaw:Algorithm::0.6198:1.1301:--relative-epsilon+0.75+--pass-blend+1.5+--pass-model+Energy"   Analysis/scripts/build_test_dbs.sh                                # acc model v4 + pass rating v2 (algo-b-v4)

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

**"Maps with short speedy parts get the benefit of a high pass rating and of the acc/tech rating."** Checked on B-v2 with
burstiness = log(hardest 32 swings / median swing difficulty) (analyzer `SwingDiff`; the 8-swing and swing-speed versions agree,
r 0.92–0.93), maps with ≥ 100 scores, effects per SD of burstiness (peak/median ×1.32):

| | effect | reading |
|---|---|---|
| pass success rate at equal pass rating | +0.012 logit (t 1.4) | pass rating is *not* inflated by bursts |
| score-implied acc difficulty − B, all players / top 2 % | −0.0003 / −0.004 | acc rating is *not* inflated (≈ 0.2 % PP for top players) |
| Megametric at equal stars, B / ML | **+0.036 / +0.029** (t 19 / 14) | burst maps *are* top plays far more often |
| … at equal pass, acc and tech ratings, B | +0.010 (t 6) | 70 % of it is the rating mix |

So the claim holds for the payout, in ML and slightly more in B, but not as a rating error. A burst map has more pass rating and less
acc difficulty per star than a smooth map. With a fair acc curve a player earns about the same acc PP on any map, so what separates
maps for a strong player is pass (and tech) PP, which is paid in full although passing is not the challenge for them; the burst map
lets them keep high accuracy *and* collect the pass bonus. From the smoothest to the burstiest fifth of maps, at equal stars,
Megametric rises by ~0.10 (≈ +50 %) and top-play scores are worth ~6 % more of the player's best score. The remaining 30 % (at equal
ratings) is mostly acc PP among top plays, i.e. selection/grinding. B adds per-map misses on top: *I Gotchu*, *Made In Love*,
*Truth Or Dare*, *Go Insane* have Megametric 0.6–0.9 in B vs 0.3–0.6 in the ML (two of them are on the over-rated list).
Directions: make pass PP fade when passing is clearly not the limit (e.g. by the score's accuracy relative to the map's predicted
accuracy), or pay pass PP from a sustained-difficulty pass rating (longer window) while the peak keeps classifying passability.
Attempts data will show directly how hard bursts are to *pass* for each skill level.

### B + pass fade (test deployment `algo-b-fade`)

Pass PP rewards getting through a map; when a score's accuracy shows that passing was not the limit, that reward fades.
`h = log((1 − predictedAcc) / (1 − acc))` measures how much lower the score's error rate is than the acc model's prediction for
the reference player (speed modifiers use their own prediction, so SF scores are judged against SF difficulty). Pass PP is kept in
full for `h ≤ −2` and falls as `0.3 + 0.7·exp(−(h + 2))` above it (`PassFade`, `--pass-fade 1,0.3,-2`). Top-100 players keep about 40 %
of their pass PP, so harder maps still pay more. The curve is re-solved as for B (median PP of ranks 1–1000 and 10 001–50 000
unchanged): γ 0.592, acc scale 1.245. The accuracy reward is back near today's (γ 0.601 vs B's 0.500), because the profile no longer
has to be flattened through the curve.

Moving top players' PP from pass to accuracy exposed near-perfect scores on short Easy maps (*let you* Easy 99.92 %: 1 265 PP, the
top score of the whole DB; ML 770). `AccCap` (`--acc-cap 0.6`) stops acc/tech PP growing once a score's error rate is more than
e^0.6 = 1.8× below the prediction. Every score above h = 0.75 is on an Easy/Normal map; the best hard-map plays (*Speedcore
Paradise*, *Gravisphere Crisis*) reach 0.52–0.58. 98 scores are affected and the rank bands do not move.

| | B-v2 | **B + pass fade + cap** | ML |
|---|---|---|---|
| burst overpay: Megametric per SD of burstiness at equal stars | 0.0355 | **0.0291** | 0.0290 |
| FS/SF share of the top 1 000 scores | 33.3 % | **14.4 %** | 16.4 % |
| FS/SF share of top-100 players' PP | 22 % | **10.7 %** | 21 % |
| pass share of top-100 players' PP | 24.7 % | 7.5 % | 23.6 % |
| median PP change, ranks 1–100 / 1 001–10 000 / 50 001+ (vs ML) | +2.3 / −4.4 / +9.6 % | +4.6 / −5.7 / +5.7 % | — |
| Spearman of player PP vs ML | 0.9977 | 0.9985 | — |
| highest score | 1 026 | 1 150 (*Speedcore Paradise*; top 8 all E+ feats) | 929 |

The fade parameters were chosen on a simulation of B-v2's stored PP components (`passfade_sim`, reproduces the DB's PP and
Megametric exactly) over a grid of centers −2 … −1, strengths 1–2, floors 0–0.3; a milder fade (−1.5, 1, 0.3) leaves the burst
overpay at 0.031, a stronger one (−2, 2, 0) removes nearly all pass PP for top players (0.022, γ 0.64).

Top-10 / top-100 mean PP +7.3 % / +6.2 % vs the ML (the pass-to-accuracy shift favours the best accuracy players).

### B + per-map curve + score correction (test deployment `algo-b-permap`)

Three changes on top of B + pass fade, from the field test (*toromi hearts 2* over-rated, *Speedcore Paradise* at 22★, a flat
acc curve above ~98 % on most maps):

**1. Per-map acc curve instead of `AccCap`** (`--relative-epsilon 0.75`). The cap made every curve flat once the error rate was
1.8× below the prediction: *HONESTY* Expert from 98.6 %, *let you* Easy from 99.64 %. On *Speedcore Paradise* PP even fell above
96.95 %, because the fade judged pass PP on the uncapped accuracy (fixed: fade and cap now both use the capped accuracy). The curve's
offset is now per map, `ε_j = 0.0016 + 0.75·(1 − predictedAcc_j)`, and `AccRating` is anchored at the predicted accuracy itself (no
+0.0022). Acc PP then depends on the error ratio `(1 − acc) / (1 − predictedAcc)`, keeps rising to 100 % and saturates smoothly.
γ 0.733 and acc scale 1.131 are re-solved as before (median PP of ranks 1–1000 and 10 001–50 000 unchanged).

| PP at | 97 % | 98 % | 99 % | 99.5 % | 99.8 % | 99.92 % | 100 % |
|---|---|---|---|---|---|---|---|
| *Speedcore Paradise* E+, B + fade + cap | 1 161 | 1 156 | 1 151 | 1 149 | 1 147 | 1 147 | 1 146 |
| … per-map curve | 890 | 1 010 | 1 180 | 1 293 | 1 374 | 1 410 | 1 435 |
| *HONESTY* Expert, B + fade + cap | 680 | 871 | 1 083 | 1 080 | 1 079 | 1 078 | 1 078 |
| … per-map curve | 616 | 739 | 949 | 1 120 | 1 263 | 1 332 | 1 384 |
| *let you* Easy, B + fade + cap | 177 | 237 | 381 | 588 | 703 | 703 | 703 |
| … per-map curve | 172 | 235 | 379 | 552 | 767 | 910 | 1 041 |

Rejected alternatives (`Analysis/py/permap_curve_sim.py`, on B + fade's stored PP components, reproduces the built DB):

- **Pure error-ratio curve** (no absolute 0.0016). Near-perfect Easy scores top the whole DB even at k = 1. Easy maps are
  noisier: residual SD 0.42 vs 0.30 on mid maps, because one miss is a large part of the error budget. The absolute 0.0016 keeps
  that extra damping.
- **Soft cap** (slope 0.25 above the cap). It never saturates: 100 % on *Speedcore Paradise* would pay 2 794.

**2. Score-informed predicted accuracy** (`--steps rerate,correct,…`, `ScoreCorrection`, τ 0.09; reference
`Analysis/py/score_correct.py`). Uses clean scores of players with ≥ 15 of them, fitting
`log(1 − acc) = log(1 − predictedAcc_j) + c_j − s_i` with the prior `c_j ~ N(0, 0.09²)`. A map moves by `n / (n + 12)` of what
its scores say (speed-modifier predictions move by the same `c_j`).

- Examples: *toromi hearts 2* Hard 98.45 → 98.70 % (7.6 → 6.9★); *My Album Is Out…* E+ 96.95 → 97.97 % (14.1 → 9.9★);
  *Speedcore Paradise* E+ 94.44 → 95.10 %. The largest moves towards harder are *OKAY*, *Chrome Vox* and *RTX 20000*
  (+0.34 log error).
- The correction is not an artefact of weak players struggling on hard maps. Fitted on the top 25 % / top 10 % of players
  only, the hardest band (predicted error > 3.5 %, 112 maps) moves by +0.092 / +0.078 vs +0.081 on everyone.
- For the top 2 %, the newest maps' over-rating drops from +0.032 to +0.003 (mean residual advantage on 2024–26 maps).

**3. Stars use the same formula as scores**: PP of a 96 % score with the map's curve, fade and cap, / 52. *Speedcore Paradise*
E+ 22.2 → 15.4★.

| | B + fade + cap | **B per-map + correction** | ML |
|---|---|---|---|
| stars p50 / p90 / max | 7.65 / 13.29 / 23.36 | **7.51 / 11.44 / 17.29** | 6.96 / 11.08 / 15.76 |
| FS/SF share of the top 1 000 scores | 14.4 % | **11.6 %** | 16.4 % |
| FS/SF share / pass share of top-100 players' PP | 10.7 % / 7.5 % | **8.7 % / 7.6 %** | 21 % / 23.6 % |
| highest score | 1 150 (*Speedcore Paradise*) | **926** (*Unwelcome School* E+ 96.33 %) | 929 |
| best play on a < 4★ map | 749 | **905** (*let you* Easy 99.92 %, 4th overall) | 844 |
| Megametric by predicted error < 1 % / 1–2 % / 2–3.5 % / > 3.5 % | 0.061 / 0.166 / 0.280 / 0.359 | **0.079 / 0.178 / 0.305 / 0.461** | 0.042 / 0.179 / 0.278 / 0.217 |
| Megametric by upload year 2018–19 / 20–21 / 22–23 / 24–26 | 0.256 / 0.163 / 0.177 / 0.220 | **0.287 / 0.184 / 0.204 / 0.223** | 0.144 / 0.178 / 0.179 / 0.214 |
| median PP change vs ML, ranks 1–100 / 1 001–10 000 / 50 001+ | +4.6 / −5.7 / +5.7 % | **−0.6 / −3.3 / +2.9 %** | — |
| Spearman of player PP vs ML | 0.9985 | **0.9983** | — |

**Old maps.** Megametric on 2018–19 maps stays about twice the ML's, but this is not an overpay. Per skill tier, scores on those
maps match the prediction: the mean residual advantage is between −0.026 and +0.018 for every tier, and +0.018 for the top 2 %
(≈ 1 % acc PP). The high Megametric is selection: Megametric counts how often a map is among its players' best plays, and old
maps are mostly played by people who pick them on purpose. A flat Megametric across eras is a different target from equal PP for
equal skill, and the reweighter is the tool for it. The hardest band is similar: its Megametric doubles vs the ML because those
112 maps are now the top plays of the players who can play them, and the players' own scores agree they are harder than B rated.

### Pass PP as a p-norm instead of a fade (test deployment `algo-b-blend`)

Review remark: decaying pass PP by accuracy creates flat spots and can break monotonicity, and a blend or a piecewise
construction is needed. It was right. The fade subtracts pass PP at a rate proportional to the pass PP itself, and nothing makes
acc PP grow faster than that. The table shows the total PP a score loses while its accuracy rises, on `algo-b-permap`, every map,
30–99.99 %:

| ratings | maps where PP falls | lose > 5 PP | lose > 20 PP | worst |
|---|---|---|---|---|
| no modifier | 18 | 1 | 0 | 6.9 |
| FS | 107 | 26 | 1 | 24.9 |
| SF | 454 | 247 | 61 | 90.1 (*Extratongue* E+ SF: 687 PP at 50 %, 649 at 70 %) |

The curve also kinks where the fade starts (h = −2), and on 244 maps (1 099 with SF ratings) its slope falls below a quarter
of the unfaded slope over up to 12 (25) acc points. 26 % of clean scores lie in the first half unit after the fade start.

`PassBlend` (`--pass-blend p`) combines the parts as `(pass^p + (acc + tech)^p)^(1/p)`: p = 1 is today's sum, p → ∞ the maximum.
Pass PP dominates while acc/tech PP is small and merges into it as accuracy rises. The result is monotone by construction
(both terms only grow) and smooth, with no trigger point and one parameter. It is still relative to the map: acc PP is a
function of the score's error rate against the map's prediction, so the ratio of pass to acc PP says whether passing or
accuracy was the achievement. The pass/acc/tech parts are reported as Euler shares, which add up to the total.
Candidates compared on `algo-b-permap`'s stored components (`Analysis/py/pass_blend_sim.py`, which reproduces the built DB),
each with γ and acc scale re-solved:

| | fade | trade β 0.5 | **blend p 1.5** | blend p 2 | blend p 2.5 |
|---|---|---|---|---|---|
| PP falls as acc rises (worst, all maps and mods) | 90 PP | 0 | **0** | 0 | 0 |
| burst overpay (Megametric per SD burstiness; ML 0.029) | 0.034 | 0.037 | **0.025** | 0.017 | 0.013 |
| Megametric SD / maps ≥ 0.65 | 0.139 / 1.1 % | — | **0.117 / 0.6 %** | 0.102 / 0.3 % | 0.099 / 0.2 % |
| top play | 926 *Unwelcome School* | 961 *let you* Easy | **887 *Unwelcome School*** | 890 (*let you* Easy 886, 2nd) | 900 *let you* Easy |
| best play on a < 4★ map (ML 844) | 905 | 961 | **840** | 886 | 900 |
| γ / acc scale | 0.733 / 1.131 | 0.749 / 1.174 | **0.620 / 1.130** | 0.612 / 1.183 | 0.608 / 1.199 |

The piecewise "trade" variant removes at most β of the acc/tech PP gained above the fade start. It is monotone, but it keeps
the burst overpay and lets near-perfect Easy scores top the DB. With the p-norm, p ≥ 2 balances maps further but brings the
*let you* Easy 99.92 % score back into the top 3, so p = 1.5 it is. Pass fade and acc cap are off in this variant; the per-map
curve and score correction are as in `algo-b-permap`.

| PP at | 50 % | 70 % | 90 % | 97 % | 99 % | 99.8 % | 100 % |
|---|---|---|---|---|---|---|---|
| *Extratongue* E+ SF | 634 | 709 | 944 | 1 230 | 1 400 | 1 494 | 1 521 |
| *Speedcore Paradise* E+ | 260 | 326 | 542 | 856 | 1 086 | 1 235 | 1 280 |
| *HONESTY* Expert | 137 | 182 | 336 | 609 | 890 | 1 137 | 1 228 |
| *let you* Easy | 24 | 36 | 86 | 205 | 402 | 730 | 945 |

| | B per-map + correction (fade) | **B blend p 1.5** | ML |
|---|---|---|---|
| stars p50 / p90 / max | 7.51 / 11.44 / 17.29 | **7.71 / 11.37 / 16.61** | 6.96 / 11.08 / 15.76 |
| FS/SF share of the top 1 000 scores | 11.6 % | **14.7 %** | 16.4 % |
| FS/SF share / pass share of top-100 players' PP | 8.7 % / 7.6 % | **11.2 % / 9.5 %** | 21 % / 23.6 % |
| Megametric by predicted error < 1 % / 1–2 % / 2–3.5 % / > 3.5 % | 0.079 / 0.178 / 0.305 / 0.461 | **0.107 / 0.185 / 0.287 / 0.439** | 0.042 / 0.179 / 0.278 / 0.217 |
| median PP change vs ML, ranks 1–100 / 1 001–10 000 / 50 001+ | −0.6 / −3.3 / +2.9 % | **−2.0 / −3.4 / +7.3 %** | — |
| Spearman of player PP vs ML | 0.9983 | **0.9957** | — |

Easy maps gain a little relative to the fade: they have little pass PP to lose to the blend, and the re-solved acc scale lifts
them. Their Megametric rises from 0.079 to 0.107, still the lowest band. Ranks 50 001+ gain 7 % against the ML; only two rank
bands are pinned by the calibration.

### Why maps disagree with their scores: a better acc model instead of the score correction (test deployment `algo-b-v3`)

The score correction lets a map's own scores move its rating after ranking. That is the same family as Autoreweight, with
the same problems: grinding feeds back into ratings, sandbagging is possible, and ratings can't be reproduced from the map.
This variant drops it and uses the disagreement as a guide for the acc model instead.

**Disagreement** `e` = score-implied difficulty minus the acc model's song-grouped out-of-fold prediction, in log error
(`a15_disagreement.py`, maps with ≥ 100 clean scores):

| question | answer |
|---|---|
| real or noise? | real: SD 0.094 vs sampling noise 0.015 |
| only weaker players? | no: the top quarter's own map term gives the same SD (0.096), r 0.92 with everyone's; the population gap explains 2.5 % |
| nonlinearity in the 76 features? | barely: gradient boosting on them gets CV R² 0.958 vs ridge 0.958 |
| not about the map at all? | weak: popularity r −0.12, upload year −0.14 (2024+ maps 0.03 easier), retries +0.18 |
| mapping style? | partly: other maps of the same mapper predict `e` with r 0.33, other difficulties of the same song r 0.36 |
| how is the accuracy lost? (replays, `a15c_replay_mechanism.py`) | mid-field excess misses and post-swing losses track `e` (−0.32 → +0.34 log from the "easier" to the "harder" fifth); top players lose almost only centre-cut points |
| analyzer vs actual play (`a15e_analyzer_vs_observed.py`) | on the easy outliers players reset / repeat directions the analyzer does not assume (*My Album* +50 % resets, *let you* Easy +40 %) |

What map data explains, out-of-fold (`a15b_content_features.py`, `a15d_map_files.py`):

| feature group | share of `e` explained |
|---|---|
| layout: horizontal cuts, crossovers, outer lanes (→ harder) | 11–12 % |
| map file: jump distance (long JD → harder), walls, arcs | 5 % |
| rhythm (odd snaps, tempo/BPM changes), flow (turns, travel, arcs), patterns, density over time | ≈ 0 |
| all of the above, linear / gradient boosting | 16 % / 16 % |
| ridge + boosted residual on the portable set (ceiling) | 27 % |

About three quarters of the disagreement is not in the map data available here: song and sync, the play style players
choose, effort.

**Model change** (RatingAPI `AccDifficultyFeatures`): 11 new features (`frac_horizontal`, `frac_diagonal`, `frac_cross`,
`frac_outer`, `frac_top_row`, `frac_bottom_row`, `jump_distance`, `reaction_time` from Info.dat NJS and offset,
`arcs_per_note`, `wall_cover`, `walls_per_s`), refitted with `fit_acc_model.py`:
- song-grouped CV R² 0.958 → 0.965; disagreement SD 0.094 → 0.086;
- maps off by > 0.15: 405 → 322; maps off by > 0.25: 61 → 43;
- speed calibration re-fitted (k 0.89 / 0.42 / 0.67).

Some maps get worse, e.g. *The Glock* Expert −0.09 → −0.32. The outliers that are mostly play style stay: *My Album*
predicted 96.95 → 97.10 % (the score correction had moved it to 97.97 %), *toromi hearts 2* Hard 98.45 → 98.51 %.
`Analysis/out/a15_disagreement_maps.csv` lists every map's remaining disagreement for the ranking team: before and after,
standard error, the top quarter's view, and evidence flags (hints, not proof).

`algo-b-v3` = this model + per-map curve + pass blend p 1.5, **no score correction** (γ / acc scale kept: ranks 1–1000 and
10 001–50 000 move +0.8 % / +1.4 % vs `algo-b-blend`):

| | B blend (with correction) | **B v3 (no correction)** | ML |
|---|---|---|---|
| stars p50 / p90 / max | 7.71 / 11.37 / 16.61 | **7.73 / 11.36 / 16.50** | 6.96 / 11.08 / 15.76 |
| FS/SF share of the top 1 000 scores | 14.7 % | **16.2 %** | 16.4 % |
| FS/SF share / pass share of top-100 players' PP | 11.2 % / 9.5 % | **12.1 % / 9.5 %** | 21 % / 23.6 % |
| top 3 plays | 887 *Unwelcome School*, 870 *Chrome Vox*, 862 *Break* | **881 *Unwelcome School*, 870 *Gravisphere Crisis*, 867 *let you* Easy 99.92 %** | 929, 911, 910 |
| Megametric 2018–19 / 20–21 / 22–23 / 24–26 | 0.287 / 0.187 / 0.206 / 0.227 | **0.258 / 0.169 / 0.183 / 0.225** | 0.144 / 0.178 / 0.179 / 0.214 |
| Spearman of player PP vs ML | 0.9957 | **0.9965** | — |

**Bottom-up feasibility** (`a16_swing_loss.py`). Expected point loss per swing as a function of pattern, context and
player skill, fitted on 21.6 M (replay, swing) observations and summed over each map, like pass rating:
- It never sees the map's own scores (song-grouped CV), yet predicts score-implied difficulty with R² 0.93 (mid skill)
  and 0.91 (top). The map-level ridge gets 0.966, and blending adds nothing.
- Its misses correlate 0.54 with the ridge's: the same outliers.
- It does not predict how much more strong players gain on a particular map (r 0.05), but the scores barely vary in that
  respect either.

So one difficulty number per map plus the playerbase's skill distribution already gives the "share of players reaching
each accuracy" curve. A bottom-up model's value would be explanation (which sections or patterns cost accuracy), not a
better rating, unless more replay data (full crawl, more strata) changes that.

### Where accuracy is lost (leaderboard page view, deployment `algo-b-v3`)

A bottom-up, interpretable companion to the acc rating, like pass rating is for passing (`a17_acc_loss_model.py`). It does
not change ratings or PP.

**Model.** Every swing gets an expected point loss (share of its notes' 115 points) in three components:
- precision (centre cut);
- swing angles (pre/post);
- misses / bad cuts.

Each component is `exp(intercept + Σ factor terms + skill term) × calibration`, a Poisson GLM on one-hot bins fitted on 21.6 M
(replay, swing) observations. Factor terms are 0 at each factor's most common level (the "typical swing"). The factors are the
analyzer's per-swing quantities plus context: the hand's eBPM (since v5; it was the analyzer's swing speed plus time since the
hand's last swing), timing of both hands, direction change, hand movement, angle strain, repositioning, hit distance, cut direction, lane, row, crossover, multi-note pattern,
chain, parity break, bombs, walls, NJS, jump distance, density, and minutes into the map.

Validation on held-out songs:

| | result |
|---|---|
| per-swing deviance explained | 0.38 (black-box gradient boosting on the same inputs: 0.42) |
| map level vs score-implied difficulty | R² 0.90 |
| "where" (within-map Spearman over ~5 s sections, best replays / mid-field) | **0.74 / 0.60** |
| … the analyzer's swing difficulty (a pass-style graph) | 0.59 / 0.56 |
| … the replays' own split-half agreement | 0.71 / 0.40 |

The model ranks a map's sections like the replays do, as well as half of the replays agrees with the other half.

Selected effects (multiplier vs the typical swing):
- **misses:** ≥ 400 eBPM ×5.3, crossover ×4.1, 280–330 eBPM ×3.3, repositioning ×3.2, parity break ×2.2, NJS < 10 ×0.16;
- **swing angles:** ≥ 400 eBPM ×8.8, 280–330 eBPM ×3.2, notes in the same spot as the last (tiny arcs) ×2.8, long hit distance ×2.6;
- **precision:** ≥ 400 eBPM ×1.4, sparse sections ×0.7.

The typical swing is 60–90 eBPM.

**On the page.** A card shows:
- points lost per note over time, stacked by component;
- a skill selector (playerbase percentiles, each with its expected accuracy);
- the top causes of each section (tooltip) and of the whole map;
- where the replay study covered the map, the observed loss of its best and mid-field replays.

**Modifier selector (2026-10-09).** A dropdown under the title (None / SS / FS / SF; `?mod=SF` in the URL) switches the whole page
to that variant. The PP curve's own SS/FS/SF buttons drive the same selector. It switches:
- the rating triangle and the preview sliders to the modifier's ratings;
- the PP curve to the modifier's predicted accuracy;
- both tabs of this card to the modifier's profile.

`export_acc_loss_profiles.py` writes `<id>:SS/FS/SF` rows with the same swings in played time:
- eBPM, NJS and density scale with the speed;
- gaps and section times scale with 1 / speed;
- the total is anchored to the modifier's predicted accuracy.

`export_pass_profiles.py` shifts each swing's PassDiff exactly as the analyzer does: speed × k, its low-speed falloff, and the NJS buff
at NJS × k. The page's pass levels match the stored SS/FS/SF pass ratings within ±0.3 % (p1–p99). Modifier views are model only:
the replays and attempts are unmodified play.

Skill scales each component by one factor, so the page recomputes any skill exactly. The **total** per skill follows the map's
acc rating (latent model: `log(1 − acc(s)) = log(1 − predictedAcc) + reference_skill − s`), so the expected accuracies match the
rating. The bottom-up model only splits that total over sections and causes. On maps the rating over- or under-rates (e.g.
*My Album*, *Chrome Vox*), the replay line shows the gap. Profiles live in an `AccLossProfiles` table that
`export_acc_loss_profiles.py` adds to a DB. Pages of DBs without it are unchanged.

```bash
python Analysis/py/a17_acc_loss_model.py --replays <snap> --swings <swings_prod.csv.gz> --skill <player_skill.parquet> \
  --maps <map_d.parquet> --mapfile <a15_mapfile_features.parquet> --out <dir>       # -> Analysis/models/acc_loss_model.json
python Analysis/py/export_acc_loss_profiles.py --model Analysis/models/acc_loss_model.json --a17 <dir> --skill <...> \
  --replays <snap> --db wwwroot/test-algo-b-v3.db                                   # adds AccLossProfiles (~32 MB)
```

Limitations:
- It is fitted on the replay study's sample: 47 % of maps, top 8 and 6 mid-field replays each.
- Profiles are for the unmodified map only.
- Attribution splits each swing's excess over the typical swing among its harder-than-typical factors, in proportion to
  their log effects. Factors that make a swing *easier* are not shown.
- For production it would move into RatingAPI next to the analyzer, like pass rating. The model is a JSON of bins and
  coefficients, so the port is small.

### v4: everything combined (test deployment `algo-b-v4`)

`algo-b-v4` combines:
- **acc model v4:** 90 features. The 87 of v3 plus `repetition_2/4/8`, the share of 2-, 4- and 8-swing sequences already seen
  earlier in the map, found in the attempts' learning analysis (REPORT §7.2).
- **pass rating v2:** energy bar with re-weighted swing difficulty, see `PASS_RATING.md`; no fatigue term, which was tested and
  did not help.
- **the rest of v3:** per-map acc curve (k 0.75), pass PP as a p-norm (p 1.5), no score correction, and the "where accuracy is
  lost" view.

| | v3 | **v4** | ML |
|---|---|---|---|
| acc model CV R² (song-grouped) / disagreement SD | 0.9647 / 0.0866 | **0.9682 / 0.0821** | 0.883 |
| pass rating R² vs pass difficulty from attempts | 0.856 | **0.917** | 0.856 |
| stars p50 / p90 / max | 7.73 / 11.36 / 16.50 | **7.70 / 11.47 / 16.83** | 6.96 / 11.08 / 15.76 |
| top 3 plays | 881 *Unwelcome School*, 870 *Gravisphere Crisis*, 867 *let you* Easy | **898 *Unwelcome School*, 885 *Feral*, 874 *Speedcore Paradise*** | 929, 911, 910 |
| best play on a < 4★ map | 867 | **855** | 844 |
| FS/SF share of the top 1 000 scores / of top-100 players' PP | 16.2 % / 12.1 % | **18.8 % / 12.0 %** | 16.4 % / 21 % |
| median PP vs v3, ranks 1–100 / 1–1 000 / 10 001–50 000 | — | **+0.7 / +0.5 / −0.2 %** | — |
| Spearman of player PP vs ML | 0.9965 | **0.9964** | — |

The FS/SF share of single top scores rises because pass v2 raises the pass rating of some maps that players often play
with SF. The SF/FS ratings relative to the base map are unchanged at every difficulty (within 1 %), and the share of top
players' PP is unchanged.
- Some of those raises are right: *toromi hearts 2* E+ goes from 2.8 to 0.6 logits under-rated, *Memento Mori* from 2.7 to
  1.1, *The Dark Field* from 1.2 to −0.2.
- Others overshoot: *Bookmaker* Expert goes from +1.0 to −1.5, *osu!memories* E+ from −1.4 to −2.5, *Legend of Millennium*
  Expert from −0.7 to −1.7.

The overshoots are the first candidates for a term-level fix (likely the stress weight on these tech patterns).

### v5: One Saber as a parameter (test deployment `algo-b-v5`)

A reviewer flagged *The Spoils* E+ One Saber (`3898fx92`). The difficulty at the top end was right, but the curve was not steep
enough, so the map paid too much to lower-skill players. The scores agree:
- Its error rate falls with player skill at slope −0.67, where Standard maps average −1 by construction. That is flatter than
  99.9 % of maps.
- One Saber maps in general have a median slope of −0.86 against overall skill, but −0.99 against skill measured on One Saber
  maps only. One Saber players are specialists: they do better on these maps than their overall skill predicts, and more so
  lower down.

One Saber is now a characteristic with its own skill sensitivity β, not a separate model:

- **Acc model.** The 70 One Saber maps join the fit with a `one_saber` indicator, for 91 features. Their error rates follow
  skill as `log(1 − acc) = d − β · skill`, with β = 0.909 (pooled within-map; 95 % CI 0.882–0.936). β is stored as
  `mode_skill_scale` in `acc_model.json`, and the predicted accuracy is `1 − exp(d − β · reference skill)`.
  `fit_acc_model.py --one-saber` fits both. On One Saber maps the cross-validated residual SD is 0.1325, against 0.1540 when they
  are extrapolated from the Standard-only model.
- **PP curve.** One Saber uses the exponent γ / β (`--mode-gamma OneSaber=1.0998`). PP per unit of skill then matches Standard
  maps. PP at the map's predicted accuracy does not change.
- **"Where accuracy is lost"** uses the same β, from the profile export, when it anchors accuracy to skill.

| | v4 | **v5** |
|---|---|---|
| per-map slope of log PP on player skill, median: Standard / One Saber | 0.594 / 0.537 | **0.593 / 0.578** |
| One Saber / Standard median PP at the same stars, weaker third of players: < 4★ / 4–6★ / 6–8★ / 8–10★ / 10–13★ | 1.36 / 1.21 / 1.16 / 1.09 / 1.07 | **1.28 / 1.10 / 1.08 / 1.04 / 1.00** |
| Megametric mean: Standard / One Saber (68 maps) | 0.189 / 0.296 | **0.190 / 0.257** |
| *The Spoils*: stars, Megametric | 8.15, 0.905 | **7.56, 0.766** |
| *The Spoils*: median PP of the weaker / middle / stronger third, top score | 265 / 332 / 432, 615 | **235 / 300 / 402, 596** |
| Standard stars change: median (p1 … p99) | — | **+0.003 (−0.11 … +0.07)** |
| top players' PP | 23 314, 22 005, 21 948 | **23 282, 22 000, 21 906** |

What v5 fixes and what it leaves:
- **One Saber as a whole is mostly fixed.** The gap in PP-vs-skill slope shrinks from 0.057 to 0.015. The rest comes from pass
  PP, which does not depend on accuracy.
- **Hard One Saber maps gain stars** (*Ashed Wings* 14.2 → 14.9, *lustre* 14.5 → 15.1): trained with One Saber data, the model
  predicts lower accuracy on them. **Easy ones lose stars** (*Bad Liar* 7.7 → 7.1).
- **The Spoils is only partly fixed.** Its Megametric drops from 0.905 to 0.766, still above the One Saber mean (0.257) and the
  ML rating's 0.486. Its own slope is much flatter than One Saber's average. Map features explain only 23 % of the variation in
  slopes between maps, and they predict −0.92 for *The Spoils*. Fitting each map's slope to its own scores would be a
  score-informed correction, which these ratings rule out.

### eBPM instead of swing speed? Tested, not adopted (`a26_ebpm.py`)

In the analyzer the two measure nearly the same thing:
- `eBPM = 30 × SwingFrequency`, where SwingFrequency is 1 / seconds since the same hand's last swing, doubled on a reset.
- `SwingSpeed = SwingFrequency × distanceDiff`, where `distanceDiff = 1 + hitDistance / (hitDistance + 2.668)`. Its p10 / p50 / p90
  are 1.08 / 1.18 / 1.27.

So swing speed is eBPM / 30 plus a hit-distance term, and the two correlate at 0.986 on log scale per swing. Each model that
uses swing speed was refitted with eBPM in its place.

| model | swing speed (today) | eBPM | |
|---|---|---|---|
| acc model, all features, CV R² | 0.9672 | 0.9671 (swing-speed features dropped) | tie; the model already has eBPM as `frequency_*` |
| acc model, + peak sustained eBPM (analyzer, 4-swing window) | | 0.9673 | adds nothing |
| acc model, a single speed parameter (p90) on its own / with pass, tech, ln swings | 0.727 / 0.901 | 0.684 / 0.900 | swing speed slightly better |
| "where accuracy is lost": held-out deviance explained | 0.3829 | 0.3837 | tie |
| … map R² at skill 2.5 / "where" Spearman, top / mid replays | 0.906 / 0.726 / 0.600 | 0.904 / 0.730 / 0.604 | tie |
| pass v2: R² vs attempts / maps off by > 1, > 2 logits | **0.9166 / 500, 47** | 0.9125 / 532, 58 | swing speed better |

More pass variants (speed part `eBPM × distanceDiff^α`): α 0.5 gives 0.9150 and α 2 gives 0.9165, so today's α 1 is at the
optimum. Raw eBPM, counted without the reset doubling, is best with a reset term of ×2.0 (0.9134). So the analyzer's doubling is
right, and the hit-distance term still adds about 0.003.

Conclusion:
- **Ratings:** keep swing speed. For passing, how far the hand travels to the note matters on top of how often it swings (about
  30 fewer maps off by more than 1 logit). For the acc model the choice makes no difference.
- **"Where accuracy is lost": adopted.** eBPM fits just as well there because the model has its own hit-distance factor, and
  players know the value. The view's speed factor is now eBPM in bands 60 / 90 / 120 / 150 / 180 / 210 / 240 / 280 / 330 / 400. The
  separate "time since this hand's last swing" factor is dropped: it is raw eBPM under another name.
  - Refit: held-out deviance 0.3832 (was 0.3830); map R² 0.900 / 0.875 at skill 2.5 / 3.8 (was 0.904 / 0.875).
  - "Where" Spearman, best / mid-field replays: 0.731 / 0.596 (was 0.737 / 0.599).
  - Live on `algo-b-v5`. Older deployments keep their old labels until their profiles are re-exported.

## Known limitations

* Weights are fitted on today's ranked Standard and (since v5) One Saber pool; maps far outside it (gimmicks, extreme speeds,
  other characteristics) rely on extrapolation of a linear model. Predictions are clamped to 0.5 … 0.9995.
* Calibration and validation use all clean scores. Effort is the remaining confound: a map that players grind gets a better best
  score than one they try twice. The attempts data (REPORT §7) is the way to correct for it.
* The score correction makes ratings depend on scores. A newly ranked map starts at the algorithm's rating and moves halfway
  towards its score-implied difficulty at ~12 clean scores. Casual play still makes a map look harder, but τ 0.09 bounds how
  far one map can move: 90 % of maps move by less than ±0.15 log error.
* `LowNoteNerf` is still applied on top (production policy). The score-implied target already contains the farm-ability of short maps,
  so this is a deliberate double penalty you may want to revisit.
* The per-note graph endpoints still use the ONNX model.
* The bulk-insert dependency (`Z.EntityFramework.Extensions.EFCore`) was bumped 7.105.4 → 7.105.8.1 because the monthly trial of the old
  version had expired; nothing else in portaBLe depends on the version.
