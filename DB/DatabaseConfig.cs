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
        // Index in Databases of the one the website opens on; -1 (or out of range) = the last one, so the newest database
        // listed is the default
        public int MainDB { get; set; }
        public List<DatabaseConfig> Databases { get; set; } = new List<DatabaseConfig>();
    }
}