using Analyzer.BeatmapScanner.Data;
using beatleader_analyzer;
using beatleader_parser;
using ReplayStudy;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;

// ReplayStudy: for a list of leaderboards, runs the analyzer, downloads a skill-stratified sample of replays
// (in memory, nothing stored), and writes
//   swings.csv        per predicted swing: analyzer features + observed aggregates (SwingCorpus-compatible)
//   notes_obs.csv.gz  per note event per replay: cut data, scores, tip-motion metrics, matched swing index
//   replays.csv       provenance (player, rank, accuracy, stratum) of every replay used
//   leaderboards.csv  per-map summary
// Resumable (done.txt) and processed in a seeded random order, so ANY prefix of the run is a random sample.

var options = Options.Parse(args);
if (options == null) return 1;
Directory.CreateDirectory(options.OutputDir);

var api = new BeatLeaderApi(options.ApiUrl);
var mapCache = new MapCache(options.MapsDir);
var parser = new Parse();
#if !CORPUS
var analyzer = new Analyze();
#endif

var donePath = Path.Combine(options.OutputDir, "done.txt");
var doneSet = File.Exists(donePath)
    ? new HashSet<string>(File.ReadAllLines(donePath).Select(l => l.Split(',')[0]).Where(l => l.Length > 0))
    : new HashSet<string>();
var errorLog = Path.Combine(options.OutputDir, "errors.log");

var lbs = File.ReadLines(options.LbsFile).Skip(1).Select(l => l.Split(',')).Where(c => c.Length >= 4)
    .Select(c => (id: c[0], hash: c[1], mode: c[2], diff: c[3])).ToList();
var rng = new Random(options.Seed);
lbs = lbs.OrderBy(_ => rng.Next()).ToList(); // seeded shuffle: any prefix is a random sample

// attempts mode: the replays to study come from a CSV (lb_id,attempt_id,player_id,type,time,url), stratum = attempt type
Dictionary<string, List<ScoreInfo>>? attemptsByLb = null;
if (options.AttemptsFile != "")
{
    attemptsByLb = File.ReadLines(options.AttemptsFile).Skip(1).Select(l => l.Split(',', 6)).Where(c => c.Length == 6)
        .Select(c => (lb: c[0], s: new ScoreInfo { Id = int.Parse(c[1]), PlayerId = c[2], Stratum = c[3], Replay = c[5].Trim('"'),
            Timepost = (int)Math.Round(double.Parse(c[4], CultureInfo.InvariantCulture)) }))
        .GroupBy(x => x.lb).ToDictionary(g => g.Key, g => g.Select(x => x.s).ToList());
    lbs = lbs.Where(l => attemptsByLb.ContainsKey(l.id)).ToList();
    Console.WriteLine($"attempts mode: {attemptsByLb.Values.Sum(v => v.Count)} replays on {lbs.Count} leaderboards");
}
Console.WriteLine($"{lbs.Count} leaderboards listed, {doneSet.Count} already done, top={options.TopReplays} mid={options.MidReplays}, api={options.ApiUrl}");

var swingsCsv = new CsvWriter(Path.Combine(options.OutputDir, "swings.csv"), Columns.Swings);
var replaysCsv = new CsvWriter(Path.Combine(options.OutputDir, "replays.csv"), Columns.Replays);
var leaderboardsCsv = new CsvWriter(Path.Combine(options.OutputDir, "leaderboards.csv"), Columns.Leaderboards);
var notesPath = Path.Combine(options.OutputDir, "notes_obs.csv.gz");

var sw = Stopwatch.StartNew();
int processed = 0, okCount = 0, replaysTotal = 0;
long bytesTotal = 0;

foreach (var lb in lbs)
{
    if (options.MaxMaps > 0 && processed >= options.MaxMaps) break;
    if (doneSet.Contains(lb.id)) continue;

    string status;
    try { status = await ProcessLeaderboard(lb); }
    catch (Exception e)
    {
        LogError(lb.id, e.ToString().Replace("\n", " | "));
        status = "error";
    }
    File.AppendAllText(donePath, $"{lb.id},{status}\n");
    doneSet.Add(lb.id);
    processed++;
    if (status == "ok") okCount++;
    if (processed % 10 == 0)
    {
        var rss = Process.GetCurrentProcess().WorkingSet64 / 1048576;
        Console.WriteLine($"[{processed}] ok={okCount} replays={replaysTotal} {bytesTotal / 1048576}MB dl, {sw.Elapsed.TotalMinutes:0.0} min, " +
            $"{replaysTotal / Math.Max(1, sw.Elapsed.TotalSeconds):0.00} replays/s, rss={rss}MB, gc={GC.GetTotalMemory(false) / 1048576}MB");
    }
}

swingsCsv.Dispose(); replaysCsv.Dispose(); leaderboardsCsv.Dispose();
Console.WriteLine($"Finished. Processed {processed} leaderboards ({okCount} ok), {replaysTotal} replays.");
return 0;

async Task<string> ProcessLeaderboard((string id, string hash, string mode, string diff) lb)
{
    // 1. Map + analyzer
    var mapPath = await mapCache.Map(lb.hash);
    if (mapPath == null) { LogError(lb.id, "map download failed"); return "map_missing"; }

    var mapset = parser.TryLoadPath(mapPath);
    if (mapset == null) { LogError(lb.id, "map parse failed"); return "map_parse_failed"; }

    var diff = mapset.Difficulties.FirstOrDefault(d => d.Characteristic == lb.mode && d.Difficulty == lb.diff);
    if (diff == null) { LogError(lb.id, $"difficulty {lb.mode}/{lb.diff} not found"); return "diff_not_found"; }

    float bpm = mapset.Info._beatsPerMinute;
#if CORPUS
    var ratings = Analyze.GetRating(diff.Data, lb.mode, lb.diff, bpm);
#else
    var ratings = analyzer.GetRating(diff.Data, lb.mode, lb.diff, bpm);
#endif
    if (ratings == null || ratings.SwingData.Count == 0) { LogError(lb.id, "analyzer returned no swings"); return "no_swings"; }
    var swings = ratings.SwingData;

    // 2. Pick replays: top of the leaderboard + spread over the rest (skill strata)
    var (selected, totalScores) = attemptsByLb != null ? (attemptsByLb[lb.id], attemptsByLb[lb.id].Count) : await SelectScores(lb.id);
    if (selected.Count == 0) { LogError(lb.id, "no scores / no clean replays"); return "no_scores"; }

    // 3. Download + decode (bounded concurrency), match to swings
    var matcher = new ReplayMatcher(swings);
    var usable = new List<(ScoreInfo score, ReplayDecoder.Replay replay)>();
    var semaphore = new SemaphoreSlim(options.Concurrency);
    var downloads = selected.Select(async s =>
    {
        await semaphore.WaitAsync();
        try
        {
            if (string.IsNullOrEmpty(s.Replay)) return;
            var bytes = await api.DownloadReplay(s.Replay);
            if (bytes == null) return;
            Interlocked.Add(ref bytesTotal, bytes.Length);
            var (replay, _) = ReplayDecoder.ReplayDecoder.Decode(bytes);
            if (replay == null || replay.info.leftHanded || replay.info.startTime > 0) return;
            lock (usable) usable.Add((s, replay));
        }
        catch (Exception) { }
        finally { semaphore.Release(); }
    });
    await Task.WhenAll(downloads);
    if (usable.Count == 0) { LogError(lb.id, "no usable replays"); return "no_usable_replays"; }
    usable = usable.OrderBy(u => u.score.Rank).ToList();

    var noteSb = new StringBuilder();
    foreach (var (score, replay) in usable)
    {
        var match = matcher.ProcessReplay(replay);
        replaysCsv.WriteRow(lb.id, score.Id, score.PlayerId, score.Rank, score.Accuracy, score.Modifiers, score.Context, score.Stratum,
            replay.info.hmd, replay.info.controller, match.MatchedGoodCuts, match.TotalGoodCuts, swings.Count, score.Timepost,
            score.BadCuts, score.MissedNotes, score.BombCuts, score.WallsHit, score.Pauses, score.MaxCombo, score.FcAccuracy, score.FullCombo);
        foreach (var r in match.Rows) AppendNoteRow(noteSb, lb.id, score.Id, r);
    }
    AppendGz(notesPath, noteSb.ToString(), header: Columns.NoteObs);
    replaysTotal += usable.Count;

    // 4. Per-swing rows
    for (int i = 0; i < swings.Count; i++)
    {
        var s = swings[i];
        var head = s.Cubes[0];
        var obs = matcher.Observations[i];

        double? obsAngleMean = obs.CutAngles.Count > 0 ? ReplayMatcher.CircularMean(obs.CutAngles) : null;
        double? obsAngleStd = obs.CutAngles.Count > 0 ? ReplayMatcher.CircularStd(obs.CutAngles) : null;
        double? angleError = obsAngleMean != null ? ReplayMatcher.AngleDifference(obsAngleMean.Value, s.Direction) : null;
        int transitions = obs.AlternatedCount + obs.AmbiguousCount + obs.SameDirectionCount;
        double? transAngleMean = obs.TransitionAngles.Count > 0 ? obs.TransitionAngles.Average() : null;
        double? transAngleStd = obs.TransitionAngles.Count > 1
            ? Math.Sqrt(obs.TransitionAngles.Sum(a => (a - transAngleMean!.Value) * (a - transAngleMean.Value)) / (obs.TransitionAngles.Count - 1))
            : null;

        swingsCsv.WriteRow(
            lb.id, i, head.BpmTime, head.Seconds, head.Type, head.X, head.Y, head.CutDirection,
            head.CutDirection == 8 ? 1 : 0, head.Chain ? 1 : 0, s.Cubes.Count, s.PatternType, head.Njs,
            s.Direction, s.Forehand ? 1 : 0, s.ParityErrors ? 1 : 0, s.FalsePositive ? 1 : 0,
            s.SwingFrequency, s.SwingFrequency * 30.0, s.IsLinear ? 1 : 0,
            s.AngleStrain, s.RepositioningDistance, s.RotationAmount, s.SwingTech, s.HitDistance,
            s.SwingDiff, s.SwingSpeed, s.Stress, s.DistanceDiff, s.NjsBuff, s.WallBuff, s.IsStream ? 1 : 0,
            obs.CutAngles.Count, obsAngleMean, obsAngleStd, angleError,
            transitions, obs.AlternatedCount, obs.SameDirectionCount,
            transitions > 0 ? (double)obs.SameDirectionCount / transitions : 0,
            Mean(obs.SaberSpeeds), Mean(obs.TimeDeviations), Mean(obs.CutDirDeviations),
            Mean(obs.BeforeCutRatings), Mean(obs.AfterCutRatings), Mean(obs.DistancesToCenter),
            obs.MissCount, obs.BadCutCount,
            obs.AmbiguousCount, transAngleMean, transAngleStd,
            obs.FrameAlternated, obs.FrameReset, obs.FrameRoll,
            Mean(obs.CutGaps), Mean(obs.IntraSwingSpread),
            Mean(obs.TurnaroundCounts), Mean(obs.MinSpeeds));
    }

    leaderboardsCsv.WriteRow(lb.id, lb.hash, lb.diff, lb.mode, bpm, swings.Count, ratings.PassRating, ratings.TechRating,
        ratings.PeakSustainedEBPM, ratings.Statistics.ParityErrors, usable.Count,
        usable.Count(u => u.score.Stratum == "top"), usable.Count(u => u.score.Stratum.StartsWith("mid")), totalScores);
    return "ok";
}

/// <summary>Top clean replays, plus MidReplays spread between the 12th and 88th score percentile.</summary>
async Task<(List<ScoreInfo> selected, int total)> SelectScores(string lbId)
{
    int fetchCount = Math.Min(100, options.TopReplays * 3);
    var (general, total) = await api.GetScores(lbId, "General", fetchCount, 1);
    if (general.Count == 0) return (new(), 0);

    var clean = general.Where(s => Options.IsCleanModifiers(s.Modifiers)).ToList();
    foreach (var s in clean) s.Stratum = "top";
    var picked = clean.Take(options.TopReplays).ToList();

    // players who used speed modifiers in General: use their no-mods score instead
    if (picked.Count < options.TopReplays)
    {
        var speedyPlayerIds = general.Where(s => !Options.IsCleanModifiers(s.Modifiers)).Select(s => s.PlayerId).ToHashSet();
        if (speedyPlayerIds.Count > 0)
        {
            var (noMods, _) = await api.GetScores(lbId, "NoMods", fetchCount, 1);
            var have = picked.Select(s => s.PlayerId).ToHashSet();
            foreach (var s in noMods.Where(s => speedyPlayerIds.Contains(s.PlayerId) && !have.Contains(s.PlayerId) && Options.IsCleanModifiers(s.Modifiers)))
            {
                if (picked.Count >= options.TopReplays) break;
                s.Stratum = "top"; s.Context = "nomods";
                picked.Add(s);
            }
        }
    }

    if (options.MidReplays > 0 && total >= options.MinScoresForMid)
    {
        int minRank = fetchCount + 1;
        var havePlayers = picked.Select(s => s.PlayerId).ToHashSet();
        for (int k = 0; k < options.MidReplays; k++)
        {
            double pct = options.MidReplays == 1 ? 0.5 : 0.12 + 0.76 * k / (options.MidReplays - 1);
            int target = Math.Max(minRank, (int)Math.Round(pct * total));
            if (target > total) continue;
            const int pageSize = 5;
            int page = (target - 1) / pageSize + 1;
            var (pageScores, _) = await api.GetScores(lbId, "General", pageSize, page);
            var pick = pageScores
                .Where(s => Options.IsCleanModifiers(s.Modifiers) && !havePlayers.Contains(s.PlayerId))
                .OrderBy(s => Math.Abs(s.Rank - target)).FirstOrDefault();
            if (pick == null) continue;
            pick.Stratum = $"mid{(int)Math.Round(100.0 * pick.Rank / total):D2}";
            havePlayers.Add(pick.PlayerId);
            picked.Add(pick);
            if (options.RequestDelayMs > 0) await Task.Delay(options.RequestDelayMs);
        }
    }
    return (picked, total);
}

static void AppendNoteRow(StringBuilder sb, string lbId, int scoreId, NoteObsRow r)
{
    static string F(float v, string f = "0.####") => float.IsNaN(v) ? "" : v.ToString(f, CultureInfo.InvariantCulture);
    sb.Append(lbId).Append(',').Append(scoreId).Append(',').Append(F(r.Spawn, "0.###")).Append(',').Append(r.Color).Append(',')
      .Append(r.X).Append(',').Append(r.Y).Append(',').Append(r.CutDir).Append(',').Append(r.ScoringType).Append(',')
      .Append(r.EventType).Append(',').Append(F(r.EventTime, "0.###")).Append(',').Append(r.SwingIndex).Append(',');
    if (r.HasCut)
    {
        sb.Append(r.Pre).Append(',').Append(r.Post).Append(',').Append(r.Acc).Append(',').Append(F(r.BeforeR, "0.###")).Append(',')
          .Append(F(r.AfterR, "0.###")).Append(',').Append(F(r.Dist, "0.####")).Append(',').Append(F(r.SaberSpeed, "0.##")).Append(',')
          .Append(F(r.TimeDev, "0.####")).Append(',').Append(F(r.CutDirDev, "0.##")).Append(',').Append(F(r.Angle, "0.#")).Append(',')
          .Append(F(r.AngleZ, "0.###")).Append(',').Append(F(r.TipLen, "0.###")).Append(',').Append(F(r.TipPeak, "0.##")).Append(',')
          .Append(r.TipTurn < 0 ? "" : r.TipTurn.ToString()).Append(',').Append(F(r.Gap, "0.###"));
    }
    else sb.Append(",,,,,,,,,,,,,,");
    sb.Append(',').Append(r.Bad).Append('\n');
}

// Each leaderboard is appended as its own gzip member (concatenated members form a valid .gz stream),
// so a killed run never leaves a corrupt file and nothing is held in memory across maps.
static void AppendGz(string path, string text, string header)
{
    bool exists = File.Exists(path) && new FileInfo(path).Length > 0;
    using var fs = new FileStream(path, FileMode.Append, FileAccess.Write);
    using var gz = new GZipStream(fs, CompressionLevel.Optimal);
    using var w = new StreamWriter(gz, new UTF8Encoding(false));
    if (!exists) w.Write(header + "\n");
    w.Write(text);
}

void LogError(string lbId, string message)
{
    Console.WriteLine($"    ERROR {lbId}: {message}");
    File.AppendAllText(errorLog, $"{DateTime.UtcNow:O} {lbId}: {message}\n");
}

static double? Mean(List<double> values) => values.Count > 0 ? values.Average() : null;
