using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace portaBLe.Pages
{
    public class MapRecommendationsModel : PageModel
    {
        private readonly IDbContextFactory<AppContext> _dbContextFactory;

        public MapRecommendationsModel(IDbContextFactory<AppContext> dbContextFactory)
        {
            _dbContextFactory = dbContextFactory;
        }

        // Form inputs
        [BindProperty] public string PlayerIdOrName { get; set; } = "";
        [BindProperty] public string SelectedMapIds { get; set; } = "";
        [BindProperty] public int MapCount { get; set; } = 20;
        [BindProperty] public bool UnplayedOnly { get; set; } = true;
        [BindProperty] public string MapSearchQuery { get; set; } = "";

        // Player search
        public List<PlayerOption> PlayerSearchResults { get; set; } = new();

        // Map search
        public List<MapOption> MapSearchResults { get; set; } = new();

        // Selected maps (for display)
        public List<MapOption> SelectedMaps { get; set; } = new();

        // Results
        public bool HasRun { get; set; }
        public string ResolvedPlayerName { get; set; }
        public string ResolvedPlayerId { get; set; }
        public int ResultCount { get; set; }
        public List<RecommendationItem> Recommendations { get; set; } = new();
        public string ErrorMessage { get; set; }
        public double ElapsedSeconds { get; set; }

        public void OnGet()
        {
        }

        public async Task<IActionResult> OnPostSearchPlayersAsync()
        {
            if (string.IsNullOrWhiteSpace(PlayerIdOrName)) return Page();

            using var dbContext = _dbContextFactory.CreateDbContext();

            await RebuildPlayerSearchResults(dbContext);
            await RebuildSelectedMaps(dbContext);
            await RebuildMapSearchResults(dbContext);

            return Page();
        }

        public async Task<IActionResult> OnPostSearchMapsAsync()
        {
            if (string.IsNullOrWhiteSpace(MapSearchQuery)) return Page();

            using var dbContext = _dbContextFactory.CreateDbContext();

            await RebuildPlayerSearchResults(dbContext);
            await RebuildMapSearchResults(dbContext);
            await RebuildSelectedMaps(dbContext);
            return Page();
        }

        public async Task<IActionResult> OnPostAddMapAsync(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return Page();

            var existing = ParseSelectedMapIds();
            if (!existing.Contains(mapId))
            {
                existing.Add(mapId);
                SelectedMapIds = string.Join(",", existing);
            }

            ModelState.Remove(nameof(SelectedMapIds));

            using var dbContext = _dbContextFactory.CreateDbContext();
            await RebuildPlayerSearchResults(dbContext);
            await RebuildSelectedMaps(dbContext);
            await RebuildMapSearchResults(dbContext);
            return Page();
        }

        public async Task<IActionResult> OnPostRemoveMapAsync(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return Page();

            var existing = ParseSelectedMapIds();
            existing.Remove(mapId);
            SelectedMapIds = string.Join(",", existing);

            ModelState.Remove(nameof(SelectedMapIds));

            using var dbContext = _dbContextFactory.CreateDbContext();
            await RebuildPlayerSearchResults(dbContext);
            await RebuildSelectedMaps(dbContext);
            await RebuildMapSearchResults(dbContext);
            return Page();
        }

        public async Task<IActionResult> OnPostRunAsync()
        {
            if (string.IsNullOrWhiteSpace(PlayerIdOrName))
            {
                ErrorMessage = "Please enter a player ID or name.";
                return Page();
            }

            var mapIds = ParseSelectedMapIds();
            if (mapIds.Count == 0)
            {
                ErrorMessage = "Please select at least one source map.";
                return Page();
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using var dbContext = _dbContextFactory.CreateDbContext();

                // Resolve player
                var query = PlayerIdOrName.Trim();
                var player = await dbContext.Players.FirstOrDefaultAsync(p => p.Id == query);
                if (player == null)
                {
                    player = await dbContext.Players
                        .Where(p => p.Name.Contains(query))
                        .OrderBy(p => p.Rank)
                        .FirstOrDefaultAsync();
                }

                if (player == null)
                {
                    ErrorMessage = $"No player found matching \"{PlayerIdOrName}\".";
                    await RebuildSelectedMaps(dbContext);
                    return Page();
                }

                ResolvedPlayerId = player.Id;
                ResolvedPlayerName = player.Name;

                // Run GetRecommendationsAsync
                await RecommendationService.GetRecommendationsAsync(
                    dbContext, player.Id, mapIds, MapCount, UnplayedOnly);

                HasRun = true;

                // Load the generated recommendations from the latest playlist file
                var recsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recommendations");
                if (Directory.Exists(recsDir))
                {
                    var latestFile = Directory.GetFiles(recsDir, $"recommendation_{player.Id}_*.json")
                        .OrderByDescending(f => System.IO.File.GetLastWriteTime(f))
                        .FirstOrDefault();

                    if (latestFile != null)
                    {
                        var json = await System.IO.File.ReadAllTextAsync(latestFile);
                        var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var playlist = System.Text.Json.JsonSerializer.Deserialize<portaBLe.MapRecommendation.Playlist>(json, options);

                        if (playlist?.songs != null)
                        {
                            ResultCount = playlist.songs.Length;
                            for (int i = 0; i < playlist.songs.Length; i++)
                            {
                                var song = playlist.songs[i];
                                var diff = song.difficulties?.FirstOrDefault();

                                // Look up stars from DB
                                float stars = 0;
                                if (!string.IsNullOrEmpty(song.hash))
                                {
                                    var diffName = diff?.name;
                                    var characteristic = diff?.characteristic ?? "";
                                    if (!string.IsNullOrEmpty(diffName))
                                        diffName = char.ToUpper(diffName[0]) + diffName[1..];

                                    var lb = await dbContext.Leaderboards
                                        .FirstOrDefaultAsync(l => l.Hash == song.hash
                                            && l.DifficultyName == diffName
                                            && l.ModeName == characteristic);
                                    stars = lb?.Stars ?? 0;
                                }

                                Recommendations.Add(new RecommendationItem
                                {
                                    Position = i + 1,
                                    SongName = song.songName,
                                    Hash = song.hash,
                                    Mapper = song.levelAuthorName,
                                    Difficulty = diff?.name ?? "?",
                                    Characteristic = diff?.characteristic ?? "?",
                                    Stars = stars
                                });
                            }
                        }
                    }
                }

                await RebuildSelectedMaps(dbContext);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error: {ex.Message}";
            }

            sw.Stop();
            ElapsedSeconds = sw.Elapsed.TotalSeconds;
            return Page();
        }

        private async Task RebuildPlayerSearchResults(AppContext dbContext)
        {
            if (string.IsNullOrWhiteSpace(PlayerIdOrName)) return;

            var query = PlayerIdOrName.Trim();
            PlayerSearchResults = await dbContext.Players
                .Where(p => p.Name.Contains(query) || p.Id == query)
                .OrderBy(p => p.Rank)
                .Take(20)
                .Select(p => new PlayerOption { Id = p.Id, Name = p.Name, Rank = p.Rank })
                .ToListAsync();
        }

        private async Task RebuildMapSearchResults(AppContext dbContext)
        {
            if (string.IsNullOrWhiteSpace(MapSearchQuery)) return;

            var query = MapSearchQuery.Trim();
            MapSearchResults = await dbContext.Leaderboards
                .Where(l => l.Name.Contains(query) || l.Id == query || l.Hash == query)
                .OrderByDescending(l => l.Stars)
                .Take(20)
                .Select(l => new MapOption
                {
                    Id = l.Id,
                    Name = l.Name,
                    DifficultyName = l.DifficultyName,
                    ModeName = l.ModeName,
                    Stars = l.Stars,
                    Mapper = l.Mapper
                })
                .ToListAsync();
        }

        private List<string> ParseSelectedMapIds()
        {
            if (string.IsNullOrWhiteSpace(SelectedMapIds)) return new();
            return SelectedMapIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct()
                .ToList();
        }

        private async Task RebuildSelectedMaps(AppContext dbContext)
        {
            var ids = ParseSelectedMapIds();
            if (ids.Count == 0) return;

            SelectedMaps = await dbContext.Leaderboards
                .Where(l => ids.Contains(l.Id))
                .Select(l => new MapOption
                {
                    Id = l.Id,
                    Name = l.Name,
                    DifficultyName = l.DifficultyName,
                    ModeName = l.ModeName,
                    Stars = l.Stars,
                    Mapper = l.Mapper
                })
                .ToListAsync();
        }

        public class PlayerOption
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public int Rank { get; set; }
        }

        public class MapOption
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string DifficultyName { get; set; }
            public string ModeName { get; set; }
            public float Stars { get; set; }
            public string Mapper { get; set; }
            public string DisplayName => $"{Name} ({DifficultyName} - {ModeName}) ★{Stars:F2}";
        }

        public class RecommendationItem
        {
            public int Position { get; set; }
            public string SongName { get; set; }
            public string Hash { get; set; }
            public string Mapper { get; set; }
            public string Difficulty { get; set; }
            public string Characteristic { get; set; }
            public float Stars { get; set; }
        }
    }
}
