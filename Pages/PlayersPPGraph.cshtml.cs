using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using portaBLe.Services;
using System.Text.Json;

namespace portaBLe.Pages
{
    public class PlayersPPGraphModel : BasePageModel
    {
        public string TriangleJsonData { get; set; }

        [BindProperty(SupportsGet = true)]
        public int MaxPlayerRank { get; set; } = 0;

        [BindProperty(SupportsGet = true)]
        public int MinRankedPlayCount { get; set; } = 0;

        [BindProperty(SupportsGet = true)]
        public string PlayerId { get; set; } = string.Empty;

        public PlayersPPGraphModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public class PlayerPoint
        {
            public string player { get; set; } = string.Empty;
            public string id { get; set; } = string.Empty;
            public double accPP { get; set; }
            public double passPP { get; set; }
            public double techPP { get; set; }
        }

        public async Task OnGetAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            var playerPoints = await GetFilteredPointsAsync(MaxPlayerRank, MinRankedPlayCount, PlayerId);
            TriangleJsonData = JsonSerializer.Serialize(playerPoints);
        }

        // Handler for AJAX requests: ?handler=Data
        public async Task<IActionResult> OnGetDataAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            var playerPoints = await GetFilteredPointsAsync(MaxPlayerRank, MinRankedPlayCount, PlayerId);
            return new JsonResult(playerPoints);
        }

        private async Task<List<PlayerPoint>> GetFilteredPointsAsync(int maxPlayerRank, int minRankedPlayCount, string playerId)
        {
            using var context = (Services.DynamicDbContext)GetDbContext();

            var players = await context.Players
                .Where(s => s != null
                            && (maxPlayerRank == 0 || s.Rank <= maxPlayerRank)
                            && s.RankedPlayCount >= minRankedPlayCount
                            && (string.IsNullOrWhiteSpace(playerId) || s.Id == playerId))
                .ToListAsync();

            if (players == null || players.Count == 0)
                return new List<PlayerPoint>();

            var result = players.Select(s =>
            {
                if (s.AccPp + s.TechPp + s.PassPp == 0)
                    return null;

                return new PlayerPoint
                {
                    player = s.Name,
                    id = s.Id,
                    accPP = s.AccPp,
                    techPP = s.TechPp,
                    passPP = s.PassPp,
                };
            })
            .Where(x => x != null)
            .Select(x => x!)
            .ToList();

            return result;
        }
    }
}
