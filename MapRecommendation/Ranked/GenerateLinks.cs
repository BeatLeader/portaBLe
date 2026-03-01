namespace portaBLe.MapRecommendation.Ranked
{
    public class GenerateLinks
    {
        //Decides how many of the songs are kept .7 works great.

        //Create the linking of the songs found to represent a player, with matching songs on the leaderboard.
        //origin -> matching leaderboard player -> other top songs from that leaderboard player
        //Goal with this is creating a link structure for future evaluations of their strength, and place them so they are easy to reach from either side.
        //OriginSongs <-> SongLink <-> Target Songs
        public static void Execute(SongSuggestData songSuggestData)
        {

            //Generate the initial endpoints dictionary and attach it to the originLeaderboards
            var endPoints = songSuggestData.originleaderboardIDs
                .Select(leaderboardID => new LeaderboardEndPoint { leaderboardID = leaderboardID })
                .ToDictionary(endpoint => endpoint.leaderboardID, endpoint => endpoint);

            var originLeaderboards = new LeaderboardEndPointCollection() { endPoints = endPoints };

            //Set values to local values that are reused multiple times, no need to calculate them every time. (empty leaderboard needs to be handled, we set max rank to 0, it should never be used).
            int maxRank = songSuggestData.leaderboards.top10kPlayers.Any() ? songSuggestData.leaderboards.top10kPlayers.Max(c => c.rank) : 0;

            //We are preparing the actual linking between a playrs origin songs to their suggested target songs.
            //origin -> matching leaderboard player -> other leaderboard players songs
            var links = songSuggestData.leaderboards.top10kPlayers                                          //Get reference to the top 10k players data                        
                .Where(player => ValidateLinkPlayer(player, songSuggestData.playerID))                     //Remove active player so player does not use their own data
                .SelectMany(linkPlayer => linkPlayer.top10kScore                                //Get the players scores
                    .Where(originSongCandidate => ValidateOriginSong(songSuggestData.originleaderboardIDs, originSongCandidate)) //Remove scores that does not fit filtering for Origin Song.
                    .Select(originLeaderboard => new { player = linkPlayer, originLeaderboard = originLeaderboard }) //Keep variables needed for creating SongLinks
                )
                .SelectMany(originLinks => originLinks.player.top10kScore                                                                       //Get the players other scores to link with themselves.
                    .Where(potentialTargetSong => ValidateTargetSong(originLinks.originLeaderboard, potentialTargetSong))                        //Remove the selflink and bans
                    .Select(targetLeaderboard => new { player = originLinks.player, originLeaderboard = originLinks.originLeaderboard, targetLeaderboard = targetLeaderboard })    //Store needed variables again
                )
                .Select(linkData => new { link = GenerateSongLink(linkData.player, linkData.originLeaderboard, linkData.targetLeaderboard, maxRank), index = linkData.player.rank })    //Create songlinks for further processing
                .OrderBy(c => c.link.distance)
                .ToList();

            //Calculate the amount of songs to keep. 70-80% seems good initial target compared to current values. Testing with 70 as it seems it gives "easier" for now
            int targets = (int)Math.Ceiling(0.7 * links.Count);
            links = links.Take(targets).ToList();

            //Update found links
            var linkedSongsCount = links.Count();

            //Reset the targetPoints endpoint
            var targetLeaderboards = new LeaderboardEndPointCollection();

            //Link the links to the endpoints
            foreach (var item in links)
            {
                //Add the songlink to the origin list
                string originSongID = item.link.originSongScore.leaderboardID;
                originLeaderboards.endPoints[originSongID].songLinks.Add(item.link);

                //Create the target endpoint if needed.
                string targetSongID = item.link.targetSongScore.leaderboardID;
                if (!targetLeaderboards.endPoints.ContainsKey(targetSongID))
                {
                    var endPoint = new LeaderboardEndPoint { leaderboardID = targetSongID };
                    targetLeaderboards.endPoints.Add(targetSongID, endPoint);
                }

                //Add the songlink to the target list
                targetLeaderboards.endPoints[targetSongID].songLinks.Add(item.link);

                //Update Links endpoints references
                item.link.OriginScoreEndPoint = originLeaderboards.endPoints[originSongID];
                item.link.TargetScoreEndPoint = targetLeaderboards.endPoints[targetSongID];
            }

            songSuggestData.originLeaderboards = originLeaderboards;
            songSuggestData.targetLeaderboards = targetLeaderboards;
        }

        //Filters players that should not be used. (Only filters out the active player currently, previous versions checked global rank and such, but it did not improve results).
        private static bool ValidateLinkPlayer(Top10kPlayer player, String playerID)
        {
            return player.id != playerID;
        }

        //Removes songs that are to be ignored, as well as songs linking itself.
        private static bool ValidateTargetSong(Top10kScore originLeaderboard, Top10kScore suggestedSong)
        {
            string suggestedSongID = suggestedSong.leaderboardID;
            return suggestedSong.rank != originLeaderboard.rank;
        }

        //Generate the Song Link, as well as set the aproximate completion, as majority of loop should be in this part
        private static LeaderboardLink GenerateSongLink(Top10kPlayer player, Top10kScore originLeaderboard, Top10kScore suggestedSong, int maxRank)
        {
            //If originsongs PP is 0, it is because it is a seed/liked song, so it should be treated as optimal distance
            //Else we calculate the absolute distance (over or under does not matter)
            double distance = 0;
            if (originLeaderboard.pp != 0)
            {
                //Testing showed this distribution gives a good split between harder/easier songs for ordering. Would have expected 4.0 as it matched older system more with
                //Default 70% kept links for normal songs ... for Acc Saber this needs reduced.
                distance = Math.Abs(Math.Pow(suggestedSong.pp / originLeaderboard.pp, 3.0) - 1);
            }

            var songLink = new LeaderboardLink()
            {
                playerID = player.id,
                originSongScore = originLeaderboard,
                targetSongScore = suggestedSong,
                distance = distance,
            };
            return songLink;
        }

        //Filter the active score, we check first for the score having a match with origin songs, then if the score has a Score Value (liked songs will not have this but should be kept).
        //And we make sure it is within the better/worse Acc Cap range. Finally once a score is validated we increase validated score counts.

        private static bool ValidateOriginSong(List<string> originSongIDs, Top10kScore originSongCandidate)
        {
            var originSongCandidateID = originSongCandidate.leaderboardID;
            //Return false if song is not in the list of songs we are looking for.
            if (!originSongIDs.Contains(originSongCandidateID)) return false;

            //**TEST** Keep all links, distance is done later
            return true;

            ////Score validation check, we need to fail only if the song is not unplayed (0 value), and is outside the given limits. Single lining this is prone to errors.
            //double playerSongValue = data.suggestSM.PlayerScoreValue(originSongCandidate.songID);
            //bool invalidScore = true;
            //if (playerSongValue == 0) invalidScore = false;
            //if ((originSongCandidate.pp < (playerSongValue * data.betterAccCap)) && (originSongCandidate.pp > (playerSongValue * data.worseAccCap))) invalidScore = false;
            //if (invalidScore) return false; //Other checks could be later, hence the return for this section.

            //return true; //All checks passed
        }
    }
}
