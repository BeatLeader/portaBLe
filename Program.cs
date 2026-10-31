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

            var dbName = $"db-{DateTime.UtcNow:yyyyMMddHHmmss}-{new Random().Next(1000, 9999)}.db";
            var key = $"{dbName}";

            await fileTransferUtility.UploadAsync(filePath, "portabledbs", key);

            // The Top Scores page cache goes next to it (the server downloads it with the database instead of building it)
            await UploadTopScoresCacheAsync(filePath, key);

            File.WriteAllText(Path.Combine("wwwroot", "current_db_name.txt"), dbName);

            return dbName;
        }
        // Uploads the database's Top Scores cache as "<dbKey>.topscores.bin" next to it in S3, building it first if it
        // doesn't match the database. The cache is matched by content, so one built from Database.db stays valid under
        // another file name.
        public static async Task UploadTopScoresCacheAsync(string dbPath, string dbKey)
        {
            var cachePath = TopScoresCache.CachePathOf(dbPath);
            // The server rejects a cache built from another version of the database
            var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={Path.GetFullPath(dbPath)};").Options;
            using (var context = new AppContext(options)) await TopScoresCache.EnsureCurrentAsync(context);
            await new TransferUtility(GetS3Client()).UploadAsync(cachePath, "portabledbs", dbKey + ".topscores.bin");
            Console.WriteLine($"Uploaded {Path.GetFileName(cachePath)} as {dbKey}.topscores.bin");
        }

        // Uploads the local Top Scores cache of every database in databases.json
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
            // dotnet run -c Release -- upload <wwwroot/file.db>: uploads that database and its Top Scores cache; the S3 name
            // is written to wwwroot/current_db_name.txt
            if (args.Length > 1 && args[0] == "upload")
            {
                Console.WriteLine($"Uploaded {args[1]} as {await UploadDatabaseAsync(args[1])}");
                return;
            }
            // dotnet run -c Release -- cache <wwwroot/file.db> [upload]: builds that database's Top Scores cache if it's stale;
            // upload: then uploads it to S3 under the database's file name
            if (args.Length > 1 && args[0] == "cache")
            {
                if (args.Contains("upload")) await UploadTopScoresCacheAsync(args[1], Path.GetFileName(args[1]));
                else
                {
                    var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={Path.GetFullPath(args[1])};").Options;
                    using var context = new AppContext(options);
                    Console.WriteLine(await TopScoresCache.EnsureCurrentAsync(context) ? "Built" : "Already current");
                }
                return;
            }
            // dotnet run -c Release -- rescore: RefreshEverything alone on wwwroot/Database.db (after a pp formula change)
            if (args.Length > 0 && args[0] == "rescore")
            {
                await EnsureSchema("Database.db", Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "Database.db"));
                var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")}/Database.db;").Options;
                using var rescoreContext = new AppContext(options);
                await RefreshEverything(rescoreContext);
                return;
            }
            // dotnet run -c Release -- recurve <wwwroot/file.db>: CurvesRefresh.Regenerate then RefreshEverything on that database
            // (after a curve or pp formula change; keeps its ratings and acc reweighter multipliers)
            if (args.Length > 1 && args[0] == "recurve")
            {
                // Columns the queries read that older databases may lack
                await EnsureSchema(args[1], Path.GetFullPath(args[1]));
                var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={Path.GetFullPath(args[1])};").Options;
                using var recurveContext = new AppContext(options);
                await CurvesRefresh.Regenerate(recurveContext);
                await RefreshEverything(recurveContext);
                return;
            }
            // dotnet run -c Release -- rerate <wwwroot/file.db> [setting=value ...]: RatingsRefresh.Overwrite with raw acc
            // ratings (acc reweighter multipliers cleared) then RefreshEverything on that database (after an Analyzer or rating
            // constant change)
            if (args.Length > 1 && args[0] == "rerate")
            {
                // Experiments: ParityPredictor settings (name=value, see paritycheck) and pp formula ones (Pp.TechWeight=0.13)
                foreach (var setting in args.Skip(2))
                {
                    var parts = setting.Split('=');
                    double value = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    if (parts[0].StartsWith("Pp.")) (typeof(RatingAPI.Pp.PpFormula).GetProperty(parts[0][3..]) ?? throw new ArgumentException(parts[0])).SetValue(RatingAPI.Pp.PpFormula.Current, value);
                    else Analyzer.BeatmapScanner.Algorithm.ParityPredictor.Set(parts[0], value);
                }
                await EnsureSchema(args[1], Path.GetFullPath(args[1]));
                var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={Path.GetFullPath(args[1])};").Options;
                using var rerateContext = new AppContext(options);
                await RatingsRefresh.Overwrite(rerateContext, false);
                await RefreshEverything(rerateContext);
                return;
            }
            // dotnet run -c Release -- paritycheck <wwwroot/file.db> [setting=value ...]: ParityCheck.Run against
            // beatsaber-replays-ai-2/parity2 with those ParityPredictor settings (ParityPredictor.Set: flow.Extra=3,
            // ResetMapRate=0.15...). Read only; writes wwwroot/parity-check.json and parity-swings.json.gz
            if (args.Length > 1 && args[0] == "paritycheck")
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var setting in args.Skip(2))
                {
                    var parts = setting.Split('=');
                    Analyzer.BeatmapScanner.Algorithm.ParityPredictor.Set(parts[0], double.Parse(parts[1], inv));
                }
                var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={Path.GetFullPath(args[1])};Mode=ReadOnly;").Options;
                using var parityContext = new AppContext(options);
                var root = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                await ParityCheck.Run(parityContext, root, Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "beatsaber-replays-ai-2", "parity2")));
                return;
            }
            // dotnet run -c Release -- pp <wwwroot/file.db> <leaderboard id> [accuracy] [modifiers]: a score's pp on that
            // leaderboard with its stored ratings and curves (accuracy 1 and nomod by default), read only
            if (args.Length > 2 && args[0] == "pp")
            {
                var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={Path.GetFullPath(args[1])};Mode=ReadOnly;").Options;
                using var ppContext = new AppContext(options);
                var lb = await ppContext.Leaderboards.Include(l => l.ModifiersRating).FirstAsync(l => l.Id == args[2]);
                float accuracy = args.Length > 3 ? float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 1f;
                string modifiers = args.Length > 4 ? args[4] : "";
                var (pp, bonus, pass, acc, tech) = ReplayUtils.PpFromScore(accuracy, modifiers, lb.ModifiersRating, lb.AccRating, lb.PassRating, lb.TechRating, lb.Curve);
                Console.WriteLine(FormattableString.Invariant($"{lb.Name} {lb.DifficultyName} ({lb.Stars:0.00} stars) at {accuracy * 100:0.##}% {modifiers}: {pp:0.0}pp (pass {pass:0.0}, acc {acc:0.0}, tech {tech:0.0}, modifiers {bonus:+0.0;-0.0})"));
                return;
            }
            // dotnet run -c Release -- mapstats <wwwroot/file.db>: the Analyzer's counts the tags use (inlines, bomb resets, walls,
            // linear %) from the maps folder, then the tags (RatingsRefresh.RefreshMapStatistics), without rating the maps again
            if (args.Length > 1 && args[0] == "mapstats")
            {
                var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={Path.GetFullPath(args[1])};").Options;
                using var statsContext = new AppContext(options);
                await RatingsRefresh.RefreshMapStatistics(statsContext);
                return;
            }
            // dotnet run -c Release -- songreceval <wwwroot/file.db> [players=50] [set=default] [seed=7] [first plays]: the
            // song recommendations' offline evaluation (RecommendationEvaluation); first plays simulates new players with that
            // many ranked plays. Read only.
            if (args.Length > 1 && args[0] == "songreceval")
            {
                var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={Path.GetFullPath(args[1])};Mode=ReadOnly;").Options;
                using var evalContext = new AppContext(options);
                await beatleader_songrec.Tools.RecommendationEvaluation.Run(evalContext,
                    args.Length > 2 ? int.Parse(args[2]) : 50,
                    args.Length > 3 ? args[3] : "default",
                    args.Length > 4 ? int.Parse(args[4]) : 7,
                    args.Length > 5 ? int.Parse(args[5]) : null);
                return;
            }
            if (args.Length > 1 && args[0] == "songrecbench")
            {
                var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={Path.GetFullPath(args[1])};Mode=ReadOnly;").Options;
                await beatleader_songrec.Tools.RecommendationBenchmark.Run(() => new AppContext(options), args.Length > 2 ? int.Parse(args[2]) : 10);
                return;
            }
            // dotnet run -c Release -- import [nopipeline]: wwwroot/dump.zip into a new wwwroot/Database.db (the old one moved to
            // Database-<date>.db), then FullPipeline with the acc reweighter and the balancer; nopipeline: the scores, players and
            // leaderboards refreshed with the dump's ratings instead. Takes 5-20 minutes and 8-15GB of RAM
            if (args.Length > 0 && args[0] == "import")
            {
                var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                var path = Path.Combine(webRoot, "Database.db");
                if (File.Exists(path))
                {
                    var backup = Path.Combine(webRoot, $"Database-{DateTime.Now:yyyyMMddHHmmss}.db");
                    File.Move(path, backup);
                    Console.WriteLine($"Moved the old Database.db to {Path.GetFileName(backup)}");
                }
                var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={path};").Options;
                using (var schemaContext = new AppContext(options))
                {
                    schemaContext.Database.Migrate();
                }
                await EnsureSchema("Database.db", path);
                using (var dataContext = new AppContext(options))
                {
                    DataImporter.ImportData(ParseProtobuf(Path.Combine(webRoot, "dump.zip")), dataContext);
                }
                Console.WriteLine($"Imported in {Stopwatch.ElapsedMilliseconds / 1000} seconds");
                // A fresh context: the importer turns automatic change detection off and keeps its entities tracked
                using var importContext = new AppContext(options);
                if (args.Contains("nopipeline"))
                {
                    await ScoresRefresh.Refresh(importContext);
                    await PlayersRefresh.Refresh(importContext);
                    await LeaderboardsRefresh.Refresh(importContext);
                }
                else
                {
                    // The model comparison's prod database (opening it when missing would leave an empty file in its place)
                    var prodPath = Path.Combine(webRoot, ProdDatabase);
                    if (!File.Exists(prodPath) && !await DownloadDatabaseFileAsync(ProdDatabase, prodPath))
                        throw new FileNotFoundException($"Could not download {ProdDatabase} for the model comparison");
                    await FullPipeline(importContext, webRoot, true, true, true);
                }
                Console.WriteLine($"Done in {Stopwatch.ElapsedMilliseconds / 1000} seconds");
                return;
            }
            // dotnet run -c Release -- worth [Setting=value ...] <wwwroot/file.db>: LeaderboardsRefresh.RefreshWorth alone on that
            // database (the stored worth, no re-score), with any of the balance tuner's settings (e.g. PlayerAdjustedQuantile=0.9).
            // Also fills the worth by quantile columns (WorthMean, WorthMedian, WorthP75, WorthP90) of older databases.
            if (args.Length > 1 && args[0] == "worth")
            {
                var file = args.Skip(1).First(a => a.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
                foreach (var setting in args.Skip(1).Where(a => a != file))
                {
                    var parts = setting.Split('=', 2);
                    var property = typeof(BalanceTuner).GetProperty(parts[0]) ?? throw new ArgumentException($"No balance tuner setting {parts[0]}");
                    property.SetValue(null, property.PropertyType.IsEnum
                        ? Enum.Parse(property.PropertyType, parts[1], true)
                        : Convert.ChangeType(parts[1], property.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
                    Console.WriteLine($"Worth: {parts[0]} = {parts[1]}");
                }
                await EnsureSchema(file, Path.GetFullPath(file));
                var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={Path.GetFullPath(file)};").Options;
                using var worthContext = new AppContext(options);
                await LeaderboardsRefresh.RefreshWorth(worthContext);
                return;
            }
            // dotnet run -c Release -- reacc <wwwroot/file.db> [NAME=value ...]: the acc ratings and stars again from the stored
            // predicted acc (RatingsRefresh.Refresh, raw: acc reweighter multipliers cleared) then RefreshEverything, with those
            // AccRating correction constants (e.g. HIGH_PASS_BUFF=0.003) and pp formula settings (Pp.TechWeight=0.13). For
            // experiments on a copy.
            if (args.Length > 1 && args[0] == "reacc")
            {
                foreach (var setting in args.Skip(2))
                {
                    var parts = setting.Split('=', 2);
                    double value = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    if (parts[0].StartsWith("Pp."))
                    {
                        var pp = typeof(RatingAPI.Pp.PpFormula).GetProperty(parts[0][3..]) ?? throw new ArgumentException($"No PpFormula setting {parts[0]}");
                        pp.SetValue(RatingAPI.Pp.PpFormula.Current, value);
                    }
                    else
                    {
                        var property = typeof(RatingAPI.Controllers.AccRating).GetProperty(parts[0]) ?? throw new ArgumentException($"No AccRating setting {parts[0]}");
                        property.SetValue(null, value);
                    }
                    Console.WriteLine($"Acc rating: {parts[0]} = {parts[1]}");
                }
                await EnsureSchema(args[1], Path.GetFullPath(args[1]));
                var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={Path.GetFullPath(args[1])};").Options;
                using var reaccContext = new AppContext(options);
                await RatingsRefresh.Refresh(reaccContext, false);
                await RefreshEverything(reaccContext);
                return;
            }
            // dotnet run -c Release -- balancetune <wwwroot/file.db> [constants|formula] [Setting=value ...]: the balance tuner
            // (BalanceTuner.Run, wwwroot/balance-tuning.json) and / or its pp formula sweep (RunPpFormula, wwwroot/pp-formula.json)
            // on that database, both by default. Settings: the balance tuner's (e.g. PlayerAdjustedQuantile=0.5) or RatedWith.X,
            // the constants the database was rated with (e.g. RatedWith.PassCalibration=0.7115). Read only on the database.
            if (args.Length > 1 && args[0] == "balancetune")
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                bool constants = !args.Contains("formula") || args.Contains("constants"), formula = !args.Contains("constants") || args.Contains("formula");
                foreach (var setting in args.Skip(2).Where(a => a.Contains('=')))
                {
                    var parts = setting.Split('=', 2);
                    if (parts[0].StartsWith("RatedWith."))
                    {
                        var property = typeof(RatingConstants).GetProperty(parts[0]["RatedWith.".Length..]) ?? throw new ArgumentException(parts[0]);
                        property.SetValue(BalanceTuner.RatedWith, double.Parse(parts[1], inv));
                    }
                    else
                    {
                        var property = typeof(BalanceTuner).GetProperty(parts[0]) ?? throw new ArgumentException($"No balance tuner setting {parts[0]}");
                        property.SetValue(null, property.PropertyType.IsEnum ? Enum.Parse(property.PropertyType, parts[1], true)
                            : Convert.ChangeType(parts[1], property.PropertyType, inv));
                    }
                    Console.WriteLine($"Balance tuning: {parts[0]} = {parts[1]}");
                }
                var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={Path.GetFullPath(args[1])};Mode=ReadOnly;").Options;
                using var tuneContext = new AppContext(options);
                var root = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                if (constants) await BalanceTuner.Run(tuneContext, root);
                if (formula) await BalanceTuner.RunPpFormula(tuneContext, root);
                return;
            }
            // dotnet run -c Release -- slidershare <wwwroot/file.db> <out.csv>: every leaderboard's slider share (CurvesRefresh.
            // ExportSliderShares, the slider tilt's input). Read only.
            if (args.Length > 2 && args[0] == "slidershare")
            {
                var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={Path.GetFullPath(args[1])};Mode=ReadOnly;").Options;
                using var sliderContext = new AppContext(options);
                await CurvesRefresh.ExportSliderShares(sliderContext, Path.GetFullPath(args[2]));
                return;
            }
            // dotnet run -c Release -- worthnoise <wwwroot/file.db> [quantile ...]: BalanceTuner.WorthNoise, the worth's noise per
            // score (the acc reweighter's NoisePerScore) at those PlayerAdjustedQuantile values (default 0 and 0.75). Read only.
            if (args.Length > 1 && args[0] == "worthnoise")
            {
                var quantiles = args.Length > 2
                    ? args.Skip(2).Select(a => double.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).ToArray()
                    : new[] { 0, 0.75 };
                var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={Path.GetFullPath(args[1])};Mode=ReadOnly;").Options;
                using var noiseContext = new AppContext(options);
                await BalanceTuner.WorthNoise(noiseContext, quantiles);
                return;
            }
            // dotnet run -c Release -- accreweight [Setting=value ...] [rescore] [file.db]: the acc reweighter alone on
            // wwwroot/Database.db (or file.db), with any acc reweighter or balance tuner setting (e.g. NoiseDeadZone=1,
            // PlayerAdjustedQuantile=0.5); rescore: RefreshEverything after it
            if (args.Length > 0 && args[0] == "accreweight")
            {
                bool rescore = args.Contains("rescore");
                var file = args.Skip(1).FirstOrDefault(a => a.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
                foreach (var setting in args.Skip(1).Where(a => a != "rescore" && a != file))
                {
                    var parts = setting.Split('=', 2);
                    // An acc reweighter setting, or a balance tuner one (the worth measure, e.g. PlayerAdjustedQuantile=0.5)
                    var property = typeof(AccReweighter).GetProperty(parts[0]) ?? typeof(BalanceTuner).GetProperty(parts[0])
                        ?? throw new ArgumentException($"No acc reweighter or balance tuner setting {parts[0]}");
                    property.SetValue(null, property.PropertyType.IsEnum
                        ? Enum.Parse(property.PropertyType, parts[1], true)
                        : Convert.ChangeType(parts[1], property.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
                    Console.WriteLine($"Acc reweight: {parts[0]} = {parts[1]}");
                }
                var webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
                var reweightPath = file != null ? Path.GetFullPath(file) : $"{webRoot}/Database.db";
                await EnsureSchema(reweightPath, reweightPath);
                var options = new DbContextOptionsBuilder<AppContext>().UseSqlite($"Data Source={reweightPath};").Options;
                using var reweightContext = new AppContext(options);
                await AccReweighter.Run(reweightContext, webRoot);
                if (rescore) await RefreshEverything(reweightContext);
                return;
            }
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
                // Uncomment to download the DB from S3 if wwwroot/Database.db is missing (1-2 minutes)
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

                builder.Services.AddSingleton<IDynamicDbContextService, DynamicDbContextService>();

                // Download the databases listed in databases.json from S3
                var tempDbService = new DynamicDbContextService(builder.Environment);
                await tempDbService.DownloadAllDatabasesAsync(builder.Environment.WebRootPath);
                // The Top Scores caches of databases no longer in databases.json
                TopScoresCache.DeleteUnlisted(builder.Environment.WebRootPath, (await tempDbService.GetAvailableDatabasesAsync()).Select(d => d.FileName));

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
                // And its leaderboards for the song recommendations (otherwise loaded on first use, about half a second)
                _ = Task.Run(async () =>
                {
                    using var context = startupDbService.CreateContext(startupDbService.GetMainDatabaseFileName());
                    await new beatleader_songrec.Api.DbBeatLeaderClient(context).GetAllRankedMapsAsync();
                });

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
                    // (only pp formula changes, RatingAPI/Pp/PpFormula.cs: skip Overwrite)
                    // Run FullPipeline to do all of the above in one pass (about 3.5 minutes in Release and 8-15GB of RAM). autoreweight:
                    // the acc reweighter and its re-score; balancer: the balance tuner and the model comparison.
                    // await FullPipeline(dbContext, env.WebRootPath, true, true, true);

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

                    // Color note count of every leaderboard from the maps folder, without rating the maps again (Overwrite and
                    // CurvesRefresh.Regenerate store it too). For every database in databases.json: RefreshNoteCountsForAllDatabases below.
                    // await CurvesRefresh.RefreshNoteCounts(dbContext);

                    // Scores, players, leaderboards (Megametric), stars, outliers and stats from the current ratings
                    // await RefreshEverything(dbContext);

                    // ---- Balance tools (Refresh/Tools, read only) ---------------------------------------------------------
                    // Suggested pass / tech / acc constants so no kind of map is worth more than others of its stars
                    // (in memory only). Writes wwwroot/balance-tuning.json for the Balance Tuning page.
                    // BalanceTuner.RatedWith must hold the constants the database was rated with. About 30 full re-scorings.
                    // await BalanceTuner.Run(dbContext, env.WebRootPath);

                    // The parts of the pp formula (RatingAPI/Pp/PpFormula.cs) one at a time, in memory only. Writes
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
                    // Note counts of every leaderboard (from the maps folder) in every database in databases.json
                    // await RefreshNoteCountsForAllDatabases(tempDbService, builder.Environment.WebRootPath);
                }

                await app.RunAsync();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message + "   " + e.StackTrace);
            }
        }

        // autoreweight: run the acc reweighter, then re-score. balancer: run the balance tuner and the model comparison.
        // keepAccReweight: without the reweighter, keep the last run's multipliers on the acc ratings (false: cleared).
        public static async Task FullPipeline(AppContext dbContext, string webRootPath, bool autoreweight, bool balancer, bool keepAccReweight)
        {
            // The acc reweighter always starts from raw acc ratings: dividing stored multipliers back out would drift by a
            // float rounding error on every run
            await RatingsRefresh.Overwrite(dbContext, keepAccReweight && !autoreweight);
            // The acc reweighter re-scores in memory, so the stored re-score is only needed after it
            if (!autoreweight) await RefreshEverything(dbContext);
            if (autoreweight)
            {
                await AccReweighter.Run(dbContext, webRootPath);
                await RefreshEverything(dbContext);
            }
            if (balancer)
            {
                await BalanceTuner.Run(dbContext, webRootPath);
                await ModelComparisonExport.Run(webRootPath, ProdDatabase);
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

                await EnsureSchema(db.Name, path);
            }
        }

        // Ensures every table and column from DBModels exists in that database, adding missing ones with default values
        private static async Task EnsureSchema(string name, string path)
        {
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
                            Console.WriteLine($"[{name}] Created missing table {tableName}");
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
                        Console.WriteLine($"[{name}] Added missing column {tableName}.{columnName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error verifying schema for {name}: {ex.Message}");
            }
        }

        // Refreshes the stats of every database in databases.json
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

        // Fills Leaderboard.NoteCount (CurvesRefresh.RefreshNoteCounts) in every database in databases.json
        private static async Task RefreshNoteCountsForAllDatabases(IDynamicDbContextService dbService, string webRootPath)
        {
            var databases = await dbService.GetAvailableDatabasesAsync();
            foreach (var db in databases)
            {
                var path = Path.Combine(webRootPath, db.FileName);
                if (!File.Exists(path))
                {
                    Console.WriteLine($"Skipping note counts for {db.Name}: file not found");
                    continue;
                }
                try
                {
                    var optionsBuilder = new DbContextOptionsBuilder<AppContext>();
                    optionsBuilder.UseSqlite($"Data Source={path};");
                    using var dbContext = new AppContext(optionsBuilder.Options);
                    Console.WriteLine($"Note counts: {db.Name} ({db.FileName})");
                    await CurvesRefresh.RefreshNoteCounts(dbContext);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error filling note counts for {db.Name}: {ex.Message}");
                }
            }
        }

        // Refreshes the leaderboards (Megametric) of every database in databases.json
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
