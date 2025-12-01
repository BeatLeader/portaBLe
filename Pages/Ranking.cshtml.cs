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

        public RankingModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(int currentPage = 1, string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);

            using var context = (Services.DynamicDbContext)GetDbContext();

            int pageSize = 50; // Set the number of items per page
            CurrentPage = currentPage;

            var totalRecords = await context.Players.CountAsync();
            TotalPages = (int)System.Math.Ceiling(totalRecords / (double)pageSize);

            Players = await context.Players
                                    .OrderByDescending(p => p.Pp)
                                    .Skip((currentPage - 1) * pageSize)
                                    .Take(pageSize)
                                    .ToListAsync();
        }
    }
}

