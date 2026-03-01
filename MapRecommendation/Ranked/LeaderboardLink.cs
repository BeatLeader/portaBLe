namespace portaBLe.MapRecommendation.Ranked
{
    public class LeaderboardLink
    {
        public string playerID { get; set; }
        public Top10kScore originSongScore { get; set; }
        public Top10kScore targetSongScore { get; set; }
        public double distance;

        //---2025-03-17: New Calculations Data---
        public LeaderboardEndPoint OriginScoreEndPoint;
        public LeaderboardEndPoint TargetScoreEndPoint;
    }
}
