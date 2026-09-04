using beatleader_songrec.Api;
using beatleader_songrec.Core;
using beatleader_songrec.Export;
using beatleader_songrec.Models;
using beatleader_songrec.Requests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using portaBLe.Services;

namespace portaBLe.Pages
{
    public class SongRecommendationsModel : BasePageModel
    {
        [BindProperty]
        public string PlayerId { get; set; }

        [BindProperty]
        public int SeedCount { get; set; } = 50;

        [BindProperty]
        public int TopK { get; set; } = 20;

        [BindProperty]
        public double StyleWeight { get; set; } = 50;

        [BindProperty]
        public double OverweightWeight { get; set; } = 50;

        [BindProperty]
        public double LinkKeepPercent { get; set; } = 0.5;

        [BindProperty]
        public bool ExcludeAlreadyPlayed { get; set; } = true;

        public IReadOnlyList<ScoredSong> Results { get; set; } = [];

        public IReadOnlyList<string> ProgressMessages { get; private set; } = [];

        public string ErrorMessage { get; set; }

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

            var (results, progress, error) = await RunPipelineAsync();
            Results = results;
            ProgressMessages = progress;
            ErrorMessage = error;

            return Page();
        }

        public async Task<IActionResult> OnPostDownloadAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);

            var (results, _, error) = await RunPipelineAsync();

            if (error != null || results.Count == 0)
            {
                ErrorMessage = error ?? "No songs to export.";
                return Page();
            }

            var writer = new BplistWriter();
            var exportOptions = new ExportOptions
            {
                PlaylistTitle = $"BeatLeader Recommendations for {PlayerId}",
                PlaylistAuthor = "portaBLe"
            };

            var bytes = writer.BuildBytes(results, exportOptions);
            return File(bytes, "application/json", "playlist.bplist");
        }

        private async Task<(IReadOnlyList<ScoredSong> Results, IReadOnlyList<string> Progress, string Error)> RunPipelineAsync()
        {
            if (string.IsNullOrWhiteSpace(PlayerId))
            {
                return ([], [], "Please enter a player id.");
            }

            using var context = GetDbContext();

            var client = new DbBeatLeaderClient(context);
            var seedSelector = new SeedSongSelector(client);
            var finder = new LinkedPlayerFinder(client);
            var scorer = new SongScorer();

            var progress = new List<string>();
            void Report(string message) => progress.Add(message);

            try
            {
                var seedScores = await seedSelector.SelectSeedSongsAsync(PlayerId, SeedCount, onProgress: Report);
                if (seedScores.Count == 0)
                {
                    return ([], progress, "No seed scores found for this player.");
                }

                var candidates = await finder.FindCandidateSongsAsync(
                    PlayerId,
                    SeedCount,
                    TopK,
                    onProgress: Report,
                    seedScores: seedScores,
                    linkKeepPercent: LinkKeepPercent);

                if (candidates.Count == 0)
                {
                    return ([], progress, "No candidate songs found.");
                }

                if (ExcludeAlreadyPlayed)
                {
                    var alreadyPlayedLeaderboardIds = new HashSet<string>(
                        seedScores.Select(s => s.Song.LeaderboardId), StringComparer.Ordinal);

                    var allPlayerScores = await client.GetPlayerTopScoresAsync(PlayerId, int.MaxValue);
                    foreach (var score in allPlayerScores)
                    {
                        alreadyPlayedLeaderboardIds.Add(score.Song.LeaderboardId);
                    }

                    candidates = candidates
                        .Where(pair => !alreadyPlayedLeaderboardIds.Contains(pair.Key.LeaderboardId))
                        .ToDictionary(pair => pair.Key, pair => pair.Value);

                    if (candidates.Count == 0)
                    {
                        return ([], progress, "No candidate songs remain after excluding already-played songs.");
                    }
                }

                var weights = new ScoringWeights { Style = StyleWeight, Overweight = OverweightWeight };
                var scored = scorer.ScoreCandidates(candidates, seedScores, weights, topN: 50);

                return (scored, progress, null);
            }
            catch (Exception ex)
            {
                return ([], progress, $"Error: {ex.Message}");
            }
        }
    }
}
