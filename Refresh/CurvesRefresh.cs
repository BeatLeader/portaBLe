using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using RatingAPI.Controllers;

namespace portaBLe.Refresh
{
    // Regenerates the per-leaderboard acc curves from the stored predicted acc (RatingAPI Curve.GetCurve with
    // CurveSettings.Current), without rating the maps again. Run RefreshEverything afterwards to re-score with them
    // (it also recomputes the stars, which use the curves).
    public class CurvesRefresh
    {
        public static async Task Regenerate(AppContext dbContext)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "Curve", "TEXT");
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "NoteCount", "INTEGER");
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "NoteSpan", "REAL");
            foreach (var column in new[] { "SSCurve", "FSCurve", "SFCurve" })
            {
                await DbSchema.AddColumnIfMissing(dbContext, "ModifiersRating", column, "TEXT");
            }

            Console.WriteLine($"Regenerating acc curves ({FormattableString.Invariant($"A and C by predicted acc: {string.Join(", ", CurveSettings.Current.Table.Select(r => $"{r.PredictedAcc * 100}%: a {r.A}, c {r.C}"))}")})");
            var lbs = await dbContext.Leaderboards.Include(lb => lb.ModifiersRating).ToListAsync();
            var generator = new global::RatingAPI.Controllers.Curve();
            // Besides the predicted acc, the curve uses the slider share (CurveSettings.SliderTilt) and the note count
            // and span (low note nerf, CurveSettings.LowNoteStart), read from the maps folder only when those are enabled
            var settings = CurveSettings.Current;
            var maps = settings.SliderTilt != 0 || settings.LowNoteStart > 0 ? await MapCounts(lbs) : new Dictionary<string, (double, int, double)>();

            foreach (var lb in lbs)
            {
                var (sliders, notes, span) = maps.GetValueOrDefault(lb.Id);
                if (notes > 0) lb.NoteCount = notes;
                if (span > 0) lb.NoteSpan = (float)span;
                LackMapCalculation Lack(double speed) => new() { SliderPercentage = sliders, NoteCount = notes, NoteSpan = span / speed };
                lb.Curve = ReplayUtils.ToCurve(generator.GetCurve(Lack(1), lb.PredictedAcc));
                var mod = lb.ModifiersRating;
                if (mod != null)
                {
                    mod.SSCurve = ReplayUtils.ToCurve(generator.GetCurve(Lack(0.85), mod.SSPredictedAcc));
                    mod.FSCurve = ReplayUtils.ToCurve(generator.GetCurve(Lack(1.2), mod.FSPredictedAcc));
                    mod.SFCurve = ReplayUtils.ToCurve(generator.GetCurve(Lack(1.5), mod.SFPredictedAcc));
                }
            }

            Console.WriteLine($"Saving {lbs.Count} curves...");
            // The importer and refresh steps can leave automatic change detection off; BulkSaveChanges then saves nothing
            dbContext.ChangeTracker.DetectChanges();
            dbContext.BulkSaveChanges();
            Console.WriteLine($"Curves regenerated in {timer.Elapsed:mm\\:ss}");
        }

        // Every leaderboard's slider share (CurveSettings.SliderTilt's input) to a CSV (id,sliders,notes), from the maps folder.
        // Read only (portaBLe slidershare)
        public static async Task ExportSliderShares(AppContext dbContext, string path)
        {
            var lbs = await dbContext.Leaderboards.AsNoTracking().ToListAsync();
            var maps = await MapCounts(lbs);
            await File.WriteAllLinesAsync(path, new[] { "id,sliders,notes" }.Concat(maps.Select(m =>
                FormattableString.Invariant($"{m.Key},{m.Value.sliders},{m.Value.notes}"))));
            Console.WriteLine($"Slider shares: {maps.Count} leaderboards to {path}");
        }

        // Slider share, color note count and note span (seconds, no modifier) of every leaderboard, from the maps folder
        // (downloaded when missing)
        private static async Task<Dictionary<string, (double sliders, int notes, double span)>> MapCounts(List<Leaderboard> lbs)
        {
            var result = new System.Collections.Concurrent.ConcurrentDictionary<string, (double, int, double)>();
            var downloader = new Downloader("maps");
            await Parallel.ForEachAsync(lbs, new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount }, async (lb, ct) =>
            {
                try
                {
                    var mapPath = await downloader.MapAsync(lb.Hash);
                    if (mapPath == null) return;
                    var mapset = new beatleader_parser.Parse().TryLoadPath(mapPath);
                    int diffCode = RatingsRefresh.GetDiffCode(lb.DifficultyName);
                    var set = mapset?.Difficulties.FirstOrDefault(d => d.Characteristic == lb.ModeName
                        && (d.BeatMap._difficultyRank == diffCode || d.BeatMap._difficulty == lb.DifficultyName));
                    if (set == null) return;
                    var ratings = new beatleader_analyzer.Analyze().GetRating(set.Data, lb.ModeName, lb.DifficultyName, (float)mapset!.Info._beatsPerMinute);
                    result[lb.Id] = (ratings != null ? global::RatingAPI.Controllers.Curve.SliderPercentage(ratings) : 0, set.Data.Notes.Count, RatingsController.NoteSpan(set.Data));
                }
                catch { }
            });
            return new Dictionary<string, (double, int, double)>(result);
        }

        // Color note count and note span of every leaderboard (Leaderboard.NoteCount / NoteSpan) from the maps folder (downloaded
        // when missing), without rating the maps again (Overwrite and Regenerate store them too)
        public static async Task RefreshNoteCounts(AppContext dbContext)
        {
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "NoteCount", "INTEGER");
            await DbSchema.AddColumnIfMissing(dbContext, "Leaderboards", "NoteSpan", "REAL");
            var lbs = await dbContext.Leaderboards.AsNoTracking()
                .Select(l => new Leaderboard { Id = l.Id, Hash = l.Hash, ModeName = l.ModeName, DifficultyName = l.DifficultyName, NoteCount = l.NoteCount, NoteSpan = l.NoteSpan })
                .ToListAsync();
            var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, (int Notes, float Span)>();
            var downloader = new Downloader("maps");
            await Parallel.ForEachAsync(lbs, new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount }, async (lb, ct) =>
            {
                try
                {
                    var mapPath = await downloader.MapAsync(lb.Hash);
                    if (mapPath == null) return;
                    var mapset = new beatleader_parser.Parse().TryLoadPath(mapPath);
                    int diffCode = RatingsRefresh.GetDiffCode(lb.DifficultyName);
                    var set = mapset?.Difficulties.FirstOrDefault(d => d.Characteristic == lb.ModeName
                        && (d.BeatMap._difficultyRank == diffCode || d.BeatMap._difficulty == lb.DifficultyName));
                    if (set != null) counts[lb.Id] = (set.Data.Notes.Count, (float)RatingsController.NoteSpan(set.Data));
                }
                catch { }
            });
            var updates = lbs.Where(lb => counts.TryGetValue(lb.Id, out var n) && (n.Notes != lb.NoteCount || n.Span != lb.NoteSpan))
                .Select(lb => new Leaderboard { Id = lb.Id, NoteCount = counts[lb.Id].Notes, NoteSpan = counts[lb.Id].Span })
                .ToList();
            if (updates.Count > 0)
            {
                await dbContext.BulkUpdateAsync(updates, options => options.ColumnInputExpression = c => new { c.NoteCount, c.NoteSpan });
            }
            Console.WriteLine($"Note counts: {updates.Count} leaderboards updated, {lbs.Count - counts.Count} not found");
        }
    }
}
