using Dasync.Collections;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;

namespace portaBLe.Refresh
{
    public class LeaderboardsRefresh
    {
        public static async Task Refresh(AppContext dbContext) {
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            
            var weights = new Dictionary<int, float>();
            for (int i = 0; i < 10000; i++)
            {
                weights[i] = MathF.Pow(0.965f, i);
            }

            float weightTreshold = MathF.Pow(0.965f, 40);

            var leaderboards = dbContext.Scores.Select(s => new {
                s.LeaderboardId,
                s.Weight,
                s.Pp,
                s.Player.TopPp,
                s.Player.RankedPlayCount,
                s.Player.Rank
            }).ToList()
            .GroupBy(s => s.LeaderboardId)
            .Select(g => new
                {
                    Average = g.Average(s => s.Weight),
                    Megametric = g.Where(s => s.TopPp != 0).Select(s => new { s.Weight, s.RankedPlayCount, s.Pp, s.TopPp }),
                    Count8 = g.Where(s => s.Weight > 0.8).Count(),
                    Count95 = g.Where(s => s.Weight > 0.95).Count(),
                    PPsum = g.Sum(s => s.Pp * s.Weight),

                    PPAverage = g.Where(s => s.RankedPlayCount >= 50 && s.TopPp != 0).Average(s => s.Pp / s.TopPp),
                    PPAverage2 = g.Where(s => s.TopPp != 0).Average(s => s.Pp / s.TopPp),
                    Count = g.Count(),
                    Top250 = g.Where(s => s.Rank < 250 && s.Weight > weightTreshold).Count(),
                    Id = g.Key,
                })
            .ToList();

            var updates = new List<Leaderboard>();

            foreach (var item in leaderboards)
            {
                var l = item.Megametric.OrderByDescending(s => s.Weight).Take((int)(item.Megametric.Count() * 0.33));
                var ll = l.Count() > 10 ? l.Average(s => s.Weight) : 0;

                var m = item.Megametric.OrderByDescending(s => s.Weight).Take((int)(item.Megametric.Count() * 0.33)).Where(s => s.RankedPlayCount > 75);
                var mm = m.Count() > 10 ? m.Average(s => s.Pp / s.TopPp * s.Weight) : 0;

                var m2 = item.Megametric.Where(s => s.RankedPlayCount > 125).OrderByDescending(s => s.Weight).Take((int)(item.Megametric.Count() * 0.33));
                var mm2 = m2.Count() > 10 ? m2.Average(s => s.Pp / s.TopPp * s.Weight) : 0;

                var m3 = item.Megametric.Where(s => s.RankedPlayCount > 75).OrderByDescending(s => s.Weight).Take((int)(item.Megametric.Count() * 0.33));
                var mm3 = m3.Count() > 10 ? m3.Average(s => s.Pp / s.TopPp * s.Weight) : 0;

                var m4 = item.Megametric.Where(s => s.RankedPlayCount > 40).OrderByDescending(s => s.Weight).Take((int)(item.Megametric.Count() * 0.33));
                var mm4 = m4.Count() > 10 ? m4.Average(s => s.Pp / s.TopPp * s.Weight) : 0;

                updates.Add(new Leaderboard {
                    Id = item.Id,
                    Count = item.Count,
                    Count80 = item.Count8,
                    Count95 = item.Count95,
                    Average = item.Average,
                    Percentile = ll,
                    Megametric = mm,
                    Megametric125 = mm2,
                    Megametric75 = mm3,
                    Megametric40 = mm4,
                    Top250 = item.Top250,
                    TotalPP = item.PPsum,
                    PPRatioFiltered = item.PPAverage,
                    PPRatioUnfiltered = item.PPAverage
                });
            }
            await dbContext.BulkUpdateAsync(updates, options => options.ColumnInputExpression = c => 
                new { 
                    c.Count, 
                    c.Count80, 
                    c.Count95, 
                    c.Average,
                    c.Top250,
                    c.TotalPP,
                    c.Percentile,
                    c.PPRatioFiltered,
                    c.PPRatioUnfiltered,
                    c.Megametric,
                    c.Megametric40,
                    c.Megametric75,
                    c.Megametric125
                });
        }

        public static async Task RefreshStars(AppContext dbContext)
        {
            var lbs = dbContext
                .Leaderboards
                .AsNoTracking()
                .Include(s => s.AccCurve)
                .Select(s => new Leaderboard
                {
                    Id = s.Id,
                    AccRating = s.AccRating,
                    PassRating = s.PassRating,
                    TechRating = s.TechRating,
                    AccCurve = s.AccCurve
                })
                .ToList();

            foreach (var lb in lbs)
            {
                lb.Stars = ReplayUtils.ToStars(lb.AccRating, lb.PassRating, lb.TechRating, lb.AccCurve.ToList());
            }

            await dbContext.BulkUpdateAsync(lbs, options => options.ColumnInputExpression = c => new { c.Stars });

            var mods = dbContext
                .Leaderboards
                .Select(l => l.ModifiersRating)
                .AsNoTracking()
                .ToList();

            List<Point> accCurve = lbs.First().AccCurve.ToList();

            foreach (var mod in mods)
            {
                mod.SSStars = ReplayUtils.ToStars(mod.SSAccRating, mod.SSPassRating, mod.SSTechRating, accCurve);
                mod.SFStars = ReplayUtils.ToStars(mod.SFAccRating, mod.SFPassRating, mod.SFTechRating, accCurve);
                mod.FSStars = ReplayUtils.ToStars(mod.FSAccRating, mod.FSPassRating, mod.FSTechRating, accCurve);
            }

            await dbContext.BulkUpdateAsync(mods, options => options.ColumnInputExpression = c => new { c.SSStars, c.SFStars, c.FSStars });
        }
    }
}
