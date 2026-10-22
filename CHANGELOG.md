# Changelog: portaBLeRebalancer

The `loloppe-rebalancer` branch of portaBLe, plus the rating changes in the RatingAPI and Analyzer submodules. The Acc AI
model comes from `beatsaber-replays-ai-2`.

The goal is ratings that never need scores at rating time. Scores, replays and fail attempts are used offline, to fit and
validate constants. The one exception is the acc reweighter (below), the only tool that changes ratings from scores.

## Reweighter fallback and guard margins; the real stars' drift

### Acc reweighter

- **Fallback when the guard rejects** (`FallbackStrengths`): if the point to store fails the balance guard at the real
  stars, the fit runs again at the lower strengths of the last pass, highest first, and the first that passes is stored
  instead of nothing (`acc-reweight.json`: `Fallbacks`). A change to the balance now weakens the reweight instead of
  dropping it.
  - Tested on Dev with the pass share slope tolerance at 0.0037 (2.0 failed by 0.0001): 1.6, 1.3 and 1.0 failed (the pass
    share drift doesn't shrink with the strength), 0.7 was stored: worth spread 0.0333 (0.0211 at 2.0, 0.0443 without
    reweighting), mean change 1.2%.
- **Guard margins:** how far the stored point is from failing each part of the guard (`acc-reweight.json`:
  `GuardMargins`), with a warning when within `GuardMarginWarning` (25%) of a tolerance. On Dev the pass share slope has
  0.0002 of its 0.004 left, so it warns.
- **Where the drift comes from:** measured with real stars only or real star ranges only, all of it is the stars'
  (each map's reference, its players' levels and their distance to it); the star ranges move no slope.
- **Frozen stars** (`FrozenStarsRounds`, off): phase 2 and the slope correction again with each map's stars frozen at
  the last fit's real ones (the targets from the start's worth), kept when the real stars' slopes end closer.
  - Tried on Dev (2 rounds, the drift +0.0038 → −0.0021 → +0.0008): slopes within 0.0008 of the start's, least guard
    margin 79% of its tolerance (was 5%), worth spread 0.0211 → 0.0207. But by moving maps that had no gap: mean change
    3.3% → 5.1%, 614 maps at ×1.085 (170) and 283 at ×0.922 (53), low star maps worth ±0.015 before taken to the bounds
    (Teo Normal ×1.009 → ×1.085, Introduction Hard ×0.996 → ×0.923), 2cf6391 ×1.056 → ×1.085, Godspeed ExpertPlus worth
    +0.013 → +0.038. So it's off.
- **Drift compensation** (`DriftCompensationRounds`, off): phase 2 and the slope correction again at the base stars,
  aimed at the start's slopes minus the real stars' drift (+0.0038 pass share, +0.0023 tech, −0.0020 predicted acc on
  Dev), kept only when the real stars' slopes end closer to the start's.
  - Tried on Dev: the correction's terms grew ±0.0015 → +0.04 to +0.07, the worth spread at the base stars 0.0129 → 0.0495
    (0.0443 without reweighting) and the real stars' slopes overshot about 3× (pass share +0.0038 → −0.0079, tech
    +0.0023 → −0.0098 from the start's): the terms move maps' stars, so the drift grows with them. Not kept, so it's off.

## Worth without lower bound, reweighter slope correction and worth limiter, TechWeight 0.1612

### Worth (`BalanceTuner.WorthMeasures.PlayerAdjusted`)

- **No lower bound** (`PlayerAdjustedBelow` 2 → ∞): a player's scores on maps far under their level count too. Above the
  level stays bounded (`InRangeMargin`).
  - Why: a map more than 2 stars under a player's level counted none of their scores, so high paying scores on maps easy
    for the player went unseen (2cf6391, 9.8 stars: 497 of its 875 scores left out, most of the good ones, −25%).
  - Impact (Dev, Python approximation): 2.4× the scores, 3945 maps with a worth instead of 3506, worth spread 0.061 →
    0.047, 2cf6391 −17% → +4%, 3-5 star maps measured only by low level players −20 to −33% → about 0.
- **Distance term:** `log pp = skill + generosity + distance(map stars − player level)`, per `PlayerAdjustedDistanceBin`
  (0.5 stars), so a map's generosity doesn't depend on how far under its players it is. Maps 1.5-2.5 stars under a player
  paid them ~4% more than at their level, 8 under ~11% less (Dev).
- **Faster fit:** each group's and item's scores are bucketed once, and the alternating means run in parallel, with the
  same sums in the same order.

### pp formula (RatingAPI submodule)

- **`TechWeight` 0.124 → 0.1612, `PpMultiplier` 1.0597 → 1.0369** (balance tuner, with the new worth, without the acc
  reweighter): tech share slope −0.0148 → −0.0011, the most tech heavy 20% −1.0% → −0.3%, the top 1000's pp from tech
  7.0% → 9.6%. `PpMultiplier` keeps the top 1000's average.
- Each map's ratings are prepared once (stars, curve at 96%) for faster scoring.

### Acc reweighter

- **Slope correction** (`SlopeCorrection`): after phase 2, every map also gets a term for its pass share, tech share and
  predicted acc (relative to its star range, over its acc share), sized by a Newton search until those three slopes are
  back at the start's (within 0.0005).
  - Why: correcting single maps moves the slopes by itself, steadily with the strength of the first pass (pass share slope
    +0.006 → +0.011), and neither the targets nor the multipliers explained it. Correcting the pass share alone took the
    tech slope −0.0011 → +0.008, past the guard.
- **Guard `PlayerAdjustedGuardTolerance` / `GapTolerance` 0.0065 / 0.0125 → 0.004 / 0.0075:** with the new worth, the
  measure's noise is 0.0004 (pass slope) to 0.0020 (OneSaber gap), so the old tolerances held nothing back. 0.004 / 0.0075
  is about 4× the noisiest; 0.002 / 0.004 kept nothing.
- **Worth limiter** (`WorthLimit` 0.03, `WorthLimitRounds` 8, `WorthResponse` 0.6): the reweight may not make a map an
  outlier. A map's worth may not end above max(its worth without reweighting, +0.03), nor below min(it, −0.03).
  - Targets are clamped to that range before the passes, so a map already past it isn't pushed further. (On Dev it changed
    nothing: no map past ±0.03 had a target further out.)
  - Last, on the kept multipliers: a map past its limit by its own multiplier has it cut by the part of its worth change it
    overshot (in log). One whose multiplier is 1 or the other way, moved there by the maps around it (worth is relative),
    is moved against the overshoot (over `WorthResponse`, the median map's worth change per multiplier change). The other
    maps of its star range take back the mean, and it's measured again.
  - `acc-reweight.json` has each map's `WorthBefore`, `WorthAfter` and `Limited`, and `MapsLimited`.
  - Why: maps on target were buffed to the cap by the star range mean and the slope correction, and the most pass heavy
    13+ star maps went past their neighbours, their worth moving 1.3× their multiplier (the median map 0.6×): Godspeed
    ExpertPlus (pass 17) +0.010 → +0.107 at ×1.072, 13.72 → 14.86 stars. 66 maps already worth more than +0.03 were
    buffed. Cutting only a map's own multiplier left 98 maps past their limit, 53 of them moved by the maps around them
    (Calamitous Demise ExpertPlus, ×1.000: +0.054 → +0.071).
  - Impact (Dev, against the database without the reweighter): 303 maps past the limit after the slope correction, 334
    cut, 34 left past it by 0.0012 on average (at most 0.007), 8 of them by the maps around them. Godspeed ExpertPlus
    ×1.003, 13.77 stars, worth +0.029; Calamitous Demise ExpertPlus ×0.977, worth +0.033. Worth spread 0.0298 → 0.0277,
    mean change 3.1% → 3.0%.
- **Slope correction in every candidate:** the Jacobian of the three slopes on the correction's terms is measured once,
  at the start, and every candidate of every pass gets one Newton step of it before it's measured. The passes and phase 1
  choose among corrected points: corrected after phase 2 only, the correction raised the worth spread 0.0248 → 0.0298,
  which the choice didn't see. The search after phase 2 starts from the start's Jacobian.
- **Each map's own response** (`Responses`, `ResponseNoise` 0.02, `ResponseRange` 3): the direction divides by the acc
  share times how much more or less than other maps of that share the map's worth has moved with its multiplier so far,
  shrunk towards 1 when the map moved little. The limiter's opposite step uses it too (`WorthResponse` 0.6 removed). Before,
  the median map moved 0.6× its multiplier, the 90th percentile 0.98×, Godspeed ExpertPlus 1.3×. At the base stars
  (below) maps respond alike (p10 0.96, p90 1.05 of the median), so it changes little there.
- **Fit at the base stars** (`FitAtBaseStars`): during the passes and the slope correction each map keeps its stars and
  star range without reweighting, so its reference and its players' distance to it don't move with its multiplier. The
  limiter and the stored balance use the real stars. At the real stars the one-step slope correction overshot (mean change
  0.8% at strength 0.1, tech slope −0.012), nearly every candidate failed the guard and the held-out spread only reached
  0.0385 (0.0203 at the base stars).
- **`Passes` 6 → 3, `Strengths` up to 2.0:** pass 1 took the largest strength, 1.3, with the spread still falling, and
  passes 2-6 took it 0.0260 → 0.0248. Now every split chooses pass 1 at 2.0 (held out 0.0211 at 1.6, 0.0202 at 2.0;
  passes 2-3 raise it), still the edge of the range.
- **Phase 1 on 3 splits** (`HeldOutSplits`): each (pass, strength) is judged by its average held-out spread, passing the
  guard on every split. `acc-reweight.json`: `HeldOut` has every split's points (`Split`).
- **Symmetric bounds:** `Applied` × ±log(1 / (1 − `MaxChange`)), ×0.922 to ×1.085 (was ×0.922 to ×1.072, tighter for
  buffs in log). 182 maps sat at ×1.072 and 14 at ×0.922, and those at ×1.072 weren't noise: median 634 scores (879 for
  the rest), worth −0.078 → −0.035, still under. Now 160 at ×1.085, 32 at ×0.922.
- **PlayerAdjusted only:** the Megametric75 strengths and guard tolerances are removed (`GuardTolerance` /
  `GapTolerance` are now 0.004 / 0.0075); with another `BalanceTuner.WorthMeasure` the reweighter does nothing.
- **Slope correction again at the real stars** (`RealStarsSlopeCorrection`, off): after the search at the base stars, the
  kept point is measured at the real stars and the search runs again there, from a Jacobian measured at that point. At
  the base stars the three slopes are at the start's, but stored (real stars) the pass share slope is +0.0077 (start
  +0.0039, the guard allowing 0.0079) and predicted acc −0.0021 (−0.0001).
  - Tried on Dev: it took them to +0.0044 and −0.0007, but the correction moves maps by their kind, not their own gap:
    worth spread 0.0211 → 0.0247, maps beyond ±0.05 112 → 152, 236 maps past the worth limit before the limiter (9
    without it), OneSaber gap +0.0019 → +0.0044. Pass heavy low tech maps with a high predicted acc were buffed to the
    cap: 2cf6391 (pass 14.3, tech 2.9, 97.95%) ×1.056 → ×1.085, 10.51 → 10.85 stars, worth −0.006 → +0.014 (−0.044
    without reweighting), which players found too high. So it's off.
- **The balance guard on what's stored:** the point to store, after the limiter and at the real stars, must pass the
  guard, else nothing changes (`acc-reweight.json`: `Rejected`). It used to be kept anyway, and with the fit at the base
  stars that was the only check at the real stars, so the guard was never applied to what was stored.
- **`Applied` sweep (Dev):** lowering it doesn't reduce the drift at the real stars, it raises it, and gives back the
  outlier gains. Every value picked pass 1 at strength 2.0. `Applied` stays 0.5.

  | `Applied` | Worth spread | Beyond ±0.05 | Mean change | Pass share slope (start +0.0039, guard 0.0079) | Guard |
  |---|---|---|---|---|---|
  | 0.5 | 0.0211 | 112 | 3.3% | +0.0077 | passes |
  | 0.45 | 0.0227 | 137 | 2.9% | +0.0080 | fails |
  | 0.4 | 0.0247 | 176 | 2.6% | +0.0082 | fails |
  | 0.35 | 0.0267 | 223 | 2.3% | +0.0082 | fails |

- **Result (Dev):** worth spread 0.0443 → 0.0211 (0.0129 at the base stars), mean change 3.3% on 3902 maps, multipliers
  0.922-1.085 (160 at ×1.085, 32 at ×0.922). The limiter cut 9 maps (303 before the rework); 1 left past its limit.
  Maps beyond ±0.05: 112 (251 before the rework). Godspeed ExpertPlus ×0.991, worth +0.013; 2cf6391 ×1.056, worth
  −0.006. Against the start: pass share slope +0.0039 → +0.0077 (+0.0048 before the rework; the guard allows 0.0079),
  acc −0.0011 → −0.0033, tech −0.0011 → +0.0012, predicted acc −0.0001 → −0.0021; gaps: pass share +0.0041 → +0.0083,
  tech share −0.0025 → +0.0011, predicted acc −0.0004 → −0.0037, OneSaber −0.0010 → +0.0019. Passes the guard. At the
  base stars the three corrected slopes are at the start's.

### Pipeline and pages

- **`FullPipeline` with the acc reweighter** starts from the raw acc ratings (keeping the last multipliers stored them in
  float, so each run started a float rounding away from the last: 23 maps 0.5-2.9% off between two runs on the same data)
  and re-scores only after it. About 3.5 minutes in Release.
- The reweighter measures a pass's candidate strengths at once (`CandidateParallelism`, ~150 MB each).
- **Leaderboard page:** the pp curve's y axis follows the lines in view (their values at the edges included), so a line
  doesn't leave the chart when zoomed.

## Per-modifier worth, reweighter fit, smooth per-map curves, Leaderboard page

### Worth per speed modifier (`BalanceTuner.WorthSpeeds`)

- **SS / FS / SF scores are each their own item** in the player-adjusted fit, at that modifier's stars, against nomod maps
  of those stars (`ModifiersRating.SSWorth` / `FSWorth` / `SFWorth`). `Leaderboard.Worth`, worth by player rank and the
  Megametric75 / InRange measures use nomod scores only. A score's speed is its first speed modifier, as for its pp.
  - Why: these scores (276k, 6.3%) counted in the map's own worth at its nomod stars while paid by the modifier's ratings.
  - Impact: nomod worth moved 0.26 points on average (spread 0.0342 → 0.0345; the 300 maps with the most speed play
    −0.08), so they weren't what made worth look off. With enough in-range scores: SS 174 maps (+1.2% on average), FS 433
    (+2.3%), SF 34 (−4.8%).
- **NF scores count in no worth.** There are none in the database, and they get 0 pp, which the fit already skipped.

### Acc reweighter

- **`Applied` is part of the fit:** each map's target is `Applied` (0.5) of the way from its worth to what its kind of map
  is worth (in log), the strengths are compared without the part of the gap a map keeps, and the kept point is stored as is
  (bounds `Applied` × ±15%, `AppliedStep` removed). Before, the full fit was scaled afterwards: every fraction from 0.5 to
  0.15 failed the guard and 0.10 was stored (mean change 0.43%, multipliers 0.984-1.014, worth spread 0.0615 → 0.056).
- **`PlayerAdjustedGuardTolerance` 0.0035 → 0.0065:** with partial targets nothing passed the old one (the pass slope
  settles at +0.011 from +0.005, the acc slope at −0.006 from −0.001).
- **Result:** mean change 2.3% on 3084 maps, multipliers 0.922-1.072, worth spread 0.0617 → 0.0346. Against the database
  without it (balance tuner): kinds of maps within ±0.8% instead of ±0.4%, the highest pass 5% −2.1% → −0.3%, pass share
  slope +0.006 → +0.015, predicted acc gap −0.006 → −0.014. The tuner keeps every constant on both.

### Per-map acc curves (RatingAPI submodule)

- **Smooth base for per-map curves** (`Curve.SmoothBaseValue`): the base table they scale is interpolated with a monotone
  cubic (Fritsch-Carlson) instead of linearly. With the linear table the slope jumped at every table point and fell inside
  each segment (the per-map ratio falls with acc), so per-map curves were bumpy above ~98.5% (99.3-99.55% at 97%
  predicted: 0.10, 0.07, 0.12 per 0.05%). Same values at the table points, up to 1.34% lower between them. The base curve
  stays linear.
- **Curve Explorer:** the per-map curve, slider tilt and acc curve power can be turned on and off on the page, next to the
  curve without them; the predicted acc slider moved under the chart.

### Leaderboard page

- **PP curve:** pp at every accuracy (0-100%) with the selected modifier's ratings and curve, its pass / acc / tech parts,
  the base curve's pp, the comparison leaderboard's and the scores; zoom and pan. It replaces the pp table.
- **Cover, difficulty switcher** (every leaderboard of the song, same `SongId`), **worth** of the selected speed modifier.
- **Speed modifier filters the scores:** only scores with SS / FS / SFS, all scores without one.
- **"N players out of M get pp from it above X% weight, with Y ranked plays minimum":** the weight as profiles show it
  (0.01%), default 0% (about the top 280 plays) and 75 ranked plays.
- Page links keep the selected database.

## Player-adjusted worth, high pass buff, tech weight, low stars

### Worth measured with the player held fixed (`BalanceTuner.WorthMeasure`)

- **New default worth: `PlayerAdjusted`.** `log pp = skill(player, half year) + generosity(map)`, fitted on each player's
  scores near their level (from 2★ under it to 0.5★ above; level = the 5th hardest map in their top 50), then compared
  with maps of the same stars and maps first played around the same time. How much a map pays the same players than their
  other maps at their level.
  - Why: Megametric75 weights each score by where it lands in the player's profile. On pass 14+ maps, players ranked
    3001-10000 (survivors at low acc, with few high plays) inflated it: worth ≈ 0 overall, but −20% for the top 100 and
    +31% for #2001-10000. It also overreacts to rating changes (a 0.1% acc shift on tech 7+ maps: −0.02 → +0.19).
  - A measure against same-star maps only (`InRange`, kept as a diagnostic) barely reacts: stars are what a 96% score
    pays, so a map that pays more is compared with higher-star maps (pass slope flat from `PassCalibration` 0.656 to 0.888).
  - Player-adjusted reacts in proportion: 3% more pp on the tech 8+ maps moved them +1.9%. Worth spread 0.46 → 0.059.
  - Skill per half year: with one skill per player, maps ranked in 2026 looked 2.8% more generous (their scores are set by
    players at their best). Map age: maps first played in 2022 were 3% more generous (grinded for longer).
- **`Leaderboard.Worth` is this measure** (`BalanceTuner.StoredWorth`, from `LeaderboardsRefresh.Refresh`); the previous
  one is kept as `Leaderboard.MegametricWorth`.
- **`Leaderboard.WorthRank20` … `WorthRank10000`:** pp / top pp by player rank (#1-20, #21-100, #101-1000, #1001-3000,
  #3001-10000) against maps of the same stars (`LeaderboardsRefresh.RefreshWorthByRank`).

### Acc rating (RatingAPI submodule)

- **Very high pass buff** (`AccRating.HIGH_PASS_BUFF*`): −0.55% effective acc from 14.5 pass, ramped in from 13.5, flat
  above. Pass 14+ maps were 4.1% under maps of the same stars on pp / top pp in every rank range (best scores 0.25-0.5%
  under the predicted acc, others 0.1-0.4% over). Superluminal 735 → 761, 4 → 15 of the 22 maps with a 700pp+ play.
- **Tech correction tried and rejected:** an acc correction above a tech knee overpaid tech maps at every size (the same
  as noted before in `AccRating.cs`).

### pp formula (RatingAPI submodule)

- **`TechWeight` 0.0891 → 0.124** from the player-adjusted worth: tech-heavy maps paid their players less than others of
  their stars (tech share slope −0.033, tech 8+ maps −2.7%). Tech pp is 2-5% of a score, so they gain 1-2%: slope −0.007,
  top 1000 rank correlation 0.991.
- **Low stars back up** (`LowStarCurveStart` 2 → 7, `LowStarCurveEnd` 5 → 10, `LowStarBonus` 0.0321 → 0.006 up to 9★
  instead of 4.49): top players' best scores at 3-8★ had fallen 3-6% under prod's against the 10-12★ plateau, most on acc
  players' 99% scores (ACC | Pandita #10 in prod, #45). Now 3★ 1.046, 5★ 1.060, 7★ 1.018 (prod 1.035, 1.085, 1.020),
  Pandita #7. 1-2★ maps get less bonus (+10% / +8.5% at 1 / 2★, was +17.6% / +11%) but still rise with the curve power.
- **`PpMultiplier` 1.0833 → 1.0597:** the top 1000's average at prod's (16395).

### Balance tools

- **Balance tuner keeps constants** whose target is within 0.01 of 0, or moves less than 0.01 over the range tried
  (`TargetTolerance`, `MinResponse`), and says why on the Balance Tuning page with the worth measure used. With the
  player-adjusted worth it had cut every pass rating 21% to move a +0.9% slope.
- **Acc reweighter on the player-adjusted worth:** evidence from the measure's own scores, strengths 0.1-1.3 (worth moves
  ~0.6x the pp, not 3-5x), guard tolerances 0.0035 / 0.0125 (between twice the measure's noise and Megametric75's). When
  `Applied` (0.5) of the fit fails the guard, the largest fraction under it that passes is stored (`AppliedStep` 0.05):
  a fraction of the fit can drift more than the whole fit (full fit pass slope +0.006, half +0.012). On Dev Tech: 0.15
  stored, 0.6% mean change.

### Pages and tools

- **Leaderboards:** one Worth column (player-adjusted, %), coloured at ±8%. **Leaderboard Statistics:** split into views
  (Overview, Worth by rank, Profile weights, Megametric, Map traits), all columns sortable, default sort Worth.
- **Ranking:** Top PP next to Total PP. **Map Outliers:** sorted by Outlier %. **Top Acc, Weighted / Unweighted
  Scores graphs:** SS, FS and SFS unticked by default. **Database Comparison:** nothing loads until a view is picked.
- **Pass Comparison page and `PassWindowCheck` removed** (only that page used it).
- **Top Scores caches of databases not in `databases.json` are deleted at startup**, with unfinished `.tmp` saves.
- **Databases and caches excluded from the project by wildcard** (`wwwroot\**\*.db`, `wwwroot\cache\**`).

## Row streams, pass windows, prod scale, acc reweighter

### Ratings (Analyzer submodule)

- **Arrow directions refitted on replays.** Single arrows were pulled all the way towards the path from the previous
  swing; players actually cut them close to the way they point and reach the next note with the arm. The pull is now
  0.15 of that on the same row and 0.3-0.85 on a row change (more for faster swings).
  - Why: on bottom row streams, every up and down arrow was bent into a ~50° diagonal, so they looked like constant wrist
    rotation and angle strain, and those maps had too much tech.
  - Against top players' cut directions (3.3M arrows on 3886 maps): mean error 15.9° → 10.2°, within 30° 85% → 96%.
    Rotation and angle strain follow the players' real wrist roll (controller roll in the replays) much better, R² 0.27 →
    0.50 and 0.52 → 0.64.
- **Pass windows 16-128 s → 4-32 s.** Long dense streams were overvalued (Ascension to Heaven Expert) and peak-driven
  speed maps undervalued (The Purple Dimension).
- **Density bonus capped** (`DENSITY_CAP` 2 → 1): sparse stretches still count less, dense ones no more than the
  reference.
- **Distance between swings counts more** (`DISTANCE_FALLOFF` 2.668 → 1.0). Maps with longer hit distances were worth
  less than others of their stars.
- **New `Ratings.HitDistance`:** mean distance between consecutive swings, used by the acc rating.

### Acc rating (RatingAPI submodule)

- **Movement and high pass corrections.** The Acc AI predicts the accuracy of the notes players cut, but misses and bad
  cuts cost more where the hands travel far or the pass is very high. The effective predicted acc is lowered:
  - by 3% per meter of mean hit distance above 0.62 m (capped at 0.8 m), on maps under 97-97.5% predicted acc;
  - by 0.214% per pass point above 14.52.
  - Fitted on the top 300 players' scores after removing each player's own level. The Purple Dimension's acc rating was
    too low for what players can get on it (−2.5% against the prediction).
- **Legacy `star_rating` removed from RatingAPI's response**, replaced by `stars` (below).
- **pp and stars moved into RatingAPI** (`RatingAPI/Pp`): `PpFormula`, `PpCalculator` (score pp, stars, curve lookup)
  and `ModifiersMap`, so the server doesn't need portaBLe. portaBLe's `ReplayUtils` only passes its database values.
- **Stars are what a 96% score is worth:** the final pp of a 96% score without modifiers, on the star scale. They used
  the base curve and left out the low star bonus; now they use the map's own curve (slider tilt included) and include
  the bonus (solved as a fixed point, since the bonus depends on stars). SS / FS / SFS stars use their own curves.
  The star and low star bonus constants were fitted before this and need refitting.

### Ratings on prod's scale

- **Pass, tech, acc and stars on prod's scale**, fitted on the quantiles of prod's ranked ratings. The stored values are
  the ones the formulas use (no display curves):
  - pass: `PASS_CALIBRATION_FACTOR` 0.5195 → 0.7721 (a linear ×1.4862); the pass pp formula (`PpFormula.PassInner`,
    `PassTopKnee`), the tech pass factor (`Ratings.TechPassBase`) and the high pass acc correction were converted so
    the same map gets the same pp;
  - tech: `BALANCED_TECH_SCALER` × (average swing tech × pass factor)^0.8015;
  - acc: `ACC_SCALE` × Curve(...)^−0.8222 (`ACC_SCALE` 15.1 → 13.5382);
  - stars: `StarScale` × (pp at 96% / `StarDivisor`)^0.9039 (`StarDivisor` 49.13 → 45.25).
  - Medians and 99th percentiles, now vs prod: stars 6.86 / 13.34 (6.97 / 13.19), pass 4.47 / 13.03 (4.44 / 13.39), acc
    8.54 / 13.13 (8.68 / 12.63), tech 3.88 / 11.88 (3.75 / 11.23).
- **Acc and tech pp take the ratings to a power** (`PpFormula.AccRatingPower` 1.2163, `TechRatingPower` 1.2477, the
  inverses of the ratings' powers; `AccWeight` 34 → 21.584, `TechWeight` 0.0938): the same pp as before.
  - Why: prod's tech and acc ratings are flatter at the top than acc and tech pp need. With pp proportional to them, a
    star range's acc-heavy maps lost pp against its pass-heavy ones (pass share slope +0.03 → +0.11, tech share slope
    +0.01 → −0.15), and no `PassScale` / `TechWeight` combination undid it.
- **Speed modifier multipliers** multiply the ratings directly, as in prod.
- **pp level on prod's scale:** `PpMultiplier` 1.050 → 1.146 (top 1000 players within 0.4% of prod on average).
- **Low star bonus** refitted on the new stars (0.0642 × (4.49 − stars)^1.36), so the same maps get the same bonus.
- **Inflation removed:** pp was 650 × ((pass + acc + tech) / 650)^1.3; it is now linear in the sum. Most scores' sum is
  under 650, so their pp rose (×1.44 under 3★, ×1.26 at 7–9★, ×1.15 at 13★+; the highest score 949 → 897) and the
  leaderboard compressed: harder maps count relatively less. Stars are kept (`StarDivisor` 45.25 → 83.69, `StarExponent`
  0.9039 → 1.1751: exactly the same above 4.49★, +0.04 on average under it) and `PpMultiplier` 1.146 → 1.0786 keeps the
  top 1000 players' totals on average (#1 24586 → 22380, #1000 14571 → 14950). The `InflateExponent` sweep is gone
  from `BalanceTuner.RunPpFormula`.
- **Balance after the inflation removal** (measured with the balance tuner's metrics on every score, the database rated
  with today's formula):
  - new `AccCurvePower` 0.9: acc pp follows curve(96%) × (curve(acc) / curve(96%))^0.9, so stars don't move and acc
    pp grows a little less above 96%. Pass-heavy maps were worth more than others of their stars, and pp at the predicted
    acc was higher on 1–3★ maps than on 3–5★ maps;
  - `PassTopKnee` 13.375 → 10.4 (the highest pass 5% of maps +0.133 → +0.018), `TechWeight` 0.0938 → 0.0891,
    `LowStarBonus` 0.0642 → 0.0321, `ONE_SABER_NERF` 0.548 → 0.49 (Analyzer, needs re-rating), `PpMultiplier` → 1.0833
    (top 1000 kept on average);
  - | | Before | After |
    |---|---|---|
    | Worth spread | 0.460 | 0.455 |
    | Pass share slope / gap | +0.036 / +0.019 | +0.000 / +0.000 |
    | Tech share slope / gap | −0.007 / −0.002 | +0.003 / −0.005 |
    | Predicted acc slope / gap | −0.007 / −0.014 | −0.009 / −0.021 |
    | OneSaber gap | −0.009 | −0.003 |
    | pp at the predicted acc (median per range), 1–5★ | 578, 548, 525, 522 | 466, 464, 473, 490 |
    | #1 / #1000 | 22379 / 14953 | 21890 / 15052 |
  - **Low-star maps lifted back part of the way:** the acc curve power is 1.0 up to 2★, then goes linearly to 0.9 at 5★
    (`LowStarCurvePower`, `LowStarCurveStart` / `LowStarCurveEnd`). The 0.9 cut top players' scores most on easy maps:
    the top 100's median pp at 1–2 … 4–5★ went 398, 404, 443, 478 → 325, 362, 412, 456; now 362, 385, 424, 460. pp at the
    predicted acc 536, 502, 493, 496; worth spread 0.4597, OneSaber gap +0.025, predicted acc gap −0.027; stars unchanged.

### pp and balance

- **One saber nerf** 0.53 → 0.548 (OneSaber gap +0.026 → −0.006 once the stars were rescaled).
- **TechWeight** 0.2193 → 0.1348 through each step above (dense low-tech maps lost pass, high-movement maps gained acc,
  tech ratings ×1.102 on prod's scale), then 0.0938 with `TechRatingPower` (same tech pp).
- **Balance vs the start of this round:**

  | | Before | After |
  |---|---|---|
  | Worth spread (before the acc reweighter) | 0.497 | 0.479 |
  | Pass share slope / gap | 0.049 / 0.052 | 0.034 / 0.023 |
  | Tech share slope / gap | 0.038 / 0.033 | 0.013 / 0.010 |
  | Predicted acc gap | +0.010 | −0.009 |
  | OneSaber gap | +0.026 | −0.006 |

### Acc reweighter

- **New `Refresh/Tools/AccReweighter.cs`, the only tool that changes ratings from scores.** It moves each map's acc
  rating so the map is worth about as much as other maps of its kind, without moving kinds of maps against each other.
  - Every candidate is re-scored in memory, since one map's change moves every player's weights.
  - Maps with few scores move less; maps without a worth (newly ranked) keep ×1.
  - How far it goes is chosen on held-out players: worth spread on the half it didn't fit on went 0.49 → 0.24. About half
    of each map's deviation is reproducible across players, the rest is score noise and is left alone.
  - The result is only kept if the balance tuner's slopes and gaps don't get worse. Multipliers average 1 within each star
    range and stay within ±15% (4.1% on average, 3585 maps).
  - **Halved** (`Applied` 0.5: half of the fitted change, in log, so ±7.5% at most): players found the full fit too
    strong and the database better without it.
  - **Worth measured against a smooth reference:** a map's Megametric75 is compared with what maps of its stars average,
    a local linear fit on stars (Gaussian kernel, `BalanceTuner.WorthBandwidth` 0.25★), not with its own range's
    average. With the step, maps at the top of a range looked overworth (+0.09 in the top fifth) and maps at the bottom
    underworth (−0.07), and crossing a boundary changed a map's reference at once: the reweighter emptied the outer tenth
    of each range by ~20%. Now within ±0.02 by position. Interpolating between the ranges' averages (tried first)
    underrated whole ranges (2–3★ −0.11, from the 5 maps of 1–2★) and moved the balance slopes. Same in
    `LeaderboardsRefresh`'s Worth.
  - **Guard fix:** the held-out search picked the lowest held-out spread overall. With a balanced start that was pass 2,
    where every point of the full run failed the balance guard, so nothing was kept. It now picks the lowest held-out
    spread among the points that pass the guard (the lowest overall only when none do), and warns when nothing is kept.
    `acc-reweight.json` also records the stored (halved) multipliers (`Applied`).
  - Examples: Ascension to Heaven Expert +1.32 → −0.10, Godspeed ExpertPlus +0.83 → +0.06.
  - Balance after it: pass share slope / gap 0.016 / 0.024, tech share slope / gap −0.020 / 0.004, predicted acc gap
    +0.002, OneSaber gap +0.012.
- **Stored in `Leaderboard.AccReweight`** and kept when ratings are recomputed (`RatingsRefresh.Overwrite` / `Refresh`).
  Report in `wwwroot/acc-reweight.json`.
- **`FullPipeline(dbContext, env, autoreweight, balancer, keepAccReweight)`** runs it (and the re-score after it) when
  `autoreweight` is true. `keepAccReweight` no longer has a default.
- **The old `ScoresRefresh.Autoreweight` / `Autoreweight3` are removed.**

### Pages and tools

- **Worth on the Leaderboards and Leaderboard pages:** `Leaderboard.Worth` (Megametric75 relative to what maps of its
  stars average, +0.27 = 27% more than average), computed by `LeaderboardsRefresh`.
- **Leaderboards page redesigned:**
  - stars, pass, acc, tech and worth side by side, with sortable column headers;
  - difficulty badges, mapper under the name, characteristic tag;
  - worth tinted when ±0.25 or more;
  - search by song or mapper name;
  - 25 maps per page, and the selected database kept in every link.
- **Balance tuner:** works on internal ratings, can classify maps by other ratings than the ones scored (used by the
  reweighter), and can measure worth on a subset of players.
- **New `pp-calculation-example.html`:** a worked example of how a score's pp and a map's stars are calculated.
- **`ParityCheck` exports** each swing's tech parts and the players' controller roll (research).

## Parity, pass refit, pp retune

### Ratings (Analyzer submodule)

- **Parity rewritten.** The predictor now finds the cheapest path through the whole map, with costs fitted on top players'
  replays. It matches players on 99.75% of swings.
- **Pass refitted on about 11M clear / fail attempts.** Stress weighs more, the stream bonus is removed, and the difficulty
  windows are longer. Crouch walls get a ×2.0 buff (was ×1.2).
- **Tech** counts only angle strain and rotation. Repositioning and resets no longer count, so reset maps stop getting top
  tech ratings.
- **New constants:** pass calibration 0.664, one-saber nerf 0.28, tech scaler 17.6.

### pp and balance

- **pp formula retuned for the new Acc AI model:** PassScale 15.2 → 12.9, TechWeight 0.694 → 0.63, PpMultiplier 1.075 → 1.042.
  - Why: pp had inflated with the new model, and pass pp was a bit too high.
- **Hard map correction of the acc rating.** Below 96.75% predicted acc, the acc rating uses a predicted acc lowered by 1.24×
  the distance below 96.75% (at most 2.17%, reached at 95%). The stored predicted acc and the curves don't change.
  - Why: top players' acc fell further behind the Acc AI's prediction on the hardest maps (−1.1% at 96%, −1.5% at 95.4%,
    after removing each player's own level), so their pp stopped rising past about 12 stars.
  - Simulated on every score: top 100 players' median pp by star band now keeps rising past 11 stars (586, 601, 604, 614
    instead of 578, 565, 571); 208 of 3886 Standard maps change; top 1000 rank correlation 0.98.

- **Low star pp bonus** (`PpFormula.LowStarBonus` 0.06): maps under 4 stars pay more, ×(1 + 0.06 × (4 − stars)^1.5):
  +45% at 1 star, +17% at 2, +6% at 3, nothing from 4. Stars don't change.
  - Why: top players farm the easiest maps to 99%+ but stars are measured at 96%, so pp fell off steeply under 4 stars.
    A first try, a soft floor on the acc rating (removed), raised the stars of those maps too and squeezed
    them into 2.5-4 stars, where they paid more than real 3-4 star maps.
  - Simulated on the Unweighted Scores Graph (top 1000 players, 100+ ranked plays): the rolling average from 1 to 4 stars
    is 243, 274, 278, 293, 301, 313, 329 (was 194, 229, 244, 270, 288, 310, 329), unchanged from 4 stars. #1 and #100
    player totals unchanged, #1000 +6pp.

- **Per-map acc curve table updated** (`RatingAPI/Controllers/Curve.cs`, `CurveSettings.Table`): new A, C, K, W, K2 and P2
  from 95.5% to 99.5% predicted acc. The 96-97.5% rows ease back to the default shape by 97.5-98.5% instead of 97%.

### Performance

- **New `ScoresPlayersRefresh`:** recalculates scores and players in a single pass, about 3× faster. `RefreshEverything`
  uses it.
- **Faster `RatingsRefresh.Overwrite`:**
  - only the stored modifiers are rated;
  - RatingAPI loads only the difficulty being rated;
  - inference batches are larger (4 → 512).
- **Quieter logs:** the database columns are checked before being added, so `ALTER TABLE` errors are no longer printed.

### Tools and cleanup

- **NJS on the Leaderboard page**, under the predicted acc (with a speed modifier: the NJS the player sees). Stored in
  `Leaderboard.Njs`: `RatingsRefresh.Overwrite` saves it, `RatingsRefresh.RefreshNjs` fills it from the maps folder without
  rating again. The column is added to every database at startup.
- **New Top Scores page:** every score in one list, best pp first, with stars, acc, acc vs the Acc AI's prediction,
  pp split, weight and map rank. Sorts by pp, weighted pp, acc / pass / tech pp, acc, acc vs AI, stars or date, and
  filters by mode, stars, player rank, player, map and speed modifier. Never queries the scores table: it reads a
  compact cache file per database (`wwwroot/cache/<db>.topscores.bin`, about 56 MB for 4.4M scores, `Services/TopScoresCache.cs`)
  that loads in about a second. The file is matched to its database by content, so the one built locally works on the
  server: `UploadDatabaseAsync` uploads it next to the database and `DownloadAllDatabasesAsync` downloads it. When it's
  missing or outdated it's built in the background while the page says so. `RefreshEverything` builds it at the end.
- **New Top Acc per Map graph** (Graphs menu): the top X accuracies of every map against stars, every score drawn as its own
  point, with mode, player rank, date and speed modifier filters and a rolling average. A second database can be picked to
  draw its rolling average (same filters) on the same graph. Reads the Top Scores cache.
- **Speed modifier filters on the Weighted and Unweighted Scores Graph pages.** Tick which scores to include: no speed
  modifier, SS, FS and/or SFS. Modded scores are placed on the X axis with their modifier's stars and ratings (an SFS
  score uses the SFS stars).
- **New research tools in `Refresh/Tools/`:**
  - `ParityCheck`: Analyzer parity vs replays.
  - `RatingFitExport`: swings, scores and walls, used to fit the formulas and the Acc AI's player skills.
- **The balance tools moved to the same folder.**
- **Repo cleanup:**
  - `Program.cs` is organized into sections.
  - New README and `.gitignore`.
  - Old files moved to `_archive/`.

## "New model + auto-rebalancer"

- **New Acc AI model in RatingAPI.** It has 53 note-type inputs. Acc offset changed from 0.0022 to 0.0019.
- **Balance tuner and Balance Tuning page.** They suggest constants so that no kind of map is worth more than others of the
  same stars.
- **The pp formula moved to `PpFormula.cs`** and gained a `PpMultiplier`.
- **New model comparison export:** prod vs the current database.
- **Fixes:**
  - Database Comparison failed on NaN ratings.
  - The Weighted Scores Graph showed unweighted pp.
  - RatingAPI's padding check used the same note for every prediction in a batch.
