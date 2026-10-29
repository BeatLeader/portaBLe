# portaBLe (Rebalancer branch)

BeatLeader system in a compact, manageable form for quick experiments and easy deployment for evaluation.

This branch reworks the ratings and checks every change against real data (replays, scores, attempts). Ratings come from
the map only: data is used offline, to fit constants and validate. The one exception is the acc reweighter, the only tool
that changes ratings from scores (see below). The Acc AI model is trained in [beatsaber-replays-ai-2](../beatsaber-replays-ai-2).

## Setup

```
git submodule update --init --recursive
```

RatingAPI (and the Analyzer and Parser inside it) are submodules. Build Parser, then Analyzer, then RatingAPI, then
this project. The Acc AI model (`RatingAPI/model_sleep_bl.onnx`) is embedded in the RatingAPI DLL: rebuild RatingAPI
after replacing it.

Databases live in `wwwroot/` (`Database.db` is the one being worked on, `databases.json` lists the others, downloaded
from S3 at startup). They are not in git.

## Running

Everything is driven from `Program.cs` (`Main`): uncomment the steps to run, then start the project.

**After changing the Analyzer, the Acc AI model or a rating constant:**

1. `RatingsRefresh.Overwrite`: rates every leaderboard again with RatingAPI (Analyzer + Acc AI). Keeps each map's acc
   reweight (`Leaderboard.AccReweight`).
2. `RefreshEverything`: pp, ranks and weights of every score, players, leaderboards (Megametric, worth), stars, outliers,
   stats.
3. `AccReweighter.Run`, then `RefreshEverything` again: moves each map's acc rating so it's worth about as much as other
   maps of its kind (`wwwroot/acc-reweight.json`).
4. `BalanceTuner.Run`: suggests constants so that no kind of map is worth more than others of its stars
   (Balance Tuning page, `wwwroot/balance-tuning.json`).
5. `ModelComparisonExport.Run`: side by side with prod (`wwwroot/model-comparison.json`).

`FullPipeline(dbContext, env, autoreweight, balancer, keepAccReweight)` runs all of it: `autoreweight` adds step 3,
`balancer` steps 4-5. Without step 3, `keepAccReweight: false` gives raw acc ratings (the
stored multipliers are cleared; `Overwrite` / `Refresh` take the same option). After a pp formula change only
(`RatingAPI/Pp/PpFormula.cs`), skip step 1.

From the command line (`dotnet run -c Release -- …`):

- `rescore`: `RefreshEverything` alone on `wwwroot/Database.db` (after a pp formula change).
- `rerate <wwwroot/file.db>`: `RatingsRefresh.Overwrite` with raw acc ratings, then `RefreshEverything`, on that database
  (after an Analyzer or rating constant change).
- `accreweight [Setting=value ...] [rescore]`: the acc reweighter alone, with any of its settings.
- `mapstats <wwwroot/file.db>`: the Analyzer's counts (inlines, bomb resets, walls, linear %) from the maps folder, then
  the tags, without rating the maps again.
- `upload <wwwroot/file.db>` / `cache <wwwroot/file.db> [upload]`: upload a database and its Top Scores cache, or build it.
- `songreceval <wwwroot/file.db> [players] [set] [seed] [first plays]`: offline evaluation of the Song
  Recommendations page (read only): hides each sampled player's 40 most recent scores and measures how the suggestions
  match what they played next, against a popular-maps baseline and the current defaults player by player. Sets:
  `default` (without Easier, current), `easier`, `weights` (Style x Easier), `keep` (link keep %), `links` (peers per
  seed x plays per peer), `plays`, `seeds` (seed count), `offset` (level offset), `neutral` (neutral players); seed: which players are sampled; first plays: new players simulated, only their first that many ranked
  plays kept (`SongRec/Tools/RecommendationEvaluation.cs`).
- `songrecbench <wwwroot/file.db> [players]`: times the Song Recommendations page's run for players spread over the
  ranks (the first runs step by step), with a digest of each one's suggestions, to check a speed change leaves them as
  they were (`SongRec/Tools/RecommendationBenchmark.cs`).

## Ratings on prod's scale

Pass, tech, acc and stars are on prod's scale (fitted on the quantiles of prod's ranked ratings), and the stored values
are the ones every formula uses:

| | Formula | Shape |
|---|---|---|
| Pass | Analyzer `AnalyzeMap` | `PASS_CALIBRATION_FACTOR` (linear) |
| Tech | `BALANCED_TECH_SCALER` x (average swing tech x `Ratings.TechPassFactor(pass)`)^`Ratings.TechExponent` | power 0.8015 |
| Acc | RatingAPI `AccRating`: `ACC_SCALE` x Curve(effective acc + `ACC_OFFSET`)^-`ACC_EXPONENT` | power 0.8222 |
| Stars | `PpCalculator.ToStars`: `StarScale` x (final pp of a 96% score / `PpMultiplier` / `StarDivisor`)^`StarExponent`, the map's own curve and the low star bonus included | power 1.1751 |

Prod's tech and acc ratings are flatter at the top than acc and tech pp need, so the pp formula takes them to a power
(`PpFormula.AccRatingPower` 1.2163, `TechRatingPower` 1.2477). With pp proportional to the ratings, a star range's
acc-heavy maps lost pp against its pass-heavy ones and the balance broke. A server computing pp from RatingAPI's ratings
needs the same powers.

The pp of scores and the stars are computed in RatingAPI (`RatingAPI/Pp`: `PpFormula`, `PpCalculator.ScorePp` /
`ToStars`, `ModifiersMap`), so a server needs RatingAPI only; portaBLe's `ReplayUtils` just passes its database values
to them. RatingAPI's response includes `stars` (without the acc reweighter's multipliers, which only portaBLe has).

## Worth

A map's worth (`Leaderboard.Worth`, shown on the Leaderboards, Leaderboard and Leaderboard Statistics pages) is what the
balance tuner and the acc reweighter balance, in the measure `BalanceTuner.WorthMeasure` picks:

- `PlayerAdjusted` (the default): `log pp = skill(player, half year) + generosity(map)`, fitted on each player's scores
  near their level (from 2 stars under it to 0.5 above), then compared with maps of the same stars and maps first played
  around the same time. How much the map pays the same players than their other maps at their level: who plays it and
  where it lands in their profiles drop out, and a map that pays more shows it (3% more pp, +1.9% worth).
- `Megametric75`: Megametric75 relative to what maps of its stars average (kept as `Leaderboard.MegametricWorth`). Its
  profile weight overreacts to rating changes and inflates maps played by players with few high plays (survivors of
  very high pass maps).
- `InRange`: pp / top pp of the players who can play the map, against maps of the same stars. Stars are what a 96%
  score pays, so a map that pays more is compared with higher star maps: it barely reacts to rating changes.

Worth is per speed modifier: `Leaderboard.Worth` only has nomod scores, and SS / FS / SF scores are each their own item
in the fit, at that modifier's stars and against nomod maps of those stars (`ModifiersRating.SSWorth` / `FSWorth` /
`SFWorth`, on the Leaderboard page with that modifier; the Leaderboards page shows nomod worth only). A score's speed is its
first speed modifier, as for its pp. NF scores (0 pp) count in no worth.

`Leaderboard.WorthRank20` ... `WorthRank10000` split pp / top pp of nomod scores by player rank (Leaderboard Statistics,
Worth by rank).

## Acc reweighter

`Refresh/Tools/AccReweighter.cs`: each map's worth (above) is compared with what its kind of map is worth (pass /
tech share, predicted acc, OneSaber, star range), and its acc rating is moved towards it.

- What maps of its stars average is a local linear fit on stars (`BalanceTuner.WorthReference`, Gaussian kernel of
  `WorthBandwidth` 0.25 stars), so a map's reference doesn't jump at a star range boundary.
- Everything is re-scored in memory for each try, since one map's change moves every player's weights.
- Gaps within a map's measurement noise barely move (`NoiseDeadZone`: noise = 0.28 / √scores), and maps without a worth
  (newly ranked) or whose worth rests on fewer than 40 scores (`MinWorthScores`) keep ×1.
- Each try is judged by each map's distance to its own target (`TargetMetric`), and the stored multipliers are scaled so
  they move worth at the real stars as much as the fit meant to (`StarResponse`: low and mid star maps respond about half
  as much once their stars move).
- How far it goes is chosen on held-out players: it fits on one half of the players and checks on the other, and takes
  the lowest held-out distance to the targets among the points that pass the balance guard.
- The result is only kept if the balance tuner's slopes and gaps get no worse than the guard tolerances allow (0.004 on
  the slopes, 0.0075 on the gaps). It lets a small tilt through: pass-heavy and low predicted acc maps gain a little on
  acc-heavy ones.
- Multipliers average 1 within each star range. The fit closes `Applied` (0.7) of each map's gap to its target (in log),
  within ×0.922-×1.085 (`MaxChange`), or ×0.885-×1.130 for maps with a large gap (`LargeGapMaxChange`).
- Maps under 115 color notes keep ×1 (`MinNotes`): their worth is low because players can't reach the steep top of
  their curve, not because of their acc rating, so a buff would only raise what a 100% pays. Their scores still count.

## Where the constants are

| | |
|---|---|
| Pass / one saber / tech scaling, pass windows | `RatingAPI/Analyzer/.../Algorithm/AnalyzeMap.cs` (`PASS_CALIBRATION_FACTOR`, `ONE_SABER_NERF`, `BALANCED_TECH_SCALER`, `WindowSeconds`, `DENSITY_*`, linear map pass buff `LINEAR_PASS_*`) |
| Pass formula (fitted on fail data) | `RatingAPI/Analyzer/.../Algorithm/Difficulty.cs` (falloffs, stress weights, `DENSITY_CAP`, wall buffs) |
| Arrow directions (fitted on replays) | `RatingAPI/Analyzer/.../Helper/EntryExit.cs` (`SAME_ROW_ADJUST_SCALE`, `ROW_CHANGE_ADJUST_*`), dots in `Helper/DotDirections.cs` |
| Parity | `RatingAPI/Analyzer/.../Algorithm/ParityPredictor.cs` (comfort / rotation / reset costs, `UsePairResets`) |
| Tech shape | `RatingAPI/Analyzer/.../Data/Ratings.cs` (`TechExponent`, `TechPassBase`) |
| Acc rating | `RatingAPI/Controllers/AccRating.cs` (`ACC_SCALE`, `ACC_OFFSET`, hard map correction `HARD_MAP_*`, `ACC_EXPONENT`, movement and high pass corrections `HIT_DISTANCE_*` / `HIGH_PASS_*`, hard swing correction `HARD_SWING_*` from the Analyzer's `Ratings.HardSwingRatio`) |
| pp formula | `RatingAPI/Pp/PpFormula.cs` (pass / acc / tech pp and their rating powers, pass top compression `PassTopKnee` / `PassTopSlope`, tech top compression `TechTopKnee` / `TechTopSlope`, acc curve power `AccCurvePower` / `LowStarCurve*` / `VeryLowStarCurve*`, stars `StarDivisor` / `StarScale` / `StarExponent`, `PpMultiplier`, low star bonus `LowStarBonus*`) |
| Acc curves | `RatingAPI/Controllers/Curve.cs` (`CurveSettings`: the table by predicted acc, `SliderTilt`, low note nerf `LowNote*`: gain cut, grind time span factor and length bonus, speed modifier blend) |
| Acc reweighter | `Refresh/Tools/AccReweighter.cs` (`Passes`, `Strengths`, `TargetMetric`, `StarResponse`, `NoiseDeadZone`, `MaxChange`, `LargeGapMaxChange`, `Applied`, guard tolerances, `HeldOutSplits`, `FitAtBaseStars`, `WorthLimit`, top play limiter `TopPlay*`, low note maps `MinNotes`, `FallbackStrengths`, `GuardMarginWarning`) |
| Map tags | `Refresh/LeaderboardsRefresh.cs` (`LinearPercentThreshold`, `Fitbeat*`, `BombResetsPerMinuteThreshold`, `InlinesPerMinuteThreshold`; the map type from RatingAPI's `InferPublish.Tag`) |
| Constants the tuner assumes, worth reference | `Refresh/Tools/BalanceTuner.cs` (`RatingConstants`: keep it equal to the values in code; `WorthBandwidth`) |

## Layout

| | |
|---|---|
| `Refresh/` | the rating / pp pipeline (`RatingsRefresh`, `ScoresPlayersRefresh`, `LeaderboardsRefresh`, `StatsRefresh`, `ReplayUtils`) |
| `Refresh/Tools/` | `AccReweighter` (writes ratings); read-only: `BalanceTuner`, `ModelComparisonExport`, `ParityCheck` (Analyzer parity and directions vs replays), `RatingFitExport` (swings / scores / walls exports used to fit the formulas and the Acc AI's player skills) |
| `Pages/` | the website (leaderboards, players, comparisons, Balance Tuning) |
| `RatingAPI/` | submodule: Analyzer, Acc AI inference, acc rating, pp formula and stars (`Pp/`) |
| `pp-calculation-example.html` | a worked example of how a score's pp and a map's stars are calculated |
