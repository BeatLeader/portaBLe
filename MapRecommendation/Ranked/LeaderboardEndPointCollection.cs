namespace portaBLe.MapRecommendation.Ranked
{
    public class LeaderboardEndPointCollection
    {

        public Dictionary<string, LeaderboardEndPoint> endPoints = new Dictionary<string, LeaderboardEndPoint>();

        public void SetRelevance(int originPoints, int requiredMatches)
        {
            foreach (LeaderboardEndPoint songEndPoint in endPoints.Values)
            {
                songEndPoint.SetRelevance(originPoints, requiredMatches);
            }
        }

        public void SetStyle(LeaderboardEndPointCollection originSongs)
        {
            foreach (LeaderboardEndPoint songEndPoint in endPoints.Values)
            {
                songEndPoint.SetStyle(originSongs);
            }
        }
    }
}
