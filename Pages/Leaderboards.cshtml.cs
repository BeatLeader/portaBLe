using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace portaBLe.Pages
{
    public class LeaderboardsModel : BasePageModel
    {
        public List<Leaderboard> Leaderboards { get; set; }
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }
        public List<string> ModeNames { get; set; }

        [BindProperty(SupportsGet = true)]
        public string SearchString { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool SortDescending { get; set; } = true;

        [BindProperty(SupportsGet = true)]
        public string SortBy { get; set; } = "Stars";

        [BindProperty(SupportsGet = true)]
        public string ModeName { get; set; }

        public LeaderboardsModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(int currentPage = 1, string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);

            using var context = (Services.DynamicDbContext)GetDbContext();

            ModeNames = await context.Leaderboards
                .Select(l => l.ModeName)
                .Distinct()
                .OrderBy(m => m)
                .ToListAsync();

            IQueryable<Leaderboard> leaderboardQuery = context.Leaderboards;

            if (!string.IsNullOrEmpty(SearchString))
            {
                leaderboardQuery = leaderboardQuery.Where(l => EF.Functions.Like(l.Name.ToLower(), $"%{SearchString.ToLower()}%"));
            }

            if (!string.IsNullOrEmpty(ModeName))
            {
                leaderboardQuery = leaderboardQuery.Where(l => l.ModeName == ModeName);
            }

            // Apply sorting based on SortBy parameter
            leaderboardQuery = SortBy switch
            {
                "PassRating" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.PassRating) : leaderboardQuery.OrderBy(l => l.PassRating),
                "TechRating" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.TechRating) : leaderboardQuery.OrderBy(l => l.TechRating),
                "AccRating" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.AccRating) : leaderboardQuery.OrderBy(l => l.AccRating),
                _ => SortDescending ? leaderboardQuery.OrderByDescending(l => l.Stars) : leaderboardQuery.OrderBy(l => l.Stars),
            };

            int pageSize = 10; // Set the number of items per page
            CurrentPage = currentPage;
            var totalRecords = await leaderboardQuery.CountAsync();
            TotalPages = (int)System.Math.Ceiling(totalRecords / (double)pageSize);

            Leaderboards = await leaderboardQuery.Skip((currentPage - 1) * pageSize).Take(pageSize).ToListAsync();
        }
    }
}
