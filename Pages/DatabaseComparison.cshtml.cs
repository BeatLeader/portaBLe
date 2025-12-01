using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Services;
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
    }
}
