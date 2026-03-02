using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using System.Text.Json;

namespace portaBLe.Pages
{
    public class DatabaseComparisonModel : PageModel
    {
        private readonly IDbContextFactory<AppContext> _contextFactory;
        private readonly IDbContextFactory<ComparisonContext> _comparisonContextFactory;
        
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public DatabaseComparisonModel(
            IDbContextFactory<AppContext> contextFactory,
            IDbContextFactory<ComparisonContext> comparisonContextFactory)
        {
            _contextFactory = contextFactory;
            _comparisonContextFactory = comparisonContextFactory;
        }

        public string CurrentJsonData { get; set; }
        public string ComparisonJsonData { get; set; }
        public ComparisonStats Stats { get; set; }
        public StatsComparisonData StatsComparison { get; set; }
        public List<LeaderboardMegametricComparison> LeaderboardComparisons { get; set; }
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }
        public string SortBy { get; set; } = "RelativeDiff";
        public bool SortDescending { get; set; } = true;

        public async Task OnGetAsync(int currentPage = 1, string sortBy = "RelativeDiff", bool sortDescending = true)
        {
            CurrentPage = currentPage;
            SortBy = sortBy;
            SortDescending = sortDescending;

            using var currentDb = _contextFactory.CreateDbContext();
            using var comparisonDb = _comparisonContextFactory.CreateDbContext();

            // Get summary statistics
            var currentPlayerCount = await currentDb.Players.CountAsync();
            var comparisonPlayerCount = await comparisonDb.Players.CountAsync();
            var currentScoreCount = await currentDb.Scores.CountAsync();
            var comparisonScoreCount = await comparisonDb.Scores.CountAsync();

            // Get top 100 players from both databases
            var currentPlayers = await currentDb.Players
                .OrderBy(p => p.Rank)
                .Where(p => p.Rank != 0)
                .Take(100)
                .Select(p => new PlayerComparisonData
                {
                    Id = p.Id,
                    Name = p.Name,
                    Rank = p.Rank,
                    Pp = p.Pp,
                    AccPp = p.AccPp,
                    TechPp = p.TechPp,
                    PassPp = p.PassPp
                })
                .ToListAsync();

            var comparisonPlayers = await comparisonDb.Players
                .OrderBy(p => p.Rank)
                .Where(p => p.Rank != 0)
                .Take(100)
                .Select(p => new PlayerComparisonData
                {
                    Id = p.Id,
                    Name = p.Name,
                    Rank = p.Rank,
                    Pp = p.Pp,
                    AccPp = p.AccPp,
                    TechPp = p.TechPp,
                    PassPp = p.PassPp
                })
                .ToListAsync();

            CurrentJsonData = JsonSerializer.Serialize(currentPlayers, _jsonOptions);
            ComparisonJsonData = JsonSerializer.Serialize(comparisonPlayers, _jsonOptions);

            Stats = new ComparisonStats
            {
                CurrentPlayerCount = currentPlayerCount,
                ComparisonPlayerCount = comparisonPlayerCount,
                CurrentScoreCount = currentScoreCount,
                ComparisonScoreCount = comparisonScoreCount,
                PlayerCountDiff = currentPlayerCount - comparisonPlayerCount,
                ScoreCountDiff = currentScoreCount - comparisonScoreCount
            };

            // Get Stats comparison (empty string for "All" mode)
            var currentStats = await currentDb.Stats.FirstOrDefaultAsync(s => s.ModeName == "");
            var comparisonStats = await comparisonDb.Stats.FirstOrDefaultAsync(s => s.ModeName == "");

            if (currentStats != null && comparisonStats != null)
            {
                StatsComparison = new StatsComparisonData
                {
                    CurrentTotalOutlier = currentStats.TotalOutlier,
                    ComparisonTotalOutlier = comparisonStats.TotalOutlier,
                    OutlierDiff = currentStats.TotalOutlier - comparisonStats.TotalOutlier,
                    CurrentAvgOutlierPercentage = currentStats.AvgOutlierPercentage,
                    ComparisonAvgOutlierPercentage = comparisonStats.AvgOutlierPercentage,
                    AvgOutlierPercentageDiff = currentStats.AvgOutlierPercentage - comparisonStats.AvgOutlierPercentage,
                    CurrentAvgMegametric = currentStats.AvgMegametric,
                    ComparisonAvgMegametric = comparisonStats.AvgMegametric,
                    AvgMegametricDiff = currentStats.AvgMegametric - comparisonStats.AvgMegametric,
                    CurrentAvgMegametric40 = currentStats.AvgMegametric40,
                    ComparisonAvgMegametric40 = comparisonStats.AvgMegametric40,
                    AvgMegametric40Diff = currentStats.AvgMegametric40 - comparisonStats.AvgMegametric40,
                    CurrentAvgMegametric75 = currentStats.AvgMegametric75,
                    ComparisonAvgMegametric75 = comparisonStats.AvgMegametric75,
                    AvgMegametric75Diff = currentStats.AvgMegametric75 - comparisonStats.AvgMegametric75,
                    CurrentAvgMegametric125 = currentStats.AvgMegametric125,
                    ComparisonAvgMegametric125 = comparisonStats.AvgMegametric125,
                    AvgMegametric125Diff = currentStats.AvgMegametric125 - comparisonStats.AvgMegametric125
                };
            }

            // Get leaderboard comparisons
            await LoadLeaderboardComparisons(currentDb, comparisonDb);
        }

        private async Task LoadLeaderboardComparisons(AppContext currentDb, ComparisonContext comparisonDb)
        {
            const int pageSize = 50;

            // Get all leaderboards from current DB
            var currentLeaderboards = await currentDb.Leaderboards
                .Select(l => new { l.Id, l.Name, l.DifficultyName, l.ModeName, l.Megametric125, l.PassRating, l.TechRating, l.AccRating, l.Count })
                .ToDictionaryAsync(l => l.Id);

            // Get all leaderboards from comparison DB
            var comparisonLeaderboards = await comparisonDb.Leaderboards
                .Select(l => new { l.Id, l.Name, l.DifficultyName, l.ModeName, l.Megametric125, l.PassRating, l.TechRating, l.AccRating, l.Count })
                .ToDictionaryAsync(l => l.Id);

            // Create comparison list for leaderboards in both databases
            var comparisons = new List<LeaderboardMegametricComparison>();

            foreach (var current in currentLeaderboards)
            {
                if (comparisonLeaderboards.TryGetValue(current.Key, out var comparison))
                {
                    var currentValue = current.Value.Megametric125;
                    var comparisonValue = comparison.Megametric125;
                    
                    // Only include if either value is > 0.5
                    if (currentValue <= 0.5f && comparisonValue <= 0.5f)
                        continue;

                    var absoluteDiff = currentValue - comparisonValue;
                    var relativeDiff = comparisonValue != 0 
                        ? ((currentValue - comparisonValue) / comparisonValue) * 100 
                        : 0;

                    // Calculate relative differences for rating types
                    var passRelativeDiff = comparison.PassRating != 0
                        ? ((current.Value.PassRating - comparison.PassRating) / comparison.PassRating) * 100
                        : 0;
                    var techRelativeDiff = comparison.TechRating != 0
                        ? ((current.Value.TechRating - comparison.TechRating) / comparison.TechRating) * 100
                        : 0;
                    var accRelativeDiff = comparison.AccRating != 0
                        ? ((current.Value.AccRating - comparison.AccRating) / comparison.AccRating) * 100
                        : 0;

                    comparisons.Add(new LeaderboardMegametricComparison
                    {
                        Id = current.Key,
                        Name = current.Value.Name,
                        DifficultyName = current.Value.DifficultyName,
                        ModeName = current.Value.ModeName,
                        CurrentMegametric125 = currentValue,
                        ComparisonMegametric125 = comparisonValue,
                        AbsoluteDiff = absoluteDiff,
                        RelativeDiff = relativeDiff,
                        CurrentPassRating = current.Value.PassRating,
                        ComparisonPassRating = comparison.PassRating,
                        PassRatingRelativeDiff = passRelativeDiff,
                        CurrentTechRating = current.Value.TechRating,
                        ComparisonTechRating = comparison.TechRating,
                        TechRatingRelativeDiff = techRelativeDiff,
                        CurrentAccRating = current.Value.AccRating,
                        ComparisonAccRating = comparison.AccRating,
                        AccRatingRelativeDiff = accRelativeDiff,
                        CurrentScoreCount = current.Value.Count,
                        ComparisonScoreCount = comparison.Count
                    });
                }
            }

            // Sort the comparisons
            comparisons = SortBy switch
            {
                "Name" => SortDescending ? comparisons.OrderByDescending(l => l.Name).ToList() : comparisons.OrderBy(l => l.Name).ToList(),
                "Current" => SortDescending ? comparisons.OrderByDescending(l => l.CurrentMegametric125).ToList() : comparisons.OrderBy(l => l.CurrentMegametric125).ToList(),
                "Comparison" => SortDescending ? comparisons.OrderByDescending(l => l.ComparisonMegametric125).ToList() : comparisons.OrderBy(l => l.ComparisonMegametric125).ToList(),
                "AbsoluteDiff" => SortDescending ? comparisons.OrderByDescending(l => l.AbsoluteDiff).ToList() : comparisons.OrderBy(l => l.AbsoluteDiff).ToList(),
                _ => SortDescending ? comparisons.OrderByDescending(l => Math.Abs(l.RelativeDiff)).ToList() : comparisons.OrderBy(l => Math.Abs(l.RelativeDiff)).ToList(),
            };

            // Calculate pagination
            TotalPages = (int)Math.Ceiling(comparisons.Count / (double)pageSize);
            LeaderboardComparisons = comparisons
                .Skip((CurrentPage - 1) * pageSize)
                .Take(pageSize)
                .ToList();
        }

        public async Task<IActionResult> OnGetPlayerComparisonAsync(string playerId)
        {
            using var currentDb = _contextFactory.CreateDbContext();
            using var comparisonDb = _comparisonContextFactory.CreateDbContext();

            var currentPlayer = await currentDb.Players.FirstOrDefaultAsync(p => p.Id == playerId);
            var comparisonPlayer = await comparisonDb.Players.FirstOrDefaultAsync(p => p.Id == playerId);

            if (currentPlayer == null && comparisonPlayer == null)
            {
                return new JsonResult(new { error = "Player not found in either database" });
            }

            var currentScores = currentPlayer != null 
                ? await currentDb.Scores
                    .Where(s => s.PlayerId == playerId)
                    .OrderByDescending(s => s.Pp)
                    .Take(200)
                    .Select(s => new ScoreComparisonData
                    {
                        Id = s.Id,
                        LeaderboardId = s.LeaderboardId,
                        Pp = s.Pp,
                        AccPP = s.AccPP,
                        TechPP = s.TechPP,
                        PassPP = s.PassPP,
                        Accuracy = s.Accuracy,
                        Weight = s.Weight
                    })
                    .ToListAsync()
                : new List<ScoreComparisonData>();

            var comparisonScores = comparisonPlayer != null
                ? await comparisonDb.Scores
                    .Where(s => s.PlayerId == playerId)
                    .OrderByDescending(s => s.Pp)
                    .Take(200)
                    .Select(s => new ScoreComparisonData
                    {
                        Id = s.Id,
                        LeaderboardId = s.LeaderboardId,
                        Pp = s.Pp,
                        AccPP = s.AccPP,
                        TechPP = s.TechPP,
                        PassPP = s.PassPP,
                        Accuracy = s.Accuracy,
                        Weight = s.Weight
                    })
                    .ToListAsync()
                : new List<ScoreComparisonData>();

            return new JsonResult(new
            {
                currentPlayer = currentPlayer != null ? new PlayerComparisonData
                {
                    Id = currentPlayer.Id,
                    Name = currentPlayer.Name,
                    Rank = currentPlayer.Rank,
                    Pp = currentPlayer.Pp,
                    AccPp = currentPlayer.AccPp,
                    TechPp = currentPlayer.TechPp,
                    PassPp = currentPlayer.PassPp
                } : null,
                comparisonPlayer = comparisonPlayer != null ? new PlayerComparisonData
                {
                    Id = comparisonPlayer.Id,
                    Name = comparisonPlayer.Name,
                    Rank = comparisonPlayer.Rank,
                    Pp = comparisonPlayer.Pp,
                    AccPp = comparisonPlayer.AccPp,
                    TechPp = comparisonPlayer.TechPp,
                    PassPp = comparisonPlayer.PassPp
                } : null,
                currentScores = currentScores,
                comparisonScores = comparisonScores
            });
        }

        public class PlayerComparisonData
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public int Rank { get; set; }
            public float Pp { get; set; }
            public float AccPp { get; set; }
            public float TechPp { get; set; }
            public float PassPp { get; set; }
        }

        public class ScoreComparisonData
        {
            public int Id { get; set; }
            public string LeaderboardId { get; set; }
            public float Pp { get; set; }
            public float AccPP { get; set; }
            public float TechPP { get; set; }
            public float PassPP { get; set; }
            public float Accuracy { get; set; }
            public float Weight { get; set; }
        }

        public class ComparisonStats
        {
            public int CurrentPlayerCount { get; set; }
            public int ComparisonPlayerCount { get; set; }
            public int CurrentScoreCount { get; set; }
            public int ComparisonScoreCount { get; set; }
            public int PlayerCountDiff { get; set; }
            public int ScoreCountDiff { get; set; }
        }

        public class StatsComparisonData
        {
            public int CurrentTotalOutlier { get; set; }
            public int ComparisonTotalOutlier { get; set; }
            public int OutlierDiff { get; set; }
            public float CurrentAvgOutlierPercentage { get; set; }
            public float ComparisonAvgOutlierPercentage { get; set; }
            public float AvgOutlierPercentageDiff { get; set; }
            public float CurrentAvgMegametric { get; set; }
            public float ComparisonAvgMegametric { get; set; }
            public float AvgMegametricDiff { get; set; }
            public float CurrentAvgMegametric40 { get; set; }
            public float ComparisonAvgMegametric40 { get; set; }
            public float AvgMegametric40Diff { get; set; }
            public float CurrentAvgMegametric75 { get; set; }
            public float ComparisonAvgMegametric75 { get; set; }
            public float AvgMegametric75Diff { get; set; }
            public float CurrentAvgMegametric125 { get; set; }
            public float ComparisonAvgMegametric125 { get; set; }
            public float AvgMegametric125Diff { get; set; }
        }

        public class LeaderboardMegametricComparison
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string DifficultyName { get; set; }
            public string ModeName { get; set; }
            public float CurrentMegametric125 { get; set; }
            public float ComparisonMegametric125 { get; set; }
            public float AbsoluteDiff { get; set; }
            public float RelativeDiff { get; set; }
            public float CurrentPassRating { get; set; }
            public float ComparisonPassRating { get; set; }
            public float PassRatingRelativeDiff { get; set; }
            public float CurrentTechRating { get; set; }
            public float ComparisonTechRating { get; set; }
            public float TechRatingRelativeDiff { get; set; }
            public float CurrentAccRating { get; set; }
            public float ComparisonAccRating { get; set; }
            public float AccRatingRelativeDiff { get; set; }
            public int CurrentScoreCount { get; set; }
            public int ComparisonScoreCount { get; set; }
        }
    }
}
