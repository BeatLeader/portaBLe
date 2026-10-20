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
`balancer` steps 4-5 and the pass window check. Without step 3, `keepAccReweight: false` gives raw acc ratings (the
stored multipliers are cleared; `Overwrite` / `Refresh` take the same option). After a pp formula change only
(`Refresh/PpFormula.cs`), skip step 1.

## Ratings on prod's scale

Pass, tech, acc and stars are on prod's scale (fitted on the quantiles of prod's ranked ratings), and the stored values
are the ones every formula uses:

| | Formula | Shape |
|---|---|---|
| Pass | Analyzer `AnalyzeMap` | `PASS_CALIBRATION_FACTOR` (linear) |
| Tech | `BALANCED_TECH_SCALER` x (average swing tech x `Ratings.TechPassFactor(pass)`)^`Ratings.TechExponent` | power 0.8015 |
| Acc | RatingAPI `AccRating`: `ACC_SCALE` x Curve(effective acc + `ACC_OFFSET`)^-`ACC_EXPONENT` | power 0.8222 |
| Stars | `PpFormula.Stars`: `StarScale` x (pp at 96% / `StarDivisor`)^`StarExponent` | power 0.9039 |

Prod's tech and acc ratings are flatter at the top than acc and tech pp need, so the pp formula takes them to a power
(`PpFormula.AccRatingPower` 1.2163, `TechRatingPower` 1.2477). With pp proportional to the ratings, a star range's
acc-heavy maps lost pp against its pass-heavy ones and the balance broke. A server computing pp from RatingAPI's ratings
needs the same powers.

## Acc reweighter

`Refresh/Tools/AccReweighter.cs`: each map's worth (`Leaderboard.Worth`, its Megametric75 relative to its star range,
shown on the Leaderboards and Leaderboard pages) is compared with what its kind of map is worth (pass / tech share,
predicted acc, OneSaber, star range), and its acc rating is moved towards it.

- Everything is re-scored in memory for each try, since one map's change moves every player's weights.
- Maps with few scores move less, and maps without a worth (newly ranked) keep ×1.
- How far it goes is chosen on held-out players: it fits on one half of the players and checks on the other.
- The result is only kept if the balance tuner's slopes and gaps don't get worse.
- Multipliers average 1 within each star range and stay within ±15%.

## Where the constants are

| | |
|---|---|
| Pass / one saber / tech scaling, pass windows | `RatingAPI/Analyzer/.../Algorithm/AnalyzeMap.cs` (`PASS_CALIBRATION_FACTOR`, `ONE_SABER_NERF`, `BALANCED_TECH_SCALER`, `WindowSeconds`, `DENSITY_*`) |
| Pass formula (fitted on fail data) | `RatingAPI/Analyzer/.../Algorithm/Difficulty.cs` (falloffs, stress weights, `DENSITY_CAP`, wall buffs) |
| Arrow directions (fitted on replays) | `RatingAPI/Analyzer/.../Helper/EntryExit.cs` (`SAME_ROW_ADJUST_SCALE`, `ROW_CHANGE_ADJUST_*`), dots in `Helper/DotDirections.cs` |
| Parity | `RatingAPI/Analyzer/.../Algorithm/ParityPredictor.cs` (comfort / rotation / reset costs, `UsePairResets`) |
| Tech shape | `RatingAPI/Analyzer/.../Data/Ratings.cs` (`TechExponent`, `TechPassBase`) |
| Acc rating | `RatingAPI/Controllers/AccRating.cs` (`ACC_SCALE`, `ACC_OFFSET`, hard map correction `HARD_MAP_*`, `ACC_EXPONENT`, movement and high pass corrections `HIT_DISTANCE_*` / `HIGH_PASS_*`) |
| pp formula | `Refresh/PpFormula.cs` (pass / acc / tech pp and their rating powers, inflate, stars `StarDivisor` / `StarScale` / `StarExponent`, `PpMultiplier`, low star bonus `LowStarBonus*`) |
| Acc reweighter | `Refresh/Tools/AccReweighter.cs` (`Passes`, `Strengths`, `EvidenceScores`, `MaxChange`, guard tolerances) |
| Constants the tuner assumes | `Refresh/Tools/BalanceTuner.cs` (`RatingConstants`: keep it equal to the values in code) |

## Layout

| | |
|---|---|
| `Refresh/` | the rating / pp pipeline (`RatingsRefresh`, `ScoresPlayersRefresh`, `LeaderboardsRefresh`, `StatsRefresh`, `ReplayUtils`, `PpFormula`) |
| `Refresh/Tools/` | `AccReweighter` (writes ratings); read-only: `BalanceTuner`, `ModelComparisonExport`, `PassWindowCheck`, `ParityCheck` (Analyzer parity and directions vs replays), `RatingFitExport` (swings / scores / walls exports used to fit the formulas and the Acc AI's player skills) |
| `Pages/` | the website (leaderboards, players, comparisons, Balance Tuning) |
| `RatingAPI/` | submodule: Analyzer, Acc AI inference, acc rating |
