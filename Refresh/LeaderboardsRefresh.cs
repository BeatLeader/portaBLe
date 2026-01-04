using Dasync.Collections;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using System.Collections.Concurrent;

namespace portaBLe.Refresh
{
    public class MegametricData
    {
        public float Weight { get; set; }
        public int RankedPlayCount { get; set; }
        public float Pp { get; set; }
        public float TopPp { get; set; }
    }

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

        public static async Task Refresh(AppContext dbContext)
        {
            Console.WriteLine("Recalculating Megametric");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

            // Pre-calculate weight threshold
            float weightThreshold = MathF.Pow(0.965f, 40);

            // Load all leaderboard IDs first for batch processing
            var leaderboardIds = await dbContext.Leaderboards
                .Select(lb => lb.Id)
                .ToListAsync();

            Console.WriteLine($"Processing {leaderboardIds.Count} leaderboards");

            // Load all scores with necessary data in a single query
            var allScores = await dbContext.Scores
                .Select(s => new
                {
                    s.LeaderboardId,
                    s.Weight,
                    s.Pp,
                    s.Player.TopPp,
                    s.Player.RankedPlayCount,
                    s.Player.Rank
                })
                .ToListAsync();

            Console.WriteLine($"Loaded {allScores.Count} scores");

            // Group by leaderboard in memory (more efficient than database grouping)
            var leaderboardGroups = allScores
                .GroupBy(s => s.LeaderboardId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var updates = new ConcurrentBag<Leaderboard>();

            // Process leaderboards in parallel
            await Parallel.ForEachAsync(leaderboardIds,
                new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount },
                async (leaderboardId, ct) =>
                {
                    if (!leaderboardGroups.TryGetValue(leaderboardId, out var scores))
                    {
                        // Leaderboard has no scores
                        updates.Add(new Leaderboard
                        {
                            Id = leaderboardId,
                            Count = 0,
                            Count80 = 0,
                            Count95 = 0,
                            Average = 0,
                            Percentile = 0,
                            Megametric = 0,
                            Megametric125 = 0,
                            Megametric75 = 0,
                            Megametric40 = 0,
                            Top250 = 0,
                            TotalPP = 0,
                            PPRatioFiltered = 0,
                            PPRatioUnfiltered = 0
                        });
                        await Task.CompletedTask;
                        return;
                    }

                    // Pre-filter megametric data
                    var megametricData = scores
                        .Where(s => s.TopPp != 0)
                        .Select(s => new MegametricData { Weight = s.Weight, RankedPlayCount = s.RankedPlayCount, Pp = s.Pp, TopPp = s.TopPp })
                        .ToList();

                    // Calculate basic stats
                    int count = scores.Count;
                    float average = scores.Average(s => s.Weight);
                    int count80 = scores.Count(s => s.Weight > 0.8f);
                    int count95 = scores.Count(s => s.Weight > 0.95f);
                    float ppSum = scores.Sum(s => s.Pp * s.Weight);
                    int top250 = scores.Count(s => s.Rank < 250 && s.Weight > weightThreshold);

                    // Calculate PP ratios
                    var filteredScores = scores.Where(s => s.RankedPlayCount >= 50 && s.TopPp != 0).ToList();
                    float ppRatioFiltered = filteredScores.Count > 0
                        ? filteredScores.Average(s => s.Pp / s.TopPp)
                        : 0;

                    var unfilteredScores = scores.Where(s => s.TopPp != 0).ToList();
                    float ppRatioUnfiltered = unfilteredScores.Count > 0
                        ? unfilteredScores.Average(s => s.Pp / s.TopPp)
                        : 0;

                    // Calculate percentile (top 33% by weight)
                    int topCount = (int)(megametricData.Count * 0.33);
                    var topByWeight = megametricData.OrderByDescending(s => s.Weight).Take(topCount).ToList();
                    float percentile = topByWeight.Count > 10 ? topByWeight.Average(s => s.Weight) : 0;

                    // Calculate megametric variants
                    float megametric = CalculateMegametric(megametricData, topCount, 75);
                    float megametric125 = CalculateMegametric(megametricData, topCount, 125);
                    float megametric75 = CalculateMegametric(megametricData, topCount, 75);
                    float megametric40 = CalculateMegametric(megametricData, topCount, 40);

                    updates.Add(new Leaderboard
                    {
                        Id = leaderboardId,
                        Count = count,
                        Count80 = count80,
                        Count95 = count95,
                        Average = average,
                        Percentile = percentile,
                        Megametric = megametric,
                        Megametric125 = megametric125,
                        Megametric75 = megametric75,
                        Megametric40 = megametric40,
                        Top250 = top250,
                        TotalPP = ppSum,
                        PPRatioFiltered = ppRatioFiltered,
                        PPRatioUnfiltered = ppRatioUnfiltered
                    });

                    await Task.CompletedTask;
                });

            Console.WriteLine($"Processed {updates.Count} leaderboards, writing to database...");

            var updateList = updates.ToList();
            await dbContext.BulkUpdateAsync(updateList, options => options.ColumnInputExpression = c =>
                new
                {
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

            dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
            Console.WriteLine($"Complete! Total time: {Program.Stopwatch.ElapsedMilliseconds / 1000} seconds");
        }

        private static float CalculateMegametric(
            List<MegametricData> megametricData,
            int topCount,
            int minRankedPlayCount)
        {
            var filtered = megametricData
                .Where(s => s.RankedPlayCount > minRankedPlayCount)
                .OrderByDescending(s => s.Weight)
                .Take(topCount)
                .ToList();

            return filtered.Count > 10
                ? filtered.Average(s => s.Pp / s.TopPp * s.Weight)
                : 0;
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
