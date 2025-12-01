using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace portaBLe
{
    public class RankingModel : PageModel
    {
        private readonly AppContext _context;
        public List<Player> Players { get; set; }
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }

        public RankingModel(AppContext context)
        {
            _context = context;
        }

        public async Task OnGetAsync(int currentPage = 1)
        {
            int pageSize = 50; // Set the number of items per page
            CurrentPage = currentPage;

            var totalRecords = await _context.Players.CountAsync();
            TotalPages = (int)System.Math.Ceiling(totalRecords / (double)pageSize);

            Players = await _context.Players
                                    .OrderByDescending(p => p.Pp)
                                    .Skip((currentPage - 1) * pageSize)
                                    .Take(pageSize)
                                    .ToListAsync();
        }
    }
}

