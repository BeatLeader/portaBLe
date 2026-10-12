using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;

namespace portaBLe.Refresh
{
    // Rating constants the balance tuner can change without re-analysing any map. Each one is a simple transform of the
    // ratings already stored in the database:
    // - PassCalibration (Analyzer AnalyzeMap.PASS_CALIBRATION_FACTOR): pass rating is proportional to it.
    // - OneSaberNerf (AnalyzeMap.ONE_SABER_NERF): pass is multiplied by 1 - (1 - handsRatio) * nerf. handsRatio isn't stored,
    //   so the tuner applies it to OneSaber maps only (handsRatio 0 there); Standard maps where one hand does most of the
    //   work are affected too in reality.
    // - TechScaler (AnalyzeMap.BALANCED_TECH_SCALER): tech = swing tech * (1 - 1.4^-pass) * scaler, so tech follows both the
    //   scaler and the new pass rating. Not tuned, so tech ratings keep their usual scale; kept here so the tuner knows what
    //   the database was rated with.
    // - TechWeight (PpFormula.TechWeight): tech pp = e^(1.9 * acc) * TechWeight * tech rating. Does exactly what TechScaler
    //   does to pp and stars, without changing the tech ratings themselves, so this is the one the tuner moves.
    // - AccScale and AccOffset (RatingAPI AccRating.ACC_SCALE / ACC_OFFSET): acc rating = AccScale / Curve(predictedAcc + AccOffset).
    public class RatingConstants
    {
        public double PassCalibration { get; set; } = 0.825;
        public double OneSaberNerf { get; set; } = 0.54;
        public double TechScaler { get; set; } = 14;
        // Follows the PpFormula default, since the tuner re-scores everything with the formula in code
        public double TechWeight { get; set; } = new PpFormula().TechWeight;
        public double AccScale { get; set; } = 15.5;
        public double AccOffset { get; set; } = 0.0019;

        public RatingConstants Clone() => (RatingConstants)MemberwiseClone();

        public override string ToString() => FormattableString.Invariant(
            $"pass {PassCalibration:0.#####}, one saber {OneSaberNerf:0.###}, tech {TechScaler:0.####} (tech pp x{TechWeight:0.####}), acc scale {AccScale:0.###}, acc offset {AccOffset:0.#####}");
    }

    // Balance tuner: suggests pass, tech and acc rating constants so that no kind of map is systematically worth more to
    // players than other maps of the same star rating. Suggestions only: nothing is written to the database, and the
    // Analyzer constants are applied by hand afterwards.
    //
    // For every value tried, the stored ratings are transformed, stars rebuilt, every score re-scored and every player's
    // weighted pp rebuilt, in memory. A map's "worth" is its Megametric75 (how much it counts in the profiles of the players
    // who play it) divided by the average of its star range: 0 = average for its stars, +0.2 = 20% more valuable. Each
    // constant is moved until the worth no longer depends on what it controls:
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

        // The constants the database was rated with (the values in the code when RatingsRefresh.Overwrite last ran).
        // Update these when you change the code and re-rate, so the tuner starts from the right place.
        public static RatingConstants RatedWith { get; set; } = new();

        private const int MinScoresPerMap = 20;     // maps with fewer scores are left out of the per-map metrics
        private const int MegametricMinPlays = 75;  // as LeaderboardsRefresh's Megametric75
        private const int StarBandCount = 14;       // 0-1 ... 12-13, 13+
        private const int MinOneSaberMaps = 10;

        public static string StarBandName(int band) => band >= StarBandCount - 1 ? $"{StarBandCount - 1}+" : $"{band}-{band + 1}";
        private static int StarBandOf(float stars) => Math.Clamp((int)MathF.Floor(stars), 0, StarBandCount - 1);
        private static bool IsOneSaber(Leaderboard lb) => string.Equals(lb.ModeName, "OneSaber", StringComparison.OrdinalIgnoreCase);
        private static bool IsStandard(Leaderboard lb) => string.Equals(lb.ModeName, "Standard", StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------------
        // Data
        // ------------------------------------------------------------------

        private sealed class MapInfo
        {
            public Leaderboard Leaderboard = null!;
            public float Stars;
            public int StarBand;
            public int[] Scores = Array.Empty<int>();
        }

        private sealed class Data
        {
            public MapInfo[] Maps = Array.Empty<MapInfo>();
            public int[] ScoreMap = Array.Empty<int>();
            public int[] ScorePlayer = Array.Empty<int>();
            public float[] ScoreAcc = Array.Empty<float>();
            public string[] ScoreModifiers = Array.Empty<string>();
            public int[][] PlayerScores = Array.Empty<int[]>();
            public int PlayerCount;
        }

        private sealed class Evaluation
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

        private static async Task<Data> Load(AppContext dbContext)
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
                .Select(s => new { s.LeaderboardId, s.PlayerId, s.Accuracy, s.Modifiers })
                .ToListAsync();

            var playerIndex = new Dictionary<string, int>();
            var interned = new Dictionary<string, string>();
            var scoreMap = new List<int>(scores.Count);
            var scorePlayer = new List<int>(scores.Count);
            var scoreAcc = new List<float>(scores.Count);
            var scoreModifiers = new List<string>(scores.Count);
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
                scoreMap.Add(map);
                scorePlayer.Add(player);
                scoreAcc.Add(s.Accuracy);
                scoreModifiers.Add(shared);
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
                PlayerScores = byPlayer.Select(l => l.ToArray()).ToArray(),
                PlayerCount = playerIndex.Count,
            };
        }

        // ------------------------------------------------------------------
        // Run
        // ------------------------------------------------------------------

        public static async Task Run(AppContext dbContext, string webRootPath, int rounds = 2, RatingConstants? ratedWith = null)
        {
            ratedWith ??= RatedWith;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"Balance tuning: loading scores (rated with {ratedWith})...");
            var data = await Load(dbContext);
            Console.WriteLine($"Balance tuning: {data.Maps.Length} leaderboards, {data.ScoreMap.Length} scores, {data.PlayerCount} players loaded in {timer.Elapsed:mm\\:ss}");

            var reference = WithTechWeight(ratedWith, () => Evaluate(WithConstants(data, ratedWith, ratedWith)));
            var result = new BalanceTuningResult
            {
                GeneratedAt = DateTime.UtcNow,
                RatedWith = ratedWith.Clone(),
                OneSaberMaps = data.Maps.Count(m => IsOneSaber(m.Leaderboard)),
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
                    knob.Set(best, sweep.Suggested);
                    result.Sweeps.Add(sweep);
                    Console.WriteLine(FormattableString.Invariant(
                        $"Balance tuning: round {round}, {knob.Name} -> {sweep.Suggested:0.#####}{(atEdge ? " (at the edge of the range tried)" : "")} ({timer.Elapsed:mm\\:ss})"));
                }
            }

            result.SuggestedConstants = best;
            result.Suggested = Score(data, ratedWith, best, reference);
            Console.WriteLine($"Balance tuning: suggested {best}: {Describe(result.Suggested.Balance)}");

            var path = Path.Combine(webRootPath, FileName);
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

        // Only the parts of the formula that shape how pp grows: AccWeight does exactly what ACC_SCALE does, and TechWeight is
        // balanced by Run, so those aren't swept here.
        private static readonly FormulaKnob[] FormulaKnobs =
        {
            new FormulaKnob
            {
                Name = nameof(PpFormula.InflateExponent),
                Explanation = "How much more a higher total is worth (1 = linear). Higher = harder maps and high-star profiles count more.",
                Get = f => f.InflateExponent, Set = (f, v) => f.InflateExponent = v,
                Values = new[] { 1.1, 1.2, 1.25, 1.3, 1.35, 1.4, 1.5 },
            },
            new FormulaKnob
            {
                Name = nameof(PpFormula.PassExponent),
                Explanation = "Pass pp grows with e^(pass^(1/x)). Lower x = pass pp climbs faster on high pass ratings.",
                Get = f => f.PassExponent, Set = (f, v) => f.PassExponent = v,
                Values = new[] { 2.2, 2.4, 2.5, 2.62, 2.75, 2.9, 3.1 },
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
                Values = new[] { 1.3, 1.6, 1.75, 1.9, 2.05, 2.2, 2.5 },
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
                var (pass, tech, acc) = Transform(lb.PassRating, lb.TechRating, lb.AccRating, lb.PredictedAcc, oneSaber, from, to);
                var copy = new Leaderboard
                {
                    Id = lb.Id,
                    Name = lb.Name,
                    Hash = lb.Hash,
                    ModeName = lb.ModeName,
                    DifficultyName = lb.DifficultyName,
                    PredictedAcc = lb.PredictedAcc,
                    PassRating = pass,
                    TechRating = tech,
                    AccRating = acc,
                };
                var mod = lb.ModifiersRating;
                if (mod != null)
                {
                    var (ssPass, ssTech, ssAcc) = Transform(mod.SSPassRating, mod.SSTechRating, mod.SSAccRating, mod.SSPredictedAcc, oneSaber, from, to);
                    var (fsPass, fsTech, fsAcc) = Transform(mod.FSPassRating, mod.FSTechRating, mod.FSAccRating, mod.FSPredictedAcc, oneSaber, from, to);
                    var (sfPass, sfTech, sfAcc) = Transform(mod.SFPassRating, mod.SFTechRating, mod.SFAccRating, mod.SFPredictedAcc, oneSaber, from, to);
                    copy.ModifiersRating = new ModifiersRating
                    {
                        Id = mod.Id,
                        SSPredictedAcc = mod.SSPredictedAcc, SSPassRating = ssPass, SSTechRating = ssTech, SSAccRating = ssAcc,
                        FSPredictedAcc = mod.FSPredictedAcc, FSPassRating = fsPass, FSTechRating = fsTech, FSAccRating = fsAcc,
                        SFPredictedAcc = mod.SFPredictedAcc, SFPassRating = sfPass, SFTechRating = sfTech, SFAccRating = sfAcc,
                    };
                }
                float stars = ReplayUtils.ToStars(copy.AccRating, copy.PassRating, copy.TechRating);
                maps[i] = new MapInfo { Leaderboard = copy, Stars = stars, StarBand = StarBandOf(stars), Scores = original.Scores };
            });

            return new Data
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

        private static (float Pass, float Tech, float Acc) Transform(float pass, float tech, float accRating, float predictedAcc, bool oneSaber,
            RatingConstants from, RatingConstants to)
        {
            double newPass = pass * (to.PassCalibration / from.PassCalibration);
            if (oneSaber)
            {
                newPass *= (1 - to.OneSaberNerf) / Math.Max(1e-6, 1 - from.OneSaberNerf);
            }

            double newTech = tech;
            double oldFactor = 1 - Math.Pow(1.4, -pass);
            double newFactor = 1 - Math.Pow(1.4, -newPass);
            if (oldFactor > 1e-6)
            {
                newTech = tech * (to.TechScaler / from.TechScaler) * newFactor / oldFactor;
            }

            double newAcc = accRating;
            if (predictedAcc > 0)
            {
                double oldBase = from.AccScale / ReplayUtils.Curve((float)(predictedAcc + from.AccOffset));
                double newBase = to.AccScale / ReplayUtils.Curve((float)(predictedAcc + to.AccOffset));
                if (oldBase > 0 && double.IsFinite(newBase)) newAcc = accRating * newBase / oldBase;
            }
            return ((float)newPass, (float)newTech, (float)newAcc);
        }

        // ------------------------------------------------------------------
        // Re-score everything
        // ------------------------------------------------------------------

        private static Evaluation Evaluate(Data data)
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

            Parallel.For(0, scoreCount, new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount }, s =>
            {
                var lb = data.Maps[data.ScoreMap[s]].Leaderboard;
                var (pp, _, passPp, accPp, techPp) = ReplayUtils.PpFromScore(data.ScoreAcc[s], data.ScoreModifiers[s], lb.ModifiersRating,
                    lb.AccRating, lb.PassRating, lb.TechRating);
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
        private static double? Megametric75(Data data, Evaluation e, int m)
        {
            var scores = data.Maps[m].Scores;
            if (scores.Length < MinScoresPerMap) return null;
            var withTop = scores.Where(s => e.PlayerTopPp[data.ScorePlayer[s]] > 0).ToList();
            int topCount = (int)(withTop.Count * 0.33);
            var filtered = withTop
                .Where(s => data.PlayerScores[data.ScorePlayer[s]].Length > MegametricMinPlays)
                .OrderByDescending(s => e.Weight[s])
                .Take(topCount)
                .ToList();
            if (filtered.Count <= 10) return null;
            return filtered.Average(s => e.Pp[s] / e.PlayerTopPp[data.ScorePlayer[s]] * e.Weight[s]);
        }

        // Each map's Megametric75 relative to the average of its star range (null when the map has too few scores)
        private static double?[] Worth(Data data, Evaluation e, out double[] bandMeans)
        {
            int mapCount = data.Maps.Length;
            var megametric = new double?[mapCount];
            Parallel.For(0, mapCount, m => megametric[m] = Megametric75(data, e, m));

            var means = new double[StarBandCount];
            var worth = new double?[mapCount];
            foreach (var band in Enumerable.Range(0, mapCount).Where(m => megametric[m] != null).GroupBy(m => data.Maps[m].StarBand))
            {
                double mean = band.Average(m => megametric[m]!.Value);
                means[band.Key] = mean;
                if (mean <= 0 || band.Count() < 5) continue;
                foreach (var m in band) worth[m] = megametric[m]!.Value / mean - 1;
            }
            bandMeans = means;
            return worth;
        }

        private static BalanceStats MeasureBalance(Data data, Evaluation e, double?[] residual)
        {
            int mapCount = data.Maps.Length;
            var passShare = new double[mapCount];
            var accShare = new double[mapCount];
            var techShare = new double[mapCount];
            Parallel.For(0, mapCount, m =>
            {
                var lb = data.Maps[m].Leaderboard;
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
        // Change of a map's worth (Megametric75 relative to its star range) per +10% of pass / acc / tech pp share at the
        // predicted acc, and per +1 point of predicted acc. 0 = balanced.
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
        // Typical distance of a map's worth from its star range's average (lower = more even)
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
