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

        public static async Task Refresh(AppContext dbContext)
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
            foreach (var lb in lbs)
            {
                try
                {
                    var response = controller.Get(lb.Hash, lb.ModeName, GetDiffCode(lb.DifficultyName)).Value;

                    lb.PassRating = (float)response["none"].LackMapCalculation.PassRating;
                    lb.TechRating = (float)response["none"].LackMapCalculation.TechRating;
                    lb.PredictedAcc = (float)response["none"].PredictedAcc;
                    lb.AccRating = (float)response["none"].AccRating;
                    lb.Stars = ReplayUtils.ToStars(lb.AccRating, lb.PassRating, lb.TechRating);

                    var modrating = lb.ModifiersRating = new ModifiersRating
                    {
                        SSPassRating = (float)response["SS"].LackMapCalculation.PassRating,
                        SSTechRating = (float)response["SS"].LackMapCalculation.TechRating,
                        SSPredictedAcc = (float)response["SS"].PredictedAcc,
                        SSAccRating = (float)response["SS"].AccRating,

                        FSPassRating = (float)response["FS"].LackMapCalculation.PassRating,
                        FSTechRating = (float)response["FS"].LackMapCalculation.TechRating,
                        FSPredictedAcc = (float)response["FS"].PredictedAcc,
                        FSAccRating = (float)response["FS"].AccRating,

                        SFPassRating = (float)response["SFS"].LackMapCalculation.PassRating,
                        SFTechRating = (float)response["SFS"].LackMapCalculation.TechRating,
                        SFPredictedAcc = (float)response["SFS"].PredictedAcc,
                        SFAccRating = (float)response["SFS"].AccRating,
                    };

                    modrating.SFStars = ReplayUtils.ToStars(modrating.SFAccRating, modrating.SFPassRating, modrating.SFTechRating);
                    modrating.FSStars = ReplayUtils.ToStars(modrating.FSAccRating, modrating.FSPassRating, modrating.FSTechRating);
                    modrating.SSStars = ReplayUtils.ToStars(modrating.SSAccRating, modrating.SSPassRating, modrating.SSTechRating);
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