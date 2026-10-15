using System.Text.Json;
using beatleader_analyzer;
using beatleader_parser;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using RatingAPI.Controllers;

namespace portaBLe.Refresh
{
    // Exports every swing of every ranked Standard leaderboard (with at least minScores scores), as the Analyzer computes
    // them, next to the accuracy of the leaderboard's scores. Used to fit the pass / tech formulas outside portaBLe.
    // Read only. Writes:
    // - wwwroot/rating-fit.json: one row per leaderboard (accuracy, ratings, where its swings are in the .bin)
    // - wwwroot/rating-fit.bin: little-endian float32, SwingFields values per swing, swings of a map in time order
    // RunScores exports every score too (see there).
    public static class RatingFitExport
    {
        public const string MapsFile = "rating-fit.json";
        public const string SwingsFile = "rating-fit.bin";

        public static readonly string[] SwingFields =
        {
            "Seconds", "Hand", "SwingFrequency", "HitDistance", "AngleStrain", "RepositioningDistance", "RotationAmount",
            "NjsBuff", "WallBuff", "SwingDiff", "Flags",
        };

        // Flags bits
        private const int ParityError = 1, Stream = 2, Linear = 4, FalsePositive = 8, Dot = 16, MultiNote = 32, BombAvoidance = 64;

        public static async Task Run(AppContext dbContext, string webRootPath, int minScores = 50)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var accuracies = (await dbContext.Scores.AsNoTracking()
                    .Select(s => new { s.LeaderboardId, s.Accuracy })
                    .ToListAsync())
                .Where(s => s.LeaderboardId != null && s.Accuracy > 0 && s.Accuracy <= 1)
                .GroupBy(s => s.LeaderboardId!)
                .ToDictionary(g => g.Key, g => g.Select(s => s.Accuracy).OrderByDescending(a => a).ToArray());
            var lbs = (await dbContext.Leaderboards.AsNoTracking().Where(lb => lb.ModeName == "Standard").ToListAsync())
                .Where(lb => accuracies.TryGetValue(lb.Id, out var a) && a.Length >= minScores)
                .ToList();
            Console.WriteLine($"Rating fit export: {lbs.Count} leaderboards");

            var results = new System.Collections.Concurrent.ConcurrentBag<(Leaderboard Lb, float[] Swings, int Count, double Pass, double Tech)>();
            var downloader = new Downloader("maps");
            int done = 0;
            await Parallel.ForEachAsync(lbs, new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount }, async (lb, ct) =>
            {
                try
                {
                    var mapPath = await downloader.MapAsync(lb.Hash);
                    if (mapPath == null) return;
                    var mapset = new Parse().TryLoadPath(mapPath);
                    int diffCode = RatingsRefresh.GetDiffCode(lb.DifficultyName);
                    var set = mapset?.Difficulties.FirstOrDefault(d => d.Characteristic == lb.ModeName
                        && (d.BeatMap._difficultyRank == diffCode || d.BeatMap._difficulty == lb.DifficultyName));
                    if (set == null) return;
                    var ratings = new Analyze().GetRating(set.Data, lb.ModeName, lb.DifficultyName, (float)mapset!.Info._beatsPerMinute);
                    if (ratings == null || ratings.SwingData.Count == 0) return;

                    var swings = ratings.SwingData.OrderBy(s => s.Cubes[0].Seconds).ThenBy(s => s.Cubes[0].Type).ToList();
                    int fields = SwingFields.Length;
                    var values = new float[swings.Count * fields];
                    for (int i = 0; i < swings.Count; i++)
                    {
                        var s = swings[i];
                        int flags = (s.ParityErrors ? ParityError : 0) | (s.IsStream ? Stream : 0) | (s.IsLinear ? Linear : 0)
                            | (s.FalsePositive ? FalsePositive : 0)
                            | (s.Cubes.Count == 1 && s.Cubes[0].CutDirection == 8 ? Dot : 0)
                            | (s.Cubes.Count > 1 ? MultiNote : 0)
                            | (s.BombAvoidance ? BombAvoidance : 0);
                        int o = i * fields;
                        values[o] = s.Cubes[0].Seconds;
                        values[o + 1] = s.Cubes[0].Type;
                        values[o + 2] = (float)s.SwingFrequency;
                        values[o + 3] = (float)s.HitDistance;
                        values[o + 4] = (float)s.AngleStrain;
                        values[o + 5] = (float)s.RepositioningDistance;
                        values[o + 6] = (float)s.RotationAmount;
                        values[o + 7] = (float)s.NjsBuff;
                        values[o + 8] = (float)s.WallBuff;
                        values[o + 9] = (float)s.SwingDiff;
                        values[o + 10] = flags;
                    }
                    results.Add((lb, values, swings.Count, ratings.PassRating, ratings.TechRating));
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Rating fit export: {lb.Id} skipped ({e.Message})");
                }
                int n = Interlocked.Increment(ref done);
                if (n % 250 == 0) Console.WriteLine($"Rating fit export: {n}/{lbs.Count} ({timer.Elapsed:mm\\:ss})");
            });

            var binPath = Path.Combine(webRootPath, SwingsFile);
            var rows = new List<object?[]>();
            long offset = 0;
            await using (var stream = File.Create(binPath))
            await using (var writer = new BinaryWriter(stream))
            {
                foreach (var (lb, values, count, pass, tech) in results.OrderBy(r => r.Lb.Id))
                {
                    var accs = accuracies[lb.Id];
                    int top = Math.Max(1, (int)Math.Ceiling(accs.Length * 0.1));
                    rows.Add(new object?[]
                    {
                        lb.Id, lb.Name, lb.DifficultyName, accs.Length,
                        R(accs.Take(top).Average(a => (double)a)), R(accs.Average(a => (double)a)),
                        R(lb.PredictedAcc), R(lb.AccRating), R(lb.Stars), R(pass), R(tech), lb.Duration, offset, count,
                    });
                    foreach (var v in values) writer.Write(v);
                    offset += count;
                }
            }

            var jsonPath = Path.Combine(webRootPath, MapsFile);
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(new
            {
                GeneratedAt = DateTime.UtcNow,
                SwingFields,
                Flags = new { ParityError, Stream, Linear, FalsePositive, Dot, MultiNote, BombAvoidance },
                Columns = new[]
                {
                    "Id", "Name", "Difficulty", "Scores", "Top10PercentAcc", "AverageAcc", "PredictedAcc", "AccRating", "Stars",
                    "PassRating", "TechRating", "Duration", "SwingOffset", "SwingCount",
                },
                Maps = rows,
            }));
            Console.WriteLine($"Rating fit export: {rows.Count} leaderboards, {offset} swings saved to {jsonPath} and {binPath} ({timer.Elapsed:mm\\:ss})");
        }

        public const string ScoresMapsFile = "rating-fit-scores.json";
        public const string ScoresFile = "rating-fit-scores.bin";

        // Every ranked score (player, leaderboard, accuracy, modifiers, FC), to estimate how hard each map is to acc while
        // accounting for who played it. Writes wwwroot/rating-fit-scores.json (id lists) and rating-fit-scores.bin:
        // per score 4 little-endian int32/float32 values: player index, leaderboard index, accuracy (float32), modifiers index
        // (+ 1 << 16 when FC).
        public static async Task RunScores(AppContext dbContext, string webRootPath)
        {
            var scores = await dbContext.Scores.AsNoTracking()
                .Select(s => new { s.PlayerId, s.LeaderboardId, s.Accuracy, s.Modifiers, s.FC })
                .ToListAsync();
            var players = new Dictionary<string, int>();
            var leaderboards = new Dictionary<string, int>();
            var modifiers = new Dictionary<string, int>();
            int Index(Dictionary<string, int> d, string? key)
            {
                key ??= "";
                if (!d.TryGetValue(key, out var i)) d[key] = i = d.Count;
                return i;
            }

            var path = Path.Combine(webRootPath, ScoresFile);
            int written = 0;
            await using (var stream = File.Create(path))
            await using (var writer = new BinaryWriter(stream))
            {
                foreach (var s in scores)
                {
                    if (s.PlayerId == null || s.LeaderboardId == null || !(s.Accuracy > 0 && s.Accuracy <= 1)) continue;
                    writer.Write(Index(players, s.PlayerId));
                    writer.Write(Index(leaderboards, s.LeaderboardId));
                    writer.Write(s.Accuracy);
                    writer.Write(Index(modifiers, s.Modifiers) | (s.FC ? 1 << 16 : 0));
                    written++;
                }
            }
            var jsonPath = Path.Combine(webRootPath, ScoresMapsFile);
            await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(new
            {
                GeneratedAt = DateTime.UtcNow,
                Players = players.OrderBy(p => p.Value).Select(p => p.Key).ToList(),
                Leaderboards = leaderboards.OrderBy(p => p.Value).Select(p => p.Key).ToList(),
                Modifiers = modifiers.OrderBy(p => p.Value).Select(p => p.Key).ToList(),
            }));
            Console.WriteLine($"Rating fit export: {written} scores, {players.Count} players saved to {path}");
        }

        public const string ModifiersFile = "rating-fit-modifiers.json";

        // Ratings of every leaderboard without modifiers and with SS, FS, SF (as stored by RatingsRefresh.Overwrite), to compare
        // what speed-modifier scores are worth with nomod scores on maps of similar ratings (together with RunScores). Read only.
        // Writes wwwroot/rating-fit-modifiers.json: Columns and one row per leaderboard.
        public static async Task RunModifiers(AppContext dbContext, string webRootPath)
        {
            var lbs = await dbContext.Leaderboards.AsNoTracking()
                .Select(l => new
                {
                    l.Id, l.ModeName, l.DifficultyName, l.Name, l.Stars, l.PassRating, l.TechRating, l.AccRating, l.PredictedAcc,
                    Mod = l.ModifiersRating == null ? null : new
                    {
                        l.ModifiersRating.SSStars, l.ModifiersRating.SSPassRating, l.ModifiersRating.SSTechRating, l.ModifiersRating.SSAccRating, l.ModifiersRating.SSPredictedAcc,
                        l.ModifiersRating.FSStars, l.ModifiersRating.FSPassRating, l.ModifiersRating.FSTechRating, l.ModifiersRating.FSAccRating, l.ModifiersRating.FSPredictedAcc,
                        l.ModifiersRating.SFStars, l.ModifiersRating.SFPassRating, l.ModifiersRating.SFTechRating, l.ModifiersRating.SFAccRating, l.ModifiersRating.SFPredictedAcc,
                    }
                })
                .ToListAsync();

            var rows = lbs.Select(l => new object?[]
            {
                l.Id, l.Name, l.DifficultyName, l.ModeName,
                l.Stars, l.PassRating, l.TechRating, l.AccRating, l.PredictedAcc,
                l.Mod?.SSStars, l.Mod?.SSPassRating, l.Mod?.SSTechRating, l.Mod?.SSAccRating, l.Mod?.SSPredictedAcc,
                l.Mod?.FSStars, l.Mod?.FSPassRating, l.Mod?.FSTechRating, l.Mod?.FSAccRating, l.Mod?.FSPredictedAcc,
                l.Mod?.SFStars, l.Mod?.SFPassRating, l.Mod?.SFTechRating, l.Mod?.SFAccRating, l.Mod?.SFPredictedAcc,
            }).ToList();

            var path = Path.Combine(webRootPath, ModifiersFile);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                GeneratedAt = DateTime.UtcNow,
                Columns = new[]
                {
                    "Id", "Name", "Difficulty", "Mode",
                    "Stars", "PassRating", "TechRating", "AccRating", "PredictedAcc",
                    "SSStars", "SSPassRating", "SSTechRating", "SSAccRating", "SSPredictedAcc",
                    "FSStars", "FSPassRating", "FSTechRating", "FSAccRating", "FSPredictedAcc",
                    "SFStars", "SFPassRating", "SFTechRating", "SFAccRating", "SFPredictedAcc",
                },
                Rows = rows,
            }));
            Console.WriteLine($"Rating fit export: modifier ratings of {rows.Count} leaderboards saved to {path}");
        }

        public const string WallsFile = "wall-fit.json";

        // Every wall of every ranked Standard leaderboard, and how the Analyzer classifies them (WallClassifier: dodge and
        // crouch actions, with merged durations), to check wall handling against replays (head position) and attempts (where
        // players fail). Read only. Writes wwwroot/wall-fit.json: per leaderboard, Walls [seconds, duration, x, y, width,
        // height], Dodges and Crouches [seconds, duration, x, y, width, height] as the Analyzer returns them.
        public static async Task RunWalls(AppContext dbContext, string webRootPath)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var lbs = await dbContext.Leaderboards.AsNoTracking().Where(lb => lb.ModeName == "Standard").ToListAsync();
            var results = new System.Collections.Concurrent.ConcurrentBag<object>();
            var downloader = new Downloader("maps");
            int done = 0;
            await Parallel.ForEachAsync(lbs, new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount }, async (lb, ct) =>
            {
                try
                {
                    var mapPath = await downloader.MapAsync(lb.Hash);
                    if (mapPath == null) return;
                    var mapset = new Parse().TryLoadPath(mapPath);
                    int diffCode = RatingsRefresh.GetDiffCode(lb.DifficultyName);
                    var set = mapset?.Difficulties.FirstOrDefault(d => d.Characteristic == lb.ModeName
                        && (d.BeatMap._difficultyRank == diffCode || d.BeatMap._difficulty == lb.DifficultyName));
                    if (set == null) return;
                    var walls = set.Data.Walls.OrderBy(w => w.Seconds).ToList();
                    var ratings = new Analyze().GetRating(set.Data, lb.ModeName, lb.DifficultyName, (float)mapset!.Info._beatsPerMinute);
                    if (ratings == null) return;
                    static object[] Row(Parser.Map.Difficulty.V3.Grid.Wall w) =>
                        new object[] { R(w.Seconds), R(w.DurationInSeconds), w.x, w.y, w.Width, w.Height };
                    results.Add(new
                    {
                        lb.Id,
                        lb.Name,
                        lb.DifficultyName,
                        lb.Duration,
                        Pass = R(ratings.PassRating),
                        Walls = walls.Select(Row).ToList(),
                        Dodges = ratings.DodgeWalls.Select(Row).ToList(),
                        Crouches = ratings.CrouchWalls.Select(Row).ToList(),
                    });
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Wall export: {lb.Id} skipped ({e.Message})");
                }
                int n = Interlocked.Increment(ref done);
                if (n % 500 == 0) Console.WriteLine($"Wall export: {n}/{lbs.Count} ({timer.Elapsed:mm\\:ss})");
            });
            var path = Path.Combine(webRootPath, WallsFile);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { GeneratedAt = DateTime.UtcNow, Maps = results.ToList() }));
            Console.WriteLine($"Wall export: {results.Count} leaderboards saved to {path} ({timer.Elapsed:mm\\:ss})");
        }

        private static double R(double value) => double.IsFinite(value) ? Math.Round(value, 5) : 0;
    }
}
