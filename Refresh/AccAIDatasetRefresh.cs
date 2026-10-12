using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using portaBLe.DB;
using RatingAPI.Controllers;

namespace portaBLe.Refresh
{
    // Builds the data shown on the Acc AI Dataset page from the beatsaber-replays-ai-2 replay folders,
    // the BeatLeader, BeatSaver and AccSaber APIs, and RatingAPI. Only runs when called from Program.Main;
    // the page itself only reads the saved wwwroot/accai-dataset.json.
    public static class AccAIDatasetRefresh
    {
        public const string FileName = "accai-dataset.json";

        // portaBLe and beatsaber-replays-ai-2 sit next to each other in the repos folder.
        public static string DefaultReplaysPath => Path.GetFullPath(Path.Combine("..", "beatsaber-replays-ai-2", "replays"));

        private static readonly HttpClient http = new(new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 16,
            AutomaticDecompression = DecompressionMethods.All,
        })
        { Timeout = TimeSpan.FromSeconds(60) };

        private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = false };

        // BeatLeader allows 50 API requests per 10 seconds; 250 ms between calls keeps us at 4 per second.
        private static readonly SemaphoreSlim beatLeaderGate = new(1, 1);
        private static readonly TimeSpan beatLeaderInterval = TimeSpan.FromMilliseconds(250);
        private static DateTime nextBeatLeaderCall = DateTime.MinValue;

        private static readonly Regex beatSaverKeyPattern = new("^[0-9a-f]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static AccAIDatasetRefresh()
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("portaBLe/1.0");
        }

        // ------------------------------------------------------------------
        // Replay folders
        // ------------------------------------------------------------------

        // Every leaderboard folder in the replays folder, with its number of valid replay files and the accuracy
        // read from each file name ("playerid-accuracy-score-njs.npy").
        public static List<ReplayFolderInfo> ReadReplayFolders(string replaysPath)
        {
            var result = new List<ReplayFolderInfo>();
            foreach (var folder in Directory.EnumerateDirectories(replaysPath).OrderBy(f => f, StringComparer.Ordinal))
            {
                var info = new ReplayFolderInfo { LeaderboardId = Path.GetFileName(folder) };
                var njsValues = new List<float>();

                foreach (var file in Directory.EnumerateFiles(folder, "*.npy"))
                {
                    var parts = Path.GetFileNameWithoutExtension(file).Split('-');
                    if (parts.Length != 4
                        || !TryParseFloat(parts[1], out float accuracy)
                        || !TryParseFloat(parts[3], out float njs))
                    {
                        info.UnreadableFiles++;
                        continue;
                    }
                    info.Accuracies.Add(accuracy);
                    njsValues.Add(njs);
                }

                if (info.Accuracies.Count == 0) continue;

                info.Accuracies.Sort((a, b) => b.CompareTo(a));
                info.ReplayCount = info.Accuracies.Count;
                info.FileNjs = njsValues.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
                result.Add(info);
            }
            return result;
        }

        private static bool TryParseFloat(string text, out float value) =>
            float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        // "15ef1xxx91" -> "15ef1": drop the difficulty and characteristic digits, then the reupload x's.
        public static string BeatSaverKey(string leaderboardId) =>
            leaderboardId.Length > 2 ? leaderboardId[..^2].TrimEnd('x') : leaderboardId;

        // ------------------------------------------------------------------
        // Refresh
        // ------------------------------------------------------------------

        public static async Task Refresh(string webRootPath, string? replaysPath = null, bool computeRatings = true)
        {
            replaysPath ??= DefaultReplaysPath;
            Console.WriteLine($"Acc AI dataset: reading {replaysPath}");
            if (!Directory.Exists(replaysPath))
            {
                Console.WriteLine($"Acc AI dataset: folder not found, nothing done.");
                return;
            }

            var folders = ReadReplayFolders(replaysPath);
            Console.WriteLine($"Acc AI dataset: {folders.Count} leaderboards, {folders.Sum(f => f.ReplayCount)} replay files");

            var leaderboards = folders.Select(FromFolder).ToList();

            var accSaber = await FetchAccSaberRanked();
            ApplyAccSaber(leaderboards, accSaber);

            await FetchBeatLeader(leaderboards);
            await FetchBeatSaver(leaderboards);
            if (computeRatings)
            {
                ComputeRatings(leaderboards);
            }

            await Save(webRootPath, new AccAIDataset
            {
                GeneratedAt = DateTime.UtcNow,
                ReplaysPath = replaysPath,
                AccSaberRankedListSize = accSaber.Count,
                Leaderboards = leaderboards,
            });
        }

        // Updates the saved dataset from the replay folders without fetching everything again:
        // - leaderboards whose folder is gone from the replays folder are removed,
        // - the replay counts and accuracies of the others are re-read from their folders,
        // - new folders are fetched from BeatLeader, BeatSaver and AccSaber (only those) when fetchNew is true,
        // - recomputeRatings: true re-rates every leaderboard with the current RatingAPI model (local, no API calls
        //   except map downloads that aren't in the "maps" folder yet). New leaderboards are always rated.
        // Falls back to a full Refresh when nothing is saved yet.
        public static async Task RefreshFromCache(string webRootPath, string? replaysPath = null, bool fetchNew = true, bool recomputeRatings = false)
        {
            var dataset = Load(webRootPath);
            if (dataset == null)
            {
                Console.WriteLine("Acc AI dataset: nothing saved yet, running a full refresh.");
                await Refresh(webRootPath, replaysPath);
                return;
            }

            replaysPath ??= string.IsNullOrEmpty(dataset.ReplaysPath) ? DefaultReplaysPath : dataset.ReplaysPath;
            Console.WriteLine($"Acc AI dataset: updating from {replaysPath}");
            if (!Directory.Exists(replaysPath))
            {
                Console.WriteLine($"Acc AI dataset: folder not found, nothing done.");
                return;
            }

            var folders = ReadReplayFolders(replaysPath).ToDictionary(f => f.LeaderboardId, StringComparer.OrdinalIgnoreCase);
            var kept = new List<AccAIDatasetLeaderboard>();
            int removed = 0;
            foreach (var lb in dataset.Leaderboards)
            {
                if (folders.Remove(lb.Id, out var folder))
                {
                    ApplyFolder(lb, folder);
                    kept.Add(lb);
                }
                else
                {
                    removed++;
                }
            }
            var added = folders.Values.OrderBy(f => f.LeaderboardId, StringComparer.Ordinal).Select(FromFolder).ToList();
            Console.WriteLine($"Acc AI dataset: {kept.Count} kept, {removed} removed (folder gone), {added.Count} new folders");

            // OST maps saved by older versions were looked up on BeatSaver; they aren't on it.
            foreach (var lb in kept.Where(l => l.IsOst)) ClearBeatSaver(lb);

            if (added.Count > 0)
            {
                if (fetchNew)
                {
                    var accSaber = await FetchAccSaberRanked();
                    if (accSaber.Count > 0) dataset.AccSaberRankedListSize = accSaber.Count;
                    ApplyAccSaber(added, accSaber);
                    await FetchBeatLeader(added);
                    await FetchBeatSaver(added);
                    if (!recomputeRatings) ComputeRatings(added);
                }
                else
                {
                    Console.WriteLine("Acc AI dataset: fetchNew is false, the new folders are saved with their replay data only.");
                }
            }

            var leaderboards = kept.Concat(added).OrderBy(l => l.Id, StringComparer.Ordinal).ToList();
            if (recomputeRatings) ComputeRatings(leaderboards);

            dataset.GeneratedAt = DateTime.UtcNow;
            dataset.ReplaysPath = replaysPath;
            dataset.Leaderboards = leaderboards;
            await Save(webRootPath, dataset);
        }

        private static AccAIDatasetLeaderboard FromFolder(ReplayFolderInfo folder)
        {
            var lb = new AccAIDatasetLeaderboard
            {
                Id = folder.LeaderboardId,
                BeatSaverKey = BeatSaverKey(folder.LeaderboardId),
            };
            ApplyFolder(lb, folder);
            return lb;
        }

        private static void ApplyFolder(AccAIDatasetLeaderboard lb, ReplayFolderInfo folder)
        {
            lb.ReplayCount = folder.ReplayCount;
            lb.Accuracies = folder.Accuracies;
            lb.TopAccuracy = folder.Accuracies[0];
            lb.AverageAccuracy = folder.Accuracies.Average();
            lb.LowestAccuracy = folder.Accuracies[^1];
            lb.FileNjs = folder.FileNjs;
            lb.UnreadableFiles = folder.UnreadableFiles;
        }

        private static void ApplyAccSaber(List<AccAIDatasetLeaderboard> leaderboards, Dictionary<string, (string Category, double? Complexity)> accSaber)
        {
            foreach (var lb in leaderboards)
            {
                if (accSaber.TryGetValue(lb.Id, out var entry))
                {
                    lb.AccSaberRanked = true;
                    lb.AccSaberCategory = entry.Category;
                    lb.AccSaberComplexity = entry.Complexity;
                }
            }
        }

        private static void ClearBeatSaver(AccAIDatasetLeaderboard lb)
        {
            lb.FoundOnBeatSaver = false;
            lb.ScoreSaberRanked = false;
            lb.ScoreSaberStars = null;
            lb.BeatSaverBeatLeaderRanked = false;
        }

        private static async Task Save(string webRootPath, AccAIDataset dataset)
        {
            var path = Path.Combine(webRootPath, FileName);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dataset, jsonOptions));
            Console.WriteLine($"Acc AI dataset saved to {path}: {dataset.Leaderboards.Count} leaderboards ({(Program.Stopwatch.ElapsedMilliseconds / 1000)} seconds)");
        }

        public static AccAIDataset? Load(string webRootPath)
        {
            var path = Path.Combine(webRootPath, FileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<AccAIDataset>(File.ReadAllText(path), jsonOptions);
        }

        // ------------------------------------------------------------------
        // AccSaber
        // ------------------------------------------------------------------

        // AccSaber's list of every ranked difficulty, keyed by BeatLeader leaderboard id.
        private static async Task<Dictionary<string, (string Category, double? Complexity)>> FetchAccSaberRanked()
        {
            var result = new Dictionary<string, (string, double?)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var json = await http.GetStringAsync("https://api.accsaber.com/v1/maps/difficulties/all");
                foreach (var item in JsonNode.Parse(json)?.AsArray() ?? new JsonArray())
                {
                    var id = item?["blLeaderboardId"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(id)) continue;
                    result[id] = (item?["categoryCode"]?.GetValue<string>() ?? "", GetDouble(item?["complexity"]));
                }
                Console.WriteLine($"Acc AI dataset: {result.Count} AccSaber ranked difficulties");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Acc AI dataset: could not load the AccSaber ranked list ({e.Message}); AccSaber counts will be 0.");
            }
            return result;
        }

        // ------------------------------------------------------------------
        // BeatLeader
        // ------------------------------------------------------------------

        private static async Task FetchBeatLeader(List<AccAIDatasetLeaderboard> leaderboards)
        {
            int done = 0, missing = 0;
            await Parallel.ForEachAsync(leaderboards, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (lb, ct) =>
            {
                var url = $"https://api.beatleader.xyz/leaderboard/scores/{Uri.EscapeDataString(lb.Id)}?leaderboardContext=nomods&page=1&count=1&sortBy=rank&order=desc";
                var root = await GetBeatLeaderJson(url);
                if (root == null)
                {
                    Interlocked.Increment(ref missing);
                }
                else
                {
                    var song = root["song"];
                    var diff = root["difficulty"];
                    lb.FoundOnBeatLeader = true;
                    lb.Hash = song?["hash"]?.GetValue<string>() ?? "";
                    lb.Name = song?["name"]?.GetValue<string>() ?? "";
                    lb.SubName = song?["subName"]?.GetValue<string>() ?? "";
                    lb.Author = song?["author"]?.GetValue<string>() ?? "";
                    lb.Mapper = song?["mapper"]?.GetValue<string>() ?? "";
                    lb.Difficulty = diff?["difficultyName"]?.GetValue<string>() ?? "";
                    lb.Characteristic = diff?["modeName"]?.GetValue<string>() ?? "";
                    lb.Njs = (float)(GetDouble(diff?["njs"]) ?? 0);
                    lb.Nps = (float)(GetDouble(diff?["nps"]) ?? 0);
                    lb.Notes = (int)(GetDouble(diff?["notes"]) ?? 0);
                    lb.Chains = (int)(GetDouble(diff?["chains"]) ?? 0);
                    lb.Arcs = (int)(GetDouble(diff?["sliders"]) ?? 0);
                    lb.Duration = (float)(GetDouble(diff?["duration"]) ?? 0);
                    lb.BeatLeaderStatus = (int)(GetDouble(diff?["status"]) ?? 0);
                    lb.BeatLeaderStars = (float?)GetDouble(diff?["stars"]);
                    lb.BeatLeaderPassRating = (float?)GetDouble(diff?["passRating"]);
                    lb.BeatLeaderTechRating = (float?)GetDouble(diff?["techRating"]);
                    lb.BeatLeaderAccRating = (float?)GetDouble(diff?["accRating"]);
                    lb.BeatLeaderPredictedAcc = (float?)GetDouble(diff?["predictedAcc"]);
                    lb.Plays = (int)(GetDouble(root["plays"]) ?? 0);
                }

                int count = Interlocked.Increment(ref done);
                if (count % 100 == 0 || count == leaderboards.Count)
                {
                    Console.WriteLine($"Acc AI dataset: BeatLeader {count}/{leaderboards.Count} ({missing} not found)");
                }
            });
        }

        private static async Task<JsonNode?> GetBeatLeaderJson(string url)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await beatLeaderGate.WaitAsync();
                try
                {
                    var now = DateTime.UtcNow;
                    if (nextBeatLeaderCall > now) await Task.Delay(nextBeatLeaderCall - now);
                    nextBeatLeaderCall = DateTime.UtcNow + beatLeaderInterval;
                }
                finally
                {
                    beatLeaderGate.Release();
                }

                try
                {
                    using var response = await http.GetAsync(url);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                        await beatLeaderGate.WaitAsync();
                        try { nextBeatLeaderCall = DateTime.UtcNow + wait; } finally { beatLeaderGate.Release(); }
                        continue;
                    }
                    if (response.StatusCode == HttpStatusCode.NotFound) return null;
                    response.EnsureSuccessStatusCode();
                    return JsonNode.Parse(await response.Content.ReadAsStringAsync());
                }
                catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is JsonException)
                {
                    if (attempt == 4)
                    {
                        Console.WriteLine($"Acc AI dataset: BeatLeader request failed: {url} ({e.Message})");
                        return null;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)));
                }
            }
            return null;
        }

        // ------------------------------------------------------------------
        // BeatSaver
        // ------------------------------------------------------------------

        // ScoreSaber ranked = the difficulty has ScoreSaber stars on BeatSaver ("stars"); BeatSaver also mirrors BeatLeader's
        // ranked state ("blStars"). Up to 50 maps per request. OST/DLC maps aren't on BeatSaver and are skipped.
        private static async Task FetchBeatSaver(List<AccAIDatasetLeaderboard> leaderboards)
        {
            int ost = leaderboards.Count(l => l.IsOst);
            if (ost > 0) Console.WriteLine($"Acc AI dataset: {ost} OST leaderboards skipped for BeatSaver");
            leaderboards = leaderboards.Where(l => !l.IsOst).ToList();

            var keys = leaderboards.Select(l => l.BeatSaverKey.ToLowerInvariant())
                .Where(k => beatSaverKeyPattern.IsMatch(k))
                .Distinct()
                .ToList();

            var maps = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < keys.Count; i += 50)
            {
                var batch = keys.Skip(i).Take(50).ToList();
                try
                {
                    var ids = string.Join(",", batch);
                    var json = await http.GetStringAsync($"https://api.beatsaver.com/maps/ids/{ids}");
                    var root = JsonNode.Parse(json);
                    if (root is JsonObject obj && obj.ContainsKey("id") && obj.ContainsKey("versions"))
                    {
                        maps[obj["id"]!.GetValue<string>()] = obj; // single map response
                    }
                    else if (root is JsonObject dict)
                    {
                        foreach (var (key, map) in dict)
                        {
                            if (map != null) maps[key] = map;
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Acc AI dataset: BeatSaver batch {i / 50 + 1} failed ({e.Message})");
                }
                await Task.Delay(200);
            }
            Console.WriteLine($"Acc AI dataset: BeatSaver returned {maps.Count}/{keys.Count} maps");

            foreach (var lb in leaderboards)
            {
                if (!maps.TryGetValue(lb.BeatSaverKey, out var map)) continue;
                lb.FoundOnBeatSaver = true;

                var versions = map["versions"]?.AsArray();
                var version = versions?.FirstOrDefault(v => string.Equals(v?["hash"]?.GetValue<string>(), lb.Hash, StringComparison.OrdinalIgnoreCase))
                              ?? versions?.FirstOrDefault();
                var diff = version?["diffs"]?.AsArray().FirstOrDefault(d =>
                    d?["characteristic"]?.GetValue<string>() == lb.Characteristic &&
                    d?["difficulty"]?.GetValue<string>() == lb.Difficulty);

                var ssStars = GetDouble(diff?["stars"]);
                lb.ScoreSaberRanked = ssStars != null;
                lb.ScoreSaberStars = (float?)ssStars;
                lb.BeatSaverBeatLeaderRanked = GetDouble(diff?["blStars"]) != null;
            }
        }

        // ------------------------------------------------------------------
        // RatingAPI
        // ------------------------------------------------------------------

        // Pass, tech, acc ratings, stars and predicted acc without modifiers, computed the same way as RatingsRefresh.Overwrite.
        // Maps are downloaded to the "maps" folder the first time (BeatSaver), so the first run takes a while.
        private static void ComputeRatings(List<AccAIDatasetLeaderboard> leaderboards)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { { "MapsPath", "maps" } })
                .Build();

            foreach (var lb in leaderboards)
            {
                lb.RatingComputed = false;
                lb.RatingError = string.IsNullOrEmpty(lb.Hash) ? "No map hash (not found on BeatLeader)" : "";
            }
            var todo = leaderboards.Where(l => !string.IsNullOrEmpty(l.Hash)).ToList();

            Console.WriteLine($"Acc AI dataset: computing ratings for {todo.Count} leaderboards");
            int done = 0, failed = 0;
            Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount }, lb =>
            {
                try
                {
                    using var loggerFactory = LoggerFactory.Create(builder => { });
                    var controller = new RatingsController(configuration, loggerFactory.CreateLogger<RatingsController>());
                    var rating = controller.GetNoModifiers(lb.Hash, lb.Characteristic, RatingsRefresh.GetDiffCode(lb.Difficulty));
                    if (rating == null)
                    {
                        lb.RatingError = "Map or difficulty not found";
                        Interlocked.Increment(ref failed);
                    }
                    else
                    {
                        lb.RatingComputed = true;
                        lb.PassRating = (float)rating.LackMapCalculation.PassRating;
                        lb.TechRating = (float)rating.LackMapCalculation.TechRating;
                        lb.AccRating = (float)rating.AccRating;
                        lb.PredictedAcc = (float)rating.PredictedAcc;
                        lb.Stars = ReplayUtils.ToStars(lb.AccRating, lb.PassRating, lb.TechRating, rating.Curve);
                    }
                }
                catch (Exception e)
                {
                    lb.RatingError = e.Message;
                    Interlocked.Increment(ref failed);
                }

                int count = Interlocked.Increment(ref done);
                if (count % 100 == 0 || count == todo.Count)
                {
                    Console.WriteLine($"Acc AI dataset: ratings {count}/{todo.Count} ({failed} failed)");
                }
            });
        }

        private static double? GetDouble(JsonNode? node)
        {
            if (node is not JsonValue value) return null;
            if (value.TryGetValue(out double d)) return d;
            if (value.TryGetValue(out float f)) return f;
            if (value.TryGetValue(out long l)) return l;
            if (value.TryGetValue(out int i)) return i;
            return null;
        }
    }
}
