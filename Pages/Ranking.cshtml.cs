using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Pages;
using portaBLe.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace portaBLe
{
    public class RankingModel : BasePageModel
    {
        public List<Player> Players { get; set; }
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }
        public string SearchString { get; set; }

        public RankingModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(int currentPage = 1, string searchString = null, string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);

            using var context = (Services.DynamicDbContext)GetDbContext();

            SearchString = searchString;
            int pageSize = 50; // Set the number of items per page
            CurrentPage = currentPage;

            // Start with all players
            var query = context.Players.AsQueryable();

            // Apply search filter if provided
            if (!string.IsNullOrEmpty(SearchString))
            {
                query = query.Where(p => EF.Functions.Like(p.Name.ToLower(), $"%{SearchString.ToLower()}%"));
            }

            // Get total count for pagination
            var totalRecords = await query.CountAsync();
            TotalPages = (int)System.Math.Ceiling(totalRecords / (double)pageSize);

            // Get paginated results
            Players = await query
                                    .OrderByDescending(p => p.Pp)
                                    .Skip((currentPage - 1) * pageSize)
                                    .Take(pageSize)
                                    .ToListAsync();
        }
    }
}

