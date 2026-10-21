using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;

namespace portaBLe.Refresh
{
    // Acc reweighter: the only tool that uses scores to change ratings. It moves each map's acc rating up or down so the map is
    // worth about as much to players as the other maps of its kind, without changing the balance between kinds of maps.
    //
    // A map's worth is its Megametric75 (how much it counts in the profiles of the players who play it) relative to what maps
    // of its stars average (BalanceTuner.Worth: star range averages interpolated by stars, so crossing a range boundary
    // doesn't change what a map is compared with), every score re-scored in memory with the multipliers being tried
    // (players' weights and every map's worth move when one map changes).
    //
    // Target: before any change, worth is fitted on the balance tuner's features (pass and tech pp shares at the predicted acc,
    // predicted acc, OneSaber, star range; least squares, as the tuner's slopes). That fitted value is what the map's kind is
    // worth, and it stays each map's target: kinds of maps worth more or less than others are the formula's and the tuner's to
    // fix, so they keep what they had. (Without the kinds taken out, the pass share slope went +0.03 -> -0.37 in one step.)
    //
    // Direction: -evidence x log((1 + worth) / (1 + target)) / acc pp share (at least MinAccShare). evidence = n / (n +
    // EvidenceScores), n the scores its Megametric75 averages, so maps with few scores move less. An acc multiplier moves a
    // map's pp in proportion to its acc share, so a tech or pass heavy map needs more to move as much.
    //
    // Passes: in each, Strengths (fractions of the direction) are tried and the pass moves to the lowest worth spread; the next
    // pass starts again from the new worth. Fixed steps oscillated (worth moves about 3-5x as much as the acc rating, and not
    // linearly: the map moves within its players' sorted profiles). Any spread of multipliers, even random, raises the worth
    // of acc heavy maps (a buffed map climbs its players' profiles more than a nerfed one falls: random 4% multipliers moved
    // the pass share slope +0.034 -> -0.065); the later passes pull every map back to its target, kinds included.
    //
    // How far: fitted to convergence, worth spread went 0.48 -> 0.07, score noise included. So the players are split in two
    // halves: phase 1 fits on one half's scores and measures the worth spread on the other half's; the point (pass, strength)
    // where that held-out spread is lowest is how far the data supports going. Phase 2 fits on every player up to that point.
    //
    // Balance guard: the point kept must have the balance tuner's slopes (pass, acc and tech shares, predicted acc) no worse
    // than without reweighting (by GuardTolerance at most), and its gaps by GapTolerance: the gaps between fifths compare the
    // average worth of the top and bottom fifths of maps, outliers included, so they move when outliers are corrected; the
    // OneSaber gap compares 73 maps with the rest, as noisy (at GuardTolerance it rejected points by 0.002). Maps are classified by their pp shares without reweighting there (nerfing a map's acc raises its pass
    // share, and the slopes would measure the reweighting itself). When phase 2's end point doesn't pass, the point of phase 2
    // with the lowest worth spread that does is kept; without one, nothing changes.
    //
    // Maps without a worth (fewer than 20 scores, or 10 or fewer of them from players with more than 75 scores: newly ranked
    // maps) keep 1.
    // Multipliers average 1 (in log) within each star range (ranges keep their level, players' pp its scale), stay within
    // [1 - MaxChange, 1 + MaxChange]. Applied of that change (in log) is stored, and multiplies the acc rating. Stored in
    // Leaderboards.AccReweight (0 or 1 = none) and applied by RatingsRefresh (Overwrite and Refresh) to the acc rating and the
    // SS / FS / SF acc ratings, so they survive re-rating. Each run starts again from the ratings without them. Then re-score
    // (Program.RefreshEverything). Writes wwwroot/acc-reweight.json.
    public static class AccReweighter
    {
        public const string FileName = "acc-reweight.json";

        public static int Passes { get; set; } = 6;
        // Fractions of the direction tried in each pass: small, as worth moves about 3-5x as much as the acc rating
        public static double[] Strengths { get; set; } = { 0.02, 0.05, 0.08, 0.12, 0.16, 0.2, 0.3 };
        public static double EvidenceScores { get; set; } = 40;
        public static double MinAccShare { get; set; } = 0.3;
        public static double MaxChange { get; set; } = 0.15;
        // Fraction of the fitted change stored (in log): 1 = all of it, 0 = none. The full fit (mean change 4.1%, 76 maps
        // at +-15%) was too strong for players, who found the database better without it; most of the worth spread goes
        // early on the held-out path (0.49 -> 0.34 with a 1.7% mean change). Star ranges still average 1, and no map
        // moves more than Applied x MaxChange.
        public static double Applied { get; set; } = 0.5;
        public static double GuardTolerance { get; set; } = 0.005;
        public static double GapTolerance { get; set; } = 0.02;

        private sealed class Point
        {
            public int Pass;
            public double Strength;
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
            public double[][] BandFeatures = Array.Empty<double[]>();
            public System.Diagnostics.Stopwatch Timer = null!;
        }

        public static async Task Run(AppContext dbContext, string webRootPath)
        {
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

            var context = new Context
            {
                Loaded = loaded,
                BaseAcc = baseAcc,
                BaseModAcc = baseModAcc,
                BaseBand = baseBand,
                Unweighted = WithMultipliers(loaded, baseAcc, baseModAcc, new double[mapCount]),
                BandFeatures = BandFeatures(baseBand),
                Timer = timer,
            };
            context.AccShare = AccShares(context.Unweighted);

            // Phase 1: fit on half the players, check on the other half
            var random = new Random(6969);
            var half = Enumerable.Range(0, loaded.PlayerCount).Select(_ => random.Next(2) == 0).ToArray();
            var other = half.Select(h => !h).ToArray();
            var phase1 = Path(context, "held out", half, other, Passes, null);
            // The point with the lowest held-out spread among those that pass the balance guard (the early passes push kinds of
            // maps apart and the later ones pull them back: with a balanced start, the lowest held-out spread overall was at pass
            // 2, where the guard failed on every point of phase 2, so nothing was kept). Without one, the lowest overall.
            var measured = phase1.Where(p => p.HeldOutSpread != null).ToList();
            var chosen = measured.Where(p => p.Pass > 0 && p.GuardOk).OrderBy(p => p.HeldOutSpread).FirstOrDefault()
                ?? measured.OrderBy(p => p.HeldOutSpread).First();
            Console.WriteLine(FormattableString.Invariant(
                $"Acc reweight: held-out worth spread lowest at pass {chosen.Pass} strength {chosen.Strength:0.00}{(chosen.GuardOk ? "" : " (no point passes the guard)")} ({phase1[0].HeldOutSpread:0.0000} -> {chosen.HeldOutSpread:0.0000}, lowest overall {measured.Min(p => p.HeldOutSpread):0.0000})"));

            // Phase 2: every player, up to that point
            var phase2 = Path(context, "all players", null, null, chosen.Pass, chosen.Pass > 0 ? chosen.Strength : null);
            var start = phase2[0];
            var kept = phase2[^1].GuardOk ? phase2[^1] : phase2.Where(p => p.GuardOk).OrderBy(p => p.Balance.WorthSpread).First();
            Console.WriteLine(FormattableString.Invariant(
                $"Acc reweight: keeping pass {kept.Pass} strength {kept.Strength:0.00} (worth spread {start.Balance.WorthSpread:0.0000} -> {kept.Balance.WorthSpread:0.0000}), {kept.LogM.Count(v => Math.Abs(v) > 0.005)} maps changed, mean change {kept.LogM.Average(v => Math.Abs(Math.Exp(v) - 1)) * 100:0.0}%"));
            if (kept == start)
            {
                Console.WriteLine("Acc reweight: WARNING: no point of phase 2 passes the balance guard, nothing changes (see acc-reweight.json, Rounds)");
            }

            // Only Applied of the kept change is stored
            var appliedLogM = kept.LogM.Select(v => v * Applied).ToArray();
            var appliedData = WithMultipliers(loaded, baseAcc, baseModAcc, appliedLogM);
            var appliedEval = BalanceTuner.Evaluate(appliedData);
            var appliedBalance = BalanceTuner.MeasureBalance(appliedData, appliedEval, BalanceTuner.Worth(appliedData, appliedEval, out _), context.Unweighted);
            var applied = new Point { Pass = kept.Pass, Strength = kept.Strength, LogM = appliedLogM, Balance = appliedBalance, GuardOk = GuardOk(start.Balance, appliedBalance) };
            Console.WriteLine(FormattableString.Invariant(
                $"Acc reweight: applying {Applied:0.00} of it: worth spread {applied.Balance.WorthSpread:0.0000}, {applied.LogM.Count(v => Math.Abs(v) > 0.005)} maps changed, mean change {applied.LogM.Average(v => Math.Abs(Math.Exp(v) - 1)) * 100:0.0}%{(applied.GuardOk ? "" : " (guard)")}"));

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
                Round = p.Pass,
                Strength = p.Strength,
                Balance = p.Balance,
                GuardOk = p.GuardOk,
                HeldOutSpread = p.HeldOutSpread,
                MapsChanged = p.LogM.Count(v => Math.Abs(v) > 0.005),
                MeanAbsChange = p.LogM.Average(v => Math.Abs(Math.Exp(v) - 1)),
            };
            var result = new AccReweightResult
            {
                GeneratedAt = DateTime.UtcNow,
                Settings = new AccReweightSettings { Passes = Passes, Strengths = Strengths, EvidenceScores = EvidenceScores, MinAccShare = MinAccShare, MaxChange = MaxChange, Applied = Applied, GuardTolerance = GuardTolerance, GapTolerance = GapTolerance },
                HeldOut = phase1.Select(ToRound).ToList(),
                Rounds = phase2.Select(ToRound).ToList(),
                KeptRound = phase2.IndexOf(kept),
                Applied = ToRound(applied),
                Maps = Enumerable.Range(0, mapCount)
                    .Select(i => new AccReweightMap
                    {
                        Id = loaded.Maps[i].Leaderboard.Id,
                        Name = loaded.Maps[i].Leaderboard.Name,
                        Difficulty = loaded.Maps[i].Leaderboard.DifficultyName,
                        Mode = loaded.Maps[i].Leaderboard.ModeName,
                        Multiplier = Math.Round(Math.Exp(applied.LogM[i]), 4),
                    })
                    .Where(m => Math.Abs(m.Multiplier - 1) > 0.0005)
                    .OrderBy(m => m.Multiplier)
                    .ToList(),
            };
            var path = System.IO.Path.Combine(webRootPath, FileName);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result));
            Console.WriteLine($"Acc reweight saved to {path} ({timer.Elapsed:mm\\:ss}). Re-score next (RefreshEverything).");
        }

        // The passes: fit on fitPlayers (null = all), measure the held-out worth spread on checkPlayers (null = none). With
        // lastStrength, the last pass only tries that strength, so the last point is where the path ends.
        private static List<Point> Path(Context c, string label, bool[]? fitPlayers, bool[]? checkPlayers, int passes, double? lastStrength)
        {
            int mapCount = c.Loaded.Maps.Length;
            double lo = Math.Log(1 - MaxChange), hi = Math.Log(1 + MaxChange);
            var ones = Enumerable.Repeat(1.0, mapCount).ToArray();
            var points = new List<Point>();
            BalanceStats? start = null;

            (BalanceTuner.Data Data, BalanceTuner.Evaluation E, double?[] Worth, Point Point) Measure(double[] logM, int pass, double strength)
            {
                var data = WithMultipliers(c.Loaded, c.BaseAcc, c.BaseModAcc, logM);
                var e = BalanceTuner.Evaluate(data);
                var worthAll = BalanceTuner.Worth(data, e, out _);
                var balance = BalanceTuner.MeasureBalance(data, e, worthAll, c.Unweighted);
                var fitWorth = fitPlayers == null ? worthAll : BalanceTuner.Worth(data, e, out _, fitPlayers);
                double? heldOut = checkPlayers == null ? null : Spread(BalanceTuner.Worth(data, e, out _, checkPlayers));
                start ??= balance;
                var point = new Point { Pass = pass, Strength = strength, LogM = logM, Balance = balance, HeldOutSpread = heldOut, GuardOk = GuardOk(start, balance) };
                points.Add(point);
                Console.WriteLine(FormattableString.Invariant(
                    $"Acc reweight ({label}): pass {pass} strength {strength:0.00}: worth spread {balance.WorthSpread:0.0000}{(heldOut is double h ? $" (held out {h:0.0000})" : "")}, pass slope {balance.PassShareSlope:+0.000;-0.000}, tech slope {balance.TechShareSlope:+0.000;-0.000}, acc slope {balance.AccShareSlope:+0.000;-0.000}, predicted acc gap {balance.PredictedAccGap:+0.000;-0.000}, one saber gap {balance.OneSaberGap:+0.000;-0.000}, mean change {logM.Average(v => Math.Abs(Math.Exp(v) - 1)) * 100:0.0}%{(point.GuardOk ? "" : " (guard)")} ({c.Timer.Elapsed:mm\\:ss})"));
                return (data, e, fitWorth, point);
            }

            var logM = new double[mapCount];
            var current = Measure(logM, 0, 0);

            // Each map's target: what the balance features predict for its worth before any change (fitted players' scores)
            var target = new double?[mapCount];
            var linear0 = current.Worth.Select(w => w is double v && v > -0.99 ? (double?)v : null).ToArray();
            var residual0 = Residuals(Features(c.Unweighted, c.BaseBand), linear0, ones);
            for (int i = 0; i < mapCount; i++) if (linear0[i] is double v && residual0[i] is double r) target[i] = Math.Max(v - r, -0.9);
            // Maps without a worth (too few scores, e.g. newly ranked) stay at 1 and don't count in their star range's mean
            var hasTarget = target.Select(t => t != null ? 1.0 : 0.0).ToArray();

            for (int pass = 1; pass <= passes; pass++)
            {
                var direction = new double[mapCount];
                for (int i = 0; i < mapCount; i++)
                {
                    if (current.Worth[i] is not double w || w <= -0.99 || target[i] is not double t) continue;
                    BalanceTuner.Megametric75(current.Data, current.E, i, out int n, fitPlayers);
                    direction[i] = -n / (n + EvidenceScores) * Math.Log((1 + w) / (1 + t)) / Math.Max(c.AccShare[i], MinAccShare);
                }

                var strengths = pass == passes && lastStrength is double only ? new[] { only } : Strengths;
                (BalanceTuner.Data Data, BalanceTuner.Evaluation E, double?[] Worth, Point Point)? best = null;
                double bestSpread = double.MaxValue;
                foreach (var strength in strengths)
                {
                    var candidate = new double[mapCount];
                    for (int i = 0; i < mapCount; i++) candidate[i] = logM[i] + strength * direction[i];
                    // Without each star range's mean (log, over the maps with a target), then the bounds
                    var cleaned = Residuals(c.BandFeatures, candidate.Select(v => (double?)v).ToArray(), hasTarget);
                    for (int i = 0; i < mapCount; i++) candidate[i] = Math.Clamp(cleaned[i] ?? 0, lo, hi);
                    var measured = Measure(candidate, pass, strength);
                    // Moves on the fitted players' worth spread
                    double spread = Spread(measured.Worth);
                    if (spread < bestSpread)
                    {
                        bestSpread = spread;
                        best = measured;
                    }
                }
                current = best!.Value;
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

        private static double Spread(double?[] worth)
        {
            var values = worth.Where(w => w != null).Select(w => w!.Value).ToList();
            return values.Count > 0 ? Math.Sqrt(values.Average(w => w * w)) : double.MaxValue;
        }


        private static bool GuardOk(BalanceStats start, BalanceStats b)
        {
            bool Ok(double before, double after, double tolerance) => Math.Abs(after) <= Math.Abs(before) + tolerance;
            return Ok(start.PassShareSlope, b.PassShareSlope, GuardTolerance) && Ok(start.TechShareSlope, b.TechShareSlope, GuardTolerance)
                && Ok(start.AccShareSlope, b.AccShareSlope, GuardTolerance) && Ok(start.PredictedAccSlope, b.PredictedAccSlope, GuardTolerance)
                && Ok(start.OneSaberGap, b.OneSaberGap, GapTolerance)
                && Ok(start.PassShareGap, b.PassShareGap, GapTolerance) && Ok(start.TechShareGap, b.TechShareGap, GapTolerance)
                && Ok(start.PredictedAccGap, b.PredictedAccGap, GapTolerance);
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

        // The loaded data with each map's acc ratings at base x multiplier (stars and star ranges follow)
        private static BalanceTuner.Data WithMultipliers(BalanceTuner.Data source, double[] baseAcc, double[,] baseModAcc, double[] logM)
        {
            var maps = new BalanceTuner.MapInfo[source.Maps.Length];
            Parallel.For(0, maps.Length, i =>
            {
                var lb = source.Maps[i].Leaderboard;
                double m = Math.Exp(logM[i]);
                var copy = new Leaderboard
                {
                    Id = lb.Id, Name = lb.Name, Hash = lb.Hash, ModeName = lb.ModeName, DifficultyName = lb.DifficultyName,
                    PredictedAcc = lb.PredictedAcc, Curve = lb.Curve, HitDistance = lb.HitDistance,
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
                float stars = ReplayUtils.ToStars(copy.AccRating, copy.PassRating, copy.TechRating, copy.Curve);
                maps[i] = new BalanceTuner.MapInfo { Leaderboard = copy, Stars = stars, StarBand = BalanceTuner.StarBandOf(stars), Scores = source.Maps[i].Scores };
            });
            return new BalanceTuner.Data
            {
                Maps = maps,
                ScoreMap = source.ScoreMap,
                ScorePlayer = source.ScorePlayer,
                ScoreAcc = source.ScoreAcc,
                ScoreModifiers = source.ScoreModifiers,
                PlayerScores = source.PlayerScores,
                PlayerCount = source.PlayerCount,
            };
        }
    }

    public class AccReweightResult
    {
        public DateTime GeneratedAt { get; set; }
        public AccReweightSettings Settings { get; set; } = new();
        // Phase 1: fitted on half the players, worth spread measured on the other half (HeldOutSpread)
        public List<AccReweightRound> HeldOut { get; set; } = new();
        // Phase 2: every player; KeptRound is the index of the multipliers kept (0: none)
        public List<AccReweightRound> Rounds { get; set; } = new();
        public int KeptRound { get; set; }
        // The multipliers stored: Settings.Applied of the kept ones (in log)
        public AccReweightRound? Applied { get; set; }
        // Maps whose acc rating changed, lowest multiplier first
        public List<AccReweightMap> Maps { get; set; } = new();
    }

    public class AccReweightSettings
    {
        public int Passes { get; set; }
        public double[] Strengths { get; set; } = Array.Empty<double>();
        public double EvidenceScores { get; set; }
        public double MinAccShare { get; set; }
        public double MaxChange { get; set; }
        public double Applied { get; set; } = 1;
        public double GuardTolerance { get; set; }
        public double GapTolerance { get; set; }
    }

    public class AccReweightRound
    {
        public int Round { get; set; }
        public double Strength { get; set; }
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
    }
}
