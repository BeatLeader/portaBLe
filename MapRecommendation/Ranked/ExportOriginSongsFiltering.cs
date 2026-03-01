using System.Text;
using portaBLe.DB;

namespace portaBLe.MapRecommendation.Ranked
{
    public class ExportOriginSongsFiltering
    {
        public static void Execute(SongSuggestData songSuggestData, AppContext appContext = null)
        {
            if (songSuggestData.activePlayer == null || songSuggestData.leaderboards == null)
            {
                Console.WriteLine("Active player or leaderboards not initialized. Cannot export filtering details.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Origin Songs Selection - Filtering Details");
            sb.AppendLine($"Player ID: {songSuggestData.playerID}");
            sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            // Step 1: Initial songs
            var allSongs = songSuggestData.activePlayer.top10kScore
                .OrderByDescending(value => RankedSongSuggest.PlayerWeightedScoreValue(value.pp, value.rank))
                .ToList();

            sb.AppendLine("=== STEP 1: All Player Songs ===");
            sb.AppendLine($"Total: {allSongs.Count} songs");
            sb.AppendLine();

            // Step 2: 75% filtering
            double maxKeepPercentage = 0.75;
            int valueSongCount = (int)(maxKeepPercentage * allSongs.Count);
            if (valueSongCount == 0) valueSongCount = 1;

            var step1Songs = allSongs.Take(valueSongCount).ToList();
            var filteredStep1 = allSongs.Skip(valueSongCount).ToList();

            sb.AppendLine("=== STEP 2: Filter 75% Worst Songs ===");
            sb.AppendLine($"Kept: {step1Songs.Count} songs (75%)");
            sb.AppendLine($"Filtered Out: {filteredStep1.Count} songs");
            sb.AppendLine();

            if (filteredStep1.Count > 0)
            {
                sb.AppendLine("Filtered Songs (Bottom 25%):");
                foreach (var song in filteredStep1.Take(10))
                {
                    string songName = GetSongName(song.songID, appContext) ?? song.songID;
                    double weighted = RankedSongSuggest.PlayerWeightedScoreValue(song.pp, song.rank);
                    sb.AppendLine($"  - {songName}");
                    sb.AppendLine($"    PP: {song.pp:F2} | Rank: {song.rank} | Weighted: {weighted:F2}");
                }
                if (filteredStep1.Count > 10)
                    sb.AppendLine($"  ... and {filteredStep1.Count - 10} more");
                sb.AppendLine();
            }

            // Step 3: Relative score ordering
            double percentToKeep = 50.0 / (songSuggestData.originLeaderboardsCount + songSuggestData.extraLeaderboardsCount);
            int comparativeBestCount = (int)Math.Ceiling(percentToKeep * valueSongCount);

            var step2Songs = step1Songs
                .OrderByDescending(c => RankedSongSuggest.PlayerRelativeScoreValue(c.pp, c.songID, songSuggestData.leaderboards))
                .ToList();

            var filteredStep2 = step2Songs.Skip(comparativeBestCount).ToList();
            var step2Best = step2Songs.Take(comparativeBestCount).ToList();

            sb.AppendLine("=== STEP 3: Filter by Relative Score (Top 7.7%) ===");
            sb.AppendLine($"Percent to keep: {percentToKeep:P}");
            sb.AppendLine($"Kept: {step2Best.Count} songs");
            sb.AppendLine($"Filtered Out: {filteredStep2.Count} songs");
            sb.AppendLine();

            if (filteredStep2.Count > 0)
            {
                sb.AppendLine("Filtered Songs (Low Relative Score):");
                foreach (var song in filteredStep2.Take(15))
                {
                    string songName = GetSongName(song.songID, appContext) ?? song.songID;
                    double relativeScore = RankedSongSuggest.PlayerRelativeScoreValue(song.pp, song.songID, songSuggestData.leaderboards);
                    float maxScore = 0;
                    if (songSuggestData.leaderboards.top10kLeaderboardMeta.TryGetValue(song.songID, out var meta))
                    {
                        maxScore = (float)meta.maxScore;
                    }
                    double advantage = maxScore > 0 ? ((maxScore - song.pp) / song.pp) * 100 : 0;

                    sb.AppendLine($"  - {songName}");
                    sb.AppendLine($"    PP: {song.pp:F2} | Max: {maxScore:F2} | Relative Score: {relativeScore:F4} | Advantage: {advantage:F2}%");
                }
                if (filteredStep2.Count > 15)
                    sb.AppendLine($"  ... and {filteredStep2.Count - 15} more");
                sb.AppendLine();
            }

            // Step 4: Final selection (top 50)
            var step4Songs = step2Best.Take(50).ToList();

            sb.AppendLine("=== STEP 4: Final Selection (Top 50) ===");
            sb.AppendLine($"Final songs: {step4Songs.Count}");
            sb.AppendLine();

            sb.AppendLine("=== SELECTED SONGS ===");
            for (int i = 0; i < step4Songs.Count; i++)
            {
                var song = step4Songs[i];
                string songName = GetSongName(song.songID, appContext) ?? song.songID;
                double weighted = RankedSongSuggest.PlayerWeightedScoreValue(song.pp, song.rank);
                double relativeScore = RankedSongSuggest.PlayerRelativeScoreValue(song.pp, song.songID, songSuggestData.leaderboards);

                sb.AppendLine($"{(i + 1):D2}. {songName}");
                sb.AppendLine($"    PP: {song.pp:F2} | Rank: {song.rank} | Weighted: {weighted:F2} | Relative: {relativeScore:F4}");
            }

            ExportToFile(sb.ToString(), songSuggestData.playerID);
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
                // If database lookup fails, return null
            }

            return null;
        }

        private static void ExportToFile(string content, string playerID)
        {
            try
            {
                string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recommendations");
                Directory.CreateDirectory(outputDir);

                string filename = Path.Combine(outputDir, $"origin_filtering_{playerID}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt");
                File.WriteAllText(filename, content);
                Console.WriteLine($"Successfully exported origin filtering details to: {filename}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error exporting origin filtering details: {ex.Message}");
            }
        }
    }
}
