using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using static portaBLe.Pages.LeaderboardModel;

namespace portaBLe.Pages
{
    public class WeightedScoresGraphModel : PageModel
    {
        private readonly AppContext _context;
        public string WeightedJsonData { get; set; }

        // Min weight expressed as percentage 0..100 for easier UI. Will be converted to 0..1 fraction.
        [BindProperty(SupportsGet = true)]
        public float MinWeightPercent { get; set; } = 0;

        // Date components for Timepost filter. If Year is 0 -> unfiltered.
        [BindProperty(SupportsGet = true)]
        public int MinYear { get; set; } = 0;
        [BindProperty(SupportsGet = true)]
        public int MinMonth { get; set; } = 0;
        [BindProperty(SupportsGet = true)]
        public int MinDay { get; set; } = 0;

        // Now a maximum player rank. 0 = unfiltered.
        [BindProperty(SupportsGet = true)]
        public int MaxPlayerRank { get; set; } = 0;

        [BindProperty(SupportsGet = true)]
        public int MinRankedPlayCount { get; set; } = 0;

        // Optional specific player id filter
        [BindProperty(SupportsGet = true)]
        public string PlayerId { get; set; } = string.Empty;

        public WeightedScoresGraphModel(AppContext context)
        {
            _context = context;
        }

        public async Task OnGetAsync()
        {
            int minTimepost = ConvertDateToUnix(MinYear, MinMonth, MinDay);
            var filteredPoints = await GetFilteredPointsAsync(MinWeightPercent / 100f, minTimepost, MaxPlayerRank, MinRankedPlayCount, PlayerId);
            WeightedJsonData = JsonSerializer.Serialize(filteredPoints);
        }

        public async Task<IActionResult> OnGetDataAsync(float minWeightPercent = 0, int minYear = 0, int minMonth = 0, int minDay = 0, int maxPlayerRank = 0, int minRankedPlayCount = 0, string playerId = "")
        {
            int minTimepost = ConvertDateToUnix(minYear, minMonth, minDay);
            var filteredPoints = await GetFilteredPointsAsync(minWeightPercent / 100f, minTimepost, maxPlayerRank, minRankedPlayCount, playerId);
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

        private async Task<List<object>> GetFilteredPointsAsync(float minWeightFraction, int minTimepost, int maxPlayerRank, int minRankedPlayCount, string playerId)
        {
            var points = await _context.Scores
                .Include(s => s.Player)
                .Include(s => s.Leaderboard)
                .Where(s => s.Weight >= minWeightFraction
                            && s.Timepost >= minTimepost
                            && s.Player != null
                            && (maxPlayerRank == 0 || s.Player.Rank <= maxPlayerRank)
                            && s.Player.RankedPlayCount >= minRankedPlayCount
                            && (string.IsNullOrWhiteSpace(playerId) || s.PlayerId == playerId))
                .Select(s => new
                {
                    x = (double)s.Leaderboard.Stars,
                    pp = (double)s.Pp * s.Weight,
                    acc = (double)s.Accuracy,
                    accPP = (double)s.AccPP * s.Weight,
                    passPP = (double)s.PassPP * s.Weight,
                    techPP = (double)s.TechPP * s.Weight
                })
                .ToListAsync();

            int numXBins = 500;
            int numYBins = 500;

            double minX = points.Any() ? points.Min(p => p.x) : 0;
            double maxX = points.Any() ? points.Max(p => p.x) : 1;
            double minY = points.Any() ? points.Min(p => p.pp) : 0;
            double maxY = points.Any() ? points.Max(p => p.pp) : 1;

            double xBinSize = (maxX - minX) / numXBins;
            double yBinSize = (maxY - minY) / numYBins;

            // Avoid division by zero
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
