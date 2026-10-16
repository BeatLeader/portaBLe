using Microsoft.AspNetCore.Mvc;
using portaBLe.Services;

namespace portaBLe.Pages
{
    // Every score in one list, best pp first (or another sort), with filters. Reads Services/TopScoresCache (in memory, from
    // a file per database, built in the background when missing or outdated), never the scores table. Modded scores use their modifier's stars
    // and predicted acc (an SFS score shows the SFS stars), like the scores graph pages.
    public class TopScoresModel : BasePageModel
    {
        public const int PageSize = 50;

        public class Row
        {
            public int Position { get; set; }
            public string PlayerId { get; set; }
            public string PlayerName { get; set; }
            public int PlayerRank { get; set; }
            public string LeaderboardId { get; set; }
            public string MapName { get; set; }
            public string Mapper { get; set; }
            public string Difficulty { get; set; }
            public string Mode { get; set; }
            public float Stars { get; set; }
            public float Accuracy { get; set; }
            public float PredictedAcc { get; set; }
            public string Modifiers { get; set; }
            public float Pp { get; set; }
            public float AccPp { get; set; }
            public float PassPp { get; set; }
            public float TechPp { get; set; }
            public float Weight { get; set; }
            public int MapRank { get; set; }
            public bool FC { get; set; }
            public string Date { get; set; } = "";

            public float WeightedPp => Pp * Weight;
            public string MapDetails => Difficulty + (Mode != "Standard" ? " · " + Mode : "") + (string.IsNullOrEmpty(Mapper) ? "" : " · " + Mapper);
            public string ModsText => (Modifiers ?? "") + (FC ? "" : (string.IsNullOrEmpty(Modifiers) ? "✗" : " ✗"));
            // Score acc minus the Acc AI's predicted acc for the speed played (0 when no prediction)
            public float AccVsAi => PredictedAcc > 0 ? Accuracy - PredictedAcc : 0;
        }

        public List<Row> Rows { get; set; } = new();
        public List<string> Modes { get; set; } = new();
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
        public TopScoresCache.Snapshot? Cache { get; set; }
        public bool Building { get; set; }
        public string? BuildError { get; set; }

        [BindProperty(SupportsGet = true)] public int CurrentPage { get; set; } = 1;
        [BindProperty(SupportsGet = true)] public string SortBy { get; set; } = "pp";
        [BindProperty(SupportsGet = true)] public string Mode { get; set; } = "";
        [BindProperty(SupportsGet = true)] public float MinStars { get; set; } = 0;
        [BindProperty(SupportsGet = true)] public float MaxStars { get; set; } = 0;
        [BindProperty(SupportsGet = true)] public int MaxPlayerRank { get; set; } = 0;
        [BindProperty(SupportsGet = true)] public string PlayerSearch { get; set; } = "";
        [BindProperty(SupportsGet = true)] public string MapSearch { get; set; } = "";
        [BindProperty(SupportsGet = true)] public bool ModNone { get; set; } = true;
        [BindProperty(SupportsGet = true)] public bool ModSS { get; set; } = true;
        [BindProperty(SupportsGet = true)] public bool ModFS { get; set; } = true;
        [BindProperty(SupportsGet = true)] public bool ModSFS { get; set; } = true;

        public static readonly (string Key, string Label)[] SortOptions =
        {
            ("pp", "PP"), ("weighted", "Weighted PP"), ("accPP", "Acc PP"), ("passPP", "Pass PP"), ("techPP", "Tech PP"),
            ("acc", "Accuracy"), ("accVsAi", "Acc vs AI"), ("stars", "Stars"), ("date", "Newest"),
        };

        public TopScoresModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            var result = await TopScoresCache.TryGetAsync(() => GetDbContext());
            Cache = result.Data;
            Building = result.Building;
            BuildError = result.Error;
            if (Cache != null) Load(Cache);
        }

        private void Load(TopScoresCache.Snapshot data)
        {
            Modes = data.Modes.ToList();

            // Filters, checked once per player / map instead of once per score
            var mapOk = new bool[data.Maps.Length];
            string mapSearch = (MapSearch ?? "").Trim();
            for (int i = 0; i < data.Maps.Length; i++)
            {
                var m = data.Maps[i];
                mapOk[i] = (string.IsNullOrEmpty(Mode) || m.Mode == Mode)
                    && (mapSearch.Length == 0
                        || m.Id == mapSearch
                        || m.Name.Contains(mapSearch, StringComparison.OrdinalIgnoreCase)
                        || m.Mapper.Contains(mapSearch, StringComparison.OrdinalIgnoreCase));
            }
            var playerOk = new bool[data.Players.Length];
            string playerSearch = (PlayerSearch ?? "").Trim();
            for (int i = 0; i < data.Players.Length; i++)
            {
                var p = data.Players[i];
                playerOk[i] = (MaxPlayerRank <= 0 || (p.Rank > 0 && p.Rank <= MaxPlayerRank))
                    && (playerSearch.Length == 0 || p.Id == playerSearch || p.Name.Contains(playerSearch, StringComparison.OrdinalIgnoreCase));
            }
            var speedOk = new[] { ModNone, ModSS, ModFS, ModSFS };
            bool needsPrediction = SortBy == "accVsAi";

            int[]? order = SortBy switch
            {
                "weighted" => data.Order("weighted", i => data.Pp[i] * data.Weight(i)),
                "accPP" => data.Order("accPP", i => data.AccPp10[i]),
                "passPP" => data.Order("passPP", i => data.PassPp10[i]),
                "techPP" => data.Order("techPP", i => data.TechPp(i)),
                "acc" => data.Order("acc", i => data.Acc[i]),
                "accVsAi" => data.Order("accVsAi", i => data.PredictedAcc(i) > 0 ? data.Accuracy(i) - data.PredictedAcc(i) : float.MinValue),
                "stars" => data.Order("stars", i => data.Stars(i)),
                "date" => data.Order("date", i => data.Day[i]),
                _ => null, // the cache is already best pp first
            };

            int page = Math.Max(CurrentPage, 1);
            int skip = (page - 1) * PageSize;
            var picked = new List<int>(PageSize);
            int total = 0;
            for (int k = 0; k < data.Count; k++)
            {
                int i = order == null ? k : order[k];
                if (!mapOk[data.Map[i]] || !playerOk[data.Player[i]] || !speedOk[data.Speed(i)]) continue;
                if (MinStars > 0 || MaxStars > 0)
                {
                    float stars = data.Stars(i);
                    if (MinStars > 0 && stars < MinStars) continue;
                    if (MaxStars > 0 && stars > MaxStars) continue;
                }
                if (needsPrediction && !(data.PredictedAcc(i) > 0)) continue;
                if (total >= skip && picked.Count < PageSize) picked.Add(i);
                total++;
            }

            TotalCount = total;
            TotalPages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
            if (page > TotalPages)
            {
                // Past the last page (the filters changed): show the last one
                CurrentPage = TotalPages;
                Load(data);
                return;
            }
            CurrentPage = page;

            int position = skip;
            Rows = picked.Select(i =>
            {
                var p = data.Players[data.Player[i]];
                var m = data.Maps[data.Map[i]];
                return new Row
                {
                    Position = ++position,
                    PlayerId = p.Id,
                    PlayerName = p.Name,
                    PlayerRank = p.Rank,
                    LeaderboardId = m.Id,
                    MapName = m.Name,
                    Mapper = m.Mapper,
                    Difficulty = m.Difficulty,
                    Mode = m.Mode,
                    Stars = data.Stars(i),
                    Accuracy = data.Accuracy(i),
                    PredictedAcc = data.PredictedAcc(i),
                    Modifiers = data.ModifiersOf(i),
                    Pp = data.Pp[i],
                    AccPp = data.AccPp(i),
                    PassPp = data.PassPp(i),
                    TechPp = data.TechPp(i),
                    Weight = data.Weight(i),
                    MapRank = data.MapRank[i],
                    FC = data.FC(i),
                    Date = data.Date(i).ToString("yyyy-MM-dd"),
                };
            }).ToList();
        }

        // The current URL with another page number (keeps every filter and the database)
        public string PageUrl(int page)
        {
            var query = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(Request.Query.Where(q => !q.Key.Equals("CurrentPage", StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
            query["CurrentPage"] = page.ToString();
            return Request.Path + Microsoft.AspNetCore.Http.QueryString.Create(query);
        }
    }
}
