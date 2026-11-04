using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using RatingAPI.Controllers;

namespace portaBLe.Services
{
    // RatingAPI's speed / tech / acc over time (RatingResult.Breakdown, see RatingBreakdown) as stored in LeaderboardBreakdowns:
    // each series as bytes, 255 = its max
    public static class MapBreakdown
    {
        // Effective stars, the Leaderboard chart's scale: a part's raw value (a window's) at the rating of the maps that rank the
        // same on it. Each part has its own curve, fitted per database on the no modifier breakdowns: the leaderboards' average
        // raw value at each percentile against that part's rating at that percentile (speed: pass rating, tech: tech rating,
        // acc: acc rating). Not against stars: those are mostly acc on low star maps (none under ~2), which lifted a low star
        // map's speed and tech to its acc. Linear between the percentiles, from 0 at 0, and on with the top 5%'s slope above
        // the highest map average. Raw tech follows the tech rating loosely, so Scaled then sets each line's average to the
        // shown rating, the curve giving the shape over time.
        public class EffectiveStars
        {
            private const int Percentiles = 100;
            private readonly (double Raw, double Stars)[][] _curves;   // speed, tech, acc

            private EffectiveStars((double, double)[][] curves) => _curves = curves;

            public double[] Speed(double[] raw, double rating) => Scaled(_curves[0], raw, rating);
            public double[] Tech(double[] raw, double rating) => Scaled(_curves[1], raw, rating);
            public double[] Acc(double[] raw, double rating) => Scaled(_curves[2], raw, rating);

            // The curve's values, scaled so they average to the rating (the shown modifier's: a speed modifier's lines rise)
            private static double[] Scaled((double Raw, double Stars)[] curve, double[] raw, double rating)
            {
                var stars = raw.Select(v => At(curve, v)).ToArray();
                double mean = stars.Length > 0 ? stars.Average() : 0;
                return stars.Select(v => mean > 0 ? Math.Round(v / mean * rating, 3) : 0).ToArray();
            }

            private static double At((double Raw, double Stars)[] curve, double v)
            {
                if (curve.Length == 0 || v <= 0) return 0;
                var (x0, y0) = (0.0, 0.0);
                foreach (var (x1, y1) in curve)
                {
                    if (v <= x1) return y0 + (y1 - y0) * (v - x0) / (x1 - x0);
                    (x0, y0) = (x1, y1);
                }
                var (fromX, fromY) = curve[Math.Max(0, curve.Length - 1 - Percentiles / 20)];
                double slope = x0 > fromX ? (y0 - fromY) / (x0 - fromX) : 0;
                return y0 + slope * (v - x0);
            }

            private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<EffectiveStars>>> _byDatabase = new();

            // The database's curves, fitted once (key: its file name)
            public static Task<EffectiveStars> ForDatabaseAsync(string database, Func<DynamicDbContext> contextFactory) =>
                _byDatabase.GetOrAdd(database ?? "", _ => new Lazy<Task<EffectiveStars>>(() => FitAsync(contextFactory))).Value;

            private static async Task<EffectiveStars> FitAsync(Func<DynamicDbContext> contextFactory)
            {
                using var context = contextFactory();
                var ratings = await context.Leaderboards.AsNoTracking().Where(l => l.Stars > 0)
                    .ToDictionaryAsync(l => l.Id, l => new[] { (double)l.PassRating, l.TechRating, l.AccRating });
                var means = new List<double>[] { new(), new(), new() };
                var targets = new List<double>[] { new(), new(), new() };
                await foreach (var b in context.LeaderboardBreakdowns.AsNoTracking().Where(b => b.Id.EndsWith("/none")).AsAsyncEnumerable())
                {
                    if (!ratings.TryGetValue(b.Id[..b.Id.LastIndexOf('/')], out var r) || b.Speed.Length == 0) continue;
                    // Average of the stored series: its bytes' average over 255 times its max
                    means[0].Add(b.Speed.Average(x => (double)x) / 255 * b.SpeedMax);
                    means[1].Add(b.Tech.Average(x => (double)x) / 255 * b.TechMax);
                    means[2].Add(b.Acc.Average(x => (double)x) / 255 * b.AccMax);
                    for (int i = 0; i < 3; i++) targets[i].Add(r[i]);
                }
                (double, double)[] Curve(List<double> raw, List<double> rating)
                {
                    raw.Sort();
                    rating.Sort();
                    var curve = new List<(double, double)>();
                    for (int q = 0; q <= Percentiles && raw.Count > 0; q++)
                    {
                        double x = raw[q * (raw.Count - 1) / Percentiles], y = rating[q * (rating.Count - 1) / Percentiles];
                        // Rising only: a percentile with the same raw value as the one before is skipped
                        if (x > 0 && (curve.Count == 0 || x > curve[^1].Item1)) curve.Add((x, y));
                    }
                    return curve.ToArray();
                }
                return new EffectiveStars(means.Select((raw, i) => Curve(raw, targets[i])).ToArray());
            }
        }

        public static LeaderboardBreakdown Encode(string id, RatingBreakdown breakdown)
        {
            (float Max, byte[] Bytes) Quantize(double[] values)
            {
                double max = values.Length > 0 ? values.Max() : 0;
                var bytes = values.Select(v => max > 0 ? (byte)Math.Round(Math.Clamp(v / max, 0, 1) * 255) : (byte)0).ToArray();
                return ((float)max, bytes);
            }
            var (speedMax, speed) = Quantize(breakdown.Speed);
            var (techMax, tech) = Quantize(breakdown.Tech);
            var (accMax, acc) = Quantize(breakdown.Acc);
            return new LeaderboardBreakdown
            {
                Id = id, Start = (float)breakdown.Start,
                SpeedMax = speedMax, TechMax = techMax, AccMax = accMax,
                Speed = speed, Tech = tech, Acc = acc,
            };
        }

        public static RatingBreakdown Decode(LeaderboardBreakdown stored)
        {
            double[] Expand(byte[] bytes, float max) => bytes.Select(b => b / 255.0 * max).ToArray();
            return new RatingBreakdown
            {
                Start = stored.Start,
                Speed = Expand(stored.Speed, stored.SpeedMax),
                Tech = Expand(stored.Tech, stored.TechMax),
                Acc = Expand(stored.Acc, stored.AccMax),
            };
        }
    }
}
