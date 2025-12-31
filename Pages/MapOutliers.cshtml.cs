using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace portaBLe.Pages
{
    public class MapOutliersModel : BasePageModel
    {
        public List<Leaderboard> MapOutliers { get; set; }
        public string SearchString { get; set; }
        public string ModeName { get; set; }
        public List<string> ModeNames { get; set; }
        public string SortBy { get; set; } = "OutlierCount";
        public bool SortDescending { get; set; } = true;
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }

        public MapOutliersModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task<IActionResult> OnGetAsync(
            string searchString, 
            string modeName, 
            string sortBy = "OutlierCount", 
            bool? sortDescending = true, 
            int currentPage = 1,
            string db = null)
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
            SortBy = sortBy;
            SortDescending = sortDescending ?? true;
            CurrentPage = currentPage;
            int pageSize = 50;

            // Query leaderboards with outlier counts
            var query = context.Leaderboards.Where(l => l.OutlierCount > 0);

            if (!string.IsNullOrEmpty(SearchString))
            {
                query = query.Where(l => l.Name.ToLower().Contains(SearchString.ToLower()));
            }

            if (!string.IsNullOrEmpty(ModeName))
            {
                query = query.Where(l => l.ModeName == ModeName);
            }

            // Apply sorting
            query = SortBy switch
            {
                "Name" => SortDescending ? query.OrderByDescending(x => x.Name) : query.OrderBy(x => x.Name),
                "Count" => SortDescending ? query.OrderByDescending(x => x.Count) : query.OrderBy(x => x.Count),
                "OutlierPercentage" => SortDescending 
                    ? query.OrderByDescending(x => x.Count > 0 ? (float)x.OutlierCount / x.Count : 0) 
                    : query.OrderBy(x => x.Count > 0 ? (float)x.OutlierCount / x.Count : 0),
                _ => SortDescending ? query.OrderByDescending(x => x.OutlierCount) : query.OrderBy(x => x.OutlierCount),
            };

            var totalItems = await query.CountAsync();
            TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

            MapOutliers = await query
                .Skip((CurrentPage - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Page();
        }
    }
}

