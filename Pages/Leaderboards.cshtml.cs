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

        // Range filter: maps with RangeBy (Stars, PassRating, AccRating or TechRating) from RangeMin to RangeMax, either
        // side left empty = no limit on it
        [BindProperty(SupportsGet = true)]
        public string RangeBy { get; set; } = "Stars";

        [BindProperty(SupportsGet = true)]
        public float? RangeMin { get; set; }

        [BindProperty(SupportsGet = true)]
        public float? RangeMax { get; set; }

        public bool HasRange => RangeMin != null || RangeMax != null;

        // Speed modifier whose stars, ratings and worth are shown, filtered and sorted on: "SS", "FS", "SF", or null for
        // none. With one, maps without modifier ratings are left out.
        [BindProperty(SupportsGet = true)]
        public string? Modifier { get; set; }

        public static readonly string[] Modifiers = { "SS", "FS", "SF" };

        // The Leaderboard page's name of the modifier (SF is SFS there)
        public string? LeaderboardPageModifier => Modifier == "SF" ? "SFS" : Modifier;

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

            if (!Modifiers.Contains(Modifier)) Modifier = null;

            IQueryable<Leaderboard> leaderboardQuery = WithModifier(context.Leaderboards, Modifier);

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

            if (RangeMin is float min)
            {
                leaderboardQuery = RangeBy switch
                {
                    "PassRating" => leaderboardQuery.Where(l => l.PassRating >= min),
                    "AccRating" => leaderboardQuery.Where(l => l.AccRating >= min),
                    "TechRating" => leaderboardQuery.Where(l => l.TechRating >= min),
                    _ => leaderboardQuery.Where(l => l.Stars >= min),
                };
            }

            if (RangeMax is float max)
            {
                leaderboardQuery = RangeBy switch
                {
                    "PassRating" => leaderboardQuery.Where(l => l.PassRating <= max),
                    "AccRating" => leaderboardQuery.Where(l => l.AccRating <= max),
                    "TechRating" => leaderboardQuery.Where(l => l.TechRating <= max),
                    _ => leaderboardQuery.Where(l => l.Stars <= max),
                };
            }

            // Apply sorting based on SortBy parameter
            leaderboardQuery = SortBy switch
            {
                "PassRating" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.PassRating) : leaderboardQuery.OrderBy(l => l.PassRating),
                "TechRating" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.TechRating) : leaderboardQuery.OrderBy(l => l.TechRating),
                "AccRating" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.AccRating) : leaderboardQuery.OrderBy(l => l.AccRating),
                "Worth" => SortHelpers.NullsLast(leaderboardQuery, l => l.Worth, SortDescending),
                _ => SortDescending ? leaderboardQuery.OrderByDescending(l => l.Stars) : leaderboardQuery.OrderBy(l => l.Stars),
            };

            int pageSize = 25;
            TotalRecords = await leaderboardQuery.CountAsync();
            TotalPages = (int)System.Math.Ceiling(TotalRecords / (double)pageSize);
            CurrentPage = System.Math.Clamp(currentPage, 1, System.Math.Max(TotalPages, 1));

            Leaderboards = await leaderboardQuery.Skip((CurrentPage - 1) * pageSize).Take(pageSize).ToListAsync();
        }

        // The leaderboards with the modifier's stars, ratings and worth in their own fields (display only, not tracked),
        // so the filters and sorting below apply to them
        private static IQueryable<Leaderboard> WithModifier(IQueryable<Leaderboard> leaderboards, string? modifier)
        {
            if (modifier == null) return leaderboards;
            var withRatings = leaderboards.Where(l => l.ModifiersRating != null);
            return modifier switch
            {
                "SS" => withRatings.Select(l => new Leaderboard
                {
                    Id = l.Id, Name = l.Name, Mapper = l.Mapper, Cover = l.Cover, ModeName = l.ModeName, DifficultyName = l.DifficultyName,
                    Stars = l.ModifiersRating!.SSStars, PassRating = l.ModifiersRating.SSPassRating, AccRating = l.ModifiersRating.SSAccRating,
                    TechRating = l.ModifiersRating.SSTechRating, Worth = l.ModifiersRating.SSWorth,
                }),
                "FS" => withRatings.Select(l => new Leaderboard
                {
                    Id = l.Id, Name = l.Name, Mapper = l.Mapper, Cover = l.Cover, ModeName = l.ModeName, DifficultyName = l.DifficultyName,
                    Stars = l.ModifiersRating!.FSStars, PassRating = l.ModifiersRating.FSPassRating, AccRating = l.ModifiersRating.FSAccRating,
                    TechRating = l.ModifiersRating.FSTechRating, Worth = l.ModifiersRating.FSWorth,
                }),
                _ => withRatings.Select(l => new Leaderboard
                {
                    Id = l.Id, Name = l.Name, Mapper = l.Mapper, Cover = l.Cover, ModeName = l.ModeName, DifficultyName = l.DifficultyName,
                    Stars = l.ModifiersRating!.SFStars, PassRating = l.ModifiersRating.SFPassRating, AccRating = l.ModifiersRating.SFAccRating,
                    TechRating = l.ModifiersRating.SFTechRating, Worth = l.ModifiersRating.SFWorth,
                }),
            };
        }
    }
}
