using portaBLe.MapRecommendation.Ranked;

namespace Actions
{
    public static class GenerateOriginLeaderboardIDs
    {
        internal static void Execute(SongSuggestData songSuggestData)
        {
            List<string> originLeaderboardIDs = new List<string>();
            int targetCount = 50;

            var playedSongs = RankedSongSuggest.SelectPlayedOriginSongs(songSuggestData);
            originLeaderboardIDs.AddRange(playedSongs);
            originLeaderboardIDs = originLeaderboardIDs
                .Distinct()         //Remove Duplicates
                .ToList();

            //If there is no originSongs found (no played in the group, or all permabanned) we select some default songs to give suggestions from.
            if (originLeaderboardIDs.Count == 0)
            {
                //Add the filler songs to the currently found
                var fillerSongs = RankedSongSuggest.GetFillerSongs(songSuggestData.leaderboards);

                originLeaderboardIDs.AddRange(fillerSongs);

                //Remove any duplicates and reduce the list to target filler count.
                originLeaderboardIDs = originLeaderboardIDs
                    .Distinct()         //Remove Duplicates
                    .Take(50)
                    .ToList();
            }

            originLeaderboardIDs = originLeaderboardIDs
                .Take(targetCount)  //Try and get originSongsCount or all liked whichever is larger
                .ToList();
          
            // HtmlExporter.ExportList("OriginLeaderboardIDs_Debug", originLeaderboardIDs.ConvertAll(x => new { LeaderboardID = x }));

            songSuggestData.originleaderboardIDs = originLeaderboardIDs;
        }
    }
}