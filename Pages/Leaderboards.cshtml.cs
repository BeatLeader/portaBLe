using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace portaBLe.Pages
{
    public class LeaderboardsModel : PageModel
    {
        private readonly AppContext _context;
        public List<Leaderboard> Leaderboards { get; set; }
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }

        [BindProperty(SupportsGet = true)]
        public string SearchString { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool SortDescending { get; set; }

        [BindProperty(SupportsGet = true)]
        public string TagFilter { get; set; } // "all", "linear", "fitbeat"

        public LeaderboardsModel(AppContext context)
        {
            _context = context;
        }

        public async Task OnGetAsync(int currentPage = 1)
        {
            IQueryable<Leaderboard> leaderboardQuery = _context.Leaderboards;

            if (!string.IsNullOrEmpty(SearchString))
            {
                leaderboardQuery = leaderboardQuery.Where(l => EF.Functions.Like(l.Name.ToLower(), $"%{SearchString.ToLower()}%"));
            }

            // Apply tag filter
            if (!string.IsNullOrEmpty(TagFilter) && TagFilter != "all")
            {
                if (TagFilter == "linear")
                {
                    leaderboardQuery = leaderboardQuery.Where(l => l.IsLinear);
                }
                else if (TagFilter == "fitbeat")
                {
                    leaderboardQuery = leaderboardQuery.Where(l => l.IsFitbeat);
                }
            }

            if (SortDescending)
            {
                leaderboardQuery = leaderboardQuery.OrderByDescending(l => l.Stars);
            }
            else
            {
                leaderboardQuery = leaderboardQuery.OrderBy(l => l.Stars);
            }

            int pageSize = 10;
            CurrentPage = currentPage;
            var totalRecords = await leaderboardQuery.CountAsync();
            TotalPages = (int)System.Math.Ceiling(totalRecords / (double)pageSize);

            Leaderboards = await leaderboardQuery.Skip((currentPage - 1) * pageSize).Take(pageSize).ToListAsync();
        }
    }
}
