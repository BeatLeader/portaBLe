using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Refresh;
using portaBLe.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace portaBLe.Pages
{
    public class LeaderboardModel : BasePageModel
    {
        public Leaderboard Leaderboard { get; set; }
        public Leaderboard? CompareLeaderboard { get; set; }
        public List<Score> Scores { get; set; }
        public List<Score>? CompareScores { get; set; }
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }
        public int TotalScores { get; set; }
        public int CompareCurrentPage { get; set; } = 1;
        public int CompareTotalPages { get; set; }
        public int CompareTotalScores { get; set; }

        // Properties to hold the chart data
        public ICollection<ScoreGraphEntry> ScoreGraphEntries { get; set; }
        public ICollection<ScoreGraphEntry>? CompareScoreGraphEntries { get; set; }

        // Speed modifier shown (none, SS, FS, SFS): ratings, stars, predicted acc and curve of that modifier
        public string Modifier { get; set; } = "none";
        public static readonly string[] Modifiers = { "none", "SS", "FS", "SFS" };

        // Base NJS of the difficulty (Leaderboard.Njs), null when the database doesn't have it yet
        public float? Njs => Leaderboard?.Njs > 0 ? Leaderboard.Njs : null;
        public float? CompareNjs => CompareLeaderboard?.Njs > 0 ? CompareLeaderboard.Njs : null;

        // Speed multiplier of the shown modifier (as RatingsController rates them)
        public static float SpeedOf(string modifier) => modifier switch { "SS" => 0.85f, "FS" => 1.2f, "SFS" => 1.5f, _ => 1f };

        // "NJS: 18", or with a speed modifier the NJS the player sees: "NJS: 27 (18 × 1.5 with SFS)"
        public string? NjsText(float? njs)
        {
            if (!njs.HasValue) return null;
            float speed = SpeedOf(Modifier);
            var baseText = njs.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            if (speed == 1f) return $"NJS: {baseText}";
            var shown = (njs.Value * speed).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            return $"NJS: {shown} ({baseText} × {speed.ToString(System.Globalization.CultureInfo.InvariantCulture)} with {Modifier})";
        }

        // Acc curve of the shown modifier and the base curve, as [accuracy %, value] from low to high accuracy
        public List<double[]> CurvePoints { get; set; } = new();
        public List<double[]> BaseCurvePoints { get; set; } = new();
        public bool HasOwnCurve { get; set; }
        // Where the curve leaves the base curve (A from CurveSettings.Table), in %
        public double? CurveStart { get; set; }
        // ModifiableCurve.C used above A for this map (CurveSettings.Table)
        public double? CurveC { get; set; }
        // All ModifiableCurve parameters used for this map (CurveSettings.Table), null when the map has no stored curve
        public global::RatingAPI.Controllers.CurveRow? CurveParams { get; set; }
        public static readonly global::RatingAPI.Controllers.CurveRow DefaultCurveParams = new();

        // pp over the whole accuracy range with the shown modifier's ratings, as [accuracy %, pp, pass pp, acc pp, tech pp];
        // with the base curve instead of this map's, [accuracy %, pp]; and the comparison leaderboard's, [accuracy %, pp]
        public List<double[]> PpCurve { get; set; } = new();
        public List<double[]> BasePpCurve { get; set; } = new();
        public List<double[]>? ComparePpCurve { get; set; }

        // The song's other leaderboards (same SongId: other difficulties and modes), lowest difficulty first
        public List<Leaderboard> Difficulties { get; set; } = new();

        // The score modifier of the shown speed modifier (SFS scores have SF), null without one: then every score is shown
        public string? ScoreModifier => Modifier switch { "SS" => "SS", "FS" => "FS", "SFS" => "SF", _ => null };

        public LeaderboardModel(IDynamicDbContextService dbService) : base(dbService)
        {
        }

        public async Task<IActionResult> OnGetAsync(string id, string compareId = null, int currentPage = 1, int compareCurrentPage = 1, string db = null, string modifier = "none")
        {
            await InitializeDatabaseSelectionAsync(db);

            using var context = (Services.DynamicDbContext)GetDbContext();

            // Not tracked: the shown modifier's ratings are copied onto it for display only
            Leaderboard = await context.Leaderboards.AsNoTracking().Include(l => l.ModifiersRating).FirstOrDefaultAsync(l => l.Id == id);

            if (Leaderboard == null)
            {
                return NotFound();
            }

            Modifier = Modifiers.Contains(modifier) ? modifier : "none";
            ShowModifier(Leaderboard, Modifier);
            BuildCurve();
            PpCurve = PpOverAcc(Leaderboard, HasOwnCurve ? Leaderboard.Curve : null, true);
            BasePpCurve = PpOverAcc(Leaderboard, null, false);

            if (!string.IsNullOrEmpty(Leaderboard.SongId))
            {
                var songId = Leaderboard.SongId;
                Difficulties = (await context.Leaderboards.AsNoTracking()
                        .Where(l => l.SongId == songId)
                        .Select(l => new Leaderboard { Id = l.Id, DifficultyName = l.DifficultyName, ModeName = l.ModeName, Stars = l.Stars })
                        .ToListAsync())
                    .OrderBy(l => l.ModeName != Leaderboard.ModeName).ThenBy(l => l.ModeName).ThenBy(l => DifficultyOrder(l.DifficultyName))
                    .ToList();
            }

            int pageSize = 10;
            CurrentPage = currentPage;
            CompareCurrentPage = compareCurrentPage;

            TotalScores = await ScoresOf(context, id).CountAsync();
            TotalPages = (int)System.Math.Ceiling(TotalScores / (double)pageSize);

            Scores = await ScoresOf(context, id)
                                   .Include(s => s.Player)
                                   .OrderByDescending(s => s.Pp)
                                   .Skip((currentPage - 1) * pageSize)
                                   .Take(pageSize)
                                   .ToListAsync();

            // Fetch data for first leaderboard chart
            ScoreGraphEntries = await ScoresOf(context, id)
                .Select(s => new ScoreGraphEntry
                {
                    playerId = s.PlayerId,
                    timepost = s.Timepost,
                    weight = s.Weight,
                    rank = s.Rank,
                    modifiers = s.Modifiers,
                    playerRank = s.Player.Rank,
                    playerRankedPlays = s.Player.RankedPlayCount,
                    playerName = s.Player.Name,
                    accuracy = s.Accuracy * 100f,
                    pp = s.Pp,
                    playerAvatar = s.Player.Avatar,
                    fc = s.FC,
                    fcAcc = s.FCAcc
                })
                .ToListAsync();

            // Handle comparison leaderboard if compareId is provided
            if (!string.IsNullOrEmpty(compareId))
            {
                CompareLeaderboard = await context.Leaderboards.AsNoTracking().Include(l => l.ModifiersRating).FirstOrDefaultAsync(l => l.Id == compareId);
                if (CompareLeaderboard != null)
                {
                    ShowModifier(CompareLeaderboard, Modifier);
                    var compareCurve = CompareLeaderboard.Curve;
                    ComparePpCurve = PpOverAcc(CompareLeaderboard, compareCurve != null && compareCurve.Count >= 2 ? compareCurve : null, false);
                }

                if (CompareLeaderboard != null)
                {
                    CompareTotalScores = await ScoresOf(context, compareId).CountAsync();
                    CompareTotalPages = (int)System.Math.Ceiling(CompareTotalScores / (double)pageSize);

                    CompareScores = await ScoresOf(context, compareId)
                                       .Include(s => s.Player)
                                       .OrderByDescending(s => s.Pp)
                                       .Skip((compareCurrentPage - 1) * pageSize)
                                       .Take(pageSize)
                                       .ToListAsync();

                    // Fetch data for comparison leaderboard chart
                    CompareScoreGraphEntries = await ScoresOf(context, compareId)
                        .Select(s => new ScoreGraphEntry
                        {
                            playerId = s.PlayerId,
                            timepost = s.Timepost,
                            weight = s.Weight,
                            rank = s.Rank,
                            modifiers = s.Modifiers,
                            playerRank = s.Player.Rank,
                    playerRankedPlays = s.Player.RankedPlayCount,
                            playerName = s.Player.Name,
                            accuracy = s.Accuracy * 100f,
                            pp = s.Pp,
                            playerAvatar = s.Player.Avatar,
                            fc = s.FC,
                            fcAcc = s.FCAcc
                        })
                        .ToListAsync();
                }
            }

            return Page();
        }

        // A leaderboard's scores: with a speed modifier shown, only the scores with it
        private IQueryable<Score> ScoresOf(Services.DynamicDbContext context, string leaderboardId)
        {
            var scores = context.Scores.Where(s => s.LeaderboardId == leaderboardId);
            if (ScoreModifier is string modifier)
            {
                var tag = "," + modifier + ",";
                scores = scores.Where(s => s.Modifiers != null && ("," + s.Modifiers + ",").Contains(tag));
            }
            return scores;
        }

        private static int DifficultyOrder(string? difficulty) => difficulty switch
        {
            "Easy" => 0, "Normal" => 1, "Hard" => 2, "Expert" => 3, "ExpertPlus" => 4, _ => 5
        };

        // pp at every accuracy with the leaderboard's (shown) ratings: 0-50% by 1%, 50-90% by 0.5%, 90-100% by 0.05%.
        // curve: the acc curve (null: the base curve). parts: also pass, acc and tech pp.
        private static List<double[]> PpOverAcc(Leaderboard lb, IReadOnlyList<System.Numerics.Vector2>? curve, bool parts)
        {
            var accuracies = new List<double>();
            for (int i = 0; i < 50; i++) accuracies.Add(i);
            for (int i = 0; i < 80; i++) accuracies.Add(50 + i * 0.5);
            for (int i = 0; i <= 200; i++) accuracies.Add(90 + i * 0.05);
            var points = new List<double[]>(accuracies.Count);
            foreach (var acc in accuracies)
            {
                var (pp, _, passPp, accPp, techPp) = ReplayUtils.PpFromScore((float)(acc / 100), "", null, lb.AccRating, lb.PassRating, lb.TechRating, curve);
                double Finite(float v) => float.IsFinite(v) ? Math.Round(v, 2) : 0;
                points.Add(parts
                    ? new[] { Math.Round(acc, 2), Finite(pp), Finite(passPp), Finite(accPp), Finite(techPp) }
                    : new[] { Math.Round(acc, 2), Finite(pp) });
            }
            return points;
        }

        // Puts the modifier's ratings, stars, predicted acc, curve and worth in the leaderboard's own fields (display only)
        private static void ShowModifier(Leaderboard lb, string modifier)
        {
            var mod = lb.ModifiersRating;
            if (mod == null || modifier == "none") return;
            switch (modifier)
            {
                case "SS":
                    (lb.PredictedAcc, lb.PassRating, lb.AccRating, lb.TechRating, lb.Stars, lb.Curve) =
                        (mod.SSPredictedAcc, mod.SSPassRating, mod.SSAccRating, mod.SSTechRating, mod.SSStars, mod.SSCurve);
                    lb.Worth = mod.SSWorth;
                    break;
                case "FS":
                    (lb.PredictedAcc, lb.PassRating, lb.AccRating, lb.TechRating, lb.Stars, lb.Curve) =
                        (mod.FSPredictedAcc, mod.FSPassRating, mod.FSAccRating, mod.FSTechRating, mod.FSStars, mod.FSCurve);
                    lb.Worth = mod.FSWorth;
                    break;
                case "SFS":
                    (lb.PredictedAcc, lb.PassRating, lb.AccRating, lb.TechRating, lb.Stars, lb.Curve) =
                        (mod.SFPredictedAcc, mod.SFPassRating, mod.SFAccRating, mod.SFTechRating, mod.SFStars, mod.SFCurve);
                    lb.Worth = mod.SFWorth;
                    break;
            }
        }

        // Acc curve chart: this curve and the base curve
        private void BuildCurve()
        {
            var lb = Leaderboard;
            var curve = lb.Curve;
            HasOwnCurve = curve != null && curve.Count >= 2;

            BaseCurvePoints = global::RatingAPI.Controllers.Curve.BaseTable
                .Where(p => p.x >= 0.6)
                .OrderBy(p => p.x)
                .Select(p => new[] { p.x * 100, p.y })
                .ToList();
            CurvePoints = HasOwnCurve
                ? curve!.Where(p => p.X >= 0.6f).OrderBy(p => p.X).Select(p => new[] { (double)p.X * 100, (double)p.Y }).ToList()
                : BaseCurvePoints;

            var settings = global::RatingAPI.Controllers.CurveSettings.Current;
            if (HasOwnCurve && lb.PredictedAcc > 0)
            {
                CurveParams = settings.ParamsAt(lb.PredictedAcc);
                var (a, c) = (CurveParams.A, CurveParams.C);
                if (a < 0.9995)
                {
                    CurveStart = Math.Max(a, 0.951) * 100;
                    CurveC = c;
                }
            }
        }

        public class ScoreGraphEntry
        {
            public string playerId { get; set; }
            public float weight { get; set; }
            public int rank { get; set; }
            public int timepost { get; set; }
            public string modifiers { get; set; }
            public int playerRank { get; set; }
            public int playerRankedPlays { get; set; }
            public string playerName { get; set; }
            public string playerAvatar { get; set; }
            public float accuracy { get; set; }
            public float pp { get; set; }
            public bool fc { get; set; }
            public float fcAcc { get; set; }
        }
    }
}
