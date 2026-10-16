using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using portaBLe.Services;
using System.Text.Json;
using static portaBLe.Pages.LeaderboardModel;

namespace portaBLe.Pages
{
    public class UnweightedScoresGraphModel : BasePageModel
    {
        public string UnweightedJsonData { get; set; }

        [BindProperty(SupportsGet = true)]
        public int MinYear { get; set; } = 0;
        [BindProperty(SupportsGet = true)]
        public int MinMonth { get; set; } = 0;
        [BindProperty(SupportsGet = true)]
        public int MinDay { get; set; } = 0;

        [BindProperty(SupportsGet = true)]
        public int MaxPlayerRank { get; set; } = 0;

        [BindProperty(SupportsGet = true)]
        public int MinRankedPlayCount { get; set; } = 0;

        [BindProperty(SupportsGet = true)]
        public string PlayerId { get; set; } = string.Empty;

        // Speed modifiers to include (a score has at most one). All ticked = unfiltered.
        [BindProperty(SupportsGet = true)]
        public bool ModNone { get; set; } = true;
        [BindProperty(SupportsGet = true)]
        public bool ModSS { get; set; } = true;
        [BindProperty(SupportsGet = true)]
        public bool ModFS { get; set; } = true;
        [BindProperty(SupportsGet = true)]
        public bool ModSFS { get; set; } = true;

        public UnweightedScoresGraphModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            UnweightedJsonData = "[]";
        }

        public async Task<IActionResult> OnGetDataAsync(int minYear = 0, int minMonth = 0, int minDay = 0, int maxPlayerRank = 0, int minRankedPlayCount = 0, string playerId = "", bool modNone = true, bool modSS = true, bool modFS = true, bool modSFS = true, string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            int minTimepost = ConvertDateToUnix(minYear, minMonth, minDay);
            var filteredPoints = await GetFilteredPointsAsync(minTimepost, maxPlayerRank, minRankedPlayCount, playerId, modNone, modSS, modFS, modSFS);
            return new JsonResult(filteredPoints);
        }

        private int ConvertDateToUnix(int year, int month, int day)
        {
            try
            {
                if (year <= 0) return 0; // unfiltered
                if (month <= 0) month = 1;
                if (day <= 0) day = 1;
                var dt = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
                var unix = (int)(dt.Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
                return unix;
            }
            catch
            {
                return 0;
            }
        }

        private async Task<List<object>> GetFilteredPointsAsync(int minTimepost, int maxPlayerRank, int minRankedPlayCount, string playerId, bool modNone, bool modSS, bool modFS, bool modSFS)
        {
            using var context = (Services.DynamicDbContext)GetDbContext();

            var rows = await ScoreModifierFilter.Apply(context.Scores, modNone, modSS, modFS, modSFS)
                .Include(s => s.Player)
                .Include(s => s.Leaderboard)
                .Where(s => s.Timepost >= minTimepost
                            && s.Player != null
                            && (maxPlayerRank == 0 || s.Player.Rank <= maxPlayerRank)
                            && s.Player.RankedPlayCount >= minRankedPlayCount
                            && (string.IsNullOrWhiteSpace(playerId) || s.PlayerId == playerId))
                .Select(s => new
                {
                    mods = s.Modifiers,
                    stars = (double)s.Leaderboard.Stars,
                    accRating = (double)s.Leaderboard.AccRating,
                    passRating = (double)s.Leaderboard.PassRating,
                    techRating = (double)s.Leaderboard.TechRating,
                    ssStars = (double?)s.Leaderboard.ModifiersRating.SSStars,
                    fsStars = (double?)s.Leaderboard.ModifiersRating.FSStars,
                    sfStars = (double?)s.Leaderboard.ModifiersRating.SFStars,
                    ssAcc = (double?)s.Leaderboard.ModifiersRating.SSAccRating,
                    fsAcc = (double?)s.Leaderboard.ModifiersRating.FSAccRating,
                    sfAcc = (double?)s.Leaderboard.ModifiersRating.SFAccRating,
                    ssPass = (double?)s.Leaderboard.ModifiersRating.SSPassRating,
                    fsPass = (double?)s.Leaderboard.ModifiersRating.FSPassRating,
                    sfPass = (double?)s.Leaderboard.ModifiersRating.SFPassRating,
                    ssTech = (double?)s.Leaderboard.ModifiersRating.SSTechRating,
                    fsTech = (double?)s.Leaderboard.ModifiersRating.FSTechRating,
                    sfTech = (double?)s.Leaderboard.ModifiersRating.SFTechRating,
                    pp = (double)s.Pp,
                    acc = (double)s.Accuracy,
                    accPP = (double)s.AccPP,
                    passPP = (double)s.PassPP,
                    techPP = (double)s.TechPP,
                })
                .ToListAsync();

            // A modded score uses its modifier's stars and ratings (SFS score -> SFS stars), the others the base ones
            var points = rows.Select(s =>
            {
                var speed = ScoreModifierFilter.SpeedModifier(s.mods);
                return new
                {
                    x = ScoreModifierFilter.Pick(speed, s.stars, s.ssStars, s.fsStars, s.sfStars),
                    pp = s.pp,
                    acc = s.acc,
                    accPP = s.accPP,
                    passPP = s.passPP,
                    techPP = s.techPP,
                    accRating = ScoreModifierFilter.Pick(speed, s.accRating, s.ssAcc, s.fsAcc, s.sfAcc),
                    passRating = ScoreModifierFilter.Pick(speed, s.passRating, s.ssPass, s.fsPass, s.sfPass),
                    techRating = ScoreModifierFilter.Pick(speed, s.techRating, s.ssTech, s.fsTech, s.sfTech)
                };
            }).ToList();

            int numXBins = 500;
            int numYBins = 500;

            double minX = points.Any() ? points.Min(p => p.x) : 0;
            double maxX = points.Any() ? points.Max(p => p.x) : 1;
            double minY = points.Any() ? points.Min(p => p.pp) : 0;
            double maxY = points.Any() ? points.Max(p => p.pp) : 1;

            double xBinSize = (maxX - minX) / numXBins;
            double yBinSize = (maxY - minY) / numYBins;

            if (xBinSize <= 0) xBinSize = 1;
            if (yBinSize <= 0) yBinSize = 1;

            var grid = new bool[numXBins, numYBins];
            var filteredPoints = new List<object>();

            foreach (var p in points)
            {
                int xIndex = Math.Min((int)((p.x - minX) / xBinSize), numXBins - 1);
                int yIndex = Math.Min((int)((p.pp - minY) / yBinSize), numYBins - 1);

                if (!grid[xIndex, yIndex])
                {
                    filteredPoints.Add(p);
                    grid[xIndex, yIndex] = true;
                }
            }

            return filteredPoints;
        }
    }
}
