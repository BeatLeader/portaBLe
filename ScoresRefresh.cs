using Dasync.Collections;

namespace portaBLe
{
    public class ScoresRefresh
    {
        public static async Task Refresh(AppContext dbContext)
        {
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

            var allLeaderboards = dbContext.Leaderboards
                .Select(lb => new {
                    lb.AccRating,
                    lb.PassRating,
                    lb.TechRating,
                    lb.ModifiersRating,
                    Scores = lb.Scores.Select(s => new {s.Id, s.LeaderboardId, s.Accuracy, s.Modifiers })
                }).ToAsyncEnumerable();

            List<Score> newTotalScores = new();
            List<Score> newScores = new();
            await foreach (var leaderboard in allLeaderboards)
            {
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

                    newScores.Add(new() { 
                        Id = s.Id,
                        Pp = pp,
                        BonusPp = bonuspp,
                        PassPP = passPP,
                        AccPP = accPP,
                        TechPP = techPP,
                    });
                }

                foreach ((int i, Score? s) in newScores.OrderByDescending(el => Math.Round(el.Pp, 2)).ThenByDescending(el => Math.Round(el.Accuracy, 4)).Select((value, i) => (i, value)))
                {
                    s.Rank = i + 1;
                }

                newTotalScores.AddRange(newScores);
                newScores.Clear();
            };

            await dbContext.BulkUpdateAsync(newTotalScores, options => options.ColumnInputExpression = c => new { c.Rank, c.Pp, c.BonusPp, c.PassPP, c.AccPP, c.TechPP });
            dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
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
                    var value = 1.0f - (mm125 - 0.65f) * 0.35f;

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
        }

        public static async Task Autoreweight3(AppContext dbContext)
        {
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
        }
    }
}
