using Microsoft.EntityFrameworkCore;
using portaBLe.DB;

namespace portaBLe.Refresh
{
    // ScoresRefresh.Refresh + PlayersRefresh.Refresh in one pass: same results, about 3x faster.
    //
    // The two separate steps read every score twice (Players in 70+ batched queries joined with Players) and write most
    // of them twice (pp and rank, then the weights, which nearly all change after a rating change). Here every score is
    // read once, pp / leaderboard ranks / weights / player totals are computed in memory, and each score is written once.
    //
    // Differences with the old steps, none of which change a value: ties (same rounded pp and accuracy on a leaderboard,
    // same pp in a profile, same total pp) are broken by score / player id instead of database order, and every weight is
    // written instead of only the changed ones.
    public class ScoresPlayersRefresh
    {
        private const int WRITE_CHUNK = 500_000;
        private const int MAX_WEIGHTED = 10000;   // as PlayersRefresh: scores past the 10000th weigh 0

        private struct ScoreRow
        {
            public int Id;
            public int Lb;
            public int Player;
            public float Accuracy;
            public string Modifiers;
            public float Pp, BonusPp, PassPp, AccPp, TechPp, Weight;
            public int Rank;
        }

        private class LeaderboardRatings
        {
            public string Id;
            public float AccRating, PassRating, TechRating;
            public List<System.Numerics.Vector2>? Curve;
            public ModifiersRating? ModifiersRating;
        }

        public static async Task Refresh(AppContext dbContext)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine("Recalculating Scores and Players (single pass)");
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;

            var leaderboards = await dbContext.Leaderboards.AsNoTracking()
                .Select(lb => new LeaderboardRatings { Id = lb.Id, AccRating = lb.AccRating, PassRating = lb.PassRating, TechRating = lb.TechRating, Curve = lb.Curve, ModifiersRating = lb.ModifiersRating })
                .ToListAsync();
            var lbIndex = new Dictionary<string, int>(leaderboards.Count);
            for (int i = 0; i < leaderboards.Count; i++) lbIndex[leaderboards[i].Id] = i;

            var players = await dbContext.Players.AsNoTracking().Select(p => new { p.Id, p.Country }).ToListAsync();
            var playerIndex = new Dictionary<string, int>(players.Count);
            for (int i = 0; i < players.Count; i++) playerIndex[players[i].Id] = i;

            var raw = await dbContext.Scores.AsNoTracking()
                .Select(s => new { s.Id, s.LeaderboardId, s.PlayerId, s.Accuracy, s.Modifiers })
                .ToListAsync();
            var rows = new ScoreRow[raw.Count];
            int kept = 0;
            foreach (var s in raw)
            {
                if (s.LeaderboardId == null || s.PlayerId == null) continue;
                if (!lbIndex.TryGetValue(s.LeaderboardId, out int lb) || !playerIndex.TryGetValue(s.PlayerId, out int player)) continue;
                rows[kept++] = new ScoreRow { Id = s.Id, Lb = lb, Player = player, Accuracy = s.Accuracy, Modifiers = s.Modifiers ?? "" };
            }
            raw = null;
            Array.Resize(ref rows, kept);
            Console.WriteLine($"Loaded {rows.Length} scores, {leaderboards.Count} leaderboards, {players.Count} players ({timer.Elapsed:mm\\:ss})");

            // pp of every score (as ScoresRefresh)
            Parallel.For(0, rows.Length, new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount }, i =>
            {
                ref var r = ref rows[i];
                var lb = leaderboards[r.Lb];
                (float pp, float bonus, float pass, float acc, float tech) = ReplayUtils.PpFromScore(
                    r.Accuracy, r.Modifiers, lb.ModifiersRating, lb.AccRating, lb.PassRating, lb.TechRating, lb.Curve);
                r.Pp = float.IsNaN(pp) ? 0 : pp;
                r.BonusPp = bonus;
                r.PassPp = pass;
                r.AccPp = acc;
                r.TechPp = tech;
            });

            // Leaderboard ranks: rounded pp, then rounded accuracy (as ScoresRefresh)
            var byLb = GroupIndices(rows.Length, i => rows[i].Lb, leaderboards.Count);
            Parallel.ForEach(byLb, new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount }, list =>
            {
                list.Sort((a, b) =>
                {
                    int c = Math.Round(rows[b].Pp, 2).CompareTo(Math.Round(rows[a].Pp, 2));
                    if (c != 0) return c;
                    c = Math.Round(rows[b].Accuracy, 4).CompareTo(Math.Round(rows[a].Accuracy, 4));
                    return c != 0 ? c : rows[a].Id.CompareTo(rows[b].Id);
                });
                for (int k = 0; k < list.Count; k++) rows[list[k]].Rank = k + 1;
            });

            // Weights and player totals (as PlayersRefresh)
            var weights = new float[MAX_WEIGHTED];
            for (int i = 0; i < MAX_WEIGHTED; i++) weights[i] = MathF.Pow(0.965f, i);
            var byPlayer = GroupIndices(rows.Length, i => rows[i].Player, players.Count);
            var updates = new Player?[players.Count];
            Parallel.For(0, players.Count, new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount }, p =>
            {
                var list = byPlayer[p];
                if (list.Count == 0) return;
                list.Sort((a, b) =>
                {
                    int c = rows[b].Pp.CompareTo(rows[a].Pp);
                    return c != 0 ? c : rows[a].Id.CompareTo(rows[b].Id);
                });
                float total = 0, acc = 0, tech = 0, pass = 0;
                for (int k = 0; k < list.Count; k++)
                {
                    ref var r = ref rows[list[k]];
                    float w = k < MAX_WEIGHTED ? weights[k] : 0f;
                    r.Weight = w;
                    total += r.Pp * w;
                    acc += r.AccPp * w;
                    tech += r.TechPp * w;
                    pass += r.PassPp * w;
                }
                updates[p] = new Player
                {
                    Id = players[p].Id,
                    Country = players[p].Country,
                    Pp = total,
                    AccPp = acc,
                    TechPp = tech,
                    PassPp = pass,
                    TopPp = rows[list[0]].Pp,
                    RankedPlayCount = list.Count,
                };
            });

            // Global and country ranks
            var playerList = updates.Where(p => p != null).Select(p => p!)
                .OrderByDescending(p => p.Pp).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
            var countries = new Dictionary<string, int>();
            for (int i = 0; i < playerList.Count; i++)
            {
                var player = playerList[i];
                player.Rank = i + 1;
                if (!string.IsNullOrEmpty(player.Country))
                {
                    countries.TryGetValue(player.Country, out int countryRank);
                    player.CountryRank = ++countryRank;
                    countries[player.Country] = countryRank;
                }
            }
            Console.WriteLine($"Computed pp, ranks and weights ({timer.Elapsed:mm\\:ss}), writing {rows.Length} scores and {playerList.Count} players...");

            // One write per score, in chunks to keep memory down
            for (int start = 0; start < rows.Length; start += WRITE_CHUNK)
            {
                int end = Math.Min(start + WRITE_CHUNK, rows.Length);
                var chunk = new List<Score>(end - start);
                for (int i = start; i < end; i++)
                {
                    ref var r = ref rows[i];
                    chunk.Add(new Score { Id = r.Id, Pp = r.Pp, BonusPp = r.BonusPp, PassPP = r.PassPp, AccPP = r.AccPp, TechPP = r.TechPp, Rank = r.Rank, Weight = r.Weight });
                }
                await dbContext.BulkUpdateAsync(chunk,
                    options => options.ColumnInputExpression = c => new { c.Rank, c.Pp, c.BonusPp, c.PassPP, c.AccPP, c.TechPP, c.Weight });
                Console.WriteLine($"  {end}/{rows.Length} scores written ({timer.Elapsed:mm\\:ss})");
            }
            await dbContext.BulkUpdateAsync(playerList,
                options => options.ColumnInputExpression = c => new { c.Rank, c.Pp, c.TopPp, c.RankedPlayCount, c.CountryRank, c.AccPp, c.PassPp, c.TechPp });

            dbContext.ChangeTracker.AutoDetectChangesEnabled = true;
            Console.WriteLine($"Complete! Scores and players in {timer.Elapsed:mm\\:ss} (total {Program.Stopwatch.ElapsedMilliseconds / 1000} seconds)");
        }

        // Score indices grouped by a key in [0, count), each group in score order
        private static List<int>[] GroupIndices(int n, Func<int, int> key, int count)
        {
            var sizes = new int[count];
            for (int i = 0; i < n; i++) sizes[key(i)]++;
            var groups = new List<int>[count];
            for (int g = 0; g < count; g++) groups[g] = new List<int>(sizes[g]);
            for (int i = 0; i < n; i++) groups[key(i)].Add(i);
            return groups;
        }
    }
}
