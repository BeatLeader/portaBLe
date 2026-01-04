using Dasync.Collections;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using System.Collections.Concurrent;

namespace portaBLe.Refresh
{
    public class PlayersRefresh
    {
        private const int PLAYER_BATCH_SIZE = 2000;
        private const int UPDATE_BATCH_SIZE = 10000;

        private static (List<Score> scoreUpdates, Player player) ProcessPlayer(
            IGrouping<string, ScoreSelection> group, 
            Dictionary<int, float> weights)
        {
            var scoreUpdates = new List<Score>();
            var player = new Player { Id = group.Key };

            float resultPP = 0f;
            float accPP = 0f;
            float techPP = 0f;
            float passPP = 0f;
            float topPp = 0f;
            string country = null;

            var orderedScores = group.OrderByDescending(s => s.Pp).ToList();

            for (int i = 0; i < orderedScores.Count; i++)
            {
                var s = orderedScores[i];
                float weight = i < weights.Count ? weights[i] : 0f;
                
                if (Math.Abs(s.Weight - weight) > 0.0001f)
                {
                    scoreUpdates.Add(new Score { Id = s.Id, Weight = weight });
                }
                
                resultPP += s.Pp * weight;
                accPP += s.AccPP * weight;
                techPP += s.TechPP * weight;
                passPP += s.PassPP * weight;

                if (i == 0)
                {
                    topPp = s.Pp;
                    country = s.Country;
                }
            }

            player.Pp = resultPP;
            player.TopPp = topPp;
            player.RankedPlayCount = orderedScores.Count;
            player.AccPp = accPP;
            player.TechPp = techPP;
            player.PassPp = passPP;
            player.Country = country;

            return (scoreUpdates, player);
        }

        public static async Task Refresh(AppContext dbContext)
        {
            Console.WriteLine("Recalculating Players");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

            // Pre-calculate weights
            var weights = new Dictionary<int, float>(10000);
            for (int i = 0; i < 10000; i++)
            {
                weights[i] = MathF.Pow(0.965f, i);
            }

            // Get all player IDs for batch processing
            var playerIds = await dbContext.Players
                .Select(p => p.Id)
                .ToListAsync();

            Console.WriteLine($"Processing {playerIds.Count} players in batches of {PLAYER_BATCH_SIZE}");

            var allScoreUpdates = new ConcurrentBag<Score>();
            var allPlayerUpdates = new ConcurrentBag<Player>();
            int processedCount = 0;

            // Process players in batches
            for (int i = 0; i < playerIds.Count; i += PLAYER_BATCH_SIZE)
            {
                var batchPlayerIds = playerIds.Skip(i).Take(PLAYER_BATCH_SIZE).ToList();

                // Load batch data
                var batchScores = await dbContext.Scores
                    .Where(s => batchPlayerIds.Contains(s.PlayerId))
                    .Select(s => new ScoreSelection
                    {
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
                    .ToListAsync();

                // Group by player
                var playerGroups = batchScores.GroupBy(s => s.PlayerId).ToList();

                // Process players in parallel
                await Parallel.ForEachAsync(playerGroups,
                    new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount },
                    async (group, ct) =>
                    {
                        try
                        {
                            var (scoreUpdates, player) = ProcessPlayer(group, weights);

                            foreach (var score in scoreUpdates)
                            {
                                allScoreUpdates.Add(score);
                            }
                            allPlayerUpdates.Add(player);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error processing player {group.Key}: {ex.Message}");
                        }

                        await Task.CompletedTask;
                    });

                processedCount += playerGroups.Count;
                Console.WriteLine($"Processed {processedCount}/{playerIds.Count} players ({processedCount * 100 / playerIds.Count}%)");

                // Flush score updates if batch is large enough
                if (allScoreUpdates.Count >= UPDATE_BATCH_SIZE)
                {
                    var toUpdate = allScoreUpdates.ToList();
                    allScoreUpdates.Clear();

                    await dbContext.BulkUpdateAsync(toUpdate,
                        options => options.ColumnInputExpression = c => new { c.Weight });
                }
            }

            // Calculate global rankings
            Console.WriteLine("Calculating global and country rankings...");
            var playerList = allPlayerUpdates.ToList();
            var countries = new Dictionary<string, int>();

            var rankedPlayers = playerList
                .OrderByDescending(p => p.Pp)
                .ToList();

            for (int i = 0; i < rankedPlayers.Count; i++)
            {
                var player = rankedPlayers[i];
                player.Rank = i + 1;

                if (!string.IsNullOrEmpty(player.Country))
                {
                    if (!countries.TryGetValue(player.Country, out int countryRank))
                    {
                        countryRank = 0;
                    }
                    player.CountryRank = ++countryRank;
                    countries[player.Country] = countryRank;
                }
            }

            // Final bulk updates
            Console.WriteLine("Writing final updates to database...");

            if (allScoreUpdates.Count > 0)
            {
                var finalScoreUpdates = allScoreUpdates.ToList();
                await dbContext.BulkUpdateAsync(finalScoreUpdates,
                    options => options.ColumnInputExpression = c => new { c.Weight });
                Console.WriteLine($"Updated final {finalScoreUpdates.Count} score weights");
            }

            await dbContext.BulkUpdateAsync(playerList,
                options => options.ColumnInputExpression = c => new { c.Rank, c.Pp, c.TopPp, c.RankedPlayCount, c.CountryRank, c.AccPp, c.PassPp, c.TechPp });

            dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
            Console.WriteLine($"Complete! Total time: {Program.Stopwatch.ElapsedMilliseconds / 1000} seconds");
        }
    }

    public class ScoreSelection
    {
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
