using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace portaBLe.Pages
{
    public class LeaderboardsModel : BasePageModel
    {
        public List<Leaderboard> Leaderboards { get; set; }
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }
        public int TotalRecords { get; set; }
        public List<string> ModeNames { get; set; }

        [BindProperty(SupportsGet = true)]
        public string SearchString { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool SortDescending { get; set; } = true;

        [BindProperty(SupportsGet = true)]
        public string SortBy { get; set; } = "Stars";

        [BindProperty(SupportsGet = true)]
        public string ModeName { get; set; }

        public LeaderboardsModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task OnGetAsync(int currentPage = 1, string db = null)
        {
            await InitializeDatabaseSelectionAsync(db);

            using var context = (Services.DynamicDbContext)GetDbContext();

            ModeNames = await context.Leaderboards
                .Select(l => l.ModeName)
                .Distinct()
                .OrderBy(m => m)
                .ToListAsync();

            IQueryable<Leaderboard> leaderboardQuery = context.Leaderboards;

            if (!string.IsNullOrEmpty(SearchString))
            {
                string pattern = $"%{SearchString.ToLower()}%";
                leaderboardQuery = leaderboardQuery.Where(l => EF.Functions.Like(l.Name.ToLower(), pattern)
                    || EF.Functions.Like(l.Mapper.ToLower(), pattern));
            }

            if (!string.IsNullOrEmpty(ModeName))
            {
                leaderboardQuery = leaderboardQuery.Where(l => l.ModeName == ModeName);
            }

            // Apply sorting based on SortBy parameter
            leaderboardQuery = SortBy switch
            {
                "PassRating" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.PassRating) : leaderboardQuery.OrderBy(l => l.PassRating),
                "TechRating" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.TechRating) : leaderboardQuery.OrderBy(l => l.TechRating),
                "AccRating" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.AccRating) : leaderboardQuery.OrderBy(l => l.AccRating),
                // Maps without a worth go last either way
                "Worth" => SortDescending
                    ? leaderboardQuery.OrderBy(l => l.Worth == null).ThenByDescending(l => l.Worth)
                    : leaderboardQuery.OrderBy(l => l.Worth == null).ThenBy(l => l.Worth),
                "WorthRank20" => SortNullsLast(leaderboardQuery, l => l.WorthRank20),
                "WorthRank100" => SortNullsLast(leaderboardQuery, l => l.WorthRank100),
                "WorthRank1000" => SortNullsLast(leaderboardQuery, l => l.WorthRank1000),
                "WorthRank3000" => SortNullsLast(leaderboardQuery, l => l.WorthRank3000),
                "WorthRank10000" => SortNullsLast(leaderboardQuery, l => l.WorthRank10000),
                _ => SortDescending ? leaderboardQuery.OrderByDescending(l => l.Stars) : leaderboardQuery.OrderBy(l => l.Stars),
            };

            int pageSize = 25;
            TotalRecords = await leaderboardQuery.CountAsync();
            TotalPages = (int)System.Math.Ceiling(TotalRecords / (double)pageSize);
            CurrentPage = System.Math.Clamp(currentPage, 1, System.Math.Max(TotalPages, 1));

            Leaderboards = await leaderboardQuery.Skip((CurrentPage - 1) * pageSize).Take(pageSize).ToListAsync();
        }

        // Maps without a value go last either way
        private IQueryable<Leaderboard> SortNullsLast(IQueryable<Leaderboard> query, System.Linq.Expressions.Expression<System.Func<Leaderboard, float?>> key)
        {
            var withNullsLast = query.OrderBy(System.Linq.Expressions.Expression.Lambda<System.Func<Leaderboard, bool>>(
                System.Linq.Expressions.Expression.Equal(key.Body, System.Linq.Expressions.Expression.Constant(null, typeof(float?))), key.Parameters));
            return SortDescending ? withNullsLast.ThenByDescending(key) : withNullsLast.ThenBy(key);
        }
    }
}
