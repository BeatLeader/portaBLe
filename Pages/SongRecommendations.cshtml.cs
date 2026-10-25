using System.Text.RegularExpressions;
using beatleader_songrec.Api;
using beatleader_songrec.Core;
using beatleader_songrec.Export;
using beatleader_songrec.Requests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using portaBLe.Services;
using DbLeaderboard = portaBLe.DB.Leaderboard;

namespace portaBLe.Pages
{
    public class SongRecommendationsModel : BasePageModel
    {
        [BindProperty]
        public string PlayerId { get; set; }

        // Everything else is fixed at the pipeline's tuned defaults (RecommendationOptions): the player only chooses
        // how much easier or harder than their level to aim, and which maps to seed from
        private static readonly RecommendationOptions Defaults = new();

        [BindProperty]
        public double LevelOffset { get; set; } = Defaults.LevelOffset;

        [BindProperty]
        public bool ExcludeAlreadyPlayed { get; set; } = Defaults.ExcludeAlreadyPlayed;

        /// <summary>The leaderboards of the player's plays ticked as seeds (when <see cref="SeedsFor"/> is the player).</summary>
        [BindProperty]
        public List<string> Seeds { get; set; } = [];

        /// <summary>Leaderboards the player added as seeds.</summary>
        [BindProperty]
        public List<string> AddedSeeds { get; set; } = [];

        /// <summary>
        /// The player the seed selection was made for: the page's previous run. Another player id (or none yet) seeds
        /// automatically.
        /// </summary>
        [BindProperty]
        public string? SeedsFor { get; set; }

        /// <summary>The last run, once there is one (with the player's plays and seeds, also when it failed).</summary>
        public RecommendationResult? Run { get; private set; }

        public string? ErrorMessage { get; set; }

        /// <summary>How many maps the search returns at most.</summary>
        private const int SearchResults = 15;

        public SongRecommendationsModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
        }

        public async Task<IActionResult> OnPostAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            await RunPipelineAsync(automaticSeeds: false);
            return Page();
        }

        public async Task<IActionResult> OnPostResetSeedsAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            await RunPipelineAsync(automaticSeeds: true);
            return Page();
        }

        public async Task<IActionResult> OnPostDownloadAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            await RunPipelineAsync(automaticSeeds: false);

            if (ErrorMessage != null || Run is not { Results.Count: > 0 })
            {
                ErrorMessage ??= "No songs to export.";
                return Page();
            }

            var exportOptions = new ExportOptions
            {
                PlaylistTitle = $"BeatLeader Recommendations for {PlayerId}",
                PlaylistAuthor = "portaBLe"
            };
            var bytes = new BplistWriter().BuildBytes(Run.Results, exportOptions);
            return File(bytes, "application/json", "playlist.bplist");
        }

        /// <summary>
        /// Ranked maps for the "add a map" search, as JSON: a leaderboard id or a BeatLeader leaderboard URL first, then
        /// maps whose song name or mapper contains the text, the most played first.
        /// </summary>
        public async Task<IActionResult> OnGetSearchLeaderboardsAsync(string q, string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            var text = (q ?? "").Trim();
            if (text.Length < 2)
            {
                return new JsonResult(Array.Empty<object>());
            }

            // "https://beatleader.xyz/leaderboard/global/2cf6391/1" -> "2cf6391"
            var url = Regex.Match(text, @"leaderboards?/(?:global/|accsaber/)?([0-9a-zA-Z]+)");
            var id = url.Success ? url.Groups[1].Value : text;
            var pattern = $"%{text.Replace("%", "").Replace("_", "")}%";

            using var context = GetDbContext();
            var maps = await context.Set<DbLeaderboard>()
                .Include(l => l.ModifiersRating)
                .Where(l => l.Stars > 0 && (l.Id == id || EF.Functions.Like(l.Name, pattern) || EF.Functions.Like(l.Mapper, pattern)))
                .OrderByDescending(l => l.Id == id)
                .ThenByDescending(l => l.Count)
                .Take(SearchResults)
                .Select(l => new { l.Id, l.Name, l.Mapper, l.DifficultyName, l.ModeName, l.Stars, l.Cover })
                .ToListAsync();
            return new JsonResult(maps.Select(l => new
            {
                id = l.Id,
                name = l.Name,
                mapper = l.Mapper,
                difficulty = l.DifficultyName,
                mode = l.ModeName,
                stars = Math.Round(l.Stars, 2),
                cover = l.Cover
            }));
        }

        private async Task RunPipelineAsync(bool automaticSeeds)
        {
            PlayerId = PlayerId?.Trim();
            if (string.IsNullOrEmpty(PlayerId))
            {
                ErrorMessage = "Please enter a player id.";
                return;
            }

            // The seed selection only holds for the player it was made for
            var useSelection = !automaticSeeds && SeedsFor == PlayerId;
            var options = Defaults with
            {
                LevelOffset = LevelOffset,
                ExcludeAlreadyPlayed = ExcludeAlreadyPlayed,
                SeedLeaderboardIds = useSelection ? Seeds.ToHashSet(StringComparer.Ordinal) : null,
                AddedSeedLeaderboardIds = useSelection ? AddedSeeds : []
            };

            using var context = GetDbContext();
            try
            {
                // The same form again (the playlist download after the page, a reload) is the same run, while the
                // database file is unchanged
                var path = context.Database.GetDbConnection().DataSource;
                var key = string.Join("|", path, System.IO.File.GetLastWriteTimeUtc(path).Ticks, PlayerId, options.LevelOffset,
                    options.ExcludeAlreadyPlayed, options.SeedLeaderboardIds is { } chosen ? string.Join(",", chosen.Order(StringComparer.Ordinal)) : "auto",
                    string.Join(",", options.AddedSeedLeaderboardIds.Order(StringComparer.Ordinal)));
                if (!RecentRuns.TryGetValue(key, out RecommendationResult run))
                {
                    var token = HttpContext.RequestAborted;
                    await RunSlots.WaitAsync(token);
                    try
                    {
                        run = await new RecommendationPipeline(new DbBeatLeaderClient(context)).RunAsync(PlayerId, options, cancellationToken: token);
                    }
                    finally
                    {
                        RunSlots.Release();
                    }
                    RecentRuns.Set(key, run, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(10) });
                }
                Run = run;
                ErrorMessage = Run.Error;
                SeedsFor = Run.PlayerScores.Count > 0 ? PlayerId : null;
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error: {ex.Message}";
            }
        }

        // Runs at once: each reads a few hundred thousand scores and holds them (up to about 30 MB) until it's done, so
        // a burst of requests waits its turn rather than all of them slowing each other down
        private static readonly SemaphoreSlim RunSlots = new(Math.Max(2, Environment.ProcessorCount / 2));

        // The last runs (each up to a few hundred kB: the player's plays and the suggestions)
        private static readonly MemoryCache RecentRuns = new(new MemoryCacheOptions { SizeLimit = 32 });
    }
}
