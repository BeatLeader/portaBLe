using Analyzer.BeatmapScanner.Data;
using beatleader_analyzer;
using beatleader_analyzer.BeatmapScanner.Data;
using beatleader_parser;
using Parser.Map;
using Parser.Map.Difficulty.V3.Base;
using RatingAPI.Controllers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;

// RatingsDump: recompute production ratings for a list of leaderboards with ONE analyzer
// variant and dump (a) per-map ratings for each speed modifier, (b) the per-swing analyzer
// table for the unmodified map, (c) the AI model's per-note accuracy predictions.
//
//   RatingsDump --lbs lbs.csv --maps-dir maps --out out --tag prod [--threads 4] [--no-ai] [--no-swings] [--mods SS,none,FS,SFS,BFS,BSF]
//
// lbs.csv columns: lb_id,hash,mode,difficulty

string lbsPath = "", mapsDir = "maps", outDir = "out", tag = "prod";
int threads = 4;
bool noAi = false, noSwings = false;
string accModelPath = "";
string[] modNames = { "SS", "none", "FS", "SFS" };
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--lbs": lbsPath = args[++i]; break;
        case "--maps-dir": mapsDir = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--tag": tag = args[++i]; break;
        case "--threads": threads = int.Parse(args[++i]); break;
        case "--no-ai": noAi = true; break;
        case "--no-swings": noSwings = true; break;
        case "--acc-model": accModelPath = args[++i]; break;
        case "--mods": modNames = args[++i].Split(','); break;
        default: Console.WriteLine($"unknown arg {args[i]}"); return 1;
    }
}
if (lbsPath == "") { Console.WriteLine("--lbs required"); return 1; }
Directory.CreateDirectory(outDir);

var lbs = File.ReadLines(lbsPath).Skip(1)
    .Select(l => l.Split(','))
    .Where(c => c.Length >= 4)
    .Select(c => (id: c[0], hash: c[1], mode: c[2], diff: c[3]))
    .ToList();
Console.WriteLine($"{lbs.Count} leaderboards, variant tag '{tag}', {threads} threads");

// BFS / BSF: FS / SF speed with only half of the NJS increase (same njsMult as RatingAPI's /ppai2)
var allMods = new (string name, double scale, double njsMult)[] {
    ("SS", 0.85, 1.0), ("none", 1.0, 1.0), ("FS", 1.2, 1.0), ("SFS", 1.5, 1.0),
    ("BFS", 1.2, 1.1 / 1.2), ("BSF", 1.5, 1.25 / 1.5),
};
var mods = modNames.Select(n => allMods.Single(m => m.name == n)).ToArray();

string ratingsPath = Path.Combine(outDir, $"ratings_{tag}.csv");
string swingsPath = Path.Combine(outDir, $"swings_{tag}.csv.gz");
string aiPath = Path.Combine(outDir, $"ai_notes_{tag}.csv.gz");
string featPath = Path.Combine(outDir, $"features_{tag}.csv");
// algorithmic acc model: explicit file, else the one embedded from RatingAPI/acc_model.json (if any)
var accModel = accModelPath != "" ? AccDifficultyModel.Load(accModelPath) : AccDifficultyModel.Default;
Console.WriteLine("acc model: " + (accModel == null ? "none" : $"{accModel.Spec.Features.Count} features, v{accModel.Spec.Version}"));
string errPath = Path.Combine(outDir, $"errors_{tag}.log");

var doneIds = new HashSet<string>();
bool resume = File.Exists(ratingsPath);
if (resume)
{
    foreach (var l in File.ReadLines(ratingsPath).Skip(1)) { int c = l.IndexOf(','); if (c > 0) doneIds.Add(l.Substring(0, c)); }
    Console.WriteLine($"resuming: {doneIds.Count} leaderboards already in {ratingsPath}");
}

// gz outputs are written as new members on resume (concatenated gzip is valid)
var ratingsW = new StreamWriter(ratingsPath, resume, new UTF8Encoding(false));
var swingsW = noSwings ? null : new StreamWriter(new GZipStream(new FileStream(swingsPath, FileMode.Append), CompressionLevel.Optimal), new UTF8Encoding(false));
var aiW = noAi ? null : new StreamWriter(new GZipStream(new FileStream(aiPath, FileMode.Append), CompressionLevel.Optimal), new UTF8Encoding(false));

var ratingsCols = new[] {
    "lb_id", "mod", "pass", "tech", "low_note_nerf", "linear_pct", "multi_pct", "peak_ebpm",
    "stacks", "towers", "sliders", "curved_sliders", "windows", "slanted_windows", "chains",
    "dodge_walls", "dodge_wall_dur", "crouch_walls", "crouch_wall_dur", "parity_errors", "bomb_avoid", "linear_swings",
    "n_swings", "n_notes", "n_bombs", "n_walls", "bpm", "length", "njs_mean", "n_direction_repeats",
    "ai_raw_acc", "predicted_acc", "ai_free_points", "ai_n_notes", "acc_rating", "stars", "ai_ms", "analyze_ms",
    "algo_difficulty", "algo_predicted_acc"
};
if (!resume) ratingsW.WriteLine(string.Join(",", ratingsCols));
var featW = new StreamWriter(featPath, resume && File.Exists(featPath), new UTF8Encoding(false));
if (!(resume && new FileInfo(featPath).Length > 0)) featW.WriteLine("lb_id,mod," + string.Join(",", AccDifficultyFeatures.Names));
if (swingsW != null && new FileInfo(swingsPath).Length < 30)
{
    swingsW.WriteLine(string.Join(",", H.SwingCols));
}
if (aiW != null && new FileInfo(aiPath).Length < 30)
{
    aiW.WriteLine("lb_id,note_i,time,acc,key,njs");
}

#if CORPUS
RatingAPI.Utils.DotDirectionOnnx.Register();   // same registration RatingAPI-corpus does at startup (needs dot_direction.onnx next to the exe)
Console.WriteLine("dot-direction model registered: " + (Analyzer.BeatmapScanner.Algorithm.DotDirectionModel.Predictor != null));
#endif
var parser = new Parse();
var errLock = new object();
int done = 0, failed = 0;
var sw = Stopwatch.StartNew();

var todo = lbs.Where(l => !doneIds.Contains(l.id)).ToList();
// group by hash so each map folder is parsed once even when several difficulties are requested
var byHash = todo.GroupBy(l => l.hash.ToUpperInvariant()).ToList();

Parallel.ForEach(byHash, new ParallelOptions { MaxDegreeOfParallelism = threads }, group =>
{
    var ai = new InferPublish();
    BeatmapV3 mapset = null;
    try
    {
        string mapPath = H.FindMap(mapsDir, group.Key);
        if (mapPath == null) throw new FileNotFoundException("map folder missing");
        mapset = parser.TryLoadPath(mapPath);
        if (mapset == null) throw new Exception("parse returned null");
    }
    catch (Exception e)
    {
        foreach (var l in group) Fail(l.id, "load: " + e.Message);
        return;
    }

    foreach (var lb in group)
    {
        try
        {
            int diffRank = H.DiffCode(lb.diff);
            var map = mapset.Difficulties.FirstOrDefault(d => d.Characteristic == H.CustomModeMapping(lb.mode)
                && (d.BeatMap._difficultyRank == diffRank || d.BeatMap._difficulty == lb.diff));
            if (map == null) throw new Exception("difficulty not found");
            double bpm = mapset.Info._beatsPerMinute;

            var rows = new List<string>();
            var featRows = new List<string>();
            string swingText = null, aiText = null;

            foreach (var (name, timescale, njsMult) in mods)
            {
                var mapdata = H.CustomModeDataMapping(lb.mode, map.Data);

                var t0 = Stopwatch.StartNew();
                var ratings = H.Rate(mapdata, lb.mode, lb.diff, (float)bpm, (float)timescale, (float)njsMult);
                long analyzeMs = t0.ElapsedMilliseconds;
                if (ratings == null) throw new Exception($"analyzer returned null ({name}; <20 notes?)");
                double infoNjs = map.BeatMap?._noteJumpMovementSpeed ?? 0, jumpOffset = map.BeatMap?._noteJumpStartBeatOffset ?? 0;
                var feats = AccDifficultyFeatures.Compute(ratings, mapdata, bpm, timescale, njsMult, infoNjs, jumpOffset);
                var baseRatings = accModel != null && (timescale != 1 || njsMult != 1) ? H.Rate(mapdata, lb.mode, lb.diff, (float)bpm, 1, 1) : null;
                double? algoDifficulty = accModel?.Difficulty(ratings, mapdata, bpm, timescale, njsMult, baseRatings, infoNjs, jumpOffset);
                double? algoAcc = algoDifficulty == null ? null : accModel.PredictedAccFromDifficulty(algoDifficulty.Value);

                double rawAcc = 0, predicted = 0, freePoints = 0, accRating = 0, stars = 0;
                int aiNotes = 0; long aiMs = 0;
                var lack = new LackMapCalculation
                {
                    PassRating = ratings.PassRating, TechRating = ratings.TechRating,
                    LowNoteNerf = ratings.LowNoteNerf, LinearPercentage = ratings.LinearPercentage,
                };
                if (!noAi)
                {
                    t0.Restart();
                    var (accs, noteTimes, fp) = ai.PredictHitsForMap(mapdata, bpm, timescale, njsMult);
                    aiMs = t0.ElapsedMilliseconds;
                    if (accs.Count > 0)
                    {
                        rawAcc = ai.GetMapAccForHits(accs, fp);
                        predicted = ai.ScaleFarmability(rawAcc, accs.Count, ((noteTimes.Last() - noteTimes.First() + 4) / timescale) + 2);
                    }
                    freePoints = fp; aiNotes = accs.Count;

                    var ar = new AccRating();
                    accRating = ar.GetRating(predicted, ratings.PassRating, ratings.TechRating) * ratings.LowNoteNerf;
                    var curve = new Curve();
                    stars = curve.ToStars(0.96, accRating, lack, curve.GetCurve(lack));

                    if (name == "none" && aiW != null)
                    {
                        // note identity ("x y dir color" digits) lets replay notes be joined to the per-note prediction
                        var keys = DataProcessing.GetMapNotesFromJson(mapdata, bpm);
                        // the ML pipeline predicts whole 8-note segments only, so accs is a prefix of the note list (last n%8 notes dropped)
                        bool aligned = keys.Count >= accs.Count && accs.Count > 0 && Math.Abs(keys[accs.Count - 1].Item1 - noteTimes[accs.Count - 1]) < 1e-4;
                        var sb = new StringBuilder();
                        for (int i = 0; i < accs.Count; i++)
                            sb.Append(lb.id).Append(',').Append(i).Append(',').Append(H.F(noteTimes[i])).Append(',').Append(H.F(accs[i])).Append(',')
                              .Append(aligned ? keys[i].Item2 : "").Append(',').Append(aligned ? H.F(keys[i].Item3) : "").Append('\n');
                        aiText = sb.ToString();
                    }
                }

                var st = ratings.Statistics;
                double start = map.Data.Notes.Count > 0 ? map.Data.Notes.Min(x => x.Seconds) : 0;
                double end = map.Data.Notes.Count > 0 ? map.Data.Notes.Max(x => x.Seconds) : 0;
                int repeats = 0;
#if CORPUS
                repeats = ratings.SwingData.Count(s => s.DirectionRepeat);
#endif
                rows.Add(string.Join(",", new object[] {
                    lb.id, name, H.F(ratings.PassRating), H.F(ratings.TechRating), H.F(ratings.LowNoteNerf), H.F(ratings.LinearPercentage),
                    H.F(ratings.MultiPercentage), H.F(ratings.PeakSustainedEBPM),
                    st.Stacks, st.Towers, st.Sliders, st.CurvedSliders, st.Windows, st.SlantedWindows, st.Chains,
                    st.DodgeWalls, H.F(st.DodgeWallDuration), st.CrouchWalls, H.F(st.CrouchWallDuration), st.ParityErrors, st.BombAvoidances, st.LinearSwings,
                    ratings.SwingData.Count, map.Data.Notes.Count, map.Data.Bombs.Count, map.Data.Walls.Count, H.F(bpm), H.F(end - start),
                    H.F(ratings.SwingData.Count > 0 ? ratings.SwingData.Average(s => (double)s.Cubes[0].Njs) : 0), repeats,
                    H.F(rawAcc), H.F(predicted), H.F(freePoints), aiNotes, H.F(accRating), H.F(stars), aiMs, analyzeMs,
                    algoDifficulty == null ? "" : H.F(algoDifficulty.Value), algoAcc == null ? "" : H.F(algoAcc.Value)
                }));
                featRows.Add(lb.id + "," + name + "," + string.Join(",", feats.Select(H.F)));

                if (name == "none" && swingsW != null)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < ratings.SwingData.Count; i++) H.AppendSwing(sb, lb.id, i, ratings.SwingData[i]);
                    swingText = sb.ToString();
                }
            }

            lock (ratingsW)
            {
                foreach (var r in rows) ratingsW.WriteLine(r);
                foreach (var r in featRows) featW.WriteLine(r);
                featW.Flush();
                ratingsW.Flush();
                if (swingText != null) swingsW.Write(swingText);
                if (aiText != null) aiW.Write(aiText);
            }
            int d = Interlocked.Increment(ref done);
            if (d % 100 == 0) Console.WriteLine($"  {d} done, {failed} failed, {sw.Elapsed.TotalMinutes:0.0} min");
        }
        catch (Exception e)
        {
            Fail(lb.id, e.Message);
        }
    }
});

ratingsW.Dispose(); swingsW?.Dispose(); aiW?.Dispose(); featW.Dispose();
Console.WriteLine($"finished: {done} ok, {failed} failed in {sw.Elapsed.TotalMinutes:0.0} min");
return 0;

void Fail(string id, string msg)
{
    Interlocked.Increment(ref failed);
    lock (errLock) File.AppendAllText(errPath, $"{id}: {msg}\n");
}

static class H
{
public static string F(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString("G9", CultureInfo.InvariantCulture);

public static string FindMap(string mapsDir, string hashUpper)
{
    var lower = Path.Combine(mapsDir, hashUpper.ToLowerInvariant());
    if (Directory.Exists(lower)) return lower;
    var upper = Path.Combine(mapsDir, hashUpper);
    return Directory.Exists(upper) ? upper : null;
}

public static int DiffCode(string d) => d switch { "Easy" => 1, "Normal" => 3, "Hard" => 5, "Expert" => 7, "ExpertPlus" => 9, _ => 9 };

public static string CustomModeMapping(string mode) => mode == "InvertedStandard" ? "Standard" : mode;

public static DifficultyV3 CustomModeDataMapping(string mode, DifficultyV3 mapdata)
{
    switch (mode)
    {
        case "VerticalStandard": return Parser.Utils.ChiralitySupport.Mirror_Vertical(mapdata, false, false, false);
        case "HorizontalStandard": return Parser.Utils.ChiralitySupport.Mirror_Horizontal(mapdata, 4, false, false, false);
        case "InverseStandard": return Parser.Utils.ChiralitySupport.Mirror_Inverse(mapdata, 4, true, true, false, false);
        case "InvertedStandard": return Parser.Utils.ChiralitySupport.Mirror_Inverse(mapdata, 4, false, false, false, false);
        default: return mapdata;
    }
}

#if CORPUS
public static Ratings Rate(DifficultyV3 d, string mode, string diff, float bpm, float speed, float njs) => Analyze.GetRating(d, mode, diff, bpm, speed, njs);
#else
static readonly Analyze analyzer = new();
public static Ratings Rate(DifficultyV3 d, string mode, string diff, float bpm, float speed, float njs) => analyzer.GetRating(d, mode, diff, bpm, speed, njs);
#endif

public static string[] SwingCols => new[] {
    "lb_id", "swing_i", "bpm_time", "seconds", "hand", "x", "y", "cut_direction", "n_cubes", "is_chain", "pattern_type", "njs",
    "direction", "forehand", "parity_error", "false_positive", "bomb_avoidance", "is_linear", "is_stream",
    "angle_strain", "reposition", "rotation", "frequency", "hit_distance", "dist_diff", "swing_speed", "stress",
    "low_speed_falloff", "stress_mult", "njs_buff", "wall_buff", "swing_diff", "swing_tech",
    "entry_x", "entry_y", "exit_x", "exit_y", "direction_repeat", "transition_gap", "member_cubes"
};

public static void AppendSwing(StringBuilder sb, string lbId, int i, SwingData s)
{
    var head = s.Cubes[0];
    double repeat = 0, gap = 0;
#if CORPUS
    repeat = s.DirectionRepeat ? 1 : 0; gap = s.TransitionGap;
#endif
    string members = string.Join(";", s.Cubes.Select(c => $"{F(c.Seconds)}:{c.X}:{c.Y}:{c.Type}"));
    sb.Append(lbId).Append(',').Append(i).Append(',')
      .Append(F(s.BpmTime)).Append(',').Append(F(head.Seconds)).Append(',').Append(head.Type).Append(',')
      .Append(head.X).Append(',').Append(head.Y).Append(',').Append(head.CutDirection).Append(',').Append(s.Cubes.Count).Append(',')
      .Append(head.Chain ? 1 : 0).Append(',').Append(s.PatternType).Append(',').Append(F(head.Njs)).Append(',')
      .Append(F(s.Direction)).Append(',').Append(s.Forehand ? 1 : 0).Append(',').Append(s.ParityErrors ? 1 : 0).Append(',')
      .Append(s.FalsePositive ? 1 : 0).Append(',').Append(s.BombAvoidance ? 1 : 0).Append(',').Append(s.IsLinear ? 1 : 0).Append(',').Append(s.IsStream ? 1 : 0).Append(',')
      .Append(F(s.AngleStrain)).Append(',').Append(F(s.RepositioningDistance)).Append(',').Append(F(s.RotationAmount)).Append(',')
      .Append(F(s.SwingFrequency)).Append(',').Append(F(s.HitDistance)).Append(',').Append(F(s.DistanceDiff)).Append(',').Append(F(s.SwingSpeed)).Append(',')
      .Append(F(s.Stress)).Append(',').Append(F(s.LowSpeedFalloff)).Append(',').Append(F(s.StressMultiplier)).Append(',').Append(F(s.NjsBuff)).Append(',')
      .Append(F(s.WallBuff)).Append(',').Append(F(s.SwingDiff)).Append(',').Append(F(s.SwingTech)).Append(',')
      .Append(F(s.EntryPosition.x)).Append(',').Append(F(s.EntryPosition.y)).Append(',').Append(F(s.ExitPosition.x)).Append(',').Append(F(s.ExitPosition.y)).Append(',')
      .Append(F(repeat)).Append(',').Append(F(gap)).Append(',').Append(members).Append('\n');
}

}
