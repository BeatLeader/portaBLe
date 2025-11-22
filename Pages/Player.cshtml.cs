using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace portaBLe.Pages
{
    public class PlayerModel : PageModel
    {
        private readonly AppContext _context;
        public Player Player { get; set; }
        public List<Score> Scores { get; set; }
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }

        public PlayerModel(AppContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> OnGetAsync(string id, int currentPage = 1)
        {
            Player = await _context.Players.FirstOrDefaultAsync(p => p.Id == id);

            if (Player == null)
            {
                return NotFound();
            }

            int pageSize = 10; // Set the number of items per page
            CurrentPage = currentPage;

            var totalScores = await _context.Scores.CountAsync(s => s.PlayerId == id);
            TotalPages = (int)System.Math.Ceiling(totalScores / (double)pageSize);

            Scores = await _context.Scores
                                   .Include(s => s.Leaderboard)
                                   .Where(s => s.PlayerId == id)
                                   .OrderByDescending(s => s.Pp)
                                   .Skip((currentPage - 1) * pageSize)
                                   .Take(pageSize)
                                   .ToListAsync();

            return Page();
        }
    }

}
