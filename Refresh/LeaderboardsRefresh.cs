using Dasync.Collections;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;

namespace portaBLe.Refresh
{
    public class LeaderboardsRefresh
    {
        public static async Task Outliers(AppContext dbContext)
        {
            Console.WriteLine("Recalculating Outliers");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

            // Ensure OutlierCount column exists
            try
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE Leaderboards ADD COLUMN OutlierCount INTEGER DEFAULT 0");
                Console.WriteLine("OutlierCount column added successfully.");
            }
            catch
            {
                // Column already exists, continue
            }

            const int minScoresCount = 100;
            const float outlierThreshold = 8f;
            const int maxTopScoresToCheck = 10;

            // Step 1: Get all scores with PP and player info in one query
            var allScores = await dbContext.Scores
                .Where(s => s.Pp > 0)
                .Select(s => new
                {
                    s.PlayerId,
                    s.LeaderboardId,
                    s.Pp
                })
                .ToListAsync();

            Console.WriteLine($"Loaded {allScores.Count} scores with PP > 0");

            // Step 2: Group by player and identify outlier scores in top 10
            var playerScoreGroups = allScores
                .GroupBy(s => s.PlayerId)
                .Where(g => g.Count() >= minScoresCount)
                .Select(g =>
                {
                    var orderedScores = g.OrderByDescending(s => s.Pp).Take(maxTopScoresToCheck).ToList();
                    var outlierScores = new HashSet<float>();

                    // Find the cutoff point where there's a >8% drop
                    for (int i = 0; i < orderedScores.Count - 1; i++)
                    {
                        float currentPp = orderedScores[i].Pp;
                        float nextPp = orderedScores[i + 1].Pp;

                        if (nextPp > 0)
                        {
                            float ppDifference = currentPp - nextPp;
                            float percentageDifference = (ppDifference / nextPp) * 100f;

                            // If there's a >8% drop, all scores above this point are outliers
                            if (percentageDifference > outlierThreshold)
                            {
                                for (int j = 0; j <= i; j++)
                                {
                                    outlierScores.Add(orderedScores[j].Pp);
                                }
                                break;
                            }
                        }
                    }

                    return new
                    {
                        PlayerId = g.Key,
                        OutlierScores = outlierScores,
                        AllScores = g.ToList()
                    };
                })
                .ToList();

            Console.WriteLine($"Found {playerScoreGroups.Count} eligible players with {minScoresCount}+ scores");

            // Create a dictionary for fast lookup: PlayerId -> Set of outlier PP values
            var playerOutlierScores = playerScoreGroups.ToDictionary(
                p => p.PlayerId,
                p => p.OutlierScores
            );

            // Step 3: Calculate outliers per leaderboard
            var leaderboardOutliers = allScores
                .Where(s => playerOutlierScores.ContainsKey(s.PlayerId))
                .GroupBy(s => s.LeaderboardId)
                .Select(g => new
                {
                    LeaderboardId = g.Key,
                    OutlierCount = g.Count(score =>
                    {
                        if (!playerOutlierScores.TryGetValue(score.PlayerId, out var outlierScores))
                            return false;

                        // Check if this score's PP is in the player's outlier set
                        return outlierScores.Contains(score.Pp);
                    })
                })
                .ToDictionary(x => x.LeaderboardId, x => x.OutlierCount);

            Console.WriteLine($"Calculated outliers for {leaderboardOutliers.Count} leaderboards");

            // Step 4: Update leaderboards
            var leaderboards = await dbContext.Leaderboards
                .Select(l => new Leaderboard { Id = l.Id })
                .ToListAsync();

            foreach (var leaderboard in leaderboards)
            {
                leaderboard.OutlierCount = leaderboardOutliers.TryGetValue(leaderboard.Id, out var count) ? count : 0;
            }

            await dbContext.BulkUpdateAsync(leaderboards, options => options.ColumnInputExpression = c =>
                new { c.OutlierCount });

            Console.WriteLine("Outlier calculation completed");
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }

        public static async Task Refresh(AppContext dbContext) {
            Console.WriteLine("Recalculating Megametric");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

            var weights = new Dictionary<int, float>();
            for (int i = 0; i < 10000; i++)
            {
                weights[i] = MathF.Pow(0.965f, i);
            }

            float weightTreshold = MathF.Pow(0.965f, 40);

            var leaderboards = dbContext.Scores.Select(s => new {
                s.LeaderboardId,
                s.Weight,
                s.Pp,
                s.Player.TopPp,
                s.Player.RankedPlayCount,
                s.Player.Rank
            }).ToList()
            .GroupBy(s => s.LeaderboardId)
            .Select(g => new
                {
                    Average = g.Average(s => s.Weight),
                    Megametric = g.Where(s => s.TopPp != 0).Select(s => new { s.Weight, s.RankedPlayCount, s.Pp, s.TopPp }),
                    Count8 = g.Where(s => s.Weight > 0.8).Count(),
                    Count95 = g.Where(s => s.Weight > 0.95).Count(),
                    PPsum = g.Sum(s => s.Pp * s.Weight),

                    PPAverage = g.Where(s => s.RankedPlayCount >= 50 && s.TopPp != 0).Average(s => s.Pp / s.TopPp),
                    PPAverage2 = g.Where(s => s.TopPp != 0).Average(s => s.Pp / s.TopPp),
                    Count = g.Count(),
                    Top250 = g.Where(s => s.Rank < 250 && s.Weight > weightTreshold).Count(),
                    Id = g.Key,
                })
            .ToList();

            var updates = new List<Leaderboard>();

            foreach (var item in leaderboards)
            {
                var l = item.Megametric.OrderByDescending(s => s.Weight).Take((int)(item.Megametric.Count() * 0.33));
                var ll = l.Count() > 10 ? l.Average(s => s.Weight) : 0;

                var m = item.Megametric.OrderByDescending(s => s.Weight).Take((int)(item.Megametric.Count() * 0.33)).Where(s => s.RankedPlayCount > 75);
                var mm = m.Count() > 10 ? m.Average(s => s.Pp / s.TopPp * s.Weight) : 0;

                var m2 = item.Megametric.Where(s => s.RankedPlayCount > 125).OrderByDescending(s => s.Weight).Take((int)(item.Megametric.Count() * 0.33));
                var mm2 = m2.Count() > 10 ? m2.Average(s => s.Pp / s.TopPp * s.Weight) : 0;

                var m3 = item.Megametric.Where(s => s.RankedPlayCount > 75).OrderByDescending(s => s.Weight).Take((int)(item.Megametric.Count() * 0.33));
                var mm3 = m3.Count() > 10 ? m3.Average(s => s.Pp / s.TopPp * s.Weight) : 0;

                var m4 = item.Megametric.Where(s => s.RankedPlayCount > 40).OrderByDescending(s => s.Weight).Take((int)(item.Megametric.Count() * 0.33));
                var mm4 = m4.Count() > 10 ? m4.Average(s => s.Pp / s.TopPp * s.Weight) : 0;

                updates.Add(new Leaderboard {
                    Id = item.Id,
                    Count = item.Count,
                    Count80 = item.Count8,
                    Count95 = item.Count95,
                    Average = item.Average,
                    Percentile = ll,
                    Megametric = mm,
                    Megametric125 = mm2,
                    Megametric75 = mm3,
                    Megametric40 = mm4,
                    Top250 = item.Top250,
                    TotalPP = item.PPsum,
                    PPRatioFiltered = item.PPAverage,
                    PPRatioUnfiltered = item.PPAverage
                });
            }
            await dbContext.BulkUpdateAsync(updates, options => options.ColumnInputExpression = c => 
                new { 
                    c.Count, 
                    c.Count80, 
                    c.Count95, 
                    c.Average,
                    c.Top250,
                    c.TotalPP,
                    c.Percentile,
                    c.PPRatioFiltered,
                    c.PPRatioUnfiltered,
                    c.Megametric,
                    c.Megametric40,
                    c.Megametric75,
                    c.Megametric125
                });
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }

        public static async Task RefreshStars(AppContext dbContext)
        {
            Console.WriteLine("Recalculating Star Ratings");
            var lbs = dbContext
                .Leaderboards
                .AsNoTracking()
                .Select(s => new Leaderboard
                {
                    Id = s.Id,
                    AccRating = s.AccRating,
                    PassRating = s.PassRating,
                    TechRating = s.TechRating
                })
                .ToList();
            foreach (var lb in lbs)
            {
                lb.Stars = ReplayUtils.ToStars(lb.AccRating, lb.PassRating, lb.TechRating);
            }

            await dbContext.BulkUpdateAsync(lbs, options => options.ColumnInputExpression = c => new { c.Stars });

            var mods = dbContext
                .ModifiersRating
                .AsNoTracking()
                .Select(s => new ModifiersRating
                {
                    Id = s.Id,
                    SSAccRating = s.SSAccRating,
                    SSPassRating = s.SSPassRating,
                    SSTechRating = s.SSTechRating,
                    SFAccRating = s.SFAccRating,
                    SFPassRating = s.SFPassRating,
                    SFTechRating = s.SFTechRating,
                    FSAccRating = s.FSAccRating,
                    FSPassRating = s.FSPassRating,
                    FSTechRating = s.FSTechRating
                })
                .ToList();

            foreach (var mod in mods)
            {
                mod.SSStars = ReplayUtils.ToStars(mod.SSAccRating, mod.SSPassRating, mod.SSTechRating);
                mod.SFStars = ReplayUtils.ToStars(mod.SFAccRating, mod.SFPassRating, mod.SFTechRating);
                mod.FSStars = ReplayUtils.ToStars(mod.FSAccRating, mod.FSPassRating, mod.FSTechRating);
            }

            await dbContext.BulkUpdateAsync(mods, options => options.ColumnInputExpression = c => new { c.SSStars, c.SFStars, c.FSStars });
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }
    }
}
