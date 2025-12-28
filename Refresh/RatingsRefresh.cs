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
            var configDictionary = new Dictionary<string, string>
            {
                { "MapsPath", "maps" },
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configDictionary)
                .Build();

            using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var logger = loggerFactory.CreateLogger<RatingsController>();

            var controller = new RatingsController(configuration, logger);

            var lbs = dbContext.Leaderboards.Include(lb => lb.ModifiersRating).ToList();
            Console.WriteLine("Recalculating from RatingAPI");
            foreach (var lb in lbs)
            {
                try
                {
                    var response = controller.Get(lb.Hash, lb.ModeName, GetDiffCode(lb.DifficultyName)).Value;

                    lb.PassRating = (float)response["none"].LackMapCalculation.PassRating;
                    lb.TechRating = (float)response["none"].LackMapCalculation.TechRating;
                    // lb.PredictedAcc = (float)response["none"].PredictedAcc;
                    // lb.AccRating = (float)response["none"].AccRating;
                    lb.Stars = ReplayUtils.ToStars(lb.AccRating, lb.PassRating, lb.TechRating);

                    lb.ModifiersRating.SSPassRating = (float)response["SS"].LackMapCalculation.PassRating;
                    lb.ModifiersRating.SSTechRating = (float)response["SS"].LackMapCalculation.TechRating;
                    // lb.ModifiersRating.SSPredictedAcc = (float)response["SS"].PredictedAcc;
                    // lb.ModifiersRating.SSAccRating = (float)response["SS"].AccRating;

                    lb.ModifiersRating.FSPassRating = (float)response["FS"].LackMapCalculation.PassRating;
                    lb.ModifiersRating.FSTechRating = (float)response["FS"].LackMapCalculation.TechRating;
                    // lb.ModifiersRating.FSPredictedAcc = (float)response["FS"].PredictedAcc;
                    // lb.ModifiersRating.FSAccRating = (float)response["FS"].AccRating;

                    lb.ModifiersRating.SFPassRating = (float)response["SFS"].LackMapCalculation.PassRating;
                    lb.ModifiersRating.SFTechRating = (float)response["SFS"].LackMapCalculation.TechRating;
                    // lb.ModifiersRating.SFPredictedAcc = (float)response["SFS"].PredictedAcc;
                    // lb.ModifiersRating.SFAccRating = (float)response["SFS"].AccRating;

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
        }
    }
}