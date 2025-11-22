namespace portaBLe.DB
{
    public class JsonPlayer
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Country { get; set; }
        public string Avatar { get; set; }
    }

    public class JsonScore
    {
        public int Id { get; set; }
        public int Timepost { get; set; }
        public string PlayerId { get; set; }
        public string LeaderboardId { get; set; }
        public float Accuracy { get; set; }
        public string Modifiers { get; set; }
        public bool FC { get; set; }
        public float FCAcc { get; set; }
    }

    public class JsonLeaderboard
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Hash { get; set; }
        public string SongId { get; set; }
        public string ModeName { get; set; }
        public string DifficultyName { get; set; }
        public string CoverImage { get; set; }
        public string Mapper { get; set; }

        public float PassRating { get; set; }
        public float AccRating { get; set; }
        public float TechRating { get; set; }

        public float PredictedAcc { get; set; }
        public ModifiersRating? ModifiersRating { get; set; }
    }

    public class RootObject
    {
        public List<JsonPlayer> Players { get; set; }
        public List<JsonScore> Scores { get; set; }
        public List<JsonLeaderboard> Maps { get; set; }
    }
}
