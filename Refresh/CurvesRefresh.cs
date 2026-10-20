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
            // The curve depends on the predicted acc, and on the slider share with CurveSettings.SliderTilt (not stored: from
            // the maps folder, Analyzer only)
            var sliders = CurveSettings.Current.SliderTilt != 0 ? await SliderPercentages(lbs) : new Dictionary<string, double>();

            foreach (var lb in lbs)
            {
                var lack = new LackMapCalculation { SliderPercentage = sliders.GetValueOrDefault(lb.Id) };
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

        // Slider share of every leaderboard (no modifier), from the maps folder (downloaded when missing)
        private static async Task<Dictionary<string, double>> SliderPercentages(List<Leaderboard> lbs)
        {
            var result = new System.Collections.Concurrent.ConcurrentDictionary<string, double>();
            var downloader = new Downloader("maps");
            await Parallel.ForEachAsync(lbs, new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount }, async (lb, ct) =>
            {
                try
                {
                    var mapPath = await downloader.MapAsync(lb.Hash);
                    if (mapPath == null) return;
                    var mapset = new beatleader_parser.Parse().TryLoadPath(mapPath);
                    int diffCode = RatingsRefresh.GetDiffCode(lb.DifficultyName);
                    var set = mapset?.Difficulties.FirstOrDefault(d => d.Characteristic == lb.ModeName
                        && (d.BeatMap._difficultyRank == diffCode || d.BeatMap._difficulty == lb.DifficultyName));
                    if (set == null) return;
                    var ratings = new beatleader_analyzer.Analyze().GetRating(set.Data, lb.ModeName, lb.DifficultyName, (float)mapset!.Info._beatsPerMinute);
                    if (ratings != null) result[lb.Id] = global::RatingAPI.Controllers.Curve.SliderPercentage(ratings);
                }
                catch { }
            });
            return new Dictionary<string, double>(result);
        }
    }
}
