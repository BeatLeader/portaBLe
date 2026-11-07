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

        // Tag filter: a MapTags.All key (acc, tech, midspeed, speed, extreme, linear, fitbeat, bombreset, inline), or null for all maps
        [BindProperty(SupportsGet = true)]
        public string? Tag { get; set; }

        // Speed modifier whose stars, ratings and worth are shown, filtered and sorted on: "SS", "FS", "SF", or null for
        // none. With one, maps without modifier ratings are left out.
        [BindProperty(SupportsGet = true)]
        public string? Modifier { get; set; }

        public static readonly string[] Modifiers = { "SS", "FS", "SF" };

        // Which group of metric columns is shown after the map's identity and stars
        [BindProperty(SupportsGet = true)]
        public string? View { get; set; }

        public static readonly (string Key, string Label, string Title)[] Views =
        {
            ("overview", "Overview", "Ratings, the main worth and the balance metrics at a glance"),
            ("ratings", "Ratings", "Pass, acc and tech ratings, predicted acc and how players do against it"),
            ("worth", "Worth", "Worth by quantile (P75 is the main measure) and how much they disagree"),
            ("balance", "Balance", "Is the map worth it for its players: top 20 / 50 shares, top 5 vs same stars, who plays it, top play share, its top play and an emulated one"),
            ("map", "Map", "Notes, length, NJS, peak EBPM, linear swings and players"),
        };

        public string CurrentView => Views.Any(v => v.Key == View) ? View! : "overview";

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

            if (!MapTags.All.Any(t => t.Key == Tag)) Tag = null;
            leaderboardQuery = MapTags.Filter(leaderboardQuery, Tag);

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

            // The Worth column is gone: P75 is the main worth (links to Worth sort and highlight it)
            if (SortBy == "Worth") SortBy = "WorthP75";
            leaderboardQuery = SortBy switch
            {
                "PassRating" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.PassRating) : leaderboardQuery.OrderBy(l => l.PassRating),
                "TechRating" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.TechRating) : leaderboardQuery.OrderBy(l => l.TechRating),
                "AccRating" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.AccRating) : leaderboardQuery.OrderBy(l => l.AccRating),
                "NoteCount" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.NoteCount) : leaderboardQuery.OrderBy(l => l.NoteCount),
                "PeakEbpm" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.PeakEbpm) : leaderboardQuery.OrderBy(l => l.PeakEbpm),
                "WorthMean" => SortHelpers.NullsLast(leaderboardQuery, l => l.WorthMean, SortDescending),
                "WorthMedian" => SortHelpers.NullsLast(leaderboardQuery, l => l.WorthMedian, SortDescending),
                "WorthP75" => SortHelpers.NullsLast(leaderboardQuery, l => l.WorthP75, SortDescending),
                "WorthP90" => SortHelpers.NullsLast(leaderboardQuery, l => l.WorthP90, SortDescending),
                "WorthSpread" => SortHelpers.NullsLast(leaderboardQuery, l => l.WorthSpread, SortDescending),
                "TopPlayShare" => SortHelpers.NullsLast(leaderboardQuery, l => l.TopPlayShareRelative, SortDescending),
                "Top20Share" => SortHelpers.NullsLast(leaderboardQuery, l => l.Top20Share, SortDescending),
                "Top50Share" => SortHelpers.NullsLast(leaderboardQuery, l => l.Top50Share, SortDescending),
                "Top5Relative" => SortHelpers.NullsLast(leaderboardQuery, l => l.Top5Relative, SortDescending),
                "PlayersRankRelative" => SortHelpers.NullsLast(leaderboardQuery, l => l.PlayersRankRelative, SortDescending),
                "AccVsPredicted" => SortHelpers.NullsLast(leaderboardQuery, l => l.AccVsPredicted, SortDescending),
                "TopPlayPp" => SortHelpers.NullsLast(leaderboardQuery, l => l.TopPlayPp, SortDescending),
                "EmulatedTopPp" => SortHelpers.NullsLast(leaderboardQuery, l => l.EmulatedTopPp, SortDescending),
                "SpecialistTopPp" => SortHelpers.NullsLast(leaderboardQuery, l => l.SpecialistTopPp, SortDescending),
                "EmpiricalStars" => SortHelpers.NullsLast(leaderboardQuery, l => l.EmpiricalStars, SortDescending),
                "StarsVsEmpirical" => SortHelpers.NullsLast(leaderboardQuery, l => l.EmpiricalStars == null ? null : (float?)(l.Stars - l.EmpiricalStars), SortDescending),
                "PredictedAcc" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.PredictedAcc) : leaderboardQuery.OrderBy(l => l.PredictedAcc),
                "Duration" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.Duration) : leaderboardQuery.OrderBy(l => l.Duration),
                "Njs" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.Njs) : leaderboardQuery.OrderBy(l => l.Njs),
                "LinearPercent" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.LinearPercent) : leaderboardQuery.OrderBy(l => l.LinearPercent),
                "Count" => SortDescending ? leaderboardQuery.OrderByDescending(l => l.Count) : leaderboardQuery.OrderBy(l => l.Count),
                _ => SortDescending ? leaderboardQuery.OrderByDescending(l => l.Stars) : leaderboardQuery.OrderBy(l => l.Stars),
            };

            int pageSize = 25;
            TotalRecords = await leaderboardQuery.CountAsync();
            TotalPages = (int)System.Math.Ceiling(TotalRecords / (double)pageSize);
            CurrentPage = System.Math.Clamp(currentPage, 1, System.Math.Max(TotalPages, 1));

            Leaderboards = await leaderboardQuery.Skip((CurrentPage - 1) * pageSize).Take(pageSize).ToListAsync();
        }

        // The leaderboards with the modifier's stars, ratings and worth in their own fields (display only, not tracked),
        // so the filters and sorting below apply to them. The modifier's worth goes in WorthP75 too (the P75 column is the main
        // worth); the other quantiles (WorthMean ...) and the balance metrics (Top20Share ...) are nomod only: left empty. The
        // peak EBPM is the nomod one times the speed, the length the nomod one over it; NJS, linear % and players are the map's.
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
                    TechRating = l.ModifiersRating.SSTechRating, Worth = l.ModifiersRating.SSWorth, WorthP75 = l.ModifiersRating.SSWorth, NoteCount = l.NoteCount, TopPlayShare = l.TopPlayShare, TopPlayShareRelative = l.TopPlayShareRelative,
                    IsLinear = l.IsLinear, IsFitbeat = l.IsFitbeat, IsBombReset = l.IsBombReset, IsInline = l.IsInline, MapType = l.MapType, Inlines = l.Inlines, PeakEbpm = l.PeakEbpm * 0.85f,
                    PredictedAcc = l.ModifiersRating.SSPredictedAcc, Duration = l.Duration / 0.85f, Njs = l.Njs, LinearPercent = l.LinearPercent, Count = l.Count,
                }),
                "FS" => withRatings.Select(l => new Leaderboard
                {
                    Id = l.Id, Name = l.Name, Mapper = l.Mapper, Cover = l.Cover, ModeName = l.ModeName, DifficultyName = l.DifficultyName,
                    Stars = l.ModifiersRating!.FSStars, PassRating = l.ModifiersRating.FSPassRating, AccRating = l.ModifiersRating.FSAccRating,
                    TechRating = l.ModifiersRating.FSTechRating, Worth = l.ModifiersRating.FSWorth, WorthP75 = l.ModifiersRating.FSWorth, NoteCount = l.NoteCount, TopPlayShare = l.TopPlayShare, TopPlayShareRelative = l.TopPlayShareRelative,
                    IsLinear = l.IsLinear, IsFitbeat = l.IsFitbeat, IsBombReset = l.IsBombReset, IsInline = l.IsInline, MapType = l.MapType, Inlines = l.Inlines, PeakEbpm = l.PeakEbpm * 1.2f,
                    PredictedAcc = l.ModifiersRating.FSPredictedAcc, Duration = l.Duration / 1.2f, Njs = l.Njs, LinearPercent = l.LinearPercent, Count = l.Count,
                }),
                _ => withRatings.Select(l => new Leaderboard
                {
                    Id = l.Id, Name = l.Name, Mapper = l.Mapper, Cover = l.Cover, ModeName = l.ModeName, DifficultyName = l.DifficultyName,
                    Stars = l.ModifiersRating!.SFStars, PassRating = l.ModifiersRating.SFPassRating, AccRating = l.ModifiersRating.SFAccRating,
                    TechRating = l.ModifiersRating.SFTechRating, Worth = l.ModifiersRating.SFWorth, WorthP75 = l.ModifiersRating.SFWorth, NoteCount = l.NoteCount, TopPlayShare = l.TopPlayShare, TopPlayShareRelative = l.TopPlayShareRelative,
                    IsLinear = l.IsLinear, IsFitbeat = l.IsFitbeat, IsBombReset = l.IsBombReset, IsInline = l.IsInline, MapType = l.MapType, Inlines = l.Inlines, PeakEbpm = l.PeakEbpm * 1.5f,
                    PredictedAcc = l.ModifiersRating.SFPredictedAcc, Duration = l.Duration / 1.5f, Njs = l.Njs, LinearPercent = l.LinearPercent, Count = l.Count,
                }),
            };
        }
    }
}
