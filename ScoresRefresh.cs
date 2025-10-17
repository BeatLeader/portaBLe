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
                    lb.StaminaRating,
                    lb.ModifiersRating,
                    Scores = lb.Scores.Select(s => new {s.Id, s.LeaderboardId, s.Accuracy, s.Modifiers })
                }).ToAsyncEnumerable();

            List<Score> newTotalScores = new();
            List<Score> newScores = new();
            await foreach (var leaderboard in allLeaderboards)
            {
                foreach (var s in leaderboard.Scores)
                {
                    (float pp, float bonuspp, float passPP, float accPP, float techPP, float staminaPP) = ReplayUtils.PpFromScore(
                        s.Accuracy,
                        s.Modifiers,
                        leaderboard.ModifiersRating,
                        leaderboard.AccRating,
                        leaderboard.PassRating,
                        leaderboard.TechRating,
                        leaderboard.StaminaRating);

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
                        StaminaPP = staminaPP,
                    });
                }

                foreach ((int i, Score? s) in newScores.OrderByDescending(el => Math.Round(el.Pp, 2)).ThenByDescending(el => Math.Round(el.Accuracy, 4)).Select((value, i) => (i, value)))
                {
                    s.Rank = i + 1;
                }

                newTotalScores.AddRange(newScores);
                newScores.Clear();
            };

            await dbContext.BulkUpdateAsync(newTotalScores, options => options.ColumnInputExpression = c => new { c.Rank, c.Pp, c.BonusPp, c.PassPP, c.AccPP, c.TechPP, c.StaminaPP });
            dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
        }
    }
}
