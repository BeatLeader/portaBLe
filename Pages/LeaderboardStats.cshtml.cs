using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace portaBLe.Pages
{
    public class LeaderboardStatsModel : PageModel
    {
        private readonly AppContext _context;
        public List<Leaderboard> LeaderboardStats { get; set; }
        public string SearchString { get; set; }
        public string SortBy { get; set; } = "TotalPP";
        public bool SortDescending { get; set; } = true;
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }

        public LeaderboardStatsModel(AppContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> OnGetAsync(string searchString, string sortBy = "TotalPP", bool? sortDescending = true, int currentPage = 1)
        {
            SearchString = searchString;
            SortBy = sortBy;
            SortDescending = sortDescending ?? true;
            CurrentPage = currentPage;
            int pageSize = 50;

            var query = _context.Leaderboards.Where(lb => true);

            if (!string.IsNullOrEmpty(SearchString))
            {
                query = query.Where(l => l.Name.ToLower().Contains(SearchString.ToLower()));
            }

            var totalItems = await query.CountAsync();
            TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            query = SortBy switch
            {
                "Name" => SortDescending ? query.OrderByDescending(x => x.Name) : query.OrderBy(x => x.Name),
                "Count" => SortDescending ? query.OrderByDescending(x => x.Count) : query.OrderBy(x => x.Count),
                "Count80" => SortDescending ? query.OrderByDescending(x => x.Count80) : query.OrderBy(x => x.Count80),
                "Count95" => SortDescending ? query.OrderByDescending(x => x.Count95) : query.OrderBy(x => x.Count95),
                "Average" => SortDescending ? query.OrderByDescending(x => x.Average) : query.OrderBy(x => x.Average),
                "Percentile" => SortDescending ? query.OrderByDescending(x => x.Average) : query.OrderBy(x => x.Percentile),
                "Megametric" => SortDescending ? query.OrderByDescending(x => x.Megametric) : query.OrderBy(x => x.Megametric),
                "Megametric125" => SortDescending ? query.OrderByDescending(x => x.Megametric125) : query.OrderBy(x => x.Megametric125),
                "Megametric75" => SortDescending ? query.OrderByDescending(x => x.Megametric75) : query.OrderBy(x => x.Megametric75),
                "Megametric40" => SortDescending ? query.OrderByDescending(x => x.Megametric40) : query.OrderBy(x => x.Megametric40),
                "Top250" => SortDescending ? query.OrderByDescending(x => x.Top250) : query.OrderBy(x => x.Top250),
                "PPRatioFiltered" => SortDescending ? query.OrderByDescending(x => x.PPRatioFiltered) : query.OrderBy(x => x.PPRatioFiltered),
                "PPRatioUnfiltered" => SortDescending ? query.OrderByDescending(x => x.PPRatioUnfiltered) : query.OrderBy(x => x.PPRatioUnfiltered),
                "AccRating" => SortDescending ? query.OrderByDescending(x => x.AccRating) : query.OrderBy(x => x.AccRating),
                "PassRating" => SortDescending ? query.OrderByDescending(x => x.PassRating) : query.OrderBy(x => x.PassRating),
                "TechRating" => SortDescending ? query.OrderByDescending(x => x.TechRating) : query.OrderBy(x => x.TechRating),
                "MultiRating" => SortDescending ? query.OrderByDescending(x => x.MultiRating) : query.OrderBy(x => x.MultiRating),
                "LinearPercent" => SortDescending ? query.OrderByDescending(x => x.LinearPercent) : query.OrderBy(x => x.LinearPercent),
                "ParityErrors" => SortDescending ? query.OrderByDescending(x => x.ParityErrors) : query.OrderBy(x => x.ParityErrors),
                "BombAvoidances" => SortDescending ? query.OrderByDescending(x => x.BombAvoidances) : query.OrderBy(x => x.BombAvoidances),
                _ => SortDescending ? query.OrderByDescending(x => x.TotalPP) : query.OrderBy(x => x.TotalPP),
            };

            LeaderboardStats = await query
                .Skip((CurrentPage - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Page();
        }
    }
}