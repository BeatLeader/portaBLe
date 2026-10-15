# Changelog: portaBLeRebalancer

The `loloppe-rebalancer` branch of portaBLe, plus the rating changes in the RatingAPI and Analyzer submodules. The Acc AI
model comes from `beatsaber-replays-ai-2`.

The goal is ratings that never need scores at rating time. Scores, replays and fail attempts are only used offline, to fit
and validate constants.

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

### Performance

- **New `ScoresPlayersRefresh`:** recalculates scores and players in a single pass, about 3× faster. `RefreshEverything`
  uses it.
- **Faster `RatingsRefresh.Overwrite`:**
  - only the stored modifiers are rated;
  - RatingAPI loads only the difficulty being rated;
  - inference batches are larger (4 → 512).
- **Quieter logs:** the database columns are checked before being added, so `ALTER TABLE` errors are no longer printed.

### Tools and cleanup

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
