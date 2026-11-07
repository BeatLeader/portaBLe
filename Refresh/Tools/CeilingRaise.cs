using System.Numerics;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using RatingAPI.Pp;

namespace portaBLe.Refresh
{
    // Ceiling raise (2026-11-06): maps whose amazing play falls clearly under maps of their stars are raised, and only
    // those. Part of the acc reweighter's step (it reads scores).
    // 1) Each map's amazing-play acc: the best top-1000 nomod acc on it (corrected to the median map's number of contenders)
    //    blended with a map-data model's prediction (weight n / (n + ObservedWeight), n its top-1000 plays; the model:
    //    the map's ratings, pp shares at the predicted acc, curve steepness and the Analyzer's statistics, fitted offline,
    //    held-out R^2 0.48: balance-analysis/goals/ceiling_fit.py). Its ceiling: its pp there.
    // 2) Its peers: the Standard maps within PeerStars stars (at least MinPeers); the target: their median ceiling x
    //    (1 - PeerMargin). Under it, the map's acc curve gets a power from its predicted acc - Below on up (no change under
    //    that, no cap so no bump: its slope only grows), reaching the target at the ceiling's acc, a 100% play at most
    //    x(1 + MaxAt100) on acc pp. Every other map is unchanged.
    //    Faded in by stars from RaiseStart to RaiseFull: maps under ~5 stars stay as they are (under ~700).
    // Emulated (median - 3%, 2026-11-06): 1148 maps raised (a third), the 10th / 25th percentile of ceilings at 5-7 stars
    // 579 / 604 -> 608 / 615 with the median and 90th percentile unchanged, the top end 802 (no new 800), the median
    // player and the top 10 unchanged. The under-peer maps are the same by empirical stars (97% overlap): not mis-starred.
    public static class CeilingRaise
    {
        public static double PeerStars { get; set; } = 0.5;
        public static int MinPeers { get; set; } = 30;
        public static double PeerMargin { get; set; } = 0.03;
        public static double ObservedWeight { get; set; } = 10;
        public static double Below { get; set; } = 0.02;
        public static double MaxAt100 { get; set; } = 0.2;
        // No raise at RaiseStart stars and under (their predicted acc is too unstable: they stay under ~700), full from RaiseFull
        public static double RaiseStart { get; set; } = 4.5;
        public static double RaiseFull { get; set; } = 5.5;
        private const int TopPlayers = 1000;

        private static readonly double[] Mu = { 7.6653185750949016, 5.0510600634671405, 4.458505807547602, 8.832078041710963, 98.01131576664143, 0.06046516611118649, 0.044710286573101346, 0.18811163703455291, 0.017210832700582133, 17.178125790939, 5.137503716351855, 5.75287710550367, 187.21613524348186, 0.19406125619977213, 0.07140459482654006, 0.5991631588828152, 1.3893678954694997, 0.0160321718649811, 0.03118169914185724 };
        private static readonly double[] Sd = { 2.519941281530234, 3.256200378470246, 2.1545474938839413, 2.9400518109738427, 0.7669661718466351, 0.04462411084024466, 0.016947143953829322, 0.07222632828325803, 0.13005621937626657, 3.3033061537001474, 0.4180361784426896, 2.785757704693405, 86.8188469965437, 0.1267262360077292, 0.07342846068134434, 0.09583573334330238, 0.30229798671678165, 0.1075997733396558, 0.11336240584937267 };
        private static readonly double[] W = { -0.5137897228676351, 0.052684204000251554, -0.06462206597130644, -0.054774728379464066, 0.0014524832494874499, 0.05728057060881678, 0.054505612975019546, -0.0012800251196585953, 0.02923990085242696, -0.03575356507100512, 0.06475943160326485, 0.056702522550922625, 0.050466506847828295, -0.04276143769001546, 0.004173765963461532, -0.057553624618102074, -0.0019764539784190758, -0.0032950346121709702, -0.0054283150954616225, -0.011495789540968407, -0.005490694012362809, -0.0809374793848229, 0.04113591268757488, -0.03463336703358544, -0.028330245428277917, 0.00782727214512209, -0.014771225765335736, -0.08163572139410459, -0.11338676812976692, -0.04106931868145358, 0.022290345111311685, 0.05072997696865234, -0.05378752191951994, 0.033228604565036034, -0.00042404373200473247, 0.07292987233162634, 0.016929489843453938, 0.035983954834218915, 0.13207701949301212, 0.012727174533097635, 0.024214157318687157, -0.010258186811739496, -0.08087815384693879, -0.09303122267250845, -0.04049681543464885, -0.06156111622156416, 0.04162408488594546, -0.050556870279210735 };
        private const double Contenders = 4.219507705176107, ContendersWeight = 0.16879206414970196;
        private static readonly int[] Main = { 0, 1, 2, 4, 6, 5, 7 };

        // Clears every map's top tilt (before the acc reweighter fits, so it fits without it)
        public static async Task Clear(AppContext dbContext)
        {
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "TopTilt", "REAL");
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "TopTiltAnchor", "REAL");
            await dbContext.Database.ExecuteSqlRawAsync("UPDATE Leaderboards SET TopTilt = NULL, TopTiltAnchor = NULL");
        }

        // The amazing play's acc - predicted acc, in points, from the map's data (lb's ratings; tilt off)
        public static double PredictedOffset(Leaderboard lb)
        {
            var curve = lb.Curve != null && lb.Curve.Count >= 2 ? lb.Curve : null;
            // The model was fitted on raw ratings: the acc reweighter's multiplier taken back out
            float rawAcc = (float)(lb.AccRating / ReplayUtils.AccReweightOf(lb));
            var (_, _, passPp, accPp, techPp) = ReplayUtils.PpFromScore(lb.PredictedAcc, "", null, rawAcc, lb.PassRating, lb.TechRating, curve, lb.PredictedAcc, ReplayUtils.IsOneSaber(lb.ModeName));
            double total = Math.Max(passPp + accPp + techPp, 1e-6), duration = Math.Max(lb.Duration, 1);
            double steep = Math.Log(PpCalculator.CurveValue(Math.Min(lb.PredictedAcc + 0.005f, 1f), curve) / Math.Max(PpCalculator.CurveValue(lb.PredictedAcc, curve), 1e-6f));
            double[] x =
            {
                lb.Stars, lb.PassRating, lb.TechRating, rawAcc, lb.PredictedAcc * 100, passPp / total, techPp / total, steep,
                ReplayUtils.IsOneSaber(lb.ModeName) ? 1 : 0, lb.Njs, Math.Log(duration), lb.NoteCount / duration, lb.PeakEbpm,
                lb.LinearPercent, lb.MultiPercentage, lb.HitDistance, lb.HardSwingRatio, lb.Inlines / duration, lb.BombAvoidances / duration,
            };
            var z = new double[x.Length];
            for (int i = 0; i < x.Length; i++) z[i] = (x[i] - Mu[i]) / Sd[i];
            double y = W[0] + ContendersWeight * Contenders;
            int k = 1;
            for (int i = 0; i < z.Length; i++) y += W[k++] * z[i];
            foreach (int i in Main) y += W[k++] * z[i] * z[i];
            for (int a = 0; a < Main.Length; a++)
                for (int b = a + 1; b < Main.Length; b++) y += W[k++] * z[Main[a]] * z[Main[b]];
            return y;
        }

        // Sets every map's top tilt (Standard maps under their peers); re-score after it
        public static async Task Run(AppContext dbContext)
        {
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "TopTilt", "REAL");
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "TopTiltAnchor", "REAL");
            var formula = PpFormula.Current;
            double cap = Math.Log(1 + formula.TopTiltCap), soft = formula.TopTiltSoftness;
            var lbs = await dbContext.Leaderboards.AsNoTracking().Where(l => l.Stars > 0).ToListAsync();
            foreach (var lb in lbs) { lb.TopTilt = null; lb.TopTiltAnchor = null; }
            var top = (await dbContext.Players.AsNoTracking().OrderByDescending(p => p.Pp).Select(p => p.Id).Take(TopPlayers).ToListAsync()).ToHashSet();
            var observed = (await dbContext.Scores.AsNoTracking().Where(s => s.Pp > 0 && (s.Modifiers == null || s.Modifiers == ""))
                .Select(s => new { s.PlayerId, s.LeaderboardId, s.Accuracy }).ToListAsync())
                .Where(s => s.PlayerId != null && top.Contains(s.PlayerId) && s.LeaderboardId != null)
                .GroupBy(s => s.LeaderboardId!).ToDictionary(g => g.Key, g => (Best: g.Max(s => s.Accuracy), Count: g.Count()));
            // Each Standard map's amazing-play acc and ceiling
            var maps = new List<(Leaderboard Lb, float Acc, double Ceiling, double AccPp)>();
            foreach (var lb in lbs.Where(l => l.ModeName == "Standard" && l.PredictedAcc > 0))
            {
                double offset = PredictedOffset(lb);
                if (observed.TryGetValue(lb.Id, out var o))
                {
                    double seen = (o.Best - lb.PredictedAcc) * 100 - ContendersWeight * (Math.Log(o.Count) - Contenders);
                    double w = o.Count / (o.Count + ObservedWeight);
                    offset = w * seen + (1 - w) * offset;
                }
                float acc = (float)Math.Min(lb.PredictedAcc + offset / 100, 0.9995);
                var (pp, _, _, accPp, _) = ReplayUtils.PpFromScore(acc, "", lb);
                if (pp > 0 && accPp > 0) maps.Add((lb, acc, pp, accPp));
            }
            maps.Sort((a, b) => a.Lb.Stars.CompareTo(b.Lb.Stars));
            var stars = maps.Select(m => (double)m.Lb.Stars).ToArray();
            var updates = lbs.Select(l => new Leaderboard { Id = l.Id }).ToDictionary(u => u.Id);
            var raises = new List<double>();
            for (int i = 0; i < maps.Count; i++)
            {
                if (cap <= 0) break;
                var (lb, acc, ceiling, accPp) = maps[i];
                // Its peers: within PeerStars stars, widened until there are MinPeers
                double width = PeerStars;
                List<double> peers;
                do
                {
                    int lo = LowerBound(stars, stars[i] - width), hi = LowerBound(stars, stars[i] + width + 1e-9);
                    peers = maps.GetRange(lo, hi - lo).Select(m => m.Ceiling).ToList();
                    width += 0.25;
                } while (peers.Count < MinPeers && width < 5);
                peers.Sort();
                double target = peers[peers.Count / 2] * (1 - PeerMargin);
                if (ceiling >= target) continue;
                double accMultiplier = 1 + (target / ceiling - 1) * ceiling / accPp;
                float anchor = Math.Max(Math.Min(lb.PredictedAcc - (float)Below, acc - 0.002f), 0.905f);
                var curve = lb.Curve != null && lb.Curve.Count >= 2 ? lb.Curve : null;
                double LogCurve(float a) => Math.Log(Math.Max(PpCalculator.CurveValue(a, curve), 1e-6f));
                double Onset(double u) => u <= 0 ? 0 : u < soft ? u * u / (2 * soft) : u - soft / 2;
                double atCeiling = Onset(LogCurve(acc) - LogCurve(anchor)), at100 = Onset(LogCurve(1f) - LogCurve(anchor));
                if (atCeiling < 1e-4) continue;
                // A power of the curve (e^(tau x onset)), a 100% play at most x(1 + MaxAt100); stored through TopTiltMultiplier's
                // tanh, whose bound (TopTiltCap) is loose enough to leave it a plain power
                double logM = Math.Log(accMultiplier);
                if (logM / atCeiling * at100 > Math.Log(1 + MaxAt100)) logM = Math.Log(1 + MaxAt100) * atCeiling / at100;
                logM = Math.Min(logM, 0.5 * cap);
                logM *= Math.Clamp((lb.Stars - RaiseStart) / (RaiseFull - RaiseStart), 0, 1);
                if (logM < 1e-5) continue;
                updates[lb.Id].TopTilt = (float)(cap * Math.Atanh(logM / cap) / atCeiling);
                updates[lb.Id].TopTiltAnchor = anchor;
                raises.Add(Math.Exp(logM));
            }
            await dbContext.BulkUpdateAsync(updates.Values.ToList(), options => options.ColumnInputExpression = c => new { c.TopTilt, c.TopTiltAnchor });
            raises.Sort();
            Console.WriteLine(FormattableString.Invariant($"Ceiling raise: {raises.Count} of {maps.Count} Standard maps under their peers' median - {PeerMargin * 100:0.#}% raised; acc pp at their ceiling x{(raises.Count > 0 ? raises[raises.Count / 2] : 1):0.000} (median), x{(raises.Count > 0 ? raises[^1] : 1):0.000} at most"));
        }

        private static int LowerBound(double[] sorted, double value)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi) { int mid = (lo + hi) / 2; if (sorted[mid] < value) lo = mid + 1; else hi = mid; }
            return lo;
        }
    }
}
