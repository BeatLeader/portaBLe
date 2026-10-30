using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;

namespace portaBLe.Refresh
{
    // Acc reweighter: the only tool that uses scores to change ratings. It scales each map's acc rating (and its SS / FS /
    // SF acc ratings) so the map is worth about as much to players as other maps of its kind, without moving the balance
    // between kinds of maps beyond what the balance guard allows.
    //
    // Worth: BalanceTuner.Worth with WorthMeasures.PlayerAdjusted, every score re-scored in memory with the multipliers tried.
    // Target: before any change, worth is fitted on the balance tuner's features (pass / tech pp shares, predicted acc,
    // OneSaber, star range); that fit is what the map's kind is worth, and each map's target is Applied of the way from its
    // worth to it (in log). Imbalances between kinds stay the formula's and the tuner's to fix.
    // Direction: -evidence x log((1 + worth) / (1 + target)) / response; the response is the acc pp share (at least
    // MinAccShare) times how strongly the map's worth has been seen to move with its multiplier (StarResponse, Responses).
    // Passes: each tries Strengths along the direction and keeps the one closest to the targets. Fixed steps oscillate (worth
    // responds non-linearly), and any spread of multipliers raises acc heavy maps' worth, which the later passes pull back.
    // Held-out choice: phase 1 fits on half the players and measures on the other half (HeldOutSplits splits) to find how
    // far the data supports going (pass, strength); phase 2 fits on every player up to that point.
    // FitAtBaseStars: during the fit maps keep their unreweighted stars and star range, so worth follows the map's pp alone;
    // at the real stars the slopes are too non-linear for the slope correction.
    // Balance guard: the tuner's slopes and gaps may not get worse than the start's by more than GuardTolerance /
    // GapTolerance. Maps must be classified by unreweighted pp shares, or the slopes measure the reweighting itself.
    // Slope correction: correcting single maps drifts the slopes by itself, so every candidate gets per-map terms on pass
    // share, tech share and predicted acc (a Newton step from the start's Jacobian, then a Broyden search after phase 2).
    // Limiters, at the real stars after the fit: worth limiter (WorthLimit), top play limiter (TopPlayMarginPp), top play
    // share limiter (TopPlayShareRatio). The point stored must pass the guard at the real stars, else lower strengths are
    // tried (FallbackStrengths), else nothing changes.
    //
    // Maps without a worth (too few scores), under MinWorthScores, MinStars or MinNotes keep x1; each star range's median map is x1.
    // Stored in Leaderboards.AccReweight (0 or 1 = none) and applied by RatingsRefresh, so it survives re-rating; each run
    // starts again from the ratings without it. Re-score afterwards (Program.RefreshEverything). Writes acc-reweight.json.
    public static class AccReweighter
    {
        public const string FileName = "acc-reweight.json";

        public static int Passes { get; set; } = 3;
        // Fractions of the direction tried in each pass. About 1.3 closes a map's gap in one pass (worth moves ~0.6x the map's
        // pp, pp ~1.2x acc share x the multiplier); 1.6 and 2.0 keep pass 1 off the edge of the range
        public static double[] Strengths { get; set; } = { 0.1, 0.2, 0.35, 0.5, 0.7, 1.0, 1.3, 1.6, 2.0 };
        public static double EvidenceScores { get; set; } = 40;
        // How many of a pass's candidate strengths are measured at once (each re-scores every score in memory: ~150 MB)
        public static int CandidateParallelism { get; set; } = 4;
        public static double MinAccShare { get; set; } = 0.3;
        // Bounds: Applied x +-log(1 / (1 - MaxChange)), the same distance both ways in log (x0.922-x1.085 with Applied 0.7)
        public static double MaxChange { get; set; } = 0.11;
        // Fraction of each map's gap to its target the fit closes (in log; 1 = all). The full fit was too strong for players.
        // It is part of the targets, not a scaling of the result afterwards: the passes are fitted together (later ones undo the
        // first's drift), so a fraction of the finished fit drifts the balance more than the whole
        public static double Applied { get; set; } = 0.7;
        // Balance guard tolerances for the slopes and the gaps: about 4x the measurement noise of the noisiest (tech slope,
        // OneSaber gap). The drift is real (set by the first pass), so these decide how strong a reweight is let through
        public static double GuardTolerance { get; set; } = 0.004;
        public static double GapTolerance { get; set; } = 0.0075;
        // Warn when the point stored is within this fraction of a tolerance of failing the guard: a small change to the balance
        // may then reject the reweight
        public static double GuardMarginWarning { get; set; } = 0.25;
        // When the point to store fails the guard at the real stars, refit at the lower strengths of the last pass and store the
        // first that passes, instead of nothing
        public static bool FallbackStrengths { get; set; } = true;
        // Phase 1's random splits of the players, averaged so the choice doesn't rest on one split
        public static int HeldOutSplits { get; set; } = 3;
        // Responses: how much of its own worth change a map's multiplier must make before its response counts (in log: a map
        // moved by ResponseNoise counts half, the rest is the maps around it), and how far it may be from other maps' of its acc
        // share (times or over). 0.016: 0.02 x 0.82, as WorthLimit
        public static double ResponseNoise { get; set; } = 0.016;
        public static double ResponseRange { get; set; } = 3;
        // Fit at each map's stars and star range without reweighting (see above)
        public static bool FitAtBaseStars { get; set; } = true;
        // Slope correction (see above): how close to the start's slopes the search after phase 2 must get, the Jacobian's step
        // per term (log multiplier per within-range sd, over the acc share), the largest term, and the Newton steps at most
        public static bool SlopeCorrection { get; set; } = true;
        public static double SlopeCorrectionTolerance { get; set; } = 0.0005;
        public static double SlopeCorrectionStep { get; set; } = 0.005;
        public static double SlopeCorrectionMax { get; set; } = 0.1;
        public static int SlopeCorrectionSteps { get; set; } = 5;
        // The slope search again at the real stars. Off: it restores the stored slopes but moves maps by their kind rather than
        // their gap (worth spread up, pass heavy low tech maps buffed to the cap)
        public static bool RealStarsSlopeCorrection { get; set; } = false;
        // Drift compensation: rounds of phase 2 and the slope search aimed at the start's slopes minus the real stars' drift
        // (0 = off). Off: the terms move each map's stars, so the drift grows with them and the real slopes overshoot
        public static int DriftCompensationRounds { get; set; } = 0;
        // Frozen stars: rounds of phase 2 and the slope search with each map's stars and star range frozen at the last fit's
        // real ones (0 = off). Off: it fixes the slopes by moving maps that had no gap, to a much stronger point
        public static int FrozenStarsRounds { get; set; } = 0;
        // Worth limiter (see above): how far past average (worth 0) a change may take a map's worth, about the worth spread
        // after the reweight (0 = off), and the measure-and-cut rounds at most. A map's worth may not end above max(its
        // unreweighted worth, +WorthLimit) nor below min(it, -WorthLimit); maps whose stars jump otherwise pass their neighbours.
        // 0.025: 0.03 x 0.82, the P75 worth's spread against the mean's (BalanceTuner.PlayerAdjustedQuantile)
        public static double WorthLimit { get; set; } = 0.025;
        public static int WorthLimitRounds { get; set; } = 8;
        // Star response: a random +-StarResponseProbe probe measures how much worth moves with the multiplier over the acc share,
        // at the base and real stars, smoothed over stars (StarResponseBandwidth). Pass 1's step is divided by the base stars'
        // response (relative to the median map's), and the stored multipliers are scaled by base / real response (0.5x-2x) so
        // they move worth at the real stars as the fit did at the base stars (low star maps respond about half as much there)
        public static bool StarResponse { get; set; } = true;
        // Judge strengths by each map's distance to its own target. Otherwise the choice pulls every map towards 0, past the
        // target of maps whose kind is worth more or less than average
        public static bool TargetMetric { get; set; } = true;
        public static double StarResponseProbe { get; set; } = 0.03;
        public static double StarResponseBandwidth { get; set; } = 1.0;
        // Noise dead zone: each map's gap (log) is shrunk by gap² / (gap² + (NoiseDeadZone x noise)²), noise = NoisePerScore /
        // sqrt(the scores its worth rests on), replacing the n / (n + EvidenceScores) weight: gaps within the noise barely move
        // and large, well measured ones move almost fully. 0 = off. NoisePerScore 0.10: the P75 worth's measured noise per score
        // (BalanceTuner.WorthNoise, portaBLe worthnoise: 0.102, the mean's 0.126; the mean's 0.28 before was 2.2x its measured
        // noise and held back gaps of 4.5x the P75's)
        public static double NoiseDeadZone { get; set; } = 2;
        public static double NoisePerScore { get; set; } = 0.10;
        // Large gaps: a map whose gap (log, after the dead zone) is beyond LargeGapFrom may go past MaxChange's bounds, up to
        // LargeGapMaxChange's from LargeGapFull on, linearly in between; MaxChange alone held back the maps furthest off. Higher
        // values make stars jump too far. 0 = off
        public static double LargeGapMaxChange { get; set; } = 0.16;
        // Low-star nerfs: a nerfed map's bound is LowStarMaxChange's instead, up to LowStarToleranceStart stars, fading back to
        // its own at LowStarToleranceEnd; buffs keep theirs. The acc curve top compression already lowers these maps' top
        // scores, so the full bound overnerfed them. 0 = off
        public static double LowStarMaxChange { get; set; } = 0.10;
        // 0.033 / 0.066: 0.04 / 0.08 x 0.82, as WorthLimit
        public static double LargeGapFrom { get; set; } = 0.033;
        public static double LargeGapFull { get; set; } = 0.066;
        // Low star tolerance: an overpaying low-star map keeps LowStarTolerance of its gap (log, after the dead zone)
        // uncorrected, up to LowStarToleranceStart stars, fading to none at LowStarToleranceEnd; underpaying maps aren't affected.
        // A nerf cuts every score of a map alike and pushed acc players' best low-star plays off their profile's first page.
        // 0 = off
        public static double LowStarTolerance { get; set; } = 0.13;
        public static double LowStarToleranceStart { get; set; } = 4;
        public static double LowStarToleranceEnd { get; set; } = 5;
        // Each star range's median multiplier is 1, not its mean (log): the asymmetric parts (low star tolerance, bounds,
        // limiters' cuts) shift the mean, and removing it would move every map of the range. The worth limiter centers the same
        // way; the star response probe keeps the mean (a +-probe's median is one of the two)
        public static bool MedianCenter { get; set; } = true;
        // Maps with fewer color notes (Leaderboard.NoteCount, 0 = unknown: moved) keep x1: their worth is low because players
        // can't reach the steep top of their curve (CurveSettings.LowNoteStart), not because of their acc rating, so a buff
        // only raises what a 100% pays. Their scores still count in every player's worth and the balance. 0 = off
        public static int MinNotes { get; set; } = 115;
        // Maps under MinStars stars (without reweighting) keep x1 too: the acc curve (CurveSettings.Table's 99%+ rows,
        // LowStarBonus) already sets what they pay, and the worth measure overnerfs acc players' best low-star plays. Their
        // scores still count. 0 = off
        public static double MinStars { get; set; } = 4;
        // Maps whose start worth rests on fewer in-range scores (BalanceTuner.WorthEvidence) keep x1 too: their gap is all
        // noise (NoiseDeadZone leaves them nothing to correct), so only the slope terms and centering moved them, and a map at
        // the worth measure's minimum (MinScoresPerMap) loses its worth once its stars or its players' levels move. Their
        // scores still count. 0 = off
        public static int MinWorthScores { get; set; } = 40;
        // Low-star top play nerfs: maps under MinStars (kept at 1 in the fit) are nerfed after it until their
        // MinStarsNerfMinTopPlays-th best play is no more than the MinStarsNerfTopQuantile quantile of the best plays of the
        // maps within TopPlayShareBandwidth stars (unreweighted), at most LowStarMaxChange's bound; never buffed. From their top
        // plays, not the worth measure, which is off for most low-star maps: only the few farm maps whose top plays stand out
        // move (e.g. let you Easy). Not in the fit, so their star range's median stays 1. false = off
        public static bool MinStarsNerfs { get; set; } = true;
        public static int MinStarsNerfMinTopPlays { get; set; } = 2;
        public static double MinStarsNerfTopQuantile { get; set; } = 0.9;
        public static int MinStarsNerfRounds { get; set; } = 6;
        // Top play limiter: a buffed map's score may pay at most TopPlayMarginPp more than the higher of its unreweighted pp and
        // the player's best play on another map (players with TopPlayMinScores scores or more); the buff is cut until fewer than
        // TopPlayPlayers players are past that. Only what the buff adds is limited: many maps are far over their players' second
        // best without reweighting, and capping buffs outright failed the balance guard. 0 = off
        public static double TopPlayMarginPp { get; set; } = 10;
        public static int TopPlayPlayers { get; set; } = 1;
        public static int TopPlayMinScores { get; set; } = 30;
        public static int TopPlayRounds { get; set; } = 6;
        // Top play share limiter (the nerf side), for maps far more players' best play than the maps of their stars, which the
        // worth measure misses. A map's share: of its players with TopPlayShareMinScores ranked plays or more (any map may be the
        // best of players with few), those whose best play it is by TopPlayMarginPp. Past TopPlayShareRatio x the median share of
        // the maps within TopPlayShareBandwidth stars (with TopPlayShareMinPlayers such players, at least TopPlayShareFloor), with
        // TopPlayShareMinTops or more, its multiplier is cut (down to its lower bound) until no more than that many are left,
        // from each score's pp response to a TopPlayShareProbe nerf, TopPlayRounds at most. 0 = off
        public static double TopPlayShareRatio { get; set; } = 3;
        public static int TopPlayShareMinScores { get; set; } = 75;
        public static int TopPlayShareMinTops { get; set; } = 10;
        public static int TopPlayShareMinPlayers { get; set; } = 30;
        public static double TopPlayShareBandwidth { get; set; } = 1;
        public static double TopPlayShareFloor { get; set; } = 0.005;
        public static double TopPlayShareProbe { get; set; } = 0.03;

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
            // 1: the map has a worth and is moved (not pinned)
            public double[] HasWorth = Array.Empty<double>();
            // Under MinNotes, MinStars or MinWorthScores: kept at 1
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
            var lowNotes = loaded.Maps.Select(m => m.Leaderboard.NoteCount > 0 && m.Leaderboard.NoteCount < MinNotes).ToArray();
            var lowStars = context.Unweighted.Maps.Select(m => m.Stars < MinStars).ToArray();
            var startEvidence = BalanceTuner.WorthEvidence(startMeasure.Data, startMeasure.E);
            var lowEvidence = context.StartWorth.Select((w, i) => MinWorthScores > 0 && w != null && startEvidence[i] < MinWorthScores).ToArray();
            context.Pinned = lowNotes.Select((p, i) => p || lowStars[i] || lowEvidence[i]).ToArray();
            context.HasWorth = context.StartWorth.Select((w, i) => w != null && !context.Pinned[i] ? 1.0 : 0.0).ToArray();
            if (MinNotes > 0) Console.WriteLine($"Acc reweight: {lowNotes.Count(p => p)} maps under {MinNotes} notes kept at 1");
            if (MinStars > 0) Console.WriteLine(FormattableString.Invariant($"Acc reweight: {lowStars.Count(p => p)} maps under {MinStars:0.##} stars kept at 1"));
            if (MinWorthScores > 0) Console.WriteLine($"Acc reweight: {lowEvidence.Count(p => p)} maps with a worth from under {MinWorthScores} scores kept at 1");
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
            // Each (pass, strength) over every split: its average held-out spread, and whether it passes the guard on all. The
            // lowest that passes, else the lowest overall
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
            var shareLimited = new bool[mapCount];
            (Point Point, bool[] Limited, double?[]? Worth) Finish(Point p)
            {
                topLimited = new bool[mapCount];
                shareLimited = new bool[mapCount];
                if (p == start) return (p, new bool[mapCount], null);
                // StarResponse: scaled so they move worth at the real stars as the fit did at the base stars
                if (StarResponse && FitAtBaseStars)
                    p = new Point { Pass = p.Pass, Strength = p.Strength, SlopeCorrection = p.SlopeCorrection, LogM = Clean(context, p.LogM.Select((v, i) => v * context.StarScale[i]).ToArray(), context.HasWorth), Balance = p.Balance, GuardOk = p.GuardOk };
                (Point Point, bool[] Limited, double?[]? Worth) finished = WorthLimit > 0 ? LimitWorth(context, p) : (p, new bool[mapCount], null);
                if (TopPlayMarginPp > 0)
                {
                    var (limitedPoint, worth) = LimitTopPlays(context, finished.Point, topLimited);
                    if (TopPlayShareRatio > 0) (limitedPoint, worth) = LimitTopPlayShares(context, limitedPoint, shareLimited);
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
                    shareLimited = new bool[mapCount];
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

            // Applied is part of the fit (the targets), so the kept point is stored as is, with the low-star nerfs (MinStarsNerfs)
            var applied = kept;
            var lowStarNerfed = new bool[mapCount];
            if (MinStarsNerfs && MinStars > 0)
            {
                var lowStarLogM = LowStarNerfs(context, lowStars, lowNotes);
                if (lowStarLogM.Any(v => v != 0))
                {
                    var logM = (double[])kept.LogM.Clone();
                    for (int i = 0; i < mapCount; i++)
                    {
                        if (lowStarLogM[i] == 0) continue;
                        logM[i] = lowStarLogM[i];
                        lowStarNerfed[i] = true;
                    }
                    applied = new Point { Split = kept.Split, Pass = kept.Pass, Strength = kept.Strength, SlopeCorrection = kept.SlopeCorrection, LogM = logM, Balance = kept.Balance, GuardOk = kept.GuardOk, HeldOutSpread = kept.HeldOutSpread };
                }
            }
            double fraction = applied != start ? Applied : 0;

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
                Settings = new AccReweightSettings { WorthMeasure = BalanceTuner.WorthMeasureLabel, Passes = Passes, Strengths = Strengths, EvidenceScores = EvidenceScores, MinAccShare = MinAccShare, MaxChange = MaxChange, Applied = Applied, GuardTolerance = GuardTolerance, GapTolerance = GapTolerance, SlopeCorrection = SlopeCorrection, SlopeCorrectionTolerance = SlopeCorrectionTolerance, WorthLimit = WorthLimit, HeldOutSplits = HeldOutSplits, FitAtBaseStars = FitAtBaseStars, ResponseNoise = ResponseNoise, DriftCompensationRounds = DriftCompensationRounds, FrozenStarsRounds = FrozenStarsRounds, GuardMarginWarning = GuardMarginWarning, FallbackStrengths = FallbackStrengths, StarResponse = StarResponse, TargetMetric = TargetMetric, NoiseDeadZone = NoiseDeadZone, NoisePerScore = NoisePerScore, LargeGapMaxChange = LargeGapMaxChange, TopPlayMarginPp = TopPlayMarginPp, TopPlayPlayers = TopPlayPlayers, TopPlayMinScores = TopPlayMinScores, TopPlayShareRatio = TopPlayShareRatio, TopPlayShareMinScores = TopPlayShareMinScores, TopPlayShareMinTops = TopPlayShareMinTops, MinNotes = MinNotes, LowStarTolerance = LowStarTolerance, LowStarMaxChange = LowStarMaxChange, MedianCenter = MedianCenter, MinStars = MinStars, MinWorthScores = MinWorthScores, MinStarsNerfs = MinStarsNerfs, MinStarsNerfMinTopPlays = MinStarsNerfMinTopPlays, MinStarsNerfTopQuantile = MinStarsNerfTopQuantile },
                MapsLimited = limited.Count(l => l),
                MapsTopPlayLimited = topLimited.Count(l => l),
                MapsTopPlayShareLimited = shareLimited.Count(l => l),
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
                        TopPlayShareLimited = shareLimited[i],
                        LowStarNerfed = lowStarNerfed[i],
                    })
                    .Where(m => Math.Abs(m.Multiplier - 1) > 0.0005)
                    .OrderBy(m => m.Multiplier)
                    .ToList(),
            };
            var path2 = System.IO.Path.Combine(webRootPath, FileName);
            await File.WriteAllTextAsync(path2, JsonSerializer.Serialize(result));
            Console.WriteLine($"Acc reweight saved to {path2} ({timer.Elapsed:mm\\:ss}). Re-score next (RefreshEverything).");
        }

        // MinStarsNerfs (see above): each map's log multiplier (0: not nerfed). Its scores re-scored each round (their pp
        // response to the multiplier, measured), MinStarsNerfRounds at most.
        private static double[] LowStarNerfs(Context c, bool[] lowStars, bool[] lowNotes)
        {
            var data = c.Unweighted;
            int mapCount = data.Maps.Length;
            var result = new double[mapCount];
            var e = BalanceTuner.Evaluate(data);
            int k = Math.Max(MinStarsNerfMinTopPlays, 1);
            double KthBest(BalanceTuner.Data d, BalanceTuner.Evaluation ev, int i)
            {
                var pp = d.Maps[i].Scores.Select(s => ev.Pp[s]).OrderByDescending(v => v).ToList();
                return pp.Count >= k ? pp[k - 1] : 0;
            }
            // Each map's best play, and each candidate's level: its neighbours' quantile of them
            var best = data.Maps.Select(m => m.Scores.Length > 0 ? m.Scores.Max(s => e.Pp[s]) : 0).ToArray();
            var level = new double[mapCount];
            var kth0 = new double[mapCount];
            var candidates = new List<int>();
            for (int i = 0; i < mapCount; i++)
            {
                if (!lowStars[i] || lowNotes[i]) continue;
                double stars = data.Maps[i].Stars;
                var near = Enumerable.Range(0, mapCount).Where(j => best[j] > 0 && Math.Abs(data.Maps[j].Stars - stars) <= TopPlayShareBandwidth)
                    .Select(j => best[j]).OrderBy(v => v).ToList();
                if (near.Count == 0) continue;
                level[i] = near[Math.Min((int)(MinStarsNerfTopQuantile * near.Count), near.Count - 1)];
                kth0[i] = KthBest(data, e, i);
                if (kth0[i] > level[i]) candidates.Add(i);
            }
            double bound = Applied * -Math.Log(1 - LowStarMaxChange);
            // Each round: re-score with the current multipliers, then each candidate's step from its pp response so far
            // (log pp per log multiplier; 1.1 to start, about acc pp's share x AccRatingPower on these maps)
            var response = candidates.ToDictionary(i => i, i => 1.1);
            var kth = (double[])kth0.Clone();
            for (int round = 0; round < MinStarsNerfRounds && candidates.Count > 0; round++)
            {
                bool moved = false;
                foreach (var i in candidates)
                {
                    if (kth[i] <= level[i] * 1.0005 || result[i] <= -bound) continue;
                    double step = Math.Log(level[i] / kth[i]) / response[i];
                    result[i] = Math.Max(result[i] + step, -bound);
                    moved = true;
                }
                if (!moved) break;
                var d = WithMultipliers(c.Loaded, c.BaseAcc, c.BaseModAcc, result);
                var ev = BalanceTuner.Evaluate(d);
                foreach (var i in candidates)
                {
                    double now = KthBest(d, ev, i);
                    if (result[i] < 0 && now > 0 && kth0[i] > 0) response[i] = Math.Clamp(Math.Log(now / kth0[i]) / result[i], 0.3, 2);
                    kth[i] = now;
                }
            }
            var nerfed = candidates.Where(i => result[i] < 0).OrderBy(i => result[i]).Select(i => FormattableString.Invariant(
                $"{data.Maps[i].Leaderboard.Name} {data.Maps[i].Leaderboard.DifficultyName} x{Math.Exp(result[i]):0.000} (play #{k} {kth0[i]:0} -> {kth[i]:0}pp, level {level[i]:0})")).ToList();
            Console.WriteLine(FormattableString.Invariant($"Acc reweight: low-star top play nerfs under {MinStars:0.##} stars: {nerfed.Count} maps{(nerfed.Count > 0 ? ": " + string.Join(", ", nerfed) : "")}"));
            return result;
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

        // Without each star range's center (log, over the maps with weight: its median with MedianCenter and median, else its
        // mean), then the bounds
        private static double[] Clean(Context c, double[] logM, double[] weight, bool median = true)
        {
            if (!(MedianCenter && median))
            {
                var cleaned = Residuals(c.BandFeatures, logM.Select(v => (double?)v).ToArray(), weight);
                return cleaned.Select((v, i) => Math.Clamp(v ?? 0, c.MapLo[i], c.MapHi[i])).ToArray();
            }
            var centers = BandMedians(c, logM, i => weight[i] > 0);
            return logM.Select((v, i) => weight[i] > 0 ? Math.Clamp(v - centers[c.BaseBand[i]], c.MapLo[i], c.MapHi[i]) : 0).ToArray();
        }

        // Each star range's median log multiplier over the maps included
        private static Dictionary<int, double> BandMedians(Context c, double[] logM, Func<int, bool> included)
        {
            return Enumerable.Range(0, logM.Length).Where(included).GroupBy(i => c.BaseBand[i]).ToDictionary(g => g.Key, g =>
            {
                var sorted = g.Select(i => logM[i]).OrderBy(v => v).ToList();
                int n = sorted.Count;
                return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
            });
        }

        // StarResponse (see above): the probe, at the base stars and at the real ones
        private static void StarResponses(Context c)
        {
            int mapCount = c.Loaded.Maps.Length;
            var random = new Random(4242);
            var probe = c.HasWorth.Select(h => h > 0 ? (random.Next(2) == 0 ? -StarResponseProbe : StarResponseProbe) : 0).ToArray();
            // The mean: a +-probe's median is one of the two
            probe = Clean(c, probe, c.HasWorth, false);
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
                // Overpaying low-star maps keep part of their gap (LowStarTolerance)
                if (gap > 0 && LowStarTolerance > 0)
                {
                    double stars = c.Unweighted.Maps[i].Stars;
                    double tolerance = LowStarTolerance * Math.Clamp((LowStarToleranceEnd - stars) / (LowStarToleranceEnd - LowStarToleranceStart), 0, 1);
                    gap = Math.Max(gap - tolerance, 0);
                }
                double partial = Math.Exp(Math.Log(1 + w) - Applied * gap) - 1;
                double bound = c.Hi + (large - c.Hi) * Math.Clamp((Math.Abs(gap) - LargeGapFrom) / (LargeGapFull - LargeGapFrom), 0, 1);
                if (gap > 0 && LowStarMaxChange > 0)
                {
                    double lowStar = Applied * -Math.Log(1 - LowStarMaxChange);
                    bound += (lowStar - bound) * Math.Clamp((LowStarToleranceEnd - c.Unweighted.Maps[i].Stars) / (LowStarToleranceEnd - LowStarToleranceStart), 0, 1);
                }
                c.MapLo[i] = -bound;
                c.MapHi[i] = bound;
                // Not past the worth limit (WorthLimit): a map already past it isn't pushed further
                if (WorthLimit > 0) partial = Math.Clamp(partial, Math.Min(w, -WorthLimit), Math.Max(w, WorthLimit));
                keep[i] = partial - t;
                target[i] = partial;
            }
            // Pinned maps (MinNotes, MinStars, MinWorthScores) stay at 1: every change is clamped to these bounds
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

                // Each star range keeps its center (log, over the maps with a worth): the maps not cut take it back. Median
                // (MedianCenter): its median map is 1 again, shifting the maps not cut
                if (MedianCenter)
                {
                    var centers = BandMedians(c, logM, i => c.HasWorth[i] > 0);
                    for (int i = 0; i < mapCount; i++)
                        if (c.HasWorth[i] > 0 && !limited[i]) logM[i] = Math.Clamp(logM[i] - centers[c.BaseBand[i]], c.MapLo[i], c.MapHi[i]);
                    continue;
                }
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

        // The top play share limiter (TopPlayShareRatio, see above), at the real stars: each round, each map past its share
        // cut to what takes enough of its players' best plays back within TopPlayMarginPp of their second best, until none is
        // past it or TopPlayRounds. Only cuts (down to the map's lower bound). limited: the maps cut.
        private static (Point Point, double?[] Worth) LimitTopPlayShares(Context c, Point kept, bool[] limited)
        {
            int mapCount = c.Loaded.Maps.Length;
            var logM = (double[])kept.LogM.Clone();
            for (int round = 0; ; round++)
            {
                var measured = Measure(c, logM, false);
                var (data, e) = (measured.Data, measured.E);
                var eligible = new bool[data.PlayerCount];
                var best = new double[data.PlayerCount];
                var second = new double[data.PlayerCount];
                var bestScore = new int[data.PlayerCount];
                Parallel.For(0, data.PlayerCount, p =>
                {
                    if (data.PlayerScores[p].Length < TopPlayShareMinScores) return;
                    double b = 0, s2 = 0;
                    int bs = -1;
                    foreach (var s in data.PlayerScores[p])
                    {
                        double pp = e.Pp[s];
                        if (pp > b) { s2 = b; b = pp; bs = s; }
                        else if (pp > s2) s2 = pp;
                    }
                    (eligible[p], best[p], second[p], bestScore[p]) = (bs >= 0, b, s2, bs);
                });

                // Each map's eligible players and those whose best play it is by the margin
                var players = new int[mapCount];
                var tops = new List<int>[mapCount];
                for (int i = 0; i < mapCount; i++) tops[i] = new List<int>();
                Parallel.For(0, mapCount, i =>
                {
                    var seen = new HashSet<int>();
                    foreach (var s in data.Maps[i].Scores) if (eligible[data.ScorePlayer[s]]) seen.Add(data.ScorePlayer[s]);
                    players[i] = seen.Count;
                });
                for (int p = 0; p < data.PlayerCount; p++)
                {
                    if (!eligible[p] || second[p] <= 0 || best[p] - second[p] < TopPlayMarginPp) continue;
                    tops[data.ScoreMap[bestScore[p]]].Add(p);
                }
                var share = Enumerable.Range(0, mapCount).Select(i => players[i] > 0 ? (double)tops[i].Count / players[i] : 0).ToArray();
                var counted = Enumerable.Range(0, mapCount).Where(i => players[i] >= TopPlayShareMinPlayers).ToArray();

                // The maps past their share: how many of their players' best plays may stay
                var allowedTops = new int?[mapCount];
                var reference = new double[mapCount];
                Parallel.For(0, mapCount, i =>
                {
                    if (c.Pinned[i] || c.HasWorth[i] <= 0 || tops[i].Count < TopPlayShareMinTops) return;
                    var near = counted.Where(j => Math.Abs(data.Maps[j].Stars - data.Maps[i].Stars) <= TopPlayShareBandwidth).Select(j => share[j]).OrderBy(v => v).ToList();
                    if (near.Count == 0) return;
                    reference[i] = Math.Max(near[near.Count / 2], TopPlayShareFloor);
                    int allowed = Math.Max((int)(TopPlayShareRatio * reference[i] * players[i]), TopPlayShareMinTops - 1);
                    if (tops[i].Count > allowed && logM[i] > c.MapLo[i] + 1e-6) allowedTops[i] = allowed;
                });
                int over = allowedTops.Count(v => v != null);
                var furthest = Enumerable.Range(0, mapCount).Where(i => allowedTops[i] != null).OrderByDescending(i => share[i] / reference[i]).Take(3)
                    .Select(i => FormattableString.Invariant($"{c.Loaded.Maps[i].Leaderboard.Name} {c.Loaded.Maps[i].Leaderboard.DifficultyName} x{Math.Exp(logM[i]):0.000} {share[i] * 100:0.0}% ({tops[i].Count} of {players[i]} players, near {reference[i] * 100:0.0}%)"));
                Console.WriteLine(FormattableString.Invariant(
                    $"Acc reweight (top play share limiter): round {round}: {over} maps the best play of more than {TopPlayShareRatio:0.#}x the share of the maps within {TopPlayShareBandwidth:0.#} stars{(over > 0 ? $", furthest: {string.Join(", ", furthest)}" : "")}, worth spread {measured.Balance.WorthSpread:0.0000} ({c.Timer.Elapsed:mm\\:ss})"));
                if (over == 0 || round == TopPlayRounds)
                {
                    var point = new Point { Pass = kept.Pass, Strength = kept.Strength, SlopeCorrection = kept.SlopeCorrection, LogM = logM, Balance = measured.Balance, GuardOk = GuardOk(c.StartBalance, measured.Balance) };
                    Console.WriteLine(FormattableString.Invariant(
                        $"Acc reweight: top play share limiter cut {limited.Count(l => l)} maps: worth spread {kept.Balance.WorthSpread:0.0000} -> {measured.Balance.WorthSpread:0.0000}, mean change {kept.LogM.Average(v => Math.Abs(Math.Exp(v) - 1)) * 100:0.0}% -> {logM.Average(v => Math.Abs(Math.Exp(v) - 1)) * 100:0.0}%"));
                    return (point, measured.Worth);
                }

                // Each score's pp response to its map's multiplier (log pp over log multiplier): a score's pp only depends on its
                // own map's ratings, so one nerf of every map measures them all
                var probed = BalanceTuner.Evaluate(WithMultipliers(c.Loaded, c.BaseAcc, c.BaseModAcc, logM.Select(v => v - TopPlayShareProbe).ToArray(), null)).Pp;
                for (int i = 0; i < mapCount; i++)
                {
                    if (allowedTops[i] is not int allowed) continue;
                    // The cut (log) that takes each of these players back within the margin of their second best, the
                    // (count - allowed)-th smallest leaves allowed of them
                    var needed = new List<double>();
                    foreach (int p in tops[i])
                    {
                        int s = bestScore[p];
                        double pp = e.Pp[s], cap = second[p] + TopPlayMarginPp;
                        double response = pp > 0 && probed[s] > 0 ? Math.Log(pp / probed[s]) / TopPlayShareProbe : 0;
                        needed.Add(response > 1e-6 ? Math.Log(pp / cap) / response : double.MaxValue);
                    }
                    needed.Sort();
                    double cut = needed[tops[i].Count - allowed - 1] + 1e-3;
                    logM[i] = Math.Max(logM[i] - cut, c.MapLo[i]);
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
        // The fraction of each map's gap the fit closed (Settings.Applied; 0: nothing changed)
        public double AppliedFraction { get; set; }
        // Maps whose multiplier the worth limiter cut
        public int MapsLimited { get; set; }
        // Maps whose multiplier the top play limiter cut
        public int MapsTopPlayLimited { get; set; }
        // Maps whose multiplier the top play share limiter cut
        public int MapsTopPlayShareLimited { get; set; }
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
        // BalanceTuner.WorthMeasureLabel the run used (empty: Megametric75, before it could be chosen; no quantile: the mean)
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
        public double TopPlayShareRatio { get; set; }
        public int TopPlayShareMinScores { get; set; }
        public int TopPlayShareMinTops { get; set; }
        public int MinNotes { get; set; }
        public double LowStarTolerance { get; set; }
        public double LowStarMaxChange { get; set; }
        // false: each star range's mean was centered instead
        public bool MedianCenter { get; set; }
        public double MinStars { get; set; }
        public int MinWorthScores { get; set; }
        public bool MinStarsNerfs { get; set; }
        public int MinStarsNerfMinTopPlays { get; set; }
        public double MinStarsNerfTopQuantile { get; set; }
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
        // The top play share limiter cut its multiplier
        public bool TopPlayShareLimited { get; set; }
        // Nerfed after the fit for its top plays (MinStarsNerfs)
        public bool LowStarNerfed { get; set; }
    }
}
