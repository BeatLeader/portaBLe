using Microsoft.AspNetCore.Mvc;
using portaBLe.Services;

namespace portaBLe.Pages
{
    // Every score's pp against its map's stars or ratings. Reads the Top Scores cache (Services/ScoresGraphData), never the
    // scores table. The rolling average is over every filtered score; the dots are thinned.
    public class UnweightedScoresGraphModel : BasePageModel
    {
        [BindProperty(SupportsGet = true)]
        public int MinYear { get; set; } = 0;
        [BindProperty(SupportsGet = true)]
        public int MinMonth { get; set; } = 0;
        [BindProperty(SupportsGet = true)]
        public int MinDay { get; set; } = 0;

        [BindProperty(SupportsGet = true)]
        public int MaxPlayerRank { get; set; } = 1000;

        [BindProperty(SupportsGet = true)]
        public int MinRankedPlayCount { get; set; } = 100;

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

        public UnweightedScoresGraphModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
        }

        public async Task<IActionResult> OnGetDataAsync(int minYear = 0, int minMonth = 0, int minDay = 0, int maxPlayerRank = 1000, int minRankedPlayCount = 100, string playerId = "", bool modNone = true, bool modSS = true, bool modFS = true, bool modSFS = true, string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            var result = await TopScoresCache.TryGetAsync(() => GetDbContext());
            if (result.Data == null) return new JsonResult(new { building = result.Building, error = result.Error });

            var filters = new ScoresGraphData.Filters
            {
                Weighted = false,
                MinDay = ScoresGraphDates.DayOf(minYear, minMonth, minDay),
                MaxPlayerRank = maxPlayerRank,
                MinRankedPlayCount = minRankedPlayCount,
                PlayerId = playerId ?? "",
                Speeds = new[] { modNone, modSS, modFS, modSFS },
            };
            return new JsonResult(await ScoresGraphData.BuildAsync(result.Data, () => GetDbContext(), filters, result.Building));
        }
    }
}
