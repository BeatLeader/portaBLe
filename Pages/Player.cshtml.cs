using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace portaBLe.Pages
{
    public class PlayerModel : BasePageModel
    {
        public Player Player { get; set; }
        public List<Score> Scores { get; set; }
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }

        public PlayerModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task<IActionResult> OnGetAsync(string id, int currentPage = 1, string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);

            using var context = (Services.DynamicDbContext)GetDbContext();

            Player = await context.Players.FirstOrDefaultAsync(p => p.Id == id);

            if (Player == null)
            {
                return NotFound();
            }

            int pageSize = 10;
            CurrentPage = currentPage;

            var totalScores = await context.Scores.CountAsync(s => s.PlayerId == id);
            TotalPages = (int)System.Math.Ceiling(totalScores / (double)pageSize);

            Scores = await context.Scores
                                   .Include(s => s.Leaderboard).ThenInclude(l => l.ModifiersRating)
                                   .Where(s => s.PlayerId == id)
                                   .OrderByDescending(s => s.Pp)
                                   .Skip((currentPage - 1) * pageSize)
                                   .Take(pageSize)
                                   .ToListAsync();

            return Page();
        }

        // Stars of the version the score was set on (speed modifier stars with SS / FS / SF)
        public static float Stars(Score score)
        {
            var mod = score.Leaderboard.ModifiersRating;
            var modifiers = score.Modifiers ?? "";
            if (mod != null && modifiers.Contains("SF")) return mod.SFStars;
            if (mod != null && modifiers.Contains("FS")) return mod.FSStars;
            if (mod != null && modifiers.Contains("SS")) return mod.SSStars;
            return score.Leaderboard.Stars;
        }
    }

}
