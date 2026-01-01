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
        Task DownloadAllDatabasesAsync(string webRootPath);
        Task<bool> IsDatabaseDownloadedAsync(string fileName, string webRootPath);
    }

    public class DynamicDbContextService : IDynamicDbContextService
    {
        private readonly string _webRootPath;
        private readonly ConcurrentDictionary<string, DbContextOptions> _contextOptionsCache;
        private DatabasesConfig _config;
        private readonly SemaphoreSlim _configLock = new SemaphoreSlim(1, 1);

        public DynamicDbContextService(IWebHostEnvironment env)
        {
            _webRootPath = env.WebRootPath;
            _contextOptionsCache = new ConcurrentDictionary<string, DbContextOptions>();
            LoadConfiguration();
        }

        private void LoadConfiguration()
        {
            var configPath = Path.Combine(_webRootPath, "databases.json");
            
            if (!File.Exists(configPath))
            {
                // Create default configuration
                _config = new DatabasesConfig
                {
                    MainDB = 0,
                    Databases = new List<DatabaseConfig>
                    {
                        new DatabaseConfig
                        {
                            Name = "Current Database",
                            FileName = "Database.db",
                            Description = "Current production database"
                        }
                    }
                };
                
                // Save default config
                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
            }
            else
            {
                var json = File.ReadAllText(configPath);
                _config = JsonSerializer.Deserialize<DatabasesConfig>(json, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });
            }
        }

        public async Task<List<DatabaseConfig>> GetAvailableDatabasesAsync()
        {
            await _configLock.WaitAsync();
            try
            {
                LoadConfiguration(); // Reload in case it changed
                return _config.Databases.ToList();
            }
            finally
            {
                _configLock.Release();
            }
        }

        public string GetMainDatabaseFileName()
        {
            return _config.Databases[_config.MainDB].FileName;
        }

        public DbContext CreateContext(string fileName)
        {
            var options = _contextOptionsCache.GetOrAdd(fileName, fn =>
            {
                var builder = new DbContextOptionsBuilder<DynamicDbContext>();
                var connectionString = $"Data Source={Path.Combine(_webRootPath, fn)};";
                builder.UseSqlite(connectionString);
                return builder.Options;
            });

            return new DynamicDbContext(options);
        }

        public async Task<bool> IsDatabaseDownloadedAsync(string fileName, string webRootPath)
        {
            var localPath = Path.Combine(webRootPath, fileName);
            return File.Exists(localPath);
        }

        public async Task DownloadAllDatabasesAsync(string webRootPath)
        {
            var databases = await GetAvailableDatabasesAsync();
            
            foreach (var db in databases)
            {
                var localPath = Path.Combine(webRootPath, db.FileName);
                
                if (!File.Exists(localPath))
                {
                    Console.WriteLine($"Downloading {db.Name} ({db.FileName})...");
                    bool success = await Program.DownloadDatabaseFileAsync(db.FileName, localPath);
                    
                    if (success)
                    {
                        Console.WriteLine($"Successfully downloaded {db.Name}");
                    }
                    else
                    {
                        Console.WriteLine($"Failed to download {db.Name}");
                    }
                }
                else
                {
                    Console.WriteLine($"{db.Name} already exists locally.");
                }
            }
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

