using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using System.Collections.Concurrent;
using System.Text.Json;

namespace portaBLe.Services
{
    public interface IDynamicDbContextService
    {
        Task<List<DatabaseConfig>> GetAvailableDatabasesAsync();
        DbContext CreateContext(string fileName);
        string GetMainDatabaseFileName();
    }

    /// <summary>
    /// Databases the pages can switch between (the navbar selector, DB Comparison, DB Metrics).
    /// wwwroot/databases.json lists them when present (local multi-DB setups); otherwise this instance's own databases are
    /// offered: Database.db, plus Comparison.db when it exists (a portaBLe Hub deployment). Only listed files can be opened, and
    /// only read-only: the file name comes from the ?db= query string, so anything else would let a request open or create
    /// arbitrary SQLite files.
    /// </summary>
    public class DynamicDbContextService : IDynamicDbContextService
    {
        private readonly string _webRootPath;
        private readonly string _mainPath, _comparisonPath;
        private readonly ConcurrentDictionary<string, DbContextOptions> _contextOptionsCache = new();

        /// <param name="mainPath">the app's main database (--db), default wwwroot/Database.db</param>
        /// <param name="comparisonPath">the app's comparison database (--comparison), default wwwroot/Comparison.db</param>
        public DynamicDbContextService(IWebHostEnvironment env, string? mainPath = null, string? comparisonPath = null)
        {
            _webRootPath = env.WebRootPath;
            _mainPath = mainPath ?? Path.Combine(_webRootPath, "Database.db");
            _comparisonPath = comparisonPath ?? Path.Combine(_webRootPath, "Comparison.db");
        }

        // the default entries stand for the app's own databases; databases.json entries are files in wwwroot
        private string PathOf(string fileName) => fileName switch
        {
            "Database.db" => _mainPath,
            "Comparison.db" => _comparisonPath,
            _ => Path.Combine(_webRootPath, fileName),
        };

        private DatabasesConfig LoadConfiguration()
        {
            var configPath = Path.Combine(_webRootPath, "databases.json");
            if (File.Exists(configPath))
            {
                var config = JsonSerializer.Deserialize<DatabasesConfig>(File.ReadAllText(configPath), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (config != null && config.Databases.Count > 0) return config;
            }

            var keyFile = Path.Combine(_webRootPath, "current_db_name.txt");
            var databases = new List<DatabaseConfig>
            {
                new DatabaseConfig
                {
                    Name = "Main database",
                    FileName = "Database.db",
                    Description = File.Exists(keyFile) ? File.ReadAllText(keyFile).Trim() : "this instance's database"
                }
            };
            if (File.Exists(_comparisonPath))
            {
                databases.Add(new DatabaseConfig { Name = "Comparison database", FileName = "Comparison.db", Description = "Comparison.db" });
            }
            return new DatabasesConfig { MainDB = 0, Databases = databases };
        }

        public Task<List<DatabaseConfig>> GetAvailableDatabasesAsync() => Task.FromResult(LoadConfiguration().Databases.ToList());

        public string GetMainDatabaseFileName()
        {
            var config = LoadConfiguration();
            return config.Databases[Math.Clamp(config.MainDB, 0, config.Databases.Count - 1)].FileName;
        }

        public DbContext CreateContext(string fileName)
        {
            var config = LoadConfiguration();
            if (string.IsNullOrEmpty(fileName) || !config.Databases.Any(d => d.FileName == fileName))
            {
                fileName = GetMainDatabaseFileName();
            }

            var options = _contextOptionsCache.GetOrAdd(fileName, fn =>
            {
                var builder = new DbContextOptionsBuilder<DynamicDbContext>();
                builder.UseSqlite($"Data Source={PathOf(fn)};Mode=ReadOnly;");
                return builder.Options;
            });

            return new DynamicDbContext(options);
        }
    }

    // Dynamic DB Context that can be created at runtime
    public class DynamicDbContext : DbContext
    {
        public DynamicDbContext(DbContextOptions options) : base(options)
        {
        }

        public DbSet<Player> Players { get; set; }
        public DbSet<Score> Scores { get; set; }
        public DbSet<Leaderboard> Leaderboards { get; set; }
        public DbSet<ModifiersRating> ModifiersRating { get; set; }
        public DbSet<DB.Stats> Stats { get; set; }
    }
}
