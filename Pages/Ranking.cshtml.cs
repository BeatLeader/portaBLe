using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Pages;
using portaBLe.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace portaBLe
{
    public class RankingModel : BasePageModel
    {
        public List<Player> Players { get; set; }
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }

        [BindProperty(SupportsGet = true)]
        public string SearchString { get; set; }

        [BindProperty(SupportsGet = true)]
        public string SortBy { get; set; } = "rank";

        // Style filter: comma separated PlayerStyles.All keys, excluded ones with a leading "-" (PlayerStyle.Matches). Empty = all players.
        [BindProperty(SupportsGet = true)]
        public string Style { get; set; }

        public Dictionary<string, PlayerStyles.PlayerStyle> Styles { get; set; } = new();

        // Outliers of the page's players, like LeaderboardsRefresh.Outliers with a lower threshold: with enough scores, the top
        // scores above the first drop of over OutlierThreshold% to the next score
        public const float OutlierThreshold = 4f;
        private const int OutlierMinScores = 100;
        private const int OutlierTopScores = 10;
        public Dictionary<string, int> OutlierCounts { get; set; } = new();

        public RankingModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(int currentPage = 1, string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);

            using var context = (Services.DynamicDbContext)GetDbContext();

            int pageSize = 50;
            CurrentPage = currentPage;

            // Ranked players with pp
            var query = context.Players.Where(p => p.Rank > 0 && p.Pp > 0);

            if (!string.IsNullOrEmpty(SearchString))
            {
                query = query.Where(p => EF.Functions.Like(p.Name.ToLower(), $"%{SearchString.ToLower()}%"));
            }

            query = SortBy switch
            {
                "topPP" => query.OrderByDescending(p => p.TopPp),
                "accPP" => query.OrderByDescending(p => p.AccPp),
                "passPP" => query.OrderByDescending(p => p.PassPp),
                "techPP" => query.OrderByDescending(p => p.TechPp),
                "rank" or _ => query.OrderBy(p => p.Rank)
            };

            Styles = await PlayerStyles.ForDatabaseAsync(SelectedDatabase, () => (Services.DynamicDbContext)GetDbContext());

            var (include, exclude) = PlayerStyles.ParseKeys(Style);
            if (include.Length == 0 && exclude.Length == 0)
            {
                Style = null;
                var totalRecords = await query.CountAsync();
                TotalPages = (int)System.Math.Ceiling(totalRecords / (double)pageSize);

                Players = await query
                                        .Skip((currentPage - 1) * pageSize)
                                        .Take(pageSize)
                                        .ToListAsync();
            }
            else
            {
                Style = string.Join(",", include.Concat(exclude.Select(k => "-" + k)));
                // Styles are computed in memory, so the page is picked from the ordered ids
                var ids = (await query.Select(p => p.Id).ToListAsync())
                    .Where(id => Styles.TryGetValue(id, out var style) && style.Matches(include, exclude))
                    .ToList();
                TotalPages = (int)System.Math.Ceiling(ids.Count / (double)pageSize);

                var pageIds = ids.Skip((currentPage - 1) * pageSize).Take(pageSize).ToList();
                var players = await context.Players.Where(p => pageIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
                Players = pageIds.Select(id => players[id]).ToList();
            }

            var playerIds = Players.Select(p => p.Id).ToList();
            var scores = await context.Scores.Where(s => playerIds.Contains(s.PlayerId) && s.Pp > 0)
                .Select(s => new { s.PlayerId, s.Pp }).ToListAsync();
            OutlierCounts = scores.GroupBy(s => s.PlayerId)
                .ToDictionary(g => g.Key, g => g.Count() < OutlierMinScores ? 0 : CountOutliers(g.Select(s => s.Pp)));
        }

        private static int CountOutliers(IEnumerable<float> pp)
        {
            var top = pp.OrderByDescending(p => p).Take(OutlierTopScores).ToList();
            for (int i = 0; i < top.Count - 1; i++)
            {
                if ((top[i] - top[i + 1]) / top[i + 1] * 100f > OutlierThreshold) return i + 1;
            }
            return 0;
        }

        public int GetOutlierCount(Player player) => OutlierCounts.GetValueOrDefault(player.Id);

        // Total and top pp always show; sorted by acc, pass or tech pp, that column shows too
        public bool ShowsPartColumn => SortBy is "accPP" or "passPP" or "techPP";

        public PlayerStyles.PlayerStyle GetStyle(Player player) => Styles.GetValueOrDefault(player.Id);

        public string GetSortColumnHeader()
        {
            return SortBy switch
            {
                "topPP" => "Top PP",
                "accPP" => "Acc PP",
                "passPP" => "Pass PP",
                "techPP" => "Tech PP",
                "rank" or _ => "Total PP"
            };
        }

        public float GetSortColumnValue(Player player)
        {
            return SortBy switch
            {
                "topPP" => player.TopPp,
                "accPP" => player.AccPp,
                "passPP" => player.PassPp,
                "techPP" => player.TechPp,
                "rank" or _ => player.Pp
            };
        }
    }
}

