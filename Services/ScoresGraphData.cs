using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;

namespace portaBLe.Services
{
    // Data for the Weighted / Unweighted Scores Graph pages, read from the Top Scores cache (Services/TopScoresCache), never
    // the scores table.
    // - Dots: the filtered scores thinned to one per cell of a 500 x 500 stars x pp grid, sent as columns.
    // - Rolling averages: over every filtered score, not only the dots. For each x axis the scores are sorted by it and cut
    //   into AverageBins bins of equal count; each bin sends its mean x and the mean of every y. The page averages
    //   neighbouring bins, so its window is a share of the scores and changing it doesn't reload anything.
    // What the cache doesn't hold (ranked play counts, per-speed acc / pass / tech ratings) is read once per cache version
    // from the small Players and Leaderboards tables.
    public static class ScoresGraphData
    {
        public const int AverageBins = 2000;    // 0.05% of the scores each
        private const int GridBins = 500;

        // Same keys as the page's axis selects
        private static readonly string[] XFields = { "x", "accRating", "passRating", "techRating" };
        private static readonly string[] YFields = { "pp", "acc", "accPP", "passPP", "techPP" };

        public class Filters
        {
            public bool Weighted;
            public float MinWeight;                 // 0..1, weighted page only
            public int MinDay = int.MinValue;       // TopScoresCache.DayNumber, int.MinValue = unfiltered
            public int MaxPlayerRank;               // 0 = unfiltered
            public int MinRankedPlayCount;
            public string PlayerId = "";
            public bool[] Speeds = { true, true, true, true };   // none, SS, FS, SFS
        }

        private class Extras
        {
            public int[] RankedPlayCount = Array.Empty<int>();   // per cache player
            public float[] Ratings = Array.Empty<float>();        // [(map * 4 + speed) * 3 + k]: acc, pass, tech
        }

        // Keyed by the cache snapshot: a new database version gets a new snapshot, so these are read again
        private static readonly ConditionalWeakTable<TopScoresCache.Snapshot, Task<Extras>> _extras = new();

        private static Task<Extras> ExtrasAsync(TopScoresCache.Snapshot d, Func<DbContext> contextFactory)
        {
            lock (_extras)
            {
                if (_extras.TryGetValue(d, out var task) && !task.IsFaulted && !task.IsCanceled) return task;
                task = LoadExtrasAsync(d, contextFactory);
                _extras.AddOrUpdate(d, task);
                return task;
            }
        }

        private static async Task<Extras> LoadExtrasAsync(TopScoresCache.Snapshot d, Func<DbContext> contextFactory)
        {
            using var context = contextFactory();
            var connection = context.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();
            static float Flt(DbDataReader r, int i) => r.IsDBNull(i) ? 0f : (float)r.GetDouble(i);

            var extras = new Extras { RankedPlayCount = new int[d.Players.Length], Ratings = new float[d.Maps.Length * 4 * 3] };

            var playerIndex = new Dictionary<string, int>(d.Players.Length);
            for (int p = 0; p < d.Players.Length; p++) playerIndex[d.Players[p].Id] = p;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT Id, RankedPlayCount FROM Players";
                using var r = await command.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    if (!r.IsDBNull(0) && !r.IsDBNull(1) && playerIndex.TryGetValue(r.GetString(0), out int p))
                        extras.RankedPlayCount[p] = Convert.ToInt32(r.GetValue(1));
            }

            var mapIndex = new Dictionary<string, int>(d.Maps.Length);
            for (int m = 0; m < d.Maps.Length; m++) mapIndex[d.Maps[m].Id] = m;
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT l.Id, l.AccRating, l.PassRating, l.TechRating, " +
                    "m.SSAccRating, m.SSPassRating, m.SSTechRating, m.FSAccRating, m.FSPassRating, m.FSTechRating, " +
                    "m.SFAccRating, m.SFPassRating, m.SFTechRating " +
                    "FROM Leaderboards l LEFT JOIN ModifiersRating m ON m.Id = l.ModifiersRatingId";
                using var r = await command.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    if (r.IsDBNull(0) || !mapIndex.TryGetValue(r.GetString(0), out int m)) continue;
                    for (int speed = 0; speed < 4; speed++)
                        for (int k = 0; k < 3; k++)
                        {
                            float baseValue = Flt(r, 1 + k), value = speed == 0 ? baseValue : Flt(r, 1 + speed * 3 + k);
                            // A speed modifier that isn't rated uses the base rating (as ScoreModifierFilter.Pick)
                            extras.Ratings[(m * 4 + speed) * 3 + k] = value > 0 ? value : baseValue;
                        }
                }
            }
            return extras;
        }

        public static async Task<object> BuildAsync(TopScoresCache.Snapshot d, Func<DbContext> contextFactory, Filters f, bool building)
        {
            var extras = await ExtrasAsync(d, contextFactory);

            string playerId = (f.PlayerId ?? "").Trim();
            int onlyPlayer = playerId.Length == 0 ? -1 : Array.FindIndex(d.Players, p => p.Id == playerId);
            var playerOk = new bool[d.Players.Length];
            if (playerId.Length == 0 || onlyPlayer >= 0)
                for (int p = 0; p < d.Players.Length; p++)
                {
                    int rank = d.Players[p].Rank;
                    playerOk[p] = (f.MaxPlayerRank <= 0 || (rank > 0 && rank <= f.MaxPlayerRank))
                        && extras.RankedPlayCount[p] >= f.MinRankedPlayCount
                        && (onlyPlayer < 0 || p == onlyPlayer);
                }

            // The filtered scores
            var selected = new List<int>();
            for (int i = 0; i < d.Count; i++)
            {
                if (!playerOk[d.Player[i]] || !f.Speeds[d.Speed(i)]) continue;
                if (f.MinDay != int.MinValue && d.Day[i] < f.MinDay) continue;
                if (f.Weighted && d.Weight(i) < f.MinWeight) continue;
                selected.Add(i);
            }
            int n = selected.Count;

            // Their values: x axes (stars, acc / pass / tech rating) and y axes (pp, acc, acc / pass / tech pp)
            var xs = new float[XFields.Length][];
            var ys = new float[YFields.Length][];
            for (int a = 0; a < xs.Length; a++) xs[a] = new float[n];
            for (int a = 0; a < ys.Length; a++) ys[a] = new float[n];
            for (int k = 0; k < n; k++)
            {
                int i = selected[k];
                int g = (d.Map[i] * 4 + d.Speed(i)) * 3;
                float w = f.Weighted ? d.Weight(i) : 1f;
                xs[0][k] = Finite(d.Stars(i));
                xs[1][k] = Finite(extras.Ratings[g]);
                xs[2][k] = Finite(extras.Ratings[g + 1]);
                xs[3][k] = Finite(extras.Ratings[g + 2]);
                ys[0][k] = Finite(d.Pp[i] * w);
                ys[1][k] = Finite(d.Accuracy(i));
                ys[2][k] = Finite(d.AccPp(i) * w);
                ys[3][k] = Finite(d.PassPp(i) * w);
                ys[4][k] = Finite(d.TechPp(i) * w);
            }

            return new
            {
                building,
                count = n,
                dots = Dots(xs, ys, n),
                bins = Bins(xs, ys, n),
            };
        }

        // JSON can't hold NaN / infinity
        private static float Finite(float v) => float.IsFinite(v) ? v : 0f;

        // One score per cell of a 500 x 500 stars x pp grid (the first one met), each field as a column
        private static Dictionary<string, List<float>> Dots(float[][] xs, float[][] ys, int n)
        {
            var dots = new Dictionary<string, List<float>>();
            foreach (var key in XFields.Concat(YFields)) dots[key] = new List<float>();
            if (n == 0) return dots;

            float[] gx = xs[0], gy = ys[0];
            float minX = gx.Min(), maxX = gx.Max(), minY = gy.Min(), maxY = gy.Max();
            double xBin = (maxX - minX) / (double)GridBins, yBin = (maxY - minY) / (double)GridBins;
            if (xBin <= 0) xBin = 1;
            if (yBin <= 0) yBin = 1;

            var grid = new bool[GridBins * GridBins];
            for (int k = 0; k < n; k++)
            {
                int xi = Math.Clamp((int)((gx[k] - minX) / xBin), 0, GridBins - 1);
                int yi = Math.Clamp((int)((gy[k] - minY) / yBin), 0, GridBins - 1);
                if (grid[xi * GridBins + yi]) continue;
                grid[xi * GridBins + yi] = true;
                for (int a = 0; a < XFields.Length; a++) dots[XFields[a]].Add(MathF.Round(xs[a][k], 3));
                for (int a = 0; a < YFields.Length; a++) dots[YFields[a]].Add(MathF.Round(ys[a][k], a == 1 ? 5 : 2));
            }
            return dots;
        }

        // Per x axis: the scores sorted by it, cut into equal-count bins; each bin's count, mean x and mean of every y
        private static Dictionary<string, Dictionary<string, object>> Bins(float[][] xs, float[][] ys, int n)
        {
            var result = new Dictionary<string, Dictionary<string, object>>();
            int bins = Math.Min(AverageBins, n);
            var order = new int[n];
            var keys = new float[n];
            for (int a = 0; a < XFields.Length; a++)
            {
                for (int k = 0; k < n; k++) { order[k] = k; keys[k] = xs[a][k]; }
                Array.Sort(keys, order);

                var counts = new int[bins];
                var at = new float[bins];
                var means = new float[YFields.Length][];
                for (int y = 0; y < means.Length; y++) means[y] = new float[bins];
                for (int b = 0; b < bins; b++)
                {
                    int lo = (int)((long)b * n / bins), hi = (int)((long)(b + 1) * n / bins);
                    double sx = 0;
                    var sy = new double[YFields.Length];
                    for (int j = lo; j < hi; j++)
                    {
                        int k = order[j];
                        sx += keys[j];
                        for (int y = 0; y < sy.Length; y++) sy[y] += ys[y][k];
                    }
                    int c = hi - lo;
                    counts[b] = c;
                    at[b] = c > 0 ? MathF.Round((float)(sx / c), 4) : 0;
                    for (int y = 0; y < sy.Length; y++) means[y][b] = c > 0 ? MathF.Round((float)(sy[y] / c), y == 1 ? 6 : 3) : 0;
                }

                var axis = new Dictionary<string, object> { ["count"] = counts, ["at"] = at };
                for (int y = 0; y < YFields.Length; y++) axis[YFields[y]] = means[y];
                result[XFields[a]] = axis;
            }
            return result;
        }
    }
}
