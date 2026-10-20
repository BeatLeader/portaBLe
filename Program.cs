using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using portaBLe.DB;
using portaBLe.Refresh;
using portaBLe.Services;
using ProtoBuf;
using System.Diagnostics;
using System.IO.Compression;

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

        protected override void OnModelCreating(ModelBuilder modelBuilder) => CurveModelConfig.Apply(modelBuilder);
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
        public DbSet<DB.Stats> Stats { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder) => CurveModelConfig.Apply(modelBuilder);
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
                    try
                    {
                        dbContext.Database.Migrate();
                    }
                    catch (Exception ex)
                    {
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

            // The Top Scores page cache goes next to it (the server downloads it with the database instead of building it)
            await UploadTopScoresCacheAsync(filePath, key);

            // Save the database name to a file in wwwroot
            File.WriteAllText(Path.Combine("wwwroot", "current_db_name.txt"), dbName);

            return dbName;
        }
        // Uploads wwwroot/cache/<db>.topscores.bin as "<dbKey>.topscores.bin", next to the database in S3. Built by
        // RefreshEverything (or the Top Scores page). It's matched to a database by content, so a cache built from
        // Database.db works for the same database uploaded under another name.
        public static async Task UploadTopScoresCacheAsync(string dbPath, string dbKey)
        {
            var cachePath = TopScoresCache.CachePathOf(dbPath);
            if (!File.Exists(cachePath))
            {
                Console.WriteLine($"No top scores cache for {Path.GetFileName(dbPath)} (open the Top Scores page or run RefreshEverything to build it)");
                return;
            }
            await new TransferUtility(GetS3Client()).UploadAsync(cachePath, "portabledbs", dbKey + ".topscores.bin");
            Console.WriteLine($"Uploaded {Path.GetFileName(cachePath)} as {dbKey}.topscores.bin");
        }

        // The top scores cache of every database in databases.json that has one locally
        public static async Task UploadAllTopScoresCachesAsync(IDynamicDbContextService dbService, string webRootPath)
        {
            foreach (var db in await dbService.GetAvailableDatabasesAsync())
            {
                await UploadTopScoresCacheAsync(Path.Combine(webRootPath, db.FileName), db.FileName);
            }
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
            Console.WriteLine("Set DB as comparison target");
            var path = Path.Combine(Environment.CurrentDirectory, "wwwroot");
            var dest = Path.Combine(path, "Comparison.db");
            path = Path.Combine(path, "Database.db");
            File.Copy(path, dest, true);
            Console.WriteLine((Program.Stopwatch.ElapsedMilliseconds / 1000).ToString() + " seconds");
        }

        public static Stopwatch Stopwatch = new();
        public static int CoreCount = Environment.ProcessorCount;

        // The prod database ModelComparisonExport compares with (wwwroot, listed in databases.json)
        private const string ProdDatabase = "db-20261012191847-9271.db";

        // beatsaber-replays-ai-2 next to this repository (parity replays, Acc AI training)
        private static string ReplaysRepo(IWebHostEnvironment env) =>
            Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "beatsaber-replays-ai-2"));

        public static async Task Main(string[] args)
        {
            Stopwatch = Stopwatch.StartNew();
            // Run this line in terminal once to clone submodules:
            // git submodule update --init --recursive
            // For this to run properly, make sure to target those submodules:
            // RatingAPI: portaBLe
            // Analyzer: portaBLe
            // Parser: System.Text.Json
            // Then, compile Parser, then Analyzer, then RatingAPI, then this project in Debug (or Release).
            var builder = WebApplication.CreateBuilder(args);

            try
            {
                // The file current_db_name.txt in wwwroot should contain the S3 key of the current DB
                // Uncomment to download the DB from S3 if Database.db from wwwroot is missing, this usually take 1-2 minutes
                // await DownloadDatabaseIfNeeded(builder.Environment.WebRootPath);

                // Uncomment to upload the local Database.db to S3
                // await UploadDatabaseAsync($"{builder.Environment.WebRootPath}/Database.db");

                // Uncomment to upload all local databases to S3 and update databases.json
                // await UploadAllDatabasesAsync(builder.Environment.WebRootPath);

                // Uncomment to upload the Top Scores page cache of every database in databases.json (UploadDatabaseAsync
                // already uploads it with the database)
                // await UploadAllTopScoresCachesAsync(new DynamicDbContextService(builder.Environment), builder.Environment.WebRootPath);

                // Uncomment to set the current .db file as comparison target
                // SetComparisonDBTarget();

                // Register the dynamic DB context service
                builder.Services.AddSingleton<IDynamicDbContextService, DynamicDbContextService>();

                // Uncomment to download all databases from S3 based on databases.json
                var tempDbService = new DynamicDbContextService(builder.Environment);
                await tempDbService.DownloadAllDatabasesAsync(builder.Environment.WebRootPath);

                var connectionString = $"Data Source={builder.Environment.WebRootPath}/Database.db;";
                builder.Services.AddDbContextFactory<AppContext>(options => options.UseSqlite(connectionString));

                var comparisonConnectionString = $"Data Source={builder.Environment.WebRootPath}/Comparison.db;";
                builder.Services.AddDbContextFactory<ComparisonContext>(options => options.UseSqlite(comparisonConnectionString));

                builder.Services.AddRazorPages();

                var app = builder.Build();

                InitializeDatabase(app);

                // Verify all tables/columns from DBModels exist in every database, and add missing ones (after migrations)
                await EnsureSchemaForAllDatabases(tempDbService, builder.Environment.WebRootPath);

                // Load the main database's Top Scores cache in the background (or build it when it's missing or outdated),
                // so the page is ready when someone opens it
                var startupDbService = app.Services.GetRequiredService<IDynamicDbContextService>();
                _ = Task.Run(() => TopScoresCache.TryGetAsync(() => startupDbService.CreateContext(startupDbService.GetMainDatabaseFileName())));

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

                    // ---- Rating pipeline -------------------------------------------------------------------------------
                    // Usual order after changing the Analyzer, the Acc AI model or a constant:
                    //   RatingsRefresh.Overwrite -> RefreshEverything -> AccReweighter.Run -> RefreshEverything -> BalanceTuner.Run
                    //   -> ModelComparisonExport.Run
                    // (only pp formula changes, Refresh/PpFormula.cs: skip Overwrite)
                    // Run FullPipeline to do all of the above in one pass (about 10-15 minutes and 8-15GB of RAM). autoreweight:
                    // the acc reweighter and its re-score; balancer: the balance tuner, the model comparison and the pass window
                    // check.
                    // await FullPipeline(dbContext, env, false, false, false);

                    // Rate every leaderboard again with RatingAPI (Analyzer + Acc AI). Keeps the acc reweighter's multipliers;
                    // Overwrite(dbContext, false) for raw acc ratings (multipliers cleared)
                    // await RatingsRefresh.Overwrite(dbContext);

                    // Recalculate acc ratings and stars from the stored predicted acc (after changing AccRating / ReplayUtils).
                    // Refresh(dbContext, false) for raw acc ratings (acc reweighter multipliers cleared)
                    // await RatingsRefresh.Refresh(dbContext);

                    // Per-leaderboard acc curves from the stored predicted acc (RatingAPI Curve.GetCurve, CurveSettings), without
                    // rating the maps again. Overwrite stores them too. Then RefreshEverything to re-score with them.
                    // await CurvesRefresh.Regenerate(dbContext);

                    // Base NJS of every leaderboard from Info.dat in the maps folder, without rating the maps again (Overwrite
                    // stores it too). For every database in databases.json: RefreshNjsForAllDatabases below.
                    // await RatingsRefresh.RefreshNjs(dbContext);

                    // Scores, players, leaderboards (Megametric), stars, outliers and stats from the current ratings
                    // await RefreshEverything(dbContext);

                    // ---- Balance tools (Refresh/Tools, read only) ---------------------------------------------------------
                    // Suggested pass / tech / acc constants so no kind of map is worth more than others of its stars
                    // (in memory only). Writes wwwroot/balance-tuning.json for the Balance Tuning page.
                    // BalanceTuner.RatedWith must hold the constants the database was rated with. About 30 full re-scorings.
                    // await BalanceTuner.Run(dbContext, env.WebRootPath);

                    // The parts of the pp formula (Refresh/PpFormula.cs) one at a time, in memory only. Writes
                    // wwwroot/pp-formula.json, shown on the Balance Tuning page. About 25 full re-scorings.
                    // await BalanceTuner.RunPpFormula(dbContext, env.WebRootPath);

                    // Side by side of prod and this database: every map's ratings / stars / Megametric / best pp, and the
                    // top 2000 players. Writes wwwroot/model-comparison.json. Change the file name if prod is another database.
                    // await ModelComparisonExport.Run(env.WebRootPath, ProdDatabase);

                    // ---- Research exports (Refresh/Tools, read only, big files in wwwroot) ----------------------------------
                    // Analyzer parity check against replays: write the list of leaderboards, run
                    // "BLReplaysPrepare parity <that file>" in beatsaber-replays-ai-2, then compare.
                    // await ParityCheck.WriteSample(dbContext, env.WebRootPath);
                    // await ParityCheck.Run(dbContext, env.WebRootPath, Path.Combine(ReplaysRepo(env), "parity2"));

                    // Pass and tech of every leaderboard with the Analyzer as it is now, next to the pass in this database and in
                    // prod, from the maps folder, Analyzer only (no Acc AI): the new ordering before re-rating. Writes
                    // wwwroot/pass-window-check.json for the Pass Rating Comparison page.
                    // await PassWindowCheck.Run(dbContext, env.WebRootPath, ProdDatabase);

                    // Every swing of every ranked map with its scores' accuracy (rating-fit.json / .bin), to fit the pass and
                    // tech formulas.
                    // await RatingFitExport.Run(dbContext, env.WebRootPath);
                    // Every ranked score (rating-fit-scores.json / .bin). Also the input of beatsaber-replays-ai-2's
                    // tools/player_skill.py (player-adjusted Acc AI targets).
                    // await RatingFitExport.RunScores(dbContext, env.WebRootPath);
                    // Ratings with SS / FS / SF of every leaderboard (rating-fit-modifiers.json), to compare speed-modifier scores with
                    // nomod scores of similar ratings.
                    // await RatingFitExport.RunModifiers(dbContext, env.WebRootPath);
                    // Every wall and the Analyzer's dodge / crouch classification (wall-fit.json).
                    // await RatingFitExport.RunWalls(dbContext, env.WebRootPath);

                    // ---- Score-based reweighting -------------------------------------------------------------------------
                    // Acc reweighter (the only tool that changes ratings from scores): moves each map's acc rating so it's worth
                    // about as much as the other maps of its stars, under the balance tuner's metrics. Stored in
                    // Leaderboards.AccReweight (kept by Overwrite / Refresh). Writes wwwroot/acc-reweight.json. Then re-score.
                    // FullPipeline runs it when autoreweight is true.
                    // await AccReweighter.Run(dbContext, env.WebRootPath);
                    // await RefreshEverything(dbContext);

                    // ---- All databases ------------------------------------------------------------------------------------
                    // Leaderboards (Megametric) and stats of every database in databases.json
                    // await RefreshLeaderboardsForAllDatabases(tempDbService, builder.Environment.WebRootPath);
                    // await RefreshStatsForAllDatabases(tempDbService, builder.Environment.WebRootPath);
                    // NJS of every leaderboard (from the maps folder) in every database in databases.json
                    // await RefreshNjsForAllDatabases(tempDbService, builder.Environment.WebRootPath);
                }

                await app.RunAsync();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message + "   " + e.StackTrace);
            }
        }

        // autoreweight: run the acc reweighter (and re-score after it). keepAccReweight: when it doesn't run, keep the
        // multipliers of the last run on the acc ratings; false = raw acc ratings, multipliers cleared. (When it runs, it
        // starts again from the raw acc ratings either way.)
        public static async Task FullPipeline(AppContext dbContext, IWebHostEnvironment env, bool autoreweight, bool balancer, bool keepAccReweight = true)
        {
            await RatingsRefresh.Overwrite(dbContext, keepAccReweight);
            await RefreshEverything(dbContext);
            if (autoreweight)
            {
                await AccReweighter.Run(dbContext, env.WebRootPath);
                await RefreshEverything(dbContext);
            }
            if (balancer)
            {
                await BalanceTuner.Run(dbContext, env.WebRootPath);
                await ModelComparisonExport.Run(env.WebRootPath, ProdDatabase);
                await PassWindowCheck.Run(dbContext, env.WebRootPath, ProdDatabase);
            }
        }
        public static async Task RefreshEverything(AppContext dbContext)
        {
            // Same as ScoresRefresh.Refresh + PlayersRefresh.Refresh, in one pass over the scores (about 3x faster)
            await ScoresPlayersRefresh.Refresh(dbContext);
            // await ScoresRefresh.Refresh(dbContext);
            // await PlayersRefresh.Refresh(dbContext);
            await LeaderboardsRefresh.Refresh(dbContext);
            await LeaderboardsRefresh.RefreshStars(dbContext);

            // Update the Megametric and Stats
            await UpdateStats(dbContext);

            // The Top Scores page cache (wwwroot/cache/<db>.topscores.bin), so the page opens without building it
            await TopScoresCache.BuildAndSaveAsync(dbContext);
        }

        public static async Task UpdateStats(AppContext dbContext)
        {
            // await LeaderboardsRefresh.Refresh(dbContext);
            await LeaderboardsRefresh.Outliers(dbContext);
            await StatsRefresh.Refresh(dbContext);
        }

        // Ensures every table and column from DBModels exists in all databases, adding missing ones with default values
        private static async Task EnsureSchemaForAllDatabases(IDynamicDbContextService dbService, string webRootPath)
        {
            var databases = await dbService.GetAvailableDatabasesAsync();

            if (!databases.Any(d => string.Equals(d.FileName, "Database.db", StringComparison.OrdinalIgnoreCase)))
            {
                databases.Add(new DatabaseConfig { Name = "Database.db", FileName = "Database.db" });
            }

            foreach (var db in databases)
            {
                var path = Path.Combine(webRootPath, db.FileName);
                if (!File.Exists(path))
                {
                    Console.WriteLine($"Skipping schema check for {db.Name}: file not found");
                    continue;
                }

                try
                {
                    var optionsBuilder = new DbContextOptionsBuilder<AppContext>();
                    optionsBuilder.UseSqlite($"Data Source={path};");

                    using var dbContext = new AppContext(optionsBuilder.Options);
                    var connection = dbContext.Database.GetDbConnection();
                    await connection.OpenAsync();

                    foreach (var entityType in dbContext.Model.GetEntityTypes())
                    {
                        var tableName = entityType.GetTableName();
                        if (tableName == null) continue;

                        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        using (var command = connection.CreateCommand())
                        {
                            command.CommandText = $"PRAGMA table_info(\"{tableName}\")";
                            using var reader = await command.ExecuteReaderAsync();
                            while (await reader.ReadAsync())
                            {
                                existingColumns.Add(reader.GetString(1));
                            }
                        }

                        if (existingColumns.Count == 0)
                        {
                            var script = dbContext.Database.GenerateCreateScript();
                            var createSql = script
                                .Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .FirstOrDefault(s => s.StartsWith($"CREATE TABLE \"{tableName}\""));
                            if (createSql != null)
                            {
                                await dbContext.Database.ExecuteSqlRawAsync(createSql);
                                Console.WriteLine($"[{db.Name}] Created missing table {tableName}");
                            }
                            continue;
                        }

                        var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
                        foreach (var property in entityType.GetProperties())
                        {
                            var columnName = property.GetColumnName(storeObject);
                            if (columnName == null || existingColumns.Contains(columnName)) continue;

                            var columnType = property.GetColumnType(storeObject);
                            var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                            // Columns stored through a converter (the curves: List<Vector2> as text) default to their stored type
                            var storedType = property.GetValueConverter()?.ProviderClrType ?? clrType;
                            var defaultValue = storedType == typeof(string) ? "''" : "0";
                            var sql = property.IsNullable
                                ? $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnType} NULL"
                                : $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnType} NOT NULL DEFAULT {defaultValue}";

                            await dbContext.Database.ExecuteSqlRawAsync(sql);
                            Console.WriteLine($"[{db.Name}] Added missing column {tableName}.{columnName}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error verifying schema for {db.Name}: {ex.Message}");
                }
            }
        }

        // Helper method to refresh stats for all databases
        private static async Task RefreshStatsForAllDatabases(IDynamicDbContextService dbService, string webRootPath)
        {
            var databases = await dbService.GetAvailableDatabasesAsync();

            Console.WriteLine($"Refreshing statistics for {databases.Count} databases...");

            foreach (var db in databases)
            {
                Console.WriteLine($"\n========================================");
                Console.WriteLine($"Processing: {db.Name} ({db.FileName})");
                Console.WriteLine($"========================================");

                try
                {
                    var connectionString = $"Data Source={Path.Combine(webRootPath, db.FileName)};";
                    var optionsBuilder = new DbContextOptionsBuilder<AppContext>();
                    optionsBuilder.UseSqlite(connectionString);

                    using var dbContext = new AppContext(optionsBuilder.Options);
                    await StatsRefresh.Refresh(dbContext);

                    Console.WriteLine($"✓ Successfully refreshed stats for {db.Name}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"✗ Error refreshing stats for {db.Name}: {ex.Message}");
                }
            }

            Console.WriteLine($"\n========================================");
            Console.WriteLine($"Completed refreshing stats for all databases");
            Console.WriteLine($"========================================");
        }

        // Fills Leaderboard.Njs (RatingsRefresh.RefreshNjs) in every database in databases.json
        private static async Task RefreshNjsForAllDatabases(IDynamicDbContextService dbService, string webRootPath)
        {
            var databases = await dbService.GetAvailableDatabasesAsync();
            foreach (var db in databases)
            {
                var path = Path.Combine(webRootPath, db.FileName);
                if (!File.Exists(path))
                {
                    Console.WriteLine($"Skipping NJS for {db.Name}: file not found");
                    continue;
                }
                try
                {
                    var optionsBuilder = new DbContextOptionsBuilder<AppContext>();
                    optionsBuilder.UseSqlite($"Data Source={path};");
                    using var dbContext = new AppContext(optionsBuilder.Options);
                    Console.WriteLine($"NJS: {db.Name} ({db.FileName})");
                    await RatingsRefresh.RefreshNjs(dbContext);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error filling NJS for {db.Name}: {ex.Message}");
                }
            }
        }

        // Helper method to refresh leaderboards (Megametrics) for all databases
        private static async Task RefreshLeaderboardsForAllDatabases(IDynamicDbContextService dbService, string webRootPath)
        {
            var databases = await dbService.GetAvailableDatabasesAsync();

            Console.WriteLine($"Refreshing leaderboards (Megametrics) for {databases.Count} databases...");

            foreach (var db in databases)
            {
                Console.WriteLine($"\n========================================");
                Console.WriteLine($"Processing: {db.Name} ({db.FileName})");
                Console.WriteLine($"========================================");

                try
                {
                    var connectionString = $"Data Source={Path.Combine(webRootPath, db.FileName)};";
                    var optionsBuilder = new DbContextOptionsBuilder<AppContext>();
                    optionsBuilder.UseSqlite(connectionString);

                    using var dbContext = new AppContext(optionsBuilder.Options);
                    await LeaderboardsRefresh.Refresh(dbContext);

                    Console.WriteLine($"✓ Successfully refreshed leaderboards for {db.Name}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"✗ Error refreshing leaderboards for {db.Name}: {ex.Message}");
                }
            }

            Console.WriteLine($"\n========================================");
            Console.WriteLine($"Completed refreshing leaderboards for all databases");
            Console.WriteLine($"========================================");
        }
    }
}
