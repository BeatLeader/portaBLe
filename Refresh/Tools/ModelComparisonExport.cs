using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;

namespace portaBLe.Refresh
{
    // Writes a compact side-by-side of two databases (e.g. prod with the old Acc AI model vs this one with the new model
    // and balance) to wwwroot/model-comparison.json, small enough to share and analyse outside portaBLe:
    // - every leaderboard: predicted acc, acc / pass / tech ratings, stars, Megametric75 and best pp in each database,
    //   plus the actual accuracy of its scores (the same scores in both databases),
    // - every player ranked in the top 2000 of either database: rank, pp and the pass / acc / tech split in each.
    // Read only: nothing is written to either database.
    public static class ModelComparisonExport
    {
        public const string FileName = "model-comparison.json";
        private const int TopPlayers = 2000;

        public static async Task Run(string webRootPath, string oldDbFile, string newDbFile = "Database.db")
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            using var oldDb = Open(webRootPath, oldDbFile);
            using var newDb = Open(webRootPath, newDbFile);

            Console.WriteLine($"Model comparison: loading leaderboards from {oldDbFile} and {newDbFile}...");
            var oldMaps = await LoadMaps(oldDb);
            var newMaps = await LoadMaps(newDb);

            // Accuracy of the scores (the same scores in both databases, so taken from the new one)
            var accuracies = (await newDb.Scores.AsNoTracking()
                    .Select(s => new { s.LeaderboardId, s.Accuracy })
                    .ToListAsync())
                .Where(s => s.LeaderboardId != null && s.Accuracy > 0 && s.Accuracy <= 1)
                .GroupBy(s => s.LeaderboardId!)
                .ToDictionary(g => g.Key, g => g.Select(s => s.Accuracy).OrderByDescending(a => a).ToArray());

            var maps = new List<object?[]>();
            foreach (var (id, n) in newMaps)
            {
                if (!oldMaps.TryGetValue(id, out var o)) continue;
                accuracies.TryGetValue(id, out var accs);
                accs ??= Array.Empty<float>();
                int top = Math.Max(1, (int)Math.Ceiling(accs.Length * 0.1));
                maps.Add(new object?[]
                {
                    id, n.Name, n.ModeName, n.DifficultyName, accs.Length,
                    R(accs.Length > 0 ? accs.Average(a => (double)a) : 0, 5),
                    R(accs.Length > 0 ? accs.Take(top).Average(a => (double)a) : 0, 5),
                    R(o.PredictedAcc, 5), R(n.PredictedAcc, 5),
                    R(o.AccRating, 4), R(n.AccRating, 4),
                    R(o.PassRating, 4), R(n.PassRating, 4),
                    R(o.TechRating, 4), R(n.TechRating, 4),
                    R(o.Stars, 3), R(n.Stars, 3),
                    R(o.Megametric75, 4), R(n.Megametric75, 4),
                    R(o.BestPp, 1), R(n.BestPp, 1),
                });
            }
            Console.WriteLine($"Model comparison: {maps.Count} leaderboards in both ({timer.Elapsed:mm\\:ss})");

            var oldPlayers = await LoadPlayers(oldDb);
            var newPlayers = await LoadPlayers(newDb);
            var players = new List<object?[]>();
            foreach (var id in oldPlayers.Where(p => p.Value.Rank > 0 && p.Value.Rank <= TopPlayers).Select(p => p.Key)
                         .Union(newPlayers.Where(p => p.Value.Rank > 0 && p.Value.Rank <= TopPlayers).Select(p => p.Key)))
            {
                oldPlayers.TryGetValue(id, out var o);
                newPlayers.TryGetValue(id, out var n);
                players.Add(new object?[]
                {
                    id, n?.Name ?? o?.Name, n?.RankedPlayCount ?? o?.RankedPlayCount ?? 0,
                    o?.Rank ?? 0, n?.Rank ?? 0,
                    R(o?.Pp ?? 0, 1), R(n?.Pp ?? 0, 1),
                    R(o?.PassPp ?? 0, 1), R(n?.PassPp ?? 0, 1),
                    R(o?.AccPp ?? 0, 1), R(n?.AccPp ?? 0, 1),
                    R(o?.TechPp ?? 0, 1), R(n?.TechPp ?? 0, 1),
                    R(o?.TopPp ?? 0, 1), R(n?.TopPp ?? 0, 1),
                });
            }

            var result = new
            {
                GeneratedAt = DateTime.UtcNow,
                OldDb = oldDbFile,
                NewDb = newDbFile,
                MapColumns = new[]
                {
                    "Id", "Name", "Mode", "Difficulty", "Scores", "AverageAcc", "Top10PercentAcc",
                    "OldPredictedAcc", "NewPredictedAcc", "OldAccRating", "NewAccRating", "OldPassRating", "NewPassRating",
                    "OldTechRating", "NewTechRating", "OldStars", "NewStars", "OldMegametric75", "NewMegametric75", "OldBestPp", "NewBestPp",
                },
                Maps = maps,
                PlayerColumns = new[]
                {
                    "Id", "Name", "RankedPlayCount", "OldRank", "NewRank", "OldPp", "NewPp", "OldPassPp", "NewPassPp",
                    "OldAccPp", "NewAccPp", "OldTechPp", "NewTechPp", "OldTopPp", "NewTopPp",
                },
                Players = players,
            };

            var path = Path.Combine(webRootPath, FileName);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result));
            Console.WriteLine($"Model comparison saved to {path}: {maps.Count} leaderboards, {players.Count} players ({timer.Elapsed:mm\\:ss})");
        }

        private static AppContext Open(string webRootPath, string fileName)
        {
            var optionsBuilder = new DbContextOptionsBuilder<AppContext>();
            optionsBuilder.UseSqlite($"Data Source={Path.Combine(webRootPath, fileName)};");
            return new AppContext(optionsBuilder.Options);
        }

        private sealed class MapRow
        {
            public string Name = "";
            public string ModeName = "";
            public string DifficultyName = "";
            public float PredictedAcc, AccRating, PassRating, TechRating, Stars, Megametric75, BestPp;
        }

        private static async Task<Dictionary<string, MapRow>> LoadMaps(AppContext db)
        {
            var best = (await db.Scores.AsNoTracking()
                    .GroupBy(s => s.LeaderboardId)
                    .Select(g => new { Id = g.Key, Best = g.Max(s => s.Pp) })
                    .ToListAsync())
                .Where(x => x.Id != null)
                .ToDictionary(x => x.Id!, x => x.Best);

            return (await db.Leaderboards.AsNoTracking()
                    .Select(l => new { l.Id, l.Name, l.ModeName, l.DifficultyName, l.PredictedAcc, l.AccRating, l.PassRating, l.TechRating, l.Stars, l.Megametric75 })
                    .ToListAsync())
                .ToDictionary(l => l.Id, l => new MapRow
                {
                    Name = l.Name ?? "",
                    ModeName = l.ModeName ?? "",
                    DifficultyName = l.DifficultyName ?? "",
                    PredictedAcc = l.PredictedAcc,
                    AccRating = l.AccRating,
                    PassRating = l.PassRating,
                    TechRating = l.TechRating,
                    Stars = l.Stars,
                    Megametric75 = l.Megametric75,
                    BestPp = best.TryGetValue(l.Id, out var b) ? b : 0,
                });
        }

        private static async Task<Dictionary<string, Player>> LoadPlayers(AppContext db) =>
            await db.Players.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p);

        // Rounded, and 0 instead of NaN / infinity (which JSON can't hold)
        private static double R(double value, int digits) => double.IsFinite(value) ? Math.Round(value, digits) : 0;
    }
}
