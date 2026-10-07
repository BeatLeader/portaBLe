using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Services;
using System.Text;
using System.Text.Json;

namespace portaBLe.Pages
{
    public class DatabaseComparisonModel : PageModel
    {
        private readonly IDynamicDbContextService _dbService;
        
        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public DatabaseComparisonModel(IDynamicDbContextService dbService)
        {
            _dbService = dbService;
        }

        public string CurrentJsonData { get; set; }
        public string ComparisonJsonData { get; set; }
        public ComparisonStats Stats { get; set; }
        public List<DatabaseConfig> AvailableDatabases { get; set; }
        public string SelectedMainDb { get; set; }
        public string SelectedComparisonDb { get; set; }

        public async Task OnGetAsync(string mainDb = null, string comparisonDb = null)
        {
            // Get available databases
            AvailableDatabases = await _dbService.GetAvailableDatabasesAsync();
            
            // Set default selections
            SelectedMainDb = mainDb ?? _dbService.GetMainDatabaseFileName();
            SelectedComparisonDb = comparisonDb ?? AvailableDatabases.FirstOrDefault(db => db.FileName != SelectedMainDb)?.FileName 
                                   ?? AvailableDatabases.FirstOrDefault()?.FileName;

            if (SelectedMainDb == null || SelectedComparisonDb == null)
            {
                Stats = new ComparisonStats();
                CurrentJsonData = "[]";
                ComparisonJsonData = "[]";
                return;
            }

            using var currentDb = (DynamicDbContext)_dbService.CreateContext(SelectedMainDb);
            using var comparisonDbContext = (DynamicDbContext)_dbService.CreateContext(SelectedComparisonDb);

            // Get summary statistics
            var currentPlayerCount = await currentDb.Players.CountAsync();
            var comparisonPlayerCount = await comparisonDbContext.Players.CountAsync();
            var currentScoreCount = await currentDb.Scores.CountAsync();
            var comparisonScoreCount = await comparisonDbContext.Scores.CountAsync();

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

            var comparisonPlayers = await comparisonDbContext.Players
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
        }

        public async Task<IActionResult> OnGetPlayerComparisonAsync(string playerId, string mainDb = null, string comparisonDb = null)
        {
            // Get available databases
            var availableDatabases = await _dbService.GetAvailableDatabasesAsync();
            
            // Set default selections
            var selectedMainDb = mainDb ?? _dbService.GetMainDatabaseFileName();
            var selectedComparisonDb = comparisonDb ?? availableDatabases.FirstOrDefault(db => db.FileName != selectedMainDb)?.FileName 
                                       ?? availableDatabases.FirstOrDefault()?.FileName;

            if (selectedMainDb == null || selectedComparisonDb == null)
            {
                return new JsonResult(new { error = "Databases not configured" });
            }

            using var currentDb = (DynamicDbContext)_dbService.CreateContext(selectedMainDb);
            using var comparisonDbContext = (DynamicDbContext)_dbService.CreateContext(selectedComparisonDb);

            var currentPlayer = await currentDb.Players.FirstOrDefaultAsync(p => p.Id == playerId);
            var comparisonPlayer = await comparisonDbContext.Players.FirstOrDefaultAsync(p => p.Id == playerId);

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
                ? await comparisonDbContext.Scores
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

        public async Task<IActionResult> OnGetAllPlayersComparisonAsync(string mainDb = null, string comparisonDb = null, int rankLimit = 1000)
        {
            var availableDatabases = await _dbService.GetAvailableDatabasesAsync();
            
            var selectedMainDb = mainDb ?? _dbService.GetMainDatabaseFileName();
            var selectedComparisonDb = comparisonDb ?? availableDatabases.FirstOrDefault(db => db.FileName != selectedMainDb)?.FileName 
                                       ?? availableDatabases.FirstOrDefault()?.FileName;

            if (selectedMainDb == null || selectedComparisonDb == null)
            {
                return new JsonResult(new { error = "Databases not configured" });
            }

            using var currentDb = (DynamicDbContext)_dbService.CreateContext(selectedMainDb);
            using var comparisonDbContext = (DynamicDbContext)_dbService.CreateContext(selectedComparisonDb);

            var comparisonPlayers = await comparisonDbContext.Players
                .Where(p => p.Rank != 0 && p.Rank <= rankLimit)
                .OrderBy(p => p.Rank)
                .ToListAsync();

            var currentPlayersDict = await currentDb.Players
                .Where(p => p.Rank != 0)
                .ToDictionaryAsync(p => p.Id, p => p);

            var result = comparisonPlayers.Select(comparison =>
            {
                var current = currentPlayersDict.GetValueOrDefault(comparison.Id);
                return new PlayerComparisonResult
                {
                    Id = comparison.Id,
                    Name = comparison.Name,
                    CurrentRank = current?.Rank ?? 0,
                    ComparisonRank = comparison.Rank,
                    RankDiff = current != null ? current.Rank - comparison.Rank : 0,
                    CurrentPp = current?.Pp ?? 0,
                    ComparisonPp = comparison.Pp,
                    PpDiff = current != null ? comparison.Pp - current.Pp : 0,
                    AccPpDiff = current != null ? comparison.AccPp - current.AccPp : 0,
                    TechPpDiff = current != null ? comparison.TechPp - current.TechPp : 0,
                    PassPpDiff = current != null ? comparison.PassPp - current.PassPp : 0
                };
            }).ToList();

            return new JsonResult(result);
        }

        public async Task<IActionResult> OnGetAllMapsComparisonAsync(string mainDb = null, string comparisonDb = null)
        {
            var availableDatabases = await _dbService.GetAvailableDatabasesAsync();
            
            var selectedMainDb = mainDb ?? _dbService.GetMainDatabaseFileName();
            var selectedComparisonDb = comparisonDb ?? availableDatabases.FirstOrDefault(db => db.FileName != selectedMainDb)?.FileName 
                                       ?? availableDatabases.FirstOrDefault()?.FileName;

            if (selectedMainDb == null || selectedComparisonDb == null)
            {
                return new JsonResult(new { error = "Databases not configured" });
            }

            using var currentDb = (DynamicDbContext)_dbService.CreateContext(selectedMainDb);
            using var comparisonDbContext = (DynamicDbContext)_dbService.CreateContext(selectedComparisonDb);

            var currentMaps = await currentDb.Leaderboards.ToListAsync();
            var comparisonMapsDict = await comparisonDbContext.Leaderboards
                .ToDictionaryAsync(l => l.Id, l => l);

            var result = currentMaps.Select(current =>
            {
                var comparison = comparisonMapsDict.GetValueOrDefault(current.Id);
                return new MapComparisonResult
                {
                    Id = current.Id,
                    Name = current.Name,
                    ModeName = current.ModeName,
                    DifficultyName = current.DifficultyName,
                    CurrentStars = current.Stars,
                    ComparisonStars = comparison?.Stars ?? 0,
                    StarsDiff = comparison != null ? comparison.Stars - current.Stars : 0,
                    CurrentAccRating = current.AccRating,
                    ComparisonAccRating = comparison?.AccRating ?? 0,
                    AccRatingDiff = comparison != null ? comparison.AccRating - current.AccRating : 0,
                    CurrentPassRating = current.PassRating,
                    ComparisonPassRating = comparison?.PassRating ?? 0,
                    PassRatingDiff = comparison != null ? comparison.PassRating - current.PassRating : 0,
                    CurrentTechRating = current.TechRating,
                    ComparisonTechRating = comparison?.TechRating ?? 0,
                    TechRatingDiff = comparison != null ? comparison.TechRating - current.TechRating : 0
                };
            }).ToList();

            return new JsonResult(result);
        }

        public async Task<IActionResult> OnGetMegametricsComparisonAsync(string mainDb = null, string comparisonDb = null)
        {
            var availableDatabases = await _dbService.GetAvailableDatabasesAsync();
            
            var selectedMainDb = mainDb ?? _dbService.GetMainDatabaseFileName();
            var selectedComparisonDb = comparisonDb ?? availableDatabases.FirstOrDefault(db => db.FileName != selectedMainDb)?.FileName 
                                       ?? availableDatabases.FirstOrDefault()?.FileName;

            if (selectedMainDb == null || selectedComparisonDb == null)
            {
                return new JsonResult(new { error = "Databases not configured" });
            }

            using var currentDb = (DynamicDbContext)_dbService.CreateContext(selectedMainDb);
            using var comparisonDbContext = (DynamicDbContext)_dbService.CreateContext(selectedComparisonDb);

            // Load ALL leaderboards from both databases first
            var currentMapsAll = await currentDb.Leaderboards.ToListAsync();
            var comparisonMapsAll = await comparisonDbContext.Leaderboards.ToListAsync();

            // Filter for maps with Megametric125 > 0.5 after loading
            var currentMaps = currentMapsAll.Where(l => l.Megametric125 > 0.5f).ToList();
            var comparisonMapsDict = comparisonMapsAll.ToDictionary(l => l.Id, l => l);

            // Get all IDs from maps that have Megametric125 > 0.5 in at least one database
            var allIds = currentMaps.Select(m => m.Id)
                .Union(comparisonMapsAll.Where(l => l.Megametric125 > 0.5f).Select(l => l.Id))
                .ToHashSet();

            var result = new List<MegametricComparisonResult>();

            foreach (var id in allIds)
            {
                var current = currentMapsAll.FirstOrDefault(m => m.Id == id);
                var comparison = comparisonMapsDict.GetValueOrDefault(id);

                // Only include if at least one has Megametric125 > 0.5
                if ((current != null && current.Megametric125 > 0.5f) || 
                    (comparison != null && comparison.Megametric125 > 0.5f))
                {
                    var currentCount = current?.Count ?? 1;
                    var comparisonCount = comparison?.Count ?? 1;
                    
                    result.Add(new MegametricComparisonResult
                    {
                        Id = id,
                        Name = current?.Name ?? comparison?.Name ?? "",
                        ModeName = current?.ModeName ?? comparison?.ModeName ?? "",
                        DifficultyName = current?.DifficultyName ?? comparison?.DifficultyName ?? "",
                        CurrentMegametric125 = current?.Megametric125 ?? 0,
                        ComparisonMegametric125 = comparison?.Megametric125 ?? 0,
                        Megametric125Diff = (comparison?.Megametric125 ?? 0) - (current?.Megametric125 ?? 0),
                        CurrentMegametric75 = current?.Megametric75 ?? 0,
                        ComparisonMegametric75 = comparison?.Megametric75 ?? 0,
                        Megametric75Diff = (comparison?.Megametric75 ?? 0) - (current?.Megametric75 ?? 0),
                        CurrentMegametric40 = current?.Megametric40 ?? 0,
                        ComparisonMegametric40 = comparison?.Megametric40 ?? 0,
                        Megametric40Diff = (comparison?.Megametric40 ?? 0) - (current?.Megametric40 ?? 0),
                        CurrentOutlierCount = current?.OutlierCount ?? 0,
                        ComparisonOutlierCount = comparison?.OutlierCount ?? 0,
                        OutlierCountDiff = (comparison?.OutlierCount ?? 0) - (current?.OutlierCount ?? 0),
                        CurrentOutlierPercentage = currentCount > 0 ? ((current?.OutlierCount ?? 0) / (float)currentCount) * 100f : 0,
                        ComparisonOutlierPercentage = comparisonCount > 0 ? ((comparison?.OutlierCount ?? 0) / (float)comparisonCount) * 100f : 0
                    });
                }
            }

            return new JsonResult(result);
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

        public class PlayerComparisonResult
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public int CurrentRank { get; set; }
            public int ComparisonRank { get; set; }
            public int RankDiff { get; set; }
            public float CurrentPp { get; set; }
            public float ComparisonPp { get; set; }
            public float PpDiff { get; set; }
            public float AccPpDiff { get; set; }
            public float TechPpDiff { get; set; }
            public float PassPpDiff { get; set; }
        }

        public class MapComparisonResult
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string ModeName { get; set; }
            public string DifficultyName { get; set; }
            public float CurrentStars { get; set; }
            public float ComparisonStars { get; set; }
            public float StarsDiff { get; set; }
            public float CurrentAccRating { get; set; }
            public float ComparisonAccRating { get; set; }
            public float AccRatingDiff { get; set; }
            public float CurrentPassRating { get; set; }
            public float ComparisonPassRating { get; set; }
            public float PassRatingDiff { get; set; }
            public float CurrentTechRating { get; set; }
            public float ComparisonTechRating { get; set; }
            public float TechRatingDiff { get; set; }
        }

        public class MegametricComparisonResult
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string ModeName { get; set; }
            public string DifficultyName { get; set; }
            public float CurrentMegametric125 { get; set; }
            public float ComparisonMegametric125 { get; set; }
            public float Megametric125Diff { get; set; }
            public float CurrentMegametric75 { get; set; }
            public float ComparisonMegametric75 { get; set; }
            public float Megametric75Diff { get; set; }
            public float CurrentMegametric40 { get; set; }
            public float ComparisonMegametric40 { get; set; }
            public float Megametric40Diff { get; set; }
            public int CurrentOutlierCount { get; set; }
            public int ComparisonOutlierCount { get; set; }
            public int OutlierCountDiff { get; set; }
            public float CurrentOutlierPercentage { get; set; }
            public float ComparisonOutlierPercentage { get; set; }
        }
    }
}
