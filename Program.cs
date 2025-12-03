using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using Amazon.S3;
using Amazon.S3.Transfer;
using System.Text.Json;
using Amazon.S3.Model;
using Amazon;
using System.Diagnostics;
using portaBLe.DB;
using portaBLe.Refresh;

namespace portaBLe
{
    public class AppContext : DbContext
    {
        public AppContext(DbContextOptions<AppContext> options)
            : base(options)
        { }

        public DbSet<Player> Players { get; set; }
        public DbSet<Score> Scores { get; set; }
        public DbSet<Leaderboard> Leaderboards { get; set; }
        public DbSet<ModifiersRating> ModifiersRating { get; set; }
    }

    public class ComparisonContext : DbContext
    {
        public ComparisonContext(DbContextOptions<ComparisonContext> options)
            : base(options)
        { }

        public DbSet<Player> Players { get; set; }
        public DbSet<Score> Scores { get; set; }
        public DbSet<Leaderboard> Leaderboards { get; set; }
        public DbSet<ModifiersRating> ModifiersRating { get; set; }
    }

    public class Program
    {
        public static RootObject ParseJson(string path)
        {
            using FileStream openStream = File.OpenRead(path);
            using ZipArchive archive = new ZipArchive(openStream, ZipArchiveMode.Read);
            ZipArchiveEntry entry = archive.Entries[0];
            using Stream entryStream = entry.Open();
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            return JsonSerializer.Deserialize<RootObject>(entryStream, options);
        }

        public static void InitializeDatabase(IHost host)
        {
            using (var scope = host.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                var dbContext = services.GetRequiredService<AppContext>();
                dbContext.Database.EnsureCreated();

                try {
                    dbContext.Database.Migrate();
                } catch { }
            }
        }

        public static async Task ImportDump(IHost host)
        {
            using (var scope = host.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                var dbContextFactory = services.GetRequiredService<IDbContextFactory<AppContext>>();
                var env = services.GetRequiredService<IWebHostEnvironment>();

                using var dbContext = dbContextFactory.CreateDbContext();

                var dump = ParseJson(env.WebRootPath + "/dump.zip");

                DataImporter.ImportJsonData(dump, dbContext);
                await ScoresRefresh.Refresh(dbContext);
                await PlayersRefresh.Refresh(dbContext);
                await LeaderboardsRefresh.Refresh(dbContext);
            }
        }

        private static string ReconstructKey(string shuffledKey, int[] indices)
        {
            char[] originalKey = new char[indices.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                originalKey[indices[i]] = shuffledKey[i];
            }
            return new string(originalKey);
        }

        public static AmazonS3Client GetS3Client()
        {
            string shuffledAccessKey = "7LUZH3R3MAAIU3KED3FD";
            int[] accessKeyIndices = { 18, 6, 12, 8, 11, 9, 16, 10, 19, 0, 3, 2, 14, 17, 1, 13, 15, 4, 5, 7 };

            string shuffledSecretKey = "ms9+qvcBzcLHCVhClxPALWOALxLBcd6EahJFxfQ9";
            int[] secretKeyIndices = { 31, 13, 29, 0, 19, 14, 3, 2, 38, 5, 7, 24, 12, 23, 8, 17, 25, 21, 4, 39, 20, 28, 33, 1, 6, 26, 15, 36, 18, 16, 34, 22, 30, 37, 9, 35, 11, 32, 27, 10 };

            string accessKey = ReconstructKey(shuffledAccessKey, accessKeyIndices);
            string secretKey = ReconstructKey(shuffledSecretKey, secretKeyIndices);

            return new AmazonS3Client(accessKey, secretKey, RegionEndpoint.USEast1);
        }

        public static async Task<string> UploadDatabaseAsync(string filePath)
        {
            var client = GetS3Client();

            var fileTransferUtility = new TransferUtility(client);

            // Create a unique name for the database file
            var dbName = $"db-{DateTime.UtcNow:yyyyMMddHHmmss}-{new Random().Next(1000, 9999)}.db";
            var key = $"{dbName}";

            await fileTransferUtility.UploadAsync(filePath, "portabledbs", key);

            // Save the database name to a file in wwwroot
            File.WriteAllText(Path.Combine("wwwroot", "current_db_name.txt"), dbName);

            return dbName;
        }

        public static async Task<bool> DownloadDatabaseFileAsync(string fileName, string localPath)
        {
            try
            {
                var request = new GetObjectRequest
                {
                    BucketName = "portabledbs",
                    Key = fileName
                };

                using (var response = await GetS3Client().GetObjectAsync(request))
                using (var responseStream = response.ResponseStream)
                using (var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write))
                {
                    await responseStream.CopyToAsync(fileStream);
                }
                return true;
            }
            catch (AmazonS3Exception e)
            {
                if (e.ErrorCode == "NoSuchKey")
                {
                    Console.WriteLine("File not found in S3.");
                    return false;
                }
                throw; // Re-throw the exception if it's not handled here.
            }
        }

        public static async Task DownloadDatabaseIfNeeded(string webRootPath)
        {
            var dbNameFile = Path.Combine(webRootPath, "current_db_name.txt");

            if (File.Exists(dbNameFile))
            {
                var dbName = File.ReadAllText(dbNameFile);
                var localDbPath = Path.Combine(webRootPath, "Database.db");

                if (!File.Exists(localDbPath))
                {
                    bool downloaded = await DownloadDatabaseFileAsync(dbName, localDbPath);
                    if (downloaded)
                    {
                        Console.WriteLine("Database downloaded successfully.");
                    }
                    else
                    {
                        Console.WriteLine("Failed to download database.");
                    }
                }
            }
            else
            {
                Console.WriteLine("Database name file not found.");
            }
        }

        public string GetCurrentDatabaseName()
        {
            var path = Path.Combine("wwwroot", "current_db_name.txt");
            return File.ReadAllText(path);
        }

        public static void SetComparisonDBTarget()
        {
            var path = Path.Combine(Environment.CurrentDirectory, "wwwroot");
            var dest = Path.Combine(path, "Comparison.db");
            path = Path.Combine(path, "Database.db");
            File.Copy(path, dest, true);
        }

        public static async Task Main(string[] args)
        {
            // Run this line in terminal once to clone submodules:
            // git submodule update --init --recursive
            // For this to run properly, make sure to target those submodules:
            // RatingAPI: portaBLe
            // Analyzer: portaBLe
            // Parser: System.Text.Json
            // Then, compile Parser, then Analyzer, then RatingAPI, then this project in Debug
            var builder = WebApplication.CreateBuilder(args);

            try {
                // The file current_db_name.txt in wwwroot should contain the S3 key of the current DB
                // Uncomment to download the DB from S3 if Database.db from wwwroot is missing, this usually take 1-2 minutes
                // await DownloadDatabaseIfNeeded(builder.Environment.WebRootPath);

                // Uncomment to upload the local Database.db to S3
                // await UploadDatabaseAsync($"{builder.Environment.WebRootPath}/Database.db");

                // Uncomment to set the current .db file as comparison target
                // SetComparisonDBTarget();

                var connectionString = $"Data Source={builder.Environment.WebRootPath}/Database.db;";
                builder.Services.AddDbContextFactory<AppContext>(options => options.UseSqlite(connectionString));
                
                var comparisonConnectionString = $"Data Source={builder.Environment.WebRootPath}/Comparison.db;";
                builder.Services.AddDbContextFactory<ComparisonContext>(options => options.UseSqlite(comparisonConnectionString));
                
                builder.Services.AddRazorPages();

                var app = builder.Build();

                InitializeDatabase(app);

                // Configure the HTTP request pipeline.
                if (!app.Environment.IsDevelopment())
                {
                    app.UseExceptionHandler("/Error");
                    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                    app.UseHsts();
                }

                app.UseHttpsRedirection();
                app.UseStaticFiles();

                app.UseRouting();

                app.UseAuthorization();

                app.MapRazorPages();

                // Import the JSON dump.zip from wwwroot to Database.db. Takes 5-20 minutes and 8-15GB of RAM
                //await ImportDump(app);

                using (var scope = app.Services.CreateScope())
                {
                    var services = scope.ServiceProvider;
                    var dbContextFactory = services.GetRequiredService<IDbContextFactory<AppContext>>();
                    var env = services.GetRequiredService<IWebHostEnvironment>();
                    using var dbContext = dbContextFactory.CreateDbContext();

                    // Uncomment to overwrite ratings with RatingAPI
                    // await RatingsRefresh.Refresh(dbContext); // 90 minutes average for all ranked maps

                    // Uncomment to run the reweighter 
                    // Nerf
                    // await ScoresRefresh.Autoreweight(dbContext); // 30 seconds
                    // Buff
                    // await ScoresRefresh.Autoreweight3(dbContext); // 30 seconds

                    // Uncomment to refresh everything with current ratings
                    // await ScoresRefresh.Refresh(dbContext);// 60 seconds
                    // await PlayersRefresh.Refresh(dbContext); // 40 seconds
                    // await LeaderboardsRefresh.RefreshStars(dbContext); // 1 second

                    // Uncomment to update the Megametric
                    // await LeaderboardsRefresh.Refresh(dbContext); // 20 seconds
                }

                await app.RunAsync();
            } catch (Exception e) {
                Console.WriteLine(e.Message + "   " + e.StackTrace);
            }
        }
    }
}
