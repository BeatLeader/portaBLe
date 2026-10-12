using Microsoft.AspNetCore.Mvc.RazorPages;
using portaBLe.DB;
using portaBLe.Refresh;
using System.Text.Json;

namespace portaBLe.Pages
{
    // Shows the Acc AI training dataset saved by AccAIDatasetRefresh (wwwroot/accai-dataset.json).
    // Nothing is fetched here; the file is only rebuilt when AccAIDatasetRefresh.Refresh is called from Program.Main.
    public class AccAIDatasetModel : PageModel
    {
        private readonly IWebHostEnvironment _env;

        public AccAIDatasetModel(IWebHostEnvironment env)
        {
            _env = env;
        }

        public record Bucket(string Label, int Count);
        public record NamedCount(string Name, int Count);

        // Filters used when the replays were downloaded (BLReplaysPrepare), checked against the data below.
        public const float MaxSpread = 0.005f;
        public const int MinReplays = 8;
        public const float MinTopAccuracy = 0.93f;

        public static readonly string[] DifficultyNames = { "Easy", "Normal", "Hard", "Expert", "ExpertPlus" };

        public AccAIDataset? Dataset { get; set; }
        public List<AccAIDatasetLeaderboard> Leaderboards { get; set; } = new();

        public int SongCount { get; set; }
        public int ReplayFileCount { get; set; }
        public List<NamedCount> DifficultyCounts { get; set; } = new();
        public List<NamedCount> CharacteristicCounts { get; set; } = new();

        public int ScoreSaberRanked { get; set; }
        public int BeatLeaderRanked { get; set; }
        public int AccSaberRanked { get; set; }
        public int AnyRanked { get; set; }
        public int NotRanked { get; set; }
        public List<NamedCount> AccSaberCategories { get; set; } = new();

        public List<Bucket> PassBuckets { get; set; } = new();
        public List<Bucket> TechBuckets { get; set; } = new();
        public int RatingsComputed { get; set; }

        // Checks
        public List<AccAIDatasetLeaderboard> SpreadTooWide { get; set; } = new();
        public List<AccAIDatasetLeaderboard> TooFewReplays { get; set; } = new();
        public List<AccAIDatasetLeaderboard> TopTooLow { get; set; } = new();
        public List<AccAIDatasetLeaderboard> NjsMismatch { get; set; } = new();
        public List<AccAIDatasetLeaderboard> NotOnBeatLeader { get; set; } = new();
        public List<AccAIDatasetLeaderboard> NotOnBeatSaver { get; set; } = new();
        public int OstCount { get; set; }
        public List<AccAIDatasetLeaderboard> RatingFailed { get; set; } = new();
        public List<AccAIDatasetLeaderboard> UnreadableFiles { get; set; } = new();
        public List<AccAIDatasetLeaderboard> RankedMismatch { get; set; } = new();

        public double? AIAccMae { get; set; }
        public double? AIAccBias { get; set; }
        public List<AccAIDatasetLeaderboard> LargestAIAccErrors { get; set; } = new();

        public List<NamedCount> TopSongs { get; set; } = new();
        public List<NamedCount> TopMappers { get; set; } = new();

        public string ChartDataJson { get; set; } = "{}";
        // The replay histogram groups everything from this count up into the last bar, so a few maps with 200+ replays
        // don't squash the rest of the chart.
        public int ReplayChartCap { get; set; }

        public void OnGet()
        {
            Dataset = AccAIDatasetRefresh.Load(_env.WebRootPath);
            if (Dataset == null) return;

            Leaderboards = Dataset.Leaderboards;
            var lbs = Leaderboards;

            SongCount = lbs.Select(l => string.IsNullOrEmpty(l.Hash) ? l.BeatSaverKey : l.Hash.ToLowerInvariant()).Distinct().Count();
            ReplayFileCount = lbs.Sum(l => l.ReplayCount);

            DifficultyCounts = DifficultyNames
                .Select(d => new NamedCount(d, lbs.Count(l => DifficultyOf(l) == d)))
                .ToList();
            int otherDifficulties = lbs.Count(l => !DifficultyNames.Contains(DifficultyOf(l)));
            if (otherDifficulties > 0) DifficultyCounts.Add(new NamedCount("Unknown", otherDifficulties));

            CharacteristicCounts = lbs.GroupBy(CharacteristicOf)
                .Select(g => new NamedCount(g.Key, g.Count()))
                .OrderByDescending(c => c.Count)
                .ToList();

            ScoreSaberRanked = lbs.Count(l => l.ScoreSaberRanked);
            BeatLeaderRanked = lbs.Count(l => l.BeatLeaderRanked);
            AccSaberRanked = lbs.Count(l => l.AccSaberRanked);
            AnyRanked = lbs.Count(l => l.AnyRanked);
            NotRanked = lbs.Count - AnyRanked;
            AccSaberCategories = lbs.Where(l => l.AccSaberRanked)
                .GroupBy(l => string.IsNullOrEmpty(l.AccSaberCategory) ? "unknown" : l.AccSaberCategory)
                .Select(g => new NamedCount(g.Key, g.Count()))
                .OrderByDescending(c => c.Count)
                .ToList();

            var rated = lbs.Where(l => l.RatingComputed).ToList();
            RatingsComputed = rated.Count;
            PassBuckets = MakeBuckets(rated.Select(l => l.PassRating));
            TechBuckets = MakeBuckets(rated.Select(l => l.TechRating));

            SpreadTooWide = lbs.Where(l => l.AccuracySpread > MaxSpread + 1e-6f).OrderByDescending(l => l.AccuracySpread).ToList();
            TooFewReplays = lbs.Where(l => l.ReplayCount < MinReplays).OrderBy(l => l.ReplayCount).ToList();
            TopTooLow = lbs.Where(l => l.TopAccuracy < MinTopAccuracy).OrderBy(l => l.TopAccuracy).ToList();
            NjsMismatch = lbs.Where(l => l.FoundOnBeatLeader && Math.Abs(l.FileNjs - l.Njs) > 0.01f).ToList();
            NotOnBeatLeader = lbs.Where(l => !l.FoundOnBeatLeader).ToList();
            NotOnBeatSaver = lbs.Where(l => !l.FoundOnBeatSaver && !l.IsOst).ToList();
            OstCount = lbs.Count(l => l.IsOst);
            RatingFailed = lbs.Where(l => !l.RatingComputed).ToList();
            UnreadableFiles = lbs.Where(l => l.UnreadableFiles > 0).ToList();
            // BeatLeader says ranked but BeatSaver's copy of the BeatLeader status disagrees (or the other way round)
            RankedMismatch = lbs.Where(l => l.FoundOnBeatSaver && l.FoundOnBeatLeader && l.BeatLeaderRanked != l.BeatSaverBeatLeaderRanked).ToList();

            if (rated.Count > 0)
            {
                AIAccMae = rated.Average(l => Math.Abs(l.AIAccDifference ?? 0)) * 100;
                AIAccBias = rated.Average(l => l.AIAccDifference ?? 0) * 100;
                LargestAIAccErrors = rated.OrderByDescending(l => Math.Abs(l.AIAccDifference ?? 0)).Take(15).ToList();
            }

            TopSongs = lbs.GroupBy(l => string.IsNullOrEmpty(l.Name) ? l.BeatSaverKey : $"{l.Name} ({l.BeatSaverKey})")
                .Select(g => new NamedCount(g.Key, g.Count()))
                .OrderByDescending(c => c.Count).ThenBy(c => c.Name)
                .Take(10)
                .ToList();
            TopMappers = lbs.Where(l => !string.IsNullOrEmpty(l.Mapper))
                .GroupBy(l => l.Mapper)
                .Select(g => new NamedCount(g.Key, g.Count()))
                .OrderByDescending(c => c.Count).ThenBy(c => c.Name)
                .Take(10)
                .ToList();

            var sortedCounts = lbs.Select(l => l.ReplayCount).OrderBy(c => c).ToList();
            ReplayChartCap = sortedCounts.Count == 0 ? 20 : Math.Max(20, sortedCounts[(int)Math.Floor((sortedCounts.Count - 1) * 0.98)]);

            ChartDataJson = JsonSerializer.Serialize(new
            {
                stars = rated.Select(l => Math.Round(l.Stars, 3)),
                blStars = lbs.Where(l => l.BeatLeaderRanked && l.BeatLeaderStars != null).Select(l => Math.Round(l.BeatLeaderStars!.Value, 3)),
                replayCounts = lbs.Select(l => Math.Min(l.ReplayCount, ReplayChartCap)),
                replayCap = ReplayChartCap,
                replayAboveCap = lbs.Count(l => l.ReplayCount >= ReplayChartCap),
                replayMax = sortedCounts.Count == 0 ? 0 : sortedCounts[^1],
                averageAccuracy = lbs.Select(l => Math.Round(l.AverageAccuracy * 100, 3)),
                njs = lbs.Where(l => l.FoundOnBeatLeader).Select(l => l.Njs),
                aiPoints = rated.Select(l => new
                {
                    id = l.Id,
                    name = l.Name,
                    predicted = Math.Round(l.PredictedAcc * 100, 3),
                    actual = Math.Round(l.AverageAccuracy * 100, 3),
                    stars = Math.Round(l.Stars, 2),
                }),
            });
        }

        public static string DifficultyOf(AccAIDatasetLeaderboard lb)
        {
            if (!string.IsNullOrEmpty(lb.Difficulty)) return lb.Difficulty;
            // BeatLeader id: ...<difficulty digit><characteristic digit>
            if (lb.Id.Length < 2) return "";
            return lb.Id[^2] switch
            {
                '1' => "Easy",
                '3' => "Normal",
                '5' => "Hard",
                '7' => "Expert",
                '9' => "ExpertPlus",
                _ => "",
            };
        }

        public static string CharacteristicOf(AccAIDatasetLeaderboard lb)
        {
            if (!string.IsNullOrEmpty(lb.Characteristic)) return lb.Characteristic;
            if (lb.Id.Length < 1) return "Unknown";
            return lb.Id[^1] switch
            {
                '1' => "Standard",
                '2' => "OneSaber",
                '3' => "NoArrows",
                '4' => "90Degree",
                '5' => "360Degree",
                '6' => "Lightshow",
                '7' => "Lawless",
                _ => "Unknown",
            };
        }

        private static List<Bucket> MakeBuckets(IEnumerable<float> values)
        {
            var list = values.ToList();
            var buckets = new List<Bucket>();
            for (int low = 0; low < 20; low += 2)
            {
                int high = low + 2;
                buckets.Add(new Bucket($"{low} to {high}", list.Count(v => v >= low && v < high)));
            }
            int above = list.Count(v => v >= 20);
            if (above > 0) buckets.Add(new Bucket("20+", above));
            int negative = list.Count(v => v < 0);
            if (negative > 0) buckets.Insert(0, new Bucket("Below 0", negative));
            return buckets;
        }

        public static string Percent(float value) => (value * 100).ToString("F2") + "%";
    }
}
