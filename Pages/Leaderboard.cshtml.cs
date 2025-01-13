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
        public Leaderboard? CompareLeaderboard { get; set; }
        public List<Score> Scores { get; set; }
        public List<Score>? CompareScores { get; set; }
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }
        public int TotalScores { get; set; }
        public int CompareCurrentPage { get; set; } = 1;
        public int CompareTotalPages { get; set; }
        public int CompareTotalScores { get; set; }
    
        // Properties to hold the chart data
        public ICollection<ScoreGraphEntry> ScoreGraphEntries { get; set; }
        public ICollection<ScoreGraphEntry>? CompareScoreGraphEntries { get; set; }

        public LeaderboardModel(AppContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> OnGetAsync(string id, string compareId = null, int currentPage = 1, int compareCurrentPage = 1)
        {
            Leaderboard = await _context.Leaderboards.FirstOrDefaultAsync(l => l.Id == id);

            if (Leaderboard == null)
            {
                return NotFound();
            }

            int pageSize = 10;
            CurrentPage = currentPage;
            CompareCurrentPage = compareCurrentPage;

            TotalScores = await _context.Scores.Where(s => s.LeaderboardId == id).CountAsync();
            TotalPages = (int)System.Math.Ceiling(TotalScores / (double)pageSize);

            Scores = await _context.Scores
                                   .Where(s => s.LeaderboardId == id)
                                   .Include(s => s.Player)
                                   .OrderByDescending(s => s.Pp)
                                   .Skip((currentPage - 1) * pageSize)
                                   .Take(pageSize)
                                   .ToListAsync();

            // Fetch data for first leaderboard chart
            ScoreGraphEntries = await _context.Scores
                .Where(s => s.LeaderboardId == id)
                .Select(s => new ScoreGraphEntry
                {
                    playerId = s.PlayerId,
                    timepost = s.Timepost,
                    weight = s.Weight,
                    rank = s.Rank,
                    modifiers = s.Modifiers,
                    playerRank = s.Player.Rank,
                    playerName = s.Player.Name,
                    accuracy = s.Accuracy * 100f,
                    pp = s.Pp,
                    playerAvatar = s.Player.Avatar,
                    fc = s.FC,
                    fcAcc = s.FCAcc
                })
                .ToListAsync();

            // Handle comparison leaderboard if compareId is provided
            if (!string.IsNullOrEmpty(compareId))
            {
                CompareLeaderboard = await _context.Leaderboards.FirstOrDefaultAsync(l => l.Id == compareId);
                
                if (CompareLeaderboard != null)
                {
                    CompareTotalScores = await _context.Scores.Where(s => s.LeaderboardId == compareId).CountAsync();
                    CompareTotalPages = (int)System.Math.Ceiling(CompareTotalScores / (double)pageSize);

                    CompareScores = await _context.Scores
                                       .Where(s => s.LeaderboardId == compareId)
                                       .Include(s => s.Player)
                                       .OrderByDescending(s => s.Pp)
                                       .Skip((compareCurrentPage - 1) * pageSize)
                                       .Take(pageSize)
                                       .ToListAsync();

                    // Fetch data for comparison leaderboard chart
                    CompareScoreGraphEntries = await _context.Scores
                        .Where(s => s.LeaderboardId == compareId)
                        .Select(s => new ScoreGraphEntry
                        {
                            playerId = s.PlayerId,
                            timepost = s.Timepost,
                            weight = s.Weight,
                            rank = s.Rank,
                            modifiers = s.Modifiers,
                            playerRank = s.Player.Rank,
                            playerName = s.Player.Name,
                            accuracy = s.Accuracy * 100f,
                            pp = s.Pp,
                            playerAvatar = s.Player.Avatar,
                            fc = s.FC,
                            fcAcc = s.FCAcc
                        })
                        .ToListAsync();
                }
            }

            return Page();
        }

        public class ScoreGraphEntry
        {
            public string playerId { get; set; }
            public float weight { get; set; }
            public int rank { get; set; }
            public int timepost { get; set; }
            public string modifiers { get; set; }
            public int playerRank { get; set; }
            public string playerName { get; set; }
            public string playerAvatar { get; set; }
            public float accuracy { get; set; }
            public float pp { get; set; }
            public bool fc { get; set; }
            public float fcAcc { get; set; }
        }
    }
}
