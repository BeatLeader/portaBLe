using Microsoft.EntityFrameworkCore;
using portaBLe.DB;

namespace portaBLe.Refresh
{
    /// <summary>
    /// Score-informed acc difficulty: corrects each map's predicted accuracy by what its scores say, as far as the scores are
    /// numerous enough to outweigh the acc model. Latent model on clean scores (no modifiers but IF/BE, players with at least
    /// MinPlayerScores of them):  log(1 - acc_ij) = log(1 - predictedAcc_j) + c_j - s_i,  fitted by alternating least squares
    /// with the prior c_j ~ N(0, tau^2) (tau = the acc model's typical error, log units). A map with n scores moves by
    /// n / (n + n0) of its score-implied correction, n0 = residual variance / tau^2 (about 12 scores at tau 0.09). Speed
    /// modifier predictions move by the same c_j. AccRating is rescaled by the curve's change (keeps LowNoteNerf), so this
    /// must run with the curve options the ratings were made with. Analysis/py/score_correct.py is the reference.
    /// </summary>
    public static class ScoreCorrection
    {
        public const int MinPlayerScores = 15;
        static readonly string[] CleanModifiers = { "", "IF", "BE", "IF,BE", "BE,IF" };

        public static async Task Apply(AppContext dbContext, float tau = 0.09f, int iterations = 40)
        {
            Console.WriteLine($"Score correction of predicted accuracy (tau {tau})");
            var lbs = await dbContext.Leaderboards.Include(lb => lb.ModifiersRating).ToListAsync();
            var mapIndex = new Dictionary<string, int>();
            foreach (var lb in lbs.Where(lb => lb.PredictedAcc > 0 && lb.PredictedAcc < 1)) mapIndex[lb.Id] = mapIndex.Count;
            var delta = new double[mapIndex.Count];
            foreach (var lb in lbs) if (mapIndex.TryGetValue(lb.Id, out var j)) delta[j] = Math.Log(1 - lb.PredictedAcc);

            var scores = await dbContext.Scores.AsNoTracking()
                .Where(s => s.Accuracy > 0 && s.Accuracy <= 1 && (s.Modifiers == null || CleanModifiers.Contains(s.Modifiers)))
                .Select(s => new { s.PlayerId, s.LeaderboardId, s.Accuracy })
                .ToListAsync();
            var perPlayer = scores.GroupBy(s => s.PlayerId).Where(g => g.Count() >= MinPlayerScores).ToList();
            int n = perPlayer.Sum(g => g.Count(s => mapIndex.ContainsKey(s.LeaderboardId)));
            var mi = new int[n]; var pi = new int[n]; var z = new double[n];
            int k = 0;
            for (int p = 0; p < perPlayer.Count; p++)
            {
                foreach (var s in perPlayer[p])
                {
                    if (!mapIndex.TryGetValue(s.LeaderboardId, out var j)) continue;
                    mi[k] = j; pi[k] = p; z[k] = Math.Log(Math.Clamp(1.0 - s.Accuracy, 5e-4, 1.0)); k++;
                }
            }
            scores = null; perPlayer = null;

            int maps = delta.Length, players = pi.Length > 0 ? pi.Max() + 1 : 0;
            var nm = new double[maps]; var np = new double[players];
            for (int i = 0; i < n; i++) { nm[mi[i]]++; np[pi[i]]++; }
            var c = new double[maps]; var si = new double[players]; var r = new double[n];
            double n0 = 0, varE = 0;
            for (int it = 0; it < iterations; it++)
            {
                Array.Clear(si);
                for (int i = 0; i < n; i++) si[pi[i]] += delta[mi[i]] + c[mi[i]] - z[i];
                for (int p = 0; p < players; p++) si[p] /= np[p];
                for (int i = 0; i < n; i++) r[i] = z[i] - delta[mi[i]] + si[pi[i]];   // what the scores say beyond the rating
                var sumR = new double[maps];
                for (int i = 0; i < n; i++) sumR[mi[i]] += r[i];
                if (it % 10 == 0)
                {
                    // residual variance around each map's mean -> how many scores it takes to outweigh the prior
                    double sum = 0, sum2 = 0;
                    for (int i = 0; i < n; i++) { double e = r[i] - sumR[mi[i]] / nm[mi[i]]; sum += e; sum2 += e * e; }
                    varE = sum2 / n - (sum / n) * (sum / n);
                    n0 = varE / (tau * tau);
                }
                for (int j = 0; j < maps; j++) c[j] = sumR[j] / (nm[j] + n0);
            }
            Console.WriteLine($"{n:N0} clean scores, {players:N0} players, {maps:N0} maps; per-score residual SD {Math.Sqrt(varE):F3} -> n0 = {n0:F1} scores");

            var changes = new List<(Leaderboard lb, double c, double n)>();
            foreach (var lb in lbs)
            {
                if (!mapIndex.TryGetValue(lb.Id, out var j) || c[j] == 0) continue;
                changes.Add((lb, c[j], nm[j]));
                (lb.PredictedAcc, lb.AccRating) = Corrected(lb.PredictedAcc, lb.AccRating, lb.PassRating, lb.TechRating, c[j], lb.ModeName);
                lb.Stars = ReplayUtils.ToStars(lb.AccRating, lb.PassRating, lb.TechRating, lb.PredictedAcc, lb.ModeName);
                var m = lb.ModifiersRating;
                if (m == null) continue;
                (m.SSPredictedAcc, m.SSAccRating) = Corrected(m.SSPredictedAcc, m.SSAccRating, m.SSPassRating, m.SSTechRating, c[j], lb.ModeName);
                (m.FSPredictedAcc, m.FSAccRating) = Corrected(m.FSPredictedAcc, m.FSAccRating, m.FSPassRating, m.FSTechRating, c[j], lb.ModeName);
                (m.SFPredictedAcc, m.SFAccRating) = Corrected(m.SFPredictedAcc, m.SFAccRating, m.SFPassRating, m.SFTechRating, c[j], lb.ModeName);
                m.SSStars = ReplayUtils.ToStars(m.SSAccRating, m.SSPassRating, m.SSTechRating, m.SSPredictedAcc, lb.ModeName);
                m.FSStars = ReplayUtils.ToStars(m.FSAccRating, m.FSPassRating, m.FSTechRating, m.FSPredictedAcc, lb.ModeName);
                m.SFStars = ReplayUtils.ToStars(m.SFAccRating, m.SFPassRating, m.SFTechRating, m.SFPredictedAcc, lb.ModeName);
            }
            var sorted = changes.Select(x => x.c).OrderBy(x => x).ToList();
            double Q(double q) => sorted.Count == 0 ? 0 : sorted[(int)Math.Round(q * (sorted.Count - 1))];
            Console.WriteLine($"Corrected {changes.Count} maps (log error rate, + = harder): p5 {Q(0.05):+0.000;-0.000}, p50 {Q(0.5):+0.000;-0.000}, p95 {Q(0.95):+0.000;-0.000}");
            foreach (var (lb, cj, nj) in changes.OrderByDescending(x => Math.Abs(x.c)).Take(10))
            {
                Console.WriteLine($"  {cj:+0.000;-0.000} ({nj:N0} scores) {lb.Name} {lb.DifficultyName}");
            }

            dbContext.BulkSaveChanges();
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }

        static (float, float) Corrected(float predictedAcc, float accRating, float passRating, float techRating, double c, string mode)
        {
            if (predictedAcc <= 0 || predictedAcc >= 1) return (predictedAcc, accRating);
            float corrected = (float)Math.Clamp(1 - Math.Exp(Math.Log(1 - predictedAcc) + c), 0.5, 0.9995);
            float before = ReplayUtils.AccRating(predictedAcc, passRating, techRating, mode);
            float after = ReplayUtils.AccRating(corrected, passRating, techRating, mode);
            return (corrected, before > 0 ? accRating * after / before : accRating);
        }
    }
}
