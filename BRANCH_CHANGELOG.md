# Branch Changelog

**Range:** `5890677` ("New model + auto-rebalancer", included) through `372eb8c` on `loloppe-rebalancer` and the work
after it (player-adjusted worth, tech weight, low stars, per-modifier worth, the reweighter fit, smooth per-map curves:
see `CHANGELOG.md`), plus the matching commits in the submodules:
RatingAPI `3955df3 → 4f5a2c9` and later, and Analyzer `0d47de6 → 43229f6` (10 commits). The Parser submodule is unchanged.

## Overview

The branch rebuilds BeatLeader's rating system in portaBLe so that every rating change is fitted on, or checked against,
real data: top players' replays, all ranked scores and fail attempts. The most important results:

- **Analyzer:** the parity predictor and the swing directions now follow what top players actually do. The pass rating
  uses time-based windows, and tech only counts wrist work.
- **Acc rating:** a new Acc AI model, per-map acc curves, and corrections for the maps where players fall short of the
  Acc AI's prediction (the hardest maps, maps where the hands travel far, maps with very high pass).
- **pp formula:** moved into one tunable class in RatingAPI, made linear (no inflation power) and re-balanced so no kind of
  map is systematically worth more than others of the same stars. Stars are what a 96% score is worth. Ratings, stars and
  players' pp are on prod's scale.
- **Tools:** a balance tuner, and an acc reweighter that corrects individual maps from scores, under balance guards. Both
  measure a map's worth with the player held fixed (how much it pays the same players than their other maps at their
  level), not with Megametric75.
- **Website:** new Top Scores and Top Acc pages, a curve explorer, a worth column and worth by player rank, a redesigned
  Leaderboards page, a Leaderboard page with a full pp curve, Leaderboard Statistics in views, and much faster graph pages.

Where the reasoning below comes from the code comments, commit messages or the project's own `CHANGELOG.md`, it's stated
as fact. Where it isn't documented, it's marked as inferred or unclear.

---

## Analyzer (pass and tech ratings)

### Parity predicted with a cost model fitted on replays
- **What:** `ParityPredictor` finds the cheapest forehand / backhand path through each hand's swings. The costs are
  comfort (distance from the parity's natural direction), rotation from the previous swing, a time-dependent reset cost
  (cheap after a long gap, very expensive on fast patterns), a pair table for which arrow pairs players reset on, a
  lighter reset cost after bombs, and a small backhand cost. The old bomb "parity flip" heuristic is removed. A multi-note
  swing that contains an arrow now follows the arrow. (`31c2607`, refined through `62c59de`)
- **Why:** confirmed. Weights fitted on top players' replays (198 maps) and the pair table on 3.88M ranked swings. On maps
  the fit didn't see, 99.75% of swings match the players (96.1% on reset-heavy maps). Multi-note swings against their arrow
  matched players only 30% of the time.
- **Impact:** resets and their pass cost are predicted much more like real play. A variant fitted harder on wrist-roll
  maps was rejected because it missed resets on speed reset maps.

### Swing directions follow how players cut
- **What:**
  - **Single dots:** pulled towards their parity's natural direction, plus a per-position arm-arc offset that changes
    entry / exit points but not wrist work (`DotDirections`).
  - **Single arrows:** they used to be bent fully towards the path from the previous swing. Now they keep 0.15 of that
    bend on the same row and 0.3–0.85 on a row change, more for faster swings (`EntryExit.NormalizeAngle`).
- **Why:** confirmed, fitted on players' recorded cut directions. Dots: mean error 23.8° → 12°. Arrows (3.3M on 3886
  maps): 15.9° → 10.2°, and rotation / angle strain follow the players' real wrist roll much better (R² 0.27 → 0.50,
  0.52 → 0.64). The full bend turned bottom-row streams into constant ~50° diagonals.
- **Impact:** tech no longer inflates on bottom-row streams and other same-row patterns.

### Pass windows in seconds (4–32 s), with a capped density term
- **What:**
  - **Windows:** pass is the hardest stretch over 4, 8, 16 and 32-second windows, instead of 8–128-swing windows. A map
    shorter than a window counts the rest as rest.
  - **Density:** each window's average is weighted by swing density (power 0.25), capped at the reference density.
  - **Distance:** `DISTANCE_FALLOFF` 2.668 → 1.0, so the distance between swings matters.
  - **Crouch walls:** ×2.0 instead of ×1.2.
  - **Low-note nerf:** removed (`5489756`, RatingAPI `8c0d7bc`). It multiplied the acc rating by 0.6–1.0 on maps under
    200 notes.
- **Why:** confirmed, except where noted.
  - Windows in swings let a few seconds of dense burst set very fast maps' pass. Seconds fitted fail difficulty better
    (out-of-fold R² 0.628 → 0.664).
  - 16–128 s was used for a while, but it overvalued long dense streams (Ascension to Heaven Expert) and undervalued
    peak-driven speed maps (The Purple Dimension); 4–32 s balanced better.
  - The uncapped density bonus overvalued long dense streams too.
  - Maps with longer hit distances were worth less than others of their stars.
  - Crouch walls fitted ~2.0 against attempts.
  - The low-note nerf was an earlier fix for low-star maps with few notes being worth too much. It's no longer needed
    with the rest of the changes (confirmed by the author).
- **Impact:** pass tracks fail difficulty and balance better. Shorter windows make a few seconds of burst count somewhat
  more again (a known trade-off).

### Tech counts wrist work only, on prod's scale
- **What:** swing tech = angle strain + rotation; repositioning moved to pass. Tech = scaler × (average swing tech × pass
  factor)^0.8015.
- **Why:** confirmed. Without repositioning, tech explained ~15% more of the acc players lose beyond the Acc AI's
  prediction. The power gives prod's tech distribution, whose top end is flatter.
- **Impact:** reset- and movement-heavy maps no longer get top tech ratings.

### Reliability fixes
- **Deterministic note and swing ordering** (`dc5ff03`): ties in time are sorted by type, position and direction, so the
  same map always rates the same.
- **False resets** (`cf64d39`): resets faster than the map's peak sustained EBPM aren't doubled. Fixes inflated EBPM, the
  commit cites Yggdrasil.
- **New `Ratings.HitDistance`:** mean distance between consecutive swings, used by the acc rating.

---

## Acc AI and acc rating (RatingAPI)

### New Acc AI model and inference changes
- **What:**
  - **First retrain** (`66ed91f`): 53 inputs per note instead of 49 (arc head / tail and chain head note types). It's
    trained on the top 50 replays of the ranked AccSaber, ScoreSaber and BeatLeader maps, with player-adjusted targets:
    each replay's lost accuracy is scaled to a reference player, using skills fitted on all ranked scores.
  - **Deployed model** (`997b38e`, "mirrored"; training run `20261017-222131`): the same setup, plus training on the
    horizontal mirror of every map (with a flag for which hand), and left-handed replays mirrored back so their notes
    line up.
  - **Inference:** the previous model is kept as `model_sleep_bl_old.onnx`, and the inference detects which layout a model
    expects from its input size.
  - **Speed modifiers:** the model sees 75% of the NJS change they cause.
  - **Padding:** the last window is padded instead of its notes being dropped.
- **Why:** confirmed in the training repository (`beatsaber-replays-ai-2/CHANGELOG.md`) and in code:
  - **Note types:** the model couldn't tell arc and chain notes from normal ones.
  - **Player-adjusted targets:** a map's training target depended on who played it, since grind maps get the very top
    players, which made some maps overweighted. With the first retrain, the outlier spread in portaBLe went 0.584 → 0.531.
  - **Mirror training:** map-level error on held-out test maps 0.1068 → 0.1015 points of acc, consistent over 5 seeds and a
    second split.
  - **Left-handed replays:** 4.8% of the replays had been mostly dropped note by note.
  - **NJS share:** the full change under-predicted SFS acc, none made SFS scores worth about 6.5% less than the same
    players' no-mod scores, and 75% balanced both.
  - **Rejected in training:** training on speed-modifier replays, hard unranked maps, and extra weight on hard maps all
    made the model worse.
- **Impact:** different predicted acc for every map. The deployed model's map-level error on 1037 held-out validation maps
  is 0.096 points of acc. The model still rates maps from the map alone; scores only shaped the training targets.

### Per-map acc curves
- **What:** each map gets its own acc curve from its predicted acc (`CurveSettings.Table`, by predicted acc). The curve
  is tilted on slider-heavy maps: higher above the predicted acc, lower below it. The acc rating reads the map's own
  curve (`30669d6`/`997b38e`). Curves are stored per leaderboard and per speed modifier, and can be regenerated without
  re-rating (`CurvesRefresh`).
- **Why:** confirmed.
  - 9–12★ maps paid too much for scores about +1% above the prediction (the 800pp+ plays).
  - True-acc and acc maps paid too much for tiny gains around 99%.
  - Slider-heavy maps were worth more than others of their stars (+0.48 for the top 3%) while their top scores sat as
    close to the prediction as elsewhere.
  - Simulated: scores above 800pp 246 → 31.
- **Smooth base** (RatingAPI `34f8be2`): per-map curves scale the base table interpolated with a monotone cubic
  (Fritsch-Carlson) instead of linearly. With the linear table the slope jumped at every table point and fell inside each
  segment (the per-map ratio falls with acc), so the curves were bumpy above ~98.5% (0.10, 0.07, 0.12 per 0.05% at
  99.3-99.55% for 97% predicted). Same values at the table points, up to 1.34% lower between them; the base curve stays
  linear.
- **Impact:** the 99%+ tail and the slider outliers are tamed. Several table iterations happened (`65204a7`, `79c19e8`,
  `a3535f7`); only the latest values remain.

### Acc rating corrections and scale
- **What:** the acc rating's effective predicted acc is lowered:
  - **Hard maps:** on maps under 96.75% predicted acc.
  - **Hand travel:** by 3% per meter of mean hit distance above 0.62 m, capped at 0.8 m, below 97–97.5% predicted acc.
  - **Very high pass:** by 0.214% per pass point above 14.52, and by 0.55% from 14.5 pass, ramped in from 13.5
    (`HIGH_PASS_BUFF`, RatingAPI `4f5a2c9`).

  Acc rating = 13.5382 × Curve(effective acc + 0.0019)^−0.8222. Previously it was 15.5 / Curve(predicted + 0.0022) ×
  low-note nerf.
- **Why:** confirmed, all fitted on top players' scores after removing each player's own level. Top players fell further
  behind the prediction on the hardest maps (their pp stopped rising past ~12★), and on maps where misses and bad cuts cost
  more. The Acc AI's training targets exclude misses.
  - Rejected: a tech term (tech pp already pays for it) and a hit-distance term on easy maps (their acc rating is too
    sensitive there).
  - The power gives prod's acc-rating distribution.
  - **High pass buff:** confirmed. Pass 14+ maps were 4.1% under maps of the same stars on pp / top pp in every player
    rank range; their best scores sit 0.25–0.5% under the predicted acc where other maps' are 0.1–0.4% over.
- **Impact:** The Purple Dimension and similar maps are no longer under-rated, without overvaluing Godspeed. With the
  buff, 15 of the 22 maps above 14 pass have a 700pp+ play (4 before).

### API changes
- **Removed:** the legacy `star_rating` (and `low_note_nerf`) fields of RatingAPI's response.
- **Added:** `stars` (from RatingAPI's own pp formula, below, without the acc reweighter's multipliers, which only
  portaBLe has), `slider_percentage`, `njs` and the hit distance.
- **pp and stars in RatingAPI** (`3484ca5`): `RatingAPI/Pp` holds `PpFormula`, `PpCalculator` (score pp, stars, curve
  lookup) and `ModifiersMap`, so a server needs RatingAPI only. portaBLe's `ReplayUtils` just passes its database values.
- **Faster rating:** an overload rates only the requested modifiers, only the requested difficulty is parsed, and ONNX
  batches went from 4 to 512.
- **Impact:** a breaking change for any other consumer of `star_rating` (`stars` replaces it, on the new scale).

---

## pp formula, stars and scale (portaBLe)

### pp formula centralized and retuned
- **What:** the pp formula moved from hard-coded `ReplayUtils` into `PpFormula`, with a `PpMultiplier` (`5890677`), and
  then into RatingAPI (`RatingAPI/Pp`, `263dc0b`). Net changes from the start:
  - **Pass pp:** `PassScale` 15.2 → 12.3, plus a top compression (each point above pass 10.4 counts half; 13.4 before the
    inflation was removed).
  - **Acc and tech pp:** take the rating to a power, acc^1.2163 and tech^1.2477.
  - **Acc curve power:** acc pp follows curve(96%) × (curve(acc) / curve(96%))^0.9, so it grows a little less above 96%
    while stars don't move. The power is 1.0 up to 2★ and goes linearly to 0.9 at 5★.
  - **Tech:** tech pp = e^(`TechAccExponent` × acc) × `TechWeight` × tech^`TechRatingPower`. The exponent on accuracy,
    `TechAccExponent`, went 1.9 → 3.5 (`e90b2bb`); the power on the tech rating, `TechRatingPower` (1.2477), is a separate
    and later change (below). `TechWeight` is 0.0891.
  - **No inflation:** pp is (pass + acc + tech) × `PpMultiplier`; it was 650 × (sum / 650)^1.3.
  - **Low-star bonus:** maps under ~4.5★ get up to about +18% (0.0321 × (4.49 − stars)^1.36; twice that before the
    inflation was removed).
  - **Stars:** what a 96% score is worth: 1.2131 × (final pp of a 96% score / `PpMultiplier` / 83.69)^1.1751, with the
    map's own curve (slider tilt included) and the low-star bonus (solved as a fixed point, since the bonus depends on
    stars). SS / FS / SFS stars use their own curves.
  - **`PpMultiplier`:** 1.0833.
  - **One saber nerf** (Analyzer `ONE_SABER_NERF`): 0.49.
- **Why:**
  - **Pass top compression:** maps with more pass than others of their stars were worth more above ~10★.
  - **Low-star bonus:** top players farm the easiest maps past 99% while stars are measured at 96%, so pp fell off steeply
    under 4★. An earlier soft floor on the acc rating was removed because it squeezed those maps into 2.5–4★.
  - **Rating powers:** with pp proportional to the prod-scaled ratings, acc-heavy maps lost pp against pass-heavy ones of
    the same stars, and no weight combination undid it.
  - **`TechAccExponent` 1.9 → 3.5** (with `TechWeight` 0.60 → 0.2193 and the tech scaler 17.6 → 22 in the same commit):
    so tech pp goes mostly to high-acc scores on tech maps, instead of nearly as much to any pass of a tech map. With 3.5,
    a 90% score gets about 22% less tech pp than a 96% one (11% with 1.9), and tech pp is about twice as large at 96%. It
    departs from prod's 1.9. The reason is confirmed by the author; the effect is computed from the formulas at that
    commit.
  - **No inflation:** most scores' sum is under 650, so their pp rose (×1.44 under 3★, ×1.26 at 7–9★, ×1.15 at 13★+; the
    highest score 949 → 897) and harder maps count relatively less. `StarDivisor` / `StarExponent` were converted so every
    map keeps its stars (exactly above 4.49★), and `PpMultiplier` keeps the top 1000 players' totals on average.
  - **Rebalance after it** (acc curve power 0.9, `PassTopKnee` 13.375 → 10.4, `TechWeight` 0.0938 → 0.0891,
    `LowStarBonus` halved, one saber nerf 0.548 → 0.49): with pp linear, pass-heavy maps were worth more than others of
    their stars, and pp at the predicted acc was higher on 1–3★ maps than on 3–5★ maps. Pass share slope / gap
    +0.036 / +0.019 → 0.000 / 0.000, OneSaber gap −0.009 → −0.003, pp at the predicted acc 578, 548, 525, 522 → 466, 464,
    473, 490 from 1★. The 0.9 power cut top players' scores most on easy maps, so the low-star curve power brings part of
    it back (top 100's median pp at 1–2 … 4–5★: 398, 404, 443, 478 before, 325, 362, 412, 456 with 0.9, 362, 385, 424,
    460 now).
- **Impact:** over the last development round (`e90b2bb` → `1b5d6d0`), the balance tuner's metrics improved: pass share
  slope 0.049 → 0.016, tech share gap 0.033 → 0.004. The range's starting point was never measured with the tuner, so there
  are no figures from the very start.

### Retune on the player-adjusted worth (after `372eb8c`)
- **What:** `TechWeight` 0.0891 → 0.124; the acc curve power stays 1.0 up to 7★ and reaches 0.9 at 10★ (was 2★ and 5★);
  the low star bonus is 0.006 × (9 − stars)^1.36 (was 0.0321 × (4.49 − stars)^1.36); `PpMultiplier` 1.0833 → 1.0597.
- **Why:** confirmed.
  - **Tech:** for the same players at their level, tech-heavy maps paid less than others of their stars (tech share
    slope −0.033, tech 8+ maps −2.7%).
  - **Low stars:** top players' best scores at 3–8★ had fallen 3–6% under prod's against the 10–12★ plateau, most on acc
    players' 99% scores on 4–6★ maps (ACC | Pandita #10 in prod, #45).
  - **Multiplier:** the top 1000 players' average back at prod's (16395).
- **Impact:** tech share slope −0.007, top 1000 rank correlation 0.991 for the tech change. 3★ 1.046, 5★ 1.060, 7★
  1.018 against the plateau (prod 1.035, 1.085, 1.020), Pandita #7. 1–2★ maps get less bonus (+10% / +8.5%, was +17.6% /
  +11%) but still rise with the curve power. The spread of players' pp stays narrower than prod's (#1 21910 vs 24140,
  #1000 15060 vs 14693): that comes from the removed inflation, not the multiplier.

### Ratings and stars on prod's scale, without display curves
- **What:** pass, tech, acc and stars match prod's distributions. Medians and 99th percentiles, now vs prod: stars 6.86 /
  13.34 (6.97 / 13.19), pass 4.47 / 13.03 (4.44 / 13.39). The stored values are exactly what the formulas use.
- **Why:** confirmed. Comparisons with prod were hard when the scales differed. An intermediate version used "display
  curves" with internal values behind them (`ff000c1`, `5a90139` and later). They were replaced with real values, the
  shape folded into the formulas themselves.
- **Impact:** no hidden conversions. A server computing pp from RatingAPI's ratings must use the same rating powers.

### Speed modifiers
- **What:** modifier multipliers apply to the ratings directly; each modifier has its own curve and NJS shown on pages.
- **Impact:** SS / FS / SFS scores use their own curves and ratings consistently.

---

## Balance tooling

### Balance tuner and Balance Tuning page
- **What:** `BalanceTuner` re-scores every score in memory (~6 s per pass) and measures each map's worth: Megametric75
  relative to what maps of its stars average (`WorthReference`, below), and later the player-adjusted worth (next section). It reports slopes and gaps by pass / acc / tech share, predicted acc and OneSaber, and
  suggests constants. A separate sweep explores pp formula parts. Shown on a new Balance Tuning page. (`5890677` onward)
- **Why:** confirmed: so no kind of map is systematically worth more than others of the same stars.
- **Impact:** every constant change in the range was checked with it. Its pass-calibration suggestion proved unreliable
  (often at the edge of its range); it now keeps a constant whose target is within 0.01 of 0 or barely moves over the
  range tried (`TargetTolerance`, `MinResponse`), and the page says why.

### Player-adjusted worth (`BalanceTuner.WorthMeasure`, after `372eb8c`)
- **What:** `log pp = skill(player, half year) + generosity(map)`, fitted on each player's scores from 2★ under their
  level to 0.5★ above (level: the 5th hardest map in their top 50), then compared with maps of the same stars and maps
  first played around the same time. The default for the tuner, the reweighter and `Leaderboard.Worth`;
  `Leaderboard.MegametricWorth` keeps the old one. An `InRange` measure (pp / top pp of in-range players against same-star
  maps) is kept as a diagnostic.
- **Why:** confirmed.
  - **Megametric75** mixes everyone who passes the map, weighted by where it lands in their profile. On pass 14+ maps it
    read ≈ 0, but −20% for the top 100 and +31% for #2001–10000, whose few high plays put these maps high in their list.
    It overreacts to rating changes (a 0.1% acc shift on tech 7+ maps: −0.02 → +0.19).
  - **InRange** barely reacts: stars are what a 96% score pays, so a map that pays more is compared with higher-star maps.
  - **Player-adjusted** reacts in proportion (3% more pp: +1.9% worth). Skill per half year: with one skill per player,
    maps ranked in 2026 looked 2.8% more generous. Map age: maps first played in 2022 were 3% more generous (grinded longer).
- **Per speed modifier** (after `5292d1f`): SS / FS / SF scores are each their own item in the fit, at that modifier's
  stars, against nomod maps of those stars (`ModifiersRating.SSWorth` / `FSWorth` / `SFWorth`). `Leaderboard.Worth` and
  worth by rank use nomod scores only, NF scores none. They had counted in the map's worth at its nomod stars (6.3% of
  scores); nomod worth moved 0.26 points on average.
- **Impact:** worth spread 0.46 → 0.059. With it, pass, predicted acc and OneSaber were already balanced; only tech
  wasn't (above). Its curve over stars isn't reliable (players improve between their easy and hard maps).

### Acc reweighter (the only score-based rating change)
- **What:** `AccReweighter` moves each map's acc rating towards what its kind of map is worth.
  - **Target:** fitted on pass / tech share, predicted acc, OneSaber and star band.
  - **Scaling:** evidence-weighted (maps with few scores move less, newly ranked maps stay ×1).
  - **Search:** a line search over several passes, with how far to go chosen on held-out players.
  - **Guards:** balance guards must pass, multipliers average 1 within each star band and stay within ±15%.
  - **Halved** (`Applied` 0.5, `263dc0b`): only half of the fitted change (in log) is stored, so ±7.5% at most.
  - **Smooth worth reference** (`263dc0b`): a map's Megametric75 is compared with a local linear fit of Megametric75 on
    stars (Gaussian kernel, `BalanceTuner.WorthBandwidth` 0.25★) instead of its star range's average. Also used for
    `Leaderboard.Worth`.
  - **Guard fix** (`263dc0b`): the held-out search now picks the lowest held-out spread among the points that pass the
    balance guard, and warns when nothing is kept.
  - **Storage:** `Leaderboard.AccReweight`, kept by `Overwrite` / `Refresh`, which can also return raw acc ratings.
  - **Replaces:** the old `Autoreweight` / `Autoreweight3` tools, which are removed.
  - **On the player-adjusted worth** (after `372eb8c`): evidence counted from the measure's own scores, strengths 0.1–1.3,
    guard tolerances 0.0035 / 0.0125. When `Applied` (0.5) of the fit fails the guard, the largest fraction under it that
    passes is stored (`AppliedStep` 0.05).
  - **`Applied` in the fit** (after `5292d1f`): each map's target is `Applied` of the way from its worth to what its kind
    of map is worth (in log), and the kept point is stored as is, instead of a fraction of the full fit. Guard tolerance
    0.0035 → 0.0065 (`PlayerAdjustedGuardTolerance`).
- **Why:** confirmed.
  - The old tools were one-shot, unguarded, and lost on re-rating.
  - About half of each map's worth deviation reproduces across independent halves of the players (held-out spread 0.49 →
    0.24); the other half is noise and is left alone.
  - Several naive versions broke the balance: correcting kinds of maps, fixed step sizes, and slopes measured on the
    reweighted shares.
  - **Halved:** players found the full fit too strong and the database better without it. Most of the worth spread goes
    early on the held-out path (0.49 → 0.34 with a 1.7% mean change).
  - **Smooth reference:** with the step, maps at the top of a range looked overworth (+0.09 in the top fifth) and maps at
    the bottom underworth (−0.07), and a map crossing a range boundary changed reference at once, so the reweighter
    emptied the outer tenth of each range by ~20%. Now within ±0.02 by position. Interpolating between the ranges'
    averages (tried first) underrated whole ranges (2–3★ −0.11, from the 5 maps of 1–2★).
  - **Guard fix:** with a balanced start, the lowest held-out spread overall was at pass 2, where every point of the full
    run failed the guard, so nothing was kept.
  - **Player-adjusted guard:** scaled down by the ratio of the worth spreads (0.00065 / 0.0026) nothing passed: the
    measure's own noise is about 0.0009 on the pass slope and 0.0022 on the tech slope, and correcting single maps moves
    the slopes by itself (players' skills are fitted again). Halfway between twice the noise and Megametric75's.
  - **Largest passing fraction:** the passes are fitted together, the later ones undoing the first one's drift, so half of
    the fit drifted more than all of it (pass slope +0.012 vs +0.006).
  - **`Applied` in the fit:** stepping down still stored almost nothing (0.10 of the fit, 0.43% mean change, worth spread
    0.0615 → 0.056). With partial targets the drift stays (pass slope +0.005 → +0.011), so the guard was loosened for a
    reweight that changes something.
- **Impact:** outliers corrected (Ascension to Heaven Expert worth +1.32 → −0.10; 3585 maps, 4.1% on average) with balance
  kept. Worth spread is no longer a fair overall balance measure afterwards. On the player-adjusted worth with `Applied` in
  the fit: 2.3% mean change on 3084 maps (0.922–1.072), worth spread 0.062 → 0.035, the highest pass 5% −2.1% → −0.3%,
  but kinds of maps within ±0.8% instead of ±0.4% (pass share slope +0.006 → +0.015, predicted acc gap −0.006 → −0.014).

### Research exports
- **What:** new tools in `Refresh/Tools/`:
  - `ParityCheck` compares the Analyzer's parity and directions with replays, including the controller roll.
  - `RatingFitExport` exports swings, scores and walls for fitting.
  - `ModelComparisonExport` compares with prod.
  - `PassWindowCheck` compares pass orderings.
  - The Python scripts `hard_map_offset_fit.py` and `modifier_check.py`.
- **Impact:** all read-only; they produced the fits cited above.

---

## Website

### New pages and tools
- **Top Scores:** every score with filters and sorts, read from a compact per-database cache file
  (`Services/TopScoresCache`, ~56 MB for 4.4M scores, loads in about a second). The cache is uploaded and downloaded with
  its database.
- **Top Acc per Map graph:** the top accuracies against stars, with an optional comparison database.
- **Curve Explorer:** the acc curve for any predicted acc.
- **Balance Tuning page** (see above).
- **`pp-calculation-example.html`:** a worked example of how a score's pp and a map's stars are calculated.

### Improved pages
- **Leaderboard:**
  - NJS, including the effective NJS with speed modifiers;
  - a modifier selector, which also filters the scores to that modifier;
  - the map's acc curve, and a full pp curve (0–100%, with its pass / acc / tech parts, the base curve's pp and the scores)
    that replaced the pp table;
  - the cover, and a switcher to the song's other difficulties;
  - worth, of the selected speed modifier;
  - how many players get pp from it above a weight (as profiles show it), with a ranked plays minimum (default 75).
- **Leaderboards:** redesigned with all ratings, sortable headers, difficulty badges, worth tinted when far from average,
  search by song or mapper name, and 25 per page. Worth is the player-adjusted one, in %, tinted at ±8%.
- **Leaderboard Statistics:** split into views (Overview, Worth by rank, Profile weights, Megametric, Map traits); every
  column sortable (the ratio columns used to fall back to Total PP), worth by player rank and the Megametric worth added.
- **Ranking:** Top PP next to Total PP. **Map Outliers:** sorted by Outlier % by default.
- **Weighted / Unweighted Scores graphs:** read from the Top Scores cache instead of the scores table
  (`Services/ScoresGraphData`), with rolling averages over every score and speed-modifier filters (SS, FS and SFS unticked
  by default, also on Top Acc).
- **Players pp graph:** loads its points asynchronously, reading only the columns drawn.
- **Database Metrics:** shows a neutral difference (DB2 − DB1, and in %) instead of declaring a "winner", since whether
  higher or lower is better depends on the metric.
- **Database Comparison:** no longer fails on NaN ratings, and loads nothing until a view is picked (the players view
  reads every player of both databases).

### Removed
- **Batch Players Balancing and Players Balancing Graphs** (`2da77e3`). Per the commit message they were the old
  balancing pages (inferred: superseded by the balance tuner).
- **Pass Comparison and `PassWindowCheck`** (after `372eb8c`): the tool only fed that page, and the balancer doesn't use it.

### Database selection
- **What:** `MainDB: -1` (the new value in `databases.json`) makes the last listed database the default, and a saved
  browser selection that's no longer listed is forgotten.
- **Impact:** a new database becomes the default by adding it last.

---

## Pipeline, performance and project

- **Faster refresh:**
  - `ScoresPlayersRefresh` recomputes scores and players in one pass, about 3× faster.
  - `Overwrite` rates only the stored modifiers.
  - Columns are checked before being added (`DbSchema`), so failed `ALTER TABLE` errors no longer fill the log.
- **`FullPipeline(autoreweight, balancer, keepAccReweight)`:** Overwrite → RefreshEverything → AccReweighter →
  RefreshEverything → BalanceTuner / comparisons. `keepAccReweight` no longer has a default. `RatingsRefresh.RefreshNjs` and `CurvesRefresh.Regenerate` fill new
  columns without re-rating.
- **New database columns:** `Njs`, `HitDistance`, `AccReweight`, `Worth`, `MegametricWorth`, `WorthRank20` …
  `WorthRank10000`, per-modifier curves and per-modifier worth (`SSWorth`, `FSWorth`, `SFWorth`).
- **Top Scores caches** of databases not in `databases.json` are deleted at startup, with unfinished `.tmp` saves.
- **Project:**
  - `Program.cs` organized into sections;
  - README and `CHANGELOG.md` added;
  - `.gitignore` covers databases, caches, `_archive/` and generated reports;
  - the solution file targets Visual Studio 17;
  - `UploadDatabaseAsync` is commented out by default (`bbf1e34`);
  - databases and caches in `wwwroot` are excluded from the project by wildcard (`wwwroot\**\*.db`, `wwwroot\cache\**`).

---

## Overall impact and trade-offs

The branch turns portaBLe into a data-validated rating lab. Parity, directions, pass, tech and the acc rating were each
fitted on replays or scores, and the pp formula and per-map corrections were checked by an in-memory balance tuner.
Over the last development round the balance metrics clearly improved, outlier maps are corrected, and ratings and pp
sit on prod's scale.

**Trade-offs and limitations revealed along the way:**
- **Fitted to this data:** many constants (pair table, dot offsets, acc corrections, rating powers) are fitted on the
  current ranked set and replays, and should be refitted when the model or data change much.
- **Burst sensitivity:** the 4–32 s windows bring back some sensitivity to short bursts.
- **Scores in the ratings:** the acc reweighter uses scores (by design, as the only exception), adds a second re-score to
  the pipeline, and makes worth spread unusable as an overall balance measure afterwards. On the player-adjusted worth,
  a reweight that changes something tilts kinds of maps a little (pass-heavy and low predicted acc maps up ~1 point).
- **Worth measures:** each one sees something different. Player-adjusted is the target; Megametric75 and the rank
  columns are kept for comparison, and neither shows how pp should progress over stars.
- **For consumers of RatingAPI:** `star_rating` is gone (replaced by `stars`), and pp should be computed with
  `RatingAPI/Pp` (rating powers, acc curve power, no inflation) rather than prod's formula.
- **Small samples:** OneSaber (73 maps) and the 13+★ band are small, so their metrics are noisy.
