using ProtoBuf;

namespace portaBLe
{
    [ProtoContract]
    public class BigExportResponse
    {
        [ProtoMember(1)]
        public List<ExportPlayer> Players { get; set; }
        [ProtoMember(2)]
        public List<ExportScore> Scores { get; set; }
        [ProtoMember(3)]
        public List<ExportMap> Maps { get; set; }
    }

    [ProtoContract]
    public class ExportPlayer
    {
        [ProtoMember(1)]
        public string Name { get; set; }
        [ProtoMember(2)]
        public string Country { get; set; }
        [ProtoMember(3)]
        public string Id { get; set; }
        [ProtoMember(4)]
        public string Avatar { get; set; }
    }

    [ProtoContract]
    public class ExportScore
    {
        [ProtoMember(1)]
        public int Id { get; set; }
        [ProtoMember(2)]
        public string LeaderboardId { get; set; }
        [ProtoMember(3)]
        public float Accuracy { get; set; }
        [ProtoMember(4)]
        public string Modifiers { get; set; }
        [ProtoMember(5)]
        public string PlayerId { get; set; }
        [ProtoMember(6)]
        public int Timepost { get; set; }
        [ProtoMember(7)]
        public bool FC { get; set; }
        [ProtoMember(8)]
        public float FCAcc { get; set; }
    }

    [ProtoContract]
    public class ExportMap
    {
        [ProtoMember(1)]
        public string Hash { get; set; }
        [ProtoMember(2)]
        public string Name { get; set; }
        [ProtoMember(3)]
        public string CoverImage { get; set; }
        [ProtoMember(4)]
        public string Mapper { get; set; }
        [ProtoMember(5)]
        public string Id { get; set; }
        [ProtoMember(6)]
        public string SongId { get; set; }
        [ProtoMember(7)]
        public string ModeName { get; set; }
        [ProtoMember(8)]
        public string DifficultyName { get; set; }
        [ProtoMember(9)]
        public float? AccRating { get; set; }
        [ProtoMember(10)]
        public float? PassRating { get; set; }
        [ProtoMember(11)]
        public float? TechRating { get; set; }
        [ProtoMember(12)]
        public float? PredictedAcc { get; set; }
        [ProtoMember(13)]
        public ExportModifiersRating ModifiersRating { get; set; }
    }

    [ProtoContract]
    public class ExportModifiersRating
    {
        [ProtoMember(1)]  public float SSPredictedAcc { get; set; }
        [ProtoMember(2)]  public float SSPassRating { get; set; }
        [ProtoMember(3)]  public float SSAccRating { get; set; }
        [ProtoMember(4)]  public float SSTechRating { get; set; }
        [ProtoMember(5)]  public float SSStars { get; set; }
        [ProtoMember(6)]  public float FSPredictedAcc { get; set; }
        [ProtoMember(7)]  public float FSPassRating { get; set; }
        [ProtoMember(8)]  public float FSAccRating { get; set; }
        [ProtoMember(9)]  public float FSTechRating { get; set; }
        [ProtoMember(10)] public float FSStars { get; set; }
        [ProtoMember(11)] public float SFPredictedAcc { get; set; }
        [ProtoMember(12)] public float SFPassRating { get; set; }
        [ProtoMember(13)] public float SFAccRating { get; set; }
        [ProtoMember(14)] public float SFTechRating { get; set; }
        [ProtoMember(15)] public float SFStars { get; set; }
        [ProtoMember(16)] public float BFSPredictedAcc { get; set; }
        [ProtoMember(17)] public float BFSPassRating { get; set; }
        [ProtoMember(18)] public float BFSAccRating { get; set; }
        [ProtoMember(19)] public float BFSTechRating { get; set; }
        [ProtoMember(20)] public float BFSStars { get; set; }
        [ProtoMember(21)] public float BSFPredictedAcc { get; set; }
        [ProtoMember(22)] public float BSFPassRating { get; set; }
        [ProtoMember(23)] public float BSFAccRating { get; set; }
        [ProtoMember(24)] public float BSFTechRating { get; set; }
        [ProtoMember(25)] public float BSFStars { get; set; }

        public ModifiersRating ToDBModel()
        {
            return new ModifiersRating
            {
                Id = 0,
                SSPredictedAcc = SSPredictedAcc,
                SSPassRating = SSPassRating,
                SSAccRating = SSAccRating,
                SSTechRating = SSTechRating,
                SSStars = SSStars,
                FSPredictedAcc = FSPredictedAcc,
                FSPassRating = FSPassRating,
                FSAccRating = FSAccRating,
                FSTechRating = FSTechRating,
                FSStars = FSStars,
                SFPredictedAcc = SFPredictedAcc,
                SFPassRating = SFPassRating,
                SFAccRating = SFAccRating,
                SFTechRating = SFTechRating,
                SFStars = SFStars,
            };
        }
    }
}
