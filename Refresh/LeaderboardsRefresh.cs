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

            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "OutlierCount", "INTEGER");

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

            var updateList = updates.ToList();

            // Worth: Megametric75 relative to what maps of its stars average (as BalanceTuner.Worth, WorthReference)
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "Worth", "REAL");
            var stars = await dbContext.Leaderboards.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Stars);
            float StarsOf(Leaderboard u) => stars.TryGetValue(u.Id, out var s) ? s : 0;
            int StarRange(Leaderboard u) => Math.Clamp((int)MathF.Floor(StarsOf(u)), 0, 13);
            var usedRanges = updateList.Where(u => u.Megametric75 > 0).GroupBy(StarRange)
                .Where(g => g.Count() >= 5 && g.Average(u => (double)u.Megametric75) > 0)
                .Select(g => g.Key)
                .ToHashSet();
            var points = updateList.Where(u => u.Megametric75 > 0 && usedRanges.Contains(StarRange(u)))
                .Select(u => ((double)StarsOf(u), (double)u.Megametric75))
                .ToList();
            foreach (var u in updateList)
            {
                double reference = u.Megametric75 > 0 && usedRanges.Contains(StarRange(u)) ? BalanceTuner.WorthReference(points, StarsOf(u)) : 0;
                u.Worth = reference > 0 ? (float)(u.Megametric75 / reference - 1) : null;
            }

            Console.WriteLine($"Processed {updates.Count} leaderboards, writing to database...");
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
                    c.Megametric125,
                    c.Worth
                });

            dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
            Console.WriteLine($"Complete! Total time: {Program.Stopwatch.ElapsedMilliseconds / 1000} seconds");

            await RefreshWorthByRank(dbContext);
            await DetermineTags(dbContext);
        }

        // Rank ranges of Leaderboard.WorthRank*: up to each rank, from the previous one
        public static readonly int[] WorthRanks = { 20, 100, 1000, 3000, 10000 };
        public const int WorthRankMinScores = 5;
        private const int WorthRankMinPlays = 75;   // as Megametric75
        private const int WorthRankMinNeighbours = 5;   // other maps with a value within WorthRankNeighbourStars stars
        private const double WorthRankNeighbourStars = 0.5;

        // Leaderboard.WorthRank20 ... WorthRank10000 only (Refresh runs it; also on its own, it changes nothing else)
        public static async Task RefreshWorthByRank(AppContext dbContext)
        {
            Console.WriteLine("Recalculating worth by player rank");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            foreach (int rank in WorthRanks)
            {
                await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", $"WorthRank{rank}", "REAL");
            }

            var scores = await dbContext.Scores
                .Where(s => s.Player.TopPp > 0 && s.Player.RankedPlayCount > WorthRankMinPlays && s.Player.Rank > 0 && s.Player.Rank <= 10000)
                .Select(s => new { s.LeaderboardId, s.Pp, s.Player.TopPp, s.Player.Rank })
                .ToListAsync();
            var stars = await dbContext.Leaderboards.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Stars);

            // Per map and rank range: sum of pp / top pp, and how many scores
            var sums = new Dictionary<string, (double Sum, int Count)[]>();
            foreach (var s in scores)
            {
                int range = Array.FindIndex(WorthRanks, r => s.Rank <= r);
                if (range < 0 || s.LeaderboardId == null || !stars.ContainsKey(s.LeaderboardId)) continue;
                if (!sums.TryGetValue(s.LeaderboardId, out var ranges)) sums[s.LeaderboardId] = ranges = new (double, int)[WorthRanks.Length];
                ranges[range] = (ranges[range].Sum + s.Pp / s.TopPp, ranges[range].Count + 1);
            }

            var worth = stars.Keys.ToDictionary(id => id, _ => new float?[WorthRanks.Length]);
            for (int range = 0; range < WorthRanks.Length; range++)
            {
                var means = sums
                    .Where(m => m.Value[range].Count >= WorthRankMinScores)
                    .ToDictionary(m => m.Key, m => m.Value[range].Sum / m.Value[range].Count);
                var points = means.Select(m => ((double)stars[m.Key], m.Value)).ToList();
                Parallel.ForEach(means, m =>
                {
                    // Without enough other maps of its stars (the highest star maps), the reference is mostly the map itself
                    double mapStars = stars[m.Key];
                    if (points.Count(p => Math.Abs(p.Item1 - mapStars) <= WorthRankNeighbourStars) - 1 < WorthRankMinNeighbours) return;
                    double reference = BalanceTuner.WorthReference(points, mapStars);
                    if (reference > 0) worth[m.Key][range] = (float)(m.Value / reference - 1);
                });
            }

            var updates = worth.Select(w => new Leaderboard
            {
                Id = w.Key,
                WorthRank20 = w.Value[0],
                WorthRank100 = w.Value[1],
                WorthRank1000 = w.Value[2],
                WorthRank3000 = w.Value[3],
                WorthRank10000 = w.Value[4],
            }).ToList();
            await dbContext.BulkUpdateAsync(updates, options => options.ColumnInputExpression = c =>
                new { c.WorthRank20, c.WorthRank100, c.WorthRank1000, c.WorthRank3000, c.WorthRank10000 });

            dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
            Console.WriteLine($"Worth by player rank: {scores.Count} scores of the top 10000 players");
        }

        private static async Task DetermineTags(AppContext dbContext)
        {
            Console.WriteLine("Determining Leaderboard Tags (Linear and Fitbeat)");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

            const float linearPercentThreshold = 0.4f;
            const float dodgeWallValue = 0.3f;
            const float crouchWallValue = 5f;
            const float fitbeatDensityThreshold = 0.15f;

            var leaderboards = await dbContext.Leaderboards
                .Select(l => new Leaderboard
                {
                    Id = l.Id,
                    LinearPercent = l.LinearPercent,
                    DodgeWalls = l.DodgeWalls,
                    CrouchWalls = l.CrouchWalls,
                    Duration = l.Duration
                })
                .ToListAsync();

            foreach (var leaderboard in leaderboards)
            {
                leaderboard.IsLinear = leaderboard.LinearPercent >= linearPercentThreshold;

                float wallScore = (leaderboard.DodgeWalls * dodgeWallValue) + (leaderboard.CrouchWalls * crouchWallValue);

                if (leaderboard.Duration != 0)
                {
                    float wallDensity = wallScore / leaderboard.Duration;
                    leaderboard.IsFitbeat = wallDensity >= fitbeatDensityThreshold;
                }
                else leaderboard.IsFitbeat = false;
            }

            await dbContext.BulkUpdateAsync(leaderboards, options => 
                options.ColumnInputExpression = c => new { c.IsLinear, c.IsFitbeat });

            Console.WriteLine("Tag determination completed");
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
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
            // Stars use each one's curve (a 96% score's pp), so the curves are loaded too
            var lbs = dbContext
                .Leaderboards
                .AsNoTracking()
                .Include(s => s.ModifiersRating)
                .ToList();
            foreach (var lb in lbs)
            {
                ReplayUtils.SetStars(lb);
            }

            await dbContext.BulkUpdateAsync(lbs, options => options.ColumnInputExpression = c => new { c.Stars });

            var mods = lbs.Select(lb => lb.ModifiersRating).Where(mod => mod != null).Select(mod => mod!).ToList();
            await dbContext.BulkUpdateAsync(mods, options => options.ColumnInputExpression = c => new { c.SSStars, c.SFStars, c.FSStars });
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }
    }
}
