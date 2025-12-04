using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace portaBLe.Pages
{
    public class FiltersModel : BasePageModel
    {
        public List<Player> Players { get; set; } = new List<Player>();
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }
        public string SearchString { get; set; }
        public string ErrorMessage { get; set; }

        public FiltersModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(int currentPage = 1, string searchString = null, string db = null)
        {
            try
            {
                await InitializeDatabaseSelectionAsync(db);

                using var context = (Services.DynamicDbContext)GetDbContext();

                // Check if database can be accessed
                try
                {
                    await context.Database.CanConnectAsync();
                }
                catch (Exception ex)
                {
                    ErrorMessage = $"Cannot connect to database '{SelectedDatabase}': {ex.Message}";
                    return;
                }

                SearchString = searchString;
                int pageSize = 50;
                CurrentPage = currentPage;

                var query = context.Players.AsQueryable();

                if (!string.IsNullOrEmpty(SearchString))
                {
                    query = query.Where(p => EF.Functions.Like(p.Name.ToLower(), $"%{SearchString.ToLower()}%"));
                }

                var totalRecords = await query.CountAsync();
                TotalPages = (int)System.Math.Ceiling(totalRecords / (double)pageSize);

                Players = await query
                    .OrderByDescending(p => p.TopPp)
                    .Skip((currentPage - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error loading players: {ex.Message}";
                Players = new List<Player>();
            }
        }
    }
}
