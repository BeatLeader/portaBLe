using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Refresh;
using portaBLe.Services;
using System.Text.Json;

namespace portaBLe.Pages
{
    public class PlayersBalancingGraphsModel : BasePageModel
    {
        public PlayersBalancingGraphsModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        [BindProperty(SupportsGet = true)]
        public string PlayerId { get; set; }

        public Player Player { get; set; }
        public List<ScoreData> Scores { get; set; }

        public async Task<IActionResult> OnGetAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);

            if (string.IsNullOrEmpty(PlayerId))
            {
                return Page();
            }

            using var context = (Services.DynamicDbContext)GetDbContext();

            Player = await context.Players.FirstOrDefaultAsync(p => p.Id == PlayerId);

            if (Player == null)
            {
                ModelState.AddModelError(string.Empty, "Player not found");
                return Page();
            }

            Scores = await context.Scores
                .Include(s => s.Leaderboard)
                .ThenInclude(l => l.ModifiersRating)
                .Where(s => s.PlayerId == PlayerId)
                .OrderByDescending(s => s.Pp)
                .Select(s => new ScoreData
                {
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

            return Page();
        }

        public async Task<IActionResult> OnPostRecalculateAsync([FromBody] RecalculateRequest request, string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            
            using var context = (Services.DynamicDbContext)GetDbContext();

            var scores = await context.Scores
                .Include(s => s.Leaderboard)
                .ThenInclude(l => l.ModifiersRating)
                .Where(s => s.PlayerId == request.PlayerId)
                .OrderByDescending(s => s.Pp)
                .ToListAsync();

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
                    LeaderboardName = score.Leaderboard.Name,
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

            // Calculate weighted totals
            var totalPP = CalculateWeightedTotal(recalculatedScores.Select(s => s.NewPP).ToList());
            var totalAccPP = CalculateWeightedTotal(recalculatedScores.Select(s => s.NewAccPP).ToList());
            var totalTechPP = CalculateWeightedTotal(recalculatedScores.Select(s => s.NewTechPP).ToList());
            var totalPassPP = CalculateWeightedTotal(recalculatedScores.Select(s => s.NewPassPP).ToList());

            return new JsonResult(new
            {
                scores = recalculatedScores,
                totals = new
                {
                    pp = totalPP,
                    accPP = totalAccPP,
                    techPP = totalTechPP,
                    passPP = totalPassPP,
                    originalPP = scores.Sum(s => s.Pp * s.Weight),
                    originalAccPP = scores.Sum(s => s.AccPP * s.Weight),
                    originalTechPP = scores.Sum(s => s.TechPP * s.Weight),
                    originalPassPP = scores.Sum(s => s.PassPP * s.Weight)
                }
            });
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
            public string LeaderboardName { get; set; }
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

        public class RecalculateRequest
        {
            public string PlayerId { get; set; }
            public PPParameters Parameters { get; set; }
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
