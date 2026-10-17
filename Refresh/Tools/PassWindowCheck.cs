using beatleader_analyzer;
using beatleader_parser;
using Microsoft.EntityFrameworkCore;
using RatingAPI.Controllers;
using System.Collections.Concurrent;
using System.Text.Json;

namespace portaBLe.Refresh
{
    // Pass and tech ratings of every leaderboard (no modifier) with the Analyzer's pass windows in seconds (current) and in
    // swings (before), to see which maps the change moved. Analyzer only, no Acc AI, nothing written to the database.
    // Maps come from the maps folder (downloaded when missing). Writes wwwroot/pass-window-check.json.
    public static class PassWindowCheck
    {
        public const string ResultFile = "pass-window-check.json";

        public static async Task Run(AppContext dbContext, string webRootPath)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var lbs = await dbContext.Leaderboards.AsNoTracking()
                .Select(lb => new { lb.Id, lb.Hash, lb.Name, lb.ModeName, lb.DifficultyName, lb.Stars, lb.PassRating, lb.TechRating })
                .ToListAsync();
            Console.WriteLine($"Pass window check: {lbs.Count} leaderboards");

            var rows = new ConcurrentBag<object[]>();
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

                    // Same thread for both runs (the switch is per thread); no await in between
                    var analyzer = new Analyze();
                    Analyze.UseSwingCountPassWindows = false;
                    var seconds = analyzer.GetRating(set.Data, lb.ModeName, lb.DifficultyName, bpm);
                    Analyze.UseSwingCountPassWindows = true;
                    var swings = analyzer.GetRating(set.Data, lb.ModeName, lb.DifficultyName, bpm);
                    Analyze.UseSwingCountPassWindows = false;
                    if (seconds == null || swings == null) { Interlocked.Increment(ref failed); return; }

                    rows.Add(new object[]
                    {
                        lb.Id, lb.Name ?? "", lb.DifficultyName ?? "", lb.ModeName ?? "", Math.Round(lb.Stars, 3),
                        Math.Round(lb.PassRating, 4), Math.Round(lb.TechRating, 4),
                        Math.Round(seconds.PassRating, 4), Math.Round(seconds.TechRating, 4),
                        Math.Round(swings.PassRating, 4), Math.Round(swings.TechRating, 4),
                    });
                    int n = Interlocked.Increment(ref done);
                    if (n % 500 == 0) Console.WriteLine($"Pass window check: {n} done ({timer.Elapsed:mm\\:ss})");
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref failed);
                    Console.WriteLine($"Pass window check: {lb.Id} skipped ({e.Message})");
                }
                finally
                {
                    Analyze.UseSwingCountPassWindows = false;
                }
            });

            var path = Path.Combine(webRootPath, ResultFile);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                GeneratedAt = DateTime.UtcNow,
                Columns = new[] { "Id", "Name", "Difficulty", "Mode", "Stars", "DbPass", "DbTech", "PassSeconds", "TechSeconds", "PassSwings", "TechSwings" },
                Rows = rows.OrderBy(r => (string)r[0]).ToList(),
            }));
            Console.WriteLine($"Pass window check: {done} leaderboards, {failed} skipped, saved to {path} ({timer.Elapsed:mm\\:ss})");
        }
    }
}
