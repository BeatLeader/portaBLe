using System.Text;
using portaBLe.DB;

namespace portaBLe.MapRecommendation.Ranked
{
    public class ExportSongLinkDetails
    {
        public static void Execute(SongSuggestData songSuggestData, string songID, Top10kLeaderboards leaderboards, AppContext appContext = null)
        {
            var content = GenerateSongLinkContent(songSuggestData, songID, appContext);
            if (!string.IsNullOrEmpty(content))
            {
                ExportToFile(content, songID);
            }
        }

        public static void ExecuteAll(SongSuggestData songSuggestData, Top10kLeaderboards leaderboards, AppContext appContext = null)
        {
            if (songSuggestData.targetLeaderboards?.endPoints == null)
            {
                Console.WriteLine("Target leaderboards not initialized. Cannot export all song details.");
                return;
            }

            if (songSuggestData.targetLeaderboards.endPoints.Count == 0)
            {
                Console.WriteLine("No target leaderboards found to export.");
                return;
            }

            var sb = new StringBuilder();
            int successfulExports = 0;

            foreach (var songID in songSuggestData.targetLeaderboards.endPoints.Keys)
            {
                var content = GenerateSongLinkContent(songSuggestData, songID, appContext);
                if (!string.IsNullOrEmpty(content))
                {
                    sb.Append(content);
                    sb.AppendLine();
                    successfulExports++;
                }
            }

            if (successfulExports > 0)
            {
                ExportAllToFile(sb.ToString(), successfulExports);
            }
            else
            {
                Console.WriteLine("No song link details were generated for export.");
            }
        }

        public static void ExportOriginLeaderboards(SongSuggestData songSuggestData, AppContext appContext = null)
        {
            if (songSuggestData.originLeaderboards?.endPoints == null)
            {
                Console.WriteLine("Origin leaderboards not initialized. Cannot export origin songs.");
                return;
            }

            if (songSuggestData.originLeaderboards.endPoints.Count == 0)
            {
                Console.WriteLine("No origin leaderboards found to export.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Selected Origin Songs");
            sb.AppendLine();

            // Get active player's scores for comparison
            var playerScores = songSuggestData.activePlayer?.top10kScore ?? new List<Top10kScore>();
            var playerScoreDict = playerScores.ToDictionary(s => s.songID, s => s);

            // Get max scores for all songs from the top 10k leaderboards
            var allPlayersScores = songSuggestData.leaderboards?.top10kPlayers ?? new List<Top10kPlayer>();
            var songMaxScores = new Dictionary<string, float>();
            var songPlayerCounts = new Dictionary<string, int>();

            foreach (var player in allPlayersScores)
            {
                foreach (var score in player.top10kScore)
                {
                    if (!songMaxScores.ContainsKey(score.songID))
                    {
                        songMaxScores[score.songID] = score.pp;
                        songPlayerCounts[score.songID] = 1;
                    }
                    else
                    {
                        songMaxScores[score.songID] = Math.Max(songMaxScores[score.songID], score.pp);
                        songPlayerCounts[score.songID]++;
                    }
                }
            }

            // Output each origin song with detailed information
            int index = 0;
            foreach (var songID in songSuggestData.originLeaderboards.endPoints.Keys)
            {
                if (index >= 50)
                    break;

                // Get song name from database if available, otherwise use songID
                string songName = GetSongName(songID, appContext) ?? songID;
                
                // Get the endpoint to count linked songs
                var endPoint = songSuggestData.originLeaderboards.endPoints[songID];
                int linkedCount = endPoint.songLinks.Count;
                
                // Get max score for this song from all players' scores
                float maxScore = songMaxScores.TryGetValue(songID, out var max) ? max : 0;
                int playerCount = songPlayerCounts.TryGetValue(songID, out var count) ? count : 0;

                // Check if this song is used in selected suggestions
                bool isUsed = songSuggestData.sortedSuggestions?.Contains(songID) ?? false;
                string usedMarker = isUsed ? "X" : " ";

                // Get player's score on this song
                float playerScore = 0;
                if (playerScoreDict.TryGetValue(songID, out var playerScoreData))
                {
                    playerScore = playerScoreData.pp;
                }

                // Calculate advantage percentage (how much better max score is vs player score)
                float advantagePercent = 0;
                if (playerScore > 0 && maxScore > playerScore)
                {
                    advantagePercent = ((maxScore - playerScore) / playerScore) * 100;
                }

                // Get comparative ranking (estimate of how many players have a better score)
                int comparativeRank = 0;
                if (playerScore > 0 && maxScore > 0 && playerCount > 0)
                {
                    // Estimate: (1 - ratio) * total players = estimated players with better scores
                    float ratio = playerScore / maxScore;
                    comparativeRank = (int)((1 - ratio) * playerCount);
                }

                // Format: X   SongCategory: BeatLeader   Score: {playerScore}({maxScore})   Advantage%: {advantage}%   Comparative: {rank}|  {maxScore}    {songName}
                string scoreStr = $"{playerScore:F2}({maxScore:F2})";
                string advantageStr = $"{advantagePercent:F2}%";
                string comparativeStr = $"{comparativeRank:D2}| {maxScore:F5}";

                sb.AppendLine($"{usedMarker}   SongCategory: BeatLeader         Score: {scoreStr,15}   Advantage%: {advantageStr,7}   Comparative: {comparativeStr,15}    {songName}");
                index++;
            }

            sb.AppendLine();

            ExportOriginToFile(sb.ToString(), songSuggestData.playerID);
        }

        public static void ExportLeaderboardDetails(SongSuggestData songSuggestData, string songID, AppContext appContext = null)
        {
            if (songSuggestData.leaderboards?.top10kPlayers == null)
            {
                Console.WriteLine("Leaderboards not initialized. Cannot export leaderboard details.");
                return;
            }

            // Get song name from database if available
            string songName = GetSongName(songID, appContext) ?? songID;

            // Get all players' scores on this song and sort by PP descending
            var playerScoresOnSong = songSuggestData.leaderboards.top10kPlayers
                .SelectMany(player => player.top10kScore
                    .Where(score => score.songID == songID)
                    .Select(score => new
                    {
                        PlayerID = player.id,
                        PlayerName = player.name,
                        PlayerRank = player.rank,
                        PP = score.pp,
                        ScoreRank = score.rank
                    }))
                .OrderBy(x => x.PlayerRank)
                .ToList();

            if (playerScoresOnSong.Count == 0)
            {
                Console.WriteLine($"No players found with scores on song {songID}");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(songName);

            foreach (var score in playerScoresOnSong)
            {
                // Format: {PP}PP   Rank: {rank}   PlayerID: {playerID}   Player: {playerName}
                string ppStr = $"{score.PP:F2}PP";
                string rankStr = $"{score.PlayerRank:D5}";
                string playerIDStr = $"{score.PlayerID,18}";
                string playerNameStr = score.PlayerName;

                sb.AppendLine($" {ppStr,-9}   Rank: {rankStr}   PlayerID: {playerIDStr}   Player: {playerNameStr}");
            }

            ExportLeaderboardToFile(sb.ToString(), songID);
        }

        private static string GenerateSongLinkContent(SongSuggestData songSuggestData, string songID, AppContext appContext = null)
        {
            if (songSuggestData.targetLeaderboards?.endPoints == null)
            {
                return null;
            }

            if (!songSuggestData.targetLeaderboards.endPoints.ContainsKey(songID))
            {
                return null;
            }

            var targetEndPoint = songSuggestData.targetLeaderboards.endPoints[songID];
            var allLinks = targetEndPoint.songLinks;

            if (allLinks.Count == 0)
            {
                return null;
            }

            // Get all players who have this song in their origin leaderboards
            var players = songSuggestData.leaderboards.top10kPlayers
                .Where(p => p.id != songSuggestData.playerID)
                .Where(p => p.top10kScore.Any(s => s.songID == songID))
                .ToList();

            if (players.Count == 0)
            {
                return null;
            }

            // Get song name from database if available, otherwise use songID
            string songName = GetSongName(songID, appContext) ?? songID;

            // Build the output
            var sb = new StringBuilder();
            sb.AppendLine($"--- Player Link Details for {songName}");

            foreach (var player in players)
            {
                var playerOriginSongs = player.top10kScore
                    .Where(s => songSuggestData.originleaderboardIDs.Contains(s.songID))
                    .OrderByDescending(s => s.pp)
                    .ToList();

                foreach (var originSong in playerOriginSongs)
                {
                    // Check if this specific link is used (exists in the target endpoint's songLinks)
                    bool isUsed = allLinks.Any(l => 
                        l.playerID == player.id && 
                        l.originSongScore.songID == originSong.songID &&
                        l.targetSongScore.songID == songID);

                    string usedMarker = isUsed ? "+" : " ";

                    // Get origin song name
                    string originSongName = GetSongName(originSong.songID, appContext) ?? originSong.songID;

                    // Format: + ID:  {playerID}   {pp}PP   Player Name: ({rank}) {name}               Song: {originSongID}
                    string playerID = player.id;
                    string pp = $"{originSong.pp:F2}PP";
                    string playerRankFormatted = $"({player.rank})";
                    string playerNameFormatted = $"{player.name,-20}";

                    sb.AppendLine($"{usedMarker} ID: {playerID,18}  {pp,8}  Player Name: {playerRankFormatted,5} {playerNameFormatted}   Song: {originSongName}");
                }
            }

            sb.AppendLine("---");
            return sb.ToString();
        }

        private static string GetSongName(string songID, AppContext appContext)
        {
            if (appContext == null)
            {
                return null;
            }

            try
            {
                var leaderboard = appContext.Leaderboards.FirstOrDefault(l => l.Id == songID);
                if (leaderboard != null)
                {
                    return $"{leaderboard.Name} ({leaderboard.DifficultyName} - {leaderboard.ModeName} - {songID})";
                }
            }
            catch
            {
                // If database lookup fails, return null to use songID as fallback
            }

            return null;
        }

        private static void ExportToFile(string content, string songID)
        {
            try
            {
                string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recommendations");
                Directory.CreateDirectory(outputDir);

                string filename = Path.Combine(outputDir, $"song_links_{songID}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(filename, content);
                Console.WriteLine($"Successfully exported song link details to: {filename}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error exporting song link details: {ex.Message}");
            }
        }

        private static void ExportAllToFile(string content, int songCount)
        {
            try
            {
                string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recommendations");
                Directory.CreateDirectory(outputDir);

                string filename = Path.Combine(outputDir, $"all_song_links_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(filename, content);
                Console.WriteLine($"Successfully exported all song link details ({songCount} songs) to: {filename}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error exporting all song link details: {ex.Message}");
            }
        }

        private static void ExportLeaderboardToFile(string content, string songID)
        {
            try
            {
                string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recommendations");
                Directory.CreateDirectory(outputDir);

                string filename = Path.Combine(outputDir, $"leaderboard_{songID}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(filename, content);
                Console.WriteLine($"Successfully exported leaderboard details to: {filename}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error exporting leaderboard details: {ex.Message}");
            }
        }

        public static void ExportPlaylistSuggestions(SongSuggestData songSuggestData, AppContext appContext = null)
        {
            if (songSuggestData.sortedSuggestions == null || songSuggestData.sortedSuggestions.Count == 0)
            {
                Console.WriteLine("No sorted suggestions found. Cannot export playlist suggestions.");
                return;
            }

            if (songSuggestData.leaderboards == null)
            {
                Console.WriteLine("Leaderboards not initialized. Cannot export playlist suggestions.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Selected Playlist Songs (Top 50)");
            sb.AppendLine();

            // Get the top 50 sorted suggestions
            var top50Songs = songSuggestData.sortedSuggestions.Take(50).ToList();

            for (int i = 0; i < top50Songs.Count; i++)
            {
                string songID = top50Songs[i];
                
                // Get song name from database if available
                string songName = GetSongName(songID, appContext) ?? songID;

                // Get leaderboard metadata for this song
                float maxScore = 0;
                double averageScore = 0;
                int playerCount = 0;

                if (songSuggestData.leaderboards.top10kLeaderboardMeta.TryGetValue(songID, out var meta))
                {
                    maxScore = (float)meta.maxScore;
                    averageScore = meta.averageScore;
                    playerCount = (int)meta.count;
                }

                // Get player's score on this song if played
                float playerScore = 0;
                int playerRank = -1;
                var playerScore_data = songSuggestData.activePlayer?.top10kScore.FirstOrDefault(s => s.songID == songID);
                if (playerScore_data != null)
                {
                    playerScore = playerScore_data.pp;
                    playerRank = playerScore_data.rank;
                }

                // Calculate score difference vs max
                float scoreDiff = maxScore - playerScore;
                double advantage = playerScore > 0 ? (scoreDiff / playerScore) * 100 : 0;

                // Get link count from target leaderboards
                int linkedPlayers = 0;
                if (songSuggestData.targetLeaderboards?.endPoints.TryGetValue(songID, out var endpoint) == true)
                {
                    linkedPlayers = endpoint.songLinks.Select(l => l.playerID).Distinct().Count();
                }

                // Format output: Position + Song info
                string posStr = $"{(i + 1):D2}.";
                string playerScoreStr = playerScore > 0 ? $"{playerScore:F2}" : "UNPLAYED";
                string maxScoreStr = $"{maxScore:F2}";
                string advantageStr = $"{advantage:F2}%";
                string linkedStr = $"{linkedPlayers}";

                sb.AppendLine($"{posStr} {songName}");
                sb.AppendLine($"    Score: {playerScoreStr,-10} | Max: {maxScoreStr,-10} | Advantage: {advantageStr,-8} | Players: {playerCount,-5} | Linked: {linkedStr,-3}");
                sb.AppendLine();
            }

            ExportPlaylistToFile(sb.ToString());
        }

        private static void ExportPlaylistToFile(string content)
        {
            try
            {
                string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recommendations");
                Directory.CreateDirectory(outputDir);

                string filename = Path.Combine(outputDir, $"playlist_suggestions_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(filename, content);
                Console.WriteLine($"Successfully exported playlist suggestions to: {filename}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error exporting playlist suggestions: {ex.Message}");
            }
        }

        private static void ExportOriginToFile(string content, string playerID)
        {
            try
            {
                string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recommendations");
                Directory.CreateDirectory(outputDir);

                string filename = Path.Combine(outputDir, $"origin_songs_{playerID}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(filename, content);
                Console.WriteLine($"Successfully exported origin leaderboards to: {filename}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error exporting origin leaderboards: {ex.Message}");
            }
        }

        public static void ExportLeaderboardLinks(SongSuggestData songSuggestData, AppContext appContext = null)
        {
            if (songSuggestData.originLeaderboards?.endPoints == null)
            {
                Console.WriteLine("Origin leaderboards not initialized. Cannot export leaderboard links.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Leaderboard Links Export - Merged by Target Song");
            sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            // Collect all links from origin leaderboards
            var allLinks = new List<LeaderboardLink>();
            foreach (var endPoint in songSuggestData.originLeaderboards.endPoints.Values)
            {
                allLinks.AddRange(endPoint.songLinks);
            }

            sb.AppendLine($"Total Links: {allLinks.Count}");
            sb.AppendLine();

            // Count unique players
            var uniquePlayers = allLinks.Select(l => l.playerID).Distinct().Count();
            var uniqueOriginSongs = allLinks.Select(l => l.originSongScore.songID).Distinct().Count();
            var uniqueTargetSongs = allLinks.Select(l => l.targetSongScore.songID).Distinct().Count();

            sb.AppendLine("=== SUMMARY ===");
            sb.AppendLine($"Unique Players: {uniquePlayers}");
            sb.AppendLine($"Unique Origin Songs: {uniqueOriginSongs}");
            sb.AppendLine($"Unique Target Songs: {uniqueTargetSongs}");
            sb.AppendLine($"Distance Range: {(allLinks.Count > 0 ? allLinks.Min(l => l.distance) : 0):F4} - {(allLinks.Count > 0 ? allLinks.Max(l => l.distance) : 0):F4}");
            sb.AppendLine();

            // Main section: Group all links by TARGET leaderboard ID
            sb.AppendLine("=== ALL LINKS MERGED BY TARGET SONG ===");
            var linksByTargetLeaderboard = allLinks
                .GroupBy(l => l.targetSongScore.songID)
                .OrderByDescending(g => g.Count())
                .ToList();

            foreach (var targetGroup in linksByTargetLeaderboard)
            {
                string targetSongName = GetSongName(targetGroup.Key, appContext) ?? targetGroup.Key;
                var targetScore = targetGroup.First().targetSongScore;
                var allDistances = targetGroup.Select(l => l.distance).ToList();

                sb.AppendLine();
                sb.AppendLine($"========================================");
                sb.AppendLine($"TARGET: {targetSongName}");
                sb.AppendLine($"Target Score: {targetScore.pp:F2}PP | Rank: {targetScore.rank}");
                sb.AppendLine($"Total Links: {targetGroup.Count()}");
                sb.AppendLine($"Unique Linking Players: {targetGroup.Select(l => l.playerID).Distinct().Count()}");
                sb.AppendLine($"Unique Origin Songs: {targetGroup.Select(l => l.originSongScore.songID).Distinct().Count()}");
                sb.AppendLine($"Distance Stats - Avg: {allDistances.Average():F4} | Min: {allDistances.Min():F4} | Max: {allDistances.Max():F4}");
                sb.AppendLine($"========================================");
                sb.AppendLine();

                // Group by origin song for this target
                var originsByTarget = targetGroup
                    .GroupBy(l => l.originSongScore.songID)
                    .OrderByDescending(g => g.Count())
                    .ToList();

                foreach (var originGroup in originsByTarget)
                {
                    string originSongName = GetSongName(originGroup.Key, appContext) ?? originGroup.Key;
                    var originScore = originGroup.First().originSongScore;
                    var distances = originGroup.Select(l => l.distance).ToList();

                    sb.AppendLine($"  Origin: {originSongName}");
                    sb.AppendLine($"  Origin Score: {originScore.pp:F2}PP | Rank: {originScore.rank}");
                    sb.AppendLine($"  Total Links from this origin: {originGroup.Count()}");
                    sb.AppendLine($"  Distance - Avg: {distances.Average():F4} | Range: {distances.Min():F4} - {distances.Max():F4}");
                    sb.AppendLine();

                    // Show players creating these links
                    var playersByOriginTarget = originGroup
                        .GroupBy(l => l.playerID)
                        .OrderByDescending(g => g.Count())
                        .ToList();

                    sb.AppendLine($"    Players linking from this origin to target: ({playersByOriginTarget.Count})");
                    foreach (var playerGroup in playersByOriginTarget)
                    {
                        sb.AppendLine($"      - {playerGroup.Key}: {playerGroup.Count()} link(s)");
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine();
            sb.AppendLine("=== DISTANCE DISTRIBUTION ===");
            var linksByDistance = allLinks
                .GroupBy(l => Math.Round(l.distance, 2))
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var distanceGroup in linksByDistance)
            {
                int barLength = (int)(distanceGroup.Count() / (double)allLinks.Count * 50);
                string bar = new string('█', Math.Max(1, barLength));
                sb.AppendLine($"Distance {distanceGroup.Key:F2}: {bar} ({distanceGroup.Count()} links)");
            }

            ExportLinksToFile(sb.ToString());
        }

        private static void ExportLinksToFile(string content)
        {
            try
            {
                string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recommendations");
                Directory.CreateDirectory(outputDir);

                string filename = Path.Combine(outputDir, $"leaderboard_links_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(filename, content);
                Console.WriteLine($"Successfully exported leaderboard links to: {filename}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error exporting leaderboard links: {ex.Message}");
            }
        }
    }
}
