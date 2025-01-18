using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace portaBLe
{
    public class Player
    {
        [StringLength(25, MinimumLength = 0)]
        public string Id { get; set; }
        public float Pp { get; set; }
        public float AccPp { get; set; }
        public float TechPp { get; set; }
        public float PassPp { get; set; }
        public int Rank { get; set; }
        [StringLength(50, MinimumLength = 0)]
        public string Name { get; set; }
        [StringLength(5, MinimumLength = 0)]
        public string Country { get; set; }
        public int CountryRank { get; set; }
        [StringLength(100, MinimumLength = 0)]
        public string Avatar { get; set; }

        public float TopPp { get; set; }
        public int RankedPlayCount { get; set; }
    }

    public class Score
    {
        public int Id { get; set; }

        public int Timepost { get; set; }
        public float Pp { get; set; }
        public float AccPP { get; set; }
        public float TechPP { get; set; }
        public float PassPP { get; set; }
        public float BonusPp { get; set; }
        public float Weight { get; set; }
        [StringLength(25, MinimumLength = 0)]
        public string PlayerId { get; set; }
        public int Rank { get; set; }
        public Player Player { get; set; }
        [StringLength(25, MinimumLength = 0)]
        public string LeaderboardId { get; set; }
        public Leaderboard Leaderboard { get; set; }
        public float Accuracy { get; set; }
        public string Modifiers { get; set; }
        public bool FC { get; set; }
        public float FCAcc { get; set; }
    }

    public class ModifiersRating
    {
        public int Id { get; set; }
        public float FSPredictedAcc { get; set; }
        public float FSPassRating { get; set; }
        public float FSAccRating { get; set; }
        public float FSTechRating { get; set; }
        public float FSStars { get; set; }

        public float SSPredictedAcc { get; set; }
        public float SSPassRating { get; set; }
        public float SSAccRating { get; set; }
        public float SSTechRating { get; set; }
        public float SSStars { get; set; }

        public float SFPredictedAcc { get; set; }
        public float SFPassRating { get; set; }
        public float SFAccRating { get; set; }
        public float SFTechRating { get; set; }
        public float SFStars { get; set; }
    }

    public class Leaderboard
    {
        [StringLength(25, MinimumLength = 0)]
        public string Id { get; set; }
        [StringLength(150, MinimumLength = 0)]
        public string Name { get; set; }
        public string Hash { get; set; }
        [StringLength(25, MinimumLength = 0)]
        public string SongId { get; set; }
        [StringLength(25, MinimumLength = 0)]
        public string ModeName { get; set; }
        [StringLength(25, MinimumLength = 0)]
        public string DifficultyName { get; set; }
        [StringLength(150, MinimumLength = 0)]
        public string Cover { get; set; }
        [StringLength(200, MinimumLength = 0)]
        public string Mapper { get; set; }

        public float Stars { get; set; }
        public float PassRating { get; set; }
        public float AccRating { get; set; }
        public float TechRating { get; set; }

        public float PredictedAcc { get; set; }
        public ModifiersRating? ModifiersRating { get; set; }
        public ICollection<Score> Scores { get; set; }

        public int Count { get; set; }
        public int Count80 { get; set; }
        public int Count95 { get; set; }
        public float Average { get; set; }
        public float Top250 { get; set; }
        public float TotalPP { get; set; }
        public float PPRatioFiltered { get; set; }
        public float PPRatioUnfiltered { get; set; }
        public float Percentile { get; set; }
        public float Megametric { get; set; }
        public float Megametric125 { get; set; }
        public float Megametric75 { get; set; }
        public float Megametric40 { get; set; }

        [NotMapped]
        public float Count80Ratio => Count > 0 ? Count80 / (float)Count : 0;
        [NotMapped]
        public float Count95Ratio => Count > 0 ? Count95 / (float)Count : 0;
        [NotMapped]
        public float Count95to80Ratio => Count80 > 0 ? Count95 / (float)Count80 : 0;
    }
}