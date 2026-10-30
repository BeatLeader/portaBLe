using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Services;
using System.Globalization;
using System.Linq.Expressions;

namespace portaBLe.Pages
{
    public class LeaderboardStatsModel : BasePageModel
    {
        // One column of the table: the view it belongs to, how it sorts (in SQL) and how it shows. Highlight: worth columns are
        // coloured when 8% or more from what maps of their stars average.
        public record StatColumn(string Key, string Label, string Tooltip, string View, Expression<Func<Leaderboard, float?>> Sort, Func<Leaderboard, string> Format, Func<Leaderboard, float?>? Highlight = null);

        public static readonly (string Key, string Label)[] Views =
        {
            ("Overview", "Overview"),
            ("WorthByRank", "Worth by rank"),
            ("Weights", "Profile weights"),
            ("Megametric", "Megametric"),
            ("Traits", "Map traits"),
        };

        private static string Pct(float? v) => v is float w ? w.ToString("+0.0%;-0.0%;0.0%", CultureInfo.InvariantCulture) : "—";
        private static string Signed(float? v) => v is float w ? w.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) : "—";
        private const string RankTooltip = "Players ranked {0}: the pp of their scores on this map / their top pp, compared with maps of the same stars (no profile weight). Blank under 5 scores.";

        public static readonly StatColumn[] Columns =
        {
            new("Stars", "Stars", "Star rating", "Overview", x => x.Stars, x => x.Stars.ToString("0.00")),
            new("PassRating", "Pass", "Pass rating", "Overview", x => x.PassRating, x => x.PassRating.ToString("0.00")),
            new("AccRating", "Acc", "Acc rating", "Overview", x => x.AccRating, x => x.AccRating.ToString("0.00")),
            new("TechRating", "Tech", "Tech rating", "Overview", x => x.TechRating, x => x.TechRating.ToString("0.00")),
            new("Worth", "Worth", "How much the map pays its players compared with maps of the same stars: for the same players, how much more or less they get here than on their other maps at their level, from each player's better efforts (75th percentile). The balance tuner and the acc reweighter use this measure.", "Overview", x => x.Worth, x => Pct(x.Worth), x => x.Worth),
            new("MegametricWorth", "Megametric worth", "The previous worth: Megametric75 / what maps of its stars average - 1 (weighted by where the score lands in each profile, which overreacts to rating changes)", "Overview", x => x.MegametricWorth, x => Signed(x.MegametricWorth)),
            new("Count", "Scores", "Total number of scores on the leaderboard", "Overview", x => x.Count, x => x.Count.ToString()),

            new("WorthRank20", "#1-20", string.Format(RankTooltip, "#1-20"), "WorthByRank", x => x.WorthRank20, x => Pct(x.WorthRank20), x => x.WorthRank20),
            new("WorthRank100", "#21-100", string.Format(RankTooltip, "#21-100"), "WorthByRank", x => x.WorthRank100, x => Pct(x.WorthRank100), x => x.WorthRank100),
            new("WorthRank1000", "#101-1000", string.Format(RankTooltip, "#101-1000"), "WorthByRank", x => x.WorthRank1000, x => Pct(x.WorthRank1000), x => x.WorthRank1000),
            new("WorthRank3000", "#1001-3000", string.Format(RankTooltip, "#1001-3000"), "WorthByRank", x => x.WorthRank3000, x => Pct(x.WorthRank3000), x => x.WorthRank3000),
            new("WorthRank10000", "#3001-10000", string.Format(RankTooltip, "#3001-10000"), "WorthByRank", x => x.WorthRank10000, x => Pct(x.WorthRank10000), x => x.WorthRank10000),

            new("Count80", "Count >80%", "Number of scores with weight for the player >80%", "Weights", x => x.Count80, x => x.Count80.ToString()),
            new("Count95", "Count >95%", "Number of scores with weight for the player >95%", "Weights", x => x.Count95, x => x.Count95.ToString()),
            new("Count80Ratio", "Count/80", "Count >80% / Count", "Weights", x => x.Count > 0 ? (float?)x.Count80 / x.Count : 0, x => x.Count80Ratio.ToString("P1")),
            new("Count95Ratio", "Count/95", "Count >95% / Count", "Weights", x => x.Count > 0 ? (float?)x.Count95 / x.Count : 0, x => x.Count95Ratio.ToString("P1")),
            new("Count95to80Ratio", "95/80", "Count >95% / Count >80%", "Weights", x => x.Count80 > 0 ? (float?)x.Count95 / x.Count80 : 0, x => x.Count95to80Ratio.ToString("P1")),
            new("Average", "Average", "Average weights of all scores on the leaderboard", "Weights", x => x.Average, x => x.Average.ToString("P1")),
            new("Percentile", "Percentile", "Average weights of top 1/3 of leaderboard", "Weights", x => x.Percentile, x => x.Percentile.ToString("F3")),
            new("Top250", "Top250", "Scores from top250 players on their top 5 pages", "Weights", x => x.Top250, x => x.Top250.ToString()),

            new("TotalPP", "Total PP", "Sum of the weighted pp of every score", "Megametric", x => x.TotalPP, x => x.TotalPP.ToString("F2")),
            new("Megametric", "Megametric", "(score PP / player's top PP) * score Weight of top 1/3", "Megametric", x => x.Megametric, x => x.Megametric.ToString("F3")),
            new("Megametric125", "Megametric125", "Megametric but for scores from players with >125 ranked plays", "Megametric", x => x.Megametric125, x => x.Megametric125.ToString("F3")),
            new("Megametric75", "Megametric75", "Megametric but for scores from players with >75 ranked plays", "Megametric", x => x.Megametric75, x => x.Megametric75.ToString("F3")),
            new("Megametric40", "Megametric40", "Megametric but for scores from players with >40 ranked plays", "Megametric", x => x.Megametric40, x => x.Megametric40.ToString("F3")),
            new("PPRatioFiltered", "PP/topPP filtered", "Average score PP / player's top PP for players with 50 or more ranked plays", "Megametric", x => x.PPRatioFiltered, x => x.PPRatioFiltered.ToString("F3")),
            new("PPRatioUnfiltered", "PP/topPP unfiltered", "Average score PP / player's top PP", "Megametric", x => x.PPRatioUnfiltered, x => x.PPRatioUnfiltered.ToString("F3")),

            new("NoteCount", "Notes", "Color notes in the difficulty", "Traits", x => x.NoteCount, x => x.NoteCount > 0 ? x.NoteCount.ToString() : "—"),
            new("MultiPercentage", "Multi %", "Share of the notes in multi-note patterns", "Traits", x => x.MultiPercentage, x => x.MultiPercentage.ToString("F2")),
            new("LinearPercent", "Linear %", "Share of linear swings", "Traits", x => x.LinearPercent, x => x.LinearPercent.ToString("F3")),
            new("ParityErrors", "Parity errors", "Parity errors found by the Analyzer", "Traits", x => x.ParityErrors, x => x.ParityErrors.ToString()),
            new("BombAvoidances", "Bomb avoidances", "Bomb avoidances found by the Analyzer", "Traits", x => x.BombAvoidances, x => x.BombAvoidances.ToString()),
            new("Inlines", "Inlines", "Inlines found by the Analyzer: a note at most 1/4 beat behind another (either color) in the same column and row, hidden by it", "Traits", x => x.Inlines, x => x.Inlines.ToString()),
            new("InlinesPerMinute", "Inlines/min", "Inlines per minute of the song (the Inline tag's measure)", "Traits", x => x.Duration > 0 ? x.Inlines / (x.Duration / 60) : null, x => x.Duration > 0 ? (x.Inlines / (x.Duration / 60)).ToString("0.0") : "—"),
            new("BombResetsPerMinute", "Bomb resets/min", "Bomb avoidances per minute of the song (the Bomb Reset tag's measure)", "Traits", x => x.Duration > 0 ? x.BombAvoidances / (x.Duration / 60) : null, x => x.Duration > 0 ? (x.BombAvoidances / (x.Duration / 60)).ToString("0.0") : "—"),
            new("Tags", "Tags", "Map type (RatingAPI's tagging model) and Linear, Fitbeat, Bomb Reset and Inline (sorted by how many)", "Traits", x => (x.IsLinear ? 1 : 0) + (x.IsFitbeat ? 1 : 0) + (x.IsBombReset ? 1 : 0) + (x.IsInline ? 1 : 0), x => string.Join(", ", MapTags.Of(x).Select(t => t.Label))),
        };

        public List<Leaderboard> LeaderboardStats { get; set; }
        public string SearchString { get; set; }
        public string ModeName { get; set; }
        public List<string> ModeNames { get; set; }
        public string SortBy { get; set; } = "Worth";
        public bool SortDescending { get; set; } = true;
        public string View { get; set; } = "Overview";
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }

        // The current view's columns, and the sorted column when it belongs to another view (so what the table is sorted by shows)
        public List<StatColumn> ShownColumns => Columns.Where(c => c.View == View || c.Key == SortBy).ToList();

        public LeaderboardStatsModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task<IActionResult> OnGetAsync(string searchString, string modeName, string sortBy = "Worth", bool? sortDescending = true, string view = "Overview", int currentPage = 1, string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);

            using var context = (Services.DynamicDbContext)GetDbContext();

            ModeNames = await context.Leaderboards
                .Select(l => l.ModeName)
                .Distinct()
                .OrderBy(m => m)
                .ToListAsync();

            SearchString = searchString;
            ModeName = modeName;
            SortBy = sortBy == "Name" || Columns.Any(c => c.Key == sortBy) ? sortBy : "Worth";
            SortDescending = sortDescending ?? true;
            View = Views.Any(v => v.Key == view) ? view : "Overview";
            CurrentPage = currentPage;
            int pageSize = 50;

            var query = context.Leaderboards.Where(lb => true);

            if (!string.IsNullOrEmpty(SearchString))
            {
                query = query.Where(l => l.Name.ToLower().Contains(SearchString.ToLower()));
            }

            if (!string.IsNullOrEmpty(ModeName))
            {
                query = query.Where(l => l.ModeName == ModeName);
            }

            var totalItems = await query.CountAsync();
            TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            query = SortBy == "Name"
                ? (SortDescending ? query.OrderByDescending(x => x.Name) : query.OrderBy(x => x.Name))
                : SortHelpers.NullsLast(query, Columns.First(c => c.Key == SortBy).Sort, SortDescending);

            LeaderboardStats = await query
                .Skip((CurrentPage - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Page();
        }
    }
}
