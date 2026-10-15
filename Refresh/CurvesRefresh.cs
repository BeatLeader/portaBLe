using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using RatingAPI.Controllers;

namespace portaBLe.Refresh
{
    // Regenerates only the per-leaderboard acc curves from the predicted acc already stored, with RatingAPI's
    // Curve.GetCurve and CurveSettings.Current (from the AccCurvePerMap branch). No map is rated again; stars don't change
    // (they always use the base curve). Run RefreshEverything afterwards to re-score with the new curves.
    public class CurvesRefresh
    {
        public static async Task Regenerate(AppContext dbContext)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "Curve", "TEXT");
            foreach (var column in new[] { "SSCurve", "FSCurve", "SFCurve" })
            {
                await DbSchema.AddColumnIfMissing(dbContext, "ModifiersRating", column, "TEXT");
            }

            Console.WriteLine($"Regenerating acc curves ({FormattableString.Invariant($"A and C by predicted acc: {string.Join(", ", CurveSettings.Current.Table.Select(r => $"{r.PredictedAcc * 100}%: a {r.A}, c {r.C}"))}")})");
            var lbs = await dbContext.Leaderboards.Include(lb => lb.ModifiersRating).ToListAsync();
            var generator = new global::RatingAPI.Controllers.Curve();
            var lack = new LackMapCalculation();   // the curve only depends on the predicted acc

            foreach (var lb in lbs)
            {
                lb.Curve = ReplayUtils.ToCurve(generator.GetCurve(lack, lb.PredictedAcc));
                var mod = lb.ModifiersRating;
                if (mod != null)
                {
                    mod.SSCurve = ReplayUtils.ToCurve(generator.GetCurve(lack, mod.SSPredictedAcc));
                    mod.FSCurve = ReplayUtils.ToCurve(generator.GetCurve(lack, mod.FSPredictedAcc));
                    mod.SFCurve = ReplayUtils.ToCurve(generator.GetCurve(lack, mod.SFPredictedAcc));
                }
            }

            Console.WriteLine($"Saving {lbs.Count} curves...");
            dbContext.BulkSaveChanges();
            Console.WriteLine($"Curves regenerated in {timer.Elapsed:mm\\:ss}");
        }
    }
}
