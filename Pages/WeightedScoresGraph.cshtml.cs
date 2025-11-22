using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using static portaBLe.Pages.LeaderboardModel;

namespace portaBLe.Pages
{
    public class WeightedScoresGraphModel : PageModel
    {
        private readonly AppContext _context;
        public string WeightedJsonData { get; set; }

        public WeightedScoresGraphModel(AppContext context)
        {
            _context = context;
        }

        public async Task OnGetAsync()
        {
            var points = await _context.Scores
                .Where(s => s.Weight > 0)
                .Select(s => new
                {
                    x = (double)s.Leaderboard.Stars,
                    pp = (double)s.Pp * s.Weight,
                    acc = (double)s.Accuracy,
                    accPP = (double)s.AccPP * s.Weight,
                    passPP = (double)s.PassPP * s.Weight,
                    techPP = (double)s.TechPP * s.Weight
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

            WeightedJsonData = JsonSerializer.Serialize(filteredPoints);
        }
    }
}
