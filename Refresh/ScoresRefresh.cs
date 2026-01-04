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

            // Get all leaderboard IDs first to enable batch processing
            var leaderboardIds = await dbContext.Leaderboards
                .Select(lb => lb.Id)
                .ToListAsync();

            Console.WriteLine($"Processing {leaderboardIds.Count} leaderboards in batches of {LEADERBOARD_BATCH_SIZE}");

            var allProcessedScores = new ConcurrentBag<Score>();
            int processedCount = 0;

            // Process leaderboards in batches
            for (int i = 0; i < leaderboardIds.Count; i += LEADERBOARD_BATCH_SIZE)
            {
                var batchIds = leaderboardIds.Skip(i).Take(LEADERBOARD_BATCH_SIZE).ToList();
                
                // Load batch data
                var batchLeaderboards = await dbContext.Leaderboards
                    .Where(lb => batchIds.Contains(lb.Id))
                    .Select(lb => new
                    {
                        lb.Id,
                        lb.AccRating,
                        lb.PassRating,
                        lb.TechRating,
                        lb.ModifiersRating,
                        Scores = lb.Scores.Select(s => new { s.Id, s.Accuracy, s.Modifiers }).ToList()
                    })
                    .ToListAsync();

                // Process leaderboards in parallel
                var batchScores = new ConcurrentBag<Score>();
                
                await Parallel.ForEachAsync(batchLeaderboards, 
                    new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount },
                    async (leaderboard, ct) =>
                    {
                        var leaderboardScores = new List<Score>(leaderboard.Scores.Count);

                        // Calculate PP for all scores
                        foreach (var s in leaderboard.Scores)
                        {
                            (float pp, float bonuspp, float passPP, float accPP, float techPP) = ReplayUtils.PpFromScore(
                                s.Accuracy,
                                s.Modifiers,
                                leaderboard.ModifiersRating,
                                leaderboard.AccRating,
                                leaderboard.PassRating,
                                leaderboard.TechRating);

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

                        // Optimize ranking by pre-calculating rounded values
                        var rankedScores = leaderboardScores
                            .Select(s => new { Score = s, RoundedPp = Math.Round(s.Pp, 2), RoundedAcc = Math.Round(s.Accuracy, 4) })
                            .OrderByDescending(x => x.RoundedPp)
                            .ThenByDescending(x => x.RoundedAcc)
                            .ToList();

                        for (int rank = 0; rank < rankedScores.Count; rank++)
                        {
                            rankedScores[rank].Score.Rank = rank + 1;
                        }

                        // Add to concurrent bag
                        foreach (var score in leaderboardScores)
                        {
                            batchScores.Add(score);
                        }

                        await Task.CompletedTask;
                    });

                // Add batch to total
                foreach (var score in batchScores)
                {
                    allProcessedScores.Add(score);
                }

                processedCount += batchLeaderboards.Count;
                Console.WriteLine($"Processed {processedCount}/{leaderboardIds.Count} leaderboards ({(processedCount * 100 / leaderboardIds.Count)}%)");

                // Flush to database every batch to avoid memory issues
                if (allProcessedScores.Count >= UPDATE_BATCH_SIZE)
                {
                    var toUpdate = allProcessedScores.ToList();
                    allProcessedScores.Clear();
                    
                    await dbContext.BulkUpdateAsync(toUpdate, 
                        options => options.ColumnInputExpression = c => new { c.Rank, c.Pp, c.BonusPp, c.PassPP, c.AccPP, c.TechPP });
                }
            }

            // Final update for remaining scores
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

        public class PlayerSelect
        {
            public string PlayerId { get; set; }
            public float Weight { get; set; }
            public float Pp { get; set; }
            public bool Bigger40 { get; set; }
            public float TopPp { get; set; }
        }

        public static async Task Autoreweight(AppContext dbContext)
        {
            Console.WriteLine("Applying Autoreweight Nerf");
            var leaderboards = dbContext
                .Leaderboards
                .Where(lb =>
                    lb.Scores.Count > 40)
                .Select(lb => new
                {
                    //AverageWeight = lb.Scores.Average(s => s.Weight),
                    //Percentile = lb.Scores.Select(s => s.Weight),
                    Megametric = lb.Scores.Select(s => new PlayerSelect { Weight = s.Weight, Pp = s.Pp, PlayerId = s.PlayerId }),
                    //TopPP = lb.Scores
                    //    .Where(s => s.Player.ScoreStats.RankedPlayCount >= 50 && s.Player.ScoreStats.TopPp != 0)
                    //  .Average(s => s.Pp / s.Player.ScoreStats.TopPp),
                    lb.ModifiersRating,
                    leaderboard = lb
                })
                .ToList();

            var topScores = dbContext.Scores.Select(s => new { s.PlayerId, s.Pp }).ToList().GroupBy(s => s.PlayerId).ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Pp).First());
            var topPlayers = dbContext.Players.Where(p => dbContext.Scores.Where(s => s.PlayerId == p.Id).Count() > 125).Select(p => p.Id).ToDictionary(s => s, s => true);

            foreach (var lb in leaderboards)
            {
                var m125 = lb.Megametric.Where(s => topPlayers.ContainsKey(s.PlayerId)).OrderByDescending(s => s.Weight).Take((int)(((double)lb.Megametric.Count()) * 0.33));
                var mm125 = m125.Count() > 10 ? m125.Average(s => (s.Pp / topScores[s.PlayerId].Pp) * s.Weight) : 0;

                if (mm125 >= 0.65f)
                {

                    //var topAverage = lb.Percentile.OrderByDescending(s => s).Take((int)(((double)lb.Percentile.Count()) * 0.33)).Average(s => s);
                    //var adjustedTop = lb.TopPP - 0.5f;
                    var value = 1.0f - (mm125 - 0.65f) * 0.25f;

                    lb.leaderboard.AccRating *= value;
                    //lb.Difficulty.PassRating *= 1.0f - value * 0.4f;
                    if (lb.ModifiersRating != null)
                    {
                        lb.ModifiersRating.FSAccRating *= value;
                        //lb.ModifiersRating.FSPassRating *= 1.0f - value * 1.2f * 0.6f;
                        lb.ModifiersRating.SFAccRating *= value;
                        lb.ModifiersRating.SSAccRating *= value;
                        //lb.ModifiersRating.SFPassRating *=1.0f - value * 1.4f * 0.6f;
                    }
                }
            }
            await dbContext.SaveChangesAsync();
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }

        public static async Task Autoreweight3(AppContext dbContext)
        {
            Console.WriteLine("Applying Autoreweight Buff");
            var leaderboards = dbContext
                .Leaderboards
                .Where(lb =>
                    lb.Scores.Count > 100)
                .Select(lb => new
                {
                    //AverageWeight = lb.Scores.Average(s => s.Weight),
                    //Percentile = lb.Scores.Select(s => s.Weight),
                    Megametric = lb.Scores.Select(s => new { s.Weight, s.Pp }),
                    //TopPP = lb.Scores
                    //    .Where(s => s.Player.ScoreStats.RankedPlayCount >= 50 && s.Player.ScoreStats.TopPp != 0)
                    //  .Average(s => s.Pp / s.Player.ScoreStats.TopPp),

                    lb.ModifiersRating,
                    leaderboard = lb
                })
                .ToList();
            int i = 0;
            foreach (var lb in leaderboards)
            {
                var l = lb.Megametric.OrderByDescending(s => s.Weight).Take((int)(((double)lb.Megametric.Count()) * 0.33));
                var ll = l.Count() > 10 ? l.Average(s => s.Weight) : 0;

                if (ll < 0.5f)
                {

                    //var topAverage = lb.Percentile.OrderByDescending(s => s).Take((int)(((double)lb.Percentile.Count()) * 0.33)).Average(s => s);
                    //var adjustedTop = lb.TopPP - 0.5f;
                    var value = 1.0f + (0.5f - ll) * 0.28f;

                    lb.leaderboard.AccRating *= value;
                    if (lb.ModifiersRating != null)
                    {
                        lb.ModifiersRating.FSAccRating *= value;
                        lb.ModifiersRating.SFAccRating *= value;
                        lb.ModifiersRating.SSAccRating *= value;
                    }
                }
            }
            await dbContext.SaveChangesAsync();
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }
    }
}
