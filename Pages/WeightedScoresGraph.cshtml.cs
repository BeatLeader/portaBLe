using Microsoft.AspNetCore.Mvc;
using portaBLe.Services;

namespace portaBLe.Pages
{
    // Every score's weighted pp against its map's stars or ratings. Reads the Top Scores cache (Services/ScoresGraphData),
    // never the scores table. The rolling average is over every filtered score; the dots are thinned.
    public class WeightedScoresGraphModel : BasePageModel
    {
        // Min weight expressed as percentage 0..100 for easier UI. Will be converted to 0..1 fraction.
        [BindProperty(SupportsGet = true)]
        public float MinWeightPercent { get; set; } = 0;

        // Date components for the date filter (to the day). If Year is 0 -> unfiltered.
        [BindProperty(SupportsGet = true)]
        public int MinYear { get; set; } = 0;
        [BindProperty(SupportsGet = true)]
        public int MinMonth { get; set; } = 0;
        [BindProperty(SupportsGet = true)]
        public int MinDay { get; set; } = 0;

        // Maximum player rank. 0 = unfiltered.
        [BindProperty(SupportsGet = true)]
        public int MaxPlayerRank { get; set; } = 1000;

        [BindProperty(SupportsGet = true)]
        public int MinRankedPlayCount { get; set; } = 100;

        // Optional specific player id filter
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

        // Rolling average window as a share of the filtered scores (%), kept in the URL so it survives switching databases
        [BindProperty(SupportsGet = true)]
        public double RollingPercent { get; set; } = 1;

        public WeightedScoresGraphModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
        }

        public async Task<IActionResult> OnGetDataAsync(float minWeightPercent = 0, int minYear = 0, int minMonth = 0, int minDay = 0, int maxPlayerRank = 1000, int minRankedPlayCount = 100, string playerId = "", bool modNone = true, bool modSS = true, bool modFS = true, bool modSFS = true, string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            var result = await TopScoresCache.TryGetAsync(() => GetDbContext());
            if (result.Data == null) return new JsonResult(new { building = result.Building, error = result.Error });

            var filters = new ScoresGraphData.Filters
            {
                Weighted = true,
                MinWeight = minWeightPercent / 100f,
                MinDay = ScoresGraphDates.DayOf(minYear, minMonth, minDay),
                MaxPlayerRank = maxPlayerRank,
                MinRankedPlayCount = minRankedPlayCount,
                PlayerId = playerId ?? "",
                Speeds = new[] { modNone, modSS, modFS, modSFS },
            };
            return new JsonResult(await ScoresGraphData.BuildAsync(result.Data, () => GetDbContext(), filters, result.Building));
        }
    }

    public static class ScoresGraphDates
    {
        // The filter's day (TopScoresCache.DayNumber), int.MinValue when unfiltered (year 0) or not a valid date
        public static int DayOf(int year, int month, int day)
        {
            if (year <= 0) return int.MinValue;
            if (month <= 0) month = 1;
            if (day <= 0) day = 1;
            try
            {
                return TopScoresCache.DayNumber(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc));
            }
            catch
            {
                return int.MinValue;
            }
        }
    }
}
