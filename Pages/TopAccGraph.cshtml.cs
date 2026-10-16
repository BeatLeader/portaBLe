using Microsoft.AspNetCore.Mvc;
using portaBLe.Services;
using System.Globalization;

namespace portaBLe.Pages
{
    // The top X accuracies of every map (x: stars, y: acc). Reads the Top Scores cache (Services/TopScoresCache), never the
    // scores table. Each map's speed versions are ranked separately (an SFS score is placed at the SFS stars), and the
    // filters apply before ranking: "top 10" with max player rank 1000 is each map's best 10 among the top 1000 players.
    // Every point is sent and drawn (no merging of overlapping points).
    public class TopAccGraphModel : BasePageModel
    {
        public const int MaxTopPerMap = 1000;

        [BindProperty(SupportsGet = true)] public int TopPerMap { get; set; } = 10;
        [BindProperty(SupportsGet = true)] public string Mode { get; set; } = "Standard";
        [BindProperty(SupportsGet = true)] public int MaxPlayerRank { get; set; } = 0;
        [BindProperty(SupportsGet = true)] public string Since { get; set; } = "";
        [BindProperty(SupportsGet = true)] public bool ModNone { get; set; } = true;
        [BindProperty(SupportsGet = true)] public bool ModSS { get; set; } = true;
        [BindProperty(SupportsGet = true)] public bool ModFS { get; set; } = true;
        [BindProperty(SupportsGet = true)] public bool ModSFS { get; set; } = true;
        [BindProperty(SupportsGet = true)] public int RollingWindow { get; set; } = 400;

        public List<string> Modes { get; set; } = new();

        public TopAccGraphModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            var result = await TopScoresCache.TryGetAsync(() => GetDbContext());
            Modes = result.Data?.Modes.ToList() ?? new List<string> { "Standard" };
        }

        public async Task<IActionResult> OnGetDataAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            var result = await TopScoresCache.TryGetAsync(() => GetDbContext());
            if (result.Data == null) return new JsonResult(new { building = result.Building, error = result.Error });
            var d = result.Data;

            int top = Math.Clamp(TopPerMap, 1, MaxTopPerMap);
            var speedOk = new[] { ModNone, ModSS, ModFS, ModSFS };
            var mapOk = d.Maps.Select(m => string.IsNullOrEmpty(Mode) || m.Mode == Mode).ToArray();
            var playerOk = d.Players.Select(p => MaxPlayerRank <= 0 || (p.Rank > 0 && p.Rank <= MaxPlayerRank)).ToArray();
            int minDay = DateTime.TryParse(Since, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var since)
                ? TopScoresCache.DayNumber(since) : int.MinValue;

            // Highest acc first (shared with the Top Scores page's acc sort), taking each map + speed's first `top` scores
            var order = d.Order("acc", i => d.Acc[i]);
            var taken = new int[d.Maps.Length * 4];
            var mapIds = new Dictionary<int, int>();
            var playerIds = new Dictionary<int, int>();
            var maps = new List<object[]>();
            var players = new List<string>();
            var x = new List<float>();
            var y = new List<float>();
            var mapOf = new List<int>();
            var playerOf = new List<int>();
            var rankOf = new List<int>();
            var speedOf = new List<int>();

            foreach (int i in order)
            {
                int map = d.Map[i], speed = d.Speed(i), player = d.Player[i];
                if (!speedOk[speed] || !mapOk[map] || !playerOk[player]) continue;
                if (minDay != int.MinValue && d.Day[i] < minDay) continue;
                float stars = d.Stars(i);
                if (!(stars > 0)) continue;
                int group = map * 4 + speed;
                if (taken[group] >= top) continue;
                int rank = ++taken[group];

                if (!mapIds.TryGetValue(map, out int mi))
                {
                    mi = maps.Count;
                    mapIds[map] = mi;
                    var m = d.Maps[map];
                    maps.Add(new object[] { m.Id, m.Name, m.Difficulty, m.Mapper });
                }
                if (!playerIds.TryGetValue(player, out int pi))
                {
                    pi = players.Count;
                    playerIds[player] = pi;
                    players.Add(d.Players[player].Name);
                }
                x.Add(MathF.Round(stars, 3));
                y.Add(MathF.Round(d.Accuracy(i) * 100, 3));
                mapOf.Add(mi);
                playerOf.Add(pi);
                rankOf.Add(rank);
                speedOf.Add(speed);
            }

            return new JsonResult(new
            {
                building = result.Building,
                count = x.Count,
                maps,
                players,
                x,
                y,
                map = mapOf,
                player = playerOf,
                rank = rankOf,
                speed = speedOf,
            });
        }
    }
}
