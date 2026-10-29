using Microsoft.AspNetCore.Mvc;
using portaBLe.Services;

namespace portaBLe.Pages
{
    // Every score's weighted pp against its map's stars or ratings. Reads the Top Scores cache (Services/ScoresGraphData),
    // never the scores table. The rolling average is over every filtered score; the dots are thinned. "Compare with" loads
    // another database with the same filters (the page calls the data handler for both) and draws their difference.
    public class WeightedScoresGraphModel : BasePageModel
    {
        // Min weight in % (0..100), converted to a 0..1 fraction for the filter
        [BindProperty(SupportsGet = true)]
        public float MinWeightPercent { get; set; } = 0;

        // Date filter (to the day); year 0 = unfiltered
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

        // Only this player's scores when set
        [BindProperty(SupportsGet = true)]
        public string PlayerId { get; set; } = string.Empty;

        // Speed modifiers to include (a score has at most one). All ticked = unfiltered.
        [BindProperty(SupportsGet = true)]
        public bool ModNone { get; set; } = true;
        [BindProperty(SupportsGet = true)]
        public bool ModSS { get; set; } = false;
        [BindProperty(SupportsGet = true)]
        public bool ModFS { get; set; } = false;
        [BindProperty(SupportsGet = true)]
        public bool ModSFS { get; set; } = false;

        // Rolling average window as a share of the filtered scores (%), kept in the URL so it survives switching databases
        [BindProperty(SupportsGet = true)]
        public double RollingPercent { get; set; } = 1;

        // Another database drawn on the same graph with the same filters, plus their difference ("" for none)
        [BindProperty(SupportsGet = true)]
        public string CompareDb { get; set; } = "";

        public WeightedScoresGraphModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
        }

        public async Task<IActionResult> OnGetDataAsync(float minWeightPercent = 0, int minYear = 0, int minMonth = 0, int minDay = 0, int maxPlayerRank = 1000, int minRankedPlayCount = 100, string playerId = "", bool modNone = true, bool modSS = false, bool modFS = false, bool modSFS = false, string db = null)
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
