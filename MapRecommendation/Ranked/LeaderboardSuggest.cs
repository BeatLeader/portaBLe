using Newtonsoft.Json;
using System.Net;

namespace portaBLe.MapRecommendation.Ranked
{
    public class LeaderboardSuggest
    {
        public static List<Top10kPlayer> GetBeatLeaderLeaderboard()
        {
            try
            {
                WebClient client = new WebClient();
                JsonSerializerSettings serializerSettings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
                string webString = "https://api.beatleader.com/songsuggest/?leaderboardContext=noMods";
                string songInfo = client.DownloadString(webString);
                return JsonConvert.DeserializeObject<List<Top10kPlayer>>(songInfo, serializerSettings);
            }
            catch
            {
                Console.WriteLine("Failed to fetch BeatLeader leaderboard data.");
            }
            return new();
        }

        public static async void RefreshBeatLeaderLeaderBoard(AppContext appContext, SongSuggestData songSuggestData)
        {
            var playerList = GetBeatLeaderLeaderboard();

            var toRemove = new List<Top10kPlayer>();

            foreach (var player in playerList)
            {
                double diff = player.top10kScore.Last().pp / player.top10kScore.First().pp;
                if (diff < 0.7)
                {
                    Console.WriteLine($"Player:{player.name}({player.id}) Rank: {player.rank} Diff: {diff}");
                    toRemove.Add(player);
                }
            }

            foreach (var player in playerList)
            {
                if (player.top10kScore.Count < 30)//20)
                {
                    Console.WriteLine($"Player:{player.name}({player.id}) Rank: {player.rank} Scores: {player.top10kScore.Count}");
                    toRemove.Add(player);
                }
            }

            playerList = playerList.Except(toRemove).ToList();

            // TODO: Add filter to check if the player has set a new score in their top 30 in less than a year
            /*
            var players = await appContext.Players
                .Where(p => p.Pp > 0)
                .OrderBy(p => p.Rank)
                .ToListAsync();

            var scoresByPlayer = await appContext.Scores
                .Where(s => s.Pp > 0)
                .GroupBy(s => s.PlayerId)
                .Select(g => new
                {
                    PlayerId = g.Key,
                    TopScores = g
                        .OrderByDescending(s => s.Pp)
                        .Take(30)
                        .Select(s => new
                        {
                            s.LeaderboardId,
                            s.Pp
                        })
                        .ToList()
                })
                .ToDictionaryAsync(x => x.PlayerId);*/

            /*
            List<Top10kPlayer> playerList = new();

            foreach (var player in players)
            {
                if (!scoresByPlayer.TryGetValue(player.Id, out var scoreGroup))
                    continue;

                if (scoreGroup.TopScores.Count < 30)
                    continue;

                var first = scoreGroup.TopScores.First().Pp;
                var last = scoreGroup.TopScores.Last().Pp;
                var diff = last / first;

                if (diff < 0.7)
                    continue;

                var top10kPlayer = new Top10kPlayer
                {
                    id = player.Id,
                    name = player.Name,
                    rank = player.Rank,
                    top10kScore = scoreGroup.TopScores.Select((s, index) => new Top10kScore
                    {
                        leaderboardID = s.LeaderboardId,
                        pp = s.Pp,
                        rank = index + 1
                    }).ToList()
                };

                playerList.Add(top10kPlayer);
            }
            */

            songSuggestData.leaderboards = CreateComparativeBestLeaderboardEvenMapDistribution(playerList);
        }

        public static Top10kLeaderboards CreateComparativeBestLeaderboardEvenMapDistribution(List<Top10kPlayer> top10kPlayers)
        {
            Console.WriteLine($"Comparative Best: {top10kPlayers.Count()} Players, {top10kPlayers.First().top10kScore.Count()}songs");
            Top10kLeaderboards best30 = new Top10kLeaderboards();
            best30.top10kPlayers = top10kPlayers;
            //Generate meta data on top 30 scores, which allows for lookup of what the max score is on a map by the string ID of the map. (No Song Library needed)
            best30.GenerateTop10kSongMeta();

            //Store each scores comparative best values, as well as link them to the songs
            Dictionary<string, List<Top10kScore>> songScores = new Dictionary<string, List<Top10kScore>>();
            Dictionary<Top10kPlayer, int> usedPlayerScores = new Dictionary<Top10kPlayer, int>();

            foreach (var person in best30.top10kPlayers)
            {
                foreach (var score in person.top10kScore)
                {
                    if (!songScores.ContainsKey(score.songID)) songScores[score.songID] = new List<Top10kScore>();
                    songScores[score.songID].Add(score);
                    score.parent = person;
                }
                usedPlayerScores[person] = 0;
                //Remove current person assignment, we restore these from those used from songs
                person.top10kScore.Clear();
            }

            //Order each songs scores from largest to smallest PP (we work with last element as we plan on remove checked elements from the end (better list removal))
            //So order by smallest to largest, so largest is last, and is what is used.
            foreach (var key in songScores.Keys.ToList())
            {
                songScores[key] = songScores[key]
                    .OrderBy(s => s.pp)
                    .ToList();
            }

            //Get the assignment order, we want to assign scores with a priority to those with few entries, in case of a tie, strongest scores is used first.
            var songs = songScores
                .OrderBy(c => c.Value.Count())
                .ThenByDescending(c => c.Value.Last().pp)
                .Select(c => c.Key)
                .ToList();

            //Loop assignment until all scores are used up
            while (songs.Count > 0)
            {
                //Loop each remaining song until its candidates are check, if empty we remove it from songs.
                foreach (var song in songs.ToList())
                {
                    var entries = songScores[song];

                    //Remove exhausted players
                    while (entries.Count > 0 && usedPlayerScores[entries.Last().parent] >= 20)
                    {
                        entries.RemoveAt(entries.Count - 1);
                    }

                    // assign next player if any left
                    if (entries.Count > 0)
                    {
                        var lastIndex = entries.Count - 1;
                        var entry = entries[lastIndex];
                        usedPlayerScores[entry.parent]++;
                        entry.parent.top10kScore.Add(entry);
                        entries.RemoveAt(lastIndex);
                    }

                    // remove song if exhausted
                    if (entries.Count == 0)
                    {
                        songs.Remove(song);
                    }
                }
            }

            //Refix Index on scores from 30 to 20.
            foreach (var person in best30.top10kPlayers)
            {
                person.top10kScore = person.top10kScore
                    .OrderByDescending(c => c.pp)
                    //Transform current object into a new with an updated rank by its index.
                    .Select((c, index) =>
                    {
                        c.rank = index + 1; //Ranks starts at 1, index value is 0 indexed.
                        return c;
                    })
                    .ToList();
            }

            Console.WriteLine($"Comparative Best: {top10kPlayers.Count()} Players, {top10kPlayers.First().top10kScore.Count()}songs");

            var best20 = new Top10kLeaderboards();
            best20.top10kPlayers = best30.top10kPlayers;
            best20.GenerateTop10kSongMeta();

            SaveScoreBoard(best20.top10kPlayers, "BeatLeader");

            //Saves the updated top 20's with the provided name (for later loading in setup).
            return best20;
        }

        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore,
            Formatting = Formatting.None
        };

        public static void SaveScoreBoard(List<Top10kPlayer> players, string scoreBoardName)
        {
            try
            {
                string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recommendations");
                Directory.CreateDirectory(outputDir);

                string filename = Path.Combine(outputDir, scoreBoardName + ".json");
                System.IO.File.WriteAllText(filename, JsonConvert.SerializeObject(players, SerializerSettings));
                Console.WriteLine($"Successfully saved scoreboard: {filename}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving scoreboard '{scoreBoardName}': {ex.Message}");
            }
        }
    }
}
