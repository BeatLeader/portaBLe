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

        public static async Task Overwrite(AppContext dbContext)
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
                ("IsFitbeat", "INTEGER"),
                ("IsLinear", "INTEGER"),
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
                    lb.Duration = (float)response["none"].Length;
                    lb.Njs = response["none"].Njs;

                    lb.PassRating = (float)response["none"].LackMapCalculation.PassRating;
                    lb.TechRating = (float)response["none"].LackMapCalculation.TechRating;
                    lb.PredictedAcc = (float)response["none"].PredictedAcc;
                    lb.AccRating = (float)response["none"].AccRating;
                    lb.Curve = ReplayUtils.ToCurve(response["none"].PointList);
                    lb.Stars = ReplayUtils.ToStars(lb.AccRating, lb.PassRating, lb.TechRating);

                    lb.ModifiersRating.SSPassRating = (float)response["SS"].LackMapCalculation.PassRating;
                    lb.ModifiersRating.SSTechRating = (float)response["SS"].LackMapCalculation.TechRating;
                    lb.ModifiersRating.SSPredictedAcc = (float)response["SS"].PredictedAcc;
                    lb.ModifiersRating.SSAccRating = (float)response["SS"].AccRating;
                    lb.ModifiersRating.SSCurve = ReplayUtils.ToCurve(response["SS"].PointList);

                    lb.ModifiersRating.FSPassRating = (float)response["FS"].LackMapCalculation.PassRating;
                    lb.ModifiersRating.FSTechRating = (float)response["FS"].LackMapCalculation.TechRating;
                    lb.ModifiersRating.FSPredictedAcc = (float)response["FS"].PredictedAcc;
                    lb.ModifiersRating.FSAccRating = (float)response["FS"].AccRating;
                    lb.ModifiersRating.FSCurve = ReplayUtils.ToCurve(response["FS"].PointList);

                    lb.ModifiersRating.SFPassRating = (float)response["SFS"].LackMapCalculation.PassRating;
                    lb.ModifiersRating.SFTechRating = (float)response["SFS"].LackMapCalculation.TechRating;
                    lb.ModifiersRating.SFPredictedAcc = (float)response["SFS"].PredictedAcc;
                    lb.ModifiersRating.SFAccRating = (float)response["SFS"].AccRating;
                    lb.ModifiersRating.SFCurve = ReplayUtils.ToCurve(response["SFS"].PointList);

                    lb.ModifiersRating.SFStars = ReplayUtils.ToStars(lb.ModifiersRating.SFAccRating, lb.ModifiersRating.SFPassRating, lb.ModifiersRating.SFTechRating);
                    lb.ModifiersRating.FSStars = ReplayUtils.ToStars(lb.ModifiersRating.FSAccRating, lb.ModifiersRating.FSPassRating, lb.ModifiersRating.FSTechRating);
                    lb.ModifiersRating.SSStars = ReplayUtils.ToStars(lb.ModifiersRating.SSAccRating, lb.ModifiersRating.SSPassRating, lb.ModifiersRating.SSTechRating);

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

        public static async Task Refresh(AppContext dbContext)
        {
            Console.WriteLine("Recalculating Acc Rating from Predicted Accuracy");
            var lbs = dbContext.Leaderboards.Include(lb => lb.ModifiersRating).ToList();
            foreach (var lb in lbs)
            {
                try
                {
                    var mod = lb.ModifiersRating;

                    lb.AccRating = ReplayUtils.AccRating(lb.PredictedAcc, lb.PassRating, lb.TechRating);
                    lb.Stars = ReplayUtils.ToStars(lb.AccRating, lb.PassRating, lb.TechRating);

                    lb.ModifiersRating.SSAccRating = ReplayUtils.AccRating(mod.SSPredictedAcc, mod.SSPassRating, mod.SSTechRating);
                    lb.ModifiersRating.FSAccRating = ReplayUtils.AccRating(mod.FSPredictedAcc, mod.FSPassRating, mod.FSTechRating);
                    lb.ModifiersRating.SFAccRating = ReplayUtils.AccRating(mod.SFPredictedAcc, mod.SFPassRating, mod.SFTechRating);

                    lb.ModifiersRating.SFStars = ReplayUtils.ToStars(lb.ModifiersRating.SFAccRating, lb.ModifiersRating.SFPassRating, lb.ModifiersRating.SFTechRating);
                    lb.ModifiersRating.FSStars = ReplayUtils.ToStars(lb.ModifiersRating.FSAccRating, lb.ModifiersRating.FSPassRating, lb.ModifiersRating.FSTechRating);
                    lb.ModifiersRating.SSStars = ReplayUtils.ToStars(lb.ModifiersRating.SSAccRating, lb.ModifiersRating.SSPassRating, lb.ModifiersRating.SSTechRating);
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