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
- **Hard map correction of the acc rating.** Below 96.75% predicted acc, the acc rating uses a predicted acc lowered by 1.24×
  the distance below 96.75% (at most 2.17%, reached at 95%). The stored predicted acc and the curves don't change.
  - Why: top players' acc fell further behind the Acc AI's prediction on the hardest maps (−1.1% at 96%, −1.5% at 95.4%,
    after removing each player's own level), so their pp stopped rising past about 12 stars.
  - Simulated on every score: top 100 players' median pp by star band now keeps rising past 11 stars (586, 601, 604, 614
    instead of 578, 565, 571); 208 of 3886 Standard maps change; top 1000 rank correlation 0.98.

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
