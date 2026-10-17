using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using portaBLe.Services;

namespace portaBLe.Pages
{
    // Every player's acc / tech / pass pp as a 3D scatter. The page loads empty and fetches the points (?handler=Data) with the
    // filters, so opening it never waits on the query. Only the five columns drawn are read, and sent as columns.
    public class PlayersPPGraphModel : BasePageModel
    {
        // 0 = unfiltered; otherwise ranked players (rank 1..MaxPlayerRank) only
        [BindProperty(SupportsGet = true)]
        public int MaxPlayerRank { get; set; } = 0;

        [BindProperty(SupportsGet = true)]
        public int MinRankedPlayCount { get; set; } = 0;

        [BindProperty(SupportsGet = true)]
        public string PlayerId { get; set; } = string.Empty;

        public PlayersPPGraphModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
        }

        // Handler for AJAX requests: ?handler=Data
        public async Task<IActionResult> OnGetDataAsync(int maxPlayerRank = 0, int minRankedPlayCount = 0, string playerId = "", string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            using var context = (Services.DynamicDbContext)GetDbContext();
            string id = (playerId ?? "").Trim();

            var query = context.Players.AsNoTracking().Where(p => p.RankedPlayCount >= minRankedPlayCount);
            if (maxPlayerRank > 0) query = query.Where(p => p.Rank > 0 && p.Rank <= maxPlayerRank);
            if (id.Length > 0) query = query.Where(p => p.Id == id);

            var rows = await query
                .Where(p => p.AccPp + p.TechPp + p.PassPp > 0)
                .Select(p => new { p.Id, p.Name, p.AccPp, p.TechPp, p.PassPp })
                .ToListAsync();

            static float R(float v) => float.IsFinite(v) ? MathF.Round(v, 1) : 0f;
            return new JsonResult(new
            {
                count = rows.Count,
                id = rows.Select(r => r.Id).ToArray(),
                name = rows.Select(r => r.Name ?? "").ToArray(),
                accPP = rows.Select(r => R(r.AccPp)).ToArray(),
                techPP = rows.Select(r => R(r.TechPp)).ToArray(),
                passPP = rows.Select(r => R(r.PassPp)).ToArray(),
            });
        }
    }
}
