namespace portaBLe.MapRecommendation.Ranked
{
    internal static class SongNameHelper
    {
        internal static string GetSongName(string songID, AppContext appContext)
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
    }
}
