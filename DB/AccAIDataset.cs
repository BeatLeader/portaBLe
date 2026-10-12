using System.Text.Json.Serialization;

namespace portaBLe.DB
{
    // One leaderboard folder in beatsaber-replays-ai-2/replays: the valid replay data used to train the Acc AI.
    public class ReplayFolderInfo
    {
        public string LeaderboardId { get; set; } = "";
        public int ReplayCount { get; set; }
        // Accuracy of each replay file (from "playerid-accuracy-score-njs.npy"), highest first.
        public List<float> Accuracies { get; set; } = new();
        // NJS written in the file names (0 when the downloader didn't know it).
        public float FileNjs { get; set; }
        public int UnreadableFiles { get; set; }
    }

    // Saved to wwwroot/accai-dataset.json by AccAIDatasetRefresh and shown on the Acc AI Dataset page.
    public class AccAIDataset
    {
        public DateTime GeneratedAt { get; set; }
        public string ReplaysPath { get; set; } = "";
        public int AccSaberRankedListSize { get; set; }
        public List<AccAIDatasetLeaderboard> Leaderboards { get; set; } = new();
    }

    public class AccAIDatasetLeaderboard
    {
        // From the folder
        public string Id { get; set; } = "";
        public string BeatSaverKey { get; set; } = "";
        public int ReplayCount { get; set; }
        public List<float> Accuracies { get; set; } = new();
        public float TopAccuracy { get; set; }
        public float AverageAccuracy { get; set; }
        public float LowestAccuracy { get; set; }
        public float FileNjs { get; set; }
        public int UnreadableFiles { get; set; }

        // From BeatLeader
        public bool FoundOnBeatLeader { get; set; }
        public string Hash { get; set; } = "";
        public string Name { get; set; } = "";
        public string SubName { get; set; } = "";
        public string Author { get; set; } = "";
        public string Mapper { get; set; } = "";
        public string Difficulty { get; set; } = "";
        public string Characteristic { get; set; } = "";
        public float Njs { get; set; }
        public float Nps { get; set; }
        public int Notes { get; set; }
        public int Chains { get; set; }
        public int Arcs { get; set; }
        public float Duration { get; set; }
        public int Plays { get; set; }
        // 0 unranked, 1 nominated, 2 qualified, 3 ranked, 4 unrankable, 5 outdated, 6 in event
        public int BeatLeaderStatus { get; set; }
        public float? BeatLeaderStars { get; set; }
        public float? BeatLeaderPassRating { get; set; }
        public float? BeatLeaderTechRating { get; set; }
        public float? BeatLeaderAccRating { get; set; }
        public float? BeatLeaderPredictedAcc { get; set; }

        // From BeatSaver
        public bool FoundOnBeatSaver { get; set; }
        public bool ScoreSaberRanked { get; set; }
        public float? ScoreSaberStars { get; set; }
        public bool BeatSaverBeatLeaderRanked { get; set; }

        // From AccSaber
        public bool AccSaberRanked { get; set; }
        public string AccSaberCategory { get; set; } = "";
        public double? AccSaberComplexity { get; set; }

        // Computed locally with RatingAPI (no modifiers), available for ranked and unranked maps alike
        public bool RatingComputed { get; set; }
        public string RatingError { get; set; } = "";
        public float PassRating { get; set; }
        public float TechRating { get; set; }
        public float AccRating { get; set; }
        public float Stars { get; set; }
        public float PredictedAcc { get; set; }

        [JsonIgnore] public bool BeatLeaderRanked => BeatLeaderStatus == 3;
        // OST/DLC songs: BeatLeader uses the level id ("100Bills") as the hash instead of a 40-character hex hash,
        // so they don't exist on BeatSaver.
        [JsonIgnore] public bool IsOst => !string.IsNullOrEmpty(Hash) ? !IsCustomHash(Hash) : !BeatSaverKey.All(Uri.IsHexDigit);
        public static bool IsCustomHash(string hash) => hash.Length == 40 && hash.All(Uri.IsHexDigit);
        [JsonIgnore] public bool AnyRanked => BeatLeaderRanked || ScoreSaberRanked || AccSaberRanked;
        [JsonIgnore] public float AccuracySpread => TopAccuracy - LowestAccuracy;
        // Difference between the dataset's average accuracy and RatingAPI's predicted accuracy (positive: AI predicts too low)
        [JsonIgnore] public float? AIAccDifference => RatingComputed ? AverageAccuracy - PredictedAcc : null;
    }
}
