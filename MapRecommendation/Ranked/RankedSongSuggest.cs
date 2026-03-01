using Actions;
using System.Linq;

namespace portaBLe.MapRecommendation.Ranked
{
    public class  SongSuggestData
    {
        public string? playerID;
        public (List<string>, float value) playerLeaderboards;
        public Top10kPlayer activePlayer;
        public Top10kLeaderboards? leaderboards;
        public List<string> originleaderboardIDs = new();
        public LeaderboardEndPointCollection? originLeaderboards;
        public LeaderboardEndPointCollection? targetLeaderboards;
        public List<string> styleFilterOrdered;
        public List<string> overWeightFilterOrdered;
        public List<string> sortedSuggestions;
        public bool ignoreNonImproveable = true;
        public float modifierStyle = 1.0f;
        public float modifierOverweight = 0.2f;
        public int originLeaderboardsCount = 50;
        public int extraLeaderboardsCount = 15;
    }

    public class RankedSongSuggest
    {
        //Creates a playlist with playlist count suggested songs based on the link system.
        public static void SuggestedSongs(AppContext appContext, SongSuggestData songSuggestData, bool unplayed)
        {
            Console.WriteLine($"[SuggestedSongs] Starting song suggestion process for player: {songSuggestData.playerID}");

            songSuggestData.activePlayer = songSuggestData.leaderboards.top10kPlayers.Where(p => p.id == songSuggestData.playerID).FirstOrDefault();

            //Setup Base Linking (song links).
            CreateLinks(songSuggestData);

            //Generate the different filters rankings. (Calculate Scores, and Rank them)
            CreateFilterRanks(songSuggestData);

            //Takes the orderes lists runs through them and assign points based on order.
            EvaluateFilters(songSuggestData);

            //Removes filtered songs (Played/Played within X days/Banned/Not expected improveable atm) depending on settings
            RemoveIgnoredSongs(songSuggestData);

            //Creates the playist of remaining songs
            CreatePlaylist(appContext, songSuggestData, unplayed);
            
            Console.WriteLine("[SuggestedSongs] Song suggestion process completed");
        }

        //Creates the needed linked data for song evaluation for the Active Player.
        //Until Active Players top originSongsCount scores change *1 (replaced or better scores) no need to recalculate
        //*1 (Liked songs if active changes also counts as an update)
        public static void CreateLinks(SongSuggestData songSuggestData)
        {
            //Find the Origin Song ID's based on Active Players data.
            GenerateOriginLeaderboardIDs.Execute(songSuggestData);
            // HtmlExporter.ExportList("OriginleaderboardIDs", songSuggestData.originleaderboardIDs.ConvertAll(x => new { leaderboardID = x }));

            //Link the origin songs with the songs on the LeaderBoard as a basis for suggestions.
            GenerateLinks.Execute(songSuggestData);
            
            var linkedPlayerIDs = songSuggestData.targetLeaderboards.endPoints.Values
                .SelectMany(c => c.songLinks)
                .Select(d => d.playerID)
                .Distinct()
                .ToList();

            Console.WriteLine($"[CreateLinks] Found {linkedPlayerIDs.Count} linked players for suggestions");
            // HtmlExporter.ExportList("LinkedPlayerIDs", linkedPlayerIDs.ConvertAll(x => new { PlayerID = x }));
        }

        //Order the songs via the different active filters
        public static void CreateFilterRanks(SongSuggestData songSuggestData)
        {
            //Calculate the scores on the songs for suggestions
            songSuggestData.targetLeaderboards.SetRelevance(songSuggestData.originLeaderboards.endPoints.Count(), 10);
            
            songSuggestData.targetLeaderboards.SetStyle(songSuggestData.originLeaderboards);
            
            //Filter on how much over/under linked a song is in the active players data vs the global player population
            songSuggestData.styleFilterOrdered = songSuggestData.targetLeaderboards.endPoints.Values.OrderBy(s => (0.0 + songSuggestData.leaderboards.top10kLeaderboardMeta[s.leaderboardID].count) / (0.0 + s.proportionalStyle)).Select(p => p.leaderboardID).ToList();
            // HtmlExporter.ExportList("StyleFilterOrdered", songSuggestData.styleFilterOrdered.ConvertAll(x => new { LeaderboardID = x }));

            //Filter on how the selected songs rank are better than average
            songSuggestData.overWeightFilterOrdered = songSuggestData.targetLeaderboards.endPoints.Values.OrderBy(s => s.averageRank).Select(p => p.leaderboardID).ToList();
            // HtmlExporter.ExportList("OverWeightFilterOrdered", songSuggestData.overWeightFilterOrdered.ConvertAll(x => new { LeaderboardID = x }));
        }

        //Takes the orderes suggestions and apply the filter values to their ranks, and create the nameplate orderings
        //**Consider rewriting to handle any amount of filters in the future (loop each filter for its position and record it before multiplying all).**
        public static void EvaluateFilters(SongSuggestData songSuggestData)
        {
            Dictionary<string, double> totalScore = new Dictionary<string, double>();

            //Get Base Weights reset them from % value to [0-1], and must not all be 0)
            double modifierStyle = songSuggestData.modifierStyle;
            double modifierOverweight = songSuggestData.modifierOverweight;

            //reset if all = 0, reset to 100%.
            if (modifierStyle == 0 && modifierOverweight == 0) modifierStyle = modifierOverweight = 1.0;

            //Get count of candidates, and remove 1, as index start as 0, so max value is songs-1
            double totalCandidates = songSuggestData.overWeightFilterOrdered.Count() - 1;

            //We loop either of the 2 filters and record its ordering in a temporary dictionary for quick lookup.
            Dictionary<string, int> overweightValues = new Dictionary<string, int>();
            int rankCount = 0;
            foreach (var leaderboardID in songSuggestData.overWeightFilterOrdered)
            {
                overweightValues[leaderboardID] = rankCount;
                rankCount++;
            }

            //Reset count and do the same for the 2nd, but we might as well do the calculations at the same time.
            rankCount = 0;
            int skippedSongs = 0;
            foreach (string leaderboardID in songSuggestData.styleFilterOrdered)
            {
                //Get the location of the candidate in the list as a [0-1] value
                double styleValue = rankCount / totalCandidates;
                double overWeightedValue = overweightValues.TryGetValue(leaderboardID, out var rank) ? rank / totalCandidates : 0;

                if (!overweightValues.ContainsKey(leaderboardID))
                {
                    skippedSongs++;
                }

                //Switch the range from [0-1] to [0.5-1.5] and reduce the gap based on modifier weight.
                //**Spacing between values may be more correct to consider a log spacing (e.g. due to 1.5*.0.5 != 1)
                //**But as values are kept around 1, and it is not important to keep total average at 1, the difference in
                //**Actual ratings in the 0.5 to 1.5 range is minimal at the "best suggestions range" even with quite a few filters.
                //**So a "correct range" of 0.5 to 2 would give a higher penalty on bad matches on a single filter, so current
                //**setup means a song must do worse on more filters to actual lose rank, which actually may be prefered.
                //double distanceTotal = distanceValue * modifierDistance + (1.0 - 0.5 * modifierDistance);
                double styleTotal = styleValue * modifierStyle + (1.0 - 0.5 * modifierStyle);
                double overWeightedTotal = overWeightedValue * modifierOverweight + (1.0 - 0.5 * modifierOverweight);

                //Get the songs multiplied average 
                double score = styleTotal * overWeightedTotal;

                //Add song ID and its score to a list for sorting and reducing size for the playlist generation
                totalScore.Add(leaderboardID, score);

                //Increase rank count for next song.
                rankCount++;
            }
            
            Console.WriteLine($"[EvaluateFilters] Processed {rankCount} songs, {skippedSongs} songs missing from overweight filter");
            Console.WriteLine($"[EvaluateFilters] Total scores calculated: {totalScore.Count}");
            
            //Export all scores before sorting
            // HtmlExporter.ExportDictionary("AllScores", totalScore);
            
            //Sort list, and get song ID's only (OrderBy because lower scores are better)
            songSuggestData.sortedSuggestions = totalScore.OrderBy(s => s.Value).Select(s => s.Key).ToList();
            Console.WriteLine($"[EvaluateFilters] Sorted suggestions count: {songSuggestData.sortedSuggestions.Count}");
            
            if (songSuggestData.sortedSuggestions.Count > 0)
            {
                var bottomScores = totalScore.OrderBy(s => s.Value).Take(5).ToList();
                var topScores = totalScore.OrderByDescending(s => s.Value).Take(5).ToList();

                // Export top and bottom scores
                // HtmlExporter.ExportDictionary("TopScores", new Dictionary<string, double>(topScores));
                // HtmlExporter.ExportDictionary("BottomScores", new Dictionary<string, double>(bottomScores));
            }
        }

        //Filters out any songs that should not be in the generated playlist
        //Ignore All Played
        //Ignore X Days
        //Banned Songs
        //Songs that is not expected improveable
        public static void RemoveIgnoredSongs(SongSuggestData songSuggestData)
        {
            //Filter out ignoreSongs before making the playlist.
            //Get the ignore lists ready (permaban, ban, and improved within X days, not improveable by X ranks)
            List<string> ignoreSongs = CreateIgnoreLists(songSuggestData, false ? -1 : 14);
            songSuggestData.sortedSuggestions = songSuggestData.sortedSuggestions
                .Except(ignoreSongs)
                .ToList();
        }

        //Create a List of leaderboardID's to filter out. Consider splitting it so Permaban does not get links, while
        //standard temporary banned, and recently played gets removed after.
        //Send -1 if all played should be ignored, else amount of days to ignore.
        public static List<string> CreateIgnoreLists(SongSuggestData songSuggestData, int ignoreDays)
        {
            List<string> ignoreSongs = new List<string>();

            //Ignore recently/all played songs
            //Add either all played songs
            var playedSongs = songSuggestData.activePlayer.top10kScore.Select(s => s.leaderboardID).ToList();

            if (ignoreDays == -1)
            {
                ignoreSongs.AddRange(playedSongs);
            }
            // TODO: Add this back
            //Or the songs only played within a given time periode
            /*
            else
            {
                var filteredSongs = playedSongs
                    .Where(song => (DateTime.UtcNow - suggestSM.PlayerScoreDate(song)).TotalDays < ignoreDays)
                    .ToList();

                ignoreSongs.AddRange(filteredSongs);
            }*/

            //Add songs that is not expected to be improveable by X ranks
            if (songSuggestData.ignoreNonImproveable)
            {
                ignoreSongs.AddRange(LeaderboardNonImproveableFiltering(songSuggestData));
            }

            return ignoreSongs;
        }

        //Filtering out songs that are deemed nonimproveable.
        private static List<string> LeaderboardNonImproveableFiltering(SongSuggestData songSuggestData)
        {
            List<string> ignoreSongs = new List<string>();

            //Create a lookup for ranks of the leaderboards. If multiple leaderboards use the sub leaderboards rank.
            var scoreToRank = songSuggestData.sortedSuggestions
                .Select((leaderboardID, index) => new { leaderboardID, rank = index + 1 })                                             //Assign Rank Index (1-indexed)
                .ToDictionary(x => x.leaderboardID, x => x.rank);                                                               //Create a lookup for leaderboardID -> rank

            foreach (string leaderboardID in songSuggestData.sortedSuggestions)
            {
                int? currentSongRank = songSuggestData.activePlayer.top10kScore.FirstOrDefault(s => s.leaderboardID == leaderboardID)?.rank;
                if (currentSongRank == null)
                {
                    currentSongRank = -1;
                }

                //Add songs ID to ignore list if current rank is not expected improveable by at least X spots, and it is not an unplayed song
                if (currentSongRank < scoreToRank[leaderboardID] + 5 && currentSongRank != -1)
                {
                    ignoreSongs.Add(leaderboardID);
                }
            }

            return ignoreSongs;
        }

        //Goal here is to get a good sample of a players songs that are not banned. The goal is try and find originSongsCount candidates to represent a player.
        //We filter out round up 25% worst scores (keeping at least 1) to allow progression on actual scores on lower song counts by filtering bad fits earlier
        //Then we filter out the requested portion of low accuracy songs.
        public static List<string> SelectPlayedOriginSongs(SongSuggestData songSuggestData)
        {
            double maxKeepPercentage = 0.75;                                                //Percent of plays to keep on players with low playcount (Rounding is handled locally)

            //Find available songs
            var filteredSongs = songSuggestData.activePlayer.top10kScore                               //Grab leaderboardID's for songs matching the given Suggest Context from Source Manager
                .OrderByDescending(value => PlayerWeightedScoreValue(value.pp, value.rank))            //Order Songs by Leaderboards Effective value
                .ToList();
            Console.WriteLine($"[SelectPlayedOriginSongs] Total songs gathered: {filteredSongs.Count}");
            // HtmlExporter.ExportList("PlayedOriginSongs_Initial", filteredSongs.ConvertAll(x => new { LeaderboardID = x.leaderboardID, PP = x.pp, Rank = x.rank }));

            //To ensure worst songs are always removed (progression while getting enough songs) we only keep a certain percent of songs (75% default)
            int valueSongCount = filteredSongs.Count();
            valueSongCount = (int)(maxKeepPercentage * valueSongCount);     //Reduce the list to 75% best
            if (valueSongCount == 0) valueSongCount = 1;                    //If 1 is available, 1 should always be selected, but outside this goal is to reduce to 75% rounded down
            Console.WriteLine($"[SelectPlayedOriginSongs] Value song count (75% of total): {valueSongCount}");

            //Find the target song count after removing accuracy adjustments
             double percentToKeep = 50.0 / (songSuggestData.originLeaderboardsCount + songSuggestData.extraLeaderboardsCount);
            int comparativeBestCount = (int)Math.Ceiling(percentToKeep * valueSongCount);
            Console.WriteLine($"[SelectPlayedOriginSongs] Percent to keep: {percentToKeep:P}, Comparative best count: {comparativeBestCount}");

            // Step 1: Take 75% best
            var step1 = filteredSongs
                .Take(Math.Min(valueSongCount, songSuggestData.originLeaderboardsCount + songSuggestData.extraLeaderboardsCount))
                .ToList();

            // Step 2: Order by relative score
            var step2 = step1
                .OrderByDescending(c => PlayerRelativeScoreValue(c.pp, c.leaderboardID, songSuggestData.leaderboards))
                .ToList();

            // Step 3: Take comparative best (adaptive count)
            var step3 = step2
                .Take(comparativeBestCount)
                .ToList();

            // Step 4: Take top 50
            var step4 = step3
                .Take(50)
                .ToList();

            // Step 5: Reorder by weighted score
            var filteredLeaderboards = step4
                .OrderByDescending(c => PlayerWeightedScoreValue(c.pp, c.rank))
                .Select(x => x.leaderboardID)
                .ToList();
            Console.WriteLine($"[SelectPlayedOriginSongs] Final selected origin songs: {filteredLeaderboards.Count}");
            // HtmlExporter.ExportList("PlayedOriginSongs_Final", filteredLeaderboards.ConvertAll(x => new { LeaderboardID = x }));

            //Returns the found songs.
            return filteredLeaderboards;
        }

        public static List<string> GetFillerSongs(Top10kLeaderboards leaderboards)
        {
            //Find all songs in the leaderboard with at least a minimum of top scores, so we base seed songs on stuff that are linked to other stuff
            //We then order by lowest max scores, so we pick the likely easier songs (best metric on current leaderboards)
            //We keep a certain %'age of the remaining scores to get rid of the "highest ranked" ones
            //And then we sort by the best average rank in the list, so we get strong songs as candidates. (This is a mix of best of the worst)

            //Taste Testing 30% seemed to get spread results on the leaderboards
            double percentToLookIn = 0.30;

            var fillerSongCandidates = leaderboards.top10kLeaderboardMeta
                .Where(c => c.Value.count >= 20)                                  //Linked Up songs only
                .OrderBy(c => c.Value.averageScore)                                     //Picks lowest value to get easier songs (Need better handling)
                .ToList();

            // HtmlExporter.ExportList("FillerSongs_Candidates", fillerSongCandidates.ConvertAll(x => new { LeaderboardID = x.Key, Count = x.Value.count, AverageScore = x.Value.averageScore }));

            int targetCount = (int)(percentToLookIn * fillerSongCandidates.Count()) + 1;  //Int rounds down, lets keep at least 1 song. Take cannot overflow.

            var fillerleaderboardIDs = fillerSongCandidates
                .Take(targetCount)                                                      //Testing found this %'age to give a mix of old and new, and lower amount of horrible stuff
                .OrderBy(c => c.Value.totalRank / c.Value.count)                        //Selecting best average rank, makes strong candidates appear
                .Select(c => c.Value.leaderboardID)
                .ToList();

            // HtmlExporter.ExportList("FillerSongs_Final", fillerleaderboardIDs.ConvertAll(x => new { LeaderboardID = x }));

            return fillerleaderboardIDs;
        }

        internal static double PlayerWeightedScoreValue(float pp, int rank)
        {
            return pp * Math.Pow(0.965, rank - 1);
        }

        public static double PlayerRelativeScoreValue(float pp, string leaderboardID, Top10kLeaderboards leaderboards)
        {
            double playerScore = pp;
            string songString = leaderboardID;
            //Try to lookup the value of the songs max if known, else 0 is used
            //(should filter out the song as a candidate, which is good, we want candidates that can link to plays).
            double leaderboardMax = leaderboards.top10kLeaderboardMeta.TryGetValue(songString, out var songMeta)
                ? songMeta.maxScore
                : 0;
            double relativeScore = leaderboardMax != 0 ? playerScore / leaderboardMax : 0;
            return relativeScore;
        }

        //Make Playlist
        public async static void CreatePlaylist(AppContext appContext, SongSuggestData songSuggestData, bool unplayed)
        {
            // HtmlExporter.ExportList("FinalSortedSuggestions", songSuggestData.sortedSuggestions.ConvertAll(x => new { LeaderboardID = x }));
            
            var idOrder = songSuggestData.sortedSuggestions
            .Select((id, index) => new { id, index })
            .ToDictionary(x => x.id, x => x.index);

            var query = appContext.Leaderboards
            .Where(l => songSuggestData.sortedSuggestions.Contains(l.Id));

            if (unplayed)
            {
                query = query.Where(l => !l.Scores.Any(s => s.PlayerId == songSuggestData.playerID));
            }

            var songs = query
                .AsEnumerable() // switch to memory HERE
                .OrderBy(l => idOrder[l.Id]) // dictionary lookup in memory
                .Take(50)
                .Select(item => new Song
                {
                    hash = item.Hash,
                    songName = item.Name,
                    levelAuthorName = item.Mapper,
                    levelid = "custom_level_" + item.Hash,
                    difficulties = new Difficulty[]
                    {
                new Difficulty
                {
                    name = item.DifficultyName.LowercaseFirstChar(),
                    characteristic = item.ModeName
                }
                    }
                })
                .ToArray();

            // HtmlExporter.ExportList("PlaylistFinalSongs", songs.ToList().ConvertAll(x => new { Name = x.songName, Hash = x.hash, Mapper = x.levelAuthorName, Difficulty = x.difficulties?.FirstOrDefault()?.name }));

            var playlist = new Playlist
            {
                playlistTitle = $"Recommendations for {songSuggestData.playerID}",
                playlistAuthor = "BeatLeader",
                songs = songs,
                customData = new Customdata
                {
                    syncURL = "",
                    owner = songSuggestData.playerID,
                    id = "",
                    hash = "",
                    shared = false
                },
                image = ""
            };

            Console.WriteLine($"[CreatePlaylist] Exporting playlist with {songs.Length} songs for player {songSuggestData.playerID}...");
            await RecommendationService.ExportPlaylistToJsonAsync(playlist, songSuggestData.playerID);
        }
    }
}
