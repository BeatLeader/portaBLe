namespace portaBLe.MapRecommendation.Ranked
{
    public class SongSuggestDataService
    {
        public SongSuggestData Data { get; set; }

        /// <summary>
        /// Cached leaderboard data from BeatLeader API. Populated once at startup.
        /// Reused across multiple SuggestedSongs runs without re-fetching.
        /// </summary>
        public Top10kLeaderboards CachedLeaderboards { get; set; }
    }
}
