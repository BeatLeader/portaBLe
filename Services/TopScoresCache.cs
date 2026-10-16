using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace portaBLe.Services
{
    // Every score with what the Top Scores page shows, kept in memory and saved to a file per database
    // (wwwroot/cache/<db file>.topscores.bin), so the page never queries the scores table.
    //
    // - Which database version a file belongs to is checked by content (player count / pp / ranks, map ratings, last score
    //   id), not by the file's modified time, so a file built locally still matches the same database downloaded on the
    //   server. Program.UploadDatabaseAsync uploads it next to the database; DownloadAllDatabasesAsync downloads it.
    // - Missing or outdated: built in the background (read straight from SQLite, about a minute for 4M scores) while the page
    //   says so. RefreshEverything builds it at the end.
    // - Compact columns (about 56 MB for 4.4M scores, gzip): acc and the acc / pass pp are rounded (0.0015% and 0.1pp), pp to
    //   0.01, dates to the day. Weight (from the score's position in the player's list, as ScoresPlayersRefresh) and tech pp
    //   (pp - acc pp - pass pp) are computed on load. Modded scores use their modifier's stars and predicted acc.
    public static class TopScoresCache
    {
        private const string Magic = "TOPSCORES";
        private const int FormatVersion = 2;
        private const int MaxInMemory = 2;          // databases kept in memory (about 120 MB each for 4.4M scores)
        private const int MaxSortOrders = 4;        // extra sort orders kept per database (17 MB each)
        public const int MaxWeighted = 10000;       // as ScoresPlayersRefresh: scores past the 10000th weigh 0
        private static readonly DateTime DayZero = new(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly float[] Weights = Enumerable.Range(0, MaxWeighted).Select(i => MathF.Pow(0.965f, i)).ToArray();

        // Day number of a date, as stored per score (Snapshot.Day): for date filters
        public static int DayNumber(DateTime utc) => (int)Math.Floor((utc - DayZero).TotalDays);

        public record PlayerInfo(string Id, string Name, int Rank);
        public record MapInfo(string Id, string Name, string Mapper, string Difficulty, string Mode);

        public class Snapshot
        {
            public string DbPath = "";
            public string ContentStamp = "";
            internal (long Ticks, long Length) FileStamp;
            public DateTime BuiltAtUtc;
            public double BuildSeconds;

            public PlayerInfo[] Players = Array.Empty<PlayerInfo>();
            public MapInfo[] Maps = Array.Empty<MapInfo>();
            public string[] Modifiers = Array.Empty<string>();
            public float[] MapStars = Array.Empty<float>();          // [map * 4 + speed]: base, SS, FS, SFS
            public float[] MapPredictedAcc = Array.Empty<float>();   // same layout

            // One entry per score, best pp first
            public int Count;
            public int[] Player = Array.Empty<int>();
            public ushort[] Map = Array.Empty<ushort>();
            public ushort[] ModsFc = Array.Empty<ushort>();          // Modifiers index * 2 + FC
            public ushort[] Acc = Array.Empty<ushort>();             // accuracy * 65535
            public float[] Pp = Array.Empty<float>();
            public ushort[] AccPp10 = Array.Empty<ushort>();         // acc pp * 10
            public ushort[] PassPp10 = Array.Empty<ushort>();        // pass pp * 10
            public int[] MapRank = Array.Empty<int>();
            public ushort[] Day = Array.Empty<ushort>();             // days since 2015-01-01
            public ushort[] PlayerPos = Array.Empty<ushort>();       // position in the player's list (0 = their best)

            // Derived on load
            public string[] Modes = Array.Empty<string>();
            public byte[] ModsSpeed = Array.Empty<byte>();           // per Modifiers entry: 0 none, 1 SS, 2 FS, 3 SFS

            public int Speed(int i) => ModsSpeed[ModsFc[i] >> 1];
            public string ModifiersOf(int i) => Modifiers[ModsFc[i] >> 1];
            public bool FC(int i) => (ModsFc[i] & 1) != 0;
            public float Stars(int i) => MapStars[Map[i] * 4 + Speed(i)];
            public float PredictedAcc(int i) => MapPredictedAcc[Map[i] * 4 + Speed(i)];
            public float Accuracy(int i) => Acc[i] / 65535f;
            public float AccPp(int i) => AccPp10[i] / 10f;
            public float PassPp(int i) => PassPp10[i] / 10f;
            public float TechPp(int i) => MathF.Max(0, Pp[i] - AccPp(i) - PassPp(i));
            public float Weight(int i) => PlayerPos[i] < MaxWeighted ? Weights[PlayerPos[i]] : 0;
            public DateTime Date(int i) => DayZero.AddDays(Day[i]);

            private readonly ConcurrentDictionary<string, int[]> _orders = new();

            // Score indexes sorted by a value, highest first, computed once per key (a few kept)
            public int[] Order(string key, Func<int, float> value)
            {
                if (_orders.TryGetValue(key, out var cached)) return cached;
                var keys = new float[Count];
                var order = new int[Count];
                for (int i = 0; i < Count; i++) { keys[i] = -value(i); order[i] = i; }
                Array.Sort(keys, order);
                if (_orders.Count >= MaxSortOrders) _orders.Clear();
                _orders[key] = order;
                return order;
            }

            internal void Derive()
            {
                Modes = Maps.Select(m => m.Mode).Where(m => !string.IsNullOrEmpty(m)).Distinct().OrderBy(m => m).ToArray();
                ModsSpeed = Modifiers.Select(m => (byte)(Pages.ScoreModifierFilter.SpeedModifier(m) switch { "SS" => 1, "FS" => 2, "SF" => 3, _ => 0 })).ToArray();

                // Weight positions: scores are best pp first (ties by score id), as ScoresPlayersRefresh orders them
                var seen = new int[Players.Length];
                PlayerPos = new ushort[Count];
                for (int i = 0; i < Count; i++)
                {
                    int k = seen[Player[i]]++;
                    PlayerPos[i] = (ushort)Math.Min(k, ushort.MaxValue);
                }
            }
        }

        public record Result(Snapshot? Data, bool Building, string? Error);

        private static readonly ConcurrentDictionary<string, Snapshot> _loaded = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Task> _building = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, (string Message, DateTime AtUtc)> _errors = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _buildLock = new();

        public static string DbPathOf(DbContext context) => Path.GetFullPath(context.Database.GetDbConnection().DataSource);

        public static string CachePathOf(string dbPath) =>
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".", "cache", Path.GetFileName(dbPath) + ".topscores.bin");

        private static (long, long) FileStampOf(string dbPath)
        {
            var info = new FileInfo(dbPath);
            if (!info.Exists) return (0, 0);
            long ticks = info.LastWriteTimeUtc.Ticks;
            var wal = new FileInfo(dbPath + "-wal");
            if (wal.Exists && wal.Length > 0) ticks = Math.Max(ticks, wal.LastWriteTimeUtc.Ticks);
            return (ticks, info.Length);
        }

        public static bool IsBuilding(string dbPath) => _building.TryGetValue(dbPath, out var task) && !task.IsCompleted;

        // For the page: the scores when they're in memory or in an up-to-date file (about a second to load). Otherwise starts
        // building them in the background and returns what's in memory (an older version, or null) with Building = true.
        public static async Task<Result> TryGetAsync(Func<DbContext> contextFactory)
        {
            string dbPath;
            using (var c = contextFactory()) dbPath = DbPathOf(c);
            var fileStamp = FileStampOf(dbPath);

            _loaded.TryGetValue(dbPath, out var current);
            if (current != null && current.FileStamp == fileStamp) return new Result(current, IsBuilding(dbPath), null);
            if (IsBuilding(dbPath)) return new Result(current, true, null);

            var gate = _locks.GetOrAdd(dbPath, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                _loaded.TryGetValue(dbPath, out current);
                if (current != null && current.FileStamp == fileStamp) return new Result(current, IsBuilding(dbPath), null);

                // The database file changed on disk (or first use): compare its content with what's in memory, then the file
                string content;
                using (var c = contextFactory()) content = await ContentStampAsync(c);
                if (current != null && current.ContentStamp == content)
                {
                    current.FileStamp = fileStamp;
                    return new Result(current, IsBuilding(dbPath), null);
                }

                var fromFile = TryRead(CachePathOf(dbPath), content);
                if (fromFile != null)
                {
                    Keep(fromFile, dbPath, fileStamp);
                    return new Result(fromFile, false, null);
                }

                // After a failed build, retry 10 minutes later instead of on every visit
                if (_errors.TryGetValue(dbPath, out var error) && DateTime.UtcNow - error.AtUtc < RetryAfter)
                    return new Result(current, false, error.Message);
                StartBuild(contextFactory, dbPath);
                return new Result(current, true, null);
            }
            finally
            {
                gate.Release();
            }
        }

        // Builds in the background (does nothing when a build of this database is already running)
        private static void StartBuild(Func<DbContext> contextFactory, string? dbPath = null)
        {
            if (dbPath == null) using (var c = contextFactory()) dbPath = DbPathOf(c);
            lock (_buildLock)
            {
                if (IsBuilding(dbPath)) return;
                _errors.TryRemove(dbPath, out _);
                string path = dbPath;
                _building[path] = Task.Run(async () =>
                {
                    try
                    {
                        using var context = contextFactory();
                        await BuildAndSaveAsync(context);
                    }
                    catch (Exception e)
                    {
                        _errors[path] = (e.Message, DateTime.UtcNow);
                        Console.WriteLine($"Top scores cache: build failed for {Path.GetFileName(path)}: {e}");
                    }
                });
            }
        }

        // Builds from the database, saves the file and keeps it in memory (RefreshEverything, background builds)
        public static async Task<Snapshot> BuildAndSaveAsync(DbContext context)
        {
            string dbPath = DbPathOf(context);
            var fileStamp = FileStampOf(dbPath);
            string content = await ContentStampAsync(context);
            var snapshot = await BuildAsync(context, dbPath);
            snapshot.ContentStamp = content;
            string cachePath = CachePathOf(dbPath);
            try
            {
                Write(cachePath, snapshot);
                Console.WriteLine($"Top scores cache: saved {cachePath} ({new FileInfo(cachePath).Length / 1e6:F1} MB)");
            }
            catch (Exception e)
            {
                Console.WriteLine($"Top scores cache: could not write {cachePath}: {e.Message}");
            }
            snapshot.Derive();
            Keep(snapshot, dbPath, fileStamp);
            return snapshot;
        }

        private static void Keep(Snapshot snapshot, string dbPath, (long, long) fileStamp)
        {
            snapshot.DbPath = dbPath;
            snapshot.FileStamp = fileStamp;
            foreach (var key in _loaded.Keys.Where(k => !k.Equals(dbPath, StringComparison.OrdinalIgnoreCase)).Skip(MaxInMemory - 1).ToList())
                _loaded.TryRemove(key, out _);
            _loaded[dbPath] = snapshot;
        }

        // ---- Database ---------------------------------------------------------------------------------------------------

        private static async Task<DbConnection> OpenAsync(DbContext context)
        {
            var connection = context.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await connection.OpenAsync();
            return connection;
        }

        private static DbCommand Command(DbConnection connection, string sql)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 0;
            return command;
        }

        // Changes whenever a refresh changes ratings, scores or players; the same for the same database copied elsewhere
        private static async Task<string> ContentStampAsync(DbContext context)
        {
            var connection = await OpenAsync(context);
            var parts = new List<string>();
            foreach (var sql in new[]
            {
                "SELECT COUNT(*), TOTAL(Pp), TOTAL(Rank), TOTAL(AccPp) FROM Players",
                "SELECT COUNT(*), TOTAL(Stars), TOTAL(PredictedAcc), TOTAL(AccRating), TOTAL(PassRating) FROM Leaderboards",
                "SELECT MAX(Id) FROM Scores",
            })
            {
                using var command = Command(connection, sql);
                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                    for (int i = 0; i < reader.FieldCount; i++)
                        parts.Add(reader.IsDBNull(i) ? "-" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "");
            }
            return string.Join("|", parts);
        }

        private static async Task<Snapshot> BuildAsync(DbContext context, string dbPath)
        {
            var watch = Stopwatch.StartNew();
            Console.WriteLine($"Top scores cache: building for {Path.GetFileName(dbPath)}");
            var connection = await OpenAsync(context);
            static string Str(DbDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
            static float Flt(DbDataReader r, int i) => r.IsDBNull(i) ? 0f : (float)r.GetDouble(i);

            var players = new List<PlayerInfo>();
            var playerIndex = new Dictionary<string, int>();
            using (var command = Command(connection, "SELECT Id, Name, Rank FROM Players"))
            using (var r = await command.ExecuteReaderAsync())
                while (await r.ReadAsync())
                {
                    string id = Str(r, 0);
                    playerIndex[id] = players.Count;
                    players.Add(new PlayerInfo(id, Str(r, 1), r.IsDBNull(2) ? 0 : r.GetInt32(2)));
                }

            var maps = new List<MapInfo>();
            var mapIndex = new Dictionary<string, int>();
            var stars = new List<float>();
            var predicted = new List<float>();
            using (var command = Command(connection,
                "SELECT l.Id, l.Name, l.Mapper, l.DifficultyName, l.ModeName, l.Stars, l.PredictedAcc, " +
                "m.SSStars, m.FSStars, m.SFStars, m.SSPredictedAcc, m.FSPredictedAcc, m.SFPredictedAcc " +
                "FROM Leaderboards l LEFT JOIN ModifiersRating m ON m.Id = l.ModifiersRatingId"))
            using (var r = await command.ExecuteReaderAsync())
                while (await r.ReadAsync())
                {
                    string id = Str(r, 0);
                    mapIndex[id] = maps.Count;
                    maps.Add(new MapInfo(id, Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4)));
                    float baseStars = Flt(r, 5), basePredicted = Flt(r, 6);
                    // A speed modifier that isn't rated uses the base values (as ScoreModifierFilter.Pick)
                    stars.Add(baseStars);
                    for (int k = 7; k <= 9; k++) { float v = Flt(r, k); stars.Add(v > 0 ? v : baseStars); }
                    predicted.Add(basePredicted);
                    for (int k = 10; k <= 12; k++) { float v = Flt(r, k); predicted.Add(v > 0 ? v : basePredicted); }
                }
            if (maps.Count > ushort.MaxValue) throw new InvalidOperationException($"{maps.Count} leaderboards: more than the cache format holds");

            int capacity;
            using (var command = Command(connection, "SELECT COUNT(*) FROM Scores WHERE Pp > 0"))
                capacity = Convert.ToInt32(await command.ExecuteScalarAsync());

            var ids = new int[capacity];
            var player = new int[capacity];
            var map = new ushort[capacity];
            var modsFc = new ushort[capacity];
            var acc = new ushort[capacity];
            var pp = new float[capacity];
            var accPp = new ushort[capacity];
            var passPp = new ushort[capacity];
            var rank = new int[capacity];
            var day = new ushort[capacity];
            var modifiers = new List<string>();
            var modifierIndex = new Dictionary<string, int>();
            static ushort U16(double v) => (ushort)Math.Clamp(Math.Round(v), 0, ushort.MaxValue);

            int n = 0;
            using (var command = Command(connection,
                "SELECT Id, PlayerId, LeaderboardId, Modifiers, Accuracy, Pp, AccPP, PassPP, Rank, FC, Timepost FROM Scores WHERE Pp > 0"))
            using (var r = await command.ExecuteReaderAsync())
                while (await r.ReadAsync())
                {
                    if (n >= capacity) break;
                    if (r.IsDBNull(1) || r.IsDBNull(2)) continue;
                    if (!playerIndex.TryGetValue(r.GetString(1), out int p) || !mapIndex.TryGetValue(r.GetString(2), out int m)) continue;
                    string mods = Str(r, 3);
                    if (!modifierIndex.TryGetValue(mods, out int mi))
                    {
                        mi = modifiers.Count;
                        modifiers.Add(mods);
                        modifierIndex[mods] = mi;
                    }
                    bool fc = !r.IsDBNull(9) && r.GetInt64(9) != 0;
                    long timepost = r.IsDBNull(10) ? 0 : r.GetInt64(10);

                    ids[n] = r.GetInt32(0);
                    player[n] = p;
                    map[n] = (ushort)m;
                    modsFc[n] = (ushort)(mi * 2 + (fc ? 1 : 0));
                    acc[n] = U16(Flt(r, 4) * 65535.0);
                    pp[n] = Flt(r, 5);
                    accPp[n] = U16(Flt(r, 6) * 10.0);
                    passPp[n] = U16(Flt(r, 7) * 10.0);
                    rank[n] = r.IsDBNull(8) ? 0 : r.GetInt32(8);
                    day[n] = timepost > 0 ? U16((DateTimeOffset.FromUnixTimeSeconds(timepost).UtcDateTime - DayZero).TotalDays) : (ushort)0;
                    n++;
                }
            if (modifiers.Count > ushort.MaxValue / 2) throw new InvalidOperationException("Too many modifier combinations for the cache format");

            // Best pp first, ties by score id (the order ScoresPlayersRefresh gives the weights in)
            var keys = new long[n];
            var order = new int[n];
            for (int i = 0; i < n; i++)
            {
                keys[i] = ((long)(int.MaxValue - BitConverter.SingleToInt32Bits(pp[i])) << 32) | (uint)ids[i];
                order[i] = i;
            }
            Array.Sort(keys, order);
            keys = null!;
            ids = null!;
            T[] Sorted<T>(T[] column) { var result = new T[n]; for (int i = 0; i < n; i++) result[i] = column[order[i]]; return result; }

            var snapshot = new Snapshot
            {
                DbPath = dbPath,
                BuiltAtUtc = DateTime.UtcNow,
                Players = players.ToArray(),
                Maps = maps.ToArray(),
                Modifiers = modifiers.ToArray(),
                MapStars = stars.ToArray(),
                MapPredictedAcc = predicted.ToArray(),
                Count = n,
            };
            snapshot.Player = Sorted(player); player = null!;
            snapshot.Map = Sorted(map); map = null!;
            snapshot.ModsFc = Sorted(modsFc); modsFc = null!;
            snapshot.Acc = Sorted(acc); acc = null!;
            // pp to 0.01, as stored in the file
            var sortedPp = Sorted(pp); pp = null!;
            for (int i = 0; i < n; i++) sortedPp[i] = MathF.Round(sortedPp[i] * 100) / 100;
            snapshot.Pp = sortedPp;
            snapshot.AccPp10 = Sorted(accPp); accPp = null!;
            snapshot.PassPp10 = Sorted(passPp); passPp = null!;
            snapshot.MapRank = Sorted(rank); rank = null!;
            snapshot.Day = Sorted(day);

            watch.Stop();
            snapshot.BuildSeconds = watch.Elapsed.TotalSeconds;
            Console.WriteLine($"Top scores cache: {n:N0} scores in {snapshot.BuildSeconds:F1} s");
            return snapshot;
        }

        // ---- File -------------------------------------------------------------------------------------------------------

        private static void WriteColumn<T>(Stream stream, T[] column) where T : struct =>
            stream.Write(MemoryMarshal.AsBytes(column.AsSpan()));

        private static T[] ReadColumn<T>(Stream stream, int count) where T : struct
        {
            var column = new T[count];
            stream.ReadExactly(MemoryMarshal.AsBytes(column.AsSpan()));
            return column;
        }

        private static void Write(string path, Snapshot s)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            using (var file = File.Create(temp))
            {
                using (var header = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    header.Write(Magic);
                    header.Write(FormatVersion);
                    header.Write(s.ContentStamp);
                    header.Write(s.BuiltAtUtc.Ticks);
                    header.Write(s.BuildSeconds);
                    header.Write(s.Count);
                }
                using var gzip = new GZipStream(file, CompressionLevel.Optimal);
                using var buffered = new BufferedStream(gzip, 1 << 16);
                using (var w = new BinaryWriter(buffered, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    w.Write(s.Players.Length);
                    foreach (var p in s.Players) { w.Write(p.Id); w.Write(p.Name); w.Write(p.Rank); }
                    w.Write(s.Maps.Length);
                    foreach (var m in s.Maps) { w.Write(m.Id); w.Write(m.Name); w.Write(m.Mapper); w.Write(m.Difficulty); w.Write(m.Mode); }
                    w.Write(s.Modifiers.Length);
                    foreach (var m in s.Modifiers) w.Write(m);
                }
                WriteColumn(buffered, s.MapStars);
                WriteColumn(buffered, s.MapPredictedAcc);
                WriteColumn(buffered, s.Player);
                WriteColumn(buffered, s.Map);
                WriteColumn(buffered, s.ModsFc);
                WriteColumn(buffered, s.Acc);
                // pp as drops in hundredths from the previous score (sorted, so mostly 0 or tiny: compresses to almost nothing)
                var drops = new int[s.Count];
                long previous = 0;
                for (int i = 0; i < s.Count; i++)
                {
                    long hundredths = (long)Math.Round(s.Pp[i] * 100.0);
                    drops[i] = (int)(i == 0 ? hundredths : previous - hundredths);
                    previous = hundredths;
                }
                WriteColumn(buffered, drops);
                WriteColumn(buffered, s.AccPp10);
                WriteColumn(buffered, s.PassPp10);
                WriteColumn(buffered, s.MapRank);
                WriteColumn(buffered, s.Day);
            }
            File.Move(temp, path, true);
        }

        // null when the file is missing, unreadable, an older format or made from another version of the database
        private static Snapshot? TryRead(string path, string contentStamp)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var watch = Stopwatch.StartNew();
                using var file = File.OpenRead(path);
                var s = new Snapshot();
                using (var header = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    if (header.ReadString() != Magic || header.ReadInt32() != FormatVersion) return null;
                    s.ContentStamp = header.ReadString();
                    if (s.ContentStamp != contentStamp)
                    {
                        Console.WriteLine($"Top scores cache: {Path.GetFileName(path)} is for another version of the database");
                        return null;
                    }
                    s.BuiltAtUtc = new DateTime(header.ReadInt64(), DateTimeKind.Utc);
                    s.BuildSeconds = header.ReadDouble();
                    s.Count = header.ReadInt32();
                }
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var buffered = new BufferedStream(gzip, 1 << 16);
                using (var r = new BinaryReader(buffered, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    s.Players = new PlayerInfo[r.ReadInt32()];
                    for (int i = 0; i < s.Players.Length; i++) s.Players[i] = new PlayerInfo(r.ReadString(), r.ReadString(), r.ReadInt32());
                    s.Maps = new MapInfo[r.ReadInt32()];
                    for (int i = 0; i < s.Maps.Length; i++) s.Maps[i] = new MapInfo(r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString());
                    s.Modifiers = new string[r.ReadInt32()];
                    for (int i = 0; i < s.Modifiers.Length; i++) s.Modifiers[i] = r.ReadString();
                }
                int n = s.Count;
                s.MapStars = ReadColumn<float>(buffered, s.Maps.Length * 4);
                s.MapPredictedAcc = ReadColumn<float>(buffered, s.Maps.Length * 4);
                s.Player = ReadColumn<int>(buffered, n);
                s.Map = ReadColumn<ushort>(buffered, n);
                s.ModsFc = ReadColumn<ushort>(buffered, n);
                s.Acc = ReadColumn<ushort>(buffered, n);
                var drops = ReadColumn<int>(buffered, n);
                s.Pp = new float[n];
                long hundredths = 0;
                for (int i = 0; i < n; i++)
                {
                    hundredths = i == 0 ? drops[i] : hundredths - drops[i];
                    s.Pp[i] = hundredths / 100f;
                }
                drops = null!;
                s.AccPp10 = ReadColumn<ushort>(buffered, n);
                s.PassPp10 = ReadColumn<ushort>(buffered, n);
                s.MapRank = ReadColumn<int>(buffered, n);
                s.Day = ReadColumn<ushort>(buffered, n);
                s.Derive();
                Console.WriteLine($"Top scores cache: loaded {Path.GetFileName(path)} ({n:N0} scores) in {watch.Elapsed.TotalSeconds:F1} s");
                return s;
            }
            catch (Exception e)
            {
                Console.WriteLine($"Top scores cache: could not read {path}, rebuilding: {e.Message}");
                return null;
            }
        }
    }
}
