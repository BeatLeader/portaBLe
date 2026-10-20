using System.Text.Json;
using beatleader_analyzer;
using beatleader_parser;
using Microsoft.EntityFrameworkCore;
using Parser.Map.Difficulty.V3.Base;
using portaBLe.DB;
using RatingAPI.Controllers;

namespace portaBLe.Refresh
{
    // Checks the analyzer's parity (forehand / backhand of every swing, and so its resets) and swing directions against what
    // top players actually did in their replays. Read only: nothing is written to the database.
    //
    // 1. WriteSample: picks the leaderboards to check and writes their ids to wwwroot/parity-sample.txt
    //    (a spread over the star range, plus the maps with the most parity errors per second: DD / reset maps).
    // 2. On the BLReplaysPrepare side: BLReplaysPrepare parity <parity-sample.txt> [--top 10]
    //    writes parity2\<leaderboardId>.json: for every cut, the direction the saber moved in the hand's own frame and on screen.
    // 3. Run: analyses every sampled map with the current Analyzer and compares each swing's parity and direction with the
    //    players'. Writes wwwroot/parity-check.json, and every swing to wwwroot/parity-swings.json (to fit the Analyzer on).
    //
    // Player parity: forehand and backhand move the saber in opposite directions relative to the hand, so the angle stored by
    // BLReplaysPrepare splits them. Which angle is "forehand" is calibrated per replay and hand on the down arrows (forehands).
    // A note only counts when most of the players agree on it (ConsensusShare), so notes players play both ways are left out.
    public static class ParityCheck
    {
        public const string SampleFile = "parity-sample.txt";
        public const string ResultFile = "parity-check.json";
        public const string SwingsFile = "parity-swings.json.gz";   // gzip: the whole ranked list is several hundred MB as text

        // A replay's vote on a note counts when |cos(angle - forehand angle)| is above this (about 70 degrees away at most)
        private const double ConfidentCos = 0.35;
        // A note has a player parity when at least this many confident votes and this share of them agree
        private const int MinVotes = 3;
        private const double ConsensusShare = 0.7;
        // Analyzer note <-> replay note: same position, color and direction, and time within this
        private const double MatchSeconds = 0.05;
        private const int ExamplesPerMap = 20;
        // A note has a player direction when the players' cut directions agree at least this much (mean resultant length;
        // 0.7 is roughly a 45 degree spread)
        private const double DirectionAgreement = 0.7;

        // ------------------------------------------------------------------
        // 1. Sample
        // ------------------------------------------------------------------

        // Leaderboards always checked: 192c971 is a bomb reset reference
        public static string[] ExtraLeaderboards { get; } = { "192c971" };

        public static async Task WriteSample(AppContext dbContext, string webRootPath, int perStar = 8, int resetMaps = 40, int bombMaps = 40)
        {
            var lbs = await dbContext.Leaderboards.AsNoTracking()
                .Where(lb => lb.ModeName == "Standard" && lb.Stars > 0)
                .Select(lb => new { lb.Id, lb.Name, lb.DifficultyName, lb.Stars, lb.ParityErrors, lb.BombAvoidances, lb.Duration })
                .ToListAsync();

            var random = new Random(42);
            var picked = new List<(string Id, string Why)>();
            foreach (var bin in lbs.GroupBy(lb => (int)Math.Floor(lb.Stars)).OrderBy(g => g.Key))
            {
                foreach (var lb in bin.OrderBy(_ => random.Next()).Take(perStar))
                {
                    picked.Add((lb.Id, $"{bin.Key}* {lb.Name} [{lb.DifficultyName}]"));
                }
            }
            var chosen = picked.Select(p => p.Id).ToHashSet();
            foreach (var lb in lbs.Where(lb => lb.Duration > 30 && !chosen.Contains(lb.Id))
                         .OrderByDescending(lb => lb.ParityErrors / lb.Duration)
                         .Take(resetMaps))
            {
                picked.Add((lb.Id, FormattableString.Invariant($"resets {lb.ParityErrors / lb.Duration:0.00}/s, {lb.Stars:0.00}* {lb.Name} [{lb.DifficultyName}]")));
            }
            // Maps where the analyzer sees the most bomb avoidances per second
            chosen = picked.Select(p => p.Id).ToHashSet();
            foreach (var lb in lbs.Where(lb => lb.Duration > 30 && !chosen.Contains(lb.Id))
                         .OrderByDescending(lb => lb.BombAvoidances / lb.Duration)
                         .Take(bombMaps))
            {
                picked.Add((lb.Id, FormattableString.Invariant($"bombs {lb.BombAvoidances / lb.Duration:0.00}/s, {lb.Stars:0.00}* {lb.Name} [{lb.DifficultyName}]")));
            }
            chosen = picked.Select(p => p.Id).ToHashSet();
            foreach (var id in ExtraLeaderboards.Where(id => !chosen.Contains(id)))
            {
                var lb = lbs.FirstOrDefault(l => l.Id == id);
                picked.Add((id, lb == null ? "extra" : FormattableString.Invariant($"extra, {lb.Stars:0.00}* {lb.Name} [{lb.DifficultyName}]")));
            }

            var path = Path.Combine(webRootPath, SampleFile);
            await File.WriteAllLinesAsync(path, picked.Select(p => $"{p.Id}  # {p.Why.Replace('\n', ' ')}"));
            Console.WriteLine($"Parity sample: {picked.Count} leaderboards written to {path}");
        }

        // ------------------------------------------------------------------
        // 2. Player parity (from BLReplaysPrepare's parity files)
        // ------------------------------------------------------------------

        private class ParityFile
        {
            public string LeaderboardId { get; set; } = "";
            public List<ParityReplay> Replays { get; set; } = new();
        }

        private class ParityReplay
        {
            public string Player { get; set; } = "";
            public float Accuracy { get; set; }
            public List<float[]> Notes { get; set; } = new();
        }

        private readonly record struct NoteKey(int X, int Y, int Color, int CutDirection);

        private class PlayerNote
        {
            public double Time;
            public int Forehand, Backhand;
            public int Votes => Forehand + Backhand;
            // true forehand, false backhand, null when the players don't agree enough
            public bool? Parity => Votes >= MinVotes && Math.Max(Forehand, Backhand) >= ConsensusShare * Votes ? Forehand > Backhand : null;
            public double Share => Votes > 0 ? (double)Math.Max(Forehand, Backhand) / Votes : 0;

            // Direction the saber moved at the cut (world space, degrees, 0 right / 90 up): mean of the players' unit vectors
            public double DirX, DirY;
            // Roll of the controller at the cut (world direction minus the direction in the hand frame, both sign conventions
            // as the hand frame may be mirrored), minus the replay's mean for that hand: wrist rotation, not arm movement
            public double RollMX, RollMY, RollPX, RollPY;
            public int RollCount;
            public double? RollMinus => RollCount >= MinVotes ? (Math.Atan2(RollMY, RollMX) * 180 / Math.PI + 360) % 360 : null;
            public double? RollPlus => RollCount >= MinVotes ? (Math.Atan2(RollPY, RollPX) * 180 / Math.PI + 360) % 360 : null;
            public int DirCount;
            public double Angle => (Math.Atan2(DirY, DirX) * 180 / Math.PI + 360) % 360;
            // 1 when every player cut in the same direction, lower when they spread out
            public double Spread => DirCount > 0 ? Math.Sqrt(DirX * DirX + DirY * DirY) / DirCount : 0;
            public bool HasDirection => DirCount >= MinVotes && Spread >= DirectionAgreement;
        }

        private class PlayerParity
        {
            public int Replays;
            public Dictionary<NoteKey, List<PlayerNote>> Notes = new();

            public PlayerNote? Find(NoteKey key, double seconds)
            {
                if (!Notes.TryGetValue(key, out var list)) return null;
                PlayerNote? best = null;
                foreach (var note in list)
                {
                    double d = Math.Abs(note.Time - seconds);
                    if (d <= MatchSeconds && (best == null || d < Math.Abs(best.Time - seconds))) best = note;
                }
                return best;
            }
        }

        private static PlayerParity LoadPlayerParity(ParityFile file)
        {
            var result = new PlayerParity();
            var byNote = new Dictionary<(NoteKey, int), PlayerNote>();
            foreach (var replay in file.Replays)
            {
                var rows = new List<(NoteKey Key, double Time, int Hand, double Angle, double World)>();
                foreach (var row in replay.Notes)
                {
                    if (row.Length < 4) continue;
                    int id = (int)row[1];
                    if (id <= 0 || id >= 100000) continue; // mapping extensions ids aren't handled
                    int scoringType = id / 10000;
                    if (scoringType == 7) continue;        // burst slider element: not a swing
                    int rest = id % 10000;
                    var key = new NoteKey(rest / 1000, rest / 100 % 10, rest / 10 % 10, rest % 10);
                    int hand = (int)row[2];
                    if (hand != key.Color) continue;      // cut with the wrong saber
                    // 5th value (newer parity files): world-space cut direction
                    rows.Add((key, row[0], hand, row[3] * Math.PI / 180, row.Length >= 5 ? row[4] * Math.PI / 180 : double.NaN));
                }
                if (rows.Count == 0) continue;
                result.Replays++;

                foreach (var hand in new[] { 0, 1 })
                {
                    // Forehand direction for this replay and hand. Forehands and backhands sit at opposite ends of one axis in
                    // the hand frame: find that axis from every arrow (mean of the doubled angles), then which end is the
                    // forehand: the one most down arrows are on and most up arrows aren't. (Down arrows alone fail on maps
                    // that roll the wrist, where many of them are cut as backhands: Sanity Krush, Medicine, SONG REQUEST.)
                    var arrows = rows.Where(r => r.Hand == hand && r.Key.CutDirection != 8).ToList();
                    if (arrows.Count < 10) continue;
                    double ax = arrows.Sum(r => Math.Cos(2 * r.Angle)), ay = arrows.Sum(r => Math.Sin(2 * r.Angle));
                    if (Math.Sqrt(ax * ax + ay * ay) / arrows.Count < 0.5) continue; // no clear axis
                    double axis = Math.Atan2(ay, ax) / 2;
                    int side = 0;
                    foreach (var r in arrows)
                    {
                        int cut = r.Key.CutDirection;
                        int vote = cut == 1 || cut == 6 || cut == 7 ? 1 : cut == 0 || cut == 4 || cut == 5 ? -1 : 0;
                        double c = Math.Cos(r.Angle - axis);
                        if (vote == 0 || Math.Abs(c) < ConfidentCos) continue;
                        side += c > 0 ? vote : -vote;
                    }
                    if (side == 0) continue;
                    double forehandAngle = side > 0 ? axis : axis + Math.PI;

                    var handRows = rows.Where(r => r.Hand == hand && !double.IsNaN(r.World)).ToList();
                    double meanMinus = Math.Atan2(handRows.Sum(r => Math.Sin(r.World - r.Angle)), handRows.Sum(r => Math.Cos(r.World - r.Angle)));
                    double meanPlus = Math.Atan2(handRows.Sum(r => Math.Sin(r.World + r.Angle)), handRows.Sum(r => Math.Cos(r.World + r.Angle)));

                    foreach (var r in rows.Where(r => r.Hand == hand))
                    {
                        var noteKey = (r.Key, (int)Math.Round(r.Time * 1000));
                        if (!byNote.TryGetValue(noteKey, out var note))
                        {
                            note = new PlayerNote { Time = r.Time };
                            byNote[noteKey] = note;
                        }
                        if (!double.IsNaN(r.World))
                        {
                            note.DirX += Math.Cos(r.World);
                            note.DirY += Math.Sin(r.World);
                            note.DirCount++;
                            note.RollMX += Math.Cos(r.World - r.Angle - meanMinus);
                            note.RollMY += Math.Sin(r.World - r.Angle - meanMinus);
                            note.RollPX += Math.Cos(r.World + r.Angle - meanPlus);
                            note.RollPY += Math.Sin(r.World + r.Angle - meanPlus);
                            note.RollCount++;
                        }
                        double c = Math.Cos(r.Angle - forehandAngle);
                        if (Math.Abs(c) < ConfidentCos) continue;
                        if (c > 0) note.Forehand++; else note.Backhand++;
                    }
                }
            }
            foreach (var ((key, _), note) in byNote)
            {
                if (!result.Notes.TryGetValue(key, out var list)) result.Notes[key] = list = new();
                list.Add(note);
            }
            return result;
        }

        // ------------------------------------------------------------------
        // 3. Comparison
        // ------------------------------------------------------------------

        private class MapToCheck
        {
            public Leaderboard Leaderboard = null!;
            public DifficultyV3 Data = null!;
            public float Bpm;
            public PlayerParity Players = null!;
            // Bombs sorted by beat: (beat, x, y)
            public List<(float Beat, int X, int Y)> Bombs = new();
        }

        // Analyzer swing direction vs the players' cut direction
        private class DirAcc
        {
            public int N, W15, W30, W45, Reversed;
            public double SumAbs, SumSigned, SumSq;

            public void Add(double signed)
            {
                double a = Math.Abs(signed);
                N++; SumAbs += a; SumSigned += signed; SumSq += a * a;
                if (a <= 15) W15++;
                if (a <= 30) W30++;
                if (a <= 45) W45++;
                if (a > 90) Reversed++;
            }

            public void Add(DirAcc o)
            {
                N += o.N; W15 += o.W15; W30 += o.W30; W45 += o.W45; Reversed += o.Reversed;
                SumAbs += o.SumAbs; SumSigned += o.SumSigned; SumSq += o.SumSq;
            }

            public object Summary() => new
            {
                Swings = N,
                MeanError = R(N > 0 ? SumAbs / N : 0),
                RmsError = R(N > 0 ? Math.Sqrt(SumSq / N) : 0),
                // analyzer minus players, degrees counter-clockwise
                MeanSigned = R(N > 0 ? SumSigned / N : 0),
                Within15 = R(N > 0 ? (double)W15 / N : 0),
                Within30 = R(N > 0 ? (double)W30 / N : 0),
                Within45 = R(N > 0 ? (double)W45 / N : 0),
                Reversed = R(N > 0 ? (double)Reversed / N : 0),
            };
        }

        private class Counts
        {
            public Dictionary<string, DirAcc> Directions = new();

            public void AddDirection(string category, double signed)
            {
                if (!Directions.TryGetValue(category, out var acc)) Directions[category] = acc = new DirAcc();
                acc.Add(signed);
            }

            public int Swings, Agree, TruthResets, AnalyzerResets, ResetPairs, TP, FP, FN, MultiNote, Split;
            // Same, for the swing pairs with bombs between them on the hand's side
            public int BombPairs, BombAgree, BombTruthResets, BombAnalyzerResets, BombTP, BombFP, BombFN;

            public void Add(Counts o)
            {
                Swings += o.Swings; Agree += o.Agree; TruthResets += o.TruthResets; AnalyzerResets += o.AnalyzerResets;
                ResetPairs += o.ResetPairs; TP += o.TP; FP += o.FP; FN += o.FN; MultiNote += o.MultiNote; Split += o.Split;
                BombPairs += o.BombPairs; BombAgree += o.BombAgree; BombTruthResets += o.BombTruthResets; BombAnalyzerResets += o.BombAnalyzerResets;
                BombTP += o.BombTP; BombFP += o.BombFP; BombFN += o.BombFN;
                foreach (var (k, v) in o.Directions)
                {
                    if (!Directions.TryGetValue(k, out var acc)) Directions[k] = acc = new DirAcc();
                    acc.Add(v);
                }
            }

            public object Summary() => new
            {
                Swings,
                Agreement = R(Swings > 0 ? (double)Agree / Swings : 0),
                ResetPairs,
                TruthResets,
                AnalyzerResets,
                ResetPrecision = R(TP + FP > 0 ? (double)TP / (TP + FP) : 0),
                ResetRecall = R(TP + FN > 0 ? (double)TP / (TP + FN) : 0),
                ResetF1 = R(2 * TP + FP + FN > 0 ? 2.0 * TP / (2 * TP + FP + FN) : 0),
                MultiNoteSwings = MultiNote,
                SplitByPlayers = Split,
                Bombs = new
                {
                    Pairs = BombPairs,
                    Agreement = R(BombPairs > 0 ? (double)BombAgree / BombPairs : 0),
                    TruthResets = BombTruthResets,
                    AnalyzerResets = BombAnalyzerResets,
                    ResetPrecision = R(BombTP + BombFP > 0 ? (double)BombTP / (BombTP + BombFP) : 0),
                    ResetRecall = R(BombTP + BombFN > 0 ? (double)BombTP / (BombTP + BombFN) : 0),
                },
                Directions = Directions.OrderBy(d => d.Key).ToDictionary(d => d.Key, d => d.Value.Summary()),
            };
        }

        public static async Task Run(AppContext dbContext, string webRootPath, string parityFolder)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var files = Directory.EnumerateFiles(parityFolder, "*.json").ToList();
            var ids = files.Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet();
            var lbs = (await dbContext.Leaderboards.AsNoTracking().ToListAsync()).Where(lb => ids.Contains(lb.Id)).ToDictionary(lb => lb.Id);
            Console.WriteLine($"Parity check: {files.Count} parity files, {lbs.Count} of them in the database");

            // Load each map and its player parity, analyse it, and keep the comparison
            var maps = new System.Collections.Concurrent.ConcurrentBag<MapToCheck>();
            var perMap = new System.Collections.Concurrent.ConcurrentDictionary<string, (Counts Counts, double Pass, double Tech, List<object[]> Examples, int Matched, int Total)>();
            var swingRows = new System.Collections.Concurrent.ConcurrentDictionary<string, List<List<object?[]>>>();
            int analysed = 0;
            var downloader = new Downloader("maps");
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount }, async (file, ct) =>
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (!lbs.TryGetValue(id, out var lb)) return;
                try
                {
                    var parity = JsonSerializer.Deserialize<ParityFile>(await File.ReadAllTextAsync(file, ct));
                    if (parity == null || parity.Replays.Count == 0) return;
                    var players = LoadPlayerParity(parity);
                    if (players.Replays == 0) return;

                    var mapPath = await downloader.MapAsync(lb.Hash);
                    if (mapPath == null) return;
                    var mapset = new Parse().TryLoadPath(mapPath);
                    int diffCode = RatingsRefresh.GetDiffCode(lb.DifficultyName);
                    var set = mapset?.Difficulties.FirstOrDefault(d => d.Characteristic == lb.ModeName
                        && (d.BeatMap._difficultyRank == diffCode || d.BeatMap._difficulty == lb.DifficultyName));
                    if (set == null) return;
                    var map = new MapToCheck
                    {
                        Leaderboard = lb,
                        Data = set.Data,
                        Bpm = (float)mapset!.Info._beatsPerMinute,
                        Players = players,
                        Bombs = set.Data.Bombs.Select(b => ((float)b.BpmTime, (int)b.x, (int)b.y)).OrderBy(b => b.Item1).ToList(),
                    };
                    // Analyse right away and drop the map data, so the whole ranked list fits in memory
                    var ratings = new Analyze().GetRating(map.Data, map.Leaderboard.ModeName, map.Leaderboard.DifficultyName, map.Bpm);
                    map.Data = null!;
                    if (ratings == null) return;
                    var rows = new List<List<object?[]>> { new(), new() };
                    perMap[map.Leaderboard.Id] = Compare(ratings, map, rows);
                    swingRows[map.Leaderboard.Id] = rows;
                    maps.Add(map);
                    int loaded = Interlocked.Increment(ref analysed);
                    if (loaded % 250 == 0) Console.WriteLine($"Parity check: {loaded} maps analysed ({timer.Elapsed:mm\\:ss})");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Parity check: {id} skipped ({e.Message})");
                }
            });
            var mapList = maps.OrderBy(m => m.Leaderboard.Stars).ToList();
            Console.WriteLine($"Parity check: {perMap.Count} maps analysed ({timer.Elapsed:mm\\:ss})");

            var swingsPath = Path.Combine(webRootPath, SwingsFile);
            await using (var swingsStream = new System.IO.Compression.GZipStream(File.Create(swingsPath), System.IO.Compression.CompressionLevel.Optimal))
            await JsonSerializer.SerializeAsync(swingsStream, new
            {
                Columns = new[]
                {
                    "HeadSeconds", "TailSeconds", "HeadBeat", "TailBeat", "HeadX", "HeadY", "HeadCut", "TailX", "TailY", "TailCut", "Notes",
                    "Direction", "AnalyzerBombAvoidance", "AnalyzerForehand", "PlayerForehand", "PlayerShare", "PlayerVotes",
                    "BombsBefore", "ParityDirection", "PlayerAngle", "PlayerAngleSpread", "Chain", "AngleOffset",
                    "ArcOffset", "AngleStrain", "RotationAmount", "SwingTech", "ParityErrors", "RollMinus", "RollPlus", "RepositioningDistance",
                },
                // Per map, per hand (0 left, 1 right): swings in time order. BombsBefore: [beat, x, y] of every bomb between the
                // previous swing's last note and this swing's first note. PlayerForehand: 1, 0, or null when players don't agree.
                Maps = swingRows.ToDictionary(kv => kv.Key, kv => kv.Value),
            });
            Console.WriteLine($"Parity check: swings saved to {swingsPath} ({timer.Elapsed:mm\\:ss})");

            // Summaries: all maps, reset-heavy maps (players reset on 25%+ of the swing pairs) and the others
            var all = new Counts();
            var resetHeavy = new Counts();
            var normal = new Counts();
            foreach (var (_, r) in perMap)
            {
                all.Add(r.Counts);
                (IsResetHeavy(r.Counts) ? resetHeavy : normal).Add(r.Counts);
            }
            Console.WriteLine(FormattableString.Invariant(
                $"  parity agreement {Ratio(all.Agree, all.Swings)}  resets: precision {Ratio(all.TP, all.TP + all.FP)} recall {Ratio(all.TP, all.TP + all.FN)}  | reset maps agreement {Ratio(resetHeavy.Agree, resetHeavy.Swings)}  | bomb resets: precision {Ratio(all.BombTP, all.BombTP + all.BombFP)} recall {Ratio(all.BombTP, all.BombTP + all.BombFN)}"));
            foreach (var kind in new[] { "all", "arrow", "dot", "multi", "chain", "after bombs" })
            {
                if (!all.Directions.TryGetValue(kind, out var acc) || acc.N == 0) continue;
                Console.WriteLine(FormattableString.Invariant(
                    $"  direction {kind,-12} {acc.N,7} swings: mean error {acc.SumAbs / acc.N:0.0} deg, within 30 deg {(double)acc.W30 / acc.N:0.000}, reversed {(double)acc.Reversed / acc.N:0.000}"));
            }

            var mapRows = mapList.Where(m => perMap.ContainsKey(m.Leaderboard.Id)).Select(m =>
            {
                var lb = m.Leaderboard;
                var r = perMap[lb.Id];
                var allNotes = m.Players.Notes.Values.SelectMany(l => l).ToList();
                return new
                {
                    lb.Id,
                    lb.Name,
                    lb.DifficultyName,
                    Stars = R(lb.Stars),
                    PassRating = R(lb.PassRating),
                    TechRating = R(lb.TechRating),
                    DbParityErrors = lb.ParityErrors,
                    Replays = m.Players.Replays,
                    MatchRate = R(r.Total > 0 ? (double)r.Matched / r.Total : 0),
                    PlayerConsistency = R(allNotes.Count > 0 ? allNotes.Average(n => n.Share) : 0),
                    ResetHeavy = IsResetHeavy(r.Counts),
                    Result = r.Counts.Summary(),
                    // Ratings from the Analyzer as it is now (the database ones above may be older)
                    Pass = R(r.Pass),
                    Tech = R(r.Tech),
                    // [time (s), hand (0 left, 1 right), cut direction, analyzer forehand, players forehand, players reset]
                    Disagreements = r.Examples,
                };
            }).ToList();

            var result = new
            {
                GeneratedAt = DateTime.UtcNow,
                Settings = new { ConfidentCos, MinVotes, ConsensusShare, MatchSeconds },
                Summary = new { All = all.Summary(), ResetHeavyMaps = resetHeavy.Summary(), OtherMaps = normal.Summary() },
                Maps = mapRows,
            };
            var path = Path.Combine(webRootPath, ResultFile);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result));
            Console.WriteLine($"Parity check saved to {path}: {mapRows.Count} maps ({timer.Elapsed:mm\\:ss})");
        }

        private static bool IsResetHeavy(Counts c) => c.ResetPairs >= 20 && c.TruthResets >= 0.25 * c.ResetPairs;

        private static (Counts, double, double, List<object[]>, int, int) Compare(beatleader_analyzer.BeatmapScanner.Data.Ratings ratings, MapToCheck map, List<List<object?[]>>? export)
        {
            var players = map.Players;
            var counts = new Counts();
            var examples = new List<object[]>();
            int matched = 0, total = 0;

            foreach (var hand in ratings.SwingData.GroupBy(s => s.Cubes[0].Type).OrderBy(g => g.Key))
            {
                bool? previousTruth = null;
                bool? previousAnalyzer = null;
                float previousBeat = float.MinValue;
                var handRows = export != null && hand.Key >= 0 && hand.Key < export.Count ? export[hand.Key] : null;
                foreach (var swing in hand.OrderBy(s => s.Cubes[0].Seconds))
                {
                    var head = swing.Cubes[0];
                    var tail = swing.Cubes[^1];
                    var bombs = BombsBetween(map.Bombs, previousBeat, head.BpmTime);
                    previousBeat = tail.BpmTime;
                    // Bombs that can push this hand: its own side and the middle columns
                    bool bombsNear = bombs.Any(b => head.Type == 0 ? b.X <= 2 : b.X >= 1);
                    total++;
                    var note = players.Find(new NoteKey(head.X, head.Y, head.Type, head.CutDirection), head.Seconds);
                    if (note != null) matched++;
                    bool? truth = note?.Parity;

                    handRows?.Add(new object?[]
                    {
                        R(head.Seconds), R(tail.Seconds), R(head.BpmTime), R(tail.BpmTime), head.X, head.Y, head.CutDirection,
                        tail.X, tail.Y, tail.CutDirection, swing.Cubes.Count, R(swing.Direction), swing.BombAvoidance, swing.Forehand,
                        truth, R(note?.Share ?? 0), note?.Votes ?? 0,
                        bombs.Select(b => new object[] { R(b.Beat), b.X, b.Y }).ToList(),
                        R(head.ParityDirection),
                        note != null && note.DirCount > 0 ? R(note.Angle) : null,
                        note != null && note.DirCount > 0 ? R(note.Spread) : null,
                        head.Chain,
                        R(head.AngleOffset),
                        R(swing.ArcOffset), R(swing.AngleStrain), R(swing.RotationAmount), R(swing.SwingTech), swing.ParityErrors,
                        note?.RollMinus is double rm ? R(rm) : null, note?.RollPlus is double rp ? R(rp) : null, R(swing.RepositioningDistance),
                    });

                    // Notes of a multi-note swing that the players hit with the other parity: they swung twice
                    if (swing.Cubes.Count > 1 && truth.HasValue)
                    {
                        counts.MultiNote++;
                        if (swing.Cubes.Skip(1).Any(c => players.Find(new NoteKey(c.X, c.Y, c.Type, c.CutDirection), c.Seconds)?.Parity is bool p && p != truth.Value))
                        {
                            counts.Split++;
                        }
                    }

                    // Direction: the analyzer's final swing direction (the one the difficulty uses) vs the players' cut direction
                    // on the swing's first note
                    if (note != null && note.HasDirection)
                    {
                        double signed = AngleDifference(note.Angle, swing.Direction);
                        string kind = swing.Cubes.Count > 1 ? (head.Chain ? "chain" : "multi") : head.CutDirection == 8 ? "dot" : head.Chain ? "chain" : "arrow";
                        counts.AddDirection("all", signed);
                        counts.AddDirection(kind, signed);
                        if (bombsNear) counts.AddDirection("after bombs", signed);
                        if (truth.HasValue) counts.AddDirection(truth.Value == swing.Forehand ? "parity right" : "parity wrong", signed);
                        if (kind == "arrow") counts.AddDirection($"arrow {(head.Type == 0 ? "L" : "R")}{CutNames[head.CutDirection]}", signed);
                        if (kind == "dot") counts.AddDirection($"dot {(swing.Forehand ? "forehand" : "backhand")}", signed);
                    }

                    bool truthReset = false;
                    if (truth.HasValue)
                    {
                        counts.Swings++;
                        if (truth.Value == swing.Forehand) counts.Agree++;

                        if (previousTruth.HasValue && previousAnalyzer.HasValue)
                        {
                            counts.ResetPairs++;
                            truthReset = previousTruth.Value == truth.Value;
                            bool analyzerReset = previousAnalyzer.Value == swing.Forehand;
                            if (truthReset) counts.TruthResets++;
                            if (analyzerReset) counts.AnalyzerResets++;
                            if (truthReset && analyzerReset) counts.TP++;
                            else if (analyzerReset) counts.FP++;
                            else if (truthReset) counts.FN++;

                            if (bombsNear)
                            {
                                counts.BombPairs++;
                                if (truth.Value == swing.Forehand) counts.BombAgree++;
                                if (truthReset) counts.BombTruthResets++;
                                if (analyzerReset) counts.BombAnalyzerResets++;
                                if (truthReset && analyzerReset) counts.BombTP++;
                                else if (analyzerReset) counts.BombFP++;
                                else if (truthReset) counts.BombFN++;
                            }
                        }

                        if (truth.Value != swing.Forehand && examples.Count < ExamplesPerMap)
                        {
                            examples.Add(new object[] { R(head.Seconds), head.Type, head.CutDirection, swing.Forehand, truth.Value, truthReset });
                        }
                    }
                    // A swing the players don't agree on breaks the chain: no reset pair across it
                    previousTruth = truth;
                    previousAnalyzer = truth.HasValue ? swing.Forehand : null;
                }
            }

            examples.Sort((a, b) => ((double)a[0]).CompareTo((double)b[0]));
            return (counts, ratings.PassRating, ratings.TechRating, examples, matched, total);
        }

        private static readonly string[] CutNames = { " up", " down", " left", " right", " up-left", " up-right", " down-left", " down-right", " dot" };

        // Signed angle b - a in degrees, in [-180, 180)
        private static double AngleDifference(double a, double b) => ((b - a) % 360 + 540) % 360 - 180;

        private static List<(float Beat, int X, int Y)> BombsBetween(List<(float Beat, int X, int Y)> bombs, float after, float before)
        {
            var result = new List<(float Beat, int X, int Y)>();
            int lo = 0, hi = bombs.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (bombs[mid].Beat <= after) lo = mid + 1; else hi = mid;
            }
            for (int i = lo; i < bombs.Count && bombs[i].Beat < before; i++) result.Add(bombs[i]);
            return result;
        }

        private static string Ratio(int a, int b) => b > 0 ? ((double)a / b).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "-";

        private static double R(double value) => double.IsFinite(value) ? Math.Round(value, 4) : 0;
    }
}
