using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace portaBLe.DB
{
    // Per-leaderboard acc curves are stored as text, "x,y;x,y;..." (from the AccCurvePerMap branch).
    // Applied in OnModelCreating of every context that maps Leaderboard / ModifiersRating.
    public static class CurveModelConfig
    {
        public static void Apply(ModelBuilder modelBuilder)
        {
            var converter = new ValueConverter<List<Vector2>, string>(
                v => Leaderboard.SerializeCurve(v),
                v => Leaderboard.ParseCurve(v));
            var comparer = new ValueComparer<List<Vector2>>(
                (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
                v => v.Aggregate(0, (h, p) => HashCode.Combine(h, p)),
                v => v.ToList());

            void Map<T>(System.Linq.Expressions.Expression<Func<T, List<Vector2>>> property) where T : class =>
                modelBuilder.Entity<T>().Property(property).HasConversion(converter, comparer);

            Map<Leaderboard>(l => l.Curve);
            Map<ModifiersRating>(m => m.SSCurve);
            Map<ModifiersRating>(m => m.FSCurve);
            Map<ModifiersRating>(m => m.SFCurve);
        }
    }

    public class Player
    {
        [StringLength(25, MinimumLength = 0)]
        public string Id { get; set; }
        public float Pp { get; set; }
        public float AccPp { get; set; }
        public float TechPp { get; set; }
        public float PassPp { get; set; }
        public int Rank { get; set; }
        [StringLength(50, MinimumLength = 0)]
        public string Name { get; set; }
        [StringLength(5, MinimumLength = 0)]
        public string Country { get; set; }
        public int CountryRank { get; set; }
        [StringLength(100, MinimumLength = 0)]
        public string Avatar { get; set; }

        public float TopPp { get; set; }
        public int RankedPlayCount { get; set; }
    }

    public class Score
    {
        public int Id { get; set; }

        public int Timepost { get; set; }
        public float Pp { get; set; }
        public float AccPP { get; set; }
        public float TechPP { get; set; }
        public float PassPP { get; set; }
        public float BonusPp { get; set; }
        public float Weight { get; set; }
        [StringLength(25, MinimumLength = 0)]
        public string PlayerId { get; set; }
        public int Rank { get; set; }
        public Player Player { get; set; }
        [StringLength(25, MinimumLength = 0)]
        public string LeaderboardId { get; set; }
        public Leaderboard Leaderboard { get; set; }
        public float Accuracy { get; set; }
        public string Modifiers { get; set; }
        public bool FC { get; set; }
        public float FCAcc { get; set; }
    }

    public class ModifiersRating
    {
        public int Id { get; set; }
        public float FSPredictedAcc { get; set; }
        public float FSPassRating { get; set; }
        public float FSAccRating { get; set; }
        public float FSTechRating { get; set; }
        public float FSStars { get; set; }

        public float SSPredictedAcc { get; set; }
        public float SSPassRating { get; set; }
        public float SSAccRating { get; set; }
        public float SSTechRating { get; set; }
        public float SSStars { get; set; }

        public float SFPredictedAcc { get; set; }
        public float SFPassRating { get; set; }
        public float SFAccRating { get; set; }
        public float SFTechRating { get; set; }
        public float SFStars { get; set; }

        // Worth of the map played with each speed modifier, its own item in the worth measure (scores with that modifier,
        // at that modifier's stars), against nomod maps of those stars: as Leaderboard.Worth, which only has nomod scores.
        // Null with too few in-range scores. Not on the Leaderboards page (nomod worth only).
        public float? SSWorth { get; set; }
        public float? FSWorth { get; set; }
        public float? SFWorth { get; set; }

        public List<Vector2> SSCurve { get; set; } = new();
        public List<Vector2> FSCurve { get; set; } = new();
        public List<Vector2> SFCurve { get; set; } = new();

        public List<Vector2>? GetCurve(string modifier) => modifier switch
        {
            "SS" => SSCurve,
            "FS" => FSCurve,
            "SF" => SFCurve,
            _ => null
        };
    }

    public class Leaderboard
    {
        [StringLength(25, MinimumLength = 0)]
        public string Id { get; set; }
        [StringLength(150, MinimumLength = 0)]
        public string Name { get; set; }
        public string Hash { get; set; }
        [StringLength(25, MinimumLength = 0)]
        public string SongId { get; set; }
        [StringLength(25, MinimumLength = 0)]
        public string ModeName { get; set; }
        [StringLength(25, MinimumLength = 0)]
        public string DifficultyName { get; set; }
        [StringLength(150, MinimumLength = 0)]
        public string Cover { get; set; }
        [StringLength(200, MinimumLength = 0)]
        public string Mapper { get; set; }

        public float Stars { get; set; }
        public float PassRating { get; set; }
        public float AccRating { get; set; }
        public float TechRating { get; set; }
        public float MultiPercentage { get; set; }

        public float PredictedAcc { get; set; }
        public ModifiersRating? ModifiersRating { get; set; }
        public ICollection<Score> Scores { get; set; }

        public float Duration { get; set; }
        // Base note jump speed of the difficulty (Info.dat, without speed modifiers). 0 = not filled yet: RatingsRefresh.Overwrite
        // stores it, RatingsRefresh.RefreshNjs fills it without rating the maps again.
        public float Njs { get; set; }
        // Analyzer mean hit distance (m), used by the acc rating (RatingAPI AccRating.EffectiveAcc). Stored by
        // RatingsRefresh.Overwrite, so RatingsRefresh.Refresh and the balance tuner can rebuild the acc rating.
        public float HitDistance { get; set; }
        // Acc reweighter's multiplier of the acc rating (Refresh/Tools/AccReweighter; 0 or 1 = none). The only
        // rating change that comes from scores. RatingsRefresh applies it to the acc ratings it computes.
        public float AccReweight { get; set; }
        public bool IsLinear { get; set; }
        public bool IsFitbeat { get; set; }

        public int Count { get; set; }
        public int Count80 { get; set; }
        public int Count95 { get; set; }
        public int OutlierCount { get; set; }
        public float Average { get; set; }
        public float Top250 { get; set; }
        public float TotalPP { get; set; }
        public float PPRatioFiltered { get; set; }
        public float PPRatioUnfiltered { get; set; }
        public float Percentile { get; set; }
        public float Megametric { get; set; }
        public float Megametric125 { get; set; }
        public float Megametric75 { get; set; }
        public float Megametric40 { get; set; }
        // How much the map pays its players compared with the other maps of its stars, in the balance tuner's worth measure
        // (BalanceTuner.WorthMeasure, PlayerAdjusted: how much it pays the same players than their other maps at their level,
        // see BalanceTuner.WorthMeasures). +0.04 = 4% more than average. Null with too few in-range scores. The same value
        // the balance tuner and the acc reweighter use (LeaderboardsRefresh.Refresh, BalanceTuner.StoredWorth). Nomod scores
        // only: SS / FS / SF scores have their own (ModifiersRating.SSWorth ...), NF scores none.
        public float? Worth { get; set; }
        // The previous worth: Megametric75 / what maps of its stars average - 1 (a local linear fit of Megametric75 on stars,
        // Gaussian kernel of BalanceTuner.WorthBandwidth stars: BalanceTuner.WorthReference). +0.27 = 27% more than average.
        // Null when the map has no Megametric75 or its star range (0-1 ... 12-13, 13+) fewer than 5 maps. Megametric75
        // weights each score by where it lands in the player's profile, which overreacts to rating changes and inflates maps
        // played by players with few high plays; kept for comparison.
        public float? MegametricWorth { get; set; }
        // Worth by player rank (LeaderboardsRefresh.RefreshWorthByRank): each score's pp / the player's top pp, averaged over
        // the map's scores by the players ranked in that range (#1-20, #21-100, #101-1000, #1001-3000, #3001-10000; more than
        // 75 ranked plays, as Megametric75), divided by what maps of its stars average (BalanceTuner.WorthReference) - 1.
        // Without Worth's weight: the weight depends on how full each player's top plays are, so maps played by players
        // with few high plays (survivors of very high pass maps) looked worth more than what they give. Null with fewer
        // than LeaderboardsRefresh.WorthRankMinScores scores in the range.
        public float? WorthRank20 { get; set; }
        public float? WorthRank100 { get; set; }
        public float? WorthRank1000 { get; set; }
        public float? WorthRank3000 { get; set; }
        public float? WorthRank10000 { get; set; }
        public int DodgeWalls { get; set; }
        public int CrouchWalls { get; set; }
        public float LinearPercent { get; set; }
        public float ParityErrors { get; set; }
        public float BombAvoidances { get; set; }

        // Per-leaderboard acc curve (RatingAPI Curve.GetCurve). Empty: the base curve (ReplayUtils.Curve2).
        public List<Vector2> Curve { get; set; } = new();

        public static List<Vector2> ParseCurve(string? data)
        {
            var result = new List<Vector2>();
            if (string.IsNullOrWhiteSpace(data)) return result;

            foreach (var point in data.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = point.Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                {
                    result.Add(new Vector2(x, y));
                }
            }
            return result;
        }

        public static string SerializeCurve(IEnumerable<Vector2>? curve)
        {
            if (curve == null) return string.Empty;
            return string.Join(';', curve.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.X:R},{p.Y:R}")));
        }

        [NotMapped]
        public float Count80Ratio => Count > 0 ? Count80 / (float)Count : 0;
        [NotMapped]
        public float Count95Ratio => Count > 0 ? Count95 / (float)Count : 0;
        [NotMapped]
        public float Count95to80Ratio => Count80 > 0 ? Count95 / (float)Count80 : 0;
    }

    public class Stats
    {
        public int Id { get; set; }
        public string ModeName { get; set; }
        public int TotalOutlier { get; set; }
        public float AvgOutlierPercentage { get; set; }
        public float AvgMegametric { get; set; }
        public float AvgMegametric40 { get; set; }
        public float AvgMegametric75 { get; set; }
        public float AvgMegametric125 { get; set; }
        public int PpCount600 { get; set; }
        public int PpCount700 { get; set; }
        public int PpCount800 { get; set; }
        public int PpCount900 { get; set; }
        public int PpCount1000 { get; set; }
        public float HighestStarRating { get; set; }
        public float HighestAccRating { get; set; }
        public float HighestPassRating { get; set; }
        public float HighestTechRating { get; set; }
        public float Top1PP { get; set; }
        public float Top10PP { get; set; }
        public float Top100PP { get; set; }
        public float Top1000PP { get; set; }
        public float Top2000PP { get; set; }
        public float Top5000PP { get; set; }
        public float Top10000PP { get; set; }
        public float Top1AccPP { get; set; }
        public float Top10AccPP { get; set; }
        public float Top100AccPP { get; set; }
        public float Top1000AccPP { get; set; }
        public float Top2000AccPP { get; set; }
        public float Top5000AccPP { get; set; }
        public float Top10000AccPP { get; set; }
        public float Top1PassPP { get; set; }
        public float Top10PassPP { get; set; }
        public float Top100PassPP { get; set; }
        public float Top1000PassPP { get; set; }
        public float Top2000PassPP { get; set; }
        public float Top5000PassPP { get; set; }
        public float Top10000PassPP { get; set; }
        public float Top1TechPP { get; set; }
        public float Top10TechPP { get; set; }
        public float Top100TechPP { get; set; }
        public float Top1000TechPP { get; set; }
        public float Top2000TechPP { get; set; }
        public float Top5000TechPP { get; set; }
        public float Top10000TechPP { get; set; }
    }
}
