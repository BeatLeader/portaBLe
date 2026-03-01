using System.Text.Json.Serialization;

namespace portaBLe.MapRecommendation.Ranked
{
    public class Top10kLeaderboardMeta
    {
        public string leaderboardID { get; set; }
        public double count { get; set; } = 0;
        public double totalRank { get; set; } = 0;
        public double maxScore { get; set; } = 0;
        public double minScore { get; set; } = double.MaxValue;

        //Used for localvsglobal PP
        //---
        public double totalScore { get; set; } = 0;
        public double averageScore { get; set; } = 0;
        //---
    }

    public class Top10kLeaderboards
    {
        public List<Top10kPlayer> top10kPlayers = new List<Top10kPlayer>();
        public SortedDictionary<string, Top10kLeaderboardMeta> top10kLeaderboardMeta = new SortedDictionary<string, Top10kLeaderboardMeta>();

        public void GenerateTop10kSongMeta()
        {
            foreach (Top10kPlayer player in top10kPlayers)
            {
                foreach (Top10kScore score in player.top10kScore)
                {
                    //Add any missing songs.
                    if (!top10kLeaderboardMeta.ContainsKey(score.leaderboardID))
                    {
                        top10kLeaderboardMeta.Add(score.leaderboardID, new Top10kLeaderboardMeta { leaderboardID = score.leaderboardID });
                    }
                    Top10kLeaderboardMeta songMeta = top10kLeaderboardMeta[score.leaderboardID];
                    songMeta.count++;
                    songMeta.totalRank += score.rank;
                    songMeta.maxScore = Math.Max(songMeta.maxScore, score.pp);
                    songMeta.minScore = Math.Min(songMeta.minScore, score.pp);
                    songMeta.totalScore += score.pp;
                }
            }

            //set average for localvsglobal PP values
            foreach (Top10kLeaderboardMeta songMeta in top10kLeaderboardMeta.Values)
            {
                songMeta.averageScore = songMeta.totalScore / songMeta.count;
            }
            Console.WriteLine($"*Total Songs*: {top10kLeaderboardMeta.Count}");
        }

        public void Add(string id, string name, int rank)
        {
            Top10kPlayer newPlayer = new Top10kPlayer();
            newPlayer.id = id;
            newPlayer.name = name;
            newPlayer.rank = rank;
            top10kPlayers.Add(newPlayer);
        }
    }

    public class Top10kScore
    {
        public string leaderboardID { get; set; }
        public float pp { get; set; }
        public int rank { get; set; }
        [JsonIgnore]
        public Top10kPlayer parent { get; set; }
    }

    public class Top10kPlayer
    {
        public string id { get; set; }
        public string name { get; set; }
        public int rank { get; set; }
        public List<Top10kScore> top10kScore = new List<Top10kScore>();
    }
}
