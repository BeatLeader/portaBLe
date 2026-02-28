namespace portaBLe.MapRecommendation
{
    public class Playlist
    {
        public string playlistTitle { get; set; }
        public string playlistAuthor { get; set; }
        public Customdata customData { get; set; }
        public Song[] songs { get; set; }
        public string image { get; set; }
    }

    public class Customdata
    {
        public string syncURL { get; set; }
        public string owner { get; set; }
        public string id { get; set; }
        public string hash { get; set; }
        public bool shared { get; set; }
    }

    public class Song
    {
        public string songName { get; set; }
        public string levelAuthorName { get; set; }
        public string hash { get; set; }
        public string levelid { get; set; }
        public Customdata1 customData { get; set; }
        public Difficulty[] difficulties { get; set; }
    }

    public class Customdata1
    {
        public int UploadTime { get; set; }
    }

    public class Difficulty
    {
        public string characteristic { get; set; }
        public string name { get; set; }
    }
}
