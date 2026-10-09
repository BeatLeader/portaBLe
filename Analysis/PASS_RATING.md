# Pass rating v2: energy bar + re-weighted swing difficulty

Analyzer branch **`pass-energy`** (beatleader-analyzer 7b232d3). It is opt-in: `PassEnergy.Model = Energy`, RatingAPI
`"PassModel": "Energy"`, portaBLe `--pass-model Energy`. The classic rating stays the default and is always available as
`Ratings.ClassicPassRating`. Tech rating and the acc model's `pass` feature keep using the classic rating.

## Yardstick: pass difficulty measured from attempts

`Analysis/py/a19_pass_attempts.py`. A Rasch model on 24 M clean attempts (no modifiers, fails vs clears; 4.7 M player–map
pairs, 51.6 k players): `logit P(fail) = b_map − skill_player`. `b` is how hard a map is to pass with player skill removed; its
reliability is 0.999 on 3 732 maps.

**Deliberate fails do not distort it** (`a27_fake_fails.py`). A "fail button" mod, or walking into a wall to bail on a bad-acc
run, would show up as fails the energy bar cannot produce.
- **Impossible fails are rare.** A real fail needs at least 4 misses, bad cuts or bombs (the bar starts at 50 %; each costs at most
  15 %) or a wall hit. Of the 5.37 M fails that have counters, 0.08 % have 3 or fewer without a wall, and 0.23 % were failed by walls
  alone. They are spread over 7 k players (the top 20 make 14 %) and cluster on wall-gimmick maps.
- **The old records without counters behave like real fails.** These are the 2022-03 … 2023-08 uploads without client data
  (13.5 % of clean clears and fails, 8.1 % of fails), where every counter reads 0. Their accuracy at the fail is the same distance below the player's own clears
  on that map as for checked fails (0.756 vs 0.850, against 0.729 vs 0.819).
- **Refitting b without them changes almost nothing.** Without the suspect fails: correlation with today's b 0.9999, 3 maps move
  by more than 0.5 logits. Without the counterless records as well: correlation 0.9992. Pass v2's R² goes 0.9166 → 0.9183, and
  *Feral* E+ stays #3.
- **What it cannot see:** a run failed on purpose by missing four or more notes looks like a real fail. A run abandoned through the
  pause menu is a quit or restart, which b does not count.

Every variant is scored the same way:
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

## "Where players fail" tab (leaderboard page, `algo-b-v4`)

It sits next to "Where accuracy is lost" in the same card. `#pass` in the URL opens it, `#pass-attempts` with the
real-attempts overlay.

`export_pass_profiles.py` stores, per map, every swing's `ln PassDiff` (one byte, step 0.03) and note count, the swings per
~5 s section, the map's threshold skill, and the causes. The page runs the exact energy-bar DP for the chosen level (1–3 ms per
level). It shows:
- **Pass levels around the map's rating,** each with its chance to clear.
- **Per section:** the chance that a player who reaches the section fails in it, plus the riskiest section and the share of
  fails in the first minute.
- **Causes:** the top three per section in the tooltip, and the map-wide list. Each cause is a factor's log contribution to
  `PassDiff` above a typical swing, weighted by the swing's miss chance at the map's pass level.
- **Hints (ⓘ and "What do these mean?")** explaining every factor, on both tabs.
- **The page's modifier selector (SS / FS / SF)**, which shows the same map at that speed. Each swing's PassDiff gets speed × k,
  its low-speed falloff and the NJS buff at NJS × k, exactly as in the analyzer. There is no attempts overlay for modifiers. The
  speed cause is labelled "Speed (eBPM and reach)": eBPM, counted up to 2× higher for long reaches.

**Typical attempt** compares the model with the observed fail hazard of real clean attempts (3 950 maps).
- Real attempts are a mix of players: weaker ones fail early, so later sections see only stronger ones. The chip therefore
  models skills `~ Normal(μ, 0.3)` (9-point Gauss–Hermite), with μ set so the mix clears as often as the real attempts did.
- The spread 0.3 is fitted on 299 maps (`a25_attempt_mix.py`):

  | spread | mean squared hazard error | first-third share of fails, model − observed |
  |---|---|---|
  | 0 (one skill) | 0.00108 | −0.28 |
  | 0.2 | 0.00062 | −0.14 |
  | 0.4 | 0.00069 | +0.01 |

- With it, *Speedcore Paradise* fails cluster at 0:12–0:17 as observed (93 % of fails in the first minute), and *Metamorphose*'s
  single burst at 2:02 matches the attempts.

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

### Fatigue inside the energy model: tested, not adopted

`a24_fatigue.py` gives each hand a stamina reservoir of capacity C, the same for every map (a reference player). It drains by
the unit-fixed stamina-dev cost of every swing and refills fully in 4 minutes. A depleted hand makes its swings count as
harder: `ln PassDiff + φ · depletion`.

On a 900-map sample, no capacity (2 000 … 40 000, i.e. 0.3 … 5.6 on the stamina-rating scale) and no strength
(φ 0.1 … 1) improves the fit:
- R² stays at 0.917 where depletion is nearly constant (C = 2 000, so it changes nothing);
- it drops everywhere else, down to 0.88–0.59 for strong fatigue.

The attempts agree that fatigue is not a visible driver.

| map length | fail risk per tenth of the map | quit / restart risk per tenth |
|---|---|---|
| < 2.5 min (1 381 maps) | 0.053 → 0.022 | 0.158 → 0.033 (and 0.082 in the last tenth) |
| > 4 min (656 maps) | 0.092 → 0.033 | 0.218 → 0.022 … 0.037 |

Both are highest at the start and fall through the map, in short and long maps alike. Long maps show no late rise in fails
or in quits. What makes long, dense maps harder to pass, longer exposure and drain, is already in the energy bar. v2 / v4 use
no fatigue term. The stamina rating as a whole adds only ≈ 0.003 R².

### eBPM instead of swing speed: tested, not adopted

`a26_ebpm.py` (details in `ALGO_ACC_TEST.md`) replaced PassDiff's speed part. Swing speed is `eBPM / 30 × distanceDiff`.
- **eBPM alone:** R² 0.9125 vs 0.9166, with 532 vs 500 maps off by more than 1 logit.
- **eBPM × distanceDiff^α:** α 1 (today) is optimal; α 0.5 gives 0.9150 and α 2 gives 0.9165.
- **Raw eBPM without reset doubling:** best with a reset term of ×2.0, at 0.9134.

How far the hand travels to the note matters for passing on top of how often it swings.

## Remaining v2 outliers

At the top of the list:
- *Calamitous Demise* E+ (v2 #1, attempts #31, 2.1 logits too high) and *Godspeed* Expert (v2 #2, attempts #46, 1.9 too high;
  rated above its own E+, which attempts put level with it) are over-rated. They are not a diagonal-term bias: the diagonal share
  does not predict v2's error at any speed.
- *Superluminal* E+ is under-rated: v2 #9, hardest of all by attempts.
- *Feral* E+ (v2 #3) is right. It is #3 by attempts too: 8 301 clean attempts, 95 % failed, and clear rates match the model at
  every skill level. It has only 8.2 notes per second, but its swings are fast (median swing speed 9.5, twice the pool median),
  12 % of them are crossovers (98th percentile), and many cuts are horizontal. Crossovers, tech and horizontal/diagonal cuts are
  the terms the classic rating missed (#43). The best players clear it first try (44 of the 200 strongest attempters who tried),
  as they do every map on the list. Players fail it late: the median fail is at 1:34 of 4:00, where the other top maps' are at
  0:25–0:56.

### Community check (2026-10-09)

Players flagged pass ratings on `algo-b-v5`. Checked against the attempts-based pass difficulty `b` (rank of 3 732 maps):

| map | v2 | v2 rank | attempts rank | residual | players said | attempts say |
|---|---|---|---|---|---|---|
| *Extratongue* E+ | 15.18 | 14 | 4 | +0.37 | should be on the first page | yes |
| *Dual Doom Deathmatch* E+ | 15.23 | 13 | 29 | −0.75 | not as hard as Extratongue | yes |
| *414 PER SPEED* E+ | 15.06 | 15 | 25 | −0.54 | | |
| *SLIDE THE BPM (UP TO) 420!!* E+ | 14.86 | 16 | 19 | −0.05 | | |
| *Godspeed* Expert / E+ | 16.85 / 15.44 | 2 / 8 | 46 / 39 | −1.93 / −1.07 | about equal to pass | yes: b 4.95 / 5.00, the classic rating had them equal |
| *kannabis kultivation* E+ | 13.37 | 41 | 181 | −0.98 | easier than *Sound Chimera* | yes: *Sound Chimera* `4cbd0` / `2c00e` are #118 / #139 |

- **Godspeed Expert vs E+.** With the horizontal and diagonal terms off, both rate exactly 13.17. The whole gap is the
  diagonal ×1.29 on Expert's 500-eBPM streams (48 % diagonal against 40 % on E+).
- **Extratongue falls behind.** 414 PER SPEED (67 % diagonal) and Dual Doom gain +2.5–2.8 from the cut-direction terms.
  Extratongue's 500-eBPM streams are 22 % diagonal with no horizontals, so it barely gains and drops behind them. 73 % of its
  fails happen at 0:15–0:30.
- **Kannabis.** Every v2 term lifts it, and they multiply in its 5:47 section, where the model puts a fail spike. Real fails are
  spread out (median 2:20, busiest 15 s only 25 %). It is 4-wide at 294 eBPM: "Speed" in the pass tab counts reach as well as eBPM
  (now labelled "Speed (eBPM and reach)").
- **SS plays are not in either number.** v2 is computed from the map; `b` uses unmodified attempts only. Extratongue's
  scores are 36 % SS, but `b` uses its 6 987 unmodified attempts.
- **The attempts do not favour spiky maps.** The classic peak-window rating over-rated them (spikiest fifth −0.31 logits,
  flattest +0.21); v2 is neutral (+0.03 / −0.12).

Tested fixes (`a28_fast_diagonals.py`), none adopted:
- **Cut-direction terms only below 200–300 eBPM, or fading out between 200 and 400:** these fix most of the named maps (Godspeed
  Expert −1.93 → −0.3, 414 → +0.7, Kannabis → +0.25). But overall R² drops to 0.907–0.913, with 523–574 maps off by more than
  1 logit against 500.
- **Terms skipped or halved on straight-back reversals (flowing diagonal streams):** R² 0.910–0.915, also worse.

At speed and in flow, diagonal and horizontal cuts are harder to pass on average. These maps are exceptions to that, and a
fix will need something more specific than the cut direction.

### Why they fail there: attempt replays (`a29_attempt_replays.py`)

The source is the attempt replays of the 12 maps above. They sit in R2 (`otherreplays`), read with the server's keys, since the
public API only serves them for players with public attempts. The sample is 3 843 fail and 773 clear replays, clean attempts only
(clears are rarer because personal-best clears move to the scores bucket). ReplayStudy's `--attempts` mode matched every note event
to the analyzer's swings, with bad-cut flags and bombs.

- **Fails are energy fails from note mistakes.** Replaying the game's energy rules on the note events (start 50 %, +1 % per hit,
  −10 % per bad cut, −15 % per miss or bomb) reaches 0 in 99 % of fail replays; walls matter only on *Dual Doom* (5 %). The final
  drain (the mistakes after the bar last stood at 50 %) is 69–81 % misses, 13–25 % wrong-direction cuts and 3–13 % wrong-saber cuts.
  "Too slow" never happens.
- **Where.** On the speed maps, swings at 350 eBPM or more take 55–92 % of the fatal mistakes but are only 9–40 % of the swings
  played. *Extratongue* dies at 0:23: its early 500-eBPM burst hits while the bar is still at 50 %, where 4 misses end the run.
  *Godspeed* Expert and E+ both die at 0:48, in the same section:
  - Expert: 80 % of fatal mistakes are diagonals, which are 34 % of its swings.
  - E+: 66 % diagonals (38 % of swings), plus crossovers (14 % vs 7 %) and long reaches (22 % vs 14 %).

  *Kannabis* has no fast section; its fatal mistakes are diagonals (51 % vs 27 %), horizontals and crossovers, spread from 1:30 to
  5:48 (median 3:19).
- **Which swings get missed** (mistakes on a swing type ÷ the same run's average, so skill and survivor selection cancel; clears
  only, because fail runs collapse in their fatal section):

  | swing type | ×, clears only | v2's term as miss odds |
  |---|---|---|
  | crossover | 3.05 | ≈ 3.2 (×1.59 difficulty) |
  | long reach (distanceDiff ≥ 1.35) | 2.2 | partly in the swing speed |
  | v2 tech factor ≥ 2 | 1.93 | |
  | horizontal | 1.49 | ≈ 2.2 (×1.37) |
  | diagonal | 1.18 | ≈ 1.9 (×1.29) |
  | ≥ 450 eBPM vs < 150 | 1.33 vs 0.73 | |
  | v2's per-swing difficulty, top vs bottom fifth within the map | 1.43 vs 0.62 | many times more |

What this means for v2:
- **The per-swing allocation is roughly right.** v2 ranks the swings where mistakes happen correctly, crossovers and the most
  technical swings are as costly as it says, and diagonals cost less than its ×1.29.
- **The per-swing contrast is too steep.** The slope 2.5 was fitted to map pass rates. Within a run, hard and easy swings differ
  far less than it implies, so the energy simulation puts too much fail risk on a map's hardest section. That fits *Kannabis*,
  whose modelled 5:47 spike is absent in real fails, and *Godspeed* Expert, whose diagonal stream gets every multiplier.
- **Next step:** fit the per-note model (slope, swing terms, maybe a run-level skill spread) directly on attempt replays of all
  maps, instead of on map pass rates.

Elsewhere: *Extraterrestrial* E+ (still too low), *Merry-Go-Round* Normal, *Toymatic Parade* Hard, *PISSCORD* Hard, *iLLness LiLin* Expert,
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
