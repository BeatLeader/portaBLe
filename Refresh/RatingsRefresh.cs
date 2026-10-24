using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using RatingAPI.Controllers;

namespace portaBLe.Refresh
{
    public class RatingsRefresh
    {
        public static int GetDiffCode(string difficulty)
        {
            switch (difficulty)
            {
                case "Easy": return 1;
                case "Normal": return 3;
                case "Hard": return 5;
                case "Expert": return 7;
                case "ExpertPlus": return 9;
                default: return 9;
            }
        }

        // The modifiers Overwrite stores (RatingsController.Get also rates BFS / BSF, which aren't kept here)
        private static readonly string[] StoredModifiers = { "none", "SS", "FS", "SFS" };

        // applyAccReweight: keep each map's acc reweighter multiplier (Leaderboard.AccReweight) on its acc ratings. false: raw
        // acc ratings, and the multipliers are cleared (set to 0 = none), so later steps don't apply them either.
        public static async Task Overwrite(AppContext dbContext, bool applyAccReweight = true)
        {
            // Columns older databases may not have (checked first, so no failed ALTER TABLE in the log)
            foreach (var (column, type) in new[] {
                ("LinearPercent", "REAL"),
                ("MultiRating", "REAL"),
                ("ParityErrors", "INTEGER"),
                ("BombAvoidances", "INTEGER"),
                ("DodgeWalls", "INTEGER"),
                ("CrouchWalls", "INTEGER"),
                ("Duration", "REAL"),
                ("Njs", "REAL"),
                ("NoteCount", "INTEGER"),
                ("NoteSpan", "REAL"),
                ("HitDistance", "REAL"),
                ("HardSwingRatio", "REAL"),
                ("AccReweight", "REAL"),
                ("IsFitbeat", "INTEGER"),
                ("IsLinear", "INTEGER"),
                ("IsBombReset", "INTEGER"),
                ("IsInline", "INTEGER"),
                ("MapType", "TEXT"),
                ("Inlines", "INTEGER"),
                ("Curve", "TEXT"),
            })
            {
                await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", column, type);
            }
            foreach (var column in new[] { "SSCurve", "FSCurve", "SFCurve" })
            {
                await DbSchema.AddColumnIfMissing(dbContext, "ModifiersRating", column, "TEXT");
            }

            var configDictionary = new Dictionary<string, string>
            {
                { "MapsPath", "maps" },
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configDictionary)
                .Build();

            var lbs = dbContext.Leaderboards.Include(lb => lb.ModifiersRating).ToList();
            Console.WriteLine($"Recalculating from RatingAPI for {lbs.Count} leaderboards");
            
            int processedCount = 0;
            int totalCount = lbs.Count;
            int errorCount = 0;
            var lockObj = new object();
            var startTime = DateTime.Now;

            // Use CPU core count for CPU-bound inference operations
            var parallelOptions = new ParallelOptions 
            { 
                MaxDegreeOfParallelism = Program.CoreCount
            };

            await Parallel.ForEachAsync(lbs, parallelOptions, async (lb, ct) =>
            {
                try
                {
                    // Create per-thread instances to avoid sharing state
                    using var loggerFactory = LoggerFactory.Create(builder => { });
                    var logger = loggerFactory.CreateLogger<RatingsController>();
                    var controller = new RatingsController(configuration, logger);
                    
                    var result = controller.Get(lb.Hash, lb.ModeName, GetDiffCode(lb.DifficultyName), StoredModifiers);
                    var response = result.Value;
                    if (response == null || !response.ContainsKey("none"))
                    {
                        throw new InvalidOperationException($"No rating data returned for {lb.Hash} ({lb.ModeName}/{lb.DifficultyName})");
                    }
                    lb.DodgeWalls = (int)response["none"].LackMapCalculation.Statistics.DodgeWalls;
                    lb.CrouchWalls = (int)response["none"].LackMapCalculation.Statistics.CrouchWalls;
                    lb.LinearPercent = (float)response["none"].LackMapCalculation.LinearPercentage;
                    lb.MultiPercentage = (float)response["none"].LackMapCalculation.MultiPercentage;
                    lb.ParityErrors = (float)response["none"].LackMapCalculation.Statistics.ParityErrors;
                    lb.BombAvoidances = (float)response["none"].LackMapCalculation.Statistics.BombAvoidances;
                    lb.Inlines = response["none"].LackMapCalculation.Statistics.Inlines;
                    lb.Duration = (float)response["none"].Length;
                    lb.Njs = response["none"].Njs;
                    lb.NoteCount = response["none"].LackMapCalculation.NoteCount;
                    lb.NoteSpan = (float)response["none"].LackMapCalculation.NoteSpan;
                    lb.HitDistance = (float)response["none"].LackMapCalculation.HitDistance;
                    lb.HardSwingRatio = (float)response["none"].LackMapCalculation.HardSwingRatio;

                    lb.PassRating = (float)response["none"].LackMapCalculation.PassRating;
                    lb.TechRating = (float)response["none"].LackMapCalculation.TechRating;
                    lb.PredictedAcc = (float)response["none"].PredictedAcc;
                    // The acc reweighter's multiplier (from the last AccReweighter run), unless raw acc ratings are asked for
                    if (!applyAccReweight) lb.AccReweight = 0;
                    double reweight = ReplayUtils.AccReweightOf(lb);
                    lb.AccRating = ReplayUtils.Reweighted((float)response["none"].AccRating, reweight);
                    lb.Curve = ReplayUtils.ToCurve(response["none"].PointList);

                    lb.ModifiersRating.SSPassRating = (float)response["SS"].LackMapCalculation.PassRating;
                    lb.ModifiersRating.SSTechRating = (float)response["SS"].LackMapCalculation.TechRating;
                    lb.ModifiersRating.SSPredictedAcc = (float)response["SS"].PredictedAcc;
                    lb.ModifiersRating.SSAccRating = ReplayUtils.Reweighted((float)response["SS"].AccRating, reweight);
                    lb.ModifiersRating.SSCurve = ReplayUtils.ToCurve(response["SS"].PointList);

                    lb.ModifiersRating.FSPassRating = (float)response["FS"].LackMapCalculation.PassRating;
                    lb.ModifiersRating.FSTechRating = (float)response["FS"].LackMapCalculation.TechRating;
                    lb.ModifiersRating.FSPredictedAcc = (float)response["FS"].PredictedAcc;
                    lb.ModifiersRating.FSAccRating = ReplayUtils.Reweighted((float)response["FS"].AccRating, reweight);
                    lb.ModifiersRating.FSCurve = ReplayUtils.ToCurve(response["FS"].PointList);

                    lb.ModifiersRating.SFPassRating = (float)response["SFS"].LackMapCalculation.PassRating;
                    lb.ModifiersRating.SFTechRating = (float)response["SFS"].LackMapCalculation.TechRating;
                    lb.ModifiersRating.SFPredictedAcc = (float)response["SFS"].PredictedAcc;
                    lb.ModifiersRating.SFAccRating = ReplayUtils.Reweighted((float)response["SFS"].AccRating, reweight);
                    lb.ModifiersRating.SFCurve = ReplayUtils.ToCurve(response["SFS"].PointList);

                    // Stars: what a 96% score is worth, with each one's curve
                    ReplayUtils.SetStars(lb);

                    lock (lockObj)
                    {
                        processedCount++;
                        if (processedCount % 100 == 0 || processedCount == totalCount)
                        {
                            var elapsed = (DateTime.Now - startTime).TotalSeconds;
                            var rate = processedCount / elapsed;
                            var remaining = (totalCount - processedCount) / rate;
                            Console.WriteLine($"Progress: {processedCount}/{totalCount} ({processedCount * 100 / totalCount}%) - Rate: {rate:F1}/s - ETA: {TimeSpan.FromSeconds(remaining):hh\\:mm\\:ss} - Errors: {errorCount} - Time: {(Program.Stopwatch.ElapsedMilliseconds / 1000)}s");
                        }
                    }
                }
                catch (Exception e)
                {
                    lock (lockObj)
                    {
                        errorCount++;
                        if (errorCount <= 10 || errorCount % 100 == 0)
                        {
                            Console.WriteLine($"Error processing {lb.Hash}: {e.Message}");
                        }
                    }
                }
            });

            Console.WriteLine($"\nCompleted: {processedCount - errorCount} successful, {errorCount} errors");
            dbContext.BulkSaveChanges();
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }

        // The Analyzer's counts the tags use (Leaderboard.Inlines, ParityErrors, BombAvoidances, DodgeWalls, CrouchWalls,
        // LinearPercent) from the maps folder, without rating the maps again (Overwrite stores them too). Maps missing from the
        // folder are left as they are. Then LeaderboardsRefresh.DetermineTags.
        public static async Task RefreshMapStatistics(AppContext dbContext, string mapsDirectory = "maps")
        {
            // Before any bulk operation (see LeaderboardsRefresh.EnsureTagColumns)
            await LeaderboardsRefresh.EnsureTagColumns(dbContext);
            var lbs = await dbContext.Leaderboards.AsNoTracking()
                .Select(l => new Leaderboard { Id = l.Id, Hash = l.Hash, ModeName = l.ModeName, DifficultyName = l.DifficultyName })
                .ToListAsync();
            var updates = new System.Collections.Concurrent.ConcurrentBag<Leaderboard>();
            Parallel.ForEach(lbs, new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount }, lb =>
            {
                try
                {
                    var path = new[] { lb.Hash.ToUpperInvariant(), lb.Hash.ToLowerInvariant(), lb.Hash }
                        .Select(h => Path.Combine(mapsDirectory, h)).FirstOrDefault(Directory.Exists);
                    if (path == null) return;
                    var mapset = new beatleader_parser.Parse().TryLoadPath(path);
                    int diffCode = GetDiffCode(lb.DifficultyName);
                    var set = mapset?.Difficulties.FirstOrDefault(d => d.Characteristic == lb.ModeName
                        && (d.BeatMap._difficultyRank == diffCode || d.BeatMap._difficulty == lb.DifficultyName));
                    if (set == null) return;
                    var ratings = new beatleader_analyzer.Analyze().GetRating(set.Data, lb.ModeName, lb.DifficultyName, (float)mapset!.Info._beatsPerMinute);
                    if (ratings == null) return;
                    var stats = ratings.Statistics;
                    updates.Add(new Leaderboard
                    {
                        Id = lb.Id, Inlines = stats.Inlines, ParityErrors = stats.ParityErrors, BombAvoidances = stats.BombAvoidances,
                        DodgeWalls = stats.DodgeWalls, CrouchWalls = stats.CrouchWalls, LinearPercent = (float)ratings.LinearPercentage,
                    });
                }
                catch { }
            });
            await dbContext.BulkUpdateAsync(updates.ToList(), options => options.ColumnInputExpression = c =>
                new { c.Inlines, c.ParityErrors, c.BombAvoidances, c.DodgeWalls, c.CrouchWalls, c.LinearPercent });
            Console.WriteLine($"Map statistics: {updates.Count} of {lbs.Count} leaderboards updated from {mapsDirectory}");
            await LeaderboardsRefresh.DetermineTags(dbContext);
        }

        // Base NJS of every leaderboard from Info.dat in the maps folder, without rating the maps again (Overwrite stores it
        // too). Maps missing from the folder are left as they are (they aren't downloaded).
        public static async Task RefreshNjs(AppContext dbContext, string mapsDirectory = "maps")
        {
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "Njs", "REAL");
            var lbs = await dbContext.Leaderboards.AsNoTracking()
                .Select(l => new Leaderboard { Id = l.Id, Hash = l.Hash, ModeName = l.ModeName, DifficultyName = l.DifficultyName, Njs = l.Njs })
                .ToListAsync();
            var updates = new List<Leaderboard>();
            int missing = 0;
            foreach (var lb in lbs)
            {
                var njs = ReadNjs(mapsDirectory, lb.Hash, lb.ModeName, lb.DifficultyName);
                if (njs == null) { missing++; continue; }
                if (njs.Value != lb.Njs) updates.Add(new Leaderboard { Id = lb.Id, Njs = njs.Value });
            }
            if (updates.Count > 0)
            {
                await dbContext.BulkUpdateAsync(updates, options => options.ColumnInputExpression = c => new { c.Njs });
            }
            Console.WriteLine($"NJS: {updates.Count} leaderboards updated, {missing} not found in {mapsDirectory}");
        }

        // A difficulty's NJS from Info.dat: v2 (_difficultyBeatmapSets) or v4 (difficultyBeatmaps). Custom modes
        // (InvertedStandard...) fall back to Standard, as RatingAPI rates them. Null when the map or difficulty isn't there.
        public static float? ReadNjs(string mapsDirectory, string? hash, string? mode, string? difficulty)
        {
            if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(difficulty)) return null;
            mode = string.IsNullOrEmpty(mode) ? "Standard" : mode;
            try
            {
                var dir = new[] { hash.ToLowerInvariant(), hash.ToUpperInvariant(), hash }
                    .Select(h => Path.Combine(mapsDirectory, h))
                    .FirstOrDefault(Directory.Exists);
                if (dir == null) return null;
                var file = new[] { "Info.dat", "info.dat" }.Select(f => Path.Combine(dir, f)).FirstOrDefault(File.Exists);
                if (file == null) return null;

                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var found = new List<(string Mode, string Difficulty, float Njs)>();
                if (root.TryGetProperty("_difficultyBeatmapSets", out var sets) && sets.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var set in sets.EnumerateArray())
                    {
                        var setMode = set.TryGetProperty("_beatmapCharacteristicName", out var m) ? m.GetString() ?? "" : "";
                        if (!set.TryGetProperty("_difficultyBeatmaps", out var diffs) || diffs.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                        foreach (var diff in diffs.EnumerateArray())
                        {
                            if (diff.TryGetProperty("_difficulty", out var d) && diff.TryGetProperty("_noteJumpMovementSpeed", out var n)
                                && n.TryGetSingle(out var value))
                            {
                                found.Add((setMode, d.GetString() ?? "", value));
                            }
                        }
                    }
                }
                if (root.TryGetProperty("difficultyBeatmaps", out var v4) && v4.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var diff in v4.EnumerateArray())
                    {
                        var diffMode = diff.TryGetProperty("characteristic", out var m) ? m.GetString() ?? "Standard" : "Standard";
                        if (diff.TryGetProperty("difficulty", out var d) && diff.TryGetProperty("noteJumpMovementSpeed", out var n)
                            && n.TryGetSingle(out var value))
                        {
                            found.Add((diffMode, d.GetString() ?? "", value));
                        }
                    }
                }

                float? Find(string wanted) => found
                    .Where(f => string.Equals(f.Mode, wanted, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(f.Difficulty, difficulty, StringComparison.OrdinalIgnoreCase))
                    .Select(f => (float?)f.Njs)
                    .FirstOrDefault();
                return Find(mode) ?? Find("Standard");
            }
            catch
            {
                return null;
            }
        }

        // applyAccReweight: as in Overwrite
        public static async Task Refresh(AppContext dbContext, bool applyAccReweight = true)
        {
            Console.WriteLine("Recalculating Acc Rating from Predicted Accuracy");
            var lbs = dbContext.Leaderboards.Include(lb => lb.ModifiersRating).ToList();
            foreach (var lb in lbs)
            {
                try
                {
                    var mod = lb.ModifiersRating;

                    if (!applyAccReweight) lb.AccReweight = 0;
                    double reweight = ReplayUtils.AccReweightOf(lb);
                    lb.AccRating = ReplayUtils.Reweighted(ReplayUtils.AccRating(lb.PredictedAcc, lb.PassRating, lb.TechRating, lb.HitDistance, lb.HardSwingRatio), reweight);

                    lb.ModifiersRating.SSAccRating = ReplayUtils.Reweighted(ReplayUtils.AccRating(mod.SSPredictedAcc, mod.SSPassRating, mod.SSTechRating, lb.HitDistance, lb.HardSwingRatio), reweight);
                    lb.ModifiersRating.FSAccRating = ReplayUtils.Reweighted(ReplayUtils.AccRating(mod.FSPredictedAcc, mod.FSPassRating, mod.FSTechRating, lb.HitDistance, lb.HardSwingRatio), reweight);
                    lb.ModifiersRating.SFAccRating = ReplayUtils.Reweighted(ReplayUtils.AccRating(mod.SFPredictedAcc, mod.SFPassRating, mod.SFTechRating, lb.HitDistance, lb.HardSwingRatio), reweight);
                    // The low note speed blend (pass and tech are stored blended by Overwrite)
                    var blend = global::RatingAPI.Controllers.CurveSettings.Current;
                    mod.SSAccRating = (float)blend.LowNoteSpeedBlend(lb.AccRating, mod.SSAccRating, lb.NoteCount);
                    mod.FSAccRating = (float)blend.LowNoteSpeedBlend(lb.AccRating, mod.FSAccRating, lb.NoteCount);
                    mod.SFAccRating = (float)blend.LowNoteSpeedBlend(lb.AccRating, mod.SFAccRating, lb.NoteCount);

                    // Stars: what a 96% score is worth, with each one's curve
                    ReplayUtils.SetStars(lb);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"{e.Message}");
                }
            }

            dbContext.BulkSaveChanges();
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }
    }
}