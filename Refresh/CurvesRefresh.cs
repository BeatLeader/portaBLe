using Analyzer.BeatmapScanner.Data;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using RatingAPI.Controllers;

namespace portaBLe.Refresh
{
    public class CurvesRefresh
    {
        // Regenerates only the acc curves (and the stars depending on them) from the ratings already stored
        // in the database, using RatingAPI's Curve.GetCurve. No map is analyzed again.
        public static async Task Regenerate(AppContext dbContext)
        {
            Console.WriteLine("Regenerating curves from stored ratings");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var lbs = await dbContext.Leaderboards.Include(lb => lb.ModifiersRating).ToListAsync();
            var curveGenerator = new RatingAPI.Controllers.Curve();

            int totalCount = lbs.Count;
            int processedCount = 0;
            int errorCount = 0;
            Console.WriteLine($"Loaded {totalCount} leaderboards in {timer.Elapsed:hh\\:mm\\:ss}");

            foreach (var lb in lbs)
            {
                try
                {
                    var statistics = new Statistics
                    {
                        DodgeWalls = lb.DodgeWalls,
                        CrouchWalls = lb.CrouchWalls,
                        ParityErrors = (int)lb.ParityErrors,
                        BombAvoidances = (int)lb.BombAvoidances,
                    };

                    lb.Curve = Generate(curveGenerator, lb, statistics, lb.PassRating, lb.TechRating, lb.PredictedAcc, lb.AccRating);
                    lb.Stars = ReplayUtils.ToStars(lb.AccRating, lb.PassRating, lb.TechRating, lb.Curve);

                    var mod = lb.ModifiersRating;
                    if (mod != null)
                    {
                        mod.SSCurve = Generate(curveGenerator, lb, statistics, mod.SSPassRating, mod.SSTechRating, mod.SSPredictedAcc, mod.SSAccRating);
                        mod.FSCurve = Generate(curveGenerator, lb, statistics, mod.FSPassRating, mod.FSTechRating, mod.FSPredictedAcc, mod.FSAccRating);
                        mod.SFCurve = Generate(curveGenerator, lb, statistics, mod.SFPassRating, mod.SFTechRating, mod.SFPredictedAcc, mod.SFAccRating);

                        mod.SSStars = ReplayUtils.ToStars(mod.SSAccRating, mod.SSPassRating, mod.SSTechRating, mod.SSCurve);
                        mod.FSStars = ReplayUtils.ToStars(mod.FSAccRating, mod.FSPassRating, mod.FSTechRating, mod.FSCurve);
                        mod.SFStars = ReplayUtils.ToStars(mod.SFAccRating, mod.SFPassRating, mod.SFTechRating, mod.SFCurve);
                    }
                }
                catch (Exception e)
                {
                    errorCount++;
                    if (errorCount <= 10 || errorCount % 100 == 0)
                    {
                        Console.WriteLine($"Error regenerating curve for {lb.Id}: {e.Message}");
                    }
                }

                processedCount++;
                if (processedCount % 500 == 0 || processedCount == totalCount)
                {
                    var elapsed = timer.Elapsed.TotalSeconds;
                    var rate = processedCount / Math.Max(elapsed, 0.001);
                    var remaining = TimeSpan.FromSeconds((totalCount - processedCount) / rate);
                    Console.WriteLine($"Progress: {processedCount}/{totalCount} ({processedCount * 100 / Math.Max(totalCount, 1)}%) - Rate: {rate:F1}/s - ETA: {remaining:hh\\:mm\\:ss} - Errors: {errorCount}");
                }
            }

            Console.WriteLine("Saving regenerated curves to the database...");
            dbContext.BulkSaveChanges();
            timer.Stop();
            Console.WriteLine($"Curve regeneration completed: {processedCount - errorCount} successful, {errorCount} errors, total time {timer.Elapsed:hh\\:mm\\:ss}");
        }

        private static List<System.Numerics.Vector2> Generate(
            RatingAPI.Controllers.Curve generator,
            Leaderboard lb,
            Statistics statistics,
            float passRating,
            float techRating,
            float predictedAcc,
            float accRating)
        {
            var lack = new LackMapCalculation
            {
                PassRating = passRating,
                TechRating = techRating,
                LowNoteNerf = 1, // accRating already includes the low note nerf
                MultiPercentage = lb.MultiPercentage,
                LinearPercentage = lb.LinearPercent,
                Statistics = statistics,
            };

            return generator.GetCurve(lack, predictedAcc, accRating);
        }
    }
}
