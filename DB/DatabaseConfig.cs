namespace portaBLe.DB
{
    public class DatabaseConfig
    {
        public string Name { get; set; }
        public string FileName { get; set; }
        public string Description { get; set; }
    }

    public class DatabasesConfig
    {
        public int MainDB { get; set; }
        public List<DatabaseConfig> Databases { get; set; } = new List<DatabaseConfig>();
    }
}

