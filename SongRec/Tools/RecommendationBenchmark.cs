using beatleader_songrec.Api;
using beatleader_songrec.Core;
using Microsoft.EntityFrameworkCore;
using DbContextType = Microsoft.EntityFrameworkCore.DbContext;
using DbPlayer = portaBLe.DB.Player;

namespace beatleader_songrec.Tools;

/// <summary>
/// Times the Song Recommendations page's run (<see cref="RecommendationPipeline"/> with the default options, on the
/// database as is) for players spread over the ranks, and prints a digest of each player's suggestions, so a speed
/// change can be checked to leave the suggestions as they were.
/// Run: dotnet run -c Release -- songrecbench &lt;wwwroot/file.db&gt; [players].
/// </summary>
public static class RecommendationBenchmark
{
    public static async Task Run(Func<DbContextType> createContext, int players = 10)
    {
        List<string> ids;
        using (var context = createContext())
        {
            var ranked = await context.Set<DbPlayer>().AsNoTracking()
                .Where(p => p.Rank > 0 && p.RankedPlayCount >= 20)
                .OrderBy(p => p.Rank)
                .Select(p => p.Id)
                .ToListAsync();
            // Spread over the ranks, more of them at the top (where players have the most plays)
            ids = Enumerable.Range(0, players)
                .Select(i => ranked[(int)(ranked.Count * Math.Pow((double)i / players, 2))])
                .Distinct()
                .ToList();
        }

        var options = new RecommendationOptions();
        var total = System.Diagnostics.Stopwatch.StartNew();
        var times = new List<double>();
        foreach (var id in ids)
        {
            // A fresh context per run, as the page
            using var context = createContext();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            // The first run's steps, timed
            Action<string>? onProgress = times.Count < 3 ? message => Console.WriteLine($"  {stopwatch.ElapsedMilliseconds,6} ms  {message}") : null;
            var result = await new RecommendationPipeline(new DbBeatLeaderClient(context)).RunAsync(id, options, onProgress);
            stopwatch.Stop();
            times.Add(stopwatch.Elapsed.TotalMilliseconds);
            var digest = string.Join(",", result.Results.Select(s => $"{s.Song.LeaderboardId}{s.Song.Modifier}:{s.Score:0.0000}"));
            Console.WriteLine($"{id,-20} {result.PlayerScores.Count,5} plays {stopwatch.Elapsed.TotalMilliseconds,8:0} ms  digest {(uint)StableHash(digest):x8}  {result.Error}");
        }
        times.Sort();
        Console.WriteLine($"{ids.Count} runs in {total.Elapsed.TotalSeconds:0.0} s, median {times[times.Count / 2]:0} ms, max {times[^1]:0} ms");
    }

    // FNV-1a: string.GetHashCode differs between processes
    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in text) hash = (hash ^ c) * 16777619;
            return hash;
        }
    }
}
