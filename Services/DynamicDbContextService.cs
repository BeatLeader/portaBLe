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
        string? Listed(string? fileName);
        string ResolveDatabaseFileName(string? requested);
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
            // -1 (or out of range): the last database listed
            int index = _config.MainDB >= 0 && _config.MainDB < _config.Databases.Count ? _config.MainDB : _config.Databases.Count - 1;
            return _config.Databases[index].FileName;
        }

        // The file name when it's a database listed in databases.json and present on disk, else null: a ?db= from an old link
        // or bookmark (database files are renamed each time one is replaced) or one that wasn't downloaded
        public string? Listed(string? fileName) =>
            !string.IsNullOrEmpty(fileName) && _config.Databases.Any(d => d.FileName == fileName) && File.Exists(Path.Combine(_webRootPath, fileName))
                ? fileName : null;

        // The database a page shows: the one asked for when it's listed and present, else the main one
        public string ResolveDatabaseFileName(string? requested) => Listed(requested) ?? GetMainDatabaseFileName();

        public DbContext CreateContext(string fileName)
        {
            // Never a file that isn't there: SQLite would create it empty ("no such table" on the page), and the next startup
            // would take it for the downloaded database
            fileName = ResolveDatabaseFileName(fileName);
            var options = _contextOptionsCache.GetOrAdd(fileName, fn =>
            {
                var builder = new DbContextOptionsBuilder<DynamicDbContext>();
                var connectionString = $"Data Source={Path.Combine(_webRootPath, fn)};Mode=ReadWrite;";
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

                // Its Top Scores page cache, when uploaded next to it (else the page builds it in the background)
                var cachePath = TopScoresCache.CachePathOf(localPath);
                if (File.Exists(localPath) && !File.Exists(cachePath))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                        if (await Program.DownloadDatabaseFileAsync(db.FileName + ".topscores.bin", cachePath))
                            Console.WriteLine($"Downloaded the top scores cache of {db.Name}");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"No top scores cache downloaded for {db.Name}: {e.Message}");
                        try { if (File.Exists(cachePath)) File.Delete(cachePath); } catch { }
                    }
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

        protected override void OnModelCreating(ModelBuilder modelBuilder) => CurveModelConfig.Apply(modelBuilder);
    }
}