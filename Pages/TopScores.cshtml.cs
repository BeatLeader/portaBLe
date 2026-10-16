using Microsoft.AspNetCore.Mvc;
using portaBLe.Services;

namespace portaBLe.Pages
{
    // Every score in one list, best pp first (or another sort), with filters. Reads Services/TopScoresCache (a file per
    // database, rebuilt when the database changes), never the database itself. Modded scores use their modifier's stars
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
            public int Timepost { get; set; }

            public float WeightedPp => Pp * Weight;
            public string MapDetails => Difficulty + (Mode != "Standard" ? " · " + Mode : "") + (string.IsNullOrEmpty(Mapper) ? "" : " · " + Mapper);
            public string ModsText => (Modifiers ?? "") + (FC ? "" : (string.IsNullOrEmpty(Modifiers) ? "✗" : " ✗"));
            // Score acc minus the Acc AI's predicted acc for the speed played (0 when no prediction)
            public float AccVsAi => PredictedAcc > 0 ? Accuracy - PredictedAcc : 0;
            public string Date => Timepost > 0 ? DateTimeOffset.FromUnixTimeSeconds(Timepost).UtcDateTime.ToString("yyyy-MM-dd") : "";
        }

        public List<Row> Rows { get; set; } = new();
        public List<string> Modes { get; set; } = new();
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
        public TopScoresCache.Snapshot? Cache { get; set; }

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
            await LoadAsync(rebuild: false);
        }

        // "Rebuild cache" button: builds the file again from the database, then shows the same filters
        public async Task<IActionResult> OnPostRebuildAsync(string db = null, string returnUrl = null)
        {
            await InitializeDatabaseSelectionAsync(db);
            using (var context = GetDbContext())
            {
                await TopScoresCache.GetAsync(context, rebuild: true);
            }
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
            return RedirectToPage(new { db = SelectedDatabase });
        }

        private async Task LoadAsync(bool rebuild)
        {
            TopScoresCache.Snapshot data;
            using (var context = GetDbContext())
            {
                data = await TopScoresCache.GetAsync(context, rebuild);
            }
            Cache = data;
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
                "weighted" => data.Order("weighted", r => r.Pp * r.Weight),
                "accPP" => data.Order("accPP", r => r.AccPp),
                "passPP" => data.Order("passPP", r => r.PassPp),
                "techPP" => data.Order("techPP", r => r.TechPp),
                "acc" => data.Order("acc", r => r.Accuracy),
                "accVsAi" => data.Order("accVsAi", r => r.PredictedAcc > 0 ? r.Accuracy - r.PredictedAcc : float.MinValue),
                "stars" => data.Order("stars", r => r.Stars),
                "date" => data.Order("date", r => r.Timepost),
                _ => null, // the file is already best pp first
            };

            int skip = (Math.Max(CurrentPage, 1) - 1) * PageSize;
            var picked = new List<int>(PageSize);
            int total = 0;
            int count = data.Scores.Length;
            for (int k = 0; k < count; k++)
            {
                int i = order == null ? k : order[k];
                var r = data.Scores[i];
                if (!mapOk[r.Map] || !playerOk[r.Player] || !speedOk[data.ModsSpeed[r.Mods]]) continue;
                if (MinStars > 0 && r.Stars < MinStars) continue;
                if (MaxStars > 0 && r.Stars > MaxStars) continue;
                if (needsPrediction && !(r.PredictedAcc > 0)) continue;
                if (total >= skip && picked.Count < PageSize) picked.Add(i);
                total++;
            }

            TotalCount = total;
            TotalPages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
            if (CurrentPage > TotalPages || CurrentPage < 1)
            {
                // Past the last page (filters changed): show the last one
                CurrentPage = Math.Clamp(CurrentPage, 1, TotalPages);
                if (total > 0 && picked.Count == 0) { await LoadAsync(false); return; }
            }

            int position = (CurrentPage - 1) * PageSize;
            Rows = picked.Select(i =>
            {
                var r = data.Scores[i];
                var p = data.Players[r.Player];
                var m = data.Maps[r.Map];
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
                    Stars = r.Stars,
                    Accuracy = r.Accuracy,
                    PredictedAcc = r.PredictedAcc,
                    Modifiers = data.Modifiers[r.Mods],
                    Pp = r.Pp,
                    AccPp = r.AccPp,
                    PassPp = r.PassPp,
                    TechPp = r.TechPp,
                    Weight = r.Weight,
                    MapRank = r.MapRank,
                    FC = r.FC,
                    Timepost = r.Timepost,
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
