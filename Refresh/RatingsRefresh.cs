using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using RatingAPI.Controllers;

namespace portaBLe.Refresh
{
    public class RatingsRefresh
    {
        public static int GetDiffCode(string difficulty)
        {
            switch (difficulty)
            {
                case "Easy": return 1;
                case "Normal": return 3;
                case "Hard": return 5;
                case "Expert": return 7;
                case "ExpertPlus": return 9;
                default: return 9;
            }
        }

        public static async Task Overwrite(AppContext dbContext)
        {
            try
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE Leaderboards ADD COLUMN LinearPercent REAL DEFAULT 0");
                Console.WriteLine("LinearPercent column added successfully.");
            }
            catch
            {
                Console.WriteLine("LinearPercent column already exist.");
            }
            try
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE Leaderboards ADD COLUMN MultiRating REAL DEFAULT 0");
                Console.WriteLine("MultiRating column added successfully.");
            }
            catch
            {
                Console.WriteLine("MultiRating column already exist.");
            }
            try
            {
                await dbContext.Database.ExecuteSqlRawAsync(
                     "ALTER TABLE Leaderboards ADD COLUMN ParityErrors INTEGER DEFAULT 0");
                Console.WriteLine("ParityErrors column added successfully.");
            }
            catch
            {
                Console.WriteLine("ParityErrors column already exist.");
            }
            try
            {

                await dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE Leaderboards ADD COLUMN BombAvoidances INTEGER DEFAULT 0");
                Console.WriteLine("BombAvoidances column added successfully.");
            }
            catch
            {
                Console.WriteLine("BombAvoidances column already exist.");
            }

            var configDictionary = new Dictionary<string, string>
            {
                { "MapsPath", "maps" },
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configDictionary)
                .Build();

            var lbs = dbContext.Leaderboards.Include(lb => lb.ModifiersRating).ToList();
            Console.WriteLine($"Recalculating from RatingAPI for {lbs.Count} leaderboards");
            
            int processedCount = 0;
            int totalCount = lbs.Count;
            int errorCount = 0;
            var lockObj = new object();
            var startTime = DateTime.Now;

            // Use CPU core count for CPU-bound inference operations
            var parallelOptions = new ParallelOptions 
            { 
                MaxDegreeOfParallelism = Program.CoreCount
            };

            await Parallel.ForEachAsync(lbs, parallelOptions, async (lb, ct) =>
            {
                try
                {
                    // Create per-thread instances to avoid sharing state
                    using var loggerFactory = LoggerFactory.Create(builder => { });
                    var logger = loggerFactory.CreateLogger<RatingsController>();
                    var controller = new RatingsController(configuration, logger);
                    
                    var response = controller.Get(lb.Hash, lb.ModeName, GetDiffCode(lb.DifficultyName)).Value;

                    lb.LinearPercent = (float)response["none"].LackMapCalculation.LinearPercentage;
                    lb.MultiRating = (float)response["none"].LackMapCalculation.MultiRating;
                    lb.ParityErrors = (float)response["none"].LackMapCalculation.Statistics.ParityErrors;
                    lb.BombAvoidances = (float)response["none"].LackMapCalculation.Statistics.BombAvoidances;

                    lb.PassRating = (float)response["none"].LackMapCalculation.PassRating;
                    lb.TechRating = (float)response["none"].LackMapCalculation.TechRating;
                    lb.PredictedAcc = (float)response["none"].PredictedAcc;
                    lb.AccRating = (float)response["none"].AccRating;
                    lb.Stars = ReplayUtils.ToStars(lb.AccRating, lb.PassRating, lb.TechRating);

                    lb.ModifiersRating.SSPassRating = (float)response["SS"].LackMapCalculation.PassRating;
                    lb.ModifiersRating.SSTechRating = (float)response["SS"].LackMapCalculation.TechRating;
                    lb.ModifiersRating.SSPredictedAcc = (float)response["SS"].PredictedAcc;
                    lb.ModifiersRating.SSAccRating = (float)response["SS"].AccRating;

                    lb.ModifiersRating.FSPassRating = (float)response["FS"].LackMapCalculation.PassRating;
                    lb.ModifiersRating.FSTechRating = (float)response["FS"].LackMapCalculation.TechRating;
                    lb.ModifiersRating.FSPredictedAcc = (float)response["FS"].PredictedAcc;
                    lb.ModifiersRating.FSAccRating = (float)response["FS"].AccRating;

                    lb.ModifiersRating.SFPassRating = (float)response["SFS"].LackMapCalculation.PassRating;
                    lb.ModifiersRating.SFTechRating = (float)response["SFS"].LackMapCalculation.TechRating;
                    lb.ModifiersRating.SFPredictedAcc = (float)response["SFS"].PredictedAcc;
                    lb.ModifiersRating.SFAccRating = (float)response["SFS"].AccRating;

                    lb.ModifiersRating.SFStars = ReplayUtils.ToStars(lb.ModifiersRating.SFAccRating, lb.ModifiersRating.SFPassRating, lb.ModifiersRating.SFTechRating);
                    lb.ModifiersRating.FSStars = ReplayUtils.ToStars(lb.ModifiersRating.FSAccRating, lb.ModifiersRating.FSPassRating, lb.ModifiersRating.FSTechRating);
                    lb.ModifiersRating.SSStars = ReplayUtils.ToStars(lb.ModifiersRating.SSAccRating, lb.ModifiersRating.SSPassRating, lb.ModifiersRating.SSTechRating);

                    lock (lockObj)
                    {
                        processedCount++;
                        if (processedCount % 100 == 0 || processedCount == totalCount)
                        {
                            var elapsed = (DateTime.Now - startTime).TotalSeconds;
                            var rate = processedCount / elapsed;
                            var remaining = (totalCount - processedCount) / rate;
                            Console.WriteLine($"Progress: {processedCount}/{totalCount} ({processedCount * 100 / totalCount}%) - Rate: {rate:F1}/s - ETA: {TimeSpan.FromSeconds(remaining):hh\\:mm\\:ss} - Errors: {errorCount} - Time: {(Program.Stopwatch.ElapsedMilliseconds / 1000)}s");
                        }
                    }
                }
                catch (Exception e)
                {
                    lock (lockObj)
                    {
                        errorCount++;
                        if (errorCount <= 10 || errorCount % 100 == 0)
                        {
                            Console.WriteLine($"Error processing {lb.Hash}: {e.Message}");
                        }
                    }
                }
            });

            Console.WriteLine($"\nCompleted: {processedCount - errorCount} successful, {errorCount} errors");
            dbContext.BulkSaveChanges();
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }

        public static async Task Refresh(AppContext dbContext)
        {
            Console.WriteLine("Recalculating Acc Rating from Predicted Accuracy");
            var lbs = dbContext.Leaderboards.Include(lb => lb.ModifiersRating).ToList();
            foreach (var lb in lbs)
            {
                try
                {
                    var mod = lb.ModifiersRating;

                    lb.AccRating = ReplayUtils.AccRating(lb.PredictedAcc, lb.PassRating, lb.TechRating);
                    lb.Stars = ReplayUtils.ToStars(lb.AccRating, lb.PassRating, lb.TechRating);

                    lb.ModifiersRating.SSAccRating = ReplayUtils.AccRating(mod.SSPredictedAcc, mod.SSPassRating, mod.SSTechRating);
                    lb.ModifiersRating.FSAccRating = ReplayUtils.AccRating(mod.FSPredictedAcc, mod.FSPassRating, mod.FSTechRating);
                    lb.ModifiersRating.SFAccRating = ReplayUtils.AccRating(mod.SFPredictedAcc, mod.SFPassRating, mod.SFTechRating);

                    lb.ModifiersRating.SFStars = ReplayUtils.ToStars(lb.ModifiersRating.SFAccRating, lb.ModifiersRating.SFPassRating, lb.ModifiersRating.SFTechRating);
                    lb.ModifiersRating.FSStars = ReplayUtils.ToStars(lb.ModifiersRating.FSAccRating, lb.ModifiersRating.FSPassRating, lb.ModifiersRating.FSTechRating);
                    lb.ModifiersRating.SSStars = ReplayUtils.ToStars(lb.ModifiersRating.SSAccRating, lb.ModifiersRating.SSPassRating, lb.ModifiersRating.SSTechRating);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{e.Message}");
                }
            }

            dbContext.BulkSaveChanges();
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }
    }
}