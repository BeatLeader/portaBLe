using Dasync.Collections;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using System.Collections.Concurrent;

namespace portaBLe.Refresh
{
    public class MegametricData
    {
        public float Weight { get; set; }
        public int RankedPlayCount { get; set; }
        public float Pp { get; set; }
        public float TopPp { get; set; }
    }

    public class LeaderboardsRefresh
    {
        public static async Task Outliers(AppContext dbContext)
        {
            Console.WriteLine("Recalculating Outliers");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "OutlierCount", "INTEGER");

            const int minScoresCount = 100;
            const float outlierThreshold = 8f;
            const int maxTopScoresToCheck = 10;

            var allScores = await dbContext.Scores
                .Where(s => s.Pp > 0)
                .Select(s => new
                {
                    s.PlayerId,
                    s.LeaderboardId,
                    s.Pp
                })
                .ToListAsync();

            Console.WriteLine($"Loaded {allScores.Count} scores with PP > 0");

            // Per player with enough scores: top scores above a drop of over outlierThreshold% to the next are outliers
            var playerScoreGroups = allScores
                .GroupBy(s => s.PlayerId)
                .Where(g => g.Count() >= minScoresCount)
                .Select(g =>
                {
                    var orderedScores = g.OrderByDescending(s => s.Pp).Take(maxTopScoresToCheck).ToList();
                    var outlierScores = new HashSet<float>();

                    for (int i = 0; i < orderedScores.Count - 1; i++)
                    {
                        float currentPp = orderedScores[i].Pp;
                        float nextPp = orderedScores[i + 1].Pp;

                        if (nextPp > 0)
                        {
                            float ppDifference = currentPp - nextPp;
                            float percentageDifference = (ppDifference / nextPp) * 100f;

                            if (percentageDifference > outlierThreshold)
                            {
                                for (int j = 0; j <= i; j++)
                                {
                                    outlierScores.Add(orderedScores[j].Pp);
                                }
                                break;
                            }
                        }
                    }

                    return new
                    {
                        PlayerId = g.Key,
                        OutlierScores = outlierScores,
                        AllScores = g.ToList()
                    };
                })
                .ToList();

            Console.WriteLine($"Found {playerScoreGroups.Count} eligible players with {minScoresCount}+ scores");

            var playerOutlierScores = playerScoreGroups.ToDictionary(
                p => p.PlayerId,
                p => p.OutlierScores
            );

            // Outlier count per leaderboard
            var leaderboardOutliers = allScores
                .Where(s => playerOutlierScores.ContainsKey(s.PlayerId))
                .GroupBy(s => s.LeaderboardId)
                .Select(g => new
                {
                    LeaderboardId = g.Key,
                    OutlierCount = g.Count(score =>
                    {
                        if (!playerOutlierScores.TryGetValue(score.PlayerId, out var outlierScores))
                            return false;

                        return outlierScores.Contains(score.Pp);
                    })
                })
                .ToDictionary(x => x.LeaderboardId, x => x.OutlierCount);

            Console.WriteLine($"Calculated outliers for {leaderboardOutliers.Count} leaderboards");

            var leaderboards = await dbContext.Leaderboards
                .Select(l => new Leaderboard { Id = l.Id })
                .ToListAsync();

            foreach (var leaderboard in leaderboards)
            {
                leaderboard.OutlierCount = leaderboardOutliers.TryGetValue(leaderboard.Id, out var count) ? count : 0;
            }

            await dbContext.BulkUpdateAsync(leaderboards, options => options.ColumnInputExpression = c =>
                new { c.OutlierCount });

            Console.WriteLine("Outlier calculation completed");
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }

        public static async Task Refresh(AppContext dbContext)
        {
            // Before any bulk operation: the bulk extension reads a table's columns once, so columns added later are missing
            await EnsureTagColumns(dbContext);
            Console.WriteLine("Recalculating Megametric");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

            // Weight of a profile's 40th score (Top250: scores of top 250 players within their top 40)
            float weightThreshold = MathF.Pow(0.965f, 40);

            var leaderboardIds = await dbContext.Leaderboards
                .Select(lb => lb.Id)
                .ToListAsync();

            Console.WriteLine($"Processing {leaderboardIds.Count} leaderboards");

            var allScores = await dbContext.Scores
                .Select(s => new
                {
                    s.LeaderboardId,
                    s.Weight,
                    s.Pp,
                    s.Player.TopPp,
                    s.Player.RankedPlayCount,
                    s.Player.Rank
                })
                .ToListAsync();

            Console.WriteLine($"Loaded {allScores.Count} scores");

            var leaderboardGroups = allScores
                .GroupBy(s => s.LeaderboardId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var updates = new ConcurrentBag<Leaderboard>();

            await Parallel.ForEachAsync(leaderboardIds,
                new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount },
                async (leaderboardId, ct) =>
                {
                    if (!leaderboardGroups.TryGetValue(leaderboardId, out var scores))
                    {
                        updates.Add(new Leaderboard
                        {
                            Id = leaderboardId,
                            Count = 0,
                            Count80 = 0,
                            Count95 = 0,
                            Average = 0,
                            Percentile = 0,
                            Megametric = 0,
                            Megametric125 = 0,
                            Megametric75 = 0,
                            Megametric40 = 0,
                            Top250 = 0,
                            TotalPP = 0,
                            PPRatioFiltered = 0,
                            PPRatioUnfiltered = 0
                        });
                        await Task.CompletedTask;
                        return;
                    }

                    var megametricData = scores
                        .Where(s => s.TopPp != 0)
                        .Select(s => new MegametricData { Weight = s.Weight, RankedPlayCount = s.RankedPlayCount, Pp = s.Pp, TopPp = s.TopPp })
                        .ToList();

                    int count = scores.Count;
                    float average = scores.Average(s => s.Weight);
                    int count80 = scores.Count(s => s.Weight > 0.8f);
                    int count95 = scores.Count(s => s.Weight > 0.95f);
                    float ppSum = scores.Sum(s => s.Pp * s.Weight);
                    int top250 = scores.Count(s => s.Rank < 250 && s.Weight > weightThreshold);

                    var filteredScores = scores.Where(s => s.RankedPlayCount >= 50 && s.TopPp != 0).ToList();
                    float ppRatioFiltered = filteredScores.Count > 0
                        ? filteredScores.Average(s => s.Pp / s.TopPp)
                        : 0;

                    var unfilteredScores = scores.Where(s => s.TopPp != 0).ToList();
                    float ppRatioUnfiltered = unfilteredScores.Count > 0
                        ? unfilteredScores.Average(s => s.Pp / s.TopPp)
                        : 0;

                    // Percentile: average weight of the top third of scores by weight
                    int topCount = (int)(megametricData.Count * 0.33);
                    var topByWeight = megametricData.OrderByDescending(s => s.Weight).Take(topCount).ToList();
                    float percentile = topByWeight.Count > 10 ? topByWeight.Average(s => s.Weight) : 0;

                    float megametric = CalculateMegametric(megametricData, topCount, 75);
                    float megametric125 = CalculateMegametric(megametricData, topCount, 125);
                    float megametric75 = CalculateMegametric(megametricData, topCount, 75);
                    float megametric40 = CalculateMegametric(megametricData, topCount, 40);

                    updates.Add(new Leaderboard
                    {
                        Id = leaderboardId,
                        Count = count,
                        Count80 = count80,
                        Count95 = count95,
                        Average = average,
                        Percentile = percentile,
                        Megametric = megametric,
                        Megametric125 = megametric125,
                        Megametric75 = megametric75,
                        Megametric40 = megametric40,
                        Top250 = top250,
                        TotalPP = ppSum,
                        PPRatioFiltered = ppRatioFiltered,
                        PPRatioUnfiltered = ppRatioUnfiltered
                    });

                    await Task.CompletedTask;
                });

            var updateList = updates.ToList();

            // MegametricWorth: Megametric75 relative to what maps of its stars average (WorthReference)
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "Worth", "REAL");
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "MegametricWorth", "REAL");
            var stars = await dbContext.Leaderboards.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Stars);
            float StarsOf(Leaderboard u) => stars.TryGetValue(u.Id, out var s) ? s : 0;
            int StarRange(Leaderboard u) => Math.Clamp((int)MathF.Floor(StarsOf(u)), 0, 13);
            var usedRanges = updateList.Where(u => u.Megametric75 > 0).GroupBy(StarRange)
                .Where(g => g.Count() >= 5 && g.Average(u => (double)u.Megametric75) > 0)
                .Select(g => g.Key)
                .ToHashSet();
            var points = updateList.Where(u => u.Megametric75 > 0 && usedRanges.Contains(StarRange(u)))
                .Select(u => ((double)StarsOf(u), (double)u.Megametric75))
                .ToList();
            foreach (var u in updateList)
            {
                double reference = u.Megametric75 > 0 && usedRanges.Contains(StarRange(u)) ? BalanceTuner.WorthReference(points, StarsOf(u)) : 0;
                u.MegametricWorth = reference > 0 ? (float)(u.Megametric75 / reference - 1) : null;
            }

            // Worth: the balance tuner's worth measure (BalanceTuner.WorthMeasure), from the scores as they are. Nomod here,
            // SS / FS / SF in ModifiersRating (below)
            var worth = await BalanceTuner.StoredWorth(dbContext);
            foreach (var u in updateList) u.Worth = worth.TryGetValue(u.Id, out var w) ? w[0] : null;

            Console.WriteLine($"Processed {updates.Count} leaderboards, writing to database...");
            await dbContext.BulkUpdateAsync(updateList, options => options.ColumnInputExpression = c =>
                new
                {
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
                    c.Megametric125,
                    c.Worth,
                    c.MegametricWorth
                });

            await StoreModifierWorth(dbContext, worth);

            dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
            Console.WriteLine($"Complete! Total time: {Program.Stopwatch.ElapsedMilliseconds / 1000} seconds");

            await RefreshWorthByRank(dbContext);
            await RefreshWorthQuantiles(dbContext);
            await RefreshTopPlayShares(dbContext);
            await RefreshBalanceMetrics(dbContext);
            await RefreshEmpiricalStars(dbContext);
            await DetermineTags(dbContext);
        }

        // Balance metrics (Leaderboard.Top20Share ... AccVsPredicted): players with at least BalanceMinPlays ranked plays,
        // maps with at least BalanceMinPlayers of them, compared with the maps within BalanceStarWindow stars
        public const int BalanceMinPlays = 50;
        public const int BalanceMinPlayers = 30;
        public const double BalanceStarWindow = 0.5;
        private const int BalanceMinNeighbours = 8;
        private const int BalanceMinOwnMaps = 5;   // a player's other nomod maps within the window, for AccVsPredicted
        // Emulated top play: the star bands' typical best acc above the predicted acc, from maps with this many top 100 plays
        public const int EmulatedTopMinTopPlays = 10;
        private const int EmulatedTopMinMaps = 5;   // per 1-star band
        // Best specialist top play: the 3 best players (top 3000) of each kind of map, each with 5+ plays on it
        private const int SpecialistPanel = 3, SpecialistMinPlays = 5, SpecialistBestPlays = 10, SpecialistMaxRank = 3000;
        private const int KindMinMaps = 15;   // smaller kinds are merged within their star band

        // Recomputes Leaderboard.Top20Share / Top50Share / Top5Relative / PlayersRankRelative / AccVsPredicted and TopPlayPp /
        // EmulatedTopAcc / EmulatedTopPp alone (Refresh runs it too)
        public static async Task RefreshBalanceMetrics(AppContext dbContext)
        {
            Console.WriteLine("Recalculating balance metrics");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            foreach (var column in new[] { "Top20Share", "Top50Share", "Top5Relative", "PlayersRankRelative", "AccVsPredicted", "TopPlayPp", "EmulatedTopAcc", "EmulatedTopPp", "SpecialistTopAcc", "SpecialistTopPp" })
                await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", column, "REAL");

            var maps = await dbContext.Leaderboards.AsNoTracking().Where(l => l.Stars > 0)
                .Select(l => new { l.Id, l.Stars, l.PredictedAcc, l.AccRating, l.PassRating, l.TechRating, l.Curve, l.ModeName, l.TopTilt, l.TopTiltAnchor, l.NoteCount, l.NoteSpan }).ToListAsync();
            var mapIndex = new Dictionary<string, int>(maps.Count);
            for (int i = 0; i < maps.Count; i++) mapIndex[maps[i].Id] = i;
            var rankOf = (await dbContext.Players.AsNoTracking().OrderByDescending(p => p.Pp).Select(p => p.Id).ToListAsync())
                .Select((id, rank) => (id, rank)).ToDictionary(p => p.id, p => p.rank);
            var scores = await dbContext.Scores.AsNoTracking().Where(s => s.Pp > 0)
                .Select(s => new { s.PlayerId, s.LeaderboardId, s.Pp, s.Accuracy, s.Modifiers, s.PassPP, s.TechPP }).ToListAsync();

            // Kinds of map (MapKinds): from each map's median pass and tech pp shares over its nomod scores
            static bool IsNomod(string? modifiers) => string.IsNullOrEmpty(modifiers) || !modifiers.ToUpperInvariant().Split(',')
                .Any(m => m is "SS" or "FS" or "SF" or "NF");
            var shares = new List<(double Pass, double Tech)>?[maps.Count];
            foreach (var s in scores)
            {
                if (s.LeaderboardId == null || !mapIndex.TryGetValue(s.LeaderboardId, out int mi) || !IsNomod(s.Modifiers)) continue;
                (shares[mi] ??= new()).Add((s.PassPP / s.Pp, s.TechPP / s.Pp));
            }
            var kindOf = MapKinds(maps.Select(m => (double)m.Stars).ToArray(),
                shares.Select(l => l == null || l.Count < 10 ? ((double, double)?)null : (Median(l.Select(x => x.Pass).ToList()), Median(l.Select(x => x.Tech).ToList()))).ToArray());
            // Each top SpecialistMaxRank player's nomod plays per kind: (pp, acc - predicted acc)
            var kindPlays = new Dictionary<int, List<List<(float Pp, double Gap)>>>();

            // Each player's plays (any modifier) for their top 20 / 50 thresholds; their nomod plays for the metrics
            var players = scores.Where(s => s.PlayerId != null && rankOf.ContainsKey(s.PlayerId)).GroupBy(s => s.PlayerId!);
            var n = new int[maps.Count]; var in20 = new int[maps.Count]; var in50 = new int[maps.Count];
            var ranks = Enumerable.Range(0, maps.Count).Select(_ => new List<int>()).ToArray();
            var accGaps = Enumerable.Range(0, maps.Count).Select(_ => new List<double>()).ToArray();
            var nomodPp = Enumerable.Range(0, maps.Count).Select(_ => new List<float>()).ToArray();
            var bestAcc = new float[maps.Count]; var topPlays = new int[maps.Count];
            static bool Nomod(string? modifiers) => string.IsNullOrEmpty(modifiers) || !modifiers.ToUpperInvariant().Split(',')
                .Any(m => m is "SS" or "FS" or "SF" or "NF");
            foreach (var player in players)
            {
                var nomod = player.Where(s => s.LeaderboardId != null && mapIndex.ContainsKey(s.LeaderboardId) && Nomod(s.Modifiers))
                    .Select(s => (Map: mapIndex[s.LeaderboardId!], s.Pp, s.Accuracy)).ToList();
                if (rankOf[player.Key] < SpecialistMaxRank)
                {
                    foreach (var g in nomod.Where(s => kindOf[s.Map] >= 0 && maps[s.Map].PredictedAcc > 0).GroupBy(s => kindOf[s.Map]))
                    {
                        if (!kindPlays.TryGetValue(g.Key, out var list)) kindPlays[g.Key] = list = new();
                        list.Add(g.Select(s => (s.Pp, (double)(s.Accuracy - maps[s.Map].PredictedAcc))).ToList());
                    }
                }
                foreach (var s in nomod)
                {
                    nomodPp[s.Map].Add(s.Pp);
                    if (s.Accuracy > bestAcc[s.Map]) bestAcc[s.Map] = s.Accuracy;
                    if (rankOf[player.Key] < 100) topPlays[s.Map]++;
                }
                int count = player.Count();
                if (count < BalanceMinPlays) continue;
                var top = player.Select(s => s.Pp).OrderByDescending(p => p).ToList();
                float t20 = top[19], t50 = top[49];
                int rank = rankOf[player.Key];
                // acc - predicted, by stars, for the player's own reference
                var own = nomod.Where(s => maps[s.Map].PredictedAcc > 0)
                    .Select(s => (Stars: maps[s.Map].Stars, Gap: (double)(s.Accuracy - maps[s.Map].PredictedAcc), s.Map))
                    .OrderBy(s => s.Stars).ToList();
                int lo = 0, hi = 0;
                var others = new List<double>();
                for (int k = 0; k < own.Count; k++)
                {
                    while (own[lo].Stars < own[k].Stars - BalanceStarWindow) lo++;
                    while (hi < own.Count && own[hi].Stars <= own[k].Stars + BalanceStarWindow) hi++;
                    if (hi - lo - 1 < BalanceMinOwnMaps) continue;
                    others.Clear();
                    for (int j = lo; j < hi; j++) if (j != k) others.Add(own[j].Gap);
                    accGaps[own[k].Map].Add(own[k].Gap - Median(others));
                }
                foreach (var s in nomod)
                {
                    n[s.Map]++;
                    if (s.Pp >= t20) in20[s.Map]++;
                    if (s.Pp >= t50) in50[s.Map]++;
                    ranks[s.Map].Add(rank);
                }
            }

            // Per map, then against the maps within the star window
            var top5 = nomodPp.Select(l => l.Count == 0 ? double.NaN : l.OrderByDescending(p => p).Take(5).Average(p => (double)p)).ToArray();
            var medianRank = ranks.Select(l => l.Count == 0 ? double.NaN : Median(l.Select(r => (double)r).ToList())).ToArray();
            var valid = Enumerable.Range(0, maps.Count).Where(i => n[i] >= BalanceMinPlayers).ToList();

            // How far above the predicted acc the world's best land, per 1-star band (median over the maps the top 100 played
            // most), interpolated between the bands' middles and flat past the first and last
            var bands = Enumerable.Range(0, maps.Count).Where(i => topPlays[i] >= EmulatedTopMinTopPlays && maps[i].PredictedAcc > 0)
                .GroupBy(i => (int)Math.Floor(maps[i].Stars)).Where(g => g.Count() >= EmulatedTopMinMaps)
                .Select(g => (Stars: g.Key + 0.5, Offset: Median(g.Select(i => (double)(bestAcc[i] - maps[i].PredictedAcc)).ToList())))
                .OrderBy(b => b.Stars).ToList();
            double TopOffset(double stars)
            {
                if (bands.Count == 0) return 0;
                if (stars <= bands[0].Stars) return bands[0].Offset;
                if (stars >= bands[^1].Stars) return bands[^1].Offset;
                int k = bands.FindIndex(b => b.Stars > stars);
                var (a, b) = (bands[k - 1], bands[k]);
                return a.Offset + (b.Offset - a.Offset) * (stars - a.Stars) / (b.Stars - a.Stars);
            }
            // Each kind's best specialist offset: of its SpecialistPanel best players (by their 5 best plays there, with
            // SpecialistMinPlays+ plays), the highest median acc - predicted acc over their SpecialistBestPlays best plays
            var kindOffset = new Dictionary<int, double>();
            foreach (var (kind, kindPlayers) in kindPlays)
            {
                var panel = kindPlayers.Where(p => p.Count >= SpecialistMinPlays)
                    .OrderByDescending(p => p.OrderByDescending(x => x.Pp).Take(5).Average(x => x.Pp)).Take(SpecialistPanel).ToList();
                if (panel.Count > 0) kindOffset[kind] = panel.Max(p => Median(p.OrderByDescending(x => x.Pp).Take(SpecialistBestPlays).Select(x => x.Gap).ToList()));
            }
            if (kindOffset.Count > 0) Console.WriteLine($"Best specialist offsets: {kindOffset.Count} kinds of map, " + FormattableString.Invariant($"median {Median(kindOffset.Values.ToList()) * 100:+0.00;-0.00} points, {kindOffset.Values.Min() * 100:+0.00;-0.00} to {kindOffset.Values.Max() * 100:+0.00;-0.00}"));
            Console.WriteLine("Emulated top play, best acc - predicted by stars: " + string.Join(", ", bands.Select(b => FormattableString.Invariant($"{b.Stars:0.#}* {b.Offset * 100:+0.00;-0.00}"))));

            var updates = maps.Select((m, i) =>
            {
                float? topPlay = nomodPp[i].Count > 0 ? nomodPp[i].Max() : null;
                float? emulatedAcc = null, emulatedPp = null, specialistAcc = null, specialistPp = null;
                float PpAt(float acc) => ReplayUtils.PpFromScore(acc, "", null, m.AccRating, m.PassRating, m.TechRating,
                    m.Curve != null && m.Curve.Count >= 2 ? m.Curve : null, m.PredictedAcc, ReplayUtils.IsOneSaber(m.ModeName), m.TopTilt ?? 0, m.TopTiltAnchor ?? 0, ReplayUtils.ShortTop(m.NoteCount, m.NoteSpan)).Item1;
                if (m.PredictedAcc > 0)
                {
                    emulatedAcc = (float)Math.Min(m.PredictedAcc + TopOffset(m.Stars), 1.0);
                    emulatedPp = PpAt(emulatedAcc.Value);
                    // never under the typical one (some kinds' specialists stay under the predicted acc, e.g. 13+ stars)
                    specialistAcc = kindOf[i] >= 0 && kindOffset.TryGetValue(kindOf[i], out double offset) ? (float)Math.Min(m.PredictedAcc + Math.Max(offset, TopOffset(m.Stars)), 1.0) : emulatedAcc;
                    specialistPp = PpAt(specialistAcc.Value);
                }
                if (n[i] < BalanceMinPlayers) return new Leaderboard { Id = m.Id, TopPlayPp = topPlay, EmulatedTopAcc = emulatedAcc, EmulatedTopPp = emulatedPp, SpecialistTopAcc = specialistAcc, SpecialistTopPp = specialistPp };
                var near = valid.Where(j => Math.Abs(maps[j].Stars - m.Stars) <= BalanceStarWindow).ToList();
                var nearCount = near.Where(j => nomodPp[j].Count * 2 >= nomodPp[i].Count && nomodPp[j].Count <= nomodPp[i].Count * 2).ToList();
                return new Leaderboard
                {
                    Id = m.Id,
                    Top20Share = (float)in20[i] / n[i],
                    Top50Share = (float)in50[i] / n[i],
                    Top5Relative = nearCount.Count >= BalanceMinNeighbours ? (float)(top5[i] / Median(nearCount.Select(j => top5[j]).ToList()) - 1) : null,
                    PlayersRankRelative = near.Count >= BalanceMinNeighbours ? (float)((medianRank[i] + 1) / (Median(near.Select(j => medianRank[j]).ToList()) + 1)) : null,
                    AccVsPredicted = accGaps[i].Count >= BalanceMinPlayers ? (float)Median(accGaps[i]) : null,
                    TopPlayPp = topPlay, EmulatedTopAcc = emulatedAcc, EmulatedTopPp = emulatedPp, SpecialistTopAcc = specialistAcc, SpecialistTopPp = specialistPp,
                };
            }).ToList();
            await dbContext.BulkUpdateAsync(updates, options => options.ColumnInputExpression = c => new { c.Top20Share, c.Top50Share, c.Top5Relative, c.PlayersRankRelative, c.AccVsPredicted, c.TopPlayPp, c.EmulatedTopAcc, c.EmulatedTopPp, c.SpecialistTopAcc, c.SpecialistTopPp });

            dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
            Console.WriteLine($"Balance metrics: {valid.Count} maps ({Program.Stopwatch.ElapsedMilliseconds / 1000} seconds)");
        }

        // Kinds of map for the best specialist offset: 1-star band (13+ together) x pass share tercile x tech share tercile
        // (over the maps with shares); kinds with fewer than KindMinMaps maps merged within their band. -1: no shares.
        public static int[] MapKinds(double[] stars, (double Pass, double Tech)?[] shares)
        {
            var known = shares.Where(x => x != null).Select(x => x!.Value).ToList();
            if (known.Count == 0) return stars.Select(_ => -1).ToArray();
            static double Tercile(IEnumerable<double> v, double q) { var l = v.OrderBy(x => x).ToList(); return l[(int)(q * (l.Count - 1))]; }
            double p1 = Tercile(known.Select(x => x.Pass), 1 / 3.0), p2 = Tercile(known.Select(x => x.Pass), 2 / 3.0);
            double t1 = Tercile(known.Select(x => x.Tech), 1 / 3.0), t2 = Tercile(known.Select(x => x.Tech), 2 / 3.0);
            var kinds = stars.Select((s, i) => shares[i] is (double, double) sh
                ? Math.Min((int)s, 13) * 9 + (sh.Pass < p1 ? 0 : sh.Pass < p2 ? 1 : 2) * 3 + (sh.Tech < t1 ? 0 : sh.Tech < t2 ? 1 : 2)
                : -1).ToArray();
            var counts = kinds.Where(k => k >= 0).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
            for (int i = 0; i < kinds.Length; i++)
                if (kinds[i] >= 0 && counts[kinds[i]] < KindMinMaps) kinds[i] = 1000 + kinds[i] / 9;   // its band's merged kind
            return kinds;
        }

        // Empirical difficulty: players with EmpiricalMinPlays+ nomod plays among the top EmpiricalPlayers, maps with
        // EmpiricalMinMapPlays+ of their plays; EmpiricalWindow maps each side for the empirical stars
        private const int EmpiricalPlayers = 20000, EmpiricalMinPlays = 50, EmpiricalMinMapPlays = 30, EmpiricalIterations = 25, EmpiricalWindow = 40;
        private const double EmpiricalQuantile = 0.75;

        // Recomputes Leaderboard.EmpiricalDifficulty / EmpiricalStars alone (Refresh runs it too). Split-half reliability
        // 0.998, correlation with stars 0.988 (2026-11-06).
        public static async Task RefreshEmpiricalStars(AppContext dbContext)
        {
            Console.WriteLine("Recalculating empirical stars");
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "EmpiricalDifficulty", "REAL");
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "EmpiricalStars", "REAL");
            var maps = await dbContext.Leaderboards.AsNoTracking().Where(l => l.Stars > 0).Select(l => new { l.Id, l.Stars }).ToListAsync();
            var mapIndex = new Dictionary<string, int>(maps.Count);
            for (int i = 0; i < maps.Count; i++) mapIndex[maps[i].Id] = i;
            var rankOf = (await dbContext.Players.AsNoTracking().OrderByDescending(p => p.Pp).Select(p => p.Id).Take(EmpiricalPlayers).ToListAsync())
                .Select((id, r) => (id, r)).ToDictionary(p => p.id, p => p.r);
            var raw = (await dbContext.Scores.AsNoTracking().Where(s => s.Pp > 0 && s.Accuracy > 0.5f && s.Accuracy < 1f)
                .Select(s => new { s.PlayerId, s.LeaderboardId, s.Accuracy, s.Modifiers }).ToListAsync())
                .Where(s => string.IsNullOrEmpty(s.Modifiers) && s.PlayerId != null && rankOf.ContainsKey(s.PlayerId) && s.LeaderboardId != null && mapIndex.ContainsKey(s.LeaderboardId))
                .Select(s => (Player: rankOf[s.PlayerId!], Map: mapIndex[s.LeaderboardId!], Y: Math.Log(s.Accuracy / (1.0 - s.Accuracy)))).ToList();
            var playerCount = new int[rankOf.Count]; var mapCount = new int[maps.Count];
            foreach (var s in raw) { playerCount[s.Player]++; mapCount[s.Map]++; }
            var plays = raw.Where(s => playerCount[s.Player] >= EmpiricalMinPlays && mapCount[s.Map] >= EmpiricalMinMapPlays).ToArray();
            var byPlayer = plays.Select((s, i) => (s.Player, i)).GroupBy(x => x.Player).ToDictionary(g => g.Key, g => g.Select(x => x.i).ToArray());
            var byMap = plays.Select((s, i) => (s.Map, i)).GroupBy(x => x.Map).ToDictionary(g => g.Key, g => g.Select(x => x.i).ToArray());
            var skill = new double[rankOf.Count]; var diff = new double[maps.Count];
            static double Quantile(double[] v, double q) { Array.Sort(v); return v[(int)((v.Length - 1) * q)]; }
            for (int it = 0; it < EmpiricalIterations; it++)
            {
                Parallel.ForEach(byPlayer, g => skill[g.Key] = Quantile(g.Value.Select(i => plays[i].Y + diff[plays[i].Map]).ToArray(), EmpiricalQuantile));
                Parallel.ForEach(byMap, g => diff[g.Key] = Quantile(g.Value.Select(i => skill[plays[i].Player] - plays[i].Y).ToArray(), 1 - EmpiricalQuantile));
                double center = Median(byMap.Keys.Select(k => diff[k]).ToList());
                foreach (var k in byMap.Keys) diff[k] -= center;
            }
            // Empirical stars: the median stars of the maps of most similar difficulty
            var ordered = byMap.Keys.OrderBy(k => diff[k]).ToArray();
            var updates = maps.Select(m => new Leaderboard { Id = m.Id }).ToList();
            for (int r = 0; r < ordered.Length; r++)
            {
                int lo = Math.Max(0, r - EmpiricalWindow), hi = Math.Min(ordered.Length, r + EmpiricalWindow + 1);
                int k = ordered[r];
                updates[k].EmpiricalDifficulty = (float)diff[k];
                updates[k].EmpiricalStars = (float)Median(ordered[lo..hi].Select(j => (double)maps[j].Stars).ToList());
            }
            await dbContext.BulkUpdateAsync(updates, options => options.ColumnInputExpression = c => new { c.EmpiricalDifficulty, c.EmpiricalStars });
            Console.WriteLine($"Empirical stars: {ordered.Length} maps from {plays.Length} plays");
        }

        private static double Median(List<double> values)
        {
            values.Sort();
            int c = values.Count;
            return c == 0 ? double.NaN : c % 2 == 1 ? values[c / 2] : (values[c / 2 - 1] + values[c / 2]) / 2;
        }

        // Recomputes Leaderboard.Worth and ModifiersRating.SSWorth / FSWorth / SFWorth from the stored scores (Refresh does
        // it too): for a change to the worth measure alone, without re-scoring
        public static async Task RefreshWorth(AppContext dbContext)
        {
            Console.WriteLine("Recalculating worth");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "Worth", "REAL");
            var worth = await BalanceTuner.StoredWorth(dbContext);
            var ids = await dbContext.Leaderboards.AsNoTracking().Select(l => l.Id).ToListAsync();
            var updates = ids.Select(id => new Leaderboard { Id = id, Worth = worth.TryGetValue(id, out var w) ? w[0] : null }).ToList();
            await dbContext.BulkUpdateAsync(updates, options => options.ColumnInputExpression = c => new { c.Worth });
            await StoreModifierWorth(dbContext, worth);
            dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
            Console.WriteLine($"Worth: {worth.Values.Count(w => w[0] != null)} leaderboards ({Program.Stopwatch.ElapsedMilliseconds / 1000} seconds)");
            await RefreshWorthQuantiles(dbContext);
        }

        // PlayerAdjustedQuantile of Leaderboard.WorthMean, WorthMedian, WorthP75, WorthP90
        public static readonly double[] WorthQuantiles = { 0, 0.5, 0.75, 0.9 };

        // Recomputes Leaderboard.WorthMean / WorthMedian / WorthP75 / WorthP90 and their WorthSpread alone (Refresh and RefreshWorth run it too)
        public static async Task RefreshWorthQuantiles(AppContext dbContext)
        {
            Console.WriteLine("Recalculating worth by quantile (mean, median, 75th, 90th percentile)");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            foreach (var column in new[] { "WorthMean", "WorthMedian", "WorthP75", "WorthP90", "WorthSpread" })
            {
                await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", column, "REAL");
            }
            var worth = await BalanceTuner.StoredWorthQuantiles(dbContext, WorthQuantiles);
            var ids = await dbContext.Leaderboards.AsNoTracking().Select(l => l.Id).ToListAsync();
            var updates = ids.Select(id =>
            {
                var w = worth.TryGetValue(id, out var values) ? values : new float?[WorthQuantiles.Length];
                float? spread = w.All(v => v != null) ? w.Max() - w.Min() : null;
                return new Leaderboard { Id = id, WorthMean = w[0], WorthMedian = w[1], WorthP75 = w[2], WorthP90 = w[3], WorthSpread = spread };
            }).ToList();
            await dbContext.BulkUpdateAsync(updates, options => options.ColumnInputExpression = c => new { c.WorthMean, c.WorthMedian, c.WorthP75, c.WorthP90, c.WorthSpread });
            dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
            Console.WriteLine($"Worth by quantile: {worth.Values.Count(w => w[0] != null)} leaderboards ({Program.Stopwatch.ElapsedMilliseconds / 1000} seconds)");
        }

        // SSWorth / FSWorth / SFWorth (BalanceTuner.WorthSpeeds order: nomod, SS, FS, SF)
        private static async Task StoreModifierWorth(AppContext dbContext, Dictionary<string, float?[]> worth)
        {
            foreach (var column in new[] { "SSWorth", "FSWorth", "SFWorth" })
            {
                await DbSchema.AddColumnIfMissing(dbContext, "ModifiersRating", column, "REAL");
            }
            var modifierIds = await dbContext.Leaderboards.AsNoTracking().Where(l => l.ModifiersRating != null)
                .Select(l => new { l.Id, ModifiersRatingId = l.ModifiersRating!.Id })
                .ToListAsync();
            var modifierUpdates = modifierIds.Select(l =>
            {
                var w = worth.TryGetValue(l.Id, out var values) ? values : new float?[4];
                return new ModifiersRating { Id = l.ModifiersRatingId, SSWorth = w[1], FSWorth = w[2], SFWorth = w[3] };
            }).ToList();
            await dbContext.BulkUpdateAsync(modifierUpdates, options => options.ColumnInputExpression = c => new { c.SSWorth, c.FSWorth, c.SFWorth });
        }

        // Recomputes Leaderboard.TopPlayShare / TopPlayShareRelative alone (Refresh runs it too), with the acc reweighter's
        // top play share limiter settings
        public static async Task RefreshTopPlayShares(AppContext dbContext)
        {
            Console.WriteLine("Recalculating top play shares");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "TopPlayShare", "REAL");
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "TopPlayShareRelative", "REAL");

            var stars = await dbContext.Leaderboards.AsNoTracking().Where(l => l.Stars > 0).ToDictionaryAsync(l => l.Id, l => l.Stars);
            var scores = await dbContext.Scores.AsNoTracking().Where(s => s.Pp > 0).Select(s => new { s.PlayerId, s.LeaderboardId, s.Pp }).ToListAsync();
            var players = new Dictionary<string, int>();
            var tops = new Dictionary<string, int>();
            foreach (var g in scores.Where(s => s.LeaderboardId != null && stars.ContainsKey(s.LeaderboardId)).GroupBy(s => s.PlayerId))
            {
                var plays = g.OrderByDescending(s => s.Pp).ToList();
                if (plays.Count < AccReweighter.TopPlayShareMinScores) continue;
                foreach (var id in plays.Select(s => s.LeaderboardId).Distinct()) players[id] = players.GetValueOrDefault(id) + 1;
                if (plays.Count > 1 && plays[0].Pp - plays[1].Pp >= AccReweighter.TopPlayMarginPp) tops[plays[0].LeaderboardId] = tops.GetValueOrDefault(plays[0].LeaderboardId) + 1;
            }
            var share = players.Where(p => p.Value >= AccReweighter.TopPlayShareMinPlayers).ToDictionary(p => p.Key, p => (double)tops.GetValueOrDefault(p.Key) / p.Value);

            var updates = stars.Keys.Select(id =>
            {
                if (!share.TryGetValue(id, out double s)) return new Leaderboard { Id = id };
                var near = share.Where(o => Math.Abs(stars[o.Key] - stars[id]) <= AccReweighter.TopPlayShareBandwidth).Select(o => o.Value).OrderBy(v => v).ToList();
                double reference = Math.Max(near[near.Count / 2], AccReweighter.TopPlayShareFloor);
                return new Leaderboard { Id = id, TopPlayShare = (float)s, TopPlayShareRelative = (float)(s / reference) };
            }).ToList();
            await dbContext.BulkUpdateAsync(updates, options => options.ColumnInputExpression = c => new { c.TopPlayShare, c.TopPlayShareRelative });

            dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
            Console.WriteLine($"Top play shares: {share.Count} maps, {updates.Count(u => u.TopPlayShareRelative > AccReweighter.TopPlayShareRatio)} past {AccReweighter.TopPlayShareRatio}x");
        }

        // Rank ranges of Leaderboard.WorthRank*: up to each rank, from the previous one
        public static readonly int[] WorthRanks = { 20, 100, 1000, 3000, 10000 };
        public const int WorthRankMinScores = 5;
        private const int WorthRankMinPlays = 75;   // as Megametric75
        private const int WorthRankMinNeighbours = 5;   // other maps with a value within WorthRankNeighbourStars stars
        private const double WorthRankNeighbourStars = 0.4498;   // (was 0.5 on the old prod-scale stars, 1.1653 x (pp / 83.6925)^1.17507)

        // Recomputes Leaderboard.WorthRank20 ... WorthRank10000 alone (Refresh runs it too)
        public static async Task RefreshWorthByRank(AppContext dbContext)
        {
            Console.WriteLine("Recalculating worth by player rank");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            foreach (int rank in WorthRanks)
            {
                await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", $"WorthRank{rank}", "REAL");
            }

            var scores = await dbContext.Scores
                .Where(s => s.Player.TopPp > 0 && s.Player.RankedPlayCount > WorthRankMinPlays && s.Player.Rank > 0 && s.Player.Rank <= 10000)
                .Select(s => new { s.LeaderboardId, s.Pp, s.Player.TopPp, s.Player.Rank, s.Modifiers })
                .ToListAsync();
            // Nomod scores only, as Worth: SS / FS / SF scores are paid by the modifier's ratings, NF scores 0
            scores = scores.Where(s => BalanceTuner.WorthSpeedOf(s.Modifiers ?? "") == 0).ToList();
            var stars = await dbContext.Leaderboards.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Stars);

            // Per map and rank range: sum of pp / top pp, and how many scores
            var sums = new Dictionary<string, (double Sum, int Count)[]>();
            foreach (var s in scores)
            {
                int range = Array.FindIndex(WorthRanks, r => s.Rank <= r);
                if (range < 0 || s.LeaderboardId == null || !stars.ContainsKey(s.LeaderboardId)) continue;
                if (!sums.TryGetValue(s.LeaderboardId, out var ranges)) sums[s.LeaderboardId] = ranges = new (double, int)[WorthRanks.Length];
                ranges[range] = (ranges[range].Sum + s.Pp / s.TopPp, ranges[range].Count + 1);
            }

            var worth = stars.Keys.ToDictionary(id => id, _ => new float?[WorthRanks.Length]);
            for (int range = 0; range < WorthRanks.Length; range++)
            {
                var means = sums
                    .Where(m => m.Value[range].Count >= WorthRankMinScores)
                    .ToDictionary(m => m.Key, m => m.Value[range].Sum / m.Value[range].Count);
                var points = means.Select(m => ((double)stars[m.Key], m.Value)).ToList();
                Parallel.ForEach(means, m =>
                {
                    // Without enough other maps of its stars (the highest star maps), the reference is mostly the map itself
                    double mapStars = stars[m.Key];
                    if (points.Count(p => Math.Abs(p.Item1 - mapStars) <= WorthRankNeighbourStars) - 1 < WorthRankMinNeighbours) return;
                    double reference = BalanceTuner.WorthReference(points, mapStars);
                    if (reference > 0) worth[m.Key][range] = (float)(m.Value / reference - 1);
                });
            }

            var updates = worth.Select(w => new Leaderboard
            {
                Id = w.Key,
                WorthRank20 = w.Value[0],
                WorthRank100 = w.Value[1],
                WorthRank1000 = w.Value[2],
                WorthRank3000 = w.Value[3],
                WorthRank10000 = w.Value[4],
            }).ToList();
            await dbContext.BulkUpdateAsync(updates, options => options.ColumnInputExpression = c =>
                new { c.WorthRank20, c.WorthRank100, c.WorthRank1000, c.WorthRank3000, c.WorthRank10000 });

            dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
            Console.WriteLine($"Worth by player rank: {scores.Count} scores of the top 10000 players");
        }

        // Tag thresholds (DetermineTags): Bomb Reset from bomb resets (the Analyzer's bomb avoidances) per minute, Inline from
        // inlines (Statistics.Inlines) per minute, both over the song's length (Leaderboard.Duration)
        public const float LinearPercentThreshold = 0.4f;
        public const float FitbeatDodgeWallValue = 0.3f;
        public const float FitbeatCrouchWallValue = 5f;
        public const float FitbeatDensityThreshold = 0.15f;
        public const float BombResetsPerMinuteThreshold = 4f;
        public const float InlinesPerMinuteThreshold = 8f;

        // The tags' columns (older databases don't have them)
        public static async Task EnsureTagColumns(AppContext dbContext)
        {
            foreach (var (column, type) in new[] { ("IsFitbeat", "INTEGER"), ("IsLinear", "INTEGER"), ("IsBombReset", "INTEGER"), ("IsInline", "INTEGER"), ("MapType", "TEXT"), ("Inlines", "INTEGER") })
            {
                await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", column, type);
            }
        }

        public static async Task DetermineTags(AppContext dbContext)
        {
            Console.WriteLine("Determining Leaderboard Tags (Linear, Fitbeat, Bomb Reset, Inline, map type)");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            await EnsureTagColumns(dbContext);

            var leaderboards = await dbContext.Leaderboards
                .Select(l => new Leaderboard
                {
                    Id = l.Id,
                    LinearPercent = l.LinearPercent,
                    DodgeWalls = l.DodgeWalls,
                    CrouchWalls = l.CrouchWalls,
                    Duration = l.Duration,
                    BombAvoidances = l.BombAvoidances,
                    Inlines = l.Inlines,
                    AccRating = l.AccRating,
                    PassRating = l.PassRating,
                    TechRating = l.TechRating,
                })
                .ToListAsync();

            // The map type from RatingAPI's tagging model (acc, tech, midspeed, speed, extreme) on the ratings
            var tagger = new global::RatingAPI.Controllers.InferPublish();
            foreach (var leaderboard in leaderboards)
            {
                leaderboard.IsLinear = leaderboard.LinearPercent >= LinearPercentThreshold;

                float wallScore = (leaderboard.DodgeWalls * FitbeatDodgeWallValue) + (leaderboard.CrouchWalls * FitbeatCrouchWallValue);
                float minutes = leaderboard.Duration / 60;
                leaderboard.IsFitbeat = leaderboard.Duration != 0 && wallScore / leaderboard.Duration >= FitbeatDensityThreshold;
                leaderboard.IsBombReset = minutes > 0 && leaderboard.BombAvoidances / minutes >= BombResetsPerMinuteThreshold;
                leaderboard.IsInline = minutes > 0 && leaderboard.Inlines / minutes >= InlinesPerMinuteThreshold;
                try
                {
                    leaderboard.MapType = leaderboard.AccRating > 0 || leaderboard.PassRating > 0
                        ? tagger.Tag(leaderboard.AccRating, leaderboard.TechRating, leaderboard.PassRating)
                        : null;
                }
                catch { leaderboard.MapType = null; }
            }

            await dbContext.BulkUpdateAsync(leaderboards, options =>
                options.ColumnInputExpression = c => new { c.IsLinear, c.IsFitbeat, c.IsBombReset, c.IsInline, c.MapType });
            Console.WriteLine(FormattableString.Invariant(
                $"Tags: Linear {leaderboards.Count(l => l.IsLinear)}, Fitbeat {leaderboards.Count(l => l.IsFitbeat)}, Bomb Reset {leaderboards.Count(l => l.IsBombReset)}, Inline {leaderboards.Count(l => l.IsInline)}; {string.Join(", ", leaderboards.GroupBy(l => l.MapType ?? "none").Select(g => $"{g.Key} {g.Count()}"))}"));

            Console.WriteLine("Tag determination completed");
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }

        private static float CalculateMegametric(
            List<MegametricData> megametricData,
            int topCount,
            int minRankedPlayCount)
        {
            var filtered = megametricData
                .Where(s => s.RankedPlayCount > minRankedPlayCount)
                .OrderByDescending(s => s.Weight)
                .Take(topCount)
                .ToList();

            return filtered.Count > 10
                ? filtered.Average(s => s.Pp / s.TopPp * s.Weight)
                : 0;
        }

        public static async Task RefreshStars(AppContext dbContext)
        {
            Console.WriteLine("Recalculating Star Ratings");
            // Stars use each one's curve (a 96% score's pp), so the curves are loaded too
            var lbs = dbContext
                .Leaderboards
                .AsNoTracking()
                .Include(s => s.ModifiersRating)
                .ToList();
            foreach (var lb in lbs)
            {
                ReplayUtils.SetStars(lb);
            }

            await dbContext.BulkUpdateAsync(lbs, options => options.ColumnInputExpression = c => new { c.Stars });

            var mods = lbs.Select(lb => lb.ModifiersRating).Where(mod => mod != null).Select(mod => mod!).ToList();
            await dbContext.BulkUpdateAsync(mods, options => options.ColumnInputExpression = c => new { c.SSStars, c.SFStars, c.FSStars });
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }
    }
}
