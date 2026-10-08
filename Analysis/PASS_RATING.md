# Pass rating v2: energy bar + re-weighted swing difficulty

Analyzer branch **`pass-energy`** (beatleader-analyzer 7b232d3). It is opt-in: `PassEnergy.Model = Energy`, RatingAPI
`"PassModel": "Energy"`, portaBLe `--pass-model Energy`. The classic rating stays the default and is always available as
`Ratings.ClassicPassRating`. Tech rating and the acc model's `pass` feature keep using the classic rating.

## Yardstick: pass difficulty measured from attempts

`Analysis/py/a19_pass_attempts.py`. A Rasch model on 24 M clean attempts (no modifiers, fails vs clears; 4.7 M player–map
pairs, 51.6 k players): `logit P(fail) = b_map − skill_player`. `b` is how hard a map is to pass with player skill removed; its
reliability is 0.999 on 3 732 maps. Every variant is scored the same way:
- R² of a cubic fit of the rating to `b`;
- the number of maps off by more than 1 or 2 logits.

## Results

| pass rating | R² | maps off by > 1 logit | maps off by > 2 logits |
|---|---|---|---|
| classic (production) | 0.856 | 861 | 134 |
| classic + window weights fitted (8…512 swings) | 0.867 (held-out) | | |
| classic + per-swing terms fitted | 0.895 (held-out) | | |
| classic + both | 0.902 (held-out) | 551 | 55 |
| energy bar on classic SwingDiff (1 constant) | 0.881 | 743 | |
| **energy bar + per-swing terms + One Saber factor (v2, C#)** | **0.917** | **500** | **47** |
| … + stamina (stamina-dev, unit-fixed), linear add-on | ≈ 0.920 | | |
| fitted benchmark: ridge on 87 map features (song-grouped CV) | 0.936 | 337 | 26 |

v2 closes about three quarters of the gap between the classic rating and a black-box fit. It stays an algorithm with named
constants, so an outlier can be traced to a term and fixed.

## What changed and why

**Per-swing difficulty (`PassDiff`).** It is `SwingDiff` with:

| term | value | evidence |
|---|---|---|
| stress (angle strain, repositioning, rotation) | ×6.52 inside the stress multiplier | tech rating correlates +0.35 with the classic rating's error |
| crossover (red in the rightmost lane, blue in the leftmost) | ×1.59 | strongest single cause of the error, r +0.49 |
| horizontal cut | ×1.37 | r +0.28 |
| diagonal cut | ×1.29 | |
| parity break (reset) | ×0.86 | resets were over-counted: `SwingFrequency` already doubles on them |

**Aggregation: the game's energy bar instead of peak windows.**
- Every note is missed with probability `sigmoid(2.5 · (ln PassDiff − skill))`.
- Energy starts at 0.5, gains 0.01 per hit note and loses 0.15 per missed note; the attempt fails at 0.
- The clear probability is computed exactly (dynamic programming over the energy in 1 % steps). The rating is the skill at
  which a player clears half the time, `exp(skill₅₀) × 0.2332`. That scale keeps today's numbers: median 4.78 vs 4.73,
  p99 13.4 vs 13.4.
- The only fitted constant of the aggregation is the slope 2.5. The rest are game rules.

This handles bursts naturally (a short spike is survivable on a full bar; the classic peak windows overpay them, r −0.21) and
so does sustained difficulty (the fitted window weights moved to 256 swings). It also explains why fail risk is highest early in
a map: energy starts at 50 %.

**One Saber.** The classic formula halves maps whose other hand has no swings (×0.5), which made all 53 One Saber maps
1.8 logits harder to pass than rated. With no nerf they come out 1.4 logits over-rated; ×0.74 centres them. It applies by
rule (a hand without swings), so the two Standard-mode "Awe (One Saber)" maps get it too.

## Where players fail

At each map's own pass threshold, the energy model's predicted fail positions match the observed fail hazard better than
`SwingDiff`: median within-map Spearman 0.52 vs 0.45 on 1 195 maps, better on 58 % of them (`a23_energy_where.py`). The same
DP can draw a "where players fail" curve per map, next to the acc-loss view.

## Stamina (analyzer branch `stamina-dev`)

`StaminaCalculator` models physical fatigue: a kinetic-energy cost per swing (swing rate², path strain, resets ×2, holding the
arms up), and the smallest capacity that never runs out with 4-minute regeneration.

- **Likely unit bug.** The port multiplies `SwingFrequency` by BPM / 60 to get swings per second. In the current analyzer
  `SwingFrequency` is already per second (`1 / Δseconds`, `SwingCreation.cs`). The older `stamina-calc` branch used a per-beat
  frequency. The as-is rating correlates +0.61 with BPM.
- **With the units fixed** it predicts pass difficulty better: alone R² 0.798 vs 0.766, on top of the classic rating
  0.869 vs 0.861, and on top of the energy model about +0.003.
- It carries real but small extra information (endurance). It is not part of v2 yet; a natural place would be a skill drift
  inside the energy simulation.

## Remaining v2 outliers

*Extraterrestrial* E+ (still too low), *Merry-Go-Round* Normal, *Toymatic Parade* Hard, *PISSCORD* Hard, *iLLness LiLin* Expert,
*een vliegtuig* E+ (now too low), *Romantic Homicide* E+ (rated 0.03: the analyzer finds almost no swings), and
*Ascension to Heaven* Expert (too high). Per map: `b`, classic, v2 and both residuals are in `out/pass_v2_maps.csv`; the fitted
variant constants are in `out/a21_pass_variants.json`.

## Reproduce

```bash
python Analysis/py/a19_pass_attempts.py ...      # pass difficulty b from attempts, fail hazard per section
python Analysis/py/a21_pass_rating.py ...        # classic reproduction (passlib.py), window / per-swing variants, held-out
python Analysis/py/a22_energy_pass.py ... --a21 a21_results.json --a21-kind swing   # energy bar, stamina port
python Analysis/py/a23_energy_where.py ...       # One Saber factor, predicted vs observed fail locations
RatingsDump ... --pass-model energy              # C# ratings (matches the Python prototype: median 0.17 % difference)
```
