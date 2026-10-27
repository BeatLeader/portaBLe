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
using DbPlayer = portaBLe.DB.Player;

namespace portaBLe.Pages
{
    public class SongRecommendationsModel : BasePageModel
    {
        /// <summary>The player picked in the search (empty while the search text was typed and not picked yet).</summary>
        [BindProperty]
        public string PlayerId { get; set; }

        /// <summary>The search box: a player name, an id or a BeatLeader profile link.</summary>
        [BindProperty]
        public string PlayerSearch { get; set; }

        /// <summary>The player the page runs for, once found.</summary>
        public DbPlayer? Player { get; private set; }

        /// <summary>The run's peers, by id, with their name, avatar and pp.</summary>
        public Dictionary<string, DbPlayer> PeerPlayers { get; private set; } = [];

        /// <summary>How many of the most linked peers the graph shows.</summary>
        public const int GraphPeers = 40;

        // Everything else is fixed at the pipeline's tuned defaults (RecommendationOptions): the player only chooses
        // the level to aim at, how much easier maps are favored, and which maps to seed from
        private static readonly RecommendationOptions Defaults = new();

        /// <summary>
        /// The level the suggestions aim at, in stars (the level slider); null (or chosen for another player) for the
        /// player's level. Shows the level the last run aimed at.
        /// </summary>
        [BindProperty]
        public double? TargetLevel { get; set; }

        /// <summary>The level slider's top: the stars of the hardest ranked map.</summary>
        public double MaxStars { get; private set; } = 15;

        /// <summary>How much maps under the level are favored over the ones above it, 0 to 100 (the Easier maps slider).</summary>
        [BindProperty]
        public double EasierWeight { get; set; } = Defaults.EasierWeight;

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

        /// <summary>How many maps (or players) the searches return at most.</summary>
        private const int SearchResults = 15;

        public SongRecommendationsModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            await LoadMaxStarsAsync();
        }

        public async Task<IActionResult> OnPostAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            await LoadMaxStarsAsync();
            await RunPipelineAsync(automaticSeeds: false);
            return Page();
        }

        public async Task<IActionResult> OnPostResetSeedsAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            await LoadMaxStarsAsync();
            await RunPipelineAsync(automaticSeeds: true);
            return Page();
        }

        private async Task LoadMaxStarsAsync()
        {
            using var context = GetDbContext();
            var max = await context.Set<DbLeaderboard>().Where(l => l.Stars > 0).MaxAsync(l => (double?)l.Stars);
            if (max is double stars)
            {
                MaxStars = Math.Ceiling(stars * 10) / 10;
            }
        }

        public async Task<IActionResult> OnPostDownloadAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            await LoadMaxStarsAsync();
            await RunPipelineAsync(automaticSeeds: false);

            if (ErrorMessage != null || Run is not { Results.Count: > 0 })
            {
                ErrorMessage ??= "No songs to export.";
                return Page();
            }

            var exportOptions = new ExportOptions
            {
                PlaylistTitle = $"BeatLeader Recommendations for {Player?.Name ?? PlayerId}",
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

        /// <summary>
        /// Players for the player search, as JSON: the one with this id (or BeatLeader profile link) first, then those
        /// whose name contains the text, the highest pp first.
        /// </summary>
        public async Task<IActionResult> OnGetSearchPlayersAsync(string q, string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            var text = (q ?? "").Trim();
            if (text.Length < 2)
            {
                return new JsonResult(Array.Empty<object>());
            }

            var id = ProfileId(text);
            var pattern = $"%{text.Replace("%", "").Replace("_", "")}%";
            using var context = GetDbContext();
            var players = await context.Set<DbPlayer>()
                .Where(p => p.Id == id || EF.Functions.Like(p.Name, pattern))
                .OrderByDescending(p => p.Id == id)
                .ThenByDescending(p => p.Pp)
                .Take(SearchResults)
                .Select(p => new { p.Id, p.Name, p.Country, p.Rank, p.Pp, p.Avatar })
                .ToListAsync();
            return new JsonResult(players.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                country = p.Country,
                rank = p.Rank,
                pp = Math.Round(p.Pp),
                avatar = p.Avatar
            }));
        }

        // "https://beatleader.xyz/u/76561198000000000" -> "76561198000000000"; anything else as typed
        private static string ProfileId(string text)
        {
            var url = Regex.Match(text, @"/u/([0-9a-zA-Z]+)");
            return url.Success ? url.Groups[1].Value : text;
        }

        /// <summary>
        /// The player picked in the search, else the one the search text names: their id (or profile link), their
        /// exact name, or else the highest pp player whose name contains it.
        /// </summary>
        private async Task<DbPlayer?> FindPlayerAsync(DbContext context)
        {
            var players = context.Set<DbPlayer>();
            if (!string.IsNullOrEmpty(PlayerId))
            {
                return await players.FirstOrDefaultAsync(p => p.Id == PlayerId);
            }

            var text = PlayerSearch.Trim();
            var id = ProfileId(text);
            var lower = text.ToLower();
            var pattern = $"%{text.Replace("%", "").Replace("_", "")}%";
            return await players.FirstOrDefaultAsync(p => p.Id == id)
                ?? await players.Where(p => p.Name.ToLower() == lower).OrderByDescending(p => p.Pp).FirstOrDefaultAsync()
                ?? await players.Where(p => EF.Functions.Like(p.Name, pattern)).OrderByDescending(p => p.Pp).FirstOrDefaultAsync();
        }

        private async Task RunPipelineAsync(bool automaticSeeds)
        {
            PlayerId = PlayerId?.Trim();
            if (string.IsNullOrEmpty(PlayerId) && string.IsNullOrWhiteSpace(PlayerSearch))
            {
                ErrorMessage = "Please enter a player name or id.";
                return;
            }

            using var context = GetDbContext();
            Player = await FindPlayerAsync(context);
            if (Player == null)
            {
                ErrorMessage = $"No player found for \"{(string.IsNullOrEmpty(PlayerId) ? PlayerSearch.Trim() : PlayerId)}\" in this database.";
                return;
            }
            PlayerId = Player.Id;
            PlayerSearch = Player.Name;
            // The form shows the player found, not what was posted
            ModelState.Remove(nameof(PlayerId));
            ModelState.Remove(nameof(PlayerSearch));

            // The seed selection only holds for the player it was made for, and so does the level (but one chosen before
            // the first run holds)
            var useSelection = !automaticSeeds && SeedsFor == PlayerId;
            var options = Defaults with
            {
                TargetLevel = SeedsFor == null || SeedsFor == PlayerId ? TargetLevel : null,
                EasierWeight = Math.Clamp(EasierWeight, 0, 100),
                ExcludeAlreadyPlayed = ExcludeAlreadyPlayed,
                SeedLeaderboardIds = useSelection ? Seeds.ToHashSet(StringComparer.Ordinal) : null,
                AddedSeedLeaderboardIds = useSelection ? AddedSeeds : []
            };

            try
            {
                // The same form again (the playlist download after the page, a reload) is the same run, while the
                // database file is unchanged
                var path = context.Database.GetDbConnection().DataSource;
                var key = string.Join("|", path, System.IO.File.GetLastWriteTimeUtc(path).Ticks, PlayerId, options.TargetLevel?.ToString("R") ?? "auto",
                    options.EasierWeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture), options.ExcludeAlreadyPlayed, options.SeedLeaderboardIds is { } chosen ? string.Join(",", chosen.Order(StringComparer.Ordinal)) : "auto",
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
                // The level the run aimed at: null while it's the player's own
                TargetLevel = options.TargetLevel;
                ModelState.Remove(nameof(TargetLevel));

                var peerIds = Run.Peers.Select(p => p.PlayerId).ToList();
                PeerPlayers = await context.Set<DbPlayer>()
                    .Where(p => peerIds.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id);
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
