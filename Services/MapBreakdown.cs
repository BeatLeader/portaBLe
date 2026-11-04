using portaBLe.DB;
using RatingAPI.Controllers;

namespace portaBLe.Services
{
    // RatingAPI's speed / tech / acc over time (RatingResult.Breakdown, see RatingBreakdown) as stored in LeaderboardBreakdowns:
    // each series as bytes, 255 = its max
    public static class MapBreakdown
    {
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
