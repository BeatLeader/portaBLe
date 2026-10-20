using beatleader_analyzer;
using beatleader_parser;
using Microsoft.EntityFrameworkCore;
using RatingAPI.Controllers;
using System.Collections.Concurrent;
using System.Text.Json;

namespace portaBLe.Refresh
{
    // Pass and tech ratings of every leaderboard (no modifier) with the Analyzer as it is now (new), next to the pass stored
    // in this database and in the prod database, to compare how each orders the maps before re-rating (Pass Rating Comparison
    // page). Analyzer only, no Acc AI, nothing written to any database. Maps come from the maps folder (downloaded when
    // missing). Writes wwwroot/pass-window-check.json.
    public static class PassWindowCheck
    {
        public const string ResultFile = "pass-window-check.json";

        public static readonly string[] Columns =
            { "Id", "Name", "Difficulty", "Mode", "Stars", "PassNew", "TechNew", "PassDb", "TechDb", "PassProd" };

        public static async Task Run(AppContext dbContext, string webRootPath, string prodDbFile)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var lbs = await dbContext.Leaderboards.AsNoTracking()
                .Select(lb => new { lb.Id, lb.Hash, lb.Name, lb.ModeName, lb.DifficultyName, lb.Stars, lb.PassRating, lb.TechRating })
                .ToListAsync();
            Dictionary<string, float> prodPass;
            using (var prodDb = ModelComparisonExport.Open(webRootPath, prodDbFile))
            {
                prodPass = await prodDb.Leaderboards.AsNoTracking().ToDictionaryAsync(lb => lb.Id, lb => lb.PassRating);
            }
            Console.WriteLine($"Pass window check: {lbs.Count} leaderboards, {prodPass.Count} in {prodDbFile}");

            var rows = new ConcurrentBag<object?[]>();
            var downloader = new Downloader("maps");
            int done = 0, failed = 0;
            await Parallel.ForEachAsync(lbs, new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount }, async (lb, ct) =>
            {
                try
                {
                    var mapPath = await downloader.MapAsync(lb.Hash);
                    if (mapPath == null) { Interlocked.Increment(ref failed); return; }
                    var mapset = new Parse().TryLoadPath(mapPath);
                    int diffCode = RatingsRefresh.GetDiffCode(lb.DifficultyName);
                    var set = mapset?.Difficulties.FirstOrDefault(d => d.Characteristic == lb.ModeName
                        && (d.BeatMap._difficultyRank == diffCode || d.BeatMap._difficulty == lb.DifficultyName));
                    if (set == null) { Interlocked.Increment(ref failed); return; }
                    float bpm = (float)mapset!.Info._beatsPerMinute;

                    var current = new Analyze().GetRating(set.Data, lb.ModeName, lb.DifficultyName, bpm);
                    if (current == null) { Interlocked.Increment(ref failed); return; }

                    rows.Add(new object?[]
                    {
                        lb.Id, lb.Name ?? "", lb.DifficultyName ?? "", lb.ModeName ?? "", Math.Round(lb.Stars, 3),
                        Math.Round(current.PassRating, 4), Math.Round(current.TechRating, 4),
                        Math.Round(lb.PassRating, 4), Math.Round(lb.TechRating, 4),
                        prodPass.TryGetValue(lb.Id, out var prod) ? Math.Round(prod, 4) : null,
                    });
                    int n = Interlocked.Increment(ref done);
                    if (n % 500 == 0) Console.WriteLine($"Pass window check: {n} done ({timer.Elapsed:mm\\:ss})");
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref failed);
                    Console.WriteLine($"Pass window check: {lb.Id} skipped ({e.Message})");
                }
            });

            var path = Path.Combine(webRootPath, ResultFile);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                GeneratedAt = DateTime.UtcNow,
                ProdDatabase = prodDbFile,
                Columns,
                Rows = rows.OrderBy(r => (string)r[0]!).ToList(),
            }));
            Console.WriteLine($"Pass window check: {done} leaderboards, {failed} skipped, saved to {path} ({timer.Elapsed:mm\\:ss})");
        }
    }
}
