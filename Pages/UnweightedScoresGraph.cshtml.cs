using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;
using static portaBLe.Pages.LeaderboardModel;

namespace portaBLe.Pages
{
    public class UnweightedScoresGraphModel : PageModel
    {
        private readonly AppContext _context;
        public string UnweightedJsonData { get; set; }

        public UnweightedScoresGraphModel(AppContext context)
        {
            _context = context;
        }

        public async Task OnGetAsync()
        {
            var points = await _context.Scores
                .Select(s => new
                {
                    x = (double)s.Leaderboard.Stars,
                    pp = (double)s.Pp,
                    acc = (double)s.Accuracy,
                    accPP = (double)s.AccPP ,
                    passPP = (double)s.PassPP,
                    techPP = (double)s.TechPP
                })
                .ToListAsync();

            int numXBins = 500;
            int numYBins = 500;

            double minX = points.Min(p => p.x);
            double maxX = points.Max(p => p.x);
            double minY = points.Min(p => p.pp);
            double maxY = points.Max(p => p.pp);

            double xBinSize = (maxX - minX) / numXBins;
            double yBinSize = (maxY - minY) / numYBins;

            var grid = new bool[numXBins, numYBins];
            var filteredPoints = new List<object>();

            foreach (var p in points)
            {
                int xIndex = Math.Min((int)((p.x - minX) / xBinSize), numXBins - 1);
                int yIndex = Math.Min((int)((p.pp - minY) / yBinSize), numYBins - 1);

                if (!grid[xIndex, yIndex])
                {
                    filteredPoints.Add(p);
                    grid[xIndex, yIndex] = true;
                }
            }

            UnweightedJsonData = JsonSerializer.Serialize(filteredPoints);
        }
    }
}
