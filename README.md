# portaBLe (Rebalancer branch)

BeatLeader system in a compact, manageable form for quick experiments and easy deployment for evaluation.

This branch reworks the ratings and checks every change against real data (replays, scores, attempts) without ever
needing scores at rating time: data is only used offline, to fit constants and validate. The Acc AI model is trained in
[beatsaber-replays-ai-2](../beatsaber-replays-ai-2).

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

1. `RatingsRefresh.Overwrite`: rates every leaderboard again with RatingAPI (Analyzer + Acc AI).
2. `RefreshEverything`: pp, ranks and weights of every score, players, leaderboards (Megametric), stars, outliers, stats.
3. `BalanceTuner.Run`: suggests constants so that no kind of map is worth more than others of its stars
   (Balance Tuning page, `wwwroot/balance-tuning.json`).
4. `ModelComparisonExport.Run`: side by side with prod (`wwwroot/model-comparison.json`).

After a pp formula change only (`Refresh/PpFormula.cs`), skip step 1.

## Where the constants are

| | |
|---|---|
| Pass / one saber / tech scaling | `RatingAPI/Analyzer/.../Algorithm/AnalyzeMap.cs` (`PASS_CALIBRATION_FACTOR`, `ONE_SABER_NERF`, `BALANCED_TECH_SCALER`) |
| Pass formula (fitted on fail data) | `RatingAPI/Analyzer/.../Algorithm/Difficulty.cs` (falloffs, stress weights, wall buffs) |
| Parity | `RatingAPI/Analyzer/.../Algorithm/ParityPredictor.cs` (comfort / rotation / reset costs, `UsePairResets`) |
| Acc rating | `RatingAPI/Controllers/AccRating.cs` (`ACC_SCALE`, `ACC_OFFSET`, hard map correction `HARD_MAP_KNEE` / `HARD_MAP_SLOPE` / `HARD_MAP_FLOOR`, fitted with `Refresh/Tools/hard_map_offset_fit.py`) |
| pp formula | `Refresh/PpFormula.cs` (pass / acc / tech pp, inflate, `StarDivisor`, `PpMultiplier`, low star bonus `LowStarBonus*`) |
| Constants the tuner assumes | `Refresh/Tools/BalanceTuner.cs` (`RatingConstants`: keep it equal to the values in code) |

## Layout

| | |
|---|---|
| `Refresh/` | the rating / pp pipeline (`RatingsRefresh`, `ScoresPlayersRefresh`, `LeaderboardsRefresh`, `StatsRefresh`, `ReplayUtils`, `PpFormula`) |
| `Refresh/Tools/` | read-only tools: `BalanceTuner`, `ModelComparisonExport`, `ParityCheck` (Analyzer parity vs replays), `RatingFitExport` (swings / scores / walls exports used to fit the formulas and the Acc AI's player skills) |
| `Pages/` | the website (leaderboards, players, comparisons, Balance Tuning) |
| `RatingAPI/` | submodule: Analyzer, Acc AI inference, acc rating |

`ScoresRefresh.Autoreweight` / `Autoreweight3` (score-based reweighting) are kept for comparison only: the new Acc AI and
the balance constants replace them without needing scores.
