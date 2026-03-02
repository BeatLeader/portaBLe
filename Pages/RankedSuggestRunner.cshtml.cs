using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using portaBLe.MapRecommendation.Ranked;

namespace portaBLe.Pages
{
    public class RankedSuggestRunnerModel : PageModel
    {
        private readonly IDbContextFactory<AppContext> _dbContextFactory;
        private readonly SongSuggestDataService _dataService;

        public RankedSuggestRunnerModel(IDbContextFactory<AppContext> dbContextFactory, SongSuggestDataService dataService)
        {
            _dbContextFactory = dbContextFactory;
            _dataService = dataService;
        }

        // Form inputs
        [BindProperty] public string PlayerIdOrName { get; set; } = "";
        [BindProperty] public float ModifierStyle { get; set; } = 1.0f;
        [BindProperty] public float ModifierOverweight { get; set; } = 0.2f;
        [BindProperty] public int OriginLeaderboardsCount { get; set; } = 50;
        [BindProperty] public int ExtraLeaderboardsCount { get; set; } = 15;
        [BindProperty] public bool IgnoreNonImproveable { get; set; } = true;
        [BindProperty] public bool Unplayed { get; set; }

        // Player search results
        public List<PlayerOption> PlayerSearchResults { get; set; } = new();

        // Results
        public bool HasRun { get; set; }
        public string ResolvedPlayerName { get; set; }
        public string ResolvedPlayerId { get; set; }
        public int SuggestionCount { get; set; }
        public List<SuggestionItem> Suggestions { get; set; } = new();
        public string ErrorMessage { get; set; }
        public double ElapsedSeconds { get; set; }

        public bool HasLeaderboards => _dataService.CachedLeaderboards != null;

        public void OnGet()
        {
        }

        public async Task<IActionResult> OnPostSearchPlayersAsync()
        {
            if (string.IsNullOrWhiteSpace(PlayerIdOrName)) return Page();

            using var dbContext = _dbContextFactory.CreateDbContext();
            var query = PlayerIdOrName.Trim();

            PlayerSearchResults = await dbContext.Players
                .Where(p => p.Name.Contains(query) || p.Id == query)
                .OrderBy(p => p.Rank)
                .Take(20)
                .Select(p => new PlayerOption { Id = p.Id, Name = p.Name, Rank = p.Rank })
                .ToListAsync();

            return Page();
        }

        public async Task<IActionResult> OnPostRunAsync()
        {
            if (!HasLeaderboards)
            {
                ErrorMessage = "Leaderboard data not loaded yet. Wait for startup to complete.";
                return Page();
            }

            if (string.IsNullOrWhiteSpace(PlayerIdOrName))
            {
                ErrorMessage = "Please enter a player ID or name.";
                return Page();
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                using var dbContext = _dbContextFactory.CreateDbContext();

                // Resolve player
                var query = PlayerIdOrName.Trim();
                var player = await dbContext.Players
                    .FirstOrDefaultAsync(p => p.Id == query);

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
                    return Page();
                }

                ResolvedPlayerId = player.Id;
                ResolvedPlayerName = player.Name;

                var songSuggestData = new SongSuggestData
                {
                    playerID = player.Id,
                    leaderboards = _dataService.CachedLeaderboards,
                    modifierStyle = ModifierStyle,
                    modifierOverweight = ModifierOverweight,
                    originLeaderboardsCount = OriginLeaderboardsCount,
                    extraLeaderboardsCount = ExtraLeaderboardsCount,
                    ignoreNonImproveable = IgnoreNonImproveable
                };

                await RankedSongSuggest.SuggestedSongs(dbContext, songSuggestData, Unplayed);

                // Store latest run in the singleton for the Analysis page
                _dataService.Data = songSuggestData;

                HasRun = true;
                SuggestionCount = songSuggestData.sortedSuggestions?.Count ?? 0;

                // Build display results (top 50)
                var top50 = songSuggestData.sortedSuggestions?.Take(50).ToList() ?? new();
                for (int i = 0; i < top50.Count; i++)
                {
                    string songID = top50[i];
                    string songName = SongNameHelper.GetSongName(songID, dbContext) ?? songID;

                    float maxScore = 0;
                    if (songSuggestData.leaderboards?.top10kLeaderboardMeta.TryGetValue(songID, out var meta) == true)
                        maxScore = (float)meta.maxScore;

                    float playerScore = songSuggestData.activePlayer?.top10kScore
                        .FirstOrDefault(s => s.songID == songID)?.pp ?? 0;

                    double advantage = maxScore > 0 ? Math.Max(0, (1 - playerScore / maxScore)) * 100 : 100;

                    int linkedPlayers = 0;
                    if (songSuggestData.targetLeaderboards?.endPoints.TryGetValue(songID, out var ep) == true)
                        linkedPlayers = ep.songLinks.Select(l => l.playerID).Distinct().Count();

                    Suggestions.Add(new SuggestionItem
                    {
                        Position = i + 1,
                        SongID = songID,
                        SongName = songName,
                        PlayerScore = playerScore,
                        MaxScore = maxScore,
                        Advantage = advantage,
                        LinkedPlayers = linkedPlayers,
                        IsUnplayed = playerScore == 0
                    });
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error: {ex.Message}";
            }

            sw.Stop();
            ElapsedSeconds = sw.Elapsed.TotalSeconds;
            return Page();
        }

        public class PlayerOption
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public int Rank { get; set; }
        }

        public class SuggestionItem
        {
            public int Position { get; set; }
            public string SongID { get; set; }
            public string SongName { get; set; }
            public float PlayerScore { get; set; }
            public float MaxScore { get; set; }
            public double Advantage { get; set; }
            public int LinkedPlayers { get; set; }
            public bool IsUnplayed { get; set; }
        }
    }
}
