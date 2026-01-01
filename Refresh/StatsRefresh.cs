using Microsoft.EntityFrameworkCore;
using portaBLe.DB;

namespace portaBLe.Refresh
{
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

            // Get all characteristics
            var characteristics = await dbContext.Leaderboards
                .Select(l => l.ModeName)
                .Distinct()
                .ToListAsync();

            // Add empty string for "All" statistics
            characteristics.Insert(0, "");

            foreach (var characteristic in characteristics)
            {
                Console.WriteLine($"Calculating stats for characteristic: {(string.IsNullOrEmpty(characteristic) ? "All" : characteristic)}");
                var stats = await CalculateStats(dbContext, characteristic);
                dbContext.Stats.Add(stats);
            }

            await dbContext.SaveChangesAsync();
            Console.WriteLine("Statistics calculation complete!");
        }

        private static async Task<Stats> CalculateStats(AppContext dbContext, string characteristic)
        {
            var stats = new Stats
            {
                ModeName = string.IsNullOrEmpty(characteristic) ? "" : characteristic
            };

            // Filter leaderboards by characteristic if specified
            var leaderboardQuery = dbContext.Leaderboards.AsQueryable();
            if (!string.IsNullOrEmpty(characteristic))
            {
                leaderboardQuery = leaderboardQuery.Where(l => l.ModeName == characteristic);
            }

            var leaderboards = await leaderboardQuery.ToListAsync();
            var leaderboardIds = leaderboards.Select(l => l.Id).ToHashSet();

            // Outlier metrics
            var outlierLeaderboards = leaderboards.Where(l => l.OutlierCount > 0).ToList();
            stats.TotalOutlier = outlierLeaderboards.Sum(l => l.OutlierCount);
            stats.AvgOutlierPercentage = outlierLeaderboards.Any()
                ? outlierLeaderboards.Average(l => l.Count > 0 ? (float)l.OutlierCount / l.Count * 100 : 0)
                : 0;

            // Megametric averages (only above 0.5)
            var leaderboardsAbove05 = leaderboards.Where(l => l.Megametric > 0.5f).ToList();
            stats.AvgMegametric = leaderboardsAbove05.Any() ? leaderboardsAbove05.Average(l => l.Megametric) : 0;

            var leaderboardsAbove05_125 = leaderboards.Where(l => l.Megametric125 > 0.5f).ToList();
            stats.AvgMegametric125 = leaderboardsAbove05_125.Any() ? leaderboardsAbove05_125.Average(l => l.Megametric125) : 0;

            var leaderboardsAbove05_75 = leaderboards.Where(l => l.Megametric75 > 0.5f).ToList();
            stats.AvgMegametric75 = leaderboardsAbove05_75.Any() ? leaderboardsAbove05_75.Average(l => l.Megametric75) : 0;

            var leaderboardsAbove05_40 = leaderboards.Where(l => l.Megametric40 > 0.5f).ToList();
            stats.AvgMegametric40 = leaderboardsAbove05_40.Any() ? leaderboardsAbove05_40.Average(l => l.Megametric40) : 0;

            // Score counts where unweighted PP is above thresholds
            var scoresQuery = dbContext.Scores.Where(s => leaderboardIds.Contains(s.LeaderboardId));
            var allScores = await scoresQuery.Select(s => new { s.Pp }).ToListAsync();

            foreach (var score in allScores)
            {
                if (score.Pp >= 1000) stats.PpCount1000++;
                if (score.Pp >= 900) stats.PpCount900++;
                if (score.Pp >= 800) stats.PpCount800++;
                if (score.Pp >= 700) stats.PpCount700++;
                if (score.Pp >= 600) stats.PpCount600++;
            }

            // Highest ratings
            stats.HighestStarRating = leaderboards.Any() ? leaderboards.Max(l => l.Stars) : 0;
            stats.HighestAccRating = leaderboards.Any() ? leaderboards.Max(l => l.AccRating) : 0;
            stats.HighestPassRating = leaderboards.Any() ? leaderboards.Max(l => l.PassRating) : 0;
            stats.HighestTechRating = leaderboards.Any() ? leaderboards.Max(l => l.TechRating) : 0;

            // Player averages by rank range
            var players = await dbContext.Players
                .Where(p => p.Pp > 0)
                .ToListAsync();

            // --------------------
            // PP
            // --------------------
            players = players.OrderByDescending(p => p.Pp).ToList();

            stats.Top1PP = GetAtOrDefault(players, 0, p => p.Pp);
            stats.Top10PP = GetAtOrDefault(players, 9, p => p.Pp);
            stats.Top100PP = GetAtOrDefault(players, 99, p => p.Pp);
            stats.Top1000PP = GetAtOrDefault(players, 999, p => p.Pp);
            stats.Top2000PP = GetAtOrDefault(players, 1999, p => p.Pp);
            stats.Top5000PP = GetAtOrDefault(players, 4999, p => p.Pp);
            stats.Top10000PP = GetAtOrDefault(players, 9999, p => p.Pp);

            // --------------------
            // Accuracy PP
            // --------------------
            players = players.OrderByDescending(p => p.AccPp).ToList();

            stats.Top1AccPP = GetAtOrDefault(players, 0, p => p.AccPp);
            stats.Top10AccPP = GetAtOrDefault(players, 9, p => p.AccPp);
            stats.Top100AccPP = GetAtOrDefault(players, 99, p => p.AccPp);
            stats.Top1000AccPP = GetAtOrDefault(players, 999, p => p.AccPp);
            stats.Top2000AccPP = GetAtOrDefault(players, 1999, p => p.AccPp);
            stats.Top5000AccPP = GetAtOrDefault(players, 4999, p => p.AccPp);
            stats.Top10000AccPP = GetAtOrDefault(players, 9999, p => p.AccPp);

            // --------------------
            // Tech PP
            // --------------------
            players = players.OrderByDescending(p => p.TechPp).ToList();

            stats.Top1TechPP = GetAtOrDefault(players, 0, p => p.TechPp);
            stats.Top10TechPP = GetAtOrDefault(players, 9, p => p.TechPp);
            stats.Top100TechPP = GetAtOrDefault(players, 99, p => p.TechPp);
            stats.Top1000TechPP = GetAtOrDefault(players, 999, p => p.TechPp);
            stats.Top2000TechPP = GetAtOrDefault(players, 1999, p => p.TechPp);
            stats.Top5000TechPP = GetAtOrDefault(players, 4999, p => p.TechPp);
            stats.Top10000TechPP = GetAtOrDefault(players, 9999, p => p.TechPp);

            // --------------------
            // Pass PP
            // --------------------
            players = players.OrderByDescending(p => p.PassPp).ToList();

            stats.Top1PassPP = GetAtOrDefault(players, 0, p => p.PassPp);
            stats.Top10PassPP = GetAtOrDefault(players, 9, p => p.PassPp);
            stats.Top100PassPP = GetAtOrDefault(players, 99, p => p.PassPp);
            stats.Top1000PassPP = GetAtOrDefault(players, 999, p => p.PassPp);
            stats.Top2000PassPP = GetAtOrDefault(players, 1999, p => p.PassPp);
            stats.Top5000PassPP = GetAtOrDefault(players, 4999, p => p.PassPp);
            stats.Top10000PassPP = GetAtOrDefault(players, 9999, p => p.PassPp);

            return stats;
        }

        static float GetAtOrDefault(List<Player> list, int index, Func<Player, float> selector)
        {
            return index < list.Count ? selector(list[index]) : 0;
        }
    }
}
