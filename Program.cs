using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.Refresh;
using ProtoBuf;
using RatingAPI.Controllers;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

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
        public DbSet<DB.Stats> Stats { get; set; }
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
        public static BigExportResponse ParseProtobuf(string path)
        {
            using FileStream openStream = File.OpenRead(path);
            using ZipArchive archive = new ZipArchive(openStream, ZipArchiveMode.Read);
            ZipArchiveEntry entry = archive.Entries[0];
            using Stream entryStream = entry.Open();
            return Serializer.Deserialize<BigExportResponse>(entryStream);
        }

        public static void InitializeDatabase(IHost host)
        {
            using (var scope = host.Services.CreateScope())
            {
                var services = scope.ServiceProvider;
                var dbContext = services.GetRequiredService<AppContext>();
                
                var pendingMigrations = dbContext.Database.GetPendingMigrations();
                if (pendingMigrations.Any())
                {
                    try {
                        dbContext.Database.Migrate();
                    } catch (Exception ex) {
                        Console.WriteLine($"Migration failed (this may be expected): {ex.Message}");
                    }
                }
                else
                {
                    dbContext.Database.EnsureCreated();
                }
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

                var dump = ParseProtobuf(env.WebRootPath + "/dump.zip");

                DataImporter.ImportData(dump, dbContext);
                await ScoresRefresh.Refresh(dbContext);
                await PlayersRefresh.Refresh(dbContext);
                await LeaderboardsRefresh.Refresh(dbContext);
            }
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
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
            Console.WriteLine("Uploading DB: " + dbName);
            // Save the database name to a file in wwwroot
            File.WriteAllText(Path.Combine("wwwroot", "current_db_name.txt"), dbName);
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
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
                    Console.WriteLine("Downloading DB");
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
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }

        public string GetCurrentDatabaseName()
        {
            var path = Path.Combine("wwwroot", "current_db_name.txt");
            return File.ReadAllText(path);
        }

        public static void SetComparisonDBTarget()
        {
            Console.WriteLine("Set DB as comparison target");
            var path = Path.Combine(Environment.CurrentDirectory, "wwwroot");
            var dest = Path.Combine(path, "Comparison.db");
            path = Path.Combine(path, "Database.db");
            File.Copy(path, dest, true);
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }

        public static Stopwatch Stopwatch = new();
        public static int CoreCount = Environment.ProcessorCount;

        public static async Task Main(string[] args)
        {
            Stopwatch = Stopwatch.StartNew();
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

                var cli = PipelineOptions.Parse(args);
                var connectionString = $"Data Source={cli.Db ?? builder.Environment.WebRootPath + "/Database.db"};";
                builder.Services.AddDbContextFactory<AppContext>(options => options.UseSqlite(connectionString));
                
                var comparisonConnectionString = $"Data Source={cli.Comparison ?? builder.Environment.WebRootPath + "/Comparison.db"};";
                builder.Services.AddDbContextFactory<ComparisonContext>(options => options.UseSqlite(comparisonConnectionString));
                
                // databases the pages can switch between (read-only; this instance's Database.db / Comparison.db unless wwwroot/databases.json lists others)
                builder.Services.AddSingleton<portaBLe.Services.IDynamicDbContextService>(sp =>
                    new portaBLe.Services.DynamicDbContextService(sp.GetRequiredService<IWebHostEnvironment>(), cli.Db, cli.Comparison));
                builder.Services.AddRazorPages();

                var app = builder.Build();

                InitializeDatabase(app);

                // Scripted experiments, e.g. (see Analysis/scripts/build_test_dbs.sh):
                //   dotnet run -- --db wwwroot/test-algo.db --steps import,rerate,scores,stats --acc-source Algorithm --curve PowerLaw --exit
                if (cli.Steps.Count > 0)
                {
                    await RunPipeline(app, cli);
                    if (cli.Exit) return;
                }

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
                // await ImportDump(app);

                using (var scope = app.Services.CreateScope())
                {
                    var services = scope.ServiceProvider;
                    var dbContextFactory = services.GetRequiredService<IDbContextFactory<AppContext>>();
                    var env = services.GetRequiredService<IWebHostEnvironment>();
                    using var dbContext = dbContextFactory.CreateDbContext();
                    
                    // Uncomment to overwrite ratings with RatingAPI
                    // await RatingsRefresh.Overwrite(dbContext);
                    // Uncomment to recalculate ratings after changing ReplayUtils.
                    // await RatingsRefresh.Refresh(dbContext);
                    /*
                    // Uncomment to run the reweighter 
                    // Nerf
                    // await ScoresRefresh.Autoreweight(dbContext);
                    // Buff
                    // await ScoresRefresh.Autoreweight3(dbContext);

                    // Uncomment to refresh everything with current ratings
                    
                    await ScoresRefresh.Refresh(dbContext);
                    await PlayersRefresh.Refresh(dbContext);
                    await LeaderboardsRefresh.RefreshStars(dbContext);
                    */

                    // Uncomment to update the Megametric and Stats
                    // await UpdateStats(dbContext);
                }

                await app.RunAsync();
            } catch (Exception e) {
                Console.WriteLine(e.Message + "   " + e.StackTrace);
            }
        }

        /// <summary>Command-line options for scripted DB builds (unknown arguments are left to ASP.NET).</summary>
        public class PipelineOptions
        {
            public string? Db, Comparison, Dump, AccModel, PassModel;
            public List<string> Steps = new();
            public AccSource AccSource = AccSource.ML;
            public CurveMode Curve = CurveMode.Classic;
            public float ScoreCorrectionTau = 0.09f;
            public bool Exit;

            public static PipelineOptions Parse(string[] args)
            {
                var o = new PipelineOptions();
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--db": o.Db = args[++i]; break;
                        case "--comparison": o.Comparison = args[++i]; break;
                        case "--dump": o.Dump = args[++i]; break;
                        case "--steps": o.Steps = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(); break;
                        case "--acc-source": o.AccSource = Enum.Parse<AccSource>(args[++i], true); break;
                        case "--curve": PpCurve.Mode = o.Curve = Enum.Parse<CurveMode>(args[++i], true); break; // also for the web pages (PP curve view)
                        case "--acc-model": o.AccModel = args[++i]; break;
                        case "--pass-model": o.PassModel = args[++i]; break;   // Classic / Energy (analyzer PassEnergy)
                        case "--gamma": PpCurve.Gamma = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                        case "--acc-scale": PpCurve.AccScale = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                        case "--epsilon": PpCurve.Epsilon = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                        case "--relative-epsilon": // per-map curve offset k * (1 - predictedAcc) (see PpCurve)
                            PpCurve.RelativeEpsilon = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                            break;
                        case "--score-correction": o.ScoreCorrectionTau = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                        case "--pass-fade": // strength,floor,center (see PassFade)
                            var fade = args[++i].Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                            (PassFade.Enabled, PassFade.Strength, PassFade.Floor, PassFade.Center) = (true, fade[0], fade[1], fade[2]);
                            break;
                        case "--pass-blend": // p-norm combining pass PP with acc + tech PP (see PassBlend)
                            PassBlend.P = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                            break;
                        case "--acc-cap": // max error-rate advantage over the map's prediction (see AccCap)
                            (AccCap.Enabled, AccCap.MaxAdvantage) = (true, float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture));
                            break;
                        case "--exit": o.Exit = true; break;
                    }
                }
                return o;
            }
        }

        /// <summary>
        /// Steps: import (dump -> empty DB), rerate (RatingAPI, --acc-source), correct (score-informed predicted accuracy,
        /// --score-correction tau; run right after rerate with the same curve options), stars (stars from stored ratings),
        /// scores (score PP + player totals), stats (Megametric, outliers, Stats table). The curve applies to every step.
        /// </summary>
        public static async Task RunPipeline(IHost host, PipelineOptions cli)
        {
            PpCurve.Mode = cli.Curve;
            Console.WriteLine($"Pipeline: {string.Join(",", cli.Steps)} | acc source {cli.AccSource} {cli.AccModel ?? "(embedded model)"} | curve {cli.Curve}"
                + (cli.Curve == CurveMode.PowerLaw ? $" (gamma {PpCurve.Gamma}, acc scale {PpCurve.AccScale}, epsilon {PpCurve.Epsilon}"
                    + (PpCurve.PerMap ? $" + {PpCurve.RelativeEpsilon} x (1 - predicted acc)" : "") + ")" : "")
                + (PassFade.Enabled ? $" | pass fade strength {PassFade.Strength}, floor {PassFade.Floor}, center {PassFade.Center}" : "")
                + (PassBlend.Enabled ? $" | pass blend p {PassBlend.P}" : "")
                + (AccCap.Enabled ? $" | acc cap {AccCap.MaxAdvantage}" : ""));
            using var scope = host.Services.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppContext>>();
            var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
            foreach (var step in cli.Steps)
            {
                Console.WriteLine($"== {step} ({Stopwatch.ElapsedMilliseconds / 1000}s)");
                using var dbContext = factory.CreateDbContext();
                switch (step)
                {
                    case "import":
                        if (await dbContext.Leaderboards.AnyAsync()) throw new InvalidOperationException("import needs an empty database (use a new --db path)");
                        DataImporter.ImportData(ParseProtobuf(cli.Dump ?? env.WebRootPath + "/dump.zip"), dbContext);
                        break;
                    case "rerate": await RatingsRefresh.Overwrite(dbContext, cli.AccSource, cli.AccModel, cli.PassModel); break;
                    case "correct": await ScoreCorrection.Apply(dbContext, cli.ScoreCorrectionTau); break;
                    case "stars": await LeaderboardsRefresh.RefreshStars(dbContext); break;
                    case "scores":
                        await ScoresRefresh.Refresh(dbContext);
                        await PlayersRefresh.Refresh(dbContext);
                        break;
                    case "stats": await UpdateStats(dbContext); break;
                    default: throw new ArgumentException($"unknown step '{step}'");
                }
            }
            Console.WriteLine($"Pipeline done ({Stopwatch.ElapsedMilliseconds / 1000}s)");
        }

        public static async Task UpdateStats(AppContext dbContext)
        {
            await LeaderboardsRefresh.Refresh(dbContext);
            await LeaderboardsRefresh.Outliers(dbContext);
            await StatsRefresh.Refresh(dbContext);
        }
    }
}
