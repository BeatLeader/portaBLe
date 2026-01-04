using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using System.Collections.Concurrent;

namespace portaBLe.Refresh
{
    public class LeaderboardData
    {
        public string Id { get; set; }
        public string ModeName { get; set; }
        public int OutlierCount { get; set; }
        public int Count { get; set; }
        public float Megametric { get; set; }
        public float Megametric40 { get; set; }
        public float Megametric75 { get; set; }
        public float Megametric125 { get; set; }
        public float Stars { get; set; }
        public float AccRating { get; set; }
        public float PassRating { get; set; }
        public float TechRating { get; set; }
    }

    public class ScoreData
    {
        public string LeaderboardId { get; set; }
        public float Pp { get; set; }
    }

    public class PlayerData
    {
        public float Pp { get; set; }
        public float AccPp { get; set; }
        public float TechPp { get; set; }
        public float PassPp { get; set; }
    }

    public class StatsRefresh
    {
        public static async Task Refresh(AppContext dbContext)
        {
            Console.WriteLine("Calculating database statistics...");

            // Ensure Stats table exists
            try
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    @"CREATE TABLE IF NOT EXISTS Stats (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ModeName TEXT,
                        TotalOutlier INTEGER DEFAULT 0,
                        AvgOutlierPercentage REAL DEFAULT 0,
                        AvgMegametric REAL DEFAULT 0,
                        AvgMegametric40 REAL DEFAULT 0,
                        AvgMegametric75 REAL DEFAULT 0,
                        AvgMegametric125 REAL DEFAULT 0,
                        PpCount600 INTEGER DEFAULT 0,
                        PpCount700 INTEGER DEFAULT 0,
                        PpCount800 INTEGER DEFAULT 0,
                        PpCount900 INTEGER DEFAULT 0,
                        PpCount1000 INTEGER DEFAULT 0,
                        HighestStarRating REAL DEFAULT 0,
                        HighestAccRating REAL DEFAULT 0,
                        HighestPassRating REAL DEFAULT 0,
                        HighestTechRating REAL DEFAULT 0,
                        Top1PP REAL DEFAULT 0,
                        Top10PP REAL DEFAULT 0,
                        Top100PP REAL DEFAULT 0,
                        Top1000PP REAL DEFAULT 0,
                        Top2000PP REAL DEFAULT 0,
                        Top5000PP REAL DEFAULT 0,
                        Top10000PP REAL DEFAULT 0,
                        Top1AccPP REAL DEFAULT 0,
                        Top10AccPP REAL DEFAULT 0,
                        Top100AccPP REAL DEFAULT 0,
                        Top1000AccPP REAL DEFAULT 0,
                        Top2000AccPP REAL DEFAULT 0,
                        Top5000AccPP REAL DEFAULT 0,
                        Top10000AccPP REAL DEFAULT 0,
                        Top1PassPP REAL DEFAULT 0,
                        Top10PassPP REAL DEFAULT 0,
                        Top100PassPP REAL DEFAULT 0,
                        Top1000PassPP REAL DEFAULT 0,
                        Top2000PassPP REAL DEFAULT 0,
                        Top5000PassPP REAL DEFAULT 0,
                        Top10000PassPP REAL DEFAULT 0,
                        Top1TechPP REAL DEFAULT 0,
                        Top10TechPP REAL DEFAULT 0,
                        Top100TechPP REAL DEFAULT 0,
                        Top1000TechPP REAL DEFAULT 0,
                        Top2000TechPP REAL DEFAULT 0,
                        Top5000TechPP REAL DEFAULT 0,
                        Top10000TechPP REAL DEFAULT 0
                    )");
            }
            catch
            {
                // Table already exists
            }

            // Clear existing stats
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM Stats");

            // Load all data once to avoid repeated database queries
            Console.WriteLine("Loading leaderboards data...");
            var allLeaderboards = await dbContext.Leaderboards
                .Select(l => new LeaderboardData
                {
                    Id = l.Id,
                    ModeName = l.ModeName,
                    OutlierCount = l.OutlierCount,
                    Count = l.Count,
                    Megametric = l.Megametric,
                    Megametric40 = l.Megametric40,
                    Megametric75 = l.Megametric75,
                    Megametric125 = l.Megametric125,
                    Stars = l.Stars,
                    AccRating = l.AccRating,
                    PassRating = l.PassRating,
                    TechRating = l.TechRating
                })
                .ToListAsync();

            Console.WriteLine("Loading scores data...");
            var allScores = await dbContext.Scores
                .Select(s => new ScoreData { LeaderboardId = s.LeaderboardId, Pp = s.Pp })
                .ToListAsync();

            Console.WriteLine("Loading players data...");
            var allPlayers = await dbContext.Players
                .Where(p => p.Pp > 0)
                .Select(p => new PlayerData { Pp = p.Pp, AccPp = p.AccPp, TechPp = p.TechPp, PassPp = p.PassPp })
                .ToListAsync();

            // Get all characteristics
            var characteristics = allLeaderboards
                .Select(l => l.ModeName)
                .Distinct()
                .ToList();

            // Add empty string for "All" statistics
            characteristics.Insert(0, "");

            Console.WriteLine($"Calculating stats for {characteristics.Count} characteristics...");

            var statsList = new ConcurrentBag<Stats>();

            // Process characteristics in parallel
            await Parallel.ForEachAsync(characteristics,
                new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount },
                async (characteristic, ct) =>
                {
                    var characteristicName = string.IsNullOrEmpty(characteristic) ? "All" : characteristic;
                    Console.WriteLine($"Calculating stats for: {characteristicName}");

                    var stats = CalculateStatsForCharacteristic(
                        characteristic,
                        allLeaderboards,
                        allScores,
                        allPlayers);

                    statsList.Add(stats);
                    await Task.CompletedTask;
                });

            // Add all stats to context
            foreach (var stats in statsList)
            {
                dbContext.Stats.Add(stats);
            }

            await dbContext.SaveChangesAsync();
            Console.WriteLine($"Complete! Statistics calculation finished. Total time: {Program.Stopwatch.ElapsedMilliseconds / 1000} seconds");
        }

        private static Stats CalculateStatsForCharacteristic(
            string characteristic,
            List<LeaderboardData> allLeaderboards,
            List<ScoreData> allScores,
            List<PlayerData> allPlayers)
        {
            var stats = new Stats
            {
                ModeName = string.IsNullOrEmpty(characteristic) ? "" : characteristic
            };

            // Filter leaderboards by characteristic
            var leaderboards = string.IsNullOrEmpty(characteristic)
                ? allLeaderboards
                : allLeaderboards.Where(l => l.ModeName == characteristic).ToList();

            if (leaderboards.Count == 0)
            {
                return stats;
            }

            var leaderboardIds = leaderboards.Select(l => l.Id).ToHashSet();

            // Outlier metrics
            var outlierLeaderboards = leaderboards.Where(l => l.OutlierCount > 0).ToList();
            stats.TotalOutlier = outlierLeaderboards.Sum(l => l.OutlierCount);
            stats.AvgOutlierPercentage = outlierLeaderboards.Any()
                ? (float)outlierLeaderboards.Average(l => l.Count > 0 ? (float)l.OutlierCount / l.Count * 100 : 0)
                : 0;

            // Megametric averages (top 50 by each metric)
            var topMegametric = leaderboards.OrderByDescending(l => l.Megametric).Take(50).ToList();
            stats.AvgMegametric = topMegametric.Any() ? topMegametric.Average(l => l.Megametric) : 0;

            var topMegametric125 = leaderboards.OrderByDescending(l => l.Megametric125).Take(50).ToList();
            stats.AvgMegametric125 = topMegametric125.Any() ? topMegametric125.Average(l => l.Megametric125) : 0;

            var topMegametric75 = leaderboards.OrderByDescending(l => l.Megametric75).Take(50).ToList();
            stats.AvgMegametric75 = topMegametric75.Any() ? topMegametric75.Average(l => l.Megametric75) : 0;

            var topMegametric40 = leaderboards.OrderByDescending(l => l.Megametric40).Take(50).ToList();
            stats.AvgMegametric40 = topMegametric40.Any() ? topMegametric40.Average(l => l.Megametric40) : 0;

            // Score counts - filter scores for this characteristic's leaderboards
            var characteristicScores = allScores.Where(s => leaderboardIds.Contains(s.LeaderboardId)).ToList();

            // Use single pass for counting multiple thresholds
            foreach (var score in characteristicScores)
            {
                float pp = score.Pp;
                if (pp >= 1000) stats.PpCount1000++;
                if (pp >= 900) stats.PpCount900++;
                if (pp >= 800) stats.PpCount800++;
                if (pp >= 700) stats.PpCount700++;
                if (pp >= 600) stats.PpCount600++;
            }

            // Highest ratings
            stats.HighestStarRating = leaderboards.Max(l => l.Stars);
            stats.HighestAccRating = leaderboards.Max(l => l.AccRating);
            stats.HighestPassRating = leaderboards.Max(l => l.PassRating);
            stats.HighestTechRating = leaderboards.Max(l => l.TechRating);

            // Player rankings - use allPlayers for global stats
            // (All characteristics use the same global player pool)
            SetPlayerRankingStats(stats, allPlayers);

            return stats;
        }

        private static void SetPlayerRankingStats(Stats stats, List<PlayerData> allPlayers)
        {
            // Sort once for PP
            var playersByPp = allPlayers.OrderByDescending(p => p.Pp).ToList();
            stats.Top1PP = GetAtOrDefault(playersByPp, 0, p => p.Pp);
            stats.Top10PP = GetAtOrDefault(playersByPp, 9, p => p.Pp);
            stats.Top100PP = GetAtOrDefault(playersByPp, 99, p => p.Pp);
            stats.Top1000PP = GetAtOrDefault(playersByPp, 999, p => p.Pp);
            stats.Top2000PP = GetAtOrDefault(playersByPp, 1999, p => p.Pp);
            stats.Top5000PP = GetAtOrDefault(playersByPp, 4999, p => p.Pp);
            stats.Top10000PP = GetAtOrDefault(playersByPp, 9999, p => p.Pp);

            // Sort once for AccPP
            var playersByAccPp = allPlayers.OrderByDescending(p => p.AccPp).ToList();
            stats.Top1AccPP = GetAtOrDefault(playersByAccPp, 0, p => p.AccPp);
            stats.Top10AccPP = GetAtOrDefault(playersByAccPp, 9, p => p.AccPp);
            stats.Top100AccPP = GetAtOrDefault(playersByAccPp, 99, p => p.AccPp);
            stats.Top1000AccPP = GetAtOrDefault(playersByAccPp, 999, p => p.AccPp);
            stats.Top2000AccPP = GetAtOrDefault(playersByAccPp, 1999, p => p.AccPp);
            stats.Top5000AccPP = GetAtOrDefault(playersByAccPp, 4999, p => p.AccPp);
            stats.Top10000AccPP = GetAtOrDefault(playersByAccPp, 9999, p => p.AccPp);

            // Sort once for TechPP
            var playersByTechPp = allPlayers.OrderByDescending(p => p.TechPp).ToList();
            stats.Top1TechPP = GetAtOrDefault(playersByTechPp, 0, p => p.TechPp);
            stats.Top10TechPP = GetAtOrDefault(playersByTechPp, 9, p => p.TechPp);
            stats.Top100TechPP = GetAtOrDefault(playersByTechPp, 99, p => p.TechPp);
            stats.Top1000TechPP = GetAtOrDefault(playersByTechPp, 999, p => p.TechPp);
            stats.Top2000TechPP = GetAtOrDefault(playersByTechPp, 1999, p => p.TechPp);
            stats.Top5000TechPP = GetAtOrDefault(playersByTechPp, 4999, p => p.TechPp);
            stats.Top10000TechPP = GetAtOrDefault(playersByTechPp, 9999, p => p.TechPp);

            // Sort once for PassPP
            var playersByPassPp = allPlayers.OrderByDescending(p => p.PassPp).ToList();
            stats.Top1PassPP = GetAtOrDefault(playersByPassPp, 0, p => p.PassPp);
            stats.Top10PassPP = GetAtOrDefault(playersByPassPp, 9, p => p.PassPp);
            stats.Top100PassPP = GetAtOrDefault(playersByPassPp, 99, p => p.PassPp);
            stats.Top1000PassPP = GetAtOrDefault(playersByPassPp, 999, p => p.PassPp);
            stats.Top2000PassPP = GetAtOrDefault(playersByPassPp, 1999, p => p.PassPp);
            stats.Top5000PassPP = GetAtOrDefault(playersByPassPp, 4999, p => p.PassPp);
            stats.Top10000PassPP = GetAtOrDefault(playersByPassPp, 9999, p => p.PassPp);
        }

        private static float GetAtOrDefault(List<PlayerData> list, int index, Func<PlayerData, float> selector)
        {
            return index < list.Count ? selector(list[index]) : 0;
        }
    }
}
