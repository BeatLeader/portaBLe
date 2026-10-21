using Microsoft.AspNetCore.Mvc;
using portaBLe.Services;
using System.Globalization;

namespace portaBLe.Pages
{
    // The top X accuracies of every map (x: stars, y: acc). Reads the Top Scores cache (Services/TopScoresCache), never the
    // scores table. Each map's speed versions are ranked separately (an SFS score is placed at the SFS stars), and the
    // filters apply before ranking: "top 10" with max player rank 1000 is each map's best 10 among the top 1000 players.
    // Every point is sent and drawn (no merging of overlapping points). A second database can be picked: its rolling average
    // (same filters) is drawn on the same graph.
    public class TopAccGraphModel : BasePageModel
    {
        public const int MaxTopPerMap = 1000;

        [BindProperty(SupportsGet = true)] public int TopPerMap { get; set; } = 10;
        [BindProperty(SupportsGet = true)] public string Mode { get; set; } = "Standard";
        [BindProperty(SupportsGet = true)] public int MaxPlayerRank { get; set; } = 0;
        [BindProperty(SupportsGet = true)] public string Since { get; set; } = "";
        [BindProperty(SupportsGet = true)] public bool ModNone { get; set; } = true;
        [BindProperty(SupportsGet = true)] public bool ModSS { get; set; } = false;
        [BindProperty(SupportsGet = true)] public bool ModFS { get; set; } = false;
        [BindProperty(SupportsGet = true)] public bool ModSFS { get; set; } = false;
        [BindProperty(SupportsGet = true)] public int RollingWindow { get; set; } = 400;
        // Another database whose rolling average is drawn on the same graph ("" for none)
        [BindProperty(SupportsGet = true)] public string CompareDb { get; set; } = "";

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
            var main = Collect(result.Data, details: true);

            // The comparison database: only its points' stars and acc, for its rolling average
            object? compare = null;
            if (!string.IsNullOrEmpty(CompareDb) && CompareDb != SelectedDatabase)
            {
                var other = await TopScoresCache.TryGetAsync(() => GetDbContext(CompareDb));
                string name = AvailableDatabases.FirstOrDefault(d => d.FileName == CompareDb)?.Name ?? CompareDb;
                if (other.Data == null)
                {
                    compare = new { name, building = other.Building, error = other.Error };
                }
                else
                {
                    var points = Collect(other.Data, details: false);
                    compare = new { name, building = other.Building, count = points.X.Count, x = points.X, y = points.Y };
                }
            }

            return new JsonResult(new
            {
                building = result.Building,
                name = AvailableDatabases.FirstOrDefault(d => d.FileName == SelectedDatabase)?.Name ?? SelectedDatabase,
                count = main.X.Count,
                maps = main.Maps,
                players = main.Players,
                x = main.X,
                y = main.Y,
                map = main.Map,
                player = main.Player,
                rank = main.Rank,
                speed = main.Speed,
                compare,
            });
        }

        private class Points
        {
            public List<float> X = new(), Y = new();
            public List<int> Map = new(), Player = new(), Rank = new(), Speed = new();
            public List<object[]> Maps = new();
            public List<string> Players = new();
        }

        // Each map + speed's best `TopPerMap` accuracies that pass the filters. details: also who / which map, for the hover
        private Points Collect(TopScoresCache.Snapshot d, bool details)
        {
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
            var result = new Points();

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

                result.X.Add(MathF.Round(stars, 3));
                result.Y.Add(MathF.Round(d.Accuracy(i) * 100, 3));
                if (!details) continue;

                if (!mapIds.TryGetValue(map, out int mi))
                {
                    mi = result.Maps.Count;
                    mapIds[map] = mi;
                    var m = d.Maps[map];
                    result.Maps.Add(new object[] { m.Id, m.Name, m.Difficulty, m.Mapper });
                }
                if (!playerIds.TryGetValue(player, out int pi))
                {
                    pi = result.Players.Count;
                    playerIds[player] = pi;
                    result.Players.Add(d.Players[player].Name);
                }
                result.Map.Add(mi);
                result.Player.Add(pi);
                result.Rank.Add(rank);
                result.Speed.Add(speed);
            }
            return result;
        }
    }
}
