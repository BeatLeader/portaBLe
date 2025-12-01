using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Refresh;
using portaBLe.Services;
using System.Text.Json;

namespace portaBLe.Pages
{
    public class BatchPlayersBalancingModel : BasePageModel
    {
        public BatchPlayersBalancingModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        [BindProperty]
        public int? MinRank { get; set; }

        [BindProperty]
        public int? MaxRank { get; set; }

        public List<string> PlayerIds { get; set; }
        public List<Player> Players { get; set; }
        public int TotalScores { get; set; }
        public List<ScoreData> AllScores { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            // Just show the empty form on GET
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);

            if (!MinRank.HasValue || !MaxRank.HasValue)
            {
                return Page();
            }

            if (MinRank.Value < 1 || MaxRank.Value < MinRank.Value)
            {
                ModelState.AddModelError(string.Empty, "Invalid rank range");
                return Page();
            }

            using var context = (Services.DynamicDbContext)GetDbContext();

            // Load players by rank range
            Players = await context.Players
                .Where(p => p.Rank >= MinRank.Value && p.Rank <= MaxRank.Value && p.Pp > 0)
                .OrderBy(p => p.Rank)
                .ToListAsync();

            if (!Players.Any())
            {
                return Page();
            }

            PlayerIds = Players.Select(p => p.Id).ToList();

            // Load all scores for these players
            AllScores = await context.Scores
                .Include(s => s.Leaderboard)
                .ThenInclude(l => l.ModifiersRating)
                .Where(s => PlayerIds.Contains(s.PlayerId))
                .OrderByDescending(s => s.Pp)
                .Select(s => new ScoreData
                {
                    PlayerId = s.PlayerId,
                    Id = s.Id,
                    LeaderboardName = s.Leaderboard.Name,
                    Accuracy = s.Accuracy,
                    Modifiers = s.Modifiers,
                    AccRating = s.Leaderboard.AccRating,
                    PassRating = s.Leaderboard.PassRating,
                    TechRating = s.Leaderboard.TechRating,
                    PredictedAcc = s.Leaderboard.PredictedAcc,
                    CurrentPP = s.Pp,
                    CurrentAccPP = s.AccPP,
                    CurrentTechPP = s.TechPP,
                    CurrentPassPP = s.PassPP,
                    Weight = s.Weight,
                    Timepost = s.Timepost
                })
                .ToListAsync();

            TotalScores = AllScores.Count;

            return Page();
        }

        public async Task<IActionResult> OnPostRecalculateBatchAsync([FromBody] RecalculateBatchRequest request, string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);

            using var context = (Services.DynamicDbContext)GetDbContext();

            var playerResults = new List<PlayerResult>();

            foreach (var playerId in request.PlayerIds)
            {
                var scores = await context.Scores
                    .Include(s => s.Leaderboard)
                    .ThenInclude(l => l.ModifiersRating)
                    .Where(s => s.PlayerId == playerId)
                    .OrderByDescending(s => s.Pp)
                    .ToListAsync();

                if (!scores.Any()) continue;

                var recalculatedScores = new List<RecalculatedScoreData>();

                foreach (var score in scores)
                {
                    var (newPP, bonusPP, newPassPP, newAccPP, newTechPP) = CalculatePP(
                        score.Accuracy,
                        score.Modifiers,
                        score.Leaderboard.ModifiersRating,
                        score.Leaderboard.AccRating,
                        score.Leaderboard.PassRating,
                        score.Leaderboard.TechRating,
                        score.Leaderboard.PredictedAcc,
                        request.Parameters
                    );

                    recalculatedScores.Add(new RecalculatedScoreData
                    {
                        Id = score.Id,
                        Accuracy = score.Accuracy,
                        CurrentPP = score.Pp,
                        CurrentAccPP = score.AccPP,
                        CurrentTechPP = score.TechPP,
                        CurrentPassPP = score.PassPP,
                        NewPP = newPP,
                        NewAccPP = newAccPP,
                        NewTechPP = newTechPP,
                        NewPassPP = newPassPP,
                        Weight = score.Weight,
                        Timepost = score.Timepost
                    });
                }

                var totalPP = CalculateWeightedTotal(recalculatedScores.Select(s => s.NewPP).ToList());
                var totalAccPP = CalculateWeightedTotal(recalculatedScores.Select(s => s.NewAccPP).ToList());
                var totalTechPP = CalculateWeightedTotal(recalculatedScores.Select(s => s.NewTechPP).ToList());
                var totalPassPP = CalculateWeightedTotal(recalculatedScores.Select(s => s.NewPassPP).ToList());

                var originalTotalPP = scores.Sum(s => s.Pp * s.Weight);
                var originalAccPP = scores.Sum(s => s.AccPP * s.Weight);
                var originalTechPP = scores.Sum(s => s.TechPP * s.Weight);
                var originalPassPP = scores.Sum(s => s.PassPP * s.Weight);

                playerResults.Add(new PlayerResult
                {
                    PlayerId = playerId,
                    Scores = recalculatedScores,
                    TotalPP = totalPP,
                    TotalAccPP = totalAccPP,
                    TotalTechPP = totalTechPP,
                    TotalPassPP = totalPassPP,
                    OriginalTotalPP = originalTotalPP,
                    OriginalAccPP = originalAccPP,
                    OriginalTechPP = originalTechPP,
                    OriginalPassPP = originalPassPP
                });
            }

            // Calculate aggregated metrics
            var aggregated = CalculateAggregatedMetrics(playerResults);

            return new JsonResult(new
            {
                playerResults = playerResults,
                aggregated = aggregated.Aggregated,
                metrics = aggregated.Metrics,
                aggregatedDecay = aggregated.AggregatedDecay,
                componentBalance = aggregated.ComponentBalance,
                accuracyReward = aggregated.AccuracyReward,
                playerChanges = aggregated.PlayerChanges
            });
        }

        private AggregatedMetricsResult CalculateAggregatedMetrics(List<PlayerResult> playerResults)
        {
            var allScores = playerResults.SelectMany(p => p.Scores).ToList();

            // Average decay curve (first 200 scores per player)
            var decayData = new Dictionary<int, (double sumOriginal, double sumNew, int count)>();
            foreach (var player in playerResults)
            {
                var scores = player.Scores.Take(200).ToList();
                for (int i = 0; i < scores.Count; i++)
                {
                    var weighted = scores[i];
                    var weightFactor = Math.Pow(0.965, i);
                    
                    if (!decayData.ContainsKey(i))
                    {
                        decayData[i] = (0, 0, 0);
                    }
                    
                    var current = decayData[i];
                    decayData[i] = (
                        current.sumOriginal + weighted.CurrentPP * weightFactor,
                        current.sumNew + weighted.NewPP * weightFactor,
                        current.count + 1
                    );
                }
            }

            var aggregatedDecay = new
            {
                labels = decayData.Keys.OrderBy(k => k).Select(k => k + 1).ToList(),
                originalWeighted = decayData.OrderBy(kv => kv.Key).Select(kv => kv.Value.sumOriginal / kv.Value.count).ToList(),
                newWeighted = decayData.OrderBy(kv => kv.Key).Select(kv => kv.Value.sumNew / kv.Value.count).ToList()
            };

            // Component balance (average across score ranges)
            var componentBalance = new
            {
                labels = new[] { "#1-50", "#51-100", "#101-150", "#151-200" },
                acc = new[] { 0.0, 0.0, 0.0, 0.0 },
                tech = new[] { 0.0, 0.0, 0.0, 0.0 },
                pass = new[] { 0.0, 0.0, 0.0, 0.0 }
            };

            for (int bucket = 0; bucket < 4; bucket++)
            {
                int start = bucket * 50;
                int end = start + 50;
                double totalAcc = 0, totalTech = 0, totalPass = 0, total = 0;
                int count = 0;

                foreach (var player in playerResults)
                {
                    var bucketScores = player.Scores.Skip(start).Take(50).ToList();
                    if (bucketScores.Any())
                    {
                        totalAcc += bucketScores.Sum(s => s.NewAccPP);
                        totalTech += bucketScores.Sum(s => s.NewTechPP);
                        totalPass += bucketScores.Sum(s => s.NewPassPP);
                        count++;
                    }
                }

                total = totalAcc + totalTech + totalPass;
                if (total > 0)
                {
                    componentBalance.acc[bucket] = (totalAcc / total) * 100;
                    componentBalance.tech[bucket] = (totalTech / total) * 100;
                    componentBalance.pass[bucket] = (totalPass / total) * 100;
                }
            }

            // Accuracy reward (average)
            var accBuckets = new Dictionary<int, (double sum, int count)>();
            foreach (var score in allScores)
            {
                var accPercent = (int)Math.Floor(score.Accuracy * 100);
                if (!accBuckets.ContainsKey(accPercent))
                {
                    accBuckets[accPercent] = (0, 0);
                }
                var current = accBuckets[accPercent];
                accBuckets[accPercent] = (current.sum + score.NewPP, current.count + 1);
            }

            var accuracyReward = accBuckets
                .OrderBy(kv => kv.Key)
                .Select(kv => new { x = kv.Key, y = kv.Value.sum / kv.Value.count })
                .ToList();

            // Per-player changes
            var playerChanges = new
            {
                labels = playerResults.Select(p => p.PlayerId.Substring(0, Math.Min(8, p.PlayerId.Length))).ToList(),
                changes = playerResults.Select(p => p.TotalPP - p.OriginalTotalPP).ToList()
            };

            // Calculate average metrics
            var avgPPChange = playerResults.Average(p => p.TotalPP - p.OriginalTotalPP);
            var avgAccPPChange = playerResults.Average(p => p.TotalAccPP - p.OriginalAccPP);
            var avgTechPPChange = playerResults.Average(p => p.TotalTechPP - p.OriginalTechPP);
            var avgPassPPChange = playerResults.Average(p => p.TotalPassPP - p.OriginalPassPP);

            // Calculate balance metrics for each player and average them
            var avgAccPercent = 0.0;
            var avgTechPercent = 0.0;
            var avgPassPercent = 0.0;
            var avgDecayCV = 0.0;
            var avgOutliers = 0.0;
            var avgOutlierPercent = 0.0;

            foreach (var player in playerResults)
            {
                var totalAcc = player.Scores.Sum(s => s.NewAccPP);
                var totalTech = player.Scores.Sum(s => s.NewTechPP);
                var totalPass = player.Scores.Sum(s => s.NewPassPP);
                var total = totalAcc + totalTech + totalPass;

                if (total > 0)
                {
                    avgAccPercent += (totalAcc / total) * 100;
                    avgTechPercent += (totalTech / total) * 100;
                    avgPassPercent += (totalPass / total) * 100;
                }

                // Decay CV
                var weightedPPs = player.Scores.Take(100).Select((s, i) => s.NewPP * Math.Pow(0.965, i)).ToList();
                if (weightedPPs.Count > 1)
                {
                    var ppDrops = new List<double>();
                    for (int i = 1; i < weightedPPs.Count; i++)
                    {
                        ppDrops.Add(weightedPPs[i - 1] - weightedPPs[i]);
                    }
                    var avgDrop = ppDrops.Average();
                    var stdDrop = Math.Sqrt(ppDrops.Average(d => Math.Pow(d - avgDrop, 2)));
                    avgDecayCV += (stdDrop / avgDrop) * 100;
                }
            }

            var playerCount = playerResults.Count;
            avgAccPercent /= playerCount;
            avgTechPercent /= playerCount;
            avgPassPercent /= playerCount;
            avgDecayCV /= playerCount;

            return new AggregatedMetricsResult
            {
                Aggregated = new
                {
                    avgPPChange = avgPPChange,
                    avgAccPPChange = avgAccPPChange,
                    avgTechPPChange = avgTechPPChange,
                    avgPassPPChange = avgPassPPChange,
                    playerCount = playerCount
                },
                Metrics = new
                {
                    avgAccPercent = avgAccPercent,
                    avgTechPercent = avgTechPercent,
                    avgPassPercent = avgPassPercent,
                    avgDecayCV = avgDecayCV,
                    avgOutliers = avgOutliers,
                    avgOutlierPercent = avgOutlierPercent
                },
                AggregatedDecay = aggregatedDecay,
                ComponentBalance = componentBalance,
                AccuracyReward = accuracyReward,
                PlayerChanges = playerChanges
            };
        }

        private float CalculateWeightedTotal(List<float> ppValues)
        {
            float total = 0;
            for (int i = 0; i < ppValues.Count; i++)
            {
                float weight = MathF.Pow(0.965f, i);
                total += ppValues[i] * weight;
            }
            return total;
        }

        private (float, float, float, float, float) CalculatePP(
            float accuracy,
            string modifiers,
            ModifiersRating modifiersRating,
            float accRating,
            float passRating,
            float techRating,
            float predictedAcc,
            PPParameters parameters)
        {
            if (accuracy <= 0 || accuracy > 1) return (0, 0, 0, 0, 0);

            float mp = ModifiersMap.RankedMap().GetTotalMultiplier(modifiers, modifiersRating == null);

            float rawPP = 0; float fullPP = 0; float passPP = 0; float accPP = 0; float techPP = 0; float increase = 0;
            if (!modifiers.Contains("NF"))
            {
                (passPP, accPP, techPP) = GetPpCustom(accuracy, accRating, passRating, techRating, parameters);

                rawPP = InflateCustom(passPP + accPP + techPP, parameters);
                if (modifiersRating != null)
                {
                    var modifiersMap = modifiersRating.ToDictionary<float>();
                    foreach (var modifier in modifiers.ToUpper().Split(","))
                    {
                        if (modifiersMap.ContainsKey(modifier + "AccRating"))
                        {
                            accRating = modifiersMap[modifier + "AccRating"];
                            passRating = modifiersMap[modifier + "PassRating"];
                            techRating = modifiersMap[modifier + "TechRating"];

                            break;
                        }
                    }
                }
                (passPP, accPP, techPP) = GetPpCustom(accuracy, accRating * mp, passRating * mp, techRating * mp, parameters);
                fullPP = InflateCustom(passPP + accPP + techPP, parameters);
                if (passPP + accPP + techPP > 0)
                {
                    increase = fullPP / (passPP + accPP + techPP);
                }
            }

            if (float.IsInfinity(rawPP) || float.IsNaN(rawPP) || float.IsNegativeInfinity(rawPP))
            {
                rawPP = 0;
            }

            if (float.IsInfinity(fullPP) || float.IsNaN(fullPP) || float.IsNegativeInfinity(fullPP))
            {
                fullPP = 0;
            }

            return (fullPP, fullPP - rawPP, passPP * increase, accPP * increase, techPP * increase);
        }

        private (float, float, float) GetPpCustom(float accuracy, float accRating, float passRating, float techRating, PPParameters parameters)
        {
            float passPP = parameters.PassMultiplier * MathF.Exp(MathF.Pow(passRating, 1 / parameters.PassExponent)) - parameters.PassOffset;
            if (float.IsInfinity(passPP) || float.IsNaN(passPP) || float.IsNegativeInfinity(passPP) || passPP < 0)
            {
                passPP = 0;
            }
            float accPP = CurveCustom(accuracy, parameters) * accRating * parameters.AccMultiplier;
            float techPP = MathF.Exp(parameters.TechAccExponent * accuracy) * parameters.TechAccMultiplier * techRating;

            return (passPP, accPP, techPP);
        }

        private float InflateCustom(float peepee, PPParameters parameters)
        {
            return parameters.InflateMultiplier * MathF.Pow(peepee, parameters.InflateExponent) / MathF.Pow(parameters.InflateMultiplier, parameters.InflateExponent);
        }

        private float CurveCustom(float acc, PPParameters parameters)
        {
            var pointList = parameters.UseAlternateCurve ? parameters.PointList2 : parameters.PointList1;

            int i = 0;
            for (; i < pointList.Count; i++)
            {
                if (pointList[i].Item1 <= acc)
                {
                    break;
                }
            }

            if (i == 0)
            {
                i = 1;
            }

            double middle_dis = (acc - pointList[i - 1].Item1) / (pointList[i].Item1 - pointList[i - 1].Item1);
            return (float)(pointList[i - 1].Item2 + middle_dis * (pointList[i].Item2 - pointList[i - 1].Item2));
        }

        public class ScoreData
        {
            public string PlayerId { get; set; }
            public int Id { get; set; }
            public string LeaderboardName { get; set; }
            public float Accuracy { get; set; }
            public string Modifiers { get; set; }
            public float AccRating { get; set; }
            public float PassRating { get; set; }
            public float TechRating { get; set; }
            public float PredictedAcc { get; set; }
            public float CurrentPP { get; set; }
            public float CurrentAccPP { get; set; }
            public float CurrentTechPP { get; set; }
            public float CurrentPassPP { get; set; }
            public float Weight { get; set; }
            public int Timepost { get; set; }
        }

        public class RecalculatedScoreData
        {
            public int Id { get; set; }
            public float Accuracy { get; set; }
            public float CurrentPP { get; set; }
            public float CurrentAccPP { get; set; }
            public float CurrentTechPP { get; set; }
            public float CurrentPassPP { get; set; }
            public float NewPP { get; set; }
            public float NewAccPP { get; set; }
            public float NewTechPP { get; set; }
            public float NewPassPP { get; set; }
            public float Weight { get; set; }
            public int Timepost { get; set; }
        }

        public class PlayerResult
        {
            public string PlayerId { get; set; }
            public List<RecalculatedScoreData> Scores { get; set; }
            public float TotalPP { get; set; }
            public float TotalAccPP { get; set; }
            public float TotalTechPP { get; set; }
            public float TotalPassPP { get; set; }
            public float OriginalTotalPP { get; set; }
            public float OriginalAccPP { get; set; }
            public float OriginalTechPP { get; set; }
            public float OriginalPassPP { get; set; }
        }

        public class RecalculateBatchRequest
        {
            public List<string> PlayerIds { get; set; }
            public PPParameters Parameters { get; set; }
        }

        public class AggregatedMetricsResult
        {
            public object Aggregated { get; set; }
            public object Metrics { get; set; }
            public object AggregatedDecay { get; set; }
            public object ComponentBalance { get; set; }
            public object AccuracyReward { get; set; }
            public object PlayerChanges { get; set; }
        }

        public class PPParameters
        {
            public float PassMultiplier { get; set; } = 15.2f;
            public float PassExponent { get; set; } = 2.62f;
            public float PassOffset { get; set; } = 30f;
            public float AccMultiplier { get; set; } = 34f;
            public float TechAccExponent { get; set; } = 1.9f;
            public float TechAccMultiplier { get; set; } = 1.08f;
            public float InflateMultiplier { get; set; } = 650f;
            public float InflateExponent { get; set; } = 1.3f;
            public bool UseAlternateCurve { get; set; } = false;

            public List<(double, double)> PointList1 { get; set; } = new List<(double, double)>
            {
                (1.0, 7.424), (0.999, 6.241), (0.9975, 5.158), (0.995, 4.010), (0.9925, 3.241),
                (0.99, 2.700), (0.9875, 2.303), (0.985, 2.007), (0.9825, 1.786), (0.98, 1.618),
                (0.9775, 1.490), (0.975, 1.392), (0.9725, 1.315), (0.97, 1.256), (0.965, 1.167),
                (0.96, 1.101), (0.955, 1.047), (0.95, 1.000), (0.94, 0.919), (0.93, 0.847),
                (0.92, 0.786), (0.91, 0.734), (0.9, 0.692), (0.875, 0.606), (0.85, 0.537),
                (0.825, 0.480), (0.8, 0.429), (0.75, 0.345), (0.7, 0.286), (0.65, 0.246),
                (0.6, 0.217), (0.0, 0.000)
            };

            public List<(double, double)> PointList2 { get; set; } = new List<(double, double)>
            {
                (1.0, 7.424), (0.999, 6.241), (0.9975, 5.158), (0.995, 4.010), (0.9925, 3.241),
                (0.99, 2.700), (0.9875, 2.303), (0.985, 2.007), (0.9825, 1.786), (0.98, 1.618),
                (0.9775, 1.490), (0.975, 1.392), (0.9725, 1.315), (0.97, 1.256), (0.965, 1.167),
                (0.96, 1.094), (0.955, 1.039), (0.95, 1.000), (0.94, 0.931), (0.93, 0.867),
                (0.92, 0.813), (0.91, 0.768), (0.9, 0.729), (0.875, 0.650), (0.85, 0.581),
                (0.825, 0.522), (0.8, 0.473), (0.75, 0.404), (0.7, 0.345), (0.65, 0.296),
                (0.6, 0.256), (0.0, 0.000)
            };
        }
    }
}
