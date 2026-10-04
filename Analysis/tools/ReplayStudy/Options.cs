using System.Globalization;
using System.Text;

namespace ReplayStudy
{
    public class Options
    {
        public string OutputDir = "./replay-study";
        public string MapsDir = "";
        public string LbsFile = "";
        public string ApiUrl = "https://api.beatleader.com";
        public int TopReplays = 8;        // best-ranked clean replays
        public int MidReplays = 6;        // replays spread across the rest of the leaderboard (skill strata)
        public int MinScoresForMid = 60;  // leaderboards with fewer scores get top replays only
        public int MaxMaps = 0;
        public int Concurrency = 3;
        public int Seed = 42;
        public int RequestDelayMs = 0;

        private static readonly string[] ExcludedModifiers =
            { "SS", "FS", "SF", "NF", "NA", "NB", "NO", "GN", "SC", "PM", "SA", "OD" };

        public static bool IsCleanModifiers(string modifiers)
        {
            if (string.IsNullOrEmpty(modifiers)) return true;
            var parts = modifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return !parts.Any(p => ExcludedModifiers.Contains(p));
        }

        public static Options? Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                try
                {
                    switch (args[i])
                    {
                        case "--output": o.OutputDir = Next(); break;
                        case "--maps-dir": o.MapsDir = Next(); break;
                        case "--lbs": o.LbsFile = Next(); break;
                        case "--api": o.ApiUrl = Next(); break;
                        case "--top": o.TopReplays = int.Parse(Next()); break;
                        case "--mid": o.MidReplays = int.Parse(Next()); break;
                        case "--max-maps": o.MaxMaps = int.Parse(Next()); break;
                        case "--concurrency": o.Concurrency = int.Parse(Next()); break;
                        case "--seed": o.Seed = int.Parse(Next()); break;
                        case "--delay-ms": o.RequestDelayMs = int.Parse(Next()); break;
                        default: Console.WriteLine($"Unknown argument: {args[i]}"); return null;
                    }
                }
                catch (Exception e) { Console.WriteLine($"Bad argument {args[i]}: {e.Message}"); return null; }
            }
            if (o.LbsFile == "") { Console.WriteLine("--lbs <csv: lb_id,hash,mode,difficulty> is required"); return null; }
            if (o.MapsDir == "") o.MapsDir = Path.Combine(o.OutputDir, "maps");
            return o;
        }
    }

    public static class Columns
    {
        public static readonly string[] Swings =
        {
            "lb_id", "swing_i", "beat", "seconds", "hand", "x", "y", "cut_direction",
            "is_dot", "is_chain", "note_count", "pattern_type", "njs",
            "pred_direction", "pred_forehand", "pred_parity_error", "pred_false_positive",
            "pred_frequency", "pred_ebpm", "pred_is_linear",
            "pred_angle_strain", "pred_reposition", "pred_rotation", "pred_swing_tech", "pred_hit_distance",
            "pred_swing_diff", "pred_swing_speed", "pred_stress", "pred_dist_diff", "pred_njs_buff", "pred_wall_buff", "pred_is_stream",
            "obs_n", "obs_angle_mean", "obs_angle_std", "obs_angle_error",
            "obs_transitions", "obs_alternated", "obs_same_direction", "obs_reset_frac",
            "obs_saber_speed", "obs_time_dev", "obs_cut_dir_dev",
            "obs_before_rating", "obs_after_rating", "obs_distance_to_center",
            "obs_misses", "obs_bad_cuts",
            "obs_ambiguous", "obs_trans_angle_mean", "obs_trans_angle_std",
            "frame_alternated", "frame_reset", "frame_roll",
            "obs_cut_dt_mean", "obs_intra_dt_spread",
            "obs_turnarounds_mean", "obs_min_speed_mean",
        };

        public static readonly string[] Replays =
        {
            "lb_id", "score_id", "player_id", "rank", "accuracy", "modifiers", "context", "stratum",
            "hmd", "controller", "matched_good_cuts", "total_good_cuts", "total_swings", "timepost",
            "bad_cuts", "missed_notes", "bomb_cuts", "walls_hit", "pauses", "max_combo", "fc_accuracy", "full_combo",
        };

        public static readonly string[] Leaderboards =
        {
            "lb_id", "hash", "difficulty", "mode", "bpm", "swing_count", "pass_rating", "tech_rating",
            "peak_sustained_ebpm", "pred_parity_errors", "replays_used", "replays_top", "replays_mid", "total_scores",
        };

        public static readonly string NoteObs =
            "lb_id,score_id,spawn,color,x,y,cut_dir,scoring_type,event_type,event_time,swing_i," +
            "pre,post,acc,before_r,after_r,dist,saber_speed,time_dev,cutdir_dev,angle,angle_z,tip_len,tip_peak,tip_turn,gap";
    }

    /// <summary>Append-mode CSV writer; writes the header only when creating the file.</summary>
    public class CsvWriter : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly int _columnCount;

        public CsvWriter(string path, string[] columns)
        {
            bool exists = File.Exists(path) && new FileInfo(path).Length > 0;
            _writer = new StreamWriter(path, append: true, new UTF8Encoding(false));
            _columnCount = columns.Length;
            if (!exists) _writer.WriteLine(string.Join(",", columns));
        }

        public void WriteRow(params object?[] values)
        {
            if (values.Length != _columnCount) throw new ArgumentException($"Expected {_columnCount} values, got {values.Length}");
            _writer.WriteLine(string.Join(",", values.Select(Format)));
            _writer.Flush();
        }

        public static string Format(object? value)
        {
            switch (value)
            {
                case null: return "";
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? "" : d.ToString("0.####", CultureInfo.InvariantCulture);
                case float f: return float.IsNaN(f) || float.IsInfinity(f) ? "" : f.ToString("0.####", CultureInfo.InvariantCulture);
                case bool b: return b ? "1" : "0";
                case string s:
                    if (s.Contains(',') || s.Contains('"') || s.Contains('\n')) return "\"" + s.Replace("\"", "\"\"") + "\"";
                    return s;
                default: return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            }
        }

        public void Dispose() => _writer.Dispose();
    }
}
