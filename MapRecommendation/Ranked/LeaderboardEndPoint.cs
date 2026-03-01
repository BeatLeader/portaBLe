namespace portaBLe.MapRecommendation.Ranked
{
    public class LeaderboardEndPoint
    {
        public string leaderboardID { get; set; }
        public List<LeaderboardLink> songLinks = new List<LeaderboardLink>();
        public float totalRelevanceScore = 0;
        public double totalRank = 0;
        public int matchedSongs = 0;
        public float weightedSongs = 0;
        public double averageRank = 0;

        //Total of all unique grouping of Origin -> Endpoint links distance averages.
        public double totalDistance = 0;
        public double averageDistance = 0;

        //StyleFilter data
        public double proportionalStyle = 0;

        //PP Estimate
        public double estimatedPP = 0;

        //Local PP vs Global
        public double localPPAverage = 0;
        public double localVSGlobalPP = 0;


        public void SetRelevance(int originPoints, int requiredMatches)
        {
            //Calculate averageRank with a minimum link amount, center vs 10.50 (1+2+...20)/20
            float minRankLinks = requiredMatches;//50;
            float rankSum = songLinks.Select(c => c.targetSongScore.rank).Sum();
            float rankLinks = songLinks.Count;
            rankSum += Math.Max(minRankLinks - rankLinks, 0.0f) * 10.5f;
            averageRank = rankSum / Math.Max(minRankLinks, rankLinks);
        }

        public void SetStyle(LeaderboardEndPointCollection originSongs)
        {
            List<string> originSongIDs = songLinks
                .Select(c => c.originSongScore.leaderboardID)
                .Distinct()
                .ToList();
            foreach (string originSongID in originSongIDs)
            {
                int originSongCount = originSongs.endPoints[originSongID].songLinks.Count();
                int linkedCount = songLinks.Select(c => c.originSongScore.leaderboardID == leaderboardID).Count();
                proportionalStyle += 1.0 * linkedCount / originSongCount;
            }
        }

        ////---New Filter---
        //private double _style;
        //public double Style { get
        //{
        //        if (_style == 0) _style = songLinks.Sum(c => c.Style);

        //    return _style;
        //}}

    }
}
