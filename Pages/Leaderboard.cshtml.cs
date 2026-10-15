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
        public List<PpAtAcc> PpTable { get; set; } = new();

        public class PpAtAcc
        {
            public string Label { get; set; } = "";
            public double Accuracy { get; set; }
            public double CurveValue { get; set; }
            public double BaseCurveValue { get; set; }
            public double Pp { get; set; }
            public double BasePp { get; set; }
        }

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

            int pageSize = 10;
            CurrentPage = currentPage;
            CompareCurrentPage = compareCurrentPage;

            TotalScores = await context.Scores.Where(s => s.LeaderboardId == id).CountAsync();
            TotalPages = (int)System.Math.Ceiling(TotalScores / (double)pageSize);

            Scores = await context.Scores
                                   .Where(s => s.LeaderboardId == id)
                                   .Include(s => s.Player)
                                   .OrderByDescending(s => s.Pp)
                                   .Skip((currentPage - 1) * pageSize)
                                   .Take(pageSize)
                                   .ToListAsync();

            // Fetch data for first leaderboard chart
            ScoreGraphEntries = await context.Scores
                .Where(s => s.LeaderboardId == id)
                .Select(s => new ScoreGraphEntry
                {
                    playerId = s.PlayerId,
                    timepost = s.Timepost,
                    weight = s.Weight,
                    rank = s.Rank,
                    modifiers = s.Modifiers,
                    playerRank = s.Player.Rank,
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
                if (CompareLeaderboard != null) ShowModifier(CompareLeaderboard, Modifier);

                if (CompareLeaderboard != null)
                {
                    CompareTotalScores = await context.Scores.Where(s => s.LeaderboardId == compareId).CountAsync();
                    CompareTotalPages = (int)System.Math.Ceiling(CompareTotalScores / (double)pageSize);

                    CompareScores = await context.Scores
                                       .Where(s => s.LeaderboardId == compareId)
                                       .Include(s => s.Player)
                                       .OrderByDescending(s => s.Pp)
                                       .Skip((compareCurrentPage - 1) * pageSize)
                                       .Take(pageSize)
                                       .ToListAsync();

                    // Fetch data for comparison leaderboard chart
                    CompareScoreGraphEntries = await context.Scores
                        .Where(s => s.LeaderboardId == compareId)
                        .Select(s => new ScoreGraphEntry
                        {
                            playerId = s.PlayerId,
                            timepost = s.Timepost,
                            weight = s.Weight,
                            rank = s.Rank,
                            modifiers = s.Modifiers,
                            playerRank = s.Player.Rank,
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

        // Puts the modifier's ratings, stars, predicted acc and curve in the leaderboard's own fields (display only)
        private static void ShowModifier(Leaderboard lb, string modifier)
        {
            var mod = lb.ModifiersRating;
            if (mod == null || modifier == "none") return;
            switch (modifier)
            {
                case "SS":
                    (lb.PredictedAcc, lb.PassRating, lb.AccRating, lb.TechRating, lb.Stars, lb.Curve) =
                        (mod.SSPredictedAcc, mod.SSPassRating, mod.SSAccRating, mod.SSTechRating, mod.SSStars, mod.SSCurve);
                    break;
                case "FS":
                    (lb.PredictedAcc, lb.PassRating, lb.AccRating, lb.TechRating, lb.Stars, lb.Curve) =
                        (mod.FSPredictedAcc, mod.FSPassRating, mod.FSAccRating, mod.FSTechRating, mod.FSStars, mod.FSCurve);
                    break;
                case "SFS":
                    (lb.PredictedAcc, lb.PassRating, lb.AccRating, lb.TechRating, lb.Stars, lb.Curve) =
                        (mod.SFPredictedAcc, mod.SFPassRating, mod.SFAccRating, mod.SFTechRating, mod.SFStars, mod.SFCurve);
                    break;
            }
        }

        // Curve chart and the pp table at a few accuracies around the predicted acc, with this curve and the base curve
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

            if (lb.PredictedAcc <= 0 || lb.PredictedAcc >= 1) return;
            double p = lb.PredictedAcc;
            var points = new List<(string, double)>
            {
                ("Predicted acc", p),
                ("Predicted + 0.25", p + 0.0025),
                ("Predicted + 0.5", p + 0.005),
                ("Predicted + 1", p + 0.01),
                ("Predicted + 2", p + 0.02),
                ("SSS", 1.0),
            };
            foreach (var (label, acc) in points)
            {
                if (acc > 1.0001 || (acc > 0.9999 && label != "SS")) continue;
                float a = (float)Math.Min(acc, 1.0);
                var withCurve = ReplayUtils.PpFromScore(a, "", null, lb.AccRating, lb.PassRating, lb.TechRating, HasOwnCurve ? curve : null);
                var withBase = ReplayUtils.PpFromScore(a, "", null, lb.AccRating, lb.PassRating, lb.TechRating);
                PpTable.Add(new PpAtAcc
                {
                    Label = label,
                    Accuracy = a * 100,
                    CurveValue = ReplayUtils.Curve2(a, HasOwnCurve ? curve : null),
                    BaseCurveValue = ReplayUtils.Curve2(a),
                    Pp = withCurve.Item1,
                    BasePp = withBase.Item1,
                });
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
            public string playerName { get; set; }
            public string playerAvatar { get; set; }
            public float accuracy { get; set; }
            public float pp { get; set; }
            public bool fc { get; set; }
            public float fcAcc { get; set; }
        }
    }
}
