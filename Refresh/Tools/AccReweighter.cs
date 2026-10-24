using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;

namespace portaBLe.Refresh
{
    // Acc reweighter: the only tool that uses scores to change ratings. It moves each map's acc rating up or down so the map is
    // worth about as much to players as the other maps of its kind, changing the balance between kinds of maps no more than
    // the balance guard allows. Nomod worth (BalanceTuner.Worth): the multiplier also applies to the SS / FS / SF ratings.
    //
    // A map's worth is BalanceTuner.Worth with BalanceTuner.WorthMeasures.PlayerAdjusted (the only measure it runs with): how
    // much the map pays the same players than their other maps at their level, relative to what maps of its stars average (a
    // local linear fit on stars, WorthReference, so crossing a range boundary doesn't change what a map is compared with),
    // every score re-scored in memory with the multipliers being tried (players' weights and every map's worth move when one
    // map changes). (Megametric75, how much a map counts in the profiles of its players, moved 3-5x as much as the acc
    // rating; its strengths and tolerances were removed on 22 Oct.)
    // On Dev (22 Oct, TechWeight 0.1612): held-out worth spread 0.0452 -> 0.0203, all players 0.0443 -> 0.0211, mean change
    // 3.3% on 3902 maps (x0.922-x1.085, 160 at x1.085 and 32 at x0.922); pass share slope +0.0039 -> +0.0077 (the guard
    // allowing 0.0079), the other slopes and gaps within 0.004 of the start's.
    // Players found small gaps moved for nothing and the maps furthest off not moved enough: the strengths were judged by
    // the worth spread around 0, not by each map's distance to its target, so strength 2.0 took maps 0.83x the way to their
    // target at 0-5 stars and 2.14x at 11+ (152 maps past 0, 118 of them 11+ stars). Since 22 Oct: TargetMetric,
    // StarResponse, NoiseDeadZone 2, Applied 0.7, MaxChange 0.11 and LargeGapMaxChange 0.16 (Dev, same data): 0.80x to
    // 1.18x the way, 12 maps past 0 (8 of them 11+), 204 maps within +-0.01 to +-0.03 moved over 2% (655), the maps beyond
    // +-0.1 closed 57% of their worth (44%), 79 maps beyond +-0.05 after (132), x0.885-x1.130, held-out spread to the
    // targets 0.0183, pass share slope +0.0076 (3% of its tolerance left).
    //
    // Target: before any change, worth is fitted on the balance tuner's features (pass and tech pp shares at the predicted acc,
    // predicted acc, OneSaber, star range; least squares, as the tuner's slopes). That fitted value is what the map's kind is
    // worth, and it stays each map's target: kinds of maps worth more or less than others are the formula's and the tuner's to
    // fix, so they keep what they had. (Without the kinds taken out, the pass share slope went +0.03 -> -0.37 in one step.)
    //
    // Direction: -evidence x log((1 + worth) / (1 + target)) / response. evidence = n / (n + EvidenceScores), n the scores
    // its worth rests on (BalanceTuner.WorthEvidence), so maps with few scores move less. response: the acc pp share (at
    // least MinAccShare; an acc multiplier moves a map's pp in proportion to it, so a tech or pass heavy map needs more to
    // move as much), times how much more or less than other maps of that share the map's worth has moved with its
    // multiplier so far (Responses: from the second pass). Maps don't respond alike: the median map's worth moved 0.6x its
    // multiplier (in log), the 90th percentile 0.98x, and Godspeed ExpertPlus 1.3x (its stars jumped 13.72 -> 14.86 at
    // x1.072). With the acc share alone those overshot and the limiter (below) cut them back.
    //
    // Passes: in each, Strengths (fractions of the direction) are tried and the pass moves to the lowest worth spread; the next
    // pass starts again from the new worth. Fixed steps oscillated (worth moves about 3-5x as much as the acc rating, and not
    // linearly: the map moves within its players' sorted profiles). Any spread of multipliers, even random, raises the worth
    // of acc heavy maps (a buffed map climbs its players' profiles more than a nerfed one falls: random 4% multipliers moved
    // the pass share slope +0.034 -> -0.065); the later passes pull every map back to its target, kinds included.
    //
    // FitAtBaseStars: during the fit (the passes and the slope correction), each map keeps its stars and star range without
    // reweighting, so the reference a map's worth is measured against and its players' distance to it don't move with its
    // multiplier: worth follows the map's pp alone. The worth limiter and the balance stored measure the real stars. At the
    // real stars (Dev, 22 Oct) the slopes aren't close to linear in the slope correction's terms (a map's stars and range
    // move with its multiplier): one Newton step overshot (mean change 0.8% at strength 0.1, tech slope -0.012), nearly
    // every candidate failed the guard, and the held-out spread only reached 0.0385 (0.0203 at the base stars). The real
    // stars' slopes drift from the fit's: pass share slope +0.0077 stored for +0.0039 fitted (start +0.0039).
    //
    // How far: fitted to convergence, worth spread went 0.48 -> 0.07, score noise included. So the players are split in two
    // halves: phase 1 fits on one half's scores and measures the worth spread on the other half's; the point (pass, strength)
    // where that held-out spread is lowest is how far the data supports going. Phase 2 fits on every player up to that point.
    // Phase 1 runs on HeldOutSplits random splits, and each (pass, strength) is judged by its average held-out spread over
    // them, passing the balance guard on all of them (one split's choice rested on which players fell in which half).
    //
    // Balance guard: the point kept must have the balance tuner's slopes (pass, acc and tech shares, predicted acc) no worse
    // than without reweighting (by GuardTolerance at most), and its gaps by GapTolerance: the gaps between fifths compare the
    // average worth of the top and bottom fifths of maps, outliers included, so they move when outliers are corrected; the
    // OneSaber gap compares 73 maps with the rest, as noisy. Maps are classified by their pp shares without reweighting there
    // (nerfing a map's acc raises its pass share, and the slopes would measure the reweighting itself). When phase 2's end
    // point doesn't pass, the point of phase 2 with the lowest worth spread that does is kept; without one, nothing changes.
    //
    // Slope correction (SlopeCorrection): correcting single maps moves the slopes by itself, steadily with the strength of
    // the very first pass (Dev, 21 Oct: pass share slope 0.1 -> +0.007, 0.35 -> +0.009, 1.3 -> +0.011 from +0.006), and
    // neither the targets nor the multipliers explain it: multipliers without their kind part (+0.0119), a separate kind
    // direction (+0.011) and pass / tech shares per star range in the targets (+0.011) left it. So every map also gets
    // exp(sum of beta_f x u_f), u_f = -(its feature f - its star range's mean) / the within-range sd / its acc share (at
    // least MinAccShare), for its pass share, tech share and predicted acc, the betas sized so those three slopes are back
    // at the start's. The Jacobian of the slopes on the betas is measured once, at the start. Every candidate of every pass
    // gets one Newton step of it before it's measured, so the passes and phase 1 choose among corrected points (corrected
    // after phase 2 only, the correction raised the worth spread 0.0248 -> 0.0298, which the choice didn't see). After phase
    // 2 a Newton search (Broyden updates) takes the slopes within SlopeCorrectionTolerance of the start's; of the points
    // tried, the one closest to the start's slopes that passes the balance guard is kept, else the uncorrected point if it
    // does. With FitAtBaseStars the base stars' slopes aren't the stored ones (pass share slope +0.0077 stored for +0.0039
    // fitted, start +0.0039, the guard allowing 0.0079). RealStarsSlopeCorrection runs the search again at the real stars,
    // from a Jacobian measured at the kept point; it's off (see there). Drift compensation (DriftCompensationRounds) instead
    // stays at the base stars: the real stars' drift from the base stars' slopes at the kept point is measured, and phase 2
    // and the search run again aimed at the start's slopes minus it, so the passes fix the maps the correction moves; kept
    // when the real stars' slopes end closer to the start's; off (see there). Frozen stars (FrozenStarsRounds) fit again
    // with each map's stars frozen at the last fit's real ones; also off (see there). The pass share alone (until 22 Oct) took the tech slope -0.0011 -> +0.008 once TechWeight 0.1612 had balanced
    // it, past the guard, so nothing was corrected (pass slope +0.0087 from +0.0039).
    //
    // Worth limiter (WorthLimit): the reweight may not make a map an outlier. A map's worth may not end above max(its worth
    // without reweighting, +WorthLimit), nor below min(it, -WorthLimit). Its target is clamped to that range first, so a
    // map already past it isn't pushed further (Dev, 22 Oct: no map's target was, the path didn't change). Then, last,
    // with the kept multipliers and the real stars: the band mean removal and the slope correction move maps that are on
    // target, a map whose stars jump climbs past its neighbours (its worth moves more than its multiplier), and maps that
    // don't change move with the maps around them (worth is relative). A map past its limit by its own multiplier has it
    // cut by the part of its worth change it overshot (in log); one whose multiplier is 1 or the other way is moved against
    // the overshoot (over its response, Responses). The other maps of its star range take back the mean, and it's measured
    // again (WorthLimitRounds at most).
    //
    // Top play limiter (TopPlayMarginPp), after the worth limiter: a buff may not make a map pay a player much more than
    // their best play on another map (see there).
    //
    // The point to store, after the limiters and at the real stars, must pass the balance guard too (acc-reweight.json,
    // Rejected). When it fails, the fit runs again at the lower strengths of the last pass, highest first, and the first
    // that passes is stored (FallbackStrengths, Fallbacks), else nothing changes: a change to the balance then weakens the
    // reweight instead of dropping it. (Dev, 22 Oct, pass share slope tolerance 0.0037 so that 2.0 failed by 0.0001: 1.6,
    // 1.3 and 1.0 failed by 0.0006, 0.0007 and 0.0004, the pass share drift doesn't shrink with the strength; 0.7 was
    // stored, worth spread 0.0333 instead of 0.0211, 0.0443 without reweighting, mean change 1.2%.) How far the point stored
    // is from failing each part of the guard is written (GuardMargins) and warned when within GuardMarginWarning of its
    // tolerance (Dev: the pass share slope, 0.0002 of 0.004). It used to be kept anyway: with FitAtBaseStars that's the only check at the real
    // stars, so the guard was never applied to what was stored (Dev, 22 Oct: Applied 0.45, 0.4 and 0.35 all failed it,
    // pass share slope +0.0080 to +0.0082 for the 0.0079 allowed, and were stored).
    //
    // Applied: each map's target is Applied of the way from its worth before any change to the fitted value (in log), so the
    // fit closes only part of each map's gap; kinds of maps keep the same average target, so the later passes still pull
    // them back. The passes pick the strength that brings worth closest to these targets (TargetMetric).
    //
    // Maps without a worth (fewer than 20 scores, or 10 or fewer of them from players with more than 75 scores: newly ranked
    // maps) keep 1.
    // Multipliers average 1 (in log) within each star range (ranges keep their level, players' pp its scale), stay within
    // Applied x +-log(1 / (1 - MaxChange)) (the same distance both ways in log; LargeGapMaxChange's for large gaps), and
    // multiply the acc rating. Stored in
    // Leaderboards.AccReweight (0 or 1 = none) and applied by RatingsRefresh (Overwrite and Refresh) to the acc rating and the
    // SS / FS / SF acc ratings, so they survive re-rating. Each run starts again from the ratings without them. Then re-score
    // (Program.RefreshEverything). Writes wwwroot/acc-reweight.json.
    public static class AccReweighter
    {
        public const string FileName = "acc-reweight.json";

        // 6 -> 3 with Strengths up to 2.0: pass 1 took the largest strength then (1.3, worth spread 0.0293 at 1.0 -> 0.0260,
        // still falling), and passes 2-6 only took it 0.0260 -> 0.0248
        public static int Passes { get; set; } = 3;
        // Fractions of the direction tried in each pass. Worth moves about 0.6x as much as the map's pp (3% more pp on the tech
        // 8+ maps moved them +1.9%), and pp about 1.2x acc share x the multiplier (acc pp ~ acc rating^AccRatingPower), so a
        // strength near 1.3 closes a map's gap in one pass; 1.6 and 2.0 so pass 1 isn't held at the edge of the range. With
        // the base stars and the slope correction in the passes (Dev, 22 Oct) every split chose pass 1 at 2.0 (held out 0.0211
        // at 1.6, 0.0202 at 2.0; passes 2-3 raised it), still the edge
        public static double[] Strengths { get; set; } = { 0.1, 0.2, 0.35, 0.5, 0.7, 1.0, 1.3, 1.6, 2.0 };
        public static double EvidenceScores { get; set; } = 40;
        // How many of a pass's candidate strengths are measured at once (each re-scores every score in memory: ~150 MB)
        public static int CandidateParallelism { get; set; } = 4;
        public static double MinAccShare { get; set; } = 0.3;
        // The bounds are Applied x +-log(1 / (1 - MaxChange)): x0.922 to x1.085 with Applied 0.5. They were Applied x
        // [log(1 - MaxChange), log(1 + MaxChange)], x0.922 to x1.072, tighter for buffs in log: 182 maps sat at x1.072 and 14
        // at x0.922 (Dev, 22 Oct), and those at x1.072 weren't noise (median 634 scores, the other maps 879; worth -0.078 ->
        // -0.035, still under)
        // 0.15 -> 0.11 with Applied 0.7 (x0.892-x1.121 otherwise; Applied 0.7 x 0.15's bounds took Break Expert 12.09 -> 14.92
        // stars), the same bounds as before, x0.922-x1.085 (x0.885-x1.130 with LargeGapMaxChange)
        public static double MaxChange { get; set; } = 0.11;
        // Fraction of each map's gap to its target the fit closes (in log): 1 = all of it, 0 = none. The full fit (mean change
        // 4.1%, 76 maps at +-15%) was too strong for players, who found the database better without it.
        // Part of the fit, not a fraction of the full fit stored afterwards: the passes are fitted together, the later ones
        // undoing the drift of the first, so a fraction of the whole drifted more than the whole. The full fit passed the
        // balance guard (pass slope +0.0049 -> +0.0068) but every fraction from 0.5 to 0.15 failed it, and only 0.1 was
        // stored (mean change 0.43%, worth spread 0.0615 -> 0.056).
        // 0.5 -> 0.7 with TargetMetric (22 Oct): before it, strength 2.0 took the maps past their targets anyway (0.83x to 2.14x
        // the way); with it and 0.5 they went 0.48x to 0.68x, the maps beyond +-0.1 closed 28% of their worth, 449 maps beyond
        // +-0.05 after (132 without it)
        public static double Applied { get; set; } = 0.7;
        // Scaled by the ratio of the worth spreads to Megametric75's (0.46 / 0.059: 0.00065 / 0.0026), nothing passed: the
        // measure's own noise (half of the players vs the other half, / 2) was about 0.0009 on the pass slope, 0.0022 on the
        // tech slope, 0.0027 on the OneSaber gap, and correcting single maps moves the slopes by itself (players' skills are
        // fitted again: the smallest step took the pass slope +0.005 -> +0.007). Halfway between twice the noise (0.002 /
        // 0.005) and the Megametric75 tolerances (0.005 / 0.02) was 0.0035; with Applied in the fit nothing passed it (the
        // pass slope settles at +0.011 from +0.005, the acc slope at -0.006 from -0.001, mean change 2.1%), so 0.0065: that
        // drift is let through for a reweight that changes something.
        // With the worth measure without the lower bound and with the distance term (BalanceTuner.PlayerAdjustedBelow) the
        // noise (rms over 10 splits, Dev, 22 Oct) is 0.0004 on the pass slope, 0.0001 acc, 0.0010 tech, 0.0000 predicted acc;
        // gaps: OneSaber 0.0020, pass share 0.0004, tech share 0.0007, predicted acc 0.0002 (the old measure: 0.0010, 0.0004,
        // 0.0011, 0.0002; 0.0026, 0.0008, 0.0010, 0.0004), so 0.0065 / 0.0125 no longer held anything back. The drift is set
        // by the first pass (acc slope +0.0035, predicted acc slope +0.0027, pass share gap +0.0051, predicted acc gap
        // +0.0059 by pass 6, all but the OneSaber gap far above the noise), so the tolerance is how strong a reweight is let
        // through: 0.004 / 0.0075 (about 4x the noise of the noisiest, tech slope and OneSaber gap) kept the full fit, 0.003 /
        // 0.005 pass 1 at strength 1.0 (worth spread 0.0296 instead of 0.0255), 0.002 / 0.004 nothing.
        public static double GuardTolerance { get; set; } = 0.004;
        public static double GapTolerance { get; set; } = 0.0075;
        // Warned when the point stored is within this fraction of a tolerance of failing the guard (acc-reweight.json,
        // GuardMargins): a small change to the balance (a constant, new maps) may then reject the reweight
        public static double GuardMarginWarning { get; set; } = 0.25;
        // When the point to store fails the guard at the real stars, the fit again at the lower strengths of the last pass,
        // the first that passes stored (Fallbacks), instead of nothing
        public static bool FallbackStrengths { get; set; } = true;
        // Phase 1's random splits of the players (see above)
        public static int HeldOutSplits { get; set; } = 3;
        // Responses: how much of its own worth change a map's multiplier must make before its response counts (in log: a map
        // moved by ResponseNoise counts half, the rest is the maps around it), and how far its response may be from other
        // maps' of its acc share (times or over)
        public static double ResponseNoise { get; set; } = 0.02;
        public static double ResponseRange { get; set; } = 3;
        // The fit at each map's stars without reweighting (see above)
        public static bool FitAtBaseStars { get; set; } = true;
        // The slope correction (see above): on, how close to the start's slopes the search after phase 2 must get, the step
        // of each term the Jacobian is measured with (log multiplier per within-range sd, over the acc share), the largest
        // size of a term, and how many Newton steps the search takes at most
        public static bool SlopeCorrection { get; set; } = true;
        public static double SlopeCorrectionTolerance { get; set; } = 0.0005;
        public static double SlopeCorrectionStep { get; set; } = 0.005;
        public static double SlopeCorrectionMax { get; set; } = 0.1;
        public static int SlopeCorrectionSteps { get; set; } = 5;
        // The search again at the real stars (see above). Off: on Dev (22 Oct) it took the stored slopes back to the start's
        // (pass share +0.0077 -> +0.0044, predicted acc -0.0021 -> -0.0007) but moved maps by their kind, not their gap:
        // worth spread 0.0211 -> 0.0247, maps beyond +-0.05 112 -> 152, 236 maps past the worth limit before the limiter
        // (9 without it), the OneSaber gap +0.0019 -> +0.0044, and pass heavy low tech maps with a high predicted acc buffed
        // to the cap: 2cf6391 (pass 14.3, tech 2.9, 97.95%) x1.056 -> x1.085, 10.51 -> 10.85 stars, worth -0.006 -> +0.014
        // (-0.044 without reweighting), which players found too high
        public static bool RealStarsSlopeCorrection { get; set; } = false;
        // Drift compensation (see above): how many times phase 2 and the slope correction run again at the base stars,
        // aimed at the start's slopes minus how far the real stars moved them from the base stars' (0 = off). Off: on Dev
        // (22 Oct) the real stars moved the pass, tech and predicted acc slopes +0.0038 +0.0023 -0.0020 from the base stars';
        // aimed at minus that, the correction's terms went +-0.0015 -> +0.04 to +0.07, the worth spread at the base stars
        // 0.0129 -> 0.0495 (0.0443 without reweighting), and the real stars' slopes overshot about 3x (pass share +0.0038 ->
        // -0.0079, tech +0.0023 -> -0.0098 from the start's; real stars' worth spread 0.0211 -> 0.0436): the terms move a
        // map's stars, so the drift grows with them. The round wasn't kept (nothing changed)
        public static int DriftCompensationRounds { get; set; } = 0;
        // Frozen stars (see above): how many times phase 2 and the slope correction run again with each map's stars and star
        // range frozen at the last fit's real ones instead of those without reweighting (0 = off). Off: on Dev (22 Oct) the
        // drift is all the stars' (reference, players' levels and distance; the star ranges move no slope), and two rounds
        // took the real stars' slopes within 0.0008 of the start's (from 0.0038; least guard margin 79% of its tolerance,
        // was 5%), worth spread 0.0211 -> 0.0207, but by moving maps that had no gap: mean change 3.3% -> 5.1%, 614 maps at
        // x1.085 (170) and 283 at x0.922 (53), low star maps worth -0.015 to +0.015 before taken to the bounds (Teo Normal
        // x1.009 -> x1.085, Introduction Hard x0.996 -> x0.923), 2cf6391 x1.056 -> x1.085, Godspeed ExpertPlus worth
        // +0.013 -> +0.038. The fit at frozen stars found another, much stronger, consistent point
        public static int FrozenStarsRounds { get; set; } = 0;
        // The worth limiter, last (see above): how far past average (worth 0) a change may take a map's worth (0 = off), and
        // how many times the multipliers are measured and cut at most. 0.03: about the worth spread after the reweight. Without
        // it (Dev, 22 Oct), 66 maps already worth more than +0.03 were buffed, and the most pass heavy 13+ star maps went past
        // their neighbours: Godspeed ExpertPlus (pass 17) +0.010 -> +0.107 at x1.072, 13.72 -> 14.86 stars (its worth moved
        // 1.3x its multiplier, the median map 0.6x).
        public static double WorthLimit { get; set; } = 0.03;
        // Without the target clamp and without moving maps against the overshoot, 53 maps were still past their limit by the
        // maps around them (multiplier 1, 48 above: Calamitous Demise ExpertPlus +0.054 -> +0.071)
        public static int WorthLimitRounds { get; set; } = 8;
        // Star response: before the fit, a probe (random +-StarResponseProbe log multipliers) measures how much each map's
        // worth moves with its multiplier, over its acc share, at the base and at the real stars, smoothed over stars
        // (StarResponseBandwidth). The responses (Responses) only count from pass 2, and pass 1 is usually the one kept. On
        // Dev (22 Oct) the base stars' response was about flat (1.07-1.17x acc share from 1 to 14 stars), the real stars'
        // 0.47-0.50 up to 5 stars, 0.62 at 7, 0.87 at 9 and 1.02-1.22 from 11 (a map's stars move with its acc rating): the
        // fit, at the base stars, moved low and mid star maps half as far as it meant to. With it the first pass's step is
        // divided by the base stars' response (relative to the median map's), and the multipliers to store are scaled by the
        // base stars' response over the real stars' (0.5x to 2x, FitAtBaseStars), so they move worth at the real stars as
        // the fit did at the base stars.
        public static bool StarResponse { get; set; } = true;
        // Target metric: the strengths (each pass's and phase 1's held-out choice) are judged by each map's distance to its
        // own target, instead of the worth spread without the part of its gap a map keeps (worth - keep: the kind's worth
        // left in, so the choice pulled every map towards 0, past the target of maps whose kind is worth more or less than
        // average). Without it (Dev, 22 Oct) the stored point moved maps 0.83x the way to their target at 0-5 stars, 2.14x
        // at 11+ (all of the way to their kind's worth, Applied 0.5), and 89 of the 118 11+ star maps that went past 0 had
        // their kind's worth on the other side.
        public static bool TargetMetric { get; set; } = true;
        public static double StarResponseProbe { get; set; } = 0.03;
        public static double StarResponseBandwidth { get; set; } = 1.0;
        // Noise dead zone: each map's gap to its kind's worth (log) is shrunk by gap² / (gap² + (NoiseDeadZone x noise)²),
        // noise = NoisePerScore / sqrt(the scores its worth rests on, WorthEvidence), instead of the step's n / (n +
        // EvidenceScores): a gap within the noise barely moves and a large, well measured one moves almost fully (n / (n +
        // 40) moved every map with 800 scores 95% of the way, 1354 maps within +-0.01 to +-0.03 about 2% each). 0 = off.
        // NoisePerScore 0.28: what EvidenceScores 40 implies with the start's spread (0.0445 x sqrt 40); the start's spread
        // with every player and with half of them gave 0.010 at the median map, 0.28 / sqrt(880) 0.0094.
        // 2: with 1, 443 maps within +-0.01 to +-0.03 still moved over 2% (Applied 0.7), with 2 204
        public static double NoiseDeadZone { get; set; } = 2;
        public static double NoisePerScore { get; set; } = 0.28;
        // Large gaps: a map whose gap (log, after the dead zone) is beyond LargeGapFrom may go past MaxChange's bounds, up to
        // LargeGapMaxChange's (Applied x +-log(1 / (1 - LargeGapMaxChange))) from LargeGapFull on, in proportion in between.
        // MaxChange's held the maps furthest off: those beyond +-0.1 closed 40-50% of their gap, the others about 68%. 0 = off.
        // 0.16 (x0.885-x1.130 with Applied 0.7, the other maps x0.922-x1.085): with 0.22 (x0.840-x1.190) the maps furthest off closed
        // 66% of their worth instead of 57%, but stars jumped up to +2.83 and 135 maps went past x1.1 (111)
        public static double LargeGapMaxChange { get; set; } = 0.16;
        public static double LargeGapFrom { get; set; } = 0.04;
        public static double LargeGapFull { get; set; } = 0.08;
        // Maps with fewer color notes (Leaderboard.NoteCount, 0 = unknown: moved) keep x1: the low note nerf's maps
        // (CurveSettings.LowNoteStart). Their worth is low because players can't reach the steep top of their curve (Naughty
        // little demon / Your Best Friend Normal, 43 / 47 notes: worth -0.13 / -0.17, best scores 98.6-99.3%), not because
        // of their acc rating, so a buff only raises what a 100% pays: x1.13 took a 100% SF on Your Best Friend Normal 1165
        // -> 1292 pp, with the best score anywhere 816. Their scores still count in every player's worth and the balance.
        // 0 = off.
        public static int MinNotes { get; set; } = 115;
        // Top play limiter: what a buff adds on top of a player's best play. A buffed map's score may pay at most
        // TopPlayMarginPp more than the higher of its pp without reweighting and the player's best play on another map (at the
        // real stars, after the worth limiter); the buff is cut until fewer than TopPlayPlayers players (with TopPlayMinScores
        // scores or more) are past that. Chrome Vox E+ ended up ~50pp over some players' second best play with x1.13. Most
        // top plays far over the player's second best are so without reweighting (309 buffed maps had one 30pp+ over, 30 of
        // them from the buff), so a map already past it isn't held back, only what the buff adds is: a cap on the buff
        // itself (no buff once a map is a top play by 1%) cut about 1100 of the 2300 buffed maps and failed the balance
        // guard at every strength. Dev, 24 Oct (hard swing correction): 233 buffed maps added more than 10pp, the most
        // Break Expert +78pp (x1.130, 19 players), Chrome Vox E+ +60pp (x1.092, 33 players); 243 maps cut (Chrome Vox E+
        // x1.015), worth spread 0.0232 -> 0.0253. 0 = off.
        public static double TopPlayMarginPp { get; set; } = 10;
        public static int TopPlayPlayers { get; set; } = 1;
        public static int TopPlayMinScores { get; set; } = 30;
        public static int TopPlayRounds { get; set; } = 6;

        private sealed class Point
        {
            public int Split = -1;   // phase 1's split (-1: every player)
            public int Pass;
            public double Strength;
            public double[] SlopeCorrection = Array.Empty<double>();   // pass, tech, predicted acc terms (empty: none)
            public double[] LogM = Array.Empty<double>();
            public BalanceStats Balance = new();
            public bool GuardOk;
            public double? HeldOutSpread;
        }

        private sealed class Context
        {
            public BalanceTuner.Data Loaded = null!;
            public double[] BaseAcc = Array.Empty<double>();
            public double[,] BaseModAcc = new double[0, 0];
            public int[] BaseBand = Array.Empty<int>();
            public BalanceTuner.Data Unweighted = null!;
            public double[] AccShare = Array.Empty<double>();
            // max(acc share, MinAccShare): the part of each map's response its acc share explains
            public double[] Prior = Array.Empty<double>();
            public double[][] BandFeatures = Array.Empty<double[]>();
            public double?[] StartWorth = Array.Empty<double?>();
            // 1: the map has a worth and is moved (not under MinNotes)
            public double[] HasWorth = Array.Empty<double>();
            // Under MinNotes: kept at 1
            public bool[] Pinned = Array.Empty<bool>();
            public BalanceStats StartBalance = new();
            // The slope correction's terms (pass, tech, predicted acc) and the Jacobian of the slopes on them at the start
            public double[][] U = Array.Empty<double[]>();
            public double[,] Jacobian = new double[0, 0];
            // What the slope correction aims the three slopes at, relative to the start's (drift compensation; 0: the start's)
            public double[] SlopeTarget = new double[3];
            // The stars and star ranges the fit measures at (FitAtBaseStars): those without reweighting, or the last fit's
            // (frozen stars)
            public BalanceTuner.Data FitStars = null!;
            // Phase 2's worth before any change, its targets' base (null: measured at FitStars; frozen stars: the start's)
            public double?[]? TargetWorth;
            public double Lo, Hi;
            // Each map's bounds (Lo, Hi, or wider with LargeGapMaxChange), set by each fit (Path)
            public double[] MapLo = Array.Empty<double>(), MapHi = Array.Empty<double>();
            // StarResponse: each map's base stars' response relative to the median map's (1: off), and the scale of its
            // multiplier to store, the base stars' response over the real stars' (1: off)
            public double[] StarRelative = Array.Empty<double>(), StarScale = Array.Empty<double>();
            // The last fit on every player's: each map's kind's worth and its target (Applied of the way there)
            public double?[] KindWorth = Array.Empty<double?>(), Target = Array.Empty<double?>();
            // Every score's pp without reweighting at the real stars (the top play limiter's, measured on first use)
            public float[]? ZeroPp;
            public System.Diagnostics.Stopwatch Timer = null!;
        }

        private sealed record Measured(BalanceTuner.Data Data, BalanceTuner.Evaluation E, double?[] Worth, Point Point, double?[]? HeldOutWorth = null);

        public static async Task Run(AppContext dbContext, string webRootPath)
        {
            if (BalanceTuner.WorthMeasure != BalanceTuner.WorthMeasures.PlayerAdjusted)
            {
                Console.WriteLine($"Acc reweight: only runs with BalanceTuner.WorthMeasures.PlayerAdjusted (is {BalanceTuner.WorthMeasure}), nothing done");
                return;
            }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "AccReweight", "REAL");
            Console.WriteLine("Acc reweight: loading scores...");
            var loaded = await BalanceTuner.Load(dbContext);
            int mapCount = loaded.Maps.Length;

            // The acc ratings without the stored multipliers, and each map's star range without them
            var baseAcc = new double[mapCount];
            var baseModAcc = new double[mapCount, 3];
            var baseBand = new int[mapCount];
            for (int i = 0; i < mapCount; i++)
            {
                var lb = loaded.Maps[i].Leaderboard;
                double stored = ReplayUtils.AccReweightOf(lb);
                baseAcc[i] = lb.AccRating / stored;
                var mod = lb.ModifiersRating;
                if (mod != null)
                {
                    baseModAcc[i, 0] = mod.SSAccRating / stored;
                    baseModAcc[i, 1] = mod.FSAccRating / stored;
                    baseModAcc[i, 2] = mod.SFAccRating / stored;
                }
                baseBand[i] = BalanceTuner.StarBandOf(ReplayUtils.ToStars((float)(baseAcc[i]), lb.PassRating, lb.TechRating, lb.Curve));
            }

            double bound = Applied * -Math.Log(1 - MaxChange);
            var context = new Context
            {
                Loaded = loaded,
                BaseAcc = baseAcc,
                BaseModAcc = baseModAcc,
                BaseBand = baseBand,
                Unweighted = WithMultipliers(loaded, baseAcc, baseModAcc, new double[mapCount]),
                BandFeatures = BandFeatures(baseBand),
                Lo = -bound,
                Hi = bound,
                MapLo = Enumerable.Repeat(-bound, mapCount).ToArray(),
                MapHi = Enumerable.Repeat(bound, mapCount).ToArray(),
                StarRelative = Enumerable.Repeat(1.0, mapCount).ToArray(),
                StarScale = Enumerable.Repeat(1.0, mapCount).ToArray(),
                Timer = timer,
            };
            context.FitStars = context.Unweighted;
            context.AccShare = AccShares(context.Unweighted);
            context.Prior = context.AccShare.Select(a => Math.Max(a, MinAccShare)).ToArray();
            var startMeasure = Measure(context, new double[mapCount], false);
            context.StartWorth = startMeasure.Worth;
            context.StartBalance = startMeasure.Balance;
            context.Pinned = loaded.Maps.Select(m => m.Leaderboard.NoteCount > 0 && m.Leaderboard.NoteCount < MinNotes).ToArray();
            context.HasWorth = context.StartWorth.Select((w, i) => w != null && !context.Pinned[i] ? 1.0 : 0.0).ToArray();
            if (MinNotes > 0) Console.WriteLine($"Acc reweight: {context.Pinned.Count(p => p)} maps under {MinNotes} notes kept at 1");
            context.U = SlopeTerms(context);
            if (SlopeCorrection) context.Jacobian = MeasureJacobian(context, null, context.StartBalance, FitAtBaseStars, "start");
            if (StarResponse) StarResponses(context);

            // Phase 1: fit on half the players, check on the other half, for each split
            var splits = new List<List<Point>>();
            for (int split = 0; split < HeldOutSplits; split++)
            {
                var random = new Random(6969 + split);
                var half = Enumerable.Range(0, loaded.PlayerCount).Select(_ => random.Next(2) == 0).ToArray();
                var other = half.Select(h => !h).ToArray();
                var path = Path(context, $"held out {split + 1}/{HeldOutSplits}", half, other, Passes, null);
                foreach (var p in path) p.Split = split;
                splits.Add(path);
            }
            // Each (pass, strength) measured on every split: its average held-out spread, and whether it passes the guard on all.
            // The lowest that passes (the early passes push kinds of maps apart and the later ones pull them back: with a
            // balanced start, the lowest held-out spread overall was at pass 2, where the guard failed on every point of phase 2,
            // so nothing was kept); without one, the lowest overall.
            var summary = splits[0].Where(p => p.HeldOutSpread != null).Select(p => (p.Pass, p.Strength)).Distinct()
                .Select(key => (Key: key, Points: splits.Select(s => s.FirstOrDefault(p => p.Pass == key.Pass && p.Strength == key.Strength && p.HeldOutSpread != null)).ToList()))
                .Where(k => k.Points.All(p => p != null))
                .Select(k => (k.Key.Pass, k.Key.Strength, HeldOut: k.Points.Average(p => p!.HeldOutSpread!.Value), GuardOk: k.Points.All(p => p!.GuardOk)))
                .ToList();
            var chosen = summary.Where(k => k.Pass > 0 && k.GuardOk).OrderBy(k => k.HeldOut).Cast<(int Pass, double Strength, double HeldOut, bool GuardOk)?>().FirstOrDefault()
                ?? summary.OrderBy(k => k.HeldOut).First();
            double startHeldOut = summary.First(k => k.Pass == 0).HeldOut;
            Console.WriteLine(FormattableString.Invariant(
                $"Acc reweight: held-out worth spread (average of {HeldOutSplits} splits) lowest at pass {chosen.Pass} strength {chosen.Strength:0.00}{(chosen.GuardOk ? "" : " (no point passes the guard)")} ({startHeldOut:0.0000} -> {chosen.HeldOut:0.0000}, lowest overall {summary.Min(k => k.HeldOut):0.0000}; each split: {string.Join(", ", splits.Select(s => FormattableString.Invariant($"pass {s.Where(p => p.HeldOutSpread != null && p.GuardOk && p.Pass > 0).OrderBy(p => p.HeldOutSpread).FirstOrDefault()?.Pass} strength {s.Where(p => p.HeldOutSpread != null && p.GuardOk && p.Pass > 0).OrderBy(p => p.HeldOutSpread).FirstOrDefault()?.Strength:0.00}")))})"));

            // Phase 2: every player, up to that point, then the slope correction's search on the kept point
            List<Point> phase2 = null!;
            Point start = null!, uncorrected = null!;
            var corrections = new List<Point>();
            Point Fit(double strength)
            {
                phase2 = Path(context, "all players", null, null, chosen.Pass, chosen.Pass > 0 ? strength : null);
                start = phase2[0];
                var fitted = phase2[^1].GuardOk ? phase2[^1] : phase2.Where(p => p.GuardOk).OrderBy(p => p.Balance.WorthSpread).First();
                Console.WriteLine(FormattableString.Invariant(
                    $"Acc reweight: keeping pass {fitted.Pass} strength {fitted.Strength:0.00} (worth spread {start.Balance.WorthSpread:0.0000} -> {fitted.Balance.WorthSpread:0.0000}), {fitted.LogM.Count(v => Math.Abs(v) > 0.005)} maps changed, mean change {fitted.LogM.Average(v => Math.Abs(Math.Exp(v) - 1)) * 100:0.0}%"));
                if (fitted == start)
                {
                    Console.WriteLine("Acc reweight: WARNING: no point of phase 2 passes the balance guard, nothing changes (see acc-reweight.json, Rounds)");
                }
                uncorrected = fitted;
                if (SlopeCorrection && fitted != start) fitted = CorrectSlopes(context, fitted, corrections, FitAtBaseStars, context.Jacobian, "slope correction");
                return fitted;
            }
            var kept = Fit(chosen.Strength);
            // Drift compensation: the fit again, aimed at the start's slopes minus the real stars' drift from the base stars'.
            // Kept when it takes the real stars' slopes closer to the start's
            var drifts = new List<AccReweightDrift>();
            if (SlopeCorrection && FitAtBaseStars)
            {
                for (int round = 1; round <= DriftCompensationRounds && kept != start; round++)
                {
                    var real = Measure(context, kept.LogM, false).Balance;
                    var drift = SlopesOff(kept.Balance, real);
                    var realOff = SlopesOff(context.StartBalance, real);
                    var (lastPhase2, lastStart, lastUncorrected, lastTarget) = (phase2, start, uncorrected, context.SlopeTarget);
                    context.SlopeTarget = drift.Select(v => -v).ToArray();
                    Console.WriteLine(FormattableString.Invariant(
                        $"Acc reweight: drift compensation {round}: the real stars move the pass, tech and predicted acc slopes {drift[0]:+0.0000;-0.0000} {drift[1]:+0.0000;-0.0000} {drift[2]:+0.0000;-0.0000} from the base stars' (real stars minus the start's {realOff[0]:+0.0000;-0.0000} {realOff[1]:+0.0000;-0.0000} {realOff[2]:+0.0000;-0.0000}), fitting again aimed at the start's minus that"));
                    var next = Fit(chosen.Strength);
                    var nextReal = next != start ? Measure(context, next.LogM, false).Balance : null;
                    var nextOff = nextReal != null ? SlopesOff(context.StartBalance, nextReal) : null;
                    bool better = nextOff != null && nextOff.Max(Math.Abs) < realOff.Max(Math.Abs);
                    drifts.Add(new AccReweightDrift { Round = round, Drift = drift, RealOffBefore = realOff, RealOffAfter = nextOff ?? Array.Empty<double>(), Kept = better });
                    Console.WriteLine(FormattableString.Invariant(
                        $"Acc reweight: drift compensation {round}: real stars minus the start's {realOff[0]:+0.0000;-0.0000} {realOff[1]:+0.0000;-0.0000} {realOff[2]:+0.0000;-0.0000} -> {(nextOff == null ? "nothing kept" : FormattableString.Invariant($"{nextOff[0]:+0.0000;-0.0000} {nextOff[1]:+0.0000;-0.0000} {nextOff[2]:+0.0000;-0.0000}"))}, worth spread {real.WorthSpread:0.0000} -> {nextReal?.WorthSpread:0.0000}{(better ? "" : ", not closer: keeping the last fit")}"));
                    if (!better)
                    {
                        (phase2, start, uncorrected, context.SlopeTarget) = (lastPhase2, lastStart, lastUncorrected, lastTarget);
                        break;
                    }
                    kept = next;
                }
            }

            // Frozen stars: the fit again with each map's stars and star range frozen at the last fit's real ones, kept when the
            // real stars' slopes end closer to the start's
            var frozen = new List<AccReweightDrift>();
            if (SlopeCorrection && FitAtBaseStars)
            {
                string Slopes(double[] v) => string.Join(" ", v.Select(x => (Math.Round(x, 4) + 0.0).ToString("+0.0000;-0.0000", System.Globalization.CultureInfo.InvariantCulture)));
                for (int round = 1; round <= FrozenStarsRounds && kept != start; round++)
                {
                    var realMeasure = Measure(context, kept.LogM, false);
                    var real = realMeasure.Balance;
                    var realOff = SlopesOff(context.StartBalance, real);
                    var fitOff = SlopesOff(context.StartBalance, kept.Balance);
                    // Which of them moves the slopes: the stars (reference, players' levels and distance) or the star range
                    var starsOnly = SlopesOff(context.StartBalance, MeasureMixed(context, kept.LogM, null, context.FitStars));
                    var bandOnly = SlopesOff(context.StartBalance, MeasureMixed(context, kept.LogM, context.FitStars, realMeasure.Data));
                    Console.WriteLine(
                        $"Acc reweight: frozen stars {round}: pass, tech and predicted acc slopes minus the start's: at the fit's stars {Slopes(fitOff)}, real stars only {Slopes(starsOnly)}, real star ranges only {Slopes(bandOnly)}, both (stored) {Slopes(realOff)}");
                    var before = (phase2, start, uncorrected, context.FitStars, context.Jacobian, context.TargetWorth);
                    context.FitStars = realMeasure.Data;
                    context.TargetWorth = context.StartWorth;
                    var atZero = Measure(context, new double[mapCount], true).Balance;
                    context.Jacobian = MeasureJacobian(context, null, atZero, true, $"frozen stars {round}");
                    var next = Fit(chosen.Strength);
                    var nextReal = next != start ? Measure(context, next.LogM, false).Balance : null;
                    var nextOff = nextReal != null ? SlopesOff(context.StartBalance, nextReal) : null;
                    bool better = nextOff != null && nextOff.Max(Math.Abs) < realOff.Max(Math.Abs);
                    frozen.Add(new AccReweightDrift { Round = round, Drift = realOff.Zip(fitOff, (a, b) => a - b).ToArray(), RealOffBefore = realOff, RealOffAfter = nextOff ?? Array.Empty<double>(), Kept = better });
                    Console.WriteLine(
                        $"Acc reweight: frozen stars {round}: real stars minus the start's {Slopes(realOff)} -> {(nextOff == null ? "nothing kept" : Slopes(nextOff))}, worth spread {real.WorthSpread.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)} -> {nextReal?.WorthSpread.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)}{(better ? "" : ", not closer: keeping the last fit")}");
                    if (!better)
                    {
                        (phase2, start, uncorrected, context.FitStars, context.Jacobian, context.TargetWorth) = before;
                        break;
                    }
                    kept = next;
                }
            }

            // Again at the real stars (the base stars' slopes aren't the stored ones), from a Jacobian measured at the kept point
            if (SlopeCorrection && RealStarsSlopeCorrection && FitAtBaseStars && kept != start)
            {
                var real = Measure(context, kept.LogM, false).Balance;
                var atReal = new Point { Pass = kept.Pass, Strength = kept.Strength, SlopeCorrection = kept.SlopeCorrection, LogM = kept.LogM, Balance = real, GuardOk = GuardOk(context.StartBalance, real) };
                Console.WriteLine(FormattableString.Invariant(
                    $"Acc reweight: at the real stars: pass slope {real.PassShareSlope:+0.0000;-0.0000}, tech slope {real.TechShareSlope:+0.0000;-0.0000}, predicted acc slope {real.PredictedAccSlope:+0.0000;-0.0000}, worth spread {real.WorthSpread:0.0000}{(atReal.GuardOk ? "" : " (guard)")}"));
                kept = CorrectSlopes(context, atReal, corrections, false, MeasureJacobian(context, kept.LogM, real, false, "kept point, real stars"), "real stars");
            }

            // The worth limiter and the top play limiter on what is kept, and the balance guard at the real stars on what would
            // be stored (the limiters measured it; without them and with FitAtBaseStars, measured here). topLimited: the last
            // Finish's maps the top play limiter cut
            var topLimited = new bool[mapCount];
            (Point Point, bool[] Limited, double?[]? Worth) Finish(Point p)
            {
                topLimited = new bool[mapCount];
                if (p == start) return (p, new bool[mapCount], null);
                // StarResponse: scaled so they move worth at the real stars as the fit did at the base stars
                if (StarResponse && FitAtBaseStars)
                    p = new Point { Pass = p.Pass, Strength = p.Strength, SlopeCorrection = p.SlopeCorrection, LogM = Clean(context, p.LogM.Select((v, i) => v * context.StarScale[i]).ToArray(), context.HasWorth), Balance = p.Balance, GuardOk = p.GuardOk };
                (Point Point, bool[] Limited, double?[]? Worth) finished = WorthLimit > 0 ? LimitWorth(context, p) : (p, new bool[mapCount], null);
                if (TopPlayMarginPp > 0)
                {
                    var (limitedPoint, worth) = LimitTopPlays(context, finished.Point, topLimited);
                    return (limitedPoint, finished.Limited, worth);
                }
                if (WorthLimit > 0 || !FitAtBaseStars) return finished;
                var real = Measure(context, p.LogM, false);
                return (new Point { Pass = p.Pass, Strength = p.Strength, SlopeCorrection = p.SlopeCorrection, LogM = p.LogM, Balance = real.Balance, GuardOk = GuardOk(context.StartBalance, real.Balance) }, new bool[mapCount], real.Worth);
            }
            void WarnFailed(Point p, string then)
            {
                Console.WriteLine(FormattableString.Invariant(
                    $"Acc reweight: WARNING: the point to store (pass {p.Pass} strength {p.Strength:0.00}) fails the balance guard at the real stars ({string.Join(", ", GuardMargins(context.StartBalance, p.Balance).Where(m => m.Margin < 0).Select(m => FormattableString.Invariant($"{m.Name} past by {-m.Margin:0.0000}")))}), {then}"));
            }
            var (limitedPoint, limited, worthAfter) = Finish(kept);
            kept = limitedPoint;
            Point? rejected = null;
            var fallbacks = new List<Point>();
            if (kept != start && !kept.GuardOk)
            {
                rejected = kept;
                // Fallback: the fit again at the lower strengths of the last pass (highest first), the first whose point to
                // store passes the guard. Else nothing changes
                var lower = FallbackStrengths ? Strengths.Where(v => v < kept.Strength).OrderByDescending(v => v).ToList() : new List<double>();
                WarnFailed(kept, lower.Count > 0 ? $"trying strengths {string.Join(", ", lower.Select(v => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))}" : "nothing changes (see acc-reweight.json, Rejected)");
                bool found = false;
                foreach (double strength in lower)
                {
                    var fitted = Fit(strength);
                    if (fitted == start) continue;
                    var tried = Finish(fitted);
                    fallbacks.Add(tried.Point);
                    if (tried.Point.GuardOk)
                    {
                        (kept, limited, worthAfter) = tried;
                        found = true;
                        Console.WriteLine(FormattableString.Invariant(
                            $"Acc reweight: WARNING: fallback: storing pass {kept.Pass} strength {strength:0.00} instead of {rejected.Strength:0.00} (worth spread {rejected.Balance.WorthSpread:0.0000} -> {kept.Balance.WorthSpread:0.0000}, mean change {rejected.LogM.Average(v => Math.Abs(Math.Exp(v) - 1)) * 100:0.0}% -> {kept.LogM.Average(v => Math.Abs(Math.Exp(v) - 1)) * 100:0.0}%)"));
                        break;
                    }
                    WarnFailed(tried.Point, "next");
                }
                if (!found)
                {
                    if (lower.Count > 0) Console.WriteLine("Acc reweight: WARNING: no strength passes the balance guard at the real stars, nothing changes (see acc-reweight.json, Rejected and Fallbacks)");
                    kept = start;
                    limited = new bool[mapCount];
                    topLimited = new bool[mapCount];
                    worthAfter = null;
                }
            }
            // How far the point stored is from failing the guard: warned when within GuardMarginWarning of a tolerance
            var margins = kept != start ? GuardMargins(context.StartBalance, kept.Balance) : new List<(string Name, double Margin, double Tolerance)>();
            if (margins.Count > 0)
            {
                var close = margins.Where(m => m.Margin < GuardMarginWarning * m.Tolerance).ToList();
                var least = margins.OrderBy(m => m.Margin / m.Tolerance).First();
                Console.WriteLine(FormattableString.Invariant(
                    $"Acc reweight: balance guard margins at the real stars: {string.Join(", ", margins.Select(m => FormattableString.Invariant($"{m.Name} {m.Margin:0.0000}")))} (least: {least.Name}, {least.Margin / least.Tolerance * 100:0}% of its tolerance)"));
                if (close.Count > 0) Console.WriteLine(FormattableString.Invariant(
                    $"Acc reweight: WARNING: close to failing the balance guard ({string.Join(", ", close.Select(m => FormattableString.Invariant($"{m.Name} {m.Margin:0.0000} of {m.Tolerance:0.0000}")))}): a small change to the balance may reject or weaken the reweight"));
            }

            // Applied is part of the fit (the targets), so the kept point is stored as is
            var applied = kept;
            double fraction = kept != start ? Applied : 0;

            // Store the multipliers and the reweighted acc ratings and stars
            var index = new Dictionary<string, int>();
            for (int i = 0; i < mapCount; i++) index[loaded.Maps[i].Leaderboard.Id] = i;
            var lbs = await dbContext.Leaderboards.Include(l => l.ModifiersRating).ToListAsync();
            foreach (var lb in lbs)
            {
                if (!index.TryGetValue(lb.Id, out int i)) continue;
                double m = Math.Exp(applied.LogM[i]);
                lb.AccReweight = (float)m;
                lb.AccRating = (float)(baseAcc[i] * m);
                var mod = lb.ModifiersRating;
                if (mod != null)
                {
                    mod.SSAccRating = (float)(baseModAcc[i, 0] * m);
                    mod.FSAccRating = (float)(baseModAcc[i, 1] * m);
                    mod.SFAccRating = (float)(baseModAcc[i, 2] * m);
                }
                ReplayUtils.SetStars(lb);
            }
            // The refresh steps before this can leave automatic change detection off
            dbContext.ChangeTracker.DetectChanges();
            int saved = await dbContext.SaveChangesAsync();
            Console.WriteLine($"Acc reweight: {saved} rows saved");

            AccReweightRound ToRound(Point p) => new()
            {
                Split = p.Split,
                Round = p.Pass,
                Strength = p.Strength,
                SlopeCorrection = p.SlopeCorrection,
                Balance = p.Balance,
                GuardOk = p.GuardOk,
                HeldOutSpread = p.HeldOutSpread,
                MapsChanged = p.LogM.Count(v => Math.Abs(v) > 0.005),
                MeanAbsChange = p.LogM.Average(v => Math.Abs(Math.Exp(v) - 1)),
            };
            var result = new AccReweightResult
            {
                GeneratedAt = DateTime.UtcNow,
                Settings = new AccReweightSettings { WorthMeasure = BalanceTuner.WorthMeasure.ToString(), Passes = Passes, Strengths = Strengths, EvidenceScores = EvidenceScores, MinAccShare = MinAccShare, MaxChange = MaxChange, Applied = Applied, GuardTolerance = GuardTolerance, GapTolerance = GapTolerance, SlopeCorrection = SlopeCorrection, SlopeCorrectionTolerance = SlopeCorrectionTolerance, WorthLimit = WorthLimit, HeldOutSplits = HeldOutSplits, FitAtBaseStars = FitAtBaseStars, ResponseNoise = ResponseNoise, DriftCompensationRounds = DriftCompensationRounds, FrozenStarsRounds = FrozenStarsRounds, GuardMarginWarning = GuardMarginWarning, FallbackStrengths = FallbackStrengths, StarResponse = StarResponse, TargetMetric = TargetMetric, NoiseDeadZone = NoiseDeadZone, NoisePerScore = NoisePerScore, LargeGapMaxChange = LargeGapMaxChange, TopPlayMarginPp = TopPlayMarginPp, TopPlayPlayers = TopPlayPlayers, TopPlayMinScores = TopPlayMinScores, MinNotes = MinNotes },
                MapsLimited = limited.Count(l => l),
                MapsTopPlayLimited = topLimited.Count(l => l),
                HeldOut = splits.SelectMany(s => s).Select(ToRound).ToList(),
                Rounds = phase2.Select(ToRound).ToList(),
                KeptRound = phase2.IndexOf(uncorrected),
                SlopeCorrections = corrections.Select(ToRound).ToList(),
                DriftCompensation = drifts,
                FrozenStars = frozen,
                AppliedFraction = fraction,
                Applied = ToRound(applied),
                Rejected = rejected == null ? null : ToRound(rejected),
                Fallbacks = fallbacks.Select(ToRound).ToList(),
                GuardMargins = margins.ToDictionary(m => m.Name, m => Math.Round(m.Margin, 5)),
                Maps = Enumerable.Range(0, mapCount)
                    .Select(i => new AccReweightMap
                    {
                        Id = loaded.Maps[i].Leaderboard.Id,
                        Name = loaded.Maps[i].Leaderboard.Name,
                        Difficulty = loaded.Maps[i].Leaderboard.DifficultyName,
                        Mode = loaded.Maps[i].Leaderboard.ModeName,
                        Multiplier = Math.Round(Math.Exp(applied.LogM[i]), 4),
                        WorthBefore = context.StartWorth[i] is double b ? Math.Round(b, 4) : null,
                        KindWorth = context.KindWorth.Length > 0 && context.KindWorth[i] is double k ? Math.Round(k, 4) : null,
                        Target = context.Target.Length > 0 && context.Target[i] is double t ? Math.Round(t, 4) : null,
                        WorthAfter = worthAfter?[i] is double a ? Math.Round(a, 4) : null,
                        Limited = limited[i],
                        TopPlayLimited = topLimited[i],
                    })
                    .Where(m => Math.Abs(m.Multiplier - 1) > 0.0005)
                    .OrderBy(m => m.Multiplier)
                    .ToList(),
            };
            var path2 = System.IO.Path.Combine(webRootPath, FileName);
            await File.WriteAllTextAsync(path2, JsonSerializer.Serialize(result));
            Console.WriteLine($"Acc reweight saved to {path2} ({timer.Elapsed:mm\\:ss}). Re-score next (RefreshEverything).");
        }

        // Every score re-scored with these multipliers: every player's worth and the balance. baseStars: each map at its stars
        // and star range without reweighting (FitAtBaseStars)
        private static (BalanceTuner.Data Data, BalanceTuner.Evaluation E, double?[] Worth, BalanceStats Balance) Measure(Context c, double[] logM, bool baseStars)
        {
            var data = WithMultipliers(c.Loaded, c.BaseAcc, c.BaseModAcc, logM, baseStars ? c.FitStars : null);
            var e = BalanceTuner.Evaluate(data);
            var worth = BalanceTuner.Worth(data, e, out _);
            return (data, e, worth, BalanceTuner.MeasureBalance(data, e, worth, c.Unweighted));
        }

        // The balance with each map's stars (reference, players' levels and distance) and star range from starsFrom and
        // bandFrom (null: the real ones), to see which of them moves the slopes
        private static BalanceStats MeasureMixed(Context c, double[] logM, BalanceTuner.Data? starsFrom, BalanceTuner.Data? bandFrom)
        {
            var data = WithMultipliers(c.Loaded, c.BaseAcc, c.BaseModAcc, logM, starsFrom, bandFrom);
            var e = BalanceTuner.Evaluate(data);
            var worth = BalanceTuner.Worth(data, e, out _);
            return BalanceTuner.MeasureBalance(data, e, worth, c.Unweighted);
        }

        // Without each star range's mean (log, over the maps with weight), then the bounds
        private static double[] Clean(Context c, double[] logM, double[] weight)
        {
            var cleaned = Residuals(c.BandFeatures, logM.Select(v => (double?)v).ToArray(), weight);
            return cleaned.Select((v, i) => Math.Clamp(v ?? 0, c.MapLo[i], c.MapHi[i])).ToArray();
        }

        // StarResponse (see above): the probe, at the base stars and at the real ones
        private static void StarResponses(Context c)
        {
            int mapCount = c.Loaded.Maps.Length;
            var random = new Random(4242);
            var probe = c.HasWorth.Select(h => h > 0 ? (random.Next(2) == 0 ? -StarResponseProbe : StarResponseProbe) : 0).ToArray();
            probe = Clean(c, probe, c.HasWorth);
            var stars = c.Unweighted.Maps.Select(m => (double)m.Stars).ToArray();

            // Each map's response (log worth change / log multiplier / Prior), then their kernel mean over stars at each map's
            double[] Smoothed(double?[] worth)
            {
                var used = new List<(double Stars, double Ratio)>();
                for (int i = 0; i < mapCount; i++)
                {
                    if (Math.Abs(probe[i]) < 1e-6 || worth[i] is not double w || c.StartWorth[i] is not double w0 || w <= -0.99 || w0 <= -0.99) continue;
                    used.Add((stars[i], Math.Clamp(Math.Log((1 + w) / (1 + w0)) / probe[i] / c.Prior[i], -1, 4)));
                }
                var result = new double[mapCount];
                double h = StarResponseBandwidth;
                Parallel.For(0, mapCount, i =>
                {
                    double sw = 0, sy = 0;
                    foreach (var (s, r) in used)
                    {
                        double d = (s - stars[i]) / h;
                        if (Math.Abs(d) > 4) continue;
                        double w = Math.Exp(-0.5 * d * d);
                        sw += w;
                        sy += w * r;
                    }
                    result[i] = sw > 0 ? sy / sw : 0;
                });
                return result;
            }
            var baseResponse = Smoothed(Measure(c, probe, true).Worth);
            var realResponse = FitAtBaseStars ? Smoothed(Measure(c, probe, false).Worth) : baseResponse;
            var withWorth = Enumerable.Range(0, mapCount).Where(i => c.HasWorth[i] > 0).Select(i => baseResponse[i]).OrderBy(v => v).ToList();
            double median = withWorth.Count > 0 ? withWorth[withWorth.Count / 2] : 0;
            for (int i = 0; i < mapCount; i++)
            {
                c.StarRelative[i] = median > 0 && baseResponse[i] > 0 ? Math.Clamp(baseResponse[i] / median, 1 / ResponseRange, ResponseRange) : 1;
                c.StarScale[i] = baseResponse[i] > 0 && realResponse[i] > 0 ? Math.Clamp(baseResponse[i] / realResponse[i], 0.5, 2) : 1;
            }
            string Table(double[] v) => string.Join(", ", new[] { 1, 3, 5, 7, 9, 11, 12, 13, 14 }.Select(s =>
            {
                int nearest = Enumerable.Range(0, mapCount).Where(i => c.HasWorth[i] > 0).OrderBy(i => Math.Abs(stars[i] - s)).First();
                return FormattableString.Invariant($"{s}* {v[nearest]:0.00}");
            }));
            Console.WriteLine($"Acc reweight: star response (x acc share), base stars: {Table(baseResponse)}; real stars: {Table(realResponse)} ({c.Timer.Elapsed:mm\\:ss})");
        }

        // logM plus the slope correction's terms
        private static double[] WithCorrection(Context c, double[] logM, double[] beta)
        {
            var result = (double[])logM.Clone();
            for (int f = 0; f < beta.Length; f++)
                for (int i = 0; i < result.Length; i++) result[i] += beta[f] * c.U[f][i];
            return result;
        }

        // The three slopes the slope correction corrects, minus the start's
        private static double[] SlopesOff(BalanceStats start, BalanceStats b) => new[]
        {
            b.PassShareSlope - start.PassShareSlope,
            b.TechShareSlope - start.TechShareSlope,
            b.PredictedAccSlope - start.PredictedAccSlope,
        };

        // The three slopes minus what the slope correction aims them at (the start's plus SlopeTarget)
        private static double[] TargetOff(Context c, BalanceStats b) => SlopesOff(c.StartBalance, b).Zip(c.SlopeTarget, (o, t) => o - t).ToArray();

        // How much each map's worth has moved with its multiplier, relative to other maps of its acc share: (change in log
        // worth) / (log multiplier) / Prior, over the median of the maps that moved more than ResponseNoise (global), shrunk
        // towards 1 by how little the map moved (a map moved by ResponseNoise counts half) and within ResponseRange. 1 for a
        // map that hasn't moved or has no worth. global: the median itself (the response of a map with Prior 1).
        private static double[] Responses(Context c, double[] logM, double?[] worth, double?[] worth0, out double global)
        {
            int mapCount = logM.Length;
            var ratio = new double?[mapCount];
            for (int i = 0; i < mapCount; i++)
            {
                if (Math.Abs(logM[i]) < 1e-6 || worth[i] is not double w || worth0[i] is not double w0 || w <= -0.99 || w0 <= -0.99) continue;
                ratio[i] = Math.Log((1 + w) / (1 + w0)) / logM[i] / c.Prior[i];
            }
            var sure = Enumerable.Range(0, mapCount).Where(i => ratio[i] != null && Math.Abs(logM[i]) > ResponseNoise).Select(i => ratio[i]!.Value).OrderBy(v => v).ToList();
            global = sure.Count > 0 ? sure[sure.Count / 2] : 0;
            var relative = Enumerable.Repeat(1.0, mapCount).ToArray();
            if (!(global > 0)) { global = 0; return relative; }
            for (int i = 0; i < mapCount; i++)
            {
                if (ratio[i] is not double r) continue;
                double clamped = Math.Clamp(r / global, 1 / ResponseRange, ResponseRange);
                double weight = logM[i] * logM[i] / (logM[i] * logM[i] + ResponseNoise * ResponseNoise);
                relative[i] = Math.Exp(weight * Math.Log(clamped));
            }
            return relative;
        }

        // The slope correction's terms: for the pass share, tech share and predicted acc, -(the map's - its star range's
        // mean) / the within-range sd / Prior, over the maps with a worth
        private static double[][] SlopeTerms(Context c)
        {
            int mapCount = c.Loaded.Maps.Length;
            var (pass, tech) = KindShares(c.Unweighted);
            var features = new[] { pass, tech, c.Unweighted.Maps.Select(m => (double)m.Leaderboard.PredictedAcc * 100).ToArray() };
            var u = new double[features.Length][];
            for (int f = 0; f < features.Length; f++)
            {
                var sums = new Dictionary<int, (double Sum, int Count)>();
                for (int i = 0; i < mapCount; i++)
                {
                    if (c.HasWorth[i] <= 0) continue;
                    sums.TryGetValue(c.BaseBand[i], out var m);
                    sums[c.BaseBand[i]] = (m.Sum + features[f][i], m.Count + 1);
                }
                var dev = new double[mapCount];
                double squares = 0;
                int n = 0;
                for (int i = 0; i < mapCount; i++)
                {
                    if (c.HasWorth[i] <= 0) continue;
                    var (sum, count) = sums[c.BaseBand[i]];
                    dev[i] = features[f][i] - sum / count;
                    squares += dev[i] * dev[i];
                    n++;
                }
                double sd = n > 0 && squares > 0 ? Math.Sqrt(squares / n) : 1;
                u[f] = new double[mapCount];
                for (int i = 0; i < mapCount; i++) if (c.HasWorth[i] > 0) u[f][i] = -dev[i] / sd / c.Prior[i];
            }
            return u;
        }

        // The Jacobian of the three slopes on the slope correction's terms at logM (null: the start, its balance at), at the
        // base stars or the real ones: one step of each
        private static double[,] MeasureJacobian(Context c, double[]? logM, BalanceStats at, bool baseStars, string label)
        {
            int k = c.U.Length, mapCount = c.Loaded.Maps.Length;
            double h = SlopeCorrectionStep;
            var jacobian = new double[k, k];
            Parallel.For(0, k, new ParallelOptions { MaxDegreeOfParallelism = CandidateParallelism }, f =>
            {
                var step = new double[k];
                step[f] = h;
                var b = Measure(c, Clean(c, WithCorrection(c, logM ?? new double[mapCount], step), c.HasWorth), baseStars).Balance;
                var off = SlopesOff(at, b);
                for (int r = 0; r < k; r++) jacobian[r, f] = off[r] / h;
            });
            Console.WriteLine(FormattableString.Invariant(
                $"Acc reweight: slope Jacobian, {label} (rows pass, tech, predicted acc slopes; columns their terms): {string.Join("; ", Enumerable.Range(0, k).Select(r => string.Join(" ", Enumerable.Range(0, k).Select(f => FormattableString.Invariant($"{jacobian[r, f]:+0.000;-0.000}")))))} ({c.Timer.Elapsed:mm\\:ss})"));
            return jacobian;
        }

        // The passes: fit on fitPlayers (null = all), measure the held-out worth spread on checkPlayers (null = none). With
        // lastStrength, the last pass only tries that strength, so the last point is where the path ends.
        private static List<Point> Path(Context c, string label, bool[]? fitPlayers, bool[]? checkPlayers, int passes, double? lastStrength)
        {
            int mapCount = c.Loaded.Maps.Length;
            var ones = Enumerable.Repeat(1.0, mapCount).ToArray();
            var points = new List<Point>();
            // Each map's target (set below, after the start is measured)
            var target = new double?[mapCount];

            // A point measured (Compute: on its own, so a pass's candidates can be measured at once), then recorded in order
            Measured Compute(double[] logM, int pass, double strength, double[] beta)
            {
                var (data, e, worthAll, balance) = Measure(c, logM, FitAtBaseStars);
                var fitWorth = fitPlayers == null ? worthAll : BalanceTuner.Worth(data, e, out _, fitPlayers);
                var heldOutWorth = checkPlayers == null ? null : BalanceTuner.Worth(data, e, out _, checkPlayers);
                double? heldOut = heldOutWorth == null || TargetMetric ? null : Spread(heldOutWorth);
                return new Measured(data, e, fitWorth, new Point { Pass = pass, Strength = strength, SlopeCorrection = beta, LogM = logM, Balance = balance, HeldOutSpread = heldOut }, heldOutWorth);
            }
            // A candidate with one Newton step of the slope correction (the start's Jacobian) before it's measured
            Measured Corrected(double[] logM, int pass, double strength, double[] weight)
            {
                var cleaned = Clean(c, logM, weight);
                if (!SlopeCorrection) return Compute(cleaned, pass, strength, Array.Empty<double>());
                var first = Measure(c, cleaned, FitAtBaseStars).Balance;
                var beta = Solve(c.Jacobian, TargetOff(c, first).Select(v => -v).ToArray())
                    .Select(b => Math.Clamp(b, -SlopeCorrectionMax, SlopeCorrectionMax)).ToArray();
                return Compute(Clean(c, WithCorrection(c, cleaned, beta), weight), pass, strength, beta);
            }
            Measured Record(Measured measured)
            {
                var point = measured.Point;
                // TargetMetric: the held-out players' worth against each map's target
                if (TargetMetric && measured.HeldOutWorth != null) point.HeldOutSpread = Spread(measured.HeldOutWorth.Select((w, i) => w - target[i]).ToArray());
                var (balance, logM, pass, strength, heldOut) = (point.Balance, point.LogM, point.Pass, point.Strength, point.HeldOutSpread);
                point.GuardOk = GuardOk(c.StartBalance, balance);
                points.Add(point);
                Console.WriteLine(FormattableString.Invariant(
                    $"Acc reweight ({label}): pass {pass} strength {strength:0.00}: worth spread {balance.WorthSpread:0.0000}{(heldOut is double h ? $" (held out {h:0.0000})" : "")}, pass slope {balance.PassShareSlope:+0.000;-0.000}, tech slope {balance.TechShareSlope:+0.000;-0.000}, acc slope {balance.AccShareSlope:+0.000;-0.000}, predicted acc gap {balance.PredictedAccGap:+0.000;-0.000}, one saber gap {balance.OneSaberGap:+0.000;-0.000}, mean change {logM.Average(v => Math.Abs(Math.Exp(v) - 1)) * 100:0.0}%{(point.GuardOk ? "" : " (guard)")} ({c.Timer.Elapsed:mm\\:ss})"));
                return measured;
            }

            var logM = new double[mapCount];
            var current = Compute(logM, 0, 0, Array.Empty<double>());

            // Each map's target: what the balance features predict for its worth before any change (fitted players' scores)
            var linear0 = (fitPlayers == null && c.TargetWorth != null ? c.TargetWorth : current.Worth).Select(w => w is double v && v > -0.99 ? (double?)v : null).ToArray();
            var residual0 = Residuals(Features(c.Unweighted, c.BaseBand), linear0, ones);
            for (int i = 0; i < mapCount; i++) if (!c.Pinned[i] && linear0[i] is double v && residual0[i] is double r) target[i] = Math.Max(v - r, -0.9);
            // Only Applied of the way there (in log). keep: the part of its gap a map keeps, which the strengths are compared
            // without (0 with Applied = 1, so they minimize the worth spread itself)
            var keep = new double[mapCount];
            var evidence0 = NoiseDeadZone > 0 ? BalanceTuner.WorthEvidence(current.Data, current.E, fitPlayers) : null;
            // Each map's bounds: MaxChange's, wider for large gaps (LargeGapMaxChange)
            c.MapLo = Enumerable.Repeat(c.Lo, mapCount).ToArray();
            c.MapHi = Enumerable.Repeat(c.Hi, mapCount).ToArray();
            double large = LargeGapMaxChange > 0 ? Math.Max(c.Hi, Applied * -Math.Log(1 - LargeGapMaxChange)) : c.Hi;
            for (int i = 0; i < mapCount; i++)
            {
                if (linear0[i] is not double w || target[i] is not double t) continue;
                // Its gap to its kind's worth (log), shrunk within its noise (NoiseDeadZone)
                double gap = Math.Log((1 + w) / (1 + t));
                if (evidence0 != null)
                {
                    double noise = NoiseDeadZone * NoisePerScore / Math.Sqrt(Math.Max(evidence0[i], 1));
                    gap *= gap * gap / (gap * gap + noise * noise);
                }
                double partial = Math.Exp(Math.Log(1 + w) - Applied * gap) - 1;
                double bound = c.Hi + (large - c.Hi) * Math.Clamp((Math.Abs(gap) - LargeGapFrom) / (LargeGapFull - LargeGapFrom), 0, 1);
                c.MapLo[i] = -bound;
                c.MapHi[i] = bound;
                // Not past the worth limit (WorthLimit): a map already past it isn't pushed further
                if (WorthLimit > 0) partial = Math.Clamp(partial, Math.Min(w, -WorthLimit), Math.Max(w, WorthLimit));
                keep[i] = partial - t;
                target[i] = partial;
            }
            // The maps under MinNotes stay at 1: every change is clamped to these bounds
            for (int i = 0; i < mapCount; i++) if (c.Pinned[i]) c.MapLo[i] = c.MapHi[i] = 0;
            if (fitPlayers == null)
            {
                c.KindWorth = linear0.Select((w, i) => w is double v && residual0[i] is double r ? (double?)Math.Max(v - r, -0.9) : null).ToArray();
                c.Target = (double?[])target.Clone();
            }
            // Maps without a worth (too few scores, e.g. newly ranked) stay at 1 and don't count in their star range's mean
            var hasTarget = target.Select(t => t != null ? 1.0 : 0.0).ToArray();
            Record(current);

            for (int pass = 1; pass <= passes; pass++)
            {
                var direction = new double[mapCount];
                var evidence = BalanceTuner.WorthEvidence(current.Data, current.E, fitPlayers);
                var relative = Responses(c, logM, current.Worth, linear0, out double global);
                if (pass > 1)
                {
                    var sorted = relative.Where((_, i) => Math.Abs(logM[i]) > 1e-6).OrderBy(v => v).ToList();
                    if (sorted.Count > 0) Console.WriteLine(FormattableString.Invariant(
                        $"Acc reweight ({label}): responses before pass {pass}: median {global:0.00} x acc share, relative p10 {sorted[sorted.Count / 10]:0.00}, p90 {sorted[sorted.Count * 9 / 10]:0.00}"));
                }
                for (int i = 0; i < mapCount; i++)
                {
                    if (current.Worth[i] is not double w || w <= -0.99 || target[i] is not double t) continue;
                    int n = evidence[i];
                    // The dead zone (NoiseDeadZone) is the evidence then; the first pass by the star response (StarResponse),
                    // the later ones by each map's own (Responses)
                    double weight = NoiseDeadZone > 0 ? (n > 0 ? 1 : 0) : (double)n / (n + EvidenceScores);
                    direction[i] = -weight * Math.Log((1 + w) / (1 + t)) / (c.Prior[i] * relative[i] * (pass == 1 ? c.StarRelative[i] : 1));
                }

                var strengths = pass == passes && lastStrength is double only ? new[] { only } : Strengths;
                // The candidates measured at once (CandidateParallelism), then recorded and compared in order
                var from = logM;
                var computed = new Measured[strengths.Length];
                Parallel.For(0, strengths.Length, new ParallelOptions { MaxDegreeOfParallelism = CandidateParallelism }, k =>
                {
                    var candidate = new double[mapCount];
                    for (int i = 0; i < mapCount; i++) candidate[i] = from[i] + strengths[k] * direction[i];
                    computed[k] = Corrected(candidate, pass, strengths[k], hasTarget);
                });
                Measured? best = null;
                double bestSpread = double.MaxValue;
                foreach (var one in computed)
                {
                    var measured = Record(one);
                    // Moves on the fitted players' worth spread, without the part of each map's gap it keeps
                    double spread = TargetMetric
                        ? Spread(measured.Worth.Select((w, i) => w - target[i]).ToArray())
                        : Spread(measured.Worth.Select((w, i) => w - keep[i]).ToArray());
                    if (spread < bestSpread)
                    {
                        bestSpread = spread;
                        best = measured;
                    }
                }
                current = best!;
                logM = current.Point.LogM;
            }
            // With lastStrength the last point is where the path ends; move it last if a lower strength was measured after it
            if (lastStrength != null && passes > 0 && points[^1] != current.Point)
            {
                points.Remove(current.Point);
                points.Add(current.Point);
            }
            return points;
        }

        // The slope correction's search after phase 2 (see above): from the kept point (already corrected one step per pass),
        // the sizes of the pass, tech and predicted acc terms by a Newton search on the three slopes (from jacobian, then
        // Broyden updates), at the base stars or the real ones. corrections: every point tried.
        private static Point CorrectSlopes(Context c, Point kept, List<Point> corrections, bool baseStars, double[,] startJacobian, string label)
        {
            int k = c.U.Length;
            var start = c.StartBalance;

            Point At(double[] beta)
            {
                var candidate = Clean(c, WithCorrection(c, kept.LogM, beta), c.HasWorth);
                var balance = Measure(c, candidate, baseStars).Balance;
                var total = kept.SlopeCorrection.Length == k ? kept.SlopeCorrection.Zip(beta, (a, b) => a + b).ToArray() : (double[])beta.Clone();
                var point = new Point { Pass = kept.Pass, Strength = kept.Strength, SlopeCorrection = total, LogM = candidate, Balance = balance, GuardOk = GuardOk(start, balance) };
                corrections.Add(point);
                Console.WriteLine(FormattableString.Invariant(
                    $"Acc reweight ({label}): pass {beta[0]:+0.0000;-0.0000}, tech {beta[1]:+0.0000;-0.0000}, predicted acc {beta[2]:+0.0000;-0.0000}: worth spread {balance.WorthSpread:0.0000}, pass slope {balance.PassShareSlope:+0.0000;-0.0000}, tech slope {balance.TechShareSlope:+0.0000;-0.0000}, acc slope {balance.AccShareSlope:+0.0000;-0.0000}, predicted acc slope {balance.PredictedAccSlope:+0.0000;-0.0000}, predicted acc gap {balance.PredictedAccGap:+0.000;-0.000}, one saber gap {balance.OneSaberGap:+0.000;-0.000}, mean change {candidate.Average(v => Math.Abs(Math.Exp(v) - 1)) * 100:0.0}%{(point.GuardOk ? "" : " (guard)")} ({c.Timer.Elapsed:mm\\:ss})"));
                return point;
            }

            double Size(Point p) => TargetOff(c, p.Balance).Max(Math.Abs);
            Point? best = null;
            void Consider(Point p) { if (p.GuardOk && (best == null || Size(p) < Size(best))) best = p; }
            Consider(kept);

            var current = TargetOff(c, kept.Balance);
            if (current.Max(Math.Abs) > SlopeCorrectionTolerance)
            {
                var jacobian = (double[,])startJacobian.Clone();
                var beta = new double[k];
                for (int iteration = 0; iteration < SlopeCorrectionSteps; iteration++)
                {
                    var delta = Solve(jacobian, current.Select(v => -v).ToArray());
                    var next = beta.Zip(delta, (b, d) => Math.Clamp(b + d, -SlopeCorrectionMax, SlopeCorrectionMax)).ToArray();
                    var p = At(next);
                    Consider(p);
                    var fn = TargetOff(c, p.Balance);
                    if (fn.Max(Math.Abs) <= SlopeCorrectionTolerance) break;
                    // Broyden: J += (df - J db) db' / (db' db)
                    var db = next.Zip(beta, (a, b) => a - b).ToArray();
                    double norm = db.Sum(v => v * v);
                    if (norm < 1e-18) break;
                    for (int r = 0; r < k; r++)
                    {
                        double predicted = 0;
                        for (int f = 0; f < k; f++) predicted += jacobian[r, f] * db[f];
                        double miss = fn[r] - current[r] - predicted;
                        for (int f = 0; f < k; f++) jacobian[r, f] += miss * db[f] / norm;
                    }
                    beta = next;
                    current = fn;
                }
            }
            if (best == null)
            {
                // No point passes the whole balance guard: the one closest to the start's slopes
                best = corrections.Append(kept).OrderBy(Size).First();
                Console.WriteLine("Acc reweight: WARNING: no slope correction point passes the balance guard, keeping the closest (see acc-reweight.json, SlopeCorrections)");
            }
            var sizes = best.SlopeCorrection.Length == k ? best.SlopeCorrection : new double[k];
            Console.WriteLine(FormattableString.Invariant(
                $"Acc reweight: {label}: pass {sizes[0]:+0.0000;-0.0000}, tech {sizes[1]:+0.0000;-0.0000}, predicted acc {sizes[2]:+0.0000;-0.0000} (the last pass's and this search's terms): pass slope {kept.Balance.PassShareSlope:+0.0000;-0.0000} -> {best.Balance.PassShareSlope:+0.0000;-0.0000} (aim {start.PassShareSlope + c.SlopeTarget[0]:+0.0000;-0.0000}), tech slope {kept.Balance.TechShareSlope:+0.0000;-0.0000} -> {best.Balance.TechShareSlope:+0.0000;-0.0000} (aim {start.TechShareSlope + c.SlopeTarget[1]:+0.0000;-0.0000}), predicted acc slope {kept.Balance.PredictedAccSlope:+0.0000;-0.0000} -> {best.Balance.PredictedAccSlope:+0.0000;-0.0000} (aim {start.PredictedAccSlope + c.SlopeTarget[2]:+0.0000;-0.0000}), worth spread {kept.Balance.WorthSpread:0.0000} -> {best.Balance.WorthSpread:0.0000}"));
            return best;
        }

        // The worth limiter (see above), at the real stars: the kept point with the multipliers of the maps past their limit
        // cut, which maps were cut, and every map's worth at the point returned
        private static (Point Point, bool[] Limited, double?[] Worth) LimitWorth(Context c, Point kept)
        {
            int mapCount = c.Loaded.Maps.Length;
            var logM = (double[])kept.LogM.Clone();
            var limited = new bool[mapCount];
            BalanceStats balance = kept.Balance;
            double?[] worth = Array.Empty<double?>();
            for (int round = 0; ; round++)
            {
                var measured = Measure(c, logM, false);
                worth = measured.Worth;
                balance = measured.Balance;
                var relative = Responses(c, logM, worth, c.StartWorth, out double global);
                if (!(global > 0)) global = 1;

                // Past its limit by its own multiplier: cut by the part of its worth change (log) it overshot. Otherwise
                // (multiplier 1 or the other way): moved against the overshoot, over its response
                int over = 0;
                for (int i = 0; i < mapCount; i++)
                {
                    if (c.Pinned[i] || worth[i] is not double w || c.StartWorth[i] is not double s) continue;
                    double limit = w > s ? Math.Max(s, WorthLimit) : Math.Min(s, -WorthLimit);
                    if (w > s ? w <= limit : w >= limit) continue;
                    over++;
                    if (round == WorthLimitRounds) continue;
                    double moved = Math.Log((1 + w) / (1 + s)), allowed = Math.Log((1 + limit) / (1 + s));
                    if (logM[i] * moved > 1e-12) logM[i] *= Math.Clamp(allowed / moved, 0, 1);
                    else logM[i] = Math.Clamp(logM[i] - (moved - allowed) / (global * c.Prior[i] * relative[i]), c.MapLo[i], c.MapHi[i]);
                    limited[i] = true;
                }
                Console.WriteLine(FormattableString.Invariant(
                    $"Acc reweight (worth limiter): round {round}: {over} maps past +-{WorthLimit:0.000}, worth spread {balance.WorthSpread:0.0000}, mean change {logM.Average(v => Math.Abs(Math.Exp(v) - 1)) * 100:0.0}%, response {global:0.00} x acc share ({c.Timer.Elapsed:mm\\:ss})"));
                if (over == 0 || round == WorthLimitRounds) break;

                // Each star range keeps its mean (log, over the maps with a worth): the maps not cut take it back
                var bands = new Dictionary<int, (double Sum, int Free)>();
                for (int i = 0; i < mapCount; i++)
                {
                    if (c.HasWorth[i] <= 0) continue;
                    bands.TryGetValue(c.BaseBand[i], out var b);
                    bands[c.BaseBand[i]] = (b.Sum + logM[i], b.Free + (limited[i] ? 0 : 1));
                }
                for (int i = 0; i < mapCount; i++)
                {
                    if (c.HasWorth[i] <= 0 || limited[i]) continue;
                    var (sum, free) = bands[c.BaseBand[i]];
                    if (free == 0) continue;
                    logM[i] = Math.Clamp(logM[i] - sum / free, c.MapLo[i], c.MapHi[i]);
                }
            }

            var point = new Point { Pass = kept.Pass, Strength = kept.Strength, SlopeCorrection = kept.SlopeCorrection, LogM = logM, Balance = balance, GuardOk = GuardOk(c.StartBalance, balance) };
            Console.WriteLine(FormattableString.Invariant(
                $"Acc reweight: worth limiter cut {limited.Count(l => l)} maps: worth spread {kept.Balance.WorthSpread:0.0000} -> {balance.WorthSpread:0.0000}, mean change {kept.LogM.Average(v => Math.Abs(Math.Exp(v) - 1)) * 100:0.0}% -> {logM.Average(v => Math.Abs(Math.Exp(v) - 1)) * 100:0.0}%"));
            return (point, limited, worth);
        }

        // The top play limiter (TopPlayMarginPp, see above), at the real stars: each round, each buffed map's multiplier cut
        // to what takes its TopPlayPlayers-th player past the margin back to it (each score's pp change over the log multiplier,
        // measured), until none is past it or TopPlayRounds (the other maps' cuts move the players' best plays). Only cuts
        // buffs. limited: the maps cut.
        private static (Point Point, double?[] Worth) LimitTopPlays(Context c, Point kept, bool[] limited)
        {
            int mapCount = c.Loaded.Maps.Length;
            var logM = (double[])kept.LogM.Clone();
            c.ZeroPp ??= Measure(c, new double[mapCount], false).E.Pp;
            var zeroPp = c.ZeroPp;
            for (int round = 0; ; round++)
            {
                var measured = Measure(c, logM, false);
                var (data, e) = (measured.Data, measured.E);
                // Each player's best and second best pp, and the best's map
                var best = new double[data.PlayerCount];
                var second = new double[data.PlayerCount];
                var bestMap = new int[data.PlayerCount];
                Parallel.For(0, data.PlayerCount, p =>
                {
                    double b = 0, s2 = 0;
                    int bm = -1;
                    foreach (var s in data.PlayerScores[p])
                    {
                        double pp = e.Pp[s];
                        if (pp > b) { s2 = b; b = pp; bm = data.ScoreMap[s]; }
                        else if (pp > s2) s2 = pp;
                    }
                    (best[p], second[p], bestMap[p]) = (b, s2, bm);
                });

                // Each buffed map's multiplier (log) that takes each player past the margin back to it, the TopPlayPlayers-th
                // lowest the cut; how far past (pp) the furthest player is
                var cut = new double?[mapCount];
                var overPlayers = new int[mapCount];
                var overPp = new double[mapCount];
                Parallel.For(0, mapCount, i =>
                {
                    if (logM[i] <= 1e-6) return;
                    var allowed = new List<double>();
                    foreach (var s in data.Maps[i].Scores)
                    {
                        int p = data.ScorePlayer[s];
                        if (data.PlayerScores[p].Length < TopPlayMinScores) continue;
                        double other = bestMap[p] == i ? second[p] : best[p];
                        double pp = e.Pp[s], pp0 = zeroPp[s], cap = Math.Max(pp0, other) + TopPlayMarginPp;
                        if (other <= 0 || pp <= cap) continue;
                        overPp[i] = Math.Max(overPp[i], pp - Math.Max(pp0, other));
                        double response = pp > pp0 && pp0 > 0 ? Math.Log(pp / pp0) / logM[i] : 0;
                        allowed.Add(response <= 0 ? 0 : Math.Max(logM[i] - Math.Log(pp / cap) / response, 0));
                    }
                    overPlayers[i] = allowed.Count;
                    if (allowed.Count >= TopPlayPlayers) cut[i] = allowed.OrderBy(v => v).ElementAt(TopPlayPlayers - 1);
                });
                int over = cut.Count(v => v != null);
                var furthest = Enumerable.Range(0, mapCount).Where(i => cut[i] != null).OrderByDescending(i => overPp[i]).Take(3)
                    .Select(i => FormattableString.Invariant($"{c.Loaded.Maps[i].Leaderboard.Name} {c.Loaded.Maps[i].Leaderboard.DifficultyName} x{Math.Exp(logM[i]):0.000} +{overPp[i]:0}pp ({overPlayers[i]} players)"));
                Console.WriteLine(FormattableString.Invariant(
                    $"Acc reweight (top play limiter): round {round}: {over} buffed maps adding more than {TopPlayMarginPp:0.#}pp over their players' best other play{(over > 0 ? $", furthest: {string.Join(", ", furthest)}" : "")}, worth spread {measured.Balance.WorthSpread:0.0000} ({c.Timer.Elapsed:mm\\:ss})"));
                if (over == 0 || round == TopPlayRounds)
                {
                    var point = new Point { Pass = kept.Pass, Strength = kept.Strength, SlopeCorrection = kept.SlopeCorrection, LogM = logM, Balance = measured.Balance, GuardOk = GuardOk(c.StartBalance, measured.Balance) };
                    Console.WriteLine(FormattableString.Invariant(
                        $"Acc reweight: top play limiter cut {limited.Count(l => l)} maps: worth spread {kept.Balance.WorthSpread:0.0000} -> {measured.Balance.WorthSpread:0.0000}, mean change {kept.LogM.Average(v => Math.Abs(Math.Exp(v) - 1)) * 100:0.0}% -> {logM.Average(v => Math.Abs(Math.Exp(v) - 1)) * 100:0.0}%"));
                    return (point, measured.Worth);
                }
                for (int i = 0; i < mapCount; i++)
                {
                    if (cut[i] is not double v) continue;
                    logM[i] = Math.Min(logM[i], v);
                    limited[i] = true;
                }
            }
        }

        private static double Spread(double?[] worth)
        {
            var values = worth.Where(w => w != null).Select(w => w!.Value).ToList();
            return values.Count > 0 ? Math.Sqrt(values.Average(w => w * w)) : double.MaxValue;
        }


        private static bool GuardOk(BalanceStats start, BalanceStats b) => GuardMargins(start, b).All(m => m.Margin >= 0);

        // Each part of the balance guard: how much further from 0 it may still go (|start| + tolerance - |now|; below 0 fails)
        private static List<(string Name, double Margin, double Tolerance)> GuardMargins(BalanceStats start, BalanceStats b)
        {
            (string, double, double) Of(string name, double before, double after, double tolerance) => (name, Math.Abs(before) + tolerance - Math.Abs(after), tolerance);
            double guard = GuardTolerance, gap = GapTolerance;
            return new List<(string Name, double Margin, double Tolerance)>
            {
                Of("pass share slope", start.PassShareSlope, b.PassShareSlope, guard),
                Of("tech share slope", start.TechShareSlope, b.TechShareSlope, guard),
                Of("acc share slope", start.AccShareSlope, b.AccShareSlope, guard),
                Of("predicted acc slope", start.PredictedAccSlope, b.PredictedAccSlope, guard),
                Of("OneSaber gap", start.OneSaberGap, b.OneSaberGap, gap),
                Of("pass share gap", start.PassShareGap, b.PassShareGap, gap),
                Of("tech share gap", start.TechShareGap, b.TechShareGap, gap),
                Of("predicted acc gap", start.PredictedAccGap, b.PredictedAccGap, gap),
            };
        }

        // Each map's acc pp share at its predicted acc
        private static double[] AccShares(BalanceTuner.Data data)
        {
            var shares = new double[data.Maps.Length];
            Parallel.For(0, data.Maps.Length, m =>
            {
                var lb = data.Maps[m].Leaderboard;
                if (lb.PredictedAcc <= 0 || lb.PredictedAcc >= 1) { shares[m] = 1; return; }
                var (_, _, passPp, accPp, techPp) = ReplayUtils.PpFromScore(lb.PredictedAcc, "", null, lb.AccRating, lb.PassRating, lb.TechRating);
                double total = passPp + accPp + techPp;
                shares[m] = total > 0 ? accPp / total : 1;
            });
            return shares;
        }

        // Each map's pass and tech pp shares at its predicted acc
        private static (double[] Pass, double[] Tech) KindShares(BalanceTuner.Data data)
        {
            var pass = new double[data.Maps.Length];
            var tech = new double[data.Maps.Length];
            Parallel.For(0, data.Maps.Length, m =>
            {
                var lb = data.Maps[m].Leaderboard;
                if (lb.PredictedAcc <= 0 || lb.PredictedAcc >= 1) return;
                var (_, _, passPp, accPp, techPp) = ReplayUtils.PpFromScore(lb.PredictedAcc, "", null, lb.AccRating, lb.PassRating, lb.TechRating);
                double total = passPp + accPp + techPp;
                if (total > 0) { pass[m] = passPp / total; tech[m] = techPp / total; }
            });
            return (pass, tech);
        }

        // One indicator per star range (without the reweighting)
        private static double[][] BandFeatures(int[] baseBand)
        {
            var bands = baseBand.Distinct().OrderBy(b => b).ToList();
            return baseBand.Select(band => bands.Select(b => band == b ? 1.0 : 0.0).ToArray()).ToArray();
        }

        // Per map: the balance tuner's features (pp shares at the predicted acc, predicted acc, OneSaber) and one indicator per
        // star range (without the reweighting), which also stands for the intercept
        private static double[][] Features(BalanceTuner.Data data, int[] baseBand)
        {
            int mapCount = data.Maps.Length;
            var bands = baseBand.Distinct().OrderBy(b => b).ToList();
            var features = new double[mapCount][];
            Parallel.For(0, mapCount, m =>
            {
                var lb = data.Maps[m].Leaderboard;
                double pass = 0, tech = 0;
                if (lb.PredictedAcc > 0 && lb.PredictedAcc < 1)
                {
                    var (_, _, passPp, accPp, techPp) = ReplayUtils.PpFromScore(lb.PredictedAcc, "", null, lb.AccRating, lb.PassRating, lb.TechRating);
                    double total = passPp + accPp + techPp;
                    if (total > 0) { pass = passPp / total; tech = techPp / total; }
                }
                var row = new List<double> { pass, tech, lb.PredictedAcc * 100, BalanceTuner.IsOneSaber(lb) ? 1 : 0 };
                foreach (var b in bands) row.Add(baseBand[m] == b ? 1 : 0);
                features[m] = row.ToArray();
            });
            return features;
        }

        // target minus its weighted least squares fit on the features
        private static double?[] Residuals(double[][] features, double?[] target, double[] weight)
        {
            int mapCount = features.Length;
            var used = Enumerable.Range(0, mapCount).Where(m => target[m] != null && weight[m] > 0).ToList();
            int k = features[0].Length;
            var xtx = new double[k, k];
            var xty = new double[k];
            foreach (var m in used)
            {
                for (int a = 0; a < k; a++)
                {
                    xty[a] += weight[m] * features[m][a] * target[m]!.Value;
                    for (int b = 0; b < k; b++) xtx[a, b] += weight[m] * features[m][a] * features[m][b];
                }
            }
            for (int a = 0; a < k; a++) xtx[a, a] += 1e-6;   // keeps it solvable (e.g. no OneSaber map, an empty range)
            var beta = Solve(xtx, xty);
            var result = new double?[mapCount];
            foreach (var m in used)
            {
                double predicted = 0;
                for (int a = 0; a < k; a++) predicted += beta[a] * features[m][a];
                result[m] = target[m]!.Value - predicted;
            }
            return result;
        }

        // Gaussian elimination with partial pivoting
        private static double[] Solve(double[,] a, double[] b)
        {
            int n = b.Length;
            var m = (double[,])a.Clone();
            var x = (double[])b.Clone();
            for (int c = 0; c < n; c++)
            {
                int pivot = c;
                for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[pivot, c])) pivot = r;
                if (pivot != c)
                {
                    for (int j = 0; j < n; j++) (m[c, j], m[pivot, j]) = (m[pivot, j], m[c, j]);
                    (x[c], x[pivot]) = (x[pivot], x[c]);
                }
                if (Math.Abs(m[c, c]) < 1e-12) continue;
                for (int r = c + 1; r < n; r++)
                {
                    double f = m[r, c] / m[c, c];
                    for (int j = c; j < n; j++) m[r, j] -= f * m[c, j];
                    x[r] -= f * x[c];
                }
            }
            var result = new double[n];
            for (int r = n - 1; r >= 0; r--)
            {
                double sum = x[r];
                for (int j = r + 1; j < n; j++) sum -= m[r, j] * result[j];
                result[r] = Math.Abs(m[r, r]) < 1e-12 ? 0 : sum / m[r, r];
            }
            return result;
        }

        // The loaded data with each map's acc ratings at base x multiplier (stars and star ranges follow, or with starsFrom
        // its stars and star ranges)
        private static BalanceTuner.Data WithMultipliers(BalanceTuner.Data source, double[] baseAcc, double[,] baseModAcc, double[] logM, BalanceTuner.Data? starsFrom = null, BalanceTuner.Data? bandFrom = null)
        {
            var maps = new BalanceTuner.MapInfo[source.Maps.Length];
            Parallel.For(0, maps.Length, i =>
            {
                var lb = source.Maps[i].Leaderboard;
                double m = Math.Exp(logM[i]);
                var copy = new Leaderboard
                {
                    Id = lb.Id, Name = lb.Name, Hash = lb.Hash, ModeName = lb.ModeName, DifficultyName = lb.DifficultyName,
                    PredictedAcc = lb.PredictedAcc, Curve = lb.Curve, HitDistance = lb.HitDistance, HardSwingRatio = lb.HardSwingRatio,
                    PassRating = lb.PassRating, TechRating = lb.TechRating,
                    AccRating = (float)(baseAcc[i] * m),
                };
                var mod = lb.ModifiersRating;
                if (mod != null)
                {
                    copy.ModifiersRating = new ModifiersRating
                    {
                        Id = mod.Id,
                        SSPredictedAcc = mod.SSPredictedAcc, SSPassRating = mod.SSPassRating, SSTechRating = mod.SSTechRating, SSAccRating = (float)(baseModAcc[i, 0] * m),
                        FSPredictedAcc = mod.FSPredictedAcc, FSPassRating = mod.FSPassRating, FSTechRating = mod.FSTechRating, FSAccRating = (float)(baseModAcc[i, 1] * m),
                        SFPredictedAcc = mod.SFPredictedAcc, SFPassRating = mod.SFPassRating, SFTechRating = mod.SFTechRating, SFAccRating = (float)(baseModAcc[i, 2] * m),
                        SSCurve = mod.SSCurve, FSCurve = mod.FSCurve, SFCurve = mod.SFCurve,
                    };
                }
                float real = ReplayUtils.ToStars(copy.AccRating, copy.PassRating, copy.TechRating, copy.Curve);
                float stars = starsFrom?.Maps[i].Stars ?? real;
                // The star range follows the stars unless given separately (MeasureMixed)
                int band = (bandFrom ?? starsFrom)?.Maps[i].StarBand ?? BalanceTuner.StarBandOf(real);
                maps[i] = new BalanceTuner.MapInfo { Leaderboard = copy, Stars = stars, StarBand = band, Scores = source.Maps[i].Scores };
            });
            return new BalanceTuner.Data
            {
                Maps = maps,
                ScoreMap = source.ScoreMap,
                ScorePlayer = source.ScorePlayer,
                ScoreAcc = source.ScoreAcc,
                ScoreModifiers = source.ScoreModifiers,
                ScoreSpeed = source.ScoreSpeed,
                ScorePpSpeed = source.ScorePpSpeed,
                ScoreTime = source.ScoreTime,
                PlayerScores = source.PlayerScores,
                PlayerIds = source.PlayerIds,
                PlayerCount = source.PlayerCount,
            };
        }
    }

    public class AccReweightResult
    {
        public DateTime GeneratedAt { get; set; }
        public AccReweightSettings Settings { get; set; } = new();
        // Phase 1: fitted on half the players, worth spread measured on the other half (HeldOutSpread), every split (Split)
        public List<AccReweightRound> HeldOut { get; set; } = new();
        // Phase 2: every player; KeptRound is the index of the multipliers kept (0: none)
        public List<AccReweightRound> Rounds { get; set; } = new();
        public int KeptRound { get; set; }
        // The slope correction's points (SlopeCorrection: the pass, tech and predicted acc terms), from the kept round;
        // Applied is the one stored (empty: off, older runs)
        public List<AccReweightRound> SlopeCorrections { get; set; } = new();
        // The drift compensation's rounds (empty: off, older runs)
        public List<AccReweightDrift> DriftCompensation { get; set; } = new();
        // The frozen stars' rounds (Drift: the real stars' slopes minus the fit's; empty: off, older runs)
        public List<AccReweightDrift> FrozenStars { get; set; } = new();
        // The fraction of each map's gap the fit closed (Settings.Applied; 0: nothing changed. Older runs: the fraction of the
        // kept change stored, Applied or less when that failed the balance guard)
        public double AppliedFraction { get; set; }
        // Maps whose multiplier the worth limiter cut
        public int MapsLimited { get; set; }
        // Maps whose multiplier the top play limiter cut
        public int MapsTopPlayLimited { get; set; }
        // The multipliers stored (the kept ones)
        public AccReweightRound? Applied { get; set; }
        // The point that failed the balance guard at the real stars (null: none); a fallback may have been stored instead
        public AccReweightRound? Rejected { get; set; }
        // After Rejected, the points to store of the lower strengths tried, in order (the last is stored if it passes the guard)
        public List<AccReweightRound> Fallbacks { get; set; } = new();
        // How far the point stored is from failing each part of the balance guard at the real stars (empty: nothing stored)
        public Dictionary<string, double> GuardMargins { get; set; } = new();
        // Maps whose acc rating changed, lowest multiplier first
        public List<AccReweightMap> Maps { get; set; } = new();
    }

    public class AccReweightSettings
    {
        // BalanceTuner.WorthMeasure the run used (empty: Megametric75, before it could be chosen)
        public string WorthMeasure { get; set; } = "";
        public int Passes { get; set; }
        public double[] Strengths { get; set; } = Array.Empty<double>();
        public double EvidenceScores { get; set; }
        public double MinAccShare { get; set; }
        public double MaxChange { get; set; }
        public double Applied { get; set; } = 1;
        public double GuardTolerance { get; set; }
        public double GapTolerance { get; set; }
        public bool SlopeCorrection { get; set; }
        public double SlopeCorrectionTolerance { get; set; }
        public double WorthLimit { get; set; }
        public int HeldOutSplits { get; set; } = 1;
        public bool FitAtBaseStars { get; set; }
        public double ResponseNoise { get; set; }
        public int DriftCompensationRounds { get; set; }
        public int FrozenStarsRounds { get; set; }
        public double GuardMarginWarning { get; set; }
        public bool FallbackStrengths { get; set; }
        public bool StarResponse { get; set; }
        public bool TargetMetric { get; set; }
        public double NoiseDeadZone { get; set; }
        public double NoisePerScore { get; set; }
        public double LargeGapMaxChange { get; set; }
        public double TopPlayMarginPp { get; set; }
        public int TopPlayPlayers { get; set; }
        public int TopPlayMinScores { get; set; }
        public int MinNotes { get; set; }
    }

    // A round of the drift compensation: pass share, tech share and predicted acc slopes
    public class AccReweightDrift
    {
        public int Round { get; set; }
        // The real stars' slopes minus the base stars' at the kept point (the fit after aims at the start's minus this)
        public double[] Drift { get; set; } = Array.Empty<double>();
        // The real stars' slopes minus the start's, before and after the round (empty: the round kept nothing)
        public double[] RealOffBefore { get; set; } = Array.Empty<double>();
        public double[] RealOffAfter { get; set; } = Array.Empty<double>();
        // Closer to the start's than before, so kept
        public bool Kept { get; set; }
    }

    public class AccReweightRound
    {
        // Phase 1's split (HeldOut; -1: every player)
        public int Split { get; set; } = -1;
        public int Round { get; set; }
        public double Strength { get; set; }
        // The slope correction's pass, tech and predicted acc terms (empty: none)
        public double[] SlopeCorrection { get; set; } = Array.Empty<double>();
        public BalanceStats Balance { get; set; } = new();
        public bool GuardOk { get; set; }
        public double? HeldOutSpread { get; set; }
        public int MapsChanged { get; set; }
        public double MeanAbsChange { get; set; }
    }

    public class AccReweightMap
    {
        public string Id { get; set; } = "";
        public string? Name { get; set; }
        public string? Difficulty { get; set; }
        public string? Mode { get; set; }
        public double Multiplier { get; set; }
        // Worth without reweighting and with the multiplier stored (null: no worth, or older runs)
        public double? WorthBefore { get; set; }
        public double? WorthAfter { get; set; }
        // What its kind of map is worth (the balance features' fit) and its target, Applied of the way there from WorthBefore
        // (null: no worth, or older runs)
        public double? KindWorth { get; set; }
        public double? Target { get; set; }
        // The worth limiter cut its multiplier
        public bool Limited { get; set; }
        // The top play limiter cut its multiplier
        public bool TopPlayLimited { get; set; }
    }
}
