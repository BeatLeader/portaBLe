using Dasync.Collections;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using System.Diagnostics;

namespace portaBLe.Refresh
{
    public class PlayersRefresh
    {
        private static async Task<(List<Score>, List<Player>)> CalculateBatch(AppContext dbContext, IAsyncEnumerable<IGrouping<string, ScoreSelection>> groups, Dictionary<int, float> weights)
        {
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            var scoreUpdates = new List<Score>();
            var playerUpdates = new List<Player>();
            await foreach (var group in groups)
            {
                try {
                    Player player = new() { Id = group.Key };
                    playerUpdates.Add(player);

                    float resultPP = 0f;
                    float accPP = 0f;
                    float techPP = 0f;
                    float passPP = 0f;

                    float topPp = 0f;

                    foreach ((int i, var s) in group.OrderByDescending(s => s.Pp).Select((value, i) => (i, value)))
                    {
                        float weight = weights[i];
                        if (s.Weight != weight)
                        {
                            scoreUpdates.Add(new() { Id = s.Id, Weight = weight });
                        }
                        resultPP += s.Pp * weight;
                        accPP += s.AccPP * weight;
                        techPP += s.TechPP * weight;
                        passPP += s.PassPP * weight;

                        if (i == 0) {
                            topPp = s.Pp;
                        }
                    }
                    player.Pp = resultPP;
                    player.TopPp = topPp;
                    player.RankedPlayCount = group.Count();

                    player.AccPp = accPP;
                    player.TechPp = techPP;
                    player.PassPp = passPP;
                } catch (Exception) {
                }
            }

            return (scoreUpdates, playerUpdates);
        }

        public static async Task Refresh(AppContext dbContext) {
            Console.WriteLine("Recalculating Players");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            
            var weights = new Dictionary<int, float>();
            for (int i = 0; i < 10000; i++)
            {
                weights[i] = MathF.Pow(0.965f, i);
            }

            var scores = dbContext
                .Scores
                .Select(s => new ScoreSelection { 
                    Id = s.Id, 
                    Accuracy = s.Accuracy, 
                    Rank = s.Rank, 
                    Pp = s.Pp, 
                    AccPP = s.AccPP, 
                    TechPP = s.TechPP, 
                    PassPP = s.PassPP, 
                    Weight = s.Weight, 
                    PlayerId = s.PlayerId, 
                    Country = s.Player.Country 
                })
                .GroupBy(s => s.PlayerId)
                .ToAsyncEnumerable();

            (List<Score> scoreUpdates, List<Player> playerUpdates) = await CalculateBatch(dbContext, scores, weights);

            Dictionary<string, int> countries = new();
            foreach ((int i, Player p) in playerUpdates.OrderByDescending(p => p.Pp).Select((value, i) => (i, value)))
            {
                p.Rank = i + 1;
                if (p.Country != null) {
                    if (!countries.TryGetValue(p.Country, out int value))
                    {
                        countries[p.Country] = value = 1;
                    }

                    p.CountryRank = value;
                    countries[p.Country] = ++value;
                }
            }
            await dbContext.BulkUpdateAsync(scoreUpdates, options => options.ColumnInputExpression = c => new { c.Weight });
            await dbContext.BulkUpdateAsync(playerUpdates, options => options.ColumnInputExpression = c => new { c.Rank, c.Pp, c.TopPp, c.RankedPlayCount, c.CountryRank, c.AccPp, c.PassPp, c.TechPp });
        }
    }

    public class ScoreSelection {
        public int Id { get; set; } 
        public float Accuracy { get; set; } 
        public int Rank { get; set; } 
        public float Pp { get; set; } 
        public float AccPP { get; set; }  
        public float TechPP { get; set; } 
        public float PassPP { get; set; } 
        public float Weight { get; set; } 
        public string PlayerId { get; set; } 
        public string Country { get; set; } 
    }
}
