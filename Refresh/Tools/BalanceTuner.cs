using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using RatingAPI.Pp;

namespace portaBLe.Refresh
{
    // Rating constants the balance tuner can change without re-analysing any map; each is a simple transform of the stored
    // ratings:
    // - PassCalibration (AnalyzeMap.PASS_CALIBRATION_FACTOR): pass rating is proportional to it.
    // - OneSaberNerf (AnalyzeMap.ONE_SABER_NERF): pass *= 1 - (1 - handsRatio) * nerf. handsRatio isn't stored, so only
    //   OneSaber maps (handsRatio 0) are transformed, although one-handed Standard maps are affected too in reality.
    // - TechScaler (AnalyzeMap.BALANCED_TECH_SCALER): tech = swing tech * (1 - 1.4^-pass) * scaler, so tech follows the new
    //   pass rating. Not tuned; records what the database was rated with.
    // - TechWeight (PpFormula.TechWeight): scales tech pp like TechScaler^TechRatingPower would, without changing the tech
    //   ratings, so this is the tech constant the tuner moves.
    // - AccScale and AccOffset (AccRating.ACC_SCALE / ACC_OFFSET): acc rating = AccScale x Curve(effective acc +
    //   AccOffset)^-ACC_EXPONENT (effective acc: AccRating.EffectiveAcc).
    // The stored acc ratings include the acc reweighter's multipliers (Leaderboard.AccReweight).
    public class RatingConstants
    {
        public double PassCalibration { get; set; } = 0.7721;
        public double OneSaberNerf { get; set; } = 0.44;
        public double TechScaler { get; set; } = 20.611;
        // Follows the PpFormula default, since the tuner re-scores with the formula in code
        public double TechWeight { get; set; } = new PpFormula().TechWeight;
        public double AccScale { get; set; } = 13.5382;
        public double AccOffset { get; set; } = 0.0019;

        public RatingConstants Clone() => (RatingConstants)MemberwiseClone();

        public override string ToString() => FormattableString.Invariant(
            $"pass {PassCalibration:0.#####}, one saber {OneSaberNerf:0.###}, tech {TechScaler:0.####} (tech pp x{TechWeight:0.####}), acc scale {AccScale:0.###}, acc offset {AccOffset:0.#####}");
    }

    // Balance tuner: suggests pass, tech and acc rating constants so that no kind of map is systematically worth more to
    // players than other maps of the same star rating. Suggestions only: nothing is written to the database, and the
    // Analyzer constants are applied by hand afterwards.
    //
    // For every value tried, the stored ratings are transformed, stars rebuilt, and every score and player's weighted pp
    // recomputed in memory. A map's "worth" is its WorthMeasure value relative to what maps of its stars average (0 =
    // average, +0.2 = 20% more valuable). Each constant is moved until worth no longer depends on what it controls:
    //   PassCalibration -> share of pass pp at the predicted acc
    //   TechWeight      -> share of tech pp at the predicted acc
    //   OneSaberNerf    -> OneSaber maps vs Standard maps of the same stars
    //   AccOffset       -> the 20% of maps with the highest predicted acc vs the 20% with the lowest
    // AccScale is the reference and isn't tuned: pass, acc and tech shares add up to 100%, so only two of the three can be
    // balanced against each other, and scaling all three together only changes the pp level.
    //
    // The result is saved to wwwroot/balance-tuning.json and shown on the Balance Tuning page.
    public static class BalanceTuner
    {
        public const string FileName = "balance-tuning.json";

        // The constants the database was rated with (the code's values when RatingsRefresh.Overwrite last ran).
        // Keep in sync when re-rating, so the tuner starts from the right place.
        public static RatingConstants RatedWith { get; set; } = new();

        private const int MinScoresPerMap = 20;     // maps with fewer scores are left out of the per-map metrics
        private const int MegametricMinPlays = 75;  // as LeaderboardsRefresh's Megametric75
        private const int StarBandCount = 14;       // 0-1 ... 12-13, 13+
        private const int MinOneSaberMaps = 10;

        public static string StarBandName(int band) => band >= StarBandCount - 1 ? $"{StarBandCount - 1}+" : $"{band}-{band + 1}";
        internal static int StarBandOf(float stars) => Math.Clamp((int)MathF.Floor(stars), 0, StarBandCount - 1);
        // Worth is measured per map and speed modifier ("item" = map * WorthSpeeds.Length + speed), each at its own stars.
        // A score's speed is its first speed modifier, as in ReplayUtils.PpFromScore; -1 for NF, which no worth counts.
        internal static readonly string[] WorthSpeeds = { "none", "SS", "FS", "SF" };
        internal static sbyte WorthSpeedOf(string modifiers)
        {
            if (string.IsNullOrEmpty(modifiers)) return 0;
            var list = modifiers.ToUpperInvariant().Split(',');
            if (list.Contains("NF")) return -1;
            foreach (var modifier in list)
            {
                int i = Array.IndexOf(WorthSpeeds, modifier);
                if (i > 0) return (sbyte)i;
            }
            return 0;
        }

        // The speed modifier whose ratings a score's pp uses (index in WorthSpeeds, 0: none), as ReplayUtils.PpFromScore.
        // Unlike WorthSpeedOf, NF is not special-cased (its pp is 0 either way).
        internal static sbyte PpSpeedOf(string modifiers)
        {
            if (string.IsNullOrEmpty(modifiers)) return 0;
            foreach (var modifier in modifiers.ToUpper().Split(","))
            {
                int i = Array.IndexOf(WorthSpeeds, modifier);
                if (i > 0) return (sbyte)i;
            }
            return 0;
        }

        internal static bool IsOneSaber(Leaderboard lb) => string.Equals(lb.ModeName, "OneSaber", StringComparison.OrdinalIgnoreCase);
        private static bool IsStandard(Leaderboard lb) => string.Equals(lb.ModeName, "Standard", StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------------
        // Data
        // ------------------------------------------------------------------

        internal sealed class MapInfo
        {
            public Leaderboard Leaderboard = null!;
            public float Stars;
            public int StarBand;
            public int[] Scores = Array.Empty<int>();
        }

        internal sealed class Data
        {
            public MapInfo[] Maps = Array.Empty<MapInfo>();
            public int[] ScoreMap = Array.Empty<int>();
            public int[] ScorePlayer = Array.Empty<int>();
            public float[] ScoreAcc = Array.Empty<float>();
            public string[] ScoreModifiers = Array.Empty<string>();
            public sbyte[] ScoreSpeed = Array.Empty<sbyte>();   // WorthSpeedOf(ScoreModifiers)
            public sbyte[] ScorePpSpeed = Array.Empty<sbyte>();   // PpSpeedOf(ScoreModifiers)
            public int[] ScoreTime = Array.Empty<int>();   // Score.Timepost (unix seconds)
            public int[][] PlayerScores = Array.Empty<int[]>();
            public string[] PlayerIds = Array.Empty<string>();   // Player.Id of each player index
            public int PlayerCount;
        }

        internal sealed class Evaluation
        {
            public float[] Pp = Array.Empty<float>();
            public float[] Weight = Array.Empty<float>();
            public float[] PassPp = Array.Empty<float>();
            public float[] AccPp = Array.Empty<float>();
            public float[] TechPp = Array.Empty<float>();
            public int[] Position = Array.Empty<int>();
            public float[] PlayerPp = Array.Empty<float>();
            public float[] PlayerTopPp = Array.Empty<float>();
            public int[] PlayerRank = Array.Empty<int>();   // 1-based
        }

        internal static async Task<Data> Load(AppContext dbContext)
        {
            var leaderboards = await dbContext.Leaderboards.AsNoTracking().Include(l => l.ModifiersRating).ToListAsync();
            var mapIndex = new Dictionary<string, int>(leaderboards.Count);
            var maps = new MapInfo[leaderboards.Count];
            for (int i = 0; i < leaderboards.Count; i++)
            {
                mapIndex[leaderboards[i].Id] = i;
                maps[i] = new MapInfo { Leaderboard = leaderboards[i] };
            }

            var scores = await dbContext.Scores.AsNoTracking()
                .Select(s => new { s.LeaderboardId, s.PlayerId, s.Accuracy, s.Modifiers, s.Timepost })
                .ToListAsync();

            var playerIndex = new Dictionary<string, int>();
            var interned = new Dictionary<string, string>();
            var scoreMap = new List<int>(scores.Count);
            var scorePlayer = new List<int>(scores.Count);
            var scoreAcc = new List<float>(scores.Count);
            var scoreModifiers = new List<string>(scores.Count);
            var scoreSpeed = new List<sbyte>(scores.Count);
            var scorePpSpeed = new List<sbyte>(scores.Count);
            var speeds = new Dictionary<string, (sbyte Worth, sbyte Pp)>();
            var scoreTime = new List<int>(scores.Count);
            foreach (var s in scores)
            {
                if (s.LeaderboardId == null || s.PlayerId == null || !mapIndex.TryGetValue(s.LeaderboardId, out int map)) continue;
                if (!playerIndex.TryGetValue(s.PlayerId, out int player))
                {
                    player = playerIndex.Count;
                    playerIndex[s.PlayerId] = player;
                }
                var modifiers = s.Modifiers ?? "";
                if (!interned.TryGetValue(modifiers, out var shared)) interned[modifiers] = shared = modifiers;
                if (!speeds.TryGetValue(shared, out var speed)) speeds[shared] = speed = (WorthSpeedOf(shared), PpSpeedOf(shared));
                scoreMap.Add(map);
                scorePlayer.Add(player);
                scoreAcc.Add(s.Accuracy);
                scoreModifiers.Add(shared);
                scoreSpeed.Add(speed.Worth);
                scorePpSpeed.Add(speed.Pp);
                scoreTime.Add(s.Timepost);
            }

            var byMap = new List<int>[maps.Length];
            var byPlayer = new List<int>[playerIndex.Count];
            for (int i = 0; i < byMap.Length; i++) byMap[i] = new List<int>();
            for (int i = 0; i < byPlayer.Length; i++) byPlayer[i] = new List<int>();
            for (int s = 0; s < scoreMap.Count; s++)
            {
                byMap[scoreMap[s]].Add(s);
                byPlayer[scorePlayer[s]].Add(s);
            }
            for (int i = 0; i < maps.Length; i++) maps[i].Scores = byMap[i].ToArray();

            return new Data
            {
                Maps = maps,
                ScoreMap = scoreMap.ToArray(),
                ScorePlayer = scorePlayer.ToArray(),
                ScoreAcc = scoreAcc.ToArray(),
                ScoreModifiers = scoreModifiers.ToArray(),
                ScoreSpeed = scoreSpeed.ToArray(),
                ScorePpSpeed = scorePpSpeed.ToArray(),
                ScoreTime = scoreTime.ToArray(),
                PlayerScores = byPlayer.Select(l => l.ToArray()).ToArray(),
                PlayerIds = playerIndex.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToArray(),
                PlayerCount = playerIndex.Count,
            };
        }

        // ------------------------------------------------------------------
        // Run
        // ------------------------------------------------------------------

        // fileName: where in wwwroot the result goes (the Balance Tuning page shows FileName)
        public static async Task Run(AppContext dbContext, string webRootPath, int rounds = 2, RatingConstants? ratedWith = null, string fileName = FileName)
        {
            ratedWith ??= RatedWith;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"Balance tuning: loading scores (rated with {ratedWith}, worth measure {WorthMeasure})...");
            var data = await Load(dbContext);
            Console.WriteLine($"Balance tuning: {data.Maps.Length} leaderboards, {data.ScoreMap.Length} scores, {data.PlayerCount} players loaded in {timer.Elapsed:mm\\:ss}");

            var reference = WithTechWeight(ratedWith, () => Evaluate(WithConstants(data, ratedWith, ratedWith)));
            var result = new BalanceTuningResult
            {
                GeneratedAt = DateTime.UtcNow,
                RatedWith = ratedWith.Clone(),
                OneSaberMaps = data.Maps.Count(m => IsOneSaber(m.Leaderboard)),
                WorthMeasure = WorthMeasure.ToString(),
            };
            result.Current = Score(data, ratedWith, ratedWith, reference);
            Console.WriteLine($"Balance tuning: in code: {Describe(result.Current.Balance)}");

            var best = ratedWith.Clone();
            for (int round = 1; round <= rounds; round++)
            {
                foreach (var knob in Knobs)
                {
                    if (knob.Name == nameof(RatingConstants.OneSaberNerf) && result.OneSaberMaps < MinOneSaberMaps) continue;

                    var sweep = new KnobSweep { Knob = knob.Name, Target = knob.TargetLabel, Round = round };
                    foreach (var value in knob.Grid(knob.Get(best), round))
                    {
                        var constants = best.Clone();
                        knob.Set(constants, value);
                        var point = Score(data, ratedWith, constants, reference);
                        sweep.Points.Add(new SweepPoint
                        {
                            Value = value,
                            Target = knob.Target(point.Balance),
                            RankCorrelationTop1000 = point.Summary.RankCorrelationTop1000,
                            StarOrderBreaks = point.Summary.ProgressionInversions,
                            Top100Pp = point.Summary.Top100Pp,
                        });
                    }

                    sweep.Suggested = ZeroCrossing(sweep.Points, out bool atEdge);
                    sweep.AtEdge = atEdge;
                    sweep.Kept = KeepReason(sweep.Points, knob.Get(best));
                    if (sweep.Kept != null)
                    {
                        sweep.Suggested = knob.Get(best);
                        sweep.AtEdge = false;
                    }
                    knob.Set(best, sweep.Suggested);
                    result.Sweeps.Add(sweep);
                    Console.WriteLine(FormattableString.Invariant(
                        $"Balance tuning: round {round}, {knob.Name} -> {sweep.Suggested:0.#####}{(sweep.AtEdge ? " (at the edge of the range tried)" : "")}{(sweep.Kept != null ? $" (kept: {sweep.Kept})" : "")} ({timer.Elapsed:mm\\:ss})"));
                }
            }

            result.SuggestedConstants = best;
            result.Suggested = Score(data, ratedWith, best, reference);
            Console.WriteLine($"Balance tuning: suggested {best}: {Describe(result.Suggested.Balance)}");

            var path = Path.Combine(webRootPath, fileName);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, JsonOptions));
            Console.WriteLine($"Balance tuning saved to {path} ({timer.Elapsed:hh\\:mm\\:ss})");
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public static BalanceTuningResult? LoadResult(string webRootPath)
        {
            var path = Path.Combine(webRootPath, FileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<BalanceTuningResult>(File.ReadAllText(path), JsonOptions);
        }

        // ------------------------------------------------------------------
        // pp formula sweeps
        // ------------------------------------------------------------------

        public const string PpFormulaFileName = "pp-formula.json";

        private sealed class FormulaKnob
        {
            public string Name = "";
            public string Explanation = "";
            public Func<PpFormula, double> Get = _ => 0;
            public Action<PpFormula, double> Set = (_, _) => { };
            public double[] Values = Array.Empty<double>();
        }

        // Only the parts of the formula that shape how pp grows. AccWeight duplicates ACC_SCALE and TechWeight is balanced by
        // Run, so neither is swept here.
        private static readonly FormulaKnob[] FormulaKnobs =
        {
            new FormulaKnob
            {
                Name = nameof(PpFormula.PassExponent),
                Explanation = "Pass pp grows with e^(pass^(1/x)). Lower x = pass pp climbs faster on high pass ratings.",
                Get = f => f.PassExponent, Set = (f, v) => f.PassExponent = v,
                Values = new[] { 2.2, 2.4, 2.5, 2.62, 2.75, 2.9, 3.1 },
            },
            new FormulaKnob
            {
                Name = nameof(PpFormula.PassTopSlope),
                Explanation = "Above PassTopKnee pass, each point counts this much for pass pp (1 = off). Lower = the highest pass ratings pay less.",
                Get = f => f.PassTopSlope, Set = (f, v) => f.PassTopSlope = v,
                Values = new[] { 0.3, 0.4, 0.5, 0.6, 0.7, 0.85, 1.0 },
            },
            new FormulaKnob
            {
                Name = nameof(PpFormula.PassTopKnee),
                Explanation = "Pass where the pass pp top compression starts. Lower = more maps compressed.",
                Get = f => f.PassTopKnee, Set = (f, v) => f.PassTopKnee = v,
                Values = new[] { 10.4, 12, 13, 14, 15 },
            },
            new FormulaKnob
            {
                Name = nameof(PpFormula.TechTopSlope),
                Explanation = "Above TechTopKnee tech, each point counts this much for tech pp (1 = off). Lower = the highest tech ratings pay less.",
                Get = f => f.TechTopSlope, Set = (f, v) => f.TechTopSlope = v,
                Values = new[] { 0.2, 0.33, 0.5, 0.67, 0.85, 1.0 },
            },
            new FormulaKnob
            {
                Name = nameof(PpFormula.TechTopKnee),
                Explanation = "Tech where the tech pp top compression starts. Lower = more maps compressed.",
                Get = f => f.TechTopKnee, Set = (f, v) => f.TechTopKnee = v,
                Values = new[] { 10.0, 11, 12, 13, 14 },
            },
            new FormulaKnob
            {
                Name = nameof(PpFormula.PassOffset),
                Explanation = "Subtracted from pass pp. Higher = easy maps get less pass pp (the cut matters most where pass pp is small).",
                Get = f => f.PassOffset, Set = (f, v) => f.PassOffset = v,
                Values = new[] { 10.0, 20, 25, 30, 35, 40, 50 },
            },
            new FormulaKnob
            {
                Name = nameof(PpFormula.TechAccExponent),
                Explanation = "Tech pp is multiplied by e^(x*acc). Higher = tech pp depends more on accuracy.",
                Get = f => f.TechAccExponent, Set = (f, v) => f.TechAccExponent = v,
                Values = new[] { 2.9, 3.2, 3.35, 3.5, 3.65, 3.8, 4.1 },
            },
        };

        // Tries each part of the pp formula on its own (the others at their current value), with the ratings as rated.
        // No automatic suggestion: these shape how pp grows with difficulty, which is a design choice. Saved to
        // wwwroot/pp-formula.json and shown on the Balance Tuning page.
        public static async Task RunPpFormula(AppContext dbContext, string webRootPath, RatingConstants? ratedWith = null)
        {
            ratedWith ??= RatedWith;
            var original = PpFormula.Current;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"pp formula: loading scores (formula {original})...");
            var data = await Load(dbContext);
            var reference = WithTechWeight(ratedWith, () => Evaluate(WithConstants(data, ratedWith, ratedWith)));

            var result = new PpFormulaResult
            {
                GeneratedAt = DateTime.UtcNow,
                Formula = original.ToString(),
                RatedWith = ratedWith.Clone(),
                Current = Score(data, ratedWith, ratedWith, reference),
            };

            try
            {
                foreach (var knob in FormulaKnobs)
                {
                    var sweep = new FormulaSweep { Knob = knob.Name, Explanation = knob.Explanation, CurrentValue = knob.Get(original) };
                    foreach (var value in knob.Values)
                    {
                        if (Math.Abs(value - sweep.CurrentValue) < 1e-9)
                        {
                            sweep.Points.Add(new FormulaPoint { Value = value, Point = result.Current });
                            continue;
                        }
                        var formula = original.Clone();
                        knob.Set(formula, value);
                        PpFormula.Current = formula;
                        sweep.Points.Add(new FormulaPoint { Value = value, Point = Score(data, ratedWith, ratedWith, reference) });
                        PpFormula.Current = original;
                    }
                    result.Sweeps.Add(sweep);
                    Console.WriteLine($"pp formula: {knob.Name} done ({timer.Elapsed:mm\\:ss})");
                }
            }
            finally
            {
                PpFormula.Current = original;
            }

            var path = Path.Combine(webRootPath, PpFormulaFileName);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, JsonOptions));
            Console.WriteLine($"pp formula sweeps saved to {path} ({timer.Elapsed:hh\\:mm\\:ss})");
        }

        public static PpFormulaResult? LoadPpFormula(string webRootPath)
        {
            var path = Path.Combine(webRootPath, PpFormulaFileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<PpFormulaResult>(File.ReadAllText(path), JsonOptions);
        }

        // ------------------------------------------------------------------
        // Knobs
        // ------------------------------------------------------------------

        private sealed class Knob
        {
            public string Name = "";
            public string TargetLabel = "";
            public Func<RatingConstants, double> Get = _ => 0;
            public Action<RatingConstants, double> Set = (_, _) => { };
            public Func<BalanceStats, double> Target = _ => 0;
            public Func<double, int, double[]> Grid = (_, _) => Array.Empty<double>();
        }

        private static double[] Relative(double center, int round, double step) =>
            Enumerable.Range(-3, 7).Select(i => center * (1 + i * step / round)).ToArray();

        private static readonly Knob[] Knobs =
        {
            new Knob
            {
                Name = nameof(RatingConstants.PassCalibration),
                TargetLabel = "Worth change per +10% pass pp share",
                Get = c => c.PassCalibration, Set = (c, v) => c.PassCalibration = v,
                Target = b => b.PassShareSlope,
                Grid = (center, round) => Relative(center, round, 0.05),
            },
            new Knob
            {
                Name = nameof(RatingConstants.TechWeight),
                TargetLabel = "Worth change per +10% tech pp share",
                Get = c => c.TechWeight, Set = (c, v) => c.TechWeight = v,
                Target = b => b.TechShareSlope,
                Grid = (center, round) => Relative(center, round, 0.1),
            },
            new Knob
            {
                Name = nameof(RatingConstants.OneSaberNerf),
                TargetLabel = "OneSaber maps' worth minus Standard maps' of the same stars",
                Get = c => c.OneSaberNerf, Set = (c, v) => c.OneSaberNerf = v,
                Target = b => b.OneSaberGap,
                Grid = (center, round) => Enumerable.Range(-3, 7).Select(i => Math.Clamp(center + i * 0.1 / round, 0, 0.95)).Distinct().ToArray(),
            },
            new Knob
            {
                Name = nameof(RatingConstants.AccOffset),
                TargetLabel = "Worth of the 20% easiest-acc maps minus the 20% hardest",
                Get = c => c.AccOffset, Set = (c, v) => c.AccOffset = v,
                Target = b => b.PredictedAccGap,
                Grid = (center, round) => Enumerable.Range(-3, 7).Select(i => center + i * 0.0011 / round).ToArray(),
            },
        };

        // A constant stays where it is when its target is already within TargetTolerance of 0, or the target moves less than
        // MinResponse over the whole range tried: then the zero crossing is far away and noise decides where, which can drag
        // a constant to the edge of its range for a negligible gain. KeepReason returns null when the constant should move.
        public static double TargetTolerance { get; set; } = 0.01;
        public static double MinResponse { get; set; } = 0.01;

        private static string? KeepReason(List<SweepPoint> points, double current)
        {
            var finite = points.Where(p => double.IsFinite(p.Target)).ToList();
            if (finite.Count == 0) return null;
            var atCurrent = finite.OrderBy(p => Math.Abs(p.Value - current)).First();
            if (Math.Abs(atCurrent.Target) <= TargetTolerance)
                return FormattableString.Invariant($"within tolerance, {atCurrent.Target:+0.000;-0.000}");
            double response = finite.Max(p => p.Target) - finite.Min(p => p.Target);
            if (response < MinResponse)
                return FormattableString.Invariant($"barely reacts, {response:0.000} over the range tried");
            return null;
        }

        // Value where the target crosses 0, by linear interpolation between the two points around it. Without a crossing,
        // the point closest to 0 (atEdge = true when that's the first or last value tried).
        private static double ZeroCrossing(List<SweepPoint> points, out bool atEdge)
        {
            atEdge = false;
            var ordered = points.Where(p => double.IsFinite(p.Target)).OrderBy(p => p.Value).ToList();
            if (ordered.Count == 0) return points.Count > 0 ? points[points.Count / 2].Value : 0;
            int closest = Enumerable.Range(0, ordered.Count).OrderBy(i => Math.Abs(ordered[i].Target)).First();
            foreach (int i in new[] { closest - 1, closest })
            {
                if (i < 0 || i + 1 >= ordered.Count) continue;
                var a = ordered[i];
                var b = ordered[i + 1];
                if (Math.Sign(a.Target) != Math.Sign(b.Target) && a.Target != b.Target)
                {
                    return a.Value + (0 - a.Target) * (b.Value - a.Value) / (b.Target - a.Target);
                }
            }
            atEdge = closest == 0 || closest == ordered.Count - 1;
            return ordered[closest].Value;
        }

        // ------------------------------------------------------------------
        // Ratings transform
        // ------------------------------------------------------------------

        // Copy of the data with every leaderboard's ratings (and modifier ratings) moved from one set of constants to another,
        // and stars rebuilt.
        private static Data WithConstants(Data source, RatingConstants from, RatingConstants to)
        {
            var maps = new MapInfo[source.Maps.Length];
            Parallel.For(0, maps.Length, i =>
            {
                var original = source.Maps[i];
                var lb = original.Leaderboard;
                bool oneSaber = IsOneSaber(lb);
                var (pass, tech, acc) = Transform(lb.PassRating, lb.TechRating, lb.AccRating, lb.PredictedAcc, lb.HitDistance, lb.HardSwingRatio, oneSaber, from, to);
                var copy = new Leaderboard
                {
                    Id = lb.Id,
                    Name = lb.Name,
                    Hash = lb.Hash,
                    ModeName = lb.ModeName,
                    DifficultyName = lb.DifficultyName,
                    PredictedAcc = lb.PredictedAcc,
                    Curve = lb.Curve,
                    HitDistance = lb.HitDistance,
                    HardSwingRatio = lb.HardSwingRatio,
                    PassRating = pass,
                    TechRating = tech,
                    AccRating = acc,
                };
                var mod = lb.ModifiersRating;
                if (mod != null)
                {
                    var (ssPass, ssTech, ssAcc) = Transform(mod.SSPassRating, mod.SSTechRating, mod.SSAccRating, mod.SSPredictedAcc, lb.HitDistance, lb.HardSwingRatio, oneSaber, from, to);
                    var (fsPass, fsTech, fsAcc) = Transform(mod.FSPassRating, mod.FSTechRating, mod.FSAccRating, mod.FSPredictedAcc, lb.HitDistance, lb.HardSwingRatio, oneSaber, from, to);
                    var (sfPass, sfTech, sfAcc) = Transform(mod.SFPassRating, mod.SFTechRating, mod.SFAccRating, mod.SFPredictedAcc, lb.HitDistance, lb.HardSwingRatio, oneSaber, from, to);
                    copy.ModifiersRating = new ModifiersRating
                    {
                        Id = mod.Id,
                        SSPredictedAcc = mod.SSPredictedAcc, SSPassRating = ssPass, SSTechRating = ssTech, SSAccRating = ssAcc,
                        FSPredictedAcc = mod.FSPredictedAcc, FSPassRating = fsPass, FSTechRating = fsTech, FSAccRating = fsAcc,
                        SFPredictedAcc = mod.SFPredictedAcc, SFPassRating = sfPass, SFTechRating = sfTech, SFAccRating = sfAcc,
                        SSCurve = mod.SSCurve, FSCurve = mod.FSCurve, SFCurve = mod.SFCurve,
                    };
                }
                float stars = ReplayUtils.ToStars(copy.AccRating, copy.PassRating, copy.TechRating, copy.Curve);
                maps[i] = new MapInfo { Leaderboard = copy, Stars = stars, StarBand = StarBandOf(stars), Scores = original.Scores };
            });

            return new Data
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

        private static (float Pass, float Tech, float Acc) Transform(float pass, float tech, float accRating, float predictedAcc, float hitDistance, float hardSwingRatio, bool oneSaber,
            RatingConstants from, RatingConstants to)
        {
            double newPass = pass * (to.PassCalibration / from.PassCalibration);
            if (oneSaber)
            {
                newPass *= (1 - to.OneSaberNerf) / Math.Max(1e-6, 1 - from.OneSaberNerf);
            }

            // tech = scaler x (swing tech x pass factor)^TechExponent (Analyzer AnalyzeMap)
            double newTech = tech;
            double oldFactor = global::beatleader_analyzer.BeatmapScanner.Data.Ratings.TechPassFactor(pass);
            double newFactor = global::beatleader_analyzer.BeatmapScanner.Data.Ratings.TechPassFactor(newPass);
            if (oldFactor > 1e-6)
            {
                newTech = tech * (to.TechScaler / from.TechScaler)
                    * Math.Pow(newFactor / oldFactor, global::beatleader_analyzer.BeatmapScanner.Data.Ratings.TechExponent);
            }

            double newAcc = accRating;
            if (predictedAcc > 0)
            {
                // Same as AccRating.GetRating, with its corrections (the high pass one follows the new pass)
                double exponent = global::RatingAPI.Controllers.AccRating.ACC_EXPONENT;
                double oldEffective = global::RatingAPI.Controllers.AccRating.EffectiveAcc(predictedAcc, pass, hitDistance);
                double newEffective = global::RatingAPI.Controllers.AccRating.EffectiveAcc(predictedAcc, newPass, hitDistance);
                double oldBase = from.AccScale * Math.Pow(global::RatingAPI.Controllers.AccRating.Curve(predictedAcc, oldEffective + from.AccOffset), -exponent)
                    * global::RatingAPI.Controllers.AccRating.HardSwingMultiplier(pass, hardSwingRatio);
                double newBase = to.AccScale * Math.Pow(global::RatingAPI.Controllers.AccRating.Curve(predictedAcc, newEffective + to.AccOffset), -exponent)
                    * global::RatingAPI.Controllers.AccRating.HardSwingMultiplier(newPass, hardSwingRatio);
                if (oldBase > 0 && double.IsFinite(newBase)) newAcc = accRating * newBase / oldBase;
            }
            return ((float)newPass, (float)newTech, (float)newAcc);
        }

        // ------------------------------------------------------------------
        // Re-score everything
        // ------------------------------------------------------------------

        internal static Evaluation Evaluate(Data data)
        {
            int scoreCount = data.ScoreMap.Length;
            var e = new Evaluation
            {
                Pp = new float[scoreCount],
                Weight = new float[scoreCount],
                PassPp = new float[scoreCount],
                AccPp = new float[scoreCount],
                TechPp = new float[scoreCount],
                Position = new int[scoreCount],
                PlayerPp = new float[data.PlayerCount],
                PlayerTopPp = new float[data.PlayerCount],
                PlayerRank = new int[data.PlayerCount],
            };

            // Each map's ratings prepared once, nomod then SS / FS / SF (WorthSpeeds order), as ReplayUtils.PpFromScore
            int speedCount = WorthSpeeds.Length;
            var prepared = new global::RatingAPI.Pp.PreparedRatings?[data.Maps.Length * speedCount];
            Parallel.For(0, data.Maps.Length, m =>
            {
                var lb = data.Maps[m].Leaderboard;
                prepared[m * speedCount] = global::RatingAPI.Pp.PpCalculator.Prepare(new global::RatingAPI.Pp.MapRatings(lb.AccRating, lb.PassRating, lb.TechRating, lb.Curve));
                if (lb.ModifiersRating == null) return;
                for (int k = 1; k < speedCount; k++)
                {
                    prepared[m * speedCount + k] = global::RatingAPI.Pp.PpCalculator.Prepare(ReplayUtils.SpeedRatings(lb.ModifiersRating, WorthSpeeds[k], lb.Curve)!.Value);
                }
            });

            Parallel.For(0, scoreCount, new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount }, s =>
            {
                int m = data.ScoreMap[s];
                bool rated = data.Maps[m].Leaderboard.ModifiersRating != null;
                int speed = rated ? data.ScorePpSpeed[s] : 0;
                var (pp, _, passPp, accPp, techPp) = global::RatingAPI.Pp.PpCalculator.ScorePp(data.ScoreAcc[s], data.ScoreModifiers[s],
                    prepared[m * speedCount]!.Value, speed > 0 ? prepared[m * speedCount + speed] : null, rated);
                e.Pp[s] = float.IsFinite(pp) ? pp : 0;
                e.PassPp[s] = float.IsFinite(passPp) ? passPp : 0;
                e.AccPp[s] = float.IsFinite(accPp) ? accPp : 0;
                e.TechPp[s] = float.IsFinite(techPp) ? techPp : 0;
            });

            // Weighted pp per player, as PlayersRefresh: 0.965^position
            Parallel.For(0, data.PlayerCount, p =>
            {
                var ordered = data.PlayerScores[p].OrderByDescending(s => e.Pp[s]).ToArray();
                float total = 0, weight = 1;
                for (int i = 0; i < ordered.Length; i++)
                {
                    e.Weight[ordered[i]] = weight;
                    e.Position[ordered[i]] = i;
                    total += e.Pp[ordered[i]] * weight;
                    weight *= 0.965f;
                }
                e.PlayerPp[p] = total;
                e.PlayerTopPp[p] = ordered.Length > 0 ? e.Pp[ordered[0]] : 0;
            });

            var byPp = Enumerable.Range(0, data.PlayerCount).OrderByDescending(p => e.PlayerPp[p]).ToArray();
            for (int i = 0; i < byPp.Length; i++) e.PlayerRank[byPp[i]] = i + 1;
            return e;
        }

        private static BalancePoint Score(Data source, RatingConstants from, RatingConstants to, Evaluation reference) => WithTechWeight(to, () =>
        {
            var data = WithConstants(source, from, to);
            var e = Evaluate(data);
            var worth = Worth(data, e, out var bandMeans);
            return new BalancePoint
            {
                Constants = to.Clone(),
                Balance = MeasureBalance(data, e, worth),
                Summary = Summarize(data, e, reference),
                Bands = Bands(data, e, reference, bandMeans),
            };
        });

        // Runs with PpFormula.Current's TechWeight set to the candidate's (the rest of the formula as it is)
        private static T WithTechWeight<T>(RatingConstants constants, Func<T> run)
        {
            var original = PpFormula.Current;
            if (Math.Abs(original.TechWeight - constants.TechWeight) < 1e-12) return run();
            var formula = original.Clone();
            formula.TechWeight = constants.TechWeight;
            PpFormula.Current = formula;
            try
            {
                return run();
            }
            finally
            {
                PpFormula.Current = original;
            }
        }

        // ------------------------------------------------------------------
        // Metrics
        // ------------------------------------------------------------------

        // Megametric75, as LeaderboardsRefresh: top 33% by weight of the players with 75+ plays, pp / top pp * weight
        private static double? Megametric75(Data data, Evaluation e, int m) => Megametric75(data, e, m, out _);

        // count: how many scores the value averages (0 when null). players: only those players' scores (null = all; the acc
        // reweighter fits on one half of the players and checks on the other)
        internal static double? Megametric75(Data data, Evaluation e, int m, out int count, bool[]? players = null)
        {
            count = 0;
            // Nomod scores only (WorthSpeeds)
            var scores = data.Maps[m].Scores.Where(s => data.ScoreSpeed[s] == 0 && (players == null || players[data.ScorePlayer[s]])).ToArray();
            if (scores.Length < (players == null ? MinScoresPerMap : MinScoresPerMap / 2)) return null;
            var withTop = scores.Where(s => e.PlayerTopPp[data.ScorePlayer[s]] > 0).ToList();
            int topCount = (int)(withTop.Count * 0.33);
            var filtered = withTop
                .Where(s => data.PlayerScores[data.ScorePlayer[s]].Length > MegametricMinPlays)
                .OrderByDescending(s => e.Weight[s])
                .Take(topCount)
                .ToList();
            if (filtered.Count <= 10) return null;
            count = filtered.Count;
            return filtered.Average(s => e.Pp[s] / e.PlayerTopPp[data.ScorePlayer[s]] * e.Weight[s]);
        }

        // What a map's worth measures (Worth):
        // - Megametric75: as LeaderboardsRefresh, top 33% by weight of the players with 75+ plays, pp / top pp * weight.
        //   Mixes everyone who passes the map, weighted by where it lands in their profile, so low-acc survivors on hard
        //   maps skew it, and it overreacts to rating changes.
        // - InRange: average pp / top pp of the scores by players the map isn't above (stars at most InRangeMargin above the
        //   player's level), unweighted. Since stars follow pay, a map that pays more is compared with higher star maps, so
        //   it barely reacts to rating changes: a diagnostic, not a target.
        // - PlayerAdjusted: log pp = skill(player, half year) + generosity(map) + distance(map stars - player level, per
        //   PlayerAdjustedDistanceBin stars), fitted on in-range scores, then compared with maps of the same stars and maps
        //   first played around the same time. Measures how much a map pays the same players compared with their other
        //   maps at their level, independent of who plays it. Skill is per half year because players improve (otherwise
        //   recent maps look generous), and map age is removed because older maps are grinded longer; neither is something
        //   the ratings can fix.
        // Level: the stars of the InRangeHardest-th hardest map among the player's InRangeTopPlays best scores, so one or
        // two lucky passes don't raise it, while top players still reach the hardest maps (an average would not).
        // The balance tuner, the acc reweighter and Leaderboard.Worth (LeaderboardsRefresh, StoredWorth) use WorthMeasure, on
        // nomod scores: SS / FS / SF scores are their own items (WorthSpeeds, ItemWorth), NF scores count in none.
        public enum WorthMeasures { Megametric75, InRange, PlayerAdjusted }
        public static WorthMeasures WorthMeasure { get; set; } = WorthMeasures.PlayerAdjusted;

        // Each leaderboard's worth (WorthMeasure) from the database as it is, ratings and scores, per WorthSpeeds (nomod, SS,
        // FS, SF): LeaderboardsRefresh stores it in Leaderboard.Worth and ModifiersRating.SSWorth ... Stars from the ratings,
        // as the tuner's (the stored ones may not be refreshed yet).
        public static async Task<Dictionary<string, float?[]>> StoredWorth(AppContext dbContext)
        {
            var data = await Load(dbContext);
            Parallel.ForEach(data.Maps, m =>
            {
                var lb = m.Leaderboard;
                m.Stars = ReplayUtils.ToStars(lb.AccRating, lb.PassRating, lb.TechRating, lb.Curve);
                m.StarBand = StarBandOf(m.Stars);
            });
            var e = Evaluate(data);
            var worth = ItemWorth(data, e, out _);
            int n = WorthSpeeds.Length;
            return Enumerable.Range(0, data.Maps.Length).ToDictionary(i => data.Maps[i].Leaderboard.Id,
                i => Enumerable.Range(0, n).Select(k => worth[i * n + k] is double w ? (float?)w : null).ToArray());
        }
        public static double InRangeMargin { get; set; } = 0.5;
        public static int InRangeTopPlays { get; set; } = 50;
        public static int InRangeHardest { get; set; } = 5;
        // How far under the player's level a map still counts (stars). Unbounded: a bound hides the high paying scores on
        // maps easy for the player, and leaves low star maps measured only by low level players. Above the level stays
        // bounded (InRangeMargin): a map too hard for a player says little about what it pays.
        public static double PlayerAdjustedBelow { get; set; } = double.PositiveInfinity;
        // The distance term: how a player's pp on a map depends on how far under their level it is (per bin of
        // PlayerAdjustedDistanceBin stars, 0 to +bin the reference), so a map's generosity doesn't depend on how far under
        // its players it is. Off: skill + generosity only.
        public static bool PlayerAdjustedDistanceTerm { get; set; } = true;
        public static double PlayerAdjustedDistanceBin { get; set; } = 0.5;
        public static int PlayerAdjustedPeriodDays { get; set; } = 182;
        private const int PlayerAdjustedMinPlayerScores = 30;   // in-range scores of a player
        private const int PlayerAdjustedMinPeriodScores = 10;   // in-range scores of a player in a half year
        private const int PlayerAdjustedIterations = 30;
        public static double PlayerAdjustedAgeBandwidthDays { get; set; } = 180;
        // Fit each group's skill, item's generosity and distance bin to this quantile of their residuals (0.5: median) instead
        // of the mean (0). Low effort scores form a long tail under the fit, mostly on low star acc maps, which a quantile
        // resists; it is more stable split-half but makes the acc reweighter move maps further.
        public static double PlayerAdjustedQuantile { get; set; } = 0;

        // Each player's level for InRange and PlayerAdjusted (see above), each score at its worth item's stars (ItemStars)
        internal static double[] PlayerLevels(Data data, Evaluation e, double[]? itemStars = null)
        {
            itemStars ??= ItemStars(data);
            var levels = new double[data.PlayerCount];
            int hardest = InRangeHardest;
            Parallel.For(0, data.PlayerCount, p =>
            {
                // The highest stars, descending, of the player's top plays: the hardest-th is the level (the lowest of them
                // with fewer)
                Span<double> top = stackalloc double[hardest];
                int n = 0;
                foreach (var s in data.PlayerScores[p])
                {
                    if (e.Position[s] >= InRangeTopPlays || data.ScoreSpeed[s] < 0) continue;
                    double v = itemStars[ItemOf(data, s)];
                    if (double.IsNaN(v) || (n == hardest && v <= top[n - 1])) continue;
                    int j = n < hardest ? n++ : n - 1;
                    while (j > 0 && top[j - 1] < v) { top[j] = top[j - 1]; j--; }
                    top[j] = v;
                }
                levels[p] = n == 0 ? 0 : top[n - 1];
            });
            return levels;
        }

        // Worth item of a score (WorthSpeeds; its ScoreSpeed must be 0 or more)
        internal static int ItemOf(Data data, int s) => data.ScoreMap[s] * WorthSpeeds.Length + data.ScoreSpeed[s];

        // Stars of every worth item: the map's, then its SS / FS / SF ratings' (NaN without them)
        internal static double[] ItemStars(Data data)
        {
            int n = WorthSpeeds.Length;
            var stars = new double[data.Maps.Length * n];
            Parallel.For(0, data.Maps.Length, m =>
            {
                var lb = data.Maps[m].Leaderboard;
                stars[m * n] = data.Maps[m].Stars;
                for (int k = 1; k < n; k++)
                {
                    var ratings = lb.ModifiersRating == null ? null : ReplayUtils.SpeedRatings(lb.ModifiersRating, WorthSpeeds[k], lb.Curve);
                    stars[m * n + k] = ratings is MapRatings r ? PpCalculator.ToStars(r) : double.NaN;
                }
            });
            return stars;
        }

        // InRange value of a map: average pp / top pp of its nomod scores by players with more than MegametricMinPlays plays
        // whose level is at least the map's stars - InRangeMargin. count: how many scores it averages (0 when null).
        internal static double? InRangeValue(Data data, Evaluation e, double[] levels, int m, out int count, bool[]? players = null)
        {
            count = 0;
            double stars = data.Maps[m].Stars, sum = 0;
            foreach (var s in data.Maps[m].Scores)
            {
                int p = data.ScorePlayer[s];
                if (data.ScoreSpeed[s] != 0 || (players != null && !players[p]) || e.PlayerTopPp[p] <= 0 || data.PlayerScores[p].Length <= MegametricMinPlays) continue;
                if (stars > levels[p] + InRangeMargin) continue;
                sum += e.Pp[s] / e.PlayerTopPp[p];
                count++;
            }
            if (count < (players == null ? MinScoresPerMap : MinScoresPerMap / 2)) { count = 0; return null; }
            return sum / count;
        }

        // PlayerAdjusted generosity of every map (nomod): e^(its map term), the median map 1 (see WorthMeasures). Null with fewer
        // than MinScoresPerMap in-range scores (half with players).
        internal static double?[] PlayerAdjustedValues(Data data, Evaluation e, bool[]? players = null) => PlayerAdjustedValues(data, e, out _, players);

        // counts: the in-range scores each map's value is fitted on (0 when null)
        internal static double?[] PlayerAdjustedValues(Data data, Evaluation e, out int[] counts, bool[]? players = null)
        {
            int n = WorthSpeeds.Length;
            var items = PlayerAdjustedItemValues(data, e, out var itemCounts, players);
            counts = Enumerable.Range(0, data.Maps.Length).Select(m => itemCounts[m * n]).ToArray();
            return Enumerable.Range(0, data.Maps.Length).Select(m => items[m * n]).ToArray();
        }

        // Per worth item (map and speed modifier, ItemOf): each its own map term, at its own stars, so SS / FS / SF scores (paid
        // by the modifier's ratings) don't count as the map's; the median nomod map 1. NF scores are left out.
        internal static double?[] PlayerAdjustedItemValues(Data data, Evaluation e, out int[] counts, bool[]? players = null, double[]? itemStars = null)
        {
            int itemCount = data.Maps.Length * WorthSpeeds.Length;
            itemStars ??= ItemStars(data);
            var levels = PlayerLevels(data, e, itemStars);
            int t0 = data.ScoreTime.Length > 0 ? data.ScoreTime.Min() : 0;
            long period = PlayerAdjustedPeriodDays * 86400L;

            // In-range scores of players with more than MegametricMinPlays plays, each in its (player, half year) group (numbered
            // in order of first appearance)
            var obsScore = new List<int>();
            var obsGroup = new List<int>();
            int groupTotal = 0;
            int periodCount = data.ScoreTime.Length > 0 ? (int)((data.ScoreTime.Max() - (long)t0) / period) + 1 : 1;
            var playerGroup = new int[periodCount];   // this player's group of each half year, -1: none yet
            Array.Fill(playerGroup, -1);
            // Each player's in-range scores (in parallel), then the groups in player order
            var playerInRange = new int[data.PlayerCount][];
            Parallel.For(0, data.PlayerCount, p =>
            {
                if ((players != null && !players[p]) || data.PlayerScores[p].Length <= MegametricMinPlays) return;
                var list = new List<int>();
                foreach (var s in data.PlayerScores[p])
                {
                    if (data.ScoreSpeed[s] < 0 || e.Pp[s] <= 0) continue;
                    double stars = itemStars[ItemOf(data, s)];
                    if (stars >= levels[p] - PlayerAdjustedBelow && stars <= levels[p] + InRangeMargin) list.Add(s);
                }
                if (list.Count >= PlayerAdjustedMinPlayerScores) playerInRange[p] = list.ToArray();
            });
            for (int p = 0; p < data.PlayerCount; p++)
            {
                var inRange = playerInRange[p];
                if (inRange == null) continue;
                foreach (var s in inRange)
                {
                    int half = (int)((data.ScoreTime[s] - (long)t0) / period);
                    if (playerGroup[half] < 0) playerGroup[half] = groupTotal++;
                    obsScore.Add(s);
                    obsGroup.Add(playerGroup[half]);
                }
                foreach (var s in inRange) playerGroup[(int)((data.ScoreTime[s] - (long)t0) / period)] = -1;
            }

            // Groups with too few scores, then items with too few, are left out
            var groupCount = new int[groupTotal];
            foreach (var g in obsGroup) groupCount[g]++;
            var itemScores = new int[itemCount];
            for (int i = 0; i < obsScore.Count; i++) if (groupCount[obsGroup[i]] >= PlayerAdjustedMinPeriodScores) itemScores[ItemOf(data, obsScore[i])]++;
            int minItemScores = players == null ? MinScoresPerMap : MinScoresPerMap / 2;
            int kept = 0;
            var keep = new int[obsScore.Count];
            for (int i = 0; i < obsScore.Count; i++)
            {
                if (groupCount[obsGroup[i]] >= PlayerAdjustedMinPeriodScores && itemScores[ItemOf(data, obsScore[i])] >= minItemScores) keep[kept++] = i;
            }
            var y = new double[kept];
            var item = new int[kept];
            var group = new int[kept];
            var rawBin = new int[kept];
            Parallel.For(0, kept, k =>
            {
                int i = keep[k];
                y[k] = Math.Log(e.Pp[obsScore[i]]);
                item[k] = ItemOf(data, obsScore[i]);
                group[k] = obsGroup[i];
                rawBin[k] = PlayerAdjustedDistanceTerm
                    ? (int)Math.Floor((itemStars[item[k]] - levels[data.ScorePlayer[obsScore[i]]]) / PlayerAdjustedDistanceBin) : 0;
            });
            // Distance bins from the lowest, the bin of 0 to +PlayerAdjustedDistanceBin the reference
            int minBin = kept > 0 ? rawBin.Min() : 0, binCount = kept > 0 ? rawBin.Max() - minBin + 1 : 1, referenceBin = -minBin;
            var bin = rawBin.Select(b => b - minBin).ToArray();
            var distance = new double[binCount];

            // Alternating fit: skill of each group given the items, generosity of each item given the groups. Bucket keeps each
            // group's and item's scores in their original order, so the parallel sums are deterministic.
            var skill = new double[groupTotal];
            var gen = new double[itemCount];
            var (groupStart, byGroup) = Bucket(group, groupTotal);
            var (itemStart, byItem) = Bucket(item, itemCount);
            var (binStart, byBin) = Bucket(bin, binCount);
            var count = new int[itemCount];
            for (int m = 0; m < itemCount; m++) count[m] = itemStart[m + 1] - itemStart[m];
            // Each group's, item's or bin's residuals at its own range of Bucket's order (PlayerAdjustedQuantile)
            var residuals = new double[kept];
            for (int it = 0; it < PlayerAdjustedIterations; it++)
            {
                Parallel.For(0, groupTotal, g =>
                {
                    for (int k = groupStart[g]; k < groupStart[g + 1]; k++) { int i = byGroup[k]; residuals[k] = y[i] - gen[item[i]] - distance[bin[i]]; }
                    skill[g] = Center(residuals.AsSpan(groupStart[g], groupStart[g + 1] - groupStart[g]));
                });
                Parallel.For(0, itemCount, m =>
                {
                    for (int k = itemStart[m]; k < itemStart[m + 1]; k++) { int i = byItem[k]; residuals[k] = y[i] - skill[group[i]] - distance[bin[i]]; }
                    gen[m] = Center(residuals.AsSpan(itemStart[m], count[m]));
                });
                if (!PlayerAdjustedDistanceTerm) continue;
                Parallel.For(0, binCount, b =>
                {
                    for (int k = binStart[b]; k < binStart[b + 1]; k++) { int i = byBin[k]; residuals[k] = y[i] - skill[group[i]] - gen[item[i]]; }
                    distance[b] = Center(residuals.AsSpan(binStart[b], binStart[b + 1] - binStart[b]));
                });
                // The reference bin at 0 (a constant moves into the skills on the next iteration)
                double reference = referenceBin >= 0 && referenceBin < binCount ? distance[referenceBin] : 0;
                for (int b = 0; b < binCount; b++) distance[b] -= reference;
            }

            var fitted = Enumerable.Range(0, itemCount).Where(i => count[i] > 0).ToList();
            var values = new double?[itemCount];
            counts = new int[itemCount];
            foreach (var i in fitted) counts[i] = count[i];
            var nomod = fitted.Where(i => i % WorthSpeeds.Length == 0).Select(i => gen[i]).OrderBy(v => v).ToArray();
            if (nomod.Length == 0) return values;
            double median = nomod[nomod.Length / 2];
            foreach (var i in fitted) values[i] = Math.Exp(gen[i] - median);
            return values;
        }

        // The mean of these residuals (in their order), or their PlayerAdjustedQuantile quantile (sorts them; linear between
        // the two nearest, as numpy's). 0 when empty.
        private static double Center(Span<double> values)
        {
            if (values.Length == 0) return 0;
            if (PlayerAdjustedQuantile <= 0)
            {
                double sum = 0;
                foreach (var v in values) sum += v;
                return sum / values.Length;
            }
            values.Sort();
            double position = Math.Min(PlayerAdjustedQuantile, 1) * (values.Length - 1);
            int below = (int)position;
            return below + 1 < values.Length ? values[below] + (position - below) * (values[below + 1] - values[below]) : values[below];
        }

        // Indices of key's entries grouped by key (counting sort, each key's in their order): Order[Start[k]..Start[k + 1]]
        private static (int[] Start, int[] Order) Bucket(int[] key, int keyCount)
        {
            var start = new int[keyCount + 1];
            foreach (var k in key) start[k + 1]++;
            for (int k = 0; k < keyCount; k++) start[k + 1] += start[k];
            var next = (int[])start.Clone();
            var order = new int[key.Length];
            for (int i = 0; i < key.Length; i++) order[next[key[i]]++] = i;
            return (start, order);
        }

        // How many scores each map's worth rests on, for WorthMeasure (Megametric75: the scores its average takes; InRange: its
        // in-range scores; PlayerAdjusted: the in-range scores it's fitted on). The acc reweighter moves maps with few less.
        internal static int[] WorthEvidence(Data data, Evaluation e, bool[]? players = null)
        {
            var counts = new int[data.Maps.Length];
            switch (WorthMeasure)
            {
                case WorthMeasures.PlayerAdjusted:
                    PlayerAdjustedValues(data, e, out counts, players);
                    break;
                case WorthMeasures.InRange:
                    var levels = PlayerLevels(data, e);
                    Parallel.For(0, counts.Length, m => { InRangeValue(data, e, levels, m, out int n, players); counts[m] = n; });
                    break;
                default:
                    Parallel.For(0, counts.Length, m => { Megametric75(data, e, m, out int n, players); counts[m] = n; });
                    break;
            }
            return counts;
        }

        // PlayerAdjusted worth of every item against nomod maps first played around the same time (Gaussian kernel of
        // PlayerAdjustedAgeBandwidthDays on each map's first score): the map age effect (see WorthMeasures)
        private static void RemoveMapAge(Data data, double?[] worth)
        {
            int n = WorthSpeeds.Length;
            var first = new double[data.Maps.Length];
            for (int m = 0; m < first.Length; m++) first[m] = data.Maps[m].Scores.Length > 0 ? data.Maps[m].Scores.Min(s => data.ScoreTime[s]) : 0;
            var withWorth = Enumerable.Range(0, data.Maps.Length).Where(m => worth[m * n] != null).ToArray();
            var needed = Enumerable.Range(0, data.Maps.Length).Where(m => Enumerable.Range(0, n).Any(k => worth[m * n + k] != null)).ToArray();
            double h = PlayerAdjustedAgeBandwidthDays * 86400;
            var reference = new double[data.Maps.Length];
            Parallel.ForEach(needed, m =>
            {
                double sw = 0, sy = 0;
                foreach (var o in withWorth)
                {
                    double d = first[o] - first[m];
                    if (Math.Abs(d) > 4 * h) continue;
                    double w = Math.Exp(-0.5 * d * d / (h * h));
                    sw += w; sy += w * worth[o * n]!.Value;
                }
                reference[m] = sw > 0 ? sy / sw : 0;
            });
            for (int i = 0; i < worth.Length; i++) if (worth[i] is double v) worth[i] = (1 + v) / (1 + reference[i / n]) - 1;
        }

        // Each map's value (WorthMeasure) relative to what maps of its stars average (WorthReference): the nomod items of
        // ItemWorth. Null when the map has too few scores, or its star range fewer than 5 maps.
        internal static double?[] Worth(Data data, Evaluation e, out double[] bandMeans, bool[]? players = null)
        {
            int n = WorthSpeeds.Length;
            var items = ItemWorth(data, e, out bandMeans, players);
            return Enumerable.Range(0, data.Maps.Length).Select(m => items[m * n]).ToArray();
        }

        // Worth of every item (map and speed modifier, ItemOf), each against nomod maps of its stars. Megametric75 and InRange
        // have nomod worth only.
        internal static double?[] ItemWorth(Data data, Evaluation e, out double[] bandMeans, bool[]? players = null)
        {
            int n = WorthSpeeds.Length, mapCount = data.Maps.Length;
            var itemStars = ItemStars(data);
            var values = new double?[mapCount * n];
            if (WorthMeasure == WorthMeasures.PlayerAdjusted)
            {
                values = PlayerAdjustedItemValues(data, e, out _, players, itemStars);
            }
            else
            {
                var levels = WorthMeasure == WorthMeasures.InRange ? PlayerLevels(data, e, itemStars) : null;
                Parallel.For(0, mapCount, m => values[m * n] = levels != null
                    ? InRangeValue(data, e, levels, m, out _, players)
                    : Megametric75(data, e, m, out _, players));
            }

            // The reference: nomod maps, by star range
            var means = new double[StarBandCount];
            var usedBands = new HashSet<int>();
            var used = new List<IGrouping<int, int>>();
            foreach (var band in Enumerable.Range(0, mapCount).Where(m => values[m * n] != null).GroupBy(m => data.Maps[m].StarBand))
            {
                double mean = band.Average(m => values[m * n]!.Value);
                means[band.Key] = mean;
                if (mean <= 0 || band.Count() < 5) continue;
                used.Add(band);
                usedBands.Add(band.Key);
            }
            var points = used.SelectMany(band => band).Select(m => ((double)data.Maps[m].Stars, values[m * n]!.Value)).ToList();

            var worth = new double?[mapCount * n];
            Parallel.For(0, worth.Length, i =>
            {
                if (values[i] is not double v || double.IsNaN(itemStars[i])) return;
                // Nomod: the map's own star range; speed modifiers: the range of their stars
                int band = i % n == 0 ? data.Maps[i / n].StarBand : StarBandOf((float)itemStars[i]);
                if (!usedBands.Contains(band)) return;
                double reference = WorthReference(points, itemStars[i]);
                if (reference > 0) worth[i] = v / reference - 1;
            });
            if (WorthMeasure == WorthMeasures.PlayerAdjusted) RemoveMapAge(data, worth);
            bandMeans = means;
            return worth;
        }

        // Gaussian kernel width (stars) of WorthReference: narrow enough to follow bends in the value curve, wide enough
        // that a map's position inside its star range doesn't matter.
        public static double WorthBandwidth { get; set; } = 0.25;

        // The value a map's worth is measured against: what maps of its stars average, as a local linear fit of value on
        // stars (Gaussian kernel of WorthBandwidth; points: every map with a worth). A smooth reference rather than per-range
        // averages, so maps near a range boundary aren't compared with the next range all at once.
        public static double WorthReference(IReadOnlyList<(double Stars, double Megametric)> points, double stars)
        {
            double h = WorthBandwidth;
            double sw = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
            foreach (var (x, y) in points)
            {
                double d = x - stars;
                if (Math.Abs(d) > 4 * h) continue;
                double w = Math.Exp(-0.5 * d * d / (h * h));
                sw += w; sx += w * d; sy += w * y; sxx += w * d * d; sxy += w * d * y;
            }
            if (sw <= 0) return 0;
            double det = sw * sxx - sx * sx;
            double local = det > 1e-9 * sw * sw ? (sy * sxx - sx * sxy) / det : sy / sw;
            return local > 0 ? local : sy / sw;
        }

        // kinds: ratings used to classify maps (pp shares at the predicted acc) when they differ from data's. The acc
        // reweighter passes its un-reweighted ratings, else nerfing a map's acc raises its pass share and the slopes
        // measure the reweighting itself.
        internal static BalanceStats MeasureBalance(Data data, Evaluation e, double?[] residual, Data? kinds = null)
        {
            int mapCount = data.Maps.Length;
            var passShare = new double[mapCount];
            var accShare = new double[mapCount];
            var techShare = new double[mapCount];
            Parallel.For(0, mapCount, m =>
            {
                var lb = (kinds ?? data).Maps[m].Leaderboard;
                if (lb.PredictedAcc <= 0 || lb.PredictedAcc >= 1) return;
                var (_, _, passPp, accPp, techPp) = ReplayUtils.PpFromScore(lb.PredictedAcc, "", null, lb.AccRating, lb.PassRating, lb.TechRating);
                double total = passPp + accPp + techPp;
                if (total <= 0) return;
                passShare[m] = passPp / total;
                accShare[m] = accPp / total;
                techShare[m] = techPp / total;
            });

            var used = Enumerable.Range(0, mapCount).Where(m => residual[m] != null && data.Maps[m].Leaderboard.PredictedAcc > 0).ToList();
            double Slope(Func<int, double> x) => RegressionSlope(used.Select(x).ToList(), used.Select(m => residual[m]!.Value).ToList());

            var oneSaber = used.Where(m => IsOneSaber(data.Maps[m].Leaderboard)).ToList();
            var standard = used.Where(m => IsStandard(data.Maps[m].Leaderboard)).ToList();

            // Where today's top 1000 (by this candidate's ranking) get their weighted pp from
            double sumPass = 0, sumAcc = 0, sumTech = 0;
            for (int p = 0; p < data.PlayerCount; p++)
            {
                if (e.PlayerRank[p] > 1000) continue;
                foreach (var s in data.PlayerScores[p])
                {
                    sumPass += e.PassPp[s] * e.Weight[s];
                    sumAcc += e.AccPp[s] * e.Weight[s];
                    sumTech += e.TechPp[s] * e.Weight[s];
                }
            }
            double sumAll = sumPass + sumAcc + sumTech;

            return new BalanceStats
            {
                Maps = used.Count,
                PassShareSlope = Slope(m => passShare[m] * 10),
                AccShareSlope = Slope(m => accShare[m] * 10),
                TechShareSlope = Slope(m => techShare[m] * 10),
                PredictedAccSlope = Slope(m => data.Maps[m].Leaderboard.PredictedAcc * 100),
                PredictedAccGap = QuintileGap(used, m => data.Maps[m].Leaderboard.PredictedAcc, residual),
                PassShareGap = QuintileGap(used, m => passShare[m], residual),
                TechShareGap = QuintileGap(used, m => techShare[m], residual),
                OneSaberMaps = oneSaber.Count,
                OneSaberGap = OneSaberGap(data, oneSaber, standard, residual),
                WorthSpread = used.Count > 1 ? Math.Sqrt(used.Average(m => residual[m]!.Value * residual[m]!.Value)) : 0,
                Top1000PassShare = sumAll > 0 ? sumPass / sumAll : 0,
                Top1000AccShare = sumAll > 0 ? sumAcc / sumAll : 0,
                Top1000TechShare = sumAll > 0 ? sumTech / sumAll : 0,
                ByType = MapTypes(data, used, residual, passShare, accShare, techShare),
            };
        }

        // OneSaber maps' average worth minus Standard maps' in the same star ranges (each OneSaber map compared with the
        // Standard maps of its own range, so the result doesn't depend on how OneSaber maps are spread over the stars)
        private static double OneSaberGap(Data data, List<int> oneSaber, List<int> standard, double?[] residual)
        {
            if (oneSaber.Count < MinOneSaberMaps || standard.Count == 0) return 0;
            var standardByBand = standard.GroupBy(m => data.Maps[m].StarBand).ToDictionary(g => g.Key, g => g.Average(m => residual[m]!.Value));
            var gaps = oneSaber
                .Where(m => standardByBand.ContainsKey(data.Maps[m].StarBand))
                .Select(m => residual[m]!.Value - standardByBand[data.Maps[m].StarBand])
                .ToList();
            return gaps.Count > 0 ? gaps.Average() : 0;
        }

        // Worth of the top 20% of maps by a feature minus the bottom 20%
        private static double QuintileGap(List<int> used, Func<int, double> feature, double?[] residual)
        {
            if (used.Count < 25) return 0;
            var ordered = used.OrderBy(feature).ToList();
            int n = ordered.Count / 5;
            return ordered.Skip(ordered.Count - n).Average(m => residual[m]!.Value) - ordered.Take(n).Average(m => residual[m]!.Value);
        }

        // Average worth of a few kinds of maps (by fifths of each feature), for the page
        private static List<MapTypeWorth> MapTypes(Data data, List<int> used, double?[] residual, double[] passShare, double[] accShare, double[] techShare)
        {
            double Cut(Func<int, double> feature, double q)
            {
                var sorted = used.Select(feature).OrderBy(v => v).ToList();
                return sorted.Count > 0 ? sorted[Math.Clamp((int)(q * (sorted.Count - 1)), 0, sorted.Count - 1)] : 0;
            }
            double accHigh = Cut(m => accShare[m], 0.8), accLow = Cut(m => accShare[m], 0.2);
            double techHigh = Cut(m => techShare[m], 0.8), techLow = Cut(m => techShare[m], 0.2);
            double passHigh = Cut(m => passShare[m], 0.8), passLow = Cut(m => passShare[m], 0.2);
            double predHigh = Cut(m => data.Maps[m].Leaderboard.PredictedAcc, 0.8), predLow = Cut(m => data.Maps[m].Leaderboard.PredictedAcc, 0.2);
            double passTop = Cut(m => data.Maps[m].Leaderboard.PassRating, 0.95);
            var types = new List<(string Name, Func<int, bool> Match)>
            {
                (FormattableString.Invariant($"Most acc-heavy 20% (acc pp share {accHigh:P0}+)"), m => accShare[m] >= accHigh),
                (FormattableString.Invariant($"Least acc-heavy 20% (acc pp share under {accLow:P0})"), m => accShare[m] <= accLow),
                (FormattableString.Invariant($"Most pass-heavy 20% (pass pp share {passHigh:P0}+)"), m => passShare[m] >= passHigh),
                (FormattableString.Invariant($"Least pass-heavy 20% (pass pp share under {passLow:P0})"), m => passShare[m] <= passLow),
                (FormattableString.Invariant($"Most tech-heavy 20% (tech pp share {techHigh:P0}+)"), m => techShare[m] >= techHigh),
                (FormattableString.Invariant($"Least tech-heavy 20% (tech pp share under {techLow:P0})"), m => techShare[m] <= techLow),
                (FormattableString.Invariant($"Highest predicted acc 20% ({predHigh:P1}+)"), m => data.Maps[m].Leaderboard.PredictedAcc >= predHigh),
                (FormattableString.Invariant($"Lowest predicted acc 20% (under {predLow:P1})"), m => data.Maps[m].Leaderboard.PredictedAcc <= predLow),
                (FormattableString.Invariant($"Highest pass 5% (pass {passTop:0.#}+)"), m => data.Maps[m].Leaderboard.PassRating >= passTop),
                ("OneSaber", m => IsOneSaber(data.Maps[m].Leaderboard)),
            };
            return types.Select(t =>
            {
                var maps = used.Where(t.Match).ToList();
                return new MapTypeWorth { Type = t.Name, Maps = maps.Count, Worth = maps.Count > 0 ? maps.Average(m => residual[m]!.Value) : 0 };
            }).ToList();
        }

        private static List<BandPoint> Bands(Data data, Evaluation e, Evaluation reference, double[] bandMeans)
        {
            var topPlayerPp = new List<double>[StarBandCount];
            for (int b = 0; b < StarBandCount; b++) topPlayerPp[b] = new List<double>();
            for (int s = 0; s < data.ScoreMap.Length; s++)
            {
                if (reference.PlayerRank[data.ScorePlayer[s]] <= 100) topPlayerPp[data.Maps[data.ScoreMap[s]].StarBand].Add(e.Pp[s]);
            }

            var bands = new List<BandPoint>();
            for (int b = 0; b < StarBandCount; b++)
            {
                var maps = Enumerable.Range(0, data.Maps.Length).Where(m => data.Maps[m].StarBand == b).ToList();
                var atPredicted = maps
                    .Select(m => data.Maps[m].Leaderboard)
                    .Where(lb => lb.PredictedAcc > 0 && lb.PredictedAcc < 1)
                    .Select(lb => (double)ReplayUtils.PpFromScore(lb.PredictedAcc, "", null, lb.AccRating, lb.PassRating, lb.TechRating).Item1)
                    .ToList();
                var best = maps.Where(m => data.Maps[m].Scores.Length > 0).Select(m => (double)data.Maps[m].Scores.Max(s => e.Pp[s])).ToList();
                bands.Add(new BandPoint
                {
                    Band = StarBandName(b),
                    Maps = maps.Count,
                    MegametricMean = bandMeans[b],
                    PpAtPredicted = Median(atPredicted),
                    TopPlayersMedianPp = Median(topPlayerPp[b]),
                    TopPlayersScores = topPlayerPp[b].Count,
                    BestPpMedian = Median(best),
                    Maps700 = best.Count(v => v >= 700),
                    Share700 = best.Count > 0 ? best.Count(v => v >= 700) / (double)best.Count : 0,
                });
            }
            return bands;
        }

        private static BalanceSummary Summarize(Data data, Evaluation e, Evaluation reference)
        {
            var sorted = e.PlayerPp.OrderByDescending(v => v).ToArray();
            double At(int i) => i < sorted.Length ? sorted[i] : 0;

            // Star ranges where the top 100 players' median pp drops compared with the range below
            var bands = Bands(data, e, reference, new double[StarBandCount]);
            int inversions = 0, ppInversions = 0;
            double? previous = null, previousAt = null;
            foreach (var band in bands.Where(b => b.TopPlayersScores >= 30))
            {
                if (previous != null && band.TopPlayersMedianPp < previous) inversions++;
                previous = band.TopPlayersMedianPp;
            }
            foreach (var band in bands.Where(b => b.Maps >= 10))
            {
                if (previousAt != null && band.PpAtPredicted < previousAt) ppInversions++;
                previousAt = band.PpAtPredicted;
            }

            var top100 = Enumerable.Range(0, data.PlayerCount).Where(p => reference.PlayerRank[p] <= 100).ToList();
            var withMaps = bands.Select((band, i) => (Band: band, Index: i)).Where(x => x.Band.Maps >= 10).ToList();
            var withPlayers = bands.Select((band, i) => (Band: band, Index: i)).Where(x => x.Band.TopPlayersScores >= 30).ToList();
            var shares = bands.Where(x => x.Maps >= 30).Select(x => x.Share700).ToList();
            double shareMean = shares.Count > 0 ? shares.Average() : 0;
            return new BalanceSummary
            {
                PpPerStarAtPredicted = RegressionSlope(withMaps.Select(x => (double)x.Index).ToList(), withMaps.Select(x => x.Band.PpAtPredicted).ToList()),
                PpPerStarTopPlayers = RegressionSlope(withPlayers.Select(x => (double)x.Index).ToList(), withPlayers.Select(x => x.Band.TopPlayersMedianPp).ToList()),
                MapsUnder10Stars700 = bands.Take(10).Sum(x => x.Maps700),
                Share700Spread = shares.Count > 1 && shareMean > 0 ? Math.Sqrt(shares.Average(v => (v - shareMean) * (v - shareMean))) / shareMean : 0,
                Top1Pp = At(0),
                Top10Pp = At(9),
                Top100Pp = At(99),
                Top1000Pp = At(999),
                RankCorrelationTop1000 = RankCorrelation(reference, e, 1000),
                AverageRankChangeTop100 = top100.Count > 0 ? top100.Average(p => Math.Abs(e.PlayerRank[p] - reference.PlayerRank[p])) : 0,
                ProgressionInversions = inversions,
                PpAtPredictedInversions = ppInversions,
            };
        }

        // Spearman correlation between the reference ranking and the candidate's, for the reference top N
        private static double RankCorrelation(Evaluation reference, Evaluation e, int topN)
        {
            var players = Enumerable.Range(0, reference.PlayerRank.Length).Where(p => reference.PlayerRank[p] <= topN).ToList();
            int n = players.Count;
            if (n < 3) return 1;
            var candidateOrder = players.OrderBy(p => e.PlayerRank[p]).ToList();
            var candidateRank = new Dictionary<int, int>(n);
            for (int i = 0; i < n; i++) candidateRank[candidateOrder[i]] = i + 1;
            double sum = 0;
            foreach (var p in players)
            {
                double d = reference.PlayerRank[p] - candidateRank[p];
                sum += d * d;
            }
            return 1 - 6 * sum / (n * ((double)n * n - 1));
        }

        private static double RegressionSlope(List<double> x, List<double> y)
        {
            if (x.Count < 3) return 0;
            double mx = x.Average(), my = y.Average();
            double cov = 0, varX = 0;
            for (int i = 0; i < x.Count; i++)
            {
                cov += (x[i] - mx) * (y[i] - my);
                varX += (x[i] - mx) * (x[i] - mx);
            }
            return varX > 0 ? cov / varX : 0;
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToArray();
            double position = 0.5 * (sorted.Length - 1);
            int low = (int)Math.Floor(position);
            int high = Math.Min(low + 1, sorted.Length - 1);
            return sorted[low] + (position - low) * (sorted[high] - sorted[low]);
        }

        private static string Describe(BalanceStats b) => FormattableString.Invariant(
            $"pass slope {b.PassShareSlope:+0.000;-0.000}, tech slope {b.TechShareSlope:+0.000;-0.000}, predicted acc gap {b.PredictedAccGap:+0.000;-0.000}, one saber gap {b.OneSaberGap:+0.000;-0.000}");
    }

    // ------------------------------------------------------------------
    // Saved results (wwwroot/balance-tuning.json)
    // ------------------------------------------------------------------

    public class BalanceTuningResult
    {
        public DateTime GeneratedAt { get; set; }
        // BalanceTuner.WorthMeasure the run used (empty in old files: Megametric75)
        public string WorthMeasure { get; set; } = "";
        public int OneSaberMaps { get; set; }
        public RatingConstants RatedWith { get; set; } = new();
        public RatingConstants SuggestedConstants { get; set; } = new();
        public BalancePoint Current { get; set; } = new();
        public BalancePoint Suggested { get; set; } = new();
        public List<KnobSweep> Sweeps { get; set; } = new();
    }

    public class BalancePoint
    {
        public RatingConstants Constants { get; set; } = new();
        public BalanceStats Balance { get; set; } = new();
        public BalanceSummary Summary { get; set; } = new();
        public List<BandPoint> Bands { get; set; } = new();
    }

    public class BalanceSummary
    {
        public double Top1Pp { get; set; }
        public double Top10Pp { get; set; }
        public double Top100Pp { get; set; }
        public double Top1000Pp { get; set; }
        // Spearman correlation with the ranking from the constants in code, today's top 1000
        public double RankCorrelationTop1000 { get; set; }
        public double AverageRankChangeTop100 { get; set; }
        // Star ranges where the top 100 players' median pp / the median pp at the predicted acc drops compared with the
        // range below (should be 0)
        public int ProgressionInversions { get; set; }
        public int PpAtPredictedInversions { get; set; }
        // Progression: pp gained per star (slope over star ranges) at the predicted acc, and for the top 100 players' scores
        public double PpPerStarAtPredicted { get; set; }
        public double PpPerStarTopPlayers { get; set; }
        // Maps under 10 stars whose best score is worth 700pp+, and how different the share of 700pp+ maps is between star
        // ranges with 30+ maps (coefficient of variation, lower = more similar)
        public int MapsUnder10Stars700 { get; set; }
        public double Share700Spread { get; set; }
    }

    public class PpFormulaResult
    {
        public DateTime GeneratedAt { get; set; }
        public string Formula { get; set; } = "";
        public RatingConstants RatedWith { get; set; } = new();
        public BalancePoint Current { get; set; } = new();
        public List<FormulaSweep> Sweeps { get; set; } = new();
    }

    public class FormulaSweep
    {
        public string Knob { get; set; } = "";
        public string Explanation { get; set; } = "";
        public double CurrentValue { get; set; }
        public List<FormulaPoint> Points { get; set; } = new();
    }

    public class FormulaPoint
    {
        public double Value { get; set; }
        public BalancePoint Point { get; set; } = new();
    }

    public class BandPoint
    {
        public string Band { get; set; } = "";
        public int Maps { get; set; }
        public double MegametricMean { get; set; }
        public double PpAtPredicted { get; set; }
        public double TopPlayersMedianPp { get; set; }
        public int TopPlayersScores { get; set; }
        // Best pp set on each map: median, and how many / what share of the range's played maps reach 700pp
        public double BestPpMedian { get; set; }
        public int Maps700 { get; set; }
        public double Share700 { get; set; }
    }

    public class BalanceStats
    {
        public int Maps { get; set; }
        // Change of a map's worth (BalanceTuner.WorthMeasure) per +10% of pass / acc / tech pp share at the predicted acc,
        // and per +1 point of predicted acc. 0 = balanced.
        public double PassShareSlope { get; set; }
        public double AccShareSlope { get; set; }
        public double TechShareSlope { get; set; }
        public double PredictedAccSlope { get; set; }
        // Worth of the top 20% of maps minus the bottom 20%, by predicted acc / pass share / tech share (0 = balanced)
        public double PredictedAccGap { get; set; }
        public double PassShareGap { get; set; }
        public double TechShareGap { get; set; }
        // OneSaber maps' average worth minus Standard maps' of the same stars (0 = balanced)
        public int OneSaberMaps { get; set; }
        public double OneSaberGap { get; set; }
        // Typical distance of a map's worth from what maps of its stars average (lower = more even)
        public double WorthSpread { get; set; }
        // Where the top 1000 players' weighted pp comes from
        public double Top1000PassShare { get; set; }
        public double Top1000AccShare { get; set; }
        public double Top1000TechShare { get; set; }
        public List<MapTypeWorth> ByType { get; set; } = new();
    }

    public class MapTypeWorth
    {
        public string Type { get; set; } = "";
        public int Maps { get; set; }
        // Average worth relative to the star range (+0.10 = 10% more valuable than the average map of the same stars)
        public double Worth { get; set; }
    }

    public class KnobSweep
    {
        public string Knob { get; set; } = "";
        public string Target { get; set; } = "";
        public int Round { get; set; }
        public double Suggested { get; set; }
        public bool AtEdge { get; set; }
        // Why the constant was left where it was (KeepReason), null when it was moved
        public string? Kept { get; set; }
        public List<SweepPoint> Points { get; set; } = new();
    }

    public class SweepPoint
    {
        public double Value { get; set; }
        public double Target { get; set; }
        public double RankCorrelationTop1000 { get; set; }
        public int StarOrderBreaks { get; set; }
        public double Top100Pp { get; set; }
    }
}
