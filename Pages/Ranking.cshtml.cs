using Microsoft.AspNetCore.Mvc;
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
        
        [BindProperty(SupportsGet = true)]
        public string SearchString { get; set; }
        
        [BindProperty(SupportsGet = true)]
        public string SortBy { get; set; } = "rank";

        public RankingModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(int currentPage = 1, string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);

            using var context = (Services.DynamicDbContext)GetDbContext();

            int pageSize = 50; // Set the number of items per page
            CurrentPage = currentPage;

            // Start with all players, excluding those with rank 0 or 0 PP
            var query = context.Players.Where(p => p.Rank > 0 && p.Pp > 0);

            // Apply search filter if provided
            if (!string.IsNullOrEmpty(SearchString))
            {
                query = query.Where(p => EF.Functions.Like(p.Name.ToLower(), $"%{SearchString.ToLower()}%"));
            }

            // Apply sorting based on selected option
            query = SortBy switch
            {
                "topPP" => query.OrderByDescending(p => p.TopPp),
                "accPP" => query.OrderByDescending(p => p.AccPp),
                "passPP" => query.OrderByDescending(p => p.PassPp),
                "techPP" => query.OrderByDescending(p => p.TechPp),
                "rank" or _ => query.OrderBy(p => p.Rank) // Default to rank
            };

            // Get total count for pagination
            var totalRecords = await query.CountAsync();
            TotalPages = (int)System.Math.Ceiling(totalRecords / (double)pageSize);

            // Get paginated results
            Players = await query
                                    .Skip((currentPage - 1) * pageSize)
                                    .Take(pageSize)
                                    .ToListAsync();
        }

        public string GetSortColumnHeader()
        {
            return SortBy switch
            {
                "topPP" => "Top PP",
                "accPP" => "Acc PP",
                "passPP" => "Pass PP",
                "techPP" => "Tech PP",
                "rank" or _ => "Total PP"
            };
        }

        public float GetSortColumnValue(Player player)
        {
            return SortBy switch
            {
                "topPP" => player.TopPp,
                "accPP" => player.AccPp,
                "passPP" => player.PassPp,
                "techPP" => player.TechPp,
                "rank" or _ => player.Pp
            };
        }
    }
}

