# Branch Changelog

**Range:** `5890677` ("New model + auto-rebalancer", included) through `bbf1e34` on `loloppe-rebalancer`, plus the matching
commits in the submodules: RatingAPI `3955df3 → 493468e` (15 commits) and Analyzer `0d47de6 → 62c59de` (9 commits). The
Parser submodule is unchanged.

## Overview

The branch rebuilds BeatLeader's rating system in portaBLe so that every rating change is fitted on, or checked against,
real data: top players' replays, all ranked scores and fail attempts. The most important results:

- **Analyzer:** the parity predictor and the swing directions now follow what top players actually do. The pass rating
  uses time-based windows, and tech only counts wrist work.
- **Acc rating:** a new Acc AI model, per-map acc curves, and corrections for the maps where players fall short of the
  Acc AI's prediction (the hardest maps, maps where the hands travel far, maps with very high pass).
- **pp formula:** moved into one tunable class and re-balanced so no kind of map is systematically worth more than others
  of the same stars. Ratings, stars and players' pp are on prod's scale.
- **Tools:** a balance tuner, and an acc reweighter that corrects individual maps from scores, under balance guards.
- **Website:** new Top Scores and Top Acc pages, a curve explorer, a pass comparison page, a worth column, a redesigned
  Leaderboards page, and much faster graph pages.

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
- **Impact:** the 99%+ tail and the slider outliers are tamed. Several table iterations happened (`65204a7`, `79c19e8`,
  `a3535f7`); only the latest values remain.

### Acc rating corrections and scale
- **What:** the acc rating's effective predicted acc is lowered:
  - **Hard maps:** on maps under 96.75% predicted acc.
  - **Hand travel:** by 3% per meter of mean hit distance above 0.62 m, capped at 0.8 m, below 97–97.5% predicted acc.
  - **Very high pass:** by 0.214% per pass point above 14.52.

  Acc rating = 13.5382 × Curve(effective acc + 0.0019)^−0.8222. Previously it was 15.5 / Curve(predicted + 0.0022) ×
  low-note nerf.
- **Why:** confirmed, all fitted on top players' scores after removing each player's own level. Top players fell further
  behind the prediction on the hardest maps (their pp stopped rising past ~12★), and on maps where misses and bad cuts cost
  more. The Acc AI's training targets exclude misses.
  - Rejected: a tech term (tech pp already pays for it) and a hit-distance term on easy maps (their acc rating is too
    sensitive there).
  - The power gives prod's acc-rating distribution.
- **Impact:** The Purple Dimension and similar maps are no longer under-rated, without overvaluing Godspeed.

### API changes
- **Removed:** the legacy `star_rating` (and `low_note_nerf`) fields of RatingAPI's response. portaBLe computes stars
  itself.
- **Added:** `slider_percentage`, `njs` and the hit distance.
- **Faster rating:** an overload rates only the requested modifiers, only the requested difficulty is parsed, and ONNX
  batches went from 4 to 512.
- **Impact:** a breaking change for any other consumer of `star_rating`.

---

## pp formula, stars and scale (portaBLe)

### pp formula centralized and retuned
- **What:** the pp formula moved from hard-coded `ReplayUtils` into `PpFormula`, with a `PpMultiplier` (`5890677`). Net
  changes from the start:
  - **Pass pp:** `PassScale` 15.2 → 12.3, plus a top compression (each point above pass 13.4 counts half).
  - **Acc and tech pp:** take the rating to a power, acc^1.2163 and tech^1.2477.
  - **Tech:** tech pp = e^(`TechAccExponent` × acc) × `TechWeight` × tech^`TechRatingPower`. The exponent on accuracy,
    `TechAccExponent`, went 1.9 → 3.5 (`e90b2bb`); the power on the tech rating, `TechRatingPower` (1.2477), is a separate
    and later change (below).
  - **Low-star bonus:** maps under ~4.5★ get up to +40%.
  - **Stars:** 1.2131 × (pp at 96% / 45.25)^0.9039.
  - **`PpMultiplier`:** 1.146.
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
- **Impact:** over the last development round (`e90b2bb` → `1b5d6d0`), the balance tuner's metrics improved: pass share
  slope 0.049 → 0.016, tech share gap 0.033 → 0.004. The range's starting point was never measured with the tuner, so there
  are no figures from the very start.

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
  relative to its star band. It reports slopes and gaps by pass / acc / tech share, predicted acc and OneSaber, and
  suggests constants. A separate sweep explores pp formula parts. Shown on a new Balance Tuning page. (`5890677` onward)
- **Why:** confirmed: so no kind of map is systematically worth more than others of the same stars.
- **Impact:** every constant change in the range was checked with it. Its pass-calibration suggestion proved unreliable
  (often at the edge of its range).

### Acc reweighter (the only score-based rating change)
- **What:** `AccReweighter` moves each map's acc rating towards what its kind of map is worth.
  - **Target:** fitted on pass / tech share, predicted acc, OneSaber and star band.
  - **Scaling:** evidence-weighted (maps with few scores move less, newly ranked maps stay ×1).
  - **Search:** a line search over several passes, with how far to go chosen on held-out players.
  - **Guards:** balance guards must pass, multipliers average 1 within each star band and stay within ±15%.
  - **Storage:** `Leaderboard.AccReweight`, kept by `Overwrite` / `Refresh`, which can also return raw acc ratings.
  - **Replaces:** the old `Autoreweight` / `Autoreweight3` tools, which are removed.
- **Why:** confirmed.
  - The old tools were one-shot, unguarded, and lost on re-rating.
  - About half of each map's worth deviation reproduces across independent halves of the players (held-out spread 0.49 →
    0.24); the other half is noise and is left alone.
  - Several naive versions broke the balance: correcting kinds of maps, fixed step sizes, and slopes measured on the
    reweighted shares.
- **Impact:** outliers corrected (Ascension to Heaven Expert worth +1.32 → −0.10; 3585 maps, 4.1% on average) with balance
  kept. Worth spread is no longer a fair overall balance measure afterwards.

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
- **Pass Comparison:** pass ordering, current vs database vs prod.
- **Balance Tuning page** (see above).

### Improved pages
- **Leaderboard:**
  - NJS, including the effective NJS with speed modifiers;
  - a modifier selector;
  - the map's acc curve and a pp table;
  - worth.
- **Leaderboards:** redesigned with all ratings, sortable headers, difficulty badges, worth tinted when far from the band,
  and 25 per page.
- **Weighted / Unweighted Scores graphs:** read from the Top Scores cache instead of the scores table
  (`Services/ScoresGraphData`), with rolling averages over every score and speed-modifier filters.
- **Players pp graph:** loads its points asynchronously, reading only the columns drawn.
- **Database Metrics:** shows a neutral difference (DB2 − DB1, and in %) instead of declaring a "winner", since whether
  higher or lower is better depends on the metric.
- **Database Comparison:** no longer fails on NaN ratings.

### Removed
- **Batch Players Balancing and Players Balancing Graphs** (`2da77e3`). Per the commit message they were the old
  balancing pages (inferred: superseded by the balance tuner).

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
  RefreshEverything → BalanceTuner / comparisons. `RatingsRefresh.RefreshNjs` and `CurvesRefresh.Regenerate` fill new
  columns without re-rating.
- **New database columns:** `Njs`, `HitDistance`, `AccReweight`, `Worth`, and per-modifier curves.
- **Project:**
  - `Program.cs` organized into sections;
  - README and `CHANGELOG.md` added;
  - `.gitignore` covers databases, caches, `_archive/` and generated reports;
  - the solution file targets Visual Studio 17;
  - `UploadDatabaseAsync` is commented out by default (`bbf1e34`).

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
  the pipeline, and makes worth spread unusable as an overall balance measure afterwards.
- **For consumers of RatingAPI:** `star_rating` is gone, and pp computed from its ratings needs the new rating powers.
- **Small samples:** OneSaber (73 maps) and the 13+★ band are small, so their metrics are noisy.
