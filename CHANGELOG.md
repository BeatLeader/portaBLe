# Changelog: portaBLeRebalancer

The `loloppe-rebalancer` branch of portaBLe, plus the rating changes in the RatingAPI and Analyzer submodules. The Acc AI
model comes from `beatsaber-replays-ai-2`.

The goal is ratings that never need scores at rating time. Scores, replays and fail attempts are used offline, to fit and
validate constants. The one exception is the acc reweighter (below), the only tool that changes ratings from scores.

## Cleanup

- Removed what today's attempts left unused: `TopShapeFitter` and its `topshape` command, the ceiling raise v1's target
  knots / scale / `TargetAt`, and `PpFormula.OneSaberNerf` (the OneSaber nerf below the reference, 0 since its fit).
  Pp unchanged. The acc predictor / AI correction (a map-data correction of the Acc AI's predicted acc) was tried in
  analysis only (held-out: worse under 6 stars, per map often wrong at 8+) and dropped; it never had code here.

## Pass light bonus: tech fade (on)

- The pass light bonus fades out as tech rating rises from 5 to 8 (the tech top bonus's range), so pass-light acc maps get
  it and tech maps get the tech top bonus instead, with no double counting; stronger for the rest: x1.07 (+0.03 near the
  predicted acc), was x1.04 (+0.02). `PpMultiplier` 1.0045 (was 1.0064): the median player kept (593).
- `exp-tech2-ar.db` -> `exp-passlight2-ar.db`: tech 6.5+ minus tech < 5 maps' same-player pay at 7-12 stars about even
  (was +0.9..+1.2%); pass-heavy minus pass-light +2.1 / +2.1 / +0.6 / +1.0 -> +2.2 / +2.5 / +0.3 / +0.1 (top 100 /
  101-1000 / 1001-10000 / 10000+); pass-light 4-6 star maps -1.9 -> -0.8% (top 1000 -3.9 -> -2.6%), 10-12 stars -0.1 ->
  -0.7%; top end 804 -> 810 (800+ 2 -> 1); top 10 -0.04%, #1000 -0.4%; #1-play outliers 10 -> 12.
- Left: tech 6.5+ maps' Top 5 vs stars back to -1.6 to -4.4% (the pass light bonus had been closing it); changes Hard
  (4.94 stars, pass share 0.06) 786 -> 810, the top play, and 700+ plays under 5 stars 19 -> 21.
- Then (`exp-passlight3-ar.db`): `TechTopBonus` back to 0.04 (the overpay had been the pass light bonus's) and the pass
  light bonus faded in from 4 to 5.5 stars (was 3 -> 4); `PpMultiplier` 1.0055 (median player 593). Tech 6.5+ maps' Top
  5 vs stars -4.4 / -2.2 / -1.6 / -2.2 / -1.8 / -1.6 -> -3.1 / -1.5 / -0.8 / -1.9 / -0.7 / -1.1% (7-8 ... 12+ stars),
  their same-player pay still even; changes Hard 810 -> 790, 700+ plays under 5 stars 21 -> 15; top end 801 (800+ 2:
  Let Mom Sleep Hard SF, Dream of Winds E+); pass-light 4-6 star maps back to -1.7% (top 1000 -4.0%), the cost of the
  fade-in; pass-heavy minus pass-light +2.4 / +2.6 / +0.4 / +0.4; #1-play outliers 11; no new slope drop.

## Pass light bonus (on)

- Pass-light maps (less pass than typical for their stars) get up to x1.04 acc pp at every acc, plus up to +0.02 more
  near and above the predicted acc, faded in from 3 to 4 stars and out from 11 to 13: it cancels the Acc AI's wrong
  prediction on those maps, from map data only and outside stars (`PpFormula.PassLightMultiplier`, nomod speed only, not
  OneSaber; lightness against `PassShareByStars`, the typical pass share and its spread by stars).
- Why: same-player pay, pass share fifths per star band, pass-heavy minus pass-light +5.1 / +4.9 / +2.6 / +2.7 points for
  the top 100 / 101-1000 / 1001-10000 / 10000+. Every group has the same predicted acc (98.0%) and so the same curve, but
  players land 0.75% further under it on pass-light maps (top 1000 -2.27 vs -1.52%). Not the base curve, not the acc
  relative pass pp (flat pass pp, `exp-passflat-ar.db`: +4.2 / +4.7 / +3.5 / +3.8). A pass rating change can't fix both
  ends: a low acc play has more pass share than stars at 96%, a top play less.
- `PpMultiplier` 1.0064 (was 1.0128): the median player's pp kept (593).
- On `exp-lownote.db`, acc reweighter + ceiling raise + rescore (`exp-techtop-ar.db` -> `exp-passlight-ar.db`): heavy -
  light +1.6 / +2.1 / +0.6 / +1.0; pass-light alone -2.8 / -2.8 / -1.7 / -1.6 -> -0.6 / -0.9 / -0.3 / -0.6%; speed maps vs
  mid +1.1 / +1.2 / +0.3 / +0.8 -> +0.2 / +0.7 / -0.2 / +0.1; 4-6 stars still -1.9% (top 1000 -4.0%) on pass-light maps;
  top end 819 -> 813 (800+ 4 -> 2); top 10 -0.4%, #1000 -0.2%; #1-play outliers 10 -> 12; under 5 stars 700+ plays 19 ->
  19 (changes Hard, 4.94 stars, pass share 0.06, 753 -> 786); no new slope drop.
- Overlap: pass-light maps at 8+ stars are mostly tech maps, which also get the tech top bonus: tech 6.5+ maps went from
  even to slightly over (same-player pay +0.5..+0.7%, top 1000 +0.8..+1.3% at 8-12 stars; Top 5 vs stars about 0); the
  reweighter's tech share slope +0.0069 -> +0.0138, its pass share slope +0.0023 -> -0.0047.
- TechTopBonus then lowered 0.04 -> 0.02 (`exp-tech2-ar.db`): tech 6.5+ maps' same-player pay unchanged (+0.4..+0.7%,
  so that part comes from the pass light bonus), top 1000 +0.6..+1.1% (was +0.8..+1.3), Top 5 vs stars back to -0.5 to
  -2.0% (tech < 5 maps +0.5..+1.0); top end 804 (800+ 2), the median player 593 without a PpMultiplier change.

## Tech top bonus (on, 0.04)

- The Acc AI predicts too much acc on tech maps: players land 0.07 / 0.19 / 0.43% further under it than on their other
  maps of those stars at tech 5-6 / 6-7 / 7-9 (about linear from tech 5), where the curve is steepest, so the world's best
  plays (#1 to #10 alike) paid 2-5% under same-star maps (Top 5 vs stars, tech 6.5+ maps: -4.2 / -2.9 / -2.2 / -2.5 /
  -1.8 / -1.1% at 7-8 / 8-9 / 9-10 / 10-11 / 11-12 / 12+ stars, tech < 5 maps +0.6 to +2.4%), while players' pay against
  their own plays stayed about even.
- `PpFormula.TechTopMultiplier`: acc pp x (1 + 0.04 x ease(tech 5 -> 8) x fade(stars 11 -> 13) x ease(acc from predicted
  - 3% to predicted)), the mid star top bonus's shape (on up to 100%). Not in stars. A pure power of the curve (the top
  tilt's) was tried first: it closed a third as much for x1.08 at 100%, the top plays sitting just under the predicted acc.
- On `exp-lownote.db`, acc reweighter + ceiling raise + rescore, without / with it (`exp-techbase-ar.db` /
  `exp-techtop-ar.db`): tech 6.5+ maps' Top 5 vs stars -4.2 -> -2.5 (7-8), -2.9 -> -1.4, -2.2 -> -1.1, -2.5 -> -1.1,
  -1.8 -> -0.4, -1.1 -> -0.9 (12+), the gap to tech < 5 maps about halved; same-player pay and worth unchanged; maps'
  best play spread within their stars -0.2 to -0.4 points at 7-12 stars; the median player 593 unchanged, top 10 +0.3%,
  #100 +0.2%; plays 700+ 1198 -> 1295, 800+ 2 -> 4 (the top end 805 -> 819: Let Mom Sleep Hard, 6.53 stars, tech 6.7,
  790 -> 819; Come to Life Expert 8.28 stars 802); #1-play outliers (x3+) 12 -> 10; no new slope drop on the 1148 maps
  it touches. The ceiling raise raises fewer maps with it (1005 -> 958).
- `ceiling <db> [Setting=value ...]` takes the ceiling raise's settings (PeerMargin=0) and pp formula ones. PeerMargin 0
  (raising to the peers' median) didn't move tech maps' Top 5 vs stars: their peers rise with them.

## Ceiling raise v2: maps under their peers (on)

- `CeilingRaise.Run` (`ceiling <db>`; in the acc reweighter's step: `accreweight ... rescore`, the autoreweighted
  FullPipeline; no longer in `rerate` or the plain pipeline, since it reads scores): each Standard map's amazing-play acc
  is its best top-1000 nomod acc (corrected to the median map's number of contenders) blended with the map-data model's
  prediction (weight n / (n + 10)). Maps whose ceiling (pp there) is under their peers' (Standard maps within 0.5 stars,
  30+) median - 3% get a pure power of their acc curve from predicted acc - 2% up (no change under it), reaching the
  target at the ceiling's acc, a 100% play at most x1.2. Faded in by stars from 4.5 (none) to 5.5 (full): maps under ~5
  stars stay under ~700. Every other map is unchanged: it raises maps that are under, not a global buff.
- `TopTiltCap` 3: loose enough that the tanh bound stays a plain power (the raise only grows with acc, no slope drop).
- On `exp-lownote-ar.db` -> `exp-ceiling2-ar.db`: 1002 maps raised (acc pp at their ceiling x1.022 median, x1.088 at
  most); maps' best play spread within their stars 10.5 -> 9.8% (5-6), 9.7 -> 8.9% (6-7), 8.4 -> 7.7% (7-8), 7.1 ->
  6.4% (8-9) with the median +5 and the 90th percentile and max unchanged; maps with a 700+ best play 657 -> 677; the top
  end 802 -> 805 (800+ plays 1 -> 2); the median player 593 unchanged, top 10 +0.07%; under 5 stars 700+ plays 18 -> 19;
  #1-play outliers 12 -> 12; no new slope drop on the pp grids.
- Without the star fade, plays at 700+ under 5 stars went 18 -> 48 (Easy maps in the top 100's top 20 4.2 -> 7.5%).

## Empirical stars (Leaderboards page, Ratings view)

- `Leaderboard.EmpiricalDifficulty` / `EmpiricalStars` (`LeaderboardsRefresh.RefreshEmpiricalStars`, in Refresh and
  `balancemetrics`): a star-free yardstick from scores, a diagnostic. logit(acc) = skill - difficulty fitted at the 75th
  percentile (nomod, players with 50+ plays among the top 20000); empirical stars: the median stars of the 81 maps of
  most similar difficulty. Split-half reliability 0.998, correlation with stars 0.988.
- Ratings view: "Empirical ★" and "★ - empirical" (highlighted from +-0.5). Speed maps read +0.4 stars over (pass
  difficulty isn't in it, by design), low note maps -0.36. The maps under their peers by stars are the same by empirical
  stars (97% overlap): the ceiling raise's targets aren't from mis-starred maps.

## Ceiling raise from map data (v1, replaced by v2; its target knots and scale removed)

- `Refresh/Tools/CeilingRaise.cs` (generated from `balance-analysis/goals/ceiling_fit.py`), `ceilingfeatures` / `ceiling`
  commands: a ceiling model predicts from map data alone where an amazing play lands against the predicted acc (held-out
  R^2 0.48, stars only 0.16); maps whose ceiling (pp there, x1.10 to the specialists' level) is under a target by stars
  (at most 690 up to 4.5 stars, then 800 minus the model's uncertainty: 737 at 5.5, 758 at 7.5, 779 at 9.5, 788 from
  11.5) get a raise-only top tilt (the top tilt's machinery), replacing the mid star top bonus.
- **Was off** (`TopTiltCap` 0, mid star top bonus kept): on a real run the top end went 802 -> 985pp (2287 plays 800+), maps'
  top plays vs their stars spread 8.6 -> 14.9%, the top 10 +21% pp (the median player unchanged). The ceiling acc sat
  under real top plays (the x1.10 was applied in pp, not in acc), which got the tilt's x1.3 bound instead of the target,
  and the bound's saturation lowers the curve's slope there (a bump) on 2959 maps.

## Top tilt from specialists (tried, removed)

- `Refresh/Tools/TopShapeFitter.cs`, `PpFormula.TopTiltMultiplier`, `Leaderboard.TopTilt` / `TopTiltAnchor`, `topshape <db>`:
  per map, a smooth tilt of the acc curve's top (no change under the map's other players' median acc, a power of the
  curve over it, every play's change bounded by `TopTiltCap`), fitted on each kind of map's 40 best specialists so the map
  pays its best players like their other maps. Runs after the acc reweighter (`accreweight ... rescore`, FullPipeline).
- **Off** (`TopTiltCap` 0): on a real run the top 100's pay spread across maps went 5.47 -> 5.58%, maps' top plays vs
  their stars 8.58 -> 8.61%, the nomod top end 786 -> 817pp, and the specialists lost 150-226pp. The emulation's 36% of
  the held-out gap removed was mostly a bias (the panel's better plays sit over the other players on every map), and
  the per-map part reads a specialist's lucky plays as overpaying maps.
- `pp`, `ppshape` and `ppat` check the database schema first (they failed on databases without the newer columns).
- Removed 2026-11-06 (`TopShapeFitter.cs`, `topshape`): the tilt machinery (`TopTiltMultiplier`, `TopTilt` /
  `TopTiltAnchor`) stays for the ceiling raise, whose `CeilingRaise.Clear` now clears the tilts before the reweighter.

## Low note nerf off (Quiet Water Easy 100% = 600pp)

- **`CurveSettings.LowNoteRefMultiplier` / `LowNoteMinMultiplier` 0.8031 -> 1** (the length nerf and the low note speed
  blend stay). Nothing short of 1 reaches 600: the nerf scales the whole curve, so stars move with it (Quiet Water Easy
  2.08 -> 2.51) and the low star bonus falls. For the same reason it never changed what low note maps pay against maps of
  their stars (-8.6% for the same players, left as is: buffing it creates outliers where top players haven't played).
  - Checked for maps a top player hasn't played yet (`ppat`: every version's pp at given accs): at the predicted acc
    + 0.15 points (above 97% of the top 100's low note plays, whose median is -0.49) no low note version passes the best
    existing play of normal maps of its stars (max 642 -> 670pp); at + 0.30, 6 -> 29 do (mostly SS on 3-star Easy maps at
    99.7-99.9%, by ~10pp), 7 over 700pp.
  - Real recurve + acc reweighter: Quiet Water Easy 100% 497 -> 600pp, low note maps' best play 678 -> 693pp, their 600+
    plays 22 -> 56; RTX 20000 Easy (20 notes, 616-626pp at 99.6-100%) became the best play of 1.6-2.2% of its players
    (mostly ranked ~150-1000); the top 1000's average pp and the 700+ / 800+ counts unchanged.

## Acc reweighter 2.2x faster, same results

- The player-adjusted worth fit (`BalanceTuner.PlayerAdjustedItemValues`), ~93% of the reweighter's time, took each
  group's, map's and distance bin's 75th percentile by sorting its residuals on every one of its 30 iterations; it now
  selects the two values it needs (quickselect, linear). Same values: every reweight multiplier identical. Full
  reweighter 6:41 -> 2:58 (the re-score after it unchanged, ~1 min).
- Not done (would change results slightly): the fit hasn't converged at 30 iterations (the largest map term still moves
  ~0.025% per iteration), so stopping early or starting from the previous candidate's fit isn't free.

## Leaderboards page: column views and balance metrics

- **Views** (Overview / Ratings / Worth / Balance / Map) swap the metric columns after the map, difficulty, tags and stars,
  kept in the URL with the sort and filters. Overview has pass / acc / tech in one cell (each sortable); Worth draws Mean,
  Median, P75 and P90 on a -15% ... +15% strip next to the spread.
- **Emulated top play** (`EmulatedTopPp` / `EmulatedTopAcc`, with the map's best nomod play `TopPlayPp`; Overview and
  Balance views, and on each map's page): the pp a play at the predicted acc + how far above it the world's best land on
  maps of its stars would get (median of best acc - predicted acc over maps with 10+ top 100 plays, per star: +0.12-0.20
  points at 2.5-4.5 stars, +0.41-0.47 at 9.5-12.5, -0.10 at 14.5), at most 100%, no modifier. For maps nobody strong has
  played: median 654pp, highest 795 (The Purple Dimension), above the actual best on 68% of maps (+32-38pp at 4-8 stars,
  +4-15 at 12+); OneSaber maps have the most room (Ashed Wings E+ best 653, emulated 792).
  - **Best specialist top play** (`SpecialistTopPp` / `SpecialistTopAcc`, next to it): at the acc the best specialists of
    the map's kind reach (1-star band x pass / tech share terciles; of its 3 best players by their 5 best plays there, the
    highest median best acc - predicted acc over their 10 best plays, at least the typical offset). The typical one is a
    median: Bizzy825 beat it on 36% of their plays (up to +18.8%), Bytesy 21%; the specialist one 9% (at most +11.2%) and
    3% (+4.6%), oermergeesh never. Median 696pp, highest 853 (-Totsuka no Tsurugi- E+ and Godspeed Expert, whose best
    plays are 683 / 678: the Acc AI's wrong prediction there).
- **Balance metrics** (`LeaderboardsRefresh.RefreshBalanceMetrics`, in every refresh; `balancemetrics <db>` fills older
  databases in ~11 s), nomod, players with 50+ ranked plays, maps with 30+ of them: `Top20Share` / `Top50Share` (share of
  its players with it in their top 20 / 50), `Top5Relative` (its 5 best plays vs maps within 0.5 star with half to twice
  its plays), `PlayersRankRelative` (its players' median rank vs maps of its stars: top 20 shares and top 5 depend on who
  plays a map) and `AccVsPredicted` (each player's acc - predicted acc there minus on their other maps of those stars).

## OneSaber: half the pass per hand, acc pp buff instead of the one saber nerf

- **`ONE_SABER_NERF` removed** (Analyzer, was 0.343: OneSaber pass x0.657): each hand is half of the pass, pass x (1 +
  hands ratio) / 2, so OneSaber pass x0.5. Standard maps with one-handed parts move with it.
- **`PpFormula.OneSaberMultiplier`** (RatingAPI, not in stars): OneSaber acc pp x1.08 above a reference of predicted acc
  minus 2 x (1 - predicted acc) (eased over +-2%), faded in from 6 to 8 stars (`OneSaberBuff` / `ErrorOffset` / `Width` /
  `BuffStart` / `BuffFull`; `OneSaberNerf`, below the reference, is 0).
  - Measured per score against the player's own Standard plays at the same stars. With the old nerf, OneSaber paid
    ranks 1-100 / 101-500 / 501-2000 / 2001-5000 / 5001-15000 / 15001+ -4.0 / -4.6 / -1.1 / -0.1 / +1.3 / +3.1%; at 50%
    per hand -5.2 / -6.0 / -2.6 / -1.8 / -1.0 / 0.0%. The Acc AI overestimates OneSaber acc more on harder maps (players'
    median 1-2% under it on 3-4 star maps, 8-13% on 9-12 stars), so a nerf below the predicted acc itself hit the hard
    maps everyone plays far under it (0-5 / 10+ stars +7.6 / -9.3%). With the lowered reference, a buff above it alone
    evens the ranks out; the fit wanted no nerf below it. The fade keeps easy OneSaber maps (already +5%) from new top
    plays (Wheels on the Bus Easy 660 -> 699pp without it).
  - Emulated: ranks -0.7 / -2.0 / +0.7 / -0.4 / -0.7 / 0.0%, all OneSaber -1.7 -> -0.3%; top OneSaber plays per star
    band under Standard's 99.99th percentile (7-9 / 9-11 / 11+ stars 633 / 637 / 648 vs 708 / 723 / 757pp). Left: by
    map (0-5 stars +5%, 10+ -5%), for the acc reweighter.
  - Balance tuner: the one saber knob is gone; `RatingConstants.OneSaberNerf` 0.5 records what a database was rated with.
- **`OneSaberTopBuff` 0.1** (tried 0.3 first, below): OneSaber acc pp also x1.3 above the predicted acc itself (eased over +-2%), faded in from
  4 to 6 stars (`OneSaberTopStart` / `Full`). Judged on the project's goals instead of "pays like Standard of the same
  stars": OneSaber maps were in as many of their players' top 20 as Standard maps of their stars, but their top 5
  plays were 6.7% under (only 4 of the 63 top 100 players with a OneSaber play had one in their top 20; best one median
  590pp vs their #20 655). From a lower reference (predicted - 0.5 x its error) a OneSaber map became the best play of
  10-23% of its players (Standard's 99th percentile 2.8%). Without the fade, Wheels on the Bus Easy / Higher Ground
  Easy made 772 / 728pp.
  - Real rerate + acc reweighter, against Prod Autoreweighted: top 5 plays -6.7% -> -1.1% of Standard maps of the same
    stars (under 6 stars still -9.6%), top 20 shares the same (+0.4 points), no map worth nothing, best play shares at
    most 4.7% (unchanged), every star band's top OneSaber play under Standard's 99.99th, OneSaber 700+ plays 0 -> 1, no
    800. Top 100 players with their best OneSaber play in their top 20: 4 -> 9 (101-500: 27 -> 30), its median 590 ->
    602pp. Players with a OneSaber play: ranks 1-100 / 101-500 / 501-2000 +13.6 / +13.2 / +7.3pp on average, 2001-10000
    / 10001+ -3.9 / -9.7pp (their OneSaber plays were overpaid by the old nerf).
  - 0.3 overshot: top 5 and top 20 shares depend on who plays a map, and each top 100 player got 2.8% more from a
    OneSaber play than from their own Standard plays at those stars (5.3% near the predicted acc). 0.1, real rerate +
    acc reweighter: per player ranks 1-100 / 101-500 / 501-2000 / 2001-5000 / 5001-15000 / 15001+ +1.0 / -1.0 / +1.1 /
    -0.6 / -0.9 / -0.4% (Prod -4.3 / -3.9 / -0.2 / +0.7 / +1.3 / +2.4%), top 5 -6.7% -> -4.5%, top OneSaber play 690pp,
    no 700+. OneSaber maps are played by stronger players than Standard ones of their stars, so their low top 5 is real.

## Acc reweighter: low star maps reweighted too

- **`AccReweighter.MinStars` removed** (was 4.326: maps under it kept ×1), and with it the low-star top play nerfs
  (`MinStarsNerfs`), which only ran on those maps. Low star maps now go through the worth fit like the others;
  `LowStarTolerance` and `LowStarMaxChange` still soften their nerfs up to 4.33-5.23 stars.
  - Impact (Dev Acc Mid Star Top Bonus v3, against its autoreweighted DB): under 4.33 stars the multipliers' sd
    0.4-0.9% -> ~2% (3-4 stars mean +0.75%, x0.93-x1.12; Crying for Rain Easy, worth +21%, x0.999 -> x0.929). Above, 7+
    stars move by 0.36% sd (the largest buffs a little smaller), 9.5+ +0.05% on average. Plays over 600 / 650 / 700 /
    750pp 31,731 / 6,701 / 814 / 68 -> 31,781 / 6,728 / 813 / 67 (+6 over 700pp under 4.33 stars). Players with 5+ low
    star plays in their top 100: ranks 1001-10000's pp curves smoother for 66% of them, the others' about the same.

## Parity and swing directions from a model trained on replays

- **The Analyzer's parity is a model** (Analyzer `BeatmapScanner/Parity/MlParityModel.cs`), trained in `beatsaber-replays-ai-2`
  `paritymodel/` on the top 10 nomods replays of 8,142 Standard leaderboards (ranked and the Acc AI's unranked lists): the
  players' forehand / backhand at every cut (calibrated per replay and hand, left-handed replays mirrored back), their cut
  direction, and whether their saber reversed between two cuts. A bidirectional GRU over each hand's swings with a map
  style vector (3 models averaged) and gradient boosted trees give per swing scores and per swing pair scores of keeping
  the parity, decoded with Viterbi, so a hand's parity is consistent from start to end. Map data only at rating time.
  - Test split (1,219 maps, songs never trained or tuned on), against `ParityPredictor`: swing pair errors 1,966 -> 782
    (1,275 -> 310 where 90%+ of the players agree; half of the rest is where players split), 110 maps better / 17
    worse; palm-up sections 90.1% -> 94.9% of the swings right; down -> dot -> down errors 397 -> 93.
  - **Merged swings**: where the players hit two Analyzer swings with one (fast dot streams, 3,790 pairs; the Analyzer's
    grouping is otherwise 99.5% precise / 98.5% complete), the swings are joined before swing creation (56.5% found at
    97.3% precision) instead of being a reset.
  - **Swing directions**: every swing takes the model's direction (Analyzer `ModelDirections`; arrows kept within their
    cut tolerance, 0.05% clamped), 10.6 -> 5.1 degrees mean error against the players' cut directions. Single dots keep
    their per-position arc offset.
  - The Analyzer's default (`ParityModel.Current`; both hands are preprocessed first), with ONNX Runtime and the model
    files embedded in the Analyzer (`parity_nn.onnx` 14 MB, `parity_trees.gzbin` 2 MB). `ParityPredictor` stays as the
    fallback for hands too short for the model and for the training data export. 114 ms per map (a map's result is
    reused for its speed modifier ratings).
  - Tools in the Analyzer's Benchmark project: `paritymaps` (the model's map data, exported with `ParityPredictor`),
    `parityml-verify` (the C# port against Python: tree inputs bit-exact, the same parity on every reference swing),
    `paritybench`. portaBLe: `parity=dp` on `rerate` / `paritycheck` rates with the old predictor.
- Recalibrated for the model's directions (real cut directions turn about 16 degrees between swings on a plain down / up
  stream, find more linear swings and register some of a linear run's travel):
  - Rotation: small turns kept with almost no weight, rotation x (1 - e^(-(rotation / 30 degrees)^3)) (16 -> 2.3, 45 ->
    43.5, 90+ whole) (`SwingMovement`).
  - `LINEAR_PASS_SLOPE` 0.375 -> 0.15 (worth slope by linear share +0.054 -> -0.021), `ONE_SABER_NERF` 0.474 -> 0.343.
- Analyzer (Loloppe): angle strain without its soft cap, tech averaged over the hardest half of the swings (was 75%).

## Raw ratings: no power to look like prod's

- **Tech** = `BALANCED_TECH_SCALER` x swing tech x pass factor, without the power 0.8015 and the top compression above 15;
  **acc** = `ACC_SCALE` / curve, without the power 0.8222; **stars** = pp of a 96% score / 83.6925, without
  1.1653 x ()^1.17507. Acc and tech pp are linear in them (`PpFormula.AccRatingPower` / `TechRatingPower` removed).
  - Scales: `ACC_SCALE` 13.5382 -> 14.8727 and `AccWeight` 21.584 -> 34.519 (the same median acc rating and acc pp),
    `BALANCED_TECH_SCALER` 27.08 -> 43.948 and `TechWeight` 0.0979 -> 0.13657 (the same tech pp), then
    `BALANCED_TECH_SCALER` 10 with `TechWeight` 0.587 (balance tuner: tech share slope 0).
  - Star thresholds converted, new = (old / 1.1653)^(1 / 1.17507) (7 stars -> 4.6, 4 -> 2.86); widths by the local
    slope: curve power ramps, the mid star top bonus, the speed blend (midpoint 6 -> 4.03, width 1.25 -> 0.72), the acc
    reweighter's low star bounds and bandwidths, the balance tuner's worth kernel, in-range margin and distance bins, song
    recommendation windows. Low star bonus refit on the new stars: 0.01948 x (5.504 - stars)^1.98. The balance tuner and
    leaderboard refresh use half-star bands.
  - Multipliers on the acc rating move pp by themselves now, not to the power 1.2163: the hard swing correction (slope
    0.02 -> 0.0243, cap 0.12 -> 0.148) and the acc reweighter's bounds (`MaxChange` 0.11 -> 0.1322, `LargeGapMaxChange`
    0.16 -> 0.1911, `LowStarMaxChange` 0.10 -> 0.1203) keep their pp. Score modifiers multiply the raw ratings (a +4%
    modifier: +4% acc / tech pp instead of +4.9%).
  - `TechTopKnee` 9 -> 9.7 on the linear tech at the 43.948 scale (median about 9.4, p99 about 39), 2.207 at the 10 scale
    (the same maps compressed): tech share slope -0.010 at 8, +0.0085 at 11.1, +0.037 without the compression (43.948
    scale).
- Rerated Dev Acc copy (not uploaded), with the parity model, its directions and the Analyzer
  changes above: balance tuner worth spread 0.03153 (tech slope +0.0015, pass +0.0047, OneSaber +0.0001), against
  0.03137 with the parity model and the Analyzer's old directions. Pass unchanged (median 4.54), acc median 8.73.
- **`StarDivisor` 83.6925 -> 54**: stars about prod's numbers again (7 prod stars -> 7.1 instead of 4.6). Every star
  threshold above x 83.6925 / 54 = 1.54986: curve power ramps (very low 3.35-5.17, low 6.89-9.33), the mid star top bonus
  (6.25-7.13-7.99-9.24), the speed blend (midpoint 6.25, width 1.108), the acc reweighter's low star bounds (4.43-5.35,
  `MinStars` 4.43) and bandwidths (0.867), the balance tuner's worth kernel (0.217), in-range margin and distance bins
  (0.433), the worth rank neighbourhood (0.433), song recommendation windows. Low star bonus 0.008181 x (8.5304 -
  stars)^1.98 (the same bonus). The balance tuner and leaderboard refresh are back to 1-star bands (13+); the tuner's
  700pp+ count is under 9 stars.

## Tech top compression from the 89th percentile, no very high pass buff

- `TechTopKnee` 2.207 -> 4.5 and `TechWeight` 0.587 -> 0.47: the knee sat at the median tech, so half the maps were
  compressed and the highest tech ones underpaid (P75 worth, top 10% of tech -0.6 to -0.8%, players ranked ~10000 -1.6 to
  -2.7%). Now -0.4 to +0.5% (top 100 about 0), the 75-90th percentile +0.8%, under the median -0.5%; worth RMS 0.0308 ->
  0.0312. Knee 3.5 left the top under, 6 overpaid it.
- `HIGH_PASS_BUFF` 0.001 -> 0 (off): pass 13.5+ maps about +0.6% -> +0.2% over others of their stars.
- Tried with `reacc` on copies of the 83.69-star rerate (`StarDivisor` 83.6925). The balance tuner then suggested
  `TechWeight` 0.361 (tech share slope +0.016 -> 0, worth spread 0.03191 -> 0.03190); a real `reacc` with it left the top
  3% of tech -1.0 to -1.5% and 700pp+ scores 620 -> 491, so 0.47 stays.
- Uploaded (rerated with `StarDivisor` 54): "Dev Acc ML Parity" db-20261102035049-4276.db and "Dev Acc ML Parity
  Autoreweighted" db-20261102035820-7858.db (acc reweighter worth spread 0.0319 -> 0.0218).

## Low star speed blend again, from an 80% to a 20% nerf

- `RatingsController.BlendLowStarSpeeds` back (reverting `adc76fb`), with the share of a speed modifier's difference from
  nomod kept going from 20% on the lowest maps to 80% on the hardest (was 0% to 100%): `LowStarSpeedMinShare` 0.2 +
  0.6 x the sigmoid (midpoint and width on the new stars, above).

## Tech from rotation^1.5 and a soft capped angle strain

- **Swing tech = soft capped angle strain + 4.649 × rotation^1.5** (Analyzer `AngleStrain` `STRAIN_SOFT_CAP` 1.5,
  `AnalyzeMap` `ROTATION_TECH_SCALE` / `ROTATION_TECH_POWER`; was strain + rotation). Strain is 1.5 × tanh(strain / 1.5):
  ordinary swings unchanged (45 degrees off natural 0.25 → 0.24), the extreme ones held under 1.5 (160 degrees 3.16 →
  1.46). Large turns count more than small ones (on average twice the plain rotation). The pass rating's stress keeps the
  plain strain (`SwingData.AngleStrainTech` is tech's). `BALANCED_TECH_SCALER` 20.611 → 14.707 keeps the tech scale (p50
  3.84, p90 7.36, p99 12.43).
  - Why: the top of the tech ratings went to extreme wrist angles over turns (Sanity Krush ExpertPlus, 77% of its tech
    from inverted forehands 160 degrees off). Now stasis ExpertPlus leads (17.41, then Sanity Krush 16.69, I Love It,
    Sacrifice of Fools, Konpeito Extremists); FAR OUT, #1f1e33, PISSCORD and Official move into the top 10, D/N/A,
    Medicine Expert and Gravisphere Crisis down. Tried first: a lower strain weight (Sanity Krush first down to 0.3),
    soft caps alone (it stays first), tech over fewer swings, linear strain (low star maps' tech up).
- Refit for it (P75 worth): `PpFormula.TechTopKnee` 12 → 9 and `TechAccExponent` 3.27 → 3.2 (tech 9-14 maps 1.2-2.2% over
  without; tech share slope +0.009 → +0.002), `PpMultiplier` 1.0199 → 1.0299 (players' pp back: #1000 15074, 15093
  before), `HIGH_PASS_BUFF` 0.0025 → 0.001 (pass 14.5+ maps +1.8-1.9% over after the tech refit, now +0.9-1.1%).
- Dev Acc: P75 worth RMS 0.0292 → 0.0295 (5-8 stars 0.0228, 8-11 0.0267, 11+ 0.0284), the cost of the new order; with
  the acc reweighter 0.0211 → 0.0213. Uploaded as `db-20261030224654-3714.db` and `db-20261030224738-6540.db`
  (autoreweighted).

## Worth from the 75th percentile

- **`BalanceTuner.PlayerAdjustedQuantile` 0 → 0.75**: worth fits each player's level, each map's pay and the distance
  term to the 75th percentile of the scores' residuals instead of their mean, so it follows each player's better efforts
  on the map, which is what their pp is made of. `Leaderboard.Worth`, the SS / FS / SF worths, worth by player rank, the
  balance tuner and the acc reweighter all use it; `WorthMean` keeps the mean. The balance tuner's and acc reweighter's
  outputs record the measure as "PlayerAdjusted P75".
  - Why: the mean also follows the low effort tail. It tilted worth by note count (maps with twice the notes +0.015 at
    8-11 stars, +0.018 at 5-8 stars), while at P75 the tilt is gone from 5 stars (+0.000 / +0.004 / −0.006 by band).
    Turning the Acc AI's farmability scaling off flattened the mean but overcorrected P75 and inflated pp (about +3.8%).
  - Rerated Dev Acc copy (`worth`, no re-score): every map's worth now equals its P75 column.
  - Databases refreshed before this keep the mean in `Worth` until `RefreshWorth` / `RefreshEverything` runs on them.
- **Acc reweighter retuned for it:** `NoisePerScore` 0.28 → 0.10, the P75 worth's measured noise per score (new
  `worthnoise` command, `BalanceTuner.WorthNoise`: each map's worth from two random halves of the players; 0.102, the
  mean's 0.126). `WorthLimit` 0.03 → 0.025, `LargeGapFrom` / `LargeGapFull` 0.04 / 0.08 → 0.033 / 0.066 and
  `ResponseNoise` 0.02 → 0.016: × 0.82, the P75 worth's spread against the mean's.
  - Why: 0.28 was 2.2× even the mean's measured noise, and with P75 it held back gaps of 4.5× the noise: the reweighter
    moved maps less than on the mean (mean multiplier 1.2% against 1.6%).
  - Dev Acc rerated, then reweighted (P75 worth RMS, unreweighted 0.0304): old settings 0.0236, only the × 0.82 scaling
    0.0232, `NoisePerScore` 0.15 0.0225, 0.10 0.0222 (5-8 stars 0.0137 → 0.0107, 8-11 0.0158 → 0.0136, 11+ 0.0165 →
    0.0132); held-out spread unchanged (0.0092-0.0094), 2173 maps changed (1488), mean multiplier 1.7%. The guard
    tolerances scaled the same way changed nothing.

## Corrections checked again on the P75 worth

Each correction's variable against the P75 worth of the rerated Dev Acc (new parity, before the acc reweighter), every
change tried alone first (`reacc`, new: acc ratings from the stored predicted acc with any `AccRating` correction constant
or pp formula setting, then re-score), then together. The low star corrections were left as they were.

- **`PpFormula.TechAccExponent` 3.5 → 3.27**: tech 9-13 maps were 1.3-1.6% over the others of their stars, tech / stars
  0.75-1.2 at 10+ stars 1.3-1.9% over; now 0.8-0.9%. The balance tuner kept `TechWeight` (tech share slope +0.006, within
  its tolerance); 0.131 would have done the same as 3.27, with a slightly higher spread. Tech isn't rescaled: its
  distribution didn't move (p90 7.25 → 7.32), only the maps that lost false resets went up (top 18.0 → 20.6).
- **`HIGH_PASS_BUFF` 0.0055 → 0.0025**: pass 14+ maps were 1.4-2.3% over; now 0.6-1.3%.
- **`HIT_DISTANCE_EASY_SHARE` 0.2** (new; was 0): a fifth of the hit distance correction stays above 97.5% predicted acc.
  Easy maps over 0.7 m were 1.1% under; now 0.2%.
- **`HARD_SWING_KNEE` 2 → 1**: the maps just past the knee were still 0.3-0.7% under; now about 0. A slope of 0.03 instead
  left them under. With the lower tech exponent the 700pp+ scores spread over more maps (447 → 484, at most 26 → 15).
- Together: P75 worth RMS 0.0304 → 0.0292 (5-8 stars 0.0232 → 0.0225, 8-11 0.0268 → 0.0258, 11+ 0.0300 → 0.0280), players'
  pp about the same (#1 −0.4%, #1000 −0.5%), 700pp+ scores 878 → 866.
- Tried for the highest tech maps (Sanity Krush ExpertPlus: 77% of its tech is angle strain, from inverted forehands
  160 degrees off their natural direction), neither kept:
  - Arrows at the angle players cut them (offsets fitted on the replays: players cut 10-25 degrees towards the parity's
    natural direction): tech p90 7.32 → 6.64, the tech 9-12 overpay mostly
    gone, but the P75 worth RMS 0.0293 at best (`TechAccExponent` 3.4) against 0.0292; in rotation too, 0.0295.
  - A softer strain past a knee (90-120 degrees, x0.25-0.5 above): top tech
    15.7-16.2, RMS 0.0294. Neither changed what those maps pay (Sanity Krush stays at 12.8 stars, P75 +0.045-0.048):
    tech pp above 12 counts a third, and their stars come from the acc rating.
  - Also tried and dropped: angle strain linear in the angle (strain became 83% of tech everywhere, low star maps'
    tech up), a lower strain weight (Sanity Krush stays first above 0.3), repositioning in tech, parity comfort scaled by
    the gap between swings (Wommotion read right, but more swings wrong elsewhere).
- Checked and kept: the slider tilt (flat at every slider share), the hard map correction (`HARD_MAP_SLOPE` 1.6 only moved
  96-96.75% from −0.0076 to −0.0063 and added 158 700pp+ scores), the pass pp compression. Not settled: hit distance
  0.62-0.7 m under 97% predicted acc stays 0.8% under whatever the correction's strength (its shape, not tried). The mid
  star top bonus can't be checked on worth (worth is against maps of the same stars).
- **`ONE_SABER_NERF` 0.39 → 0.474** (Analyzer): the balance tuner's suggestion on the P75 worth (OneSaber gap +0.010 →
  0). The nerf applies to every map by how one-handed its pass is, so it also lowers the pass of 2138 Standard maps with
  one-handed parts (up to −12pp on a score); the P75 worth spread of Standard maps is unchanged (0.0292).
  Its `RatedWith` defaults now match the Analyzer (were pass 0.7721 / one saber 0.44, the Analyzer's 0.7115 / 0.39), and
  the new `balancetune` command runs it on any database.
- **Tech rating top compression, display only** (`Ratings.TechRatingKnee` 15, `TechRatingSlope` 0.53, Analyzer): above 15
  each point of the tech rating counts 0.53, so the top stays near 18 (Sanity Krush ExpertPlus 20.64 → 18.0, SONG
  REQUEST 19.74 → 17.5) after the parity rework lifted the maps it had read with false resets. `PpFormula.TechPp` undoes
  it before its own compression (`Ratings.UncompressTech`), and `PpCalculator.ScorePp` scales the uncompressed rating by
  the modifiers' multiplier (NA's −0.3), so tech pp and stars are unchanged: a rerate without it gives
  the same pp on every score (at most 0.0001pp apart, players 0.002pp). Databases rated before it
  need a rerate (their stored tech isn't compressed, and the pp formula would now uncompress it).

## Acc reweighter: maps with a thin worth kept at ×1

- **`AccReweighter.MinWorthScores` 40**: maps whose worth before the reweight rests on fewer in-range scores
  (`BalanceTuner.WorthEvidence`) keep ×1, like those under `MinNotes` (and `MinStars`, since removed). Their scores still count.
  - Why: the noise dead zone already shrinks their own gap to almost nothing, so they were moved only by the slope
    correction terms and the star range centering. A map just over the worth measure's 20 score minimum also loses its
    worth once its stars or its players' levels move, so the Leaderboards page showed a multiplier on a map without a
    worth: The Purple Dimension ExpertPlus (16 stars, 461 scores, about 20 in range) ×1.0087, worth before +0.007,
    target +0.007, worth after none.
  - Dev, 28 Oct (estimated on the stored worth): 14 of 3955 maps with a worth, which had moved 0.7% on average (5.2%
    at most).

## Song recommendations: easier maps by default, Easier maps slider

- **Easier weight 50 → 100** (`RecommendationOptions.EasierWeight`): most suggestions are now under the player's level
  (median −0.34 stars against +0.20), and they find as many of the maps that went on to enter players' top 50
  (`songreceval`, 200 players, samples 7 / 11: +0.4 [−0.8, +1.6] / −0.7 [−2.0, +0.6] points). Before the star window,
  Easier 100 found fewer (−1.9).
  - Other ways under the level cost more: level offset −0.5 / −1: −1.3 / −3.4 points (median −0.14 / −0.50); a window
    reaching only 0.75 / 0.5 / 0 stars above the level: −1.0 / −2.2 / −6.1 (median −0.11 / −0.24 / −0.56).
- **Neutral players** (`RecommendationOptions.NeutralPlayers` = 3, `SongScorer`): each candidate's Style is pulled
  toward the pool's mean as if 3 more linked players had it there, (style × n + mean × 3) / (n + 3), n its distinct
  linked players. One peer sharing many seeds with the player linked each of their top plays from every shared seed,
  outliers outside the player's skillset included, above maps several peers agree on; such a map now keeps a quarter
  of its pull. A safeguard: as many of the maps that enter players' top 50 found (`songreceval neutral`, 200 players,
  samples 7 / 11, k 1 / 2 / 3 / 5 / 8: +0.2 / +0.1 / +0.1 / −0.1 / −0.4 and −0.1 / −0.1 / 0 / 0 / −0.2 points, every
  interval across 0), the suggestions' median 0.03 stars nearer the level.
- **Easier maps slider** on the page, 0 to 100 (100 by default): 0 for suggestions at the level or a little above.
- **SmartSongSuggest's link distance removed** (`LinkedPlayerFinder`, `RecommendationOptions.LinkKeepPercent`): its
  pruning was already off (keep 1), as the peers are the scorers nearest the player's pp. Also tried and not kept
  (`RecommendationEvaluation`): peers ranked by that distance (|(peer pp / player pp)^e − 1|, e 1 / 2.5 / 5: within
  ±0.2 points), and a Skillset factor favoring maps of the pass/acc/tech balance of the player's best plays (+0.2 /
  +0.3 points at weight 25, less above, and suggestions pulled under the level).

## Linear maps: pass buff

- **Pass × (1 + 0.375 × (linear share − 0.3))** above a 0.3 linear share (Analyzer `AnalyzeMap.LINEAR_PASS_KNEE` /
  `LINEAR_PASS_SLOPE`, after the tech, which doesn't change): ×1.08 at 0.5, ×1.12 at 0.62.
  - Why: linear maps went from overweighted in prod to underweighted in Dev. Player-adjusted worth of speed / midspeed
    maps of 8+ stars by linear share 0-0.15 / 0.15-0.25 / 0.25-0.35 / 0.35-0.45 / 0.45+: +0.000, +0.006, +0.003,
    −0.006, −0.024 (prod +0.022 at 0.45+), in every pass and tech range; DA'AT Expert −0.067. Linear swings get almost no
    stress: repositioning only counts movement across the swing, and they have no rotation or angle strain, though the
    arm travels the whole height along the swing every time.
  - Tried and not kept: the hand's path instead of the distance between entries (pass +22% on every map, nothing left
    for linear maps once the median was back); the travel along the swing as stress on linear swings, with or without a
    ramp over the run (the most linear maps fixed only by overpaying the 0.25-0.45 ones, whose hardest windows are
    linear too, and a wider worth spread).
  - Impact (Dev, 26 Oct, no acc reweight): by linear share +0.000 → −0.001, +0.006 → +0.005, +0.003 → +0.003, −0.006 →
    −0.001, −0.024 → −0.003; worth slope on linear share above 0.3 −0.092 → −0.005; pass 13+ maps −0.021 → +0.003;
    worth spread 0.0377 → 0.0376; tech-share gap −0.001 → −0.004; DA'AT Expert −0.067 → −0.018 (the rest: players
    score 1.15 points under the Acc AI's prediction there). Speed modifiers' worth unchanged. Pass median +0.3%.

## Low note and length nerf on the whole curve, stars on prod's median

- **Low note and length nerf, on the whole acc curve** (`CurveSettings.LowNoteMultiplier` = `LowNoteFactor` ×
  `ShortFactor`): each is 1 at most, so one of them nerfs a bit, both nerf more and nothing is buffed; a multiplier of the
  whole curve, so it stays smooth (the gain cut above the predicted acc bent it there). Notes: ×0.95 from 20 notes down,
  none from 115 (on ln). Length: ×0.90 from 12 s down, none from 75 s (on ln), in real time, so FS and SF are nerfed
  more (4% of ranked maps under 75 s nomod, 6% FS, 11% SF). The length bonus and the gain cut are gone. 100% on Quiet
  Water Easy (20 notes, 18 s): SS 609, nomod 604 (×0.876, was ×0.80), FS 598, SF 591 (×0.855, was ×0.74); RTX 20000
  Easy (20 notes, 111 s) 614 (×0.95, was ×1.055), so the longer map pays more. A 45 s map with 400 notes ×0.972 (was
  ×1), SF ×0.95. Dev, from a 5% length cap at 18 s: stars of the short maps ×0.994, the short low note ones ×0.982,
  the rest unchanged; top 1000 unchanged. The speed blend is unchanged.
- **Stars ×0.9606, prod's median** (`PpFormula.StarScale` 1.2131 → 1.1653): Dev's ranked stars had drifted above prod's
  (median 7.26 vs 6.97, p99 14.14 vs 13.19), now the same median (6.97; p99 13.59, p1 2.18 vs 1.39, not fitted). The star
  thresholds pp reads are ×0.9606 too (`LowStarCurveStart` / `End` 6.7242 / 9.606, `VeryLowStarCurveStart` / `End`
  2.8818 / 4.803, `LowStarBonusEnd` 8.6454) and `LowStarBonus` 0.006 → 0.0063371 (÷ 0.9606^1.36): every score's pp is
  unchanged.

## Song recommendations: 9x faster, same suggestions

A run took 3.3 s (median of 12 players over the ranks, `songrecbench`), 0.38 s now; `songreceval` 10 players 67 s → 4 s.
Every player's suggestions are the same (the benchmark's digests, songreceval's table).

- **About 1,000 queries became about 80** (`DbBeatLeaderClient`): the peers' top plays in one query
  (`IBeatLeaderClient.GetPlayersTopScoresAsync`), each seed leaderboard read once and kept for the run (the seed
  selection and the peer search asked for the same ones), the seed selection on the plays the pipeline already has.
- **Plain SQL, score columns only**: no EF query, change tracking or join of each score to its leaderboard and player;
  the songs come from a catalog of the file's leaderboards loaded once per database file (`LeaderboardCatalog`,
  reloaded when the file changes, warmed at startup for the main database).
- **Memory-mapped reads** (`PRAGMA mmap_size`): halved the queries' time.
- **Server**: a run stops when its request is aborted; at most half the cores' runs at once (the others wait);
  the last 32 runs are kept 10 minutes, so the playlist download after the page doesn't run it again.
- Tried and not kept: indexes on Scores (PlayerId, Pp) and (LeaderboardId, Pp). Those queries were 10-20x faster, but
  rewriting every score's pp took 43 s against 2 s (the rating pipeline does it on each refresh), and each index adds
  130 MB per database.

## Song recommendations: peers, easier maps, a level where players gain

Measured with the new `songreceval` (holdout: each sampled player's 40 most recent scores hidden, 200 players over
ranks 1-40000): of the maps that went on to enter the player's top 50, how many were suggested (gainRecall), the
difference with the current defaults player by player, 95% interval. Tuned on one sample of 200 players (seed 7),
checked on another (seed 11): the earlier algorithm 9.6%, the current defaults **26.4%**, the popular-maps baseline
13.7%.

- **Linked players are the seed's peers** (`LinkedPlayerFinder`, `IBeatLeaderClient.GetLeaderboardScoresNearPpAsync`):
  on each seed song, the 20 scorers whose pp is nearest the player's own (by pp ratio), instead of its 20 best scorers.
  - Why: the best scorers are far stronger than all but top players, so the suggestions were their top plays: median
    1.9 stars above the player's level (balance tuner's), 4.6 for players ranked 15000-40000. Peers +6.2 points
    [+4.6, +7.8]; ranked 2000-40000 1.8% → 12.1% with the change below. The top scorers are no longer an option.
- **Overweight removed** (`SongScorer`, the page's Overweight weight): SmartSongSuggest's second factor, the average rank
  of the candidate over its links. The port used each linked player's placement on its leaderboard; their position in
  their own top plays (SmartSongSuggest's meaning) did better (+1.7 [+0.2, +3.2]), but once Easier was on any weight on
  it found fewer (it favors harder maps), so the factor went.
- **Easier maps favored** (`ScoringWeights.Easier`, the page's "Easier maps weight"; `SongScorer`): a factor
  1 / (1 + e^(stars above the player's level / 1 star)) in the same 0.5-1.5 range as Style, neutral at the level.
- **Player level: the median stars of the 50 best plays** (`RecommendationPipeline.PlayerLevel`, with their speed
  modifiers; shown on the page, with each suggestion's stars against it). It was the balance tuner's level, the 5th
  hardest of them: the top of the player's range, a star or more above what they usually play (Loloppe 10.92 → 9.68).
  - Where players gain (holdout, 274 players): the maps that entered their top 50 were +0.11 stars from the median
    (median), −1.26 from the 5th hardest, and each player's median gain varied less between players (sd 0.75
    against 0.89). The 10th hardest −0.74 / 0.81, the mean +0.05 / 0.70 (within noise of the median, which a few lucky
    passes don't move).
- **Level offset** (`RecommendationOptions.LevelOffset`, the page's slider, −3 to +3 stars by 0.25): the suggestions
  aim at the player's level plus the offset. It moves the peers (the players about that much weaker or stronger: their
  pp on each seed is the player's times aimed level / level, pp growing about in proportion to stars), the Easier
  factor's center, and the new star window.
  - Moving only the Easier factor's center moved the suggestions' median 0.15 stars per star of offset; with the peers
    too 0.3-0.5. With the window 0.6-0.7 (offset −2 / −1 / +1 / +2: −1.44 / −0.71 / +0.58 / +1.24; 100 players).
- **Star window** (`RecommendationPipeline.StarWindow` 1.5): suggestions are maps within 1.5 stars of the aimed level
  (every candidate when none is). 79% of the maps that entered players' top 50 were within it of their level. At
  offset 0 it changes nothing measurable (−0.1 [−2.0, +1.7] points; 1 star −1.2 and less responsive offsets, 2 −0.3).
  The page shows the range.
- **Few or no ranked plays**: 57% of the players have 1-4 ranked plays. New players simulated (their first 1 / 3 / 5 /
  10 / 20 plays kept, 100 players each, share of their next 40 plays suggested): 6.3 / 6.2 / 7.8 / 8.9 / 8.6%, against
  7.8 / 6.7 / 7.3 / 7.7 / 8.0% for the most played maps around their level (better than the suggestions with 1 play,
  worse with 10). Under `RecommendationPipeline.FewPlays` 10 the page says the level and suggestions are rough. With
  none (or an unknown id) it says so instead of "No seed scores found". The seeds no longer leave out the weakest
  quarter of the plays (SmartSongSuggest's): that only did anything under 100 plays, where it found slightly fewer
  (3 plays: +0.7 [+0.1, +1.3] points without it).
- **Weights tuned** (`RecommendationOptions`): Style 100, Easier 50 (were Style 50, Overweight 50), the best of
  Style x Easier 0-100 by 25: the suggestions' median +0.3 stars from the level, where the gains are; without Easier
  +1.3. On the fresh sample Easier +2.6 [+1.0, +4.1] before the star window, +1.1 [−0.2, +2.4] with it (the window
  does part of its job). **Link keep
  % 1** (was 0.5): with peers the links are already close, so pruning threw evidence away (+1.7 [+0.5, +3.0]).
- **Top-K split and tuned**: it was both the peers linked per seed and the top plays taken per peer. Peers x plays
  10 / 20 / 40: more plays found more, more peers barely; plays 20 / 40 / 60 / 80 / 120: 0 / +2.0 [+0.8, +3.1] / +1.4 /
  +1.6 / +0.8 points. Now 20 peers per seed (`PeersPerSeed`) and 40 plays per peer (`PlaysPerPeer`). **Seed count**
  stays 50: 25 / 75 / 100 −1.4 [−2.6, −0.3] / −0.8 / −1.2 [−2.2, −0.1].
- **Nothing to tune on the page**: seed count, Top-K, Style and Easier weights and link keep % are fixed at these
  values (`RecommendationOptions`). The form is the player id, the level offset slider and Exclude already played.
- **Seeds panel**: after a run the page lists the seeds used (the player's plays, ticked) and the rest of their plays
  (collapsed, with a filter). They can untick seeds, tick other plays, add any ranked map (search by song, mapper,
  leaderboard id or BeatLeader link, `OnGetSearchLeaderboardsAsync`) and recommend again; reset goes back to the
  automatic seeds, and another player id starts from theirs (`RecommendationOptions.SeedLeaderboardIds`,
  `AddedSeedLeaderboardIds`). Seeds are never suggested back.
  - A map added without a score links the scorers whose overall pp is nearest the player's
    (`IBeatLeaderClient.GetLeaderboardScoresNearPlayerPpAsync`; the player's pp from their plays,
    `RecommendationPipeline.ProfilePp`). Weaker than their own score: every seed linked that way found 20.9% against
    26.1% (−5.2 [−6.8, −3.6] points; the most played maps at their level 13.1%).
- **Results**: cover, song (to its leaderboard page), mapper, speed modifier, difficulty, stars, stars against the aimed
  level, how many peers play it and their average pp on it. The score and factor columns are gone.
- **Tried and rejected** (same evaluation): one candidate per chart with its speed-modified plays merged −0.3
  [−1.0, +0.4], seeds ordered by leaderboard percentile instead of the top 100 +0.2 [−0.8, +1.1], Style and
  Overweight normalized by rank instead of min-max −12.3 [−14.5, −10.1] (Style then pulls the suggestions 1.3 stars
  under the player's level). Worth as a factor (Python prototype, 50 players) didn't help either.
- **Super Faster Song read** (`SpeedModifierExtensions.ParseSpeedModifier`): stored as `SF`, it was only read as `SFS`,
  so 106k SF scores were linked and shown as unmodified plays.
- **One row per chart**: a chart suggested with and without a speed modifier only keeps its best scored one
  (`RecommendationPipeline`). The self-link check compares leaderboards, so a seed played with another speed modifier
  by the linked player no longer comes back as a candidate. The Stars column shows the speed modifier's stars
  (`Song.ModifierStars`).
- **`RecommendationPipeline`**: the page's pipeline (seeds, candidates, already played, scoring) in `SongRec/Core`,
  shared with `songreceval` (`SongRec/Tools/RecommendationEvaluation.cs`).
- **Removed** `MapRecommendation/` (the earlier SmartSongSuggest port) and `SongRec/Export/HtmlReportWriter.cs`, neither
  used anywhere.

## Acc reweighter: low note maps kept at ×1

- **`AccReweighter.MinNotes` 115** (`CurveSettings.LowNoteStart`): maps with fewer color notes keep ×1. Their scores still
  count in every player's worth and the balance.
  - Why: their worth is low because players can't reach the steep top of their curve (Naughty little demon / Your Best
    Friend Normal, 43 / 47 notes: worth −0.13 / −0.17, best scores 98.6-99.3%), not because of their acc rating, so a
    buff only raised what a 100% pays: a 100% SF on Your Best Friend Normal 1165 → 1292 pp (the best score anywhere 816).
  - Dev, 24 Oct: 64 maps kept at 1; pass 1 strength 0.70 as before, top play limiter 249 maps (243), worth spread after
    the limiters 0.0252 → 0.0267 (the most negative maps aren't corrected any more), balance guard passed (pass share
    slope margin 0.0007 → 0.0008), top 1000 16306 → 16313.

## Map tags and inlines

- **New `Statistics.Inlines`** (Analyzer `AnalyzeMap.CountInlines`): a note close enough behind another note in the same
  column and row to be hidden by it, either color, at most `INLINE_MAX_GAP` 1/4 beat after it (the map's beats, so the
  speed modifier doesn't change it; under `INLINE_MIN_GAP` 0.001 beat, the same time, not counted). Vibro counts. In
  RatingAPI's `statistics`, and the new `Leaderboard.Inlines` (stored by `Overwrite`).
- **Tags** (`LeaderboardsRefresh.DetermineTags`, on every `RefreshEverything`; thresholds are the constants there):
  Linear and Fitbeat as before, new **Bomb Reset** (`BombResetsPerMinuteThreshold` 4 bomb resets per minute, 372 maps) and
  **Inline** (`InlinesPerMinuteThreshold` 8 inlines per minute, 111 maps), and the **map type** `Leaderboard.MapType` from
  RatingAPI's tagging model on the acc, tech and pass ratings (acc 1072, tech 718, midspeed 1849, speed 321). New columns
  `IsBombReset`, `IsInline`, `MapType`, `Inlines`.
- **`dotnet run -c Release -- mapstats <wwwroot/file.db>`** (`RatingsRefresh.RefreshMapStatistics`): the Analyzer's counts
  the tags use (inlines, bomb resets, walls, linear %) from the maps folder, then the tags, without rating the maps again.
- **Pages:** tag badges and a tag filter on Leaderboards (kept with a speed modifier), the tags, inlines and bomb resets on
  the Leaderboard page, Inlines, Inlines/min, Bomb resets/min and Tags in Leaderboard Stats (Map traits).

## Low note nerf: a cut of the gain above the predicted acc, by grind time, speed modifiers blended to nomod

- **The low note nerf cuts what grinding adds** (`CurveSettings.LowNoteGainCut` 0.4, `LowNoteGainFullBelow` 70,
  `LowNoteUniformShare` 0): above the map's predicted acc, 40% of what the curve gains over its value there is cut, under
  70 color notes, fading to none at 115 (on ln(notes)). Before, the whole curve was multiplied (×0.80 at 20 notes), which
  also cut the scores of players who don't grind. Maps predicted lower gain more from there to 100% and lose more of their
  100% (Your Best Friend Normal, 47 notes, 99.4%).
- **By grind time** (`LowNoteSpanStart` 18 s, `LowNoteSpanRef` 75 s, `LowNoteSpanWeight` 1, factor 0 to 1.5,
  `LowNoteSpanBonus` 0.2 up to +10%): the cut is times 1 − ln(span / 18 s) / ln(75 s / 18 s), the span being the first to the
  last color note in real time (song time / the speed modifier; new `LackMapCalculation.NoteSpan`, `Leaderboard.NoteSpan`,
  stored by `Overwrite`, `CurvesRefresh.Regenerate` and `RefreshNoteCounts`): all of it at 18 s, none at 75 s, up to 1.5× of
  it on shorter real times; past 75 s, the whole curve × 1 + 0.2 per unit of the factor under 0. A long map with few notes is
  as easy to play but takes longer to grind: RTX 20000 Easy (20 notes over 111 s) paid its top 10 456 pp at 99.4-99.7%,
  Quiet Water Easy (20 over 18 s) 509 at 99.9%.
- **Speed modifiers don't raise these maps' ratings** (`CurveSettings.LowNoteSpeedBlend`, in `RatingsController` and
  `RatingsRefresh.Refresh`): under 115 notes a speed modifier's pass, tech and acc ratings keep (notes / 115)² of their
  difference to nomod (Quiet Water's SF acc rating was 7.6% over nomod). Faster is a shorter grind, not a harder map, so
  with the span factor SS pays the most, then nomod, FS and SF.
- Dev, 24 Oct, a 100% nomod / SS / FS / SF: Quiet Water Easy 553 / 540 / 575 / 603 → 625 / 634 / 611 / 592, RTX 20000 Easy
  579 / 579 / 581 / 600 → 680 / 694 / 664 / 646; the 40-60 note maps 1000-1165 → 900-970 nomod, 770-860 SF. Their worth,
  the balance and the top 1000 unchanged.
- Tried first: pulling these maps' predicted acc towards a reference by note count. It made Quiet Water and RTX 20000 equal,
  but the Acc AI's predicted acc follows these maps' scores closely (correlation 0.89 with their top 10's accuracy, 0.90
  after notes and length), so it spread their worth (Naughty little demon Normal −0.135 → −0.201).
- Curve Explorer: the gain cut, the length and the length bonus.

## Hard swing correction

- **New `Ratings.HardSwingRatio`** (Analyzer `AnalyzeMap`): the mean swing difficulty of the hardest 10% of swings over
  the hardest 8 s stretch's average (both hands, song time; 0 on one hand maps). Above 1, the hardest swings are
  scattered over the map instead of in its hardest stretch. Stored in `Leaderboard.HardSwingRatio` by
  `RatingsRefresh.Overwrite` (new column), so `RatingsRefresh.Refresh`, the balance tuner and the acc reweighter rebuild
  the acc rating with it. Map data only: new maps get it when they're rated.
- **`AccRating.HardSwingMultiplier`:** the acc rating × 1 + 2% per point of (ratio − 1) × pass above 2, up to +12%
  (`HARD_SWING_KNEE` 2, `HARD_SWING_SLOPE` 0.02, `HARD_SWING_CAP` 0.12).
  - Why: maps with scattered hard swings were worth less than others of their stars at every pass (Dev, 24 Oct, no acc
    reweight; −4.8% for (ratio − 1) × pass above 4, +0.8% under 1), most on pass 11+ maps. The pass rating averages
    the hardest windows, so isolated hard swings count little, and the Acc AI predicts the accuracy of cut notes, so
    the misses and bad cuts on them don't count either: the top 300 players' accuracy against the prediction (after
    their own level) was 0.8-1.2% lower on the spikiest pass 11+ maps.
  - A multiplier rather than a lower effective acc: the same acc pp change at every predicted acc (the curve is much
    steeper on easy maps, which is why the hit distance correction is phased out above 97%).
  - Fitted on the worth by (ratio − 1) × pass in memory; 3% per point flattened it fully but put about twice the 30pp+
    top plays on the raised maps.
- **`ONE_SABER_NERF` 0.49 → 0.44:** the correction leaves one hand maps out and raised Standard maps, so OneSaber maps
  went from −0.003 to −0.007 (balance tuner's zero crossing). **`PpMultiplier` 1.0332 → 1.0230** keeps the top 1000's
  average (16398).
- Re-rated (Overwrite, no acc reweight, Dev, 24 Oct), against the same database before:

  | (ratio − 1) × pass | worth | top 1000 | other players |
  |---|---|---|---|
  | under 1 (1060 maps) | +0.008 → +0.006 | +0.006 → +0.005 | +0.008 → +0.007 |
  | 2-3 (877) | −0.006 → −0.005 | −0.001 → −0.001 | −0.006 → −0.006 |
  | 3-4 (270) | −0.020 → −0.007 | −0.010 → +0.001 | −0.022 → −0.009 |
  | 4+ (83) | −0.048 → −0.017 | −0.026 → −0.002 | −0.055 → −0.023 |

  Worth spread 0.0389 → 0.0377, tech share slope −0.0052 → −0.0032 (gap −0.0043 → −0.0030), OneSaber gap −0.003 →
  +0.000, the other slopes and gaps within 0.0007; maps beyond ±5% 619 → 568 (above +5% 268 → 241). 355 maps raised 2%+
  (up to ×1.093, − Divine Light of Myriad − ExpertPlus −0.079 → −0.001, 12.93 → 14.26 stars). Players with a top play
  30pp+ over their second best 2361 → 2350; the raised maps' top plays 1585 → 3242 (30pp+ 149 → 359).
  - Medicine Expert (+0.004 → +0.083) and Muzik Overload ExpertPlus (+0.003 → +0.057) went over.
- Left: most pass 11+ maps far under (worth −3% to −25%: 40 maps, −0.072 → −0.065) stay under. Their worth is the
  same from the top 1000 and from other players (correlation 0.88 on pass 11+ maps), and follows their own accuracy
  shortfall against the Acc AI, which none of the Analyzer's features predicts (a ridge fit on ~35 of them,
  cross-validated: R² 0.15, −0.06% of their −0.84%). That needs the Acc AI's prediction itself (its targets leave
  misses out).

## Acc reweighter: top play limiter

- **`TopPlayMarginPp` 10** (`TopPlayPlayers` 1, `TopPlayMinScores` 30, `TopPlayRounds` 6): after the worth limiter, at the
  real stars, a buffed map's score may pay at most 10pp more than the higher of its pp without reweighting and the
  player's best play on another map. The buff is cut back until no player (with 30+ scores) is past that; it never goes
  under ×1.
  - Why: with ×1.13, Chrome Vox ExpertPlus became some players' top play by ~50pp over their second best.
  - Only what the buff adds is limited. Most top plays far over the player's second best are so without reweighting (309
    buffed maps had one 30pp+ over, 30 of them from the buff); stopping every buff on a map that's already a top play by
    1% cut about 1100 of the 2300 buffed maps and failed the balance guard at every strength.
  - Dev, 24 Oct (with the hard swing correction): 233 buffed maps added more than 10pp, the most Break Expert +78pp (×1.130,
    19 players) and Chrome Vox ExpertPlus +60pp (×1.092, 33 players); 243 maps cut (Chrome Vox ExpertPlus ×1.015,
    Extratongue ExpertPlus ×1.053), worth spread 0.0232 → 0.0253, balance guard passed (pass share slope margin 0.0007).
  - `acc-reweight.json`: `MapsTopPlayLimited`, `TopPlayLimited` per map, the settings.

## Pass pp top compression: knee 10.4 → 13

- **`PassTopKnee` 10.4 → 13** (`PassTopSlope` 0.5, `PpFormula.PassPp`): the knee came from Megametric75, which inflated high
  pass maps' survivors; with the PlayerAdjusted worth those maps paid less than others of their stars. Without the
  compression (tried first) the highest pass maps became many players' top play by a wide margin, so it starts at 13.
  - Dev, 24 Oct, no acc reweight. Per map, for reference, pass 9-10.4 maps are 10.2 players' top play, 1.0 of them by 30pp+
    over the player's second best play:

    | | pass 10.4-13 | pass 13-15 | pass 15+ |
    |---|---|---|---|
    | knee 10.4 (before) | −0.009 | −0.031 (7.9 top plays per map, 0.7 by 30pp+) | −0.030 (6.3, 1.2) |
    | off | −0.004 | −0.011 (11.4, 1.3) | +0.002 (13.1, 3.3) |
    | **knee 13** | −0.004 | −0.015 (11.0, 1.1) | −0.016 (8.0, 1.8) |

  - Without it, The Purple Dimension went from 15 to 41 players' top play and Godspeed E+ from 31 to 58. Knee 12 cut
    pass 15+ little more (1.6 by 30pp+) for pass 13-15 −0.022; knee 14 / 15 let pass 15+ back toward off.
  - Pass share slope +0.0038 → +0.0047, tech share slope −0.0029 → −0.0046, worth spread 0.0392 → 0.0389.
    `PpMultiplier` 1.0369 → 1.0332 keeps the top 1000's average.
  - Left: the 30pp+ top plays on pass 15+ maps are mostly players ranked about 1000-3000 (median 1438; 10 of their 157
    top plays are top 100's), while most of their players get less than on other maps: their pp is uneven between
    players, so no single pass pp value fits both. Pass 10.4+ maps with low tech (< 4.5) still pay less (−0.016 vs
    +0.003).

## Tech pp top compression

- **`TechTopKnee` 12, `TechTopSlope` 0.33** (`PpFormula.TechPp`): above tech 12, each point counts a third for tech pp,
  as `PassTopKnee` does for pass. Sanity Krush ExpertPlus (tech 16.8, the highest tech pp share, 26%) was the biggest
  ExpertPlus outlier after the acc reweighter (worth +0.176 → +0.083 at ×0.903).
  - Why: tech share was worth nothing up to 12 stars (worth per sd of tech share −0.005 to +0.002) but +0.011 at
    12-13.5 and +0.017 above, so the global tech share slope (−0.0017) didn't show it and neither did the reweighter's
    targets (its kinds are linear in tech share). The 14 tech 13+ maps averaged +0.051, the tech 11-13 maps −0.009.
    Sanity Krush's acc rating wasn't the problem: its top scores sit as close to the predicted acc as other maps'.
  - Without the reweighter (Dev, 22 Oct): tech 13+ +0.051 → +0.023, Sanity Krush +0.176 → +0.106 (14.40 → 13.50
    stars), tech 11-13 unchanged, tech share slope −0.0017 → −0.0028, the other slopes and gaps within 0.0003, top 1000
    16409 → 16385. Knee 11 / 10 took the tech 11-13 maps to −0.014 / −0.019; slope 0.25 / 0.2 gained little (+0.020 /
    +0.018).
  - With the reweighter (same point, pass 2 strength 1.3 after the fallback from 1.6): Sanity Krush +0.083 → +0.020
    (×0.903 → ×0.929, 12.53 stars), SONG REQUEST +0.041 → −0.014, tech 13+ +0.032 → +0.015, other maps unchanged on
    average; worth spread 0.0231 → 0.0229, maps beyond ±0.05 79 → 73, tech share slope +0.0035 → +0.0021 (guard margin
    0.0022 → 0.0047); the pass share slope's margin is still about 0 (0.0001 → 0.0000).
- `TechTopKnee` / `TechTopSlope` in the balance tuner's pp formula sweep.
- `dotnet run -c Release -- rescore`: `RefreshEverything` alone on `wwwroot/Database.db`.

### Reset maps: tried and rejected

Speedcore Paradise ExpertPlus (880 resets on 1026 notes, 6.2 per second) and keep out! ExpertPlus (2.75 per second) were
the lowest ExpertPlus maps (worth −0.250 / −0.246 without the reweighter, −0.17 / −0.16 at its ×1.130 cap). They're miss
traps, not misrated for acc: against each player's own maps of similar predicted acc, FC scores were at −0.05% / +1.1%
and the others at −11.1% / −8.5% (FC rate 35% / 50% for the top 100, 0-1% from rank 5000). At 5★+, maps with 0.15+
resets per second were −0.025 on average.

- **Acc correction from resets per second** (`AccRating.EffectiveAcc`, like the hit distance one): six knees / slopes /
  caps took Speedcore Paradise to −0.19 at best while the reset maps under 5★ went from −0.013 to +0.010 to +0.044 and the
  tech share slope from −0.0028 to up to +0.0028. Raising the acc rating pays the FC scores, which weren't short.
- **Resets counting more for pass** (Analyzer `SwingCreation`, every map rated again at all four speeds; Dev with the tech
  knee, no reweighter): a reset's frequency multiplier 2 → 2.25 / 2.5 / 2.75 / 3 / 3.5 took Speedcore Paradise −0.250 →
  −0.192 / −0.150 / −0.102 / −0.056 / +0.010, but the worth spread only moved 0.0455 → 0.0452, maps beyond ±0.05 stayed
  918-932 (928), the reset maps' rms 0.0582 → 0.0565 at best, and reset maps above +0.05 went 36 → 37-59 (My Album Is Out
  On Dangerous Records Expert −0.009 → +0.017 to +0.214, Pink Soldiers Expert +0.017 → +0.033 to +0.102). keep out!, Get
  Get Down and Boku no Pico (pass 3-8) barely moved: pass pp is small there. Extra weight for fast resets (gap under
  0.35-0.75 s) took dense fast reset maps to pass 25-38 (Speedcore Paradise 15-20★, My Album +0.14 to +0.29).
- Neither resets per second nor their speed separates the miss traps from similar maps players don't miss on (My Album:
  2.9 resets per second at 0.2-0.3 s, −0.009). Only scores do: the acc reweighter keeps them.

## Reweighter target metric, star response and noise dead zone; low note nerf, very low star curve power, SC

### Acc reweighter

Players found small gaps moved for nothing and the maps furthest off not moved enough. Both were real: the strengths
were judged by the worth spread around 0, not by each map's distance to its own target, so strength 2.0 took maps 0.83×
the way to their target at 0-5 stars and 2.14× at 11+ (152 maps past 0, 118 of them 11+ stars); and every map moved by
the same share of its gap, so gaps within the noise moved about 2%.

- **Target metric** (`TargetMetric`): each pass's strength and phase 1's held-out choice are judged by each map's
  distance to its own target (rms of worth − target), instead of the worth spread without the part of its gap a map
  keeps. That left the kind's worth in, so the choice pulled every map towards 0, past the target of maps whose kind is
  worth more or less than average (89 of the 118 11+ star maps that went past 0 had their kind's worth on the other side).
- **Star response** (`StarResponse`, `StarResponseProbe` 0.03, `StarResponseBandwidth` 1): before the fit, a random
  ±0.03 log multiplier probe measures how much each map's worth moves with its multiplier, over its acc share, at the
  base and at the real stars, smoothed over stars. Pass 1's step is divided by the base stars' response (relative to the
  median map's), and the multipliers to store are scaled by base over real (0.5× to 2×).
  - Why: at the base stars the response is about flat (1.07-1.17× the acc share from 1 to 14 stars), at the real stars
    0.47-0.50 up to 5 stars, 0.62 at 7, 0.87 at 9, 1.02-1.22 from 11 (a map's stars move with its acc rating). The fit
    moved low and mid star maps half as far as it meant to. The per-map `Responses` only count from pass 2.
- **Noise dead zone** (`NoiseDeadZone` 2, `NoisePerScore` 0.28): each map's gap to its kind's worth (log) is shrunk by
  gap² / (gap² + (2 × noise)²), noise = 0.28 / √(scores its worth rests on), instead of the step's n / (n + 40)
  (`EvidenceScores`, now only used with `NoiseDeadZone` 0). n / (n + 40) moved every map with 800 scores 95% of the way:
  1354 maps within ±0.01 to ±0.03 moved about 2% each. With 1, 443 of them still moved over 2%; with 2, 204.
  - 0.28: what `EvidenceScores` 40 implies with the start's spread (0.0445 × √40); the median map's noise measured with
    every player and with half of them was 0.010, 0.28 / √880 is 0.0094.
- **`Applied` 0.5 → 0.7:** with the target metric and 0.5, maps went 0.48-0.68× of the way to their kind's worth, those
  beyond ±0.1 closed 28% of their worth and 449 maps stayed beyond ±0.05.
- **`MaxChange` 0.15 → 0.11:** the same bounds as before with `Applied` 0.7, ×0.922-×1.085 (0.15 gave ×0.892-×1.121, and
  took Break Expert 12.09 → 14.92 stars).
- **Large gaps** (`LargeGapMaxChange` 0.16, `LargeGapFrom` 0.04, `LargeGapFull` 0.08): a map whose gap (after the dead
  zone) is beyond 0.04 may go past `MaxChange`'s bounds, up to ×0.885-×1.130 from 0.08, in proportion in between. With
  `MaxChange`'s bounds only, maps beyond ±0.1 closed 40-50% of their gap, the others about 68%. 0.22 (×0.840-×1.190)
  closed them 66% instead of 57%, but stars jumped up to +2.83 and 135 maps went past ×1.1 (111).
- `acc-reweight.json`: each map's `KindWorth` and `Target`, and the new settings.
- **Result (Dev, same data, against the previous defaults):** pass 2 at strength 1.3 kept (pass 1's held-out minimum is
  now at 0.7, inside the grid). Maps go 0.80-1.18× of the way to their target across star ranges (0.83-2.14×), 12 maps
  pushed past 0 by more than 0.01 (152), 8 of them 11+ stars (118); 204 maps within ±0.01 to ±0.03 moved over 2% (655);
  maps beyond ±0.1 closed 57% of their worth (44%); 79 maps beyond ±0.05 after (132, 926 without reweighting). Worth
  spread 0.0456 → 0.0231 (0.0226 before), held-out distance to the targets 0.0289 → 0.0183. Mean change 2.7% on 3027
  maps, ×0.885-×1.130 (39 at ×1.130, 9 at ×0.885), the largest star change +1.91 (+1.32). Pass share slope +0.0037 →
  +0.0076 (3% of its tolerance left, 2% before), acc share slope 23% left; both warn. The limiter cut 29 maps.
- **`dotnet run -c Release -- accreweight [Setting=value ...] [rescore]`:** the reweighter alone on `wwwroot/Database.db`
  with any of its settings (e.g. `NoiseDeadZone=1 StarResponse=false`), without the website or the downloads; `rescore`
  runs `RefreshEverything` after it.

### pp formula (RatingAPI submodule)

- **Very low star curve power** (`VeryLowStarCurvePower` 1.08, `VeryLowStarCurveStart` 3, `VeryLowStarCurveEnd` 5): the
  acc curve power is 1.08 up to 3 stars, then linearly to the low star power (1.0) at 5 stars. Stars don't move (read at
  96%); scores under 96% lose a little (90%: about −3% acc pp).
  - Why: the best plays under 4 stars topped out at 690-710 pp (#1 scores, best of the band) against 775-818 from 5 to 14
    stars. The per-map curve rows can't do this: they can only lower the curve, so they raise maps by cutting it at the
    predicted acc, which lifted the median score far more than the top ones (2-3 stars: median 172 → 531 pp, #1 +12%).
  - Impact (simulated on the ranked scores, with the acc reweighter): best #1 at 1, 2, 3, 4 stars 694, 711, 706, 766 →
    795, 808, 743, 783; top 10 of each map +11.6%, +8.2%, +4.7%, +1.2%; all scores' mean +3.8%, +1.7%, 0.0%, −0.3%;
    nothing from 5 stars.
- **SC (small cubes) −50% pp** (`ModifiersMap.SC` 0 → −0.5), as NF and OP.

### Per-map acc curves (RatingAPI submodule)

- **Low note nerf** (`CurveSettings.LowNoteStart` 115, `LowNoteRefNotes` 20, `LowNoteRefMultiplier` 0.8,
  `LowNoteMinMultiplier` 0.5): maps with few color notes are worth too much for how easily they're grinded to 100%. Their
  whole curve is multiplied by 1 − 0.2 × ln(115 / notes) / ln(115 / 20) under 115 notes: ×0.8 at 20 notes, ×0.72 at 10,
  ×0.64 at 5, never under ×0.5. So acc pp at every acc and stars go down; the acc rating, pass and tech pp don't.
  - Quiet Water Easy (20 notes): a 100% SF about 785 → 633 pp on Dev with the acc reweighter (×0.75 gave 589).
  - Not the old low-note nerf (removed earlier), which multiplied the acc rating by 0.6-1.0 under 200 notes.
- RatingAPI's response has `note_count` (`LackMapCalculation.NoteCount`, 0 = unknown, no nerf).

### Pages and tools

- **Leaderboards page:**
  - range filter on stars, pass, acc or tech rating (from / to, either side open);
  - speed modifier filter (SS, FS, SFS): shows, filters and sorts on that modifier's stars, ratings and worth, and leaves
    out maps without modifier ratings; links to the Leaderboard page keep the modifier;
  - Notes column (sortable).
- **`Leaderboard.NoteCount`:** color notes (no bombs, chains or arcs), stored by `RatingsRefresh.Overwrite` and
  `CurvesRefresh.Regenerate`, filled without re-rating by `CurvesRefresh.RefreshNoteCounts` (all databases:
  `RefreshNoteCountsForAllDatabases`). Shown on the Leaderboard page and in Leaderboard Statistics (Map traits).
  `CurvesRefresh.Regenerate` passes it to the curves, with the slider share.
- **Curve Explorer:** very low star power and low note nerf, each with its own switch and values; the low star power
  ramps to 1 when the acc curve power is off.
- **Old database links fall back to the default:** a `?db=` that isn't listed in `databases.json` or isn't on disk (files
  are renamed each time a database is replaced) shows the default database (or the saved one) and is taken out of the
  URL, instead of SQLite creating an empty file ("no such table", and the next startup took it for the downloaded
  database). Connections open in `ReadWrite` mode, never create. Database Comparison and Database Metrics too.
- **Top Scores cache checked before uploading** (`TopScoresCache.EnsureCurrentAsync`): built when it's missing or for
  another version of the database, which the server rejected and then rebuilt on the first visit after every deploy
  (Dev's was Database.db's from before the acc reweight).
- **`dotnet run -c Release -- upload <wwwroot/file.db>`** uploads one database (and its cache); **`-- cache
  <wwwroot/file.db> [upload]`** builds its cache when it doesn't match, and uploads it.
- New Dev and Dev Autoreweighted databases.

## Reweighter fallback and guard margins; the real stars' drift

### Acc reweighter

- **Fallback when the guard rejects** (`FallbackStrengths`): if the point to store fails the balance guard at the real
  stars, the fit runs again at the lower strengths of the last pass, highest first, and the first that passes is stored
  instead of nothing (`acc-reweight.json`: `Fallbacks`). A change to the balance now weakens the reweight instead of
  dropping it.
  - Tested on Dev with the pass share slope tolerance at 0.0037 (2.0 failed by 0.0001): 1.6, 1.3 and 1.0 failed (the pass
    share drift doesn't shrink with the strength), 0.7 was stored: worth spread 0.0333 (0.0211 at 2.0, 0.0443 without
    reweighting), mean change 1.2%.
- **Guard margins:** how far the stored point is from failing each part of the guard (`acc-reweight.json`:
  `GuardMargins`), with a warning when within `GuardMarginWarning` (25%) of a tolerance. On Dev the pass share slope has
  0.0002 of its 0.004 left, so it warns.
- **Where the drift comes from:** measured with real stars only or real star ranges only, all of it is the stars'
  (each map's reference, its players' levels and their distance to it); the star ranges move no slope.
- **Frozen stars** (`FrozenStarsRounds`, off): phase 2 and the slope correction again with each map's stars frozen at
  the last fit's real ones (the targets from the start's worth), kept when the real stars' slopes end closer.
  - Tried on Dev (2 rounds, the drift +0.0038 → −0.0021 → +0.0008): slopes within 0.0008 of the start's, least guard
    margin 79% of its tolerance (was 5%), worth spread 0.0211 → 0.0207. But by moving maps that had no gap: mean change
    3.3% → 5.1%, 614 maps at ×1.085 (170) and 283 at ×0.922 (53), low star maps worth ±0.015 before taken to the bounds
    (Teo Normal ×1.009 → ×1.085, Introduction Hard ×0.996 → ×0.923), 2cf6391 ×1.056 → ×1.085, Godspeed ExpertPlus worth
    +0.013 → +0.038. So it's off.
- **Drift compensation** (`DriftCompensationRounds`, off): phase 2 and the slope correction again at the base stars,
  aimed at the start's slopes minus the real stars' drift (+0.0038 pass share, +0.0023 tech, −0.0020 predicted acc on
  Dev), kept only when the real stars' slopes end closer to the start's.
  - Tried on Dev: the correction's terms grew ±0.0015 → +0.04 to +0.07, the worth spread at the base stars 0.0129 → 0.0495
    (0.0443 without reweighting) and the real stars' slopes overshot about 3× (pass share +0.0038 → −0.0079, tech
    +0.0023 → −0.0098 from the start's): the terms move maps' stars, so the drift grows with them. Not kept, so it's off.

## Worth without lower bound, reweighter slope correction and worth limiter, TechWeight 0.1612

### Worth (`BalanceTuner.WorthMeasures.PlayerAdjusted`)

- **No lower bound** (`PlayerAdjustedBelow` 2 → ∞): a player's scores on maps far under their level count too. Above the
  level stays bounded (`InRangeMargin`).
  - Why: a map more than 2 stars under a player's level counted none of their scores, so high paying scores on maps easy
    for the player went unseen (2cf6391, 9.8 stars: 497 of its 875 scores left out, most of the good ones, −25%).
  - Impact (Dev, Python approximation): 2.4× the scores, 3945 maps with a worth instead of 3506, worth spread 0.061 →
    0.047, 2cf6391 −17% → +4%, 3-5 star maps measured only by low level players −20 to −33% → about 0.
- **Distance term:** `log pp = skill + generosity + distance(map stars − player level)`, per `PlayerAdjustedDistanceBin`
  (0.5 stars), so a map's generosity doesn't depend on how far under its players it is. Maps 1.5-2.5 stars under a player
  paid them ~4% more than at their level, 8 under ~11% less (Dev).
- **Faster fit:** each group's and item's scores are bucketed once, and the alternating means run in parallel, with the
  same sums in the same order.

### pp formula (RatingAPI submodule)

- **`TechWeight` 0.124 → 0.1612, `PpMultiplier` 1.0597 → 1.0369** (balance tuner, with the new worth, without the acc
  reweighter): tech share slope −0.0148 → −0.0011, the most tech heavy 20% −1.0% → −0.3%, the top 1000's pp from tech
  7.0% → 9.6%. `PpMultiplier` keeps the top 1000's average.
- Each map's ratings are prepared once (stars, curve at 96%) for faster scoring.

### Acc reweighter

- **Slope correction** (`SlopeCorrection`): after phase 2, every map also gets a term for its pass share, tech share and
  predicted acc (relative to its star range, over its acc share), sized by a Newton search until those three slopes are
  back at the start's (within 0.0005).
  - Why: correcting single maps moves the slopes by itself, steadily with the strength of the first pass (pass share slope
    +0.006 → +0.011), and neither the targets nor the multipliers explained it. Correcting the pass share alone took the
    tech slope −0.0011 → +0.008, past the guard.
- **Guard `PlayerAdjustedGuardTolerance` / `GapTolerance` 0.0065 / 0.0125 → 0.004 / 0.0075:** with the new worth, the
  measure's noise is 0.0004 (pass slope) to 0.0020 (OneSaber gap), so the old tolerances held nothing back. 0.004 / 0.0075
  is about 4× the noisiest; 0.002 / 0.004 kept nothing.
- **Worth limiter** (`WorthLimit` 0.03, `WorthLimitRounds` 8, `WorthResponse` 0.6): the reweight may not make a map an
  outlier. A map's worth may not end above max(its worth without reweighting, +0.03), nor below min(it, −0.03).
  - Targets are clamped to that range before the passes, so a map already past it isn't pushed further. (On Dev it changed
    nothing: no map past ±0.03 had a target further out.)
  - Last, on the kept multipliers: a map past its limit by its own multiplier has it cut by the part of its worth change it
    overshot (in log). One whose multiplier is 1 or the other way, moved there by the maps around it (worth is relative),
    is moved against the overshoot (over `WorthResponse`, the median map's worth change per multiplier change). The other
    maps of its star range take back the mean, and it's measured again.
  - `acc-reweight.json` has each map's `WorthBefore`, `WorthAfter` and `Limited`, and `MapsLimited`.
  - Why: maps on target were buffed to the cap by the star range mean and the slope correction, and the most pass heavy
    13+ star maps went past their neighbours, their worth moving 1.3× their multiplier (the median map 0.6×): Godspeed
    ExpertPlus (pass 17) +0.010 → +0.107 at ×1.072, 13.72 → 14.86 stars. 66 maps already worth more than +0.03 were
    buffed. Cutting only a map's own multiplier left 98 maps past their limit, 53 of them moved by the maps around them
    (Calamitous Demise ExpertPlus, ×1.000: +0.054 → +0.071).
  - Impact (Dev, against the database without the reweighter): 303 maps past the limit after the slope correction, 334
    cut, 34 left past it by 0.0012 on average (at most 0.007), 8 of them by the maps around them. Godspeed ExpertPlus
    ×1.003, 13.77 stars, worth +0.029; Calamitous Demise ExpertPlus ×0.977, worth +0.033. Worth spread 0.0298 → 0.0277,
    mean change 3.1% → 3.0%.
- **Slope correction in every candidate:** the Jacobian of the three slopes on the correction's terms is measured once,
  at the start, and every candidate of every pass gets one Newton step of it before it's measured. The passes and phase 1
  choose among corrected points: corrected after phase 2 only, the correction raised the worth spread 0.0248 → 0.0298,
  which the choice didn't see. The search after phase 2 starts from the start's Jacobian.
- **Each map's own response** (`Responses`, `ResponseNoise` 0.02, `ResponseRange` 3): the direction divides by the acc
  share times how much more or less than other maps of that share the map's worth has moved with its multiplier so far,
  shrunk towards 1 when the map moved little. The limiter's opposite step uses it too (`WorthResponse` 0.6 removed). Before,
  the median map moved 0.6× its multiplier, the 90th percentile 0.98×, Godspeed ExpertPlus 1.3×. At the base stars
  (below) maps respond alike (p10 0.96, p90 1.05 of the median), so it changes little there.
- **Fit at the base stars** (`FitAtBaseStars`): during the passes and the slope correction each map keeps its stars and
  star range without reweighting, so its reference and its players' distance to it don't move with its multiplier. The
  limiter and the stored balance use the real stars. At the real stars the one-step slope correction overshot (mean change
  0.8% at strength 0.1, tech slope −0.012), nearly every candidate failed the guard and the held-out spread only reached
  0.0385 (0.0203 at the base stars).
- **`Passes` 6 → 3, `Strengths` up to 2.0:** pass 1 took the largest strength, 1.3, with the spread still falling, and
  passes 2-6 took it 0.0260 → 0.0248. Now every split chooses pass 1 at 2.0 (held out 0.0211 at 1.6, 0.0202 at 2.0;
  passes 2-3 raise it), still the edge of the range.
- **Phase 1 on 3 splits** (`HeldOutSplits`): each (pass, strength) is judged by its average held-out spread, passing the
  guard on every split. `acc-reweight.json`: `HeldOut` has every split's points (`Split`).
- **Symmetric bounds:** `Applied` × ±log(1 / (1 − `MaxChange`)), ×0.922 to ×1.085 (was ×0.922 to ×1.072, tighter for
  buffs in log). 182 maps sat at ×1.072 and 14 at ×0.922, and those at ×1.072 weren't noise: median 634 scores (879 for
  the rest), worth −0.078 → −0.035, still under. Now 160 at ×1.085, 32 at ×0.922.
- **PlayerAdjusted only:** the Megametric75 strengths and guard tolerances are removed (`GuardTolerance` /
  `GapTolerance` are now 0.004 / 0.0075); with another `BalanceTuner.WorthMeasure` the reweighter does nothing.
- **Slope correction again at the real stars** (`RealStarsSlopeCorrection`, off): after the search at the base stars, the
  kept point is measured at the real stars and the search runs again there, from a Jacobian measured at that point. At
  the base stars the three slopes are at the start's, but stored (real stars) the pass share slope is +0.0077 (start
  +0.0039, the guard allowing 0.0079) and predicted acc −0.0021 (−0.0001).
  - Tried on Dev: it took them to +0.0044 and −0.0007, but the correction moves maps by their kind, not their own gap:
    worth spread 0.0211 → 0.0247, maps beyond ±0.05 112 → 152, 236 maps past the worth limit before the limiter (9
    without it), OneSaber gap +0.0019 → +0.0044. Pass heavy low tech maps with a high predicted acc were buffed to the
    cap: 2cf6391 (pass 14.3, tech 2.9, 97.95%) ×1.056 → ×1.085, 10.51 → 10.85 stars, worth −0.006 → +0.014 (−0.044
    without reweighting), which players found too high. So it's off.
- **The balance guard on what's stored:** the point to store, after the limiter and at the real stars, must pass the
  guard, else nothing changes (`acc-reweight.json`: `Rejected`). It used to be kept anyway, and with the fit at the base
  stars that was the only check at the real stars, so the guard was never applied to what was stored.
- **`Applied` sweep (Dev):** lowering it doesn't reduce the drift at the real stars, it raises it, and gives back the
  outlier gains. Every value picked pass 1 at strength 2.0. `Applied` stays 0.5.

  | `Applied` | Worth spread | Beyond ±0.05 | Mean change | Pass share slope (start +0.0039, guard 0.0079) | Guard |
  |---|---|---|---|---|---|
  | 0.5 | 0.0211 | 112 | 3.3% | +0.0077 | passes |
  | 0.45 | 0.0227 | 137 | 2.9% | +0.0080 | fails |
  | 0.4 | 0.0247 | 176 | 2.6% | +0.0082 | fails |
  | 0.35 | 0.0267 | 223 | 2.3% | +0.0082 | fails |

- **Result (Dev):** worth spread 0.0443 → 0.0211 (0.0129 at the base stars), mean change 3.3% on 3902 maps, multipliers
  0.922-1.085 (160 at ×1.085, 32 at ×0.922). The limiter cut 9 maps (303 before the rework); 1 left past its limit.
  Maps beyond ±0.05: 112 (251 before the rework). Godspeed ExpertPlus ×0.991, worth +0.013; 2cf6391 ×1.056, worth
  −0.006. Against the start: pass share slope +0.0039 → +0.0077 (+0.0048 before the rework; the guard allows 0.0079),
  acc −0.0011 → −0.0033, tech −0.0011 → +0.0012, predicted acc −0.0001 → −0.0021; gaps: pass share +0.0041 → +0.0083,
  tech share −0.0025 → +0.0011, predicted acc −0.0004 → −0.0037, OneSaber −0.0010 → +0.0019. Passes the guard. At the
  base stars the three corrected slopes are at the start's.

### Pipeline and pages

- **`FullPipeline` with the acc reweighter** starts from the raw acc ratings (keeping the last multipliers stored them in
  float, so each run started a float rounding away from the last: 23 maps 0.5-2.9% off between two runs on the same data)
  and re-scores only after it. About 3.5 minutes in Release.
- The reweighter measures a pass's candidate strengths at once (`CandidateParallelism`, ~150 MB each).
- **Leaderboard page:** the pp curve's y axis follows the lines in view (their values at the edges included), so a line
  doesn't leave the chart when zoomed.

## Per-modifier worth, reweighter fit, smooth per-map curves, Leaderboard page

### Worth per speed modifier (`BalanceTuner.WorthSpeeds`)

- **SS / FS / SF scores are each their own item** in the player-adjusted fit, at that modifier's stars, against nomod maps
  of those stars (`ModifiersRating.SSWorth` / `FSWorth` / `SFWorth`). `Leaderboard.Worth`, worth by player rank and the
  Megametric75 / InRange measures use nomod scores only. A score's speed is its first speed modifier, as for its pp.
  - Why: these scores (276k, 6.3%) counted in the map's own worth at its nomod stars while paid by the modifier's ratings.
  - Impact: nomod worth moved 0.26 points on average (spread 0.0342 → 0.0345; the 300 maps with the most speed play
    −0.08), so they weren't what made worth look off. With enough in-range scores: SS 174 maps (+1.2% on average), FS 433
    (+2.3%), SF 34 (−4.8%).
- **NF scores count in no worth.** There are none in the database, and they get 0 pp, which the fit already skipped.

### Acc reweighter

- **`Applied` is part of the fit:** each map's target is `Applied` (0.5) of the way from its worth to what its kind of map
  is worth (in log), the strengths are compared without the part of the gap a map keeps, and the kept point is stored as is
  (bounds `Applied` × ±15%, `AppliedStep` removed). Before, the full fit was scaled afterwards: every fraction from 0.5 to
  0.15 failed the guard and 0.10 was stored (mean change 0.43%, multipliers 0.984-1.014, worth spread 0.0615 → 0.056).
- **`PlayerAdjustedGuardTolerance` 0.0035 → 0.0065:** with partial targets nothing passed the old one (the pass slope
  settles at +0.011 from +0.005, the acc slope at −0.006 from −0.001).
- **Result:** mean change 2.3% on 3084 maps, multipliers 0.922-1.072, worth spread 0.0617 → 0.0346. Against the database
  without it (balance tuner): kinds of maps within ±0.8% instead of ±0.4%, the highest pass 5% −2.1% → −0.3%, pass share
  slope +0.006 → +0.015, predicted acc gap −0.006 → −0.014. The tuner keeps every constant on both.

### Per-map acc curves (RatingAPI submodule)

- **Smooth base for per-map curves** (`Curve.SmoothBaseValue`): the base table they scale is interpolated with a monotone
  cubic (Fritsch-Carlson) instead of linearly. With the linear table the slope jumped at every table point and fell inside
  each segment (the per-map ratio falls with acc), so per-map curves were bumpy above ~98.5% (99.3-99.55% at 97%
  predicted: 0.10, 0.07, 0.12 per 0.05%). Same values at the table points, up to 1.34% lower between them. The base curve
  stays linear.
- **Curve Explorer:** the per-map curve, slider tilt and acc curve power can be turned on and off on the page, next to the
  curve without them; the predicted acc slider moved under the chart.

### Leaderboard page

- **PP curve:** pp at every accuracy (0-100%) with the selected modifier's ratings and curve, its pass / acc / tech parts,
  the base curve's pp, the comparison leaderboard's and the scores; zoom and pan. It replaces the pp table.
- **Cover, difficulty switcher** (every leaderboard of the song, same `SongId`), **worth** of the selected speed modifier.
- **Speed modifier filters the scores:** only scores with SS / FS / SFS, all scores without one.
- **"N players out of M get pp from it above X% weight, with Y ranked plays minimum":** the weight as profiles show it
  (0.01%), default 0% (about the top 280 plays) and 75 ranked plays.
- Page links keep the selected database.

## Player-adjusted worth, high pass buff, tech weight, low stars

### Worth measured with the player held fixed (`BalanceTuner.WorthMeasure`)

- **New default worth: `PlayerAdjusted`.** `log pp = skill(player, half year) + generosity(map)`, fitted on each player's
  scores near their level (from 2★ under it to 0.5★ above; level = the 5th hardest map in their top 50), then compared
  with maps of the same stars and maps first played around the same time. How much a map pays the same players than their
  other maps at their level.
  - Why: Megametric75 weights each score by where it lands in the player's profile. On pass 14+ maps, players ranked
    3001-10000 (survivors at low acc, with few high plays) inflated it: worth ≈ 0 overall, but −20% for the top 100 and
    +31% for #2001-10000. It also overreacts to rating changes (a 0.1% acc shift on tech 7+ maps: −0.02 → +0.19).
  - A measure against same-star maps only (`InRange`, kept as a diagnostic) barely reacts: stars are what a 96% score
    pays, so a map that pays more is compared with higher-star maps (pass slope flat from `PassCalibration` 0.656 to 0.888).
  - Player-adjusted reacts in proportion: 3% more pp on the tech 8+ maps moved them +1.9%. Worth spread 0.46 → 0.059.
  - Skill per half year: with one skill per player, maps ranked in 2026 looked 2.8% more generous (their scores are set by
    players at their best). Map age: maps first played in 2022 were 3% more generous (grinded for longer).
- **`Leaderboard.Worth` is this measure** (`BalanceTuner.StoredWorth`, from `LeaderboardsRefresh.Refresh`); the previous
  one is kept as `Leaderboard.MegametricWorth`.
- **`Leaderboard.WorthRank20` … `WorthRank10000`:** pp / top pp by player rank (#1-20, #21-100, #101-1000, #1001-3000,
  #3001-10000) against maps of the same stars (`LeaderboardsRefresh.RefreshWorthByRank`).

### Acc rating (RatingAPI submodule)

- **Very high pass buff** (`AccRating.HIGH_PASS_BUFF*`): −0.55% effective acc from 14.5 pass, ramped in from 13.5, flat
  above. Pass 14+ maps were 4.1% under maps of the same stars on pp / top pp in every rank range (best scores 0.25-0.5%
  under the predicted acc, others 0.1-0.4% over). Superluminal 735 → 761, 4 → 15 of the 22 maps with a 700pp+ play.
- **Tech correction tried and rejected:** an acc correction above a tech knee overpaid tech maps at every size (the same
  as noted before in `AccRating.cs`).

### pp formula (RatingAPI submodule)

- **`TechWeight` 0.0891 → 0.124** from the player-adjusted worth: tech-heavy maps paid their players less than others of
  their stars (tech share slope −0.033, tech 8+ maps −2.7%). Tech pp is 2-5% of a score, so they gain 1-2%: slope −0.007,
  top 1000 rank correlation 0.991.
- **Low stars back up** (`LowStarCurveStart` 2 → 7, `LowStarCurveEnd` 5 → 10, `LowStarBonus` 0.0321 → 0.006 up to 9★
  instead of 4.49): top players' best scores at 3-8★ had fallen 3-6% under prod's against the 10-12★ plateau, most on acc
  players' 99% scores (ACC | Pandita #10 in prod, #45). Now 3★ 1.046, 5★ 1.060, 7★ 1.018 (prod 1.035, 1.085, 1.020),
  Pandita #7. 1-2★ maps get less bonus (+10% / +8.5% at 1 / 2★, was +17.6% / +11%) but still rise with the curve power.
- **`PpMultiplier` 1.0833 → 1.0597:** the top 1000's average at prod's (16395).

### Balance tools

- **Balance tuner keeps constants** whose target is within 0.01 of 0, or moves less than 0.01 over the range tried
  (`TargetTolerance`, `MinResponse`), and says why on the Balance Tuning page with the worth measure used. With the
  player-adjusted worth it had cut every pass rating 21% to move a +0.9% slope.
- **Acc reweighter on the player-adjusted worth:** evidence from the measure's own scores, strengths 0.1-1.3 (worth moves
  ~0.6x the pp, not 3-5x), guard tolerances 0.0035 / 0.0125 (between twice the measure's noise and Megametric75's). When
  `Applied` (0.5) of the fit fails the guard, the largest fraction under it that passes is stored (`AppliedStep` 0.05):
  a fraction of the fit can drift more than the whole fit (full fit pass slope +0.006, half +0.012). On Dev Tech: 0.15
  stored, 0.6% mean change.

### Pages and tools

- **Leaderboards:** one Worth column (player-adjusted, %), coloured at ±8%. **Leaderboard Statistics:** split into views
  (Overview, Worth by rank, Profile weights, Megametric, Map traits), all columns sortable, default sort Worth.
- **Ranking:** Top PP next to Total PP. **Map Outliers:** sorted by Outlier %. **Top Acc, Weighted / Unweighted
  Scores graphs:** SS, FS and SFS unticked by default. **Database Comparison:** nothing loads until a view is picked.
- **Pass Comparison page and `PassWindowCheck` removed** (only that page used it).
- **Top Scores caches of databases not in `databases.json` are deleted at startup**, with unfinished `.tmp` saves.
- **Databases and caches excluded from the project by wildcard** (`wwwroot\**\*.db`, `wwwroot\cache\**`).

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
