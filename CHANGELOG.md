# Changelog: portaBLeRebalancer

The `loloppe-rebalancer` branch of portaBLe, plus the rating changes in the RatingAPI and Analyzer submodules. The Acc AI
model comes from `beatsaber-replays-ai-2`.

The goal is ratings that never need scores at rating time. Scores, replays and fail attempts are used offline, to fit and
validate constants. The one exception is the acc reweighter (below), the only tool that changes ratings from scores.

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
