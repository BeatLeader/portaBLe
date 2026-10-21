using portaBLe.DB;
using RatingAPI.Pp;
using System.Numerics;

namespace portaBLe.Refresh
{
    // The pp of scores and the stars of maps are RatingAPI's (RatingAPI/Pp: PpCalculator, PpFormula), as on the server.
    // These only read the ratings and curves from portaBLe's models.
    static class ReplayUtils
    {
        // Value of a per-leaderboard curve (points from 100% down, see RatingAPI Curve.GetCurve). Null or empty: the base curve.
        public static float Curve2(float acc, IReadOnlyList<Vector2>? curve) => PpCalculator.CurveValue(acc, curve);

        public static float Curve2(float acc) => PpCalculator.CurveValue(acc);

        // RatingAPI's curve points as stored in the database
        public static List<Vector2> ToCurve(IEnumerable<global::RatingAPI.Controllers.Point>? points) => PpCalculator.ToCurve(points);

        // Same as RatingAPI (AccRating.GetRating), so the constants only live in one place
        // hitDistance: the map's Leaderboard.HitDistance (the same with speed modifiers)
        public static float AccRating(float? predictedAcc, float? passRating, float? techRating, float hitDistance = 0) =>
            (float)new global::RatingAPI.Controllers.AccRating().GetRating(predictedAcc, passRating, techRating, hitDistance);

        // The acc reweighter's multiplier of a leaderboard (Leaderboard.AccReweight, 0 = none)
        public static double AccReweightOf(Leaderboard lb) => lb.AccReweight > 0 ? lb.AccReweight : 1;

        // An acc rating times the acc reweighter's multiplier
        public static float Reweighted(float accRating, double multiplier) => (float)(accRating * multiplier);

        // What a 96% score is worth on the star scale (PpCalculator.ToStars). curve: the leaderboard's (null: the base curve).
        public static float ToStars(float accRating, float passRating, float techRating, IReadOnlyList<Vector2>? curve = null) =>
            PpCalculator.ToStars(accRating, passRating, techRating, curve);

        // Stars of a leaderboard and of its SS / FS / SF ratings, each with its own curve
        public static void SetStars(Leaderboard lb)
        {
            lb.Stars = ToStars(lb.AccRating, lb.PassRating, lb.TechRating, lb.Curve);
            var mod = lb.ModifiersRating;
            if (mod == null) return;
            mod.SSStars = PpCalculator.ToStars(SpeedRatings(mod, "SS", lb.Curve)!.Value);
            mod.FSStars = PpCalculator.ToStars(SpeedRatings(mod, "FS", lb.Curve)!.Value);
            mod.SFStars = PpCalculator.ToStars(SpeedRatings(mod, "SF", lb.Curve)!.Value);
        }

        // A speed modifier's ratings and curve (the leaderboard's curve when the modifier has none). Null: not a speed modifier.
        public static MapRatings? SpeedRatings(ModifiersRating mod, string modifier, IReadOnlyList<Vector2>? curve)
        {
            var own = mod.GetCurve(modifier);
            var used = own != null && own.Count >= 2 ? own : curve;
            return modifier switch
            {
                "SS" => new MapRatings(mod.SSAccRating, mod.SSPassRating, mod.SSTechRating, used),
                "FS" => new MapRatings(mod.FSAccRating, mod.FSPassRating, mod.FSTechRating, used),
                "SF" => new MapRatings(mod.SFAccRating, mod.SFPassRating, mod.SFTechRating, used),
                _ => null
            };
        }

        // PpCalculator.ScorePp with a leaderboard's ratings: (pp, bonus pp, pass pp, acc pp, tech pp)
        public static (float, float, float, float, float) PpFromScore(
            float accuracy,
            string modifiers,
            ModifiersRating? modifiersRating,
            float accRating,
            float passRating,
            float techRating,
            IReadOnlyList<Vector2>? curve = null)   // the leaderboard's acc curve (null: the base curve)
        {
            MapRatings? speedRatings = null;
            // Only scores with modifiers can use the modifier ratings: the first speed modifier
            if (modifiersRating != null && !string.IsNullOrEmpty(modifiers)) {
                foreach (var modifier in modifiers.ToUpper().Split(","))
                {
                    speedRatings = SpeedRatings(modifiersRating, modifier, curve);
                    if (speedRatings != null) break;
                }
            }
            return PpCalculator.ScorePp(accuracy, modifiers, new MapRatings(accRating, passRating, techRating, curve), speedRatings, modifiersRating != null);
        }

        public static int MaxScoreForNote(int count) {
          int note_score = 115;

          if (count <= 1) // x1 (+1 note)
              return note_score * (0 + (count - 0) * 1);
          if (count <= 5) // x2 (+4 notes)
              return note_score * (1 + (count - 1) * 2);
          if (count <= 13) // x4 (+8 notes)
              return note_score * (9 + (count - 5) * 4);
          // x8
          return note_score * (41 + (count - 13) * 8);
        }

        public static int ScoreForRank(int rank) {
            switch (rank) {
                case 1:
                    return 5;
                case 2:
                    return 3;
                case 3:
                    return 1;
                default:
                    return 0;
            }
        }

        public static int UpdateRankScore(int oldScore, int? oldRank, int newRank) {
            int result = oldScore;
            if (oldRank != null) {
                result -= ScoreForRank(oldRank ?? 4);
            }
            result += ScoreForRank(newRank);

            return result;
        }

        public static string SafeSubstring(this string text, int start, int length)
        {
            return text.Length <= start ? ""
                : text.Length - start <= length ? text.Substring(start)
                : text.Substring(start, length);
        }
    }
}
