using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using portaBLe;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace portaBLe.Pages
{
    public class LeaderboardModel : PageModel
    {
        private readonly AppContext _context;
        public Leaderboard Leaderboard { get; set; }
        public List<Score> Scores { get; set; }
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }

        public LeaderboardModel(AppContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> OnGetAsync(string id, int currentPage = 1)
        {
            Leaderboard = await _context.Leaderboards.FirstOrDefaultAsync(l => l.Id == id);

            if (Leaderboard == null)
            {
                return NotFound();
            }

            int pageSize = 10; // Set the number of items per page
            CurrentPage = currentPage;

            var totalScores = await _context.Scores.Where(s => s.LeaderboardId == id).CountAsync();
            TotalPages = (int)System.Math.Ceiling(totalScores / (double)pageSize);

            Scores = await _context.Scores
                                   .Where(s => s.LeaderboardId == id)
                                   .Include(s => s.Player)
                                   .OrderByDescending(s => s.Pp)
                                   .Skip((currentPage - 1) * pageSize)
                                   .Take(pageSize)
                                   .ToListAsync();

            return Page();
        }
    }
}
