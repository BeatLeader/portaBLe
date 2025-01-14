using Dasync.Collections;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace portaBLe
{
    public class LeaderboardRefresh
    {
        public static async Task RefreshStars(AppContext dbContext) {

            var lbs = dbContext
                .Leaderboards
                .AsNoTracking()
                .Select(s => new Leaderboard { 
                    Id = s.Id, 
                    AccRating = s.AccRating,
                    PassRating = s.PassRating,
                    TechRating = s.TechRating
                })
                .ToList();
            foreach (var lb in lbs)
            {
                lb.Stars = ReplayUtils.ToStars(lb.AccRating, lb.PassRating, lb.TechRating);
            }

            await dbContext.BulkUpdateAsync(lbs, options => options.ColumnInputExpression = c => new { c.Stars });

            var mods = dbContext
                .ModifiersRating
                .AsNoTracking()
                .Select(s => new ModifiersRating { 
                    Id = s.Id, 
                    SSAccRating = s.SSAccRating,
                    SSPassRating = s.SSPassRating,
                    SSTechRating = s.SSTechRating,
                    SFAccRating = s.SFAccRating,
                    SFPassRating = s.SFPassRating,
                    SFTechRating = s.SFTechRating,
                    FSAccRating = s.FSAccRating,
                    FSPassRating = s.FSPassRating,
                    FSTechRating = s.FSTechRating
                })
                .ToList();
            foreach (var mod in mods)
            {
                mod.SSStars = ReplayUtils.ToStars(mod.SSAccRating, mod.SSPassRating, mod.SSTechRating);
                mod.SFStars = ReplayUtils.ToStars(mod.SFAccRating, mod.SFPassRating, mod.SFTechRating);
                mod.FSStars = ReplayUtils.ToStars(mod.FSAccRating, mod.FSPassRating, mod.FSTechRating);
            }

            await dbContext.BulkUpdateAsync(mods, options => options.ColumnInputExpression = c => new { c.SSStars, c.SFStars, c.FSStars });
        }
    }
}
