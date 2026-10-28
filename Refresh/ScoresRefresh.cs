using Dasync.Collections;
using portaBLe.DB;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace portaBLe.Refresh
{
    public class ScoresRefresh
    {
        private const int LEADERBOARD_BATCH_SIZE = 1000;
        private const int UPDATE_BATCH_SIZE = 5000;

        public static async Task Refresh(AppContext dbContext)
        {
            Console.WriteLine("Recalculating Scores");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

            var leaderboardIds = await dbContext.Leaderboards
                .Select(lb => lb.Id)
                .ToListAsync();

            Console.WriteLine($"Processing {leaderboardIds.Count} leaderboards in batches of {LEADERBOARD_BATCH_SIZE}");

            var allProcessedScores = new ConcurrentBag<Score>();
            int processedCount = 0;

            for (int i = 0; i < leaderboardIds.Count; i += LEADERBOARD_BATCH_SIZE)
            {
                var batchIds = leaderboardIds.Skip(i).Take(LEADERBOARD_BATCH_SIZE).ToList();
                
                var batchLeaderboards = await dbContext.Leaderboards
                    .Where(lb => batchIds.Contains(lb.Id))
                    .Select(lb => new
                    {
                        lb.Id,
                        lb.AccRating,
                        lb.PassRating,
                        lb.TechRating,
                        lb.Curve,
                        lb.ModifiersRating,
                        Scores = lb.Scores.Select(s => new { s.Id, s.Accuracy, s.Modifiers }).ToList()
                    })
                    .ToListAsync();

                var batchScores = new ConcurrentBag<Score>();
                
                await Parallel.ForEachAsync(batchLeaderboards, 
                    new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount },
                    async (leaderboard, ct) =>
                    {
                        var leaderboardScores = new List<Score>(leaderboard.Scores.Count);

                        foreach (var s in leaderboard.Scores)
                        {
                            (float pp, float bonuspp, float passPP, float accPP, float techPP) = ReplayUtils.PpFromScore(
                                s.Accuracy,
                                s.Modifiers,
                                leaderboard.ModifiersRating,
                                leaderboard.AccRating,
                                leaderboard.PassRating,
                                leaderboard.TechRating,
                                leaderboard.Curve);

                            if (float.IsNaN(pp))
                            {
                                pp = 0.0f;
                            }

                            leaderboardScores.Add(new Score
                            {
                                Id = s.Id,
                                Accuracy = s.Accuracy,
                                Pp = pp,
                                BonusPp = bonuspp,
                                PassPP = passPP,
                                AccPP = accPP,
                                TechPP = techPP,
                            });
                        }

                        // Ranked by rounded pp, then rounded accuracy
                        var rankedScores = leaderboardScores
                            .Select(s => new { Score = s, RoundedPp = Math.Round(s.Pp, 2), RoundedAcc = Math.Round(s.Accuracy, 4) })
                            .OrderByDescending(x => x.RoundedPp)
                            .ThenByDescending(x => x.RoundedAcc)
                            .ToList();

                        for (int rank = 0; rank < rankedScores.Count; rank++)
                        {
                            rankedScores[rank].Score.Rank = rank + 1;
                        }

                        foreach (var score in leaderboardScores)
                        {
                            batchScores.Add(score);
                        }

                        await Task.CompletedTask;
                    });

                foreach (var score in batchScores)
                {
                    allProcessedScores.Add(score);
                }

                processedCount += batchLeaderboards.Count;
                Console.WriteLine($"Processed {processedCount}/{leaderboardIds.Count} leaderboards ({(processedCount * 100 / leaderboardIds.Count)}%)");

                // Flush every batch to keep memory down
                if (allProcessedScores.Count >= UPDATE_BATCH_SIZE)
                {
                    var toUpdate = allProcessedScores.ToList();
                    allProcessedScores.Clear();
                    
                    await dbContext.BulkUpdateAsync(toUpdate, 
                        options => options.ColumnInputExpression = c => new { c.Rank, c.Pp, c.BonusPp, c.PassPP, c.AccPP, c.TechPP });
                }
            }

            if (allProcessedScores.Count > 0)
            {
                var finalScores = allProcessedScores.ToList();
                await dbContext.BulkUpdateAsync(finalScores, 
                    options => options.ColumnInputExpression = c => new { c.Rank, c.Pp, c.BonusPp, c.PassPP, c.AccPP, c.TechPP });
                
                Console.WriteLine($"Updated final {finalScores.Count} scores in database");
            }

            dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
            Console.WriteLine($"Complete! Total time: {Program.Stopwatch.ElapsedMilliseconds / 1000} seconds");
        }
    }
}
