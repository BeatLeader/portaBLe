using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;

namespace portaBLe.Services
{
    // Every score with what the Top Scores page shows, saved to a file per database (wwwroot/cache/<db>.topscores.bin) and
    // kept in memory, so the page never queries the database. The file stores the database's modified time and size:
    // when the database changes (a refresh), the next GetAsync rebuilds it. RefreshEverything rebuilds it at the end.
    // Modded scores use their modifier's stars and predicted acc (an SFS score gets the SFS stars), base ones otherwise.
    public static class TopScoresCache
    {
        private const string Magic = "TOPSCORES";
        private const int FormatVersion = 1;

        public struct ScoreRow
        {
            public int Player;
            public int Map;
            public int Mods;          // index in Snapshot.Modifiers
            public float Stars;
            public float Accuracy;
            public float PredictedAcc;
            public float Pp;
            public float AccPp;
            public float PassPp;
            public float TechPp;
            public float Weight;
            public int MapRank;
            public int Timepost;
            public bool FC;
        }

        public record PlayerInfo(string Id, string Name, int Rank);
        public record MapInfo(string Id, string Name, string Mapper, string Difficulty, string Mode);

        public class Snapshot
        {
            public string DbPath = "";
            public long DbStamp;          // database file modified time (UTC ticks)
            public long DbLength;
            public DateTime BuiltAtUtc;
            public double BuildSeconds;
            public bool LoadedFromFile;
            public PlayerInfo[] Players = Array.Empty<PlayerInfo>();
            public MapInfo[] Maps = Array.Empty<MapInfo>();
            public string[] Modifiers = Array.Empty<string>();
            public ScoreRow[] Scores = Array.Empty<ScoreRow>();   // best pp first

            // Derived on load
            public string[] Modes = Array.Empty<string>();
            public byte[] ModsSpeed = Array.Empty<byte>();          // per Modifiers entry: 0 none, 1 SS, 2 FS, 3 SFS

            private readonly ConcurrentDictionary<string, int[]> _orders = new();

            // Score indexes in the order of a sort key, computed once per key
            public int[] Order(string key, Func<ScoreRow, float> descendingKey)
            {
                return _orders.GetOrAdd(key, _ =>
                {
                    var keys = new float[Scores.Length];
                    var order = new int[Scores.Length];
                    for (int i = 0; i < Scores.Length; i++) { keys[i] = -descendingKey(Scores[i]); order[i] = i; }
                    Array.Sort(keys, order);
                    return order;
                });
            }

            internal void Derive()
            {
                Modes = Maps.Select(m => m.Mode).Where(m => !string.IsNullOrEmpty(m)).Distinct().OrderBy(m => m).ToArray();
                ModsSpeed = Modifiers.Select(m => (byte)(Pages.ScoreModifierFilter.SpeedModifier(m) switch { "SS" => 1, "FS" => 2, "SF" => 3, _ => 0 })).ToArray();
            }
        }

        private static readonly ConcurrentDictionary<string, Snapshot> _loaded = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

        public static string DbPathOf(DbContext context) => Path.GetFullPath(context.Database.GetDbConnection().DataSource);

        public static string CachePathOf(string dbPath) =>
            Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "cache", Path.GetFileName(dbPath) + ".topscores.bin");

        private static (long Stamp, long Length) DbStamp(string dbPath)
        {
            var info = new FileInfo(dbPath);
            if (!info.Exists) return (0, 0);
            long stamp = info.LastWriteTimeUtc.Ticks;
            var wal = new FileInfo(dbPath + "-wal");
            if (wal.Exists && wal.Length > 0) stamp = Math.Max(stamp, wal.LastWriteTimeUtc.Ticks);
            return (stamp, info.Length);
        }

        // The scores of this database: from memory, else the cache file, else built from the database (and saved).
        // rebuild: ignore the memory and the file and build again.
        public static async Task<Snapshot> GetAsync(DbContext context, bool rebuild = false)
        {
            string dbPath = DbPathOf(context);
            var (stamp, length) = DbStamp(dbPath);
            if (!rebuild && _loaded.TryGetValue(dbPath, out var current) && current.DbStamp == stamp && current.DbLength == length)
                return current;

            var gate = _locks.GetOrAdd(dbPath, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                (stamp, length) = DbStamp(dbPath);
                // Another request may have loaded it while this one waited
                if (!rebuild && _loaded.TryGetValue(dbPath, out current) && current.DbStamp == stamp && current.DbLength == length)
                    return current;

                string cachePath = CachePathOf(dbPath);
                Snapshot? snapshot = rebuild ? null : TryRead(cachePath, stamp, length);
                if (snapshot == null)
                {
                    snapshot = await BuildAsync(context, dbPath, stamp, length);
                    try { Write(cachePath, snapshot); }
                    catch (Exception e) { Console.WriteLine($"Top scores cache: could not write {cachePath}: {e.Message}"); }
                }
                snapshot.DbPath = dbPath;
                snapshot.Derive();

                // Keep at most 3 databases in memory
                foreach (var key in _loaded.Keys.Where(k => !k.Equals(dbPath, StringComparison.OrdinalIgnoreCase)).Skip(2).ToList())
                    _loaded.TryRemove(key, out _);
                _loaded[dbPath] = snapshot;
                return snapshot;
            }
            finally
            {
                gate.Release();
            }
        }

        private static async Task<Snapshot> BuildAsync(DbContext context, string dbPath, long stamp, long length)
        {
            var watch = Stopwatch.StartNew();
            Console.WriteLine($"Top scores cache: building for {Path.GetFileName(dbPath)}");

            var players = await context.Set<Player>().AsNoTracking()
                .Select(p => new { p.Id, p.Name, p.Rank })
                .ToListAsync();
            var playerIndex = new Dictionary<string, int>(players.Count);
            for (int i = 0; i < players.Count; i++) playerIndex[players[i].Id] = i;

            // Without the curves (not needed here, and slow to parse)
            var maps = await context.Set<Leaderboard>().AsNoTracking()
                .Select(l => new
                {
                    l.Id, l.Name, l.Mapper, l.DifficultyName, l.ModeName, l.Stars, l.PredictedAcc,
                    SSStars = l.ModifiersRating == null ? 0f : l.ModifiersRating.SSStars,
                    FSStars = l.ModifiersRating == null ? 0f : l.ModifiersRating.FSStars,
                    SFStars = l.ModifiersRating == null ? 0f : l.ModifiersRating.SFStars,
                    SSAcc = l.ModifiersRating == null ? 0f : l.ModifiersRating.SSPredictedAcc,
                    FSAcc = l.ModifiersRating == null ? 0f : l.ModifiersRating.FSPredictedAcc,
                    SFAcc = l.ModifiersRating == null ? 0f : l.ModifiersRating.SFPredictedAcc,
                })
                .ToListAsync();
            var mapIndex = new Dictionary<string, int>(maps.Count);
            for (int i = 0; i < maps.Count; i++) mapIndex[maps[i].Id] = i;

            var modifiers = new List<string>();
            var modifierIndex = new Dictionary<string, int>();
            var rows = new List<ScoreRow>();

            var scores = context.Set<Score>().AsNoTracking()
                .Where(s => s.Pp > 0)
                .OrderByDescending(s => s.Pp)
                .Select(s => new { s.PlayerId, s.LeaderboardId, s.Modifiers, s.Accuracy, s.Pp, s.AccPP, s.PassPP, s.TechPP, s.Weight, s.Rank, s.FC, s.Timepost })
                .AsAsyncEnumerable();

            await foreach (var s in scores)
            {
                if (s.PlayerId == null || s.LeaderboardId == null) continue;
                if (!playerIndex.TryGetValue(s.PlayerId, out int player) || !mapIndex.TryGetValue(s.LeaderboardId, out int map)) continue;

                string mods = s.Modifiers ?? "";
                if (!modifierIndex.TryGetValue(mods, out int modsIndex))
                {
                    modsIndex = modifiers.Count;
                    modifiers.Add(mods);
                    modifierIndex[mods] = modsIndex;
                }

                var m = maps[map];
                var speed = Pages.ScoreModifierFilter.SpeedModifier(mods);
                rows.Add(new ScoreRow
                {
                    Player = player,
                    Map = map,
                    Mods = modsIndex,
                    Stars = (float)Pages.ScoreModifierFilter.Pick(speed, m.Stars, m.SSStars, m.FSStars, m.SFStars),
                    PredictedAcc = (float)Pages.ScoreModifierFilter.Pick(speed, m.PredictedAcc, m.SSAcc, m.FSAcc, m.SFAcc),
                    Accuracy = s.Accuracy,
                    Pp = s.Pp,
                    AccPp = s.AccPP,
                    PassPp = s.PassPP,
                    TechPp = s.TechPP,
                    Weight = s.Weight,
                    MapRank = s.Rank,
                    Timepost = s.Timepost,
                    FC = s.FC,
                });
            }

            watch.Stop();
            Console.WriteLine($"Top scores cache: {rows.Count:N0} scores in {watch.Elapsed.TotalSeconds:F1} s");
            return new Snapshot
            {
                DbPath = dbPath,
                DbStamp = stamp,
                DbLength = length,
                BuiltAtUtc = DateTime.UtcNow,
                BuildSeconds = watch.Elapsed.TotalSeconds,
                Players = players.Select(p => new PlayerInfo(p.Id, p.Name ?? "", p.Rank)).ToArray(),
                Maps = maps.Select(l => new MapInfo(l.Id, l.Name ?? "", l.Mapper ?? "", l.DifficultyName ?? "", l.ModeName ?? "")).ToArray(),
                Modifiers = modifiers.ToArray(),
                Scores = rows.ToArray(),
            };
        }

        private static void Write(string path, Snapshot s)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            using (var file = File.Create(temp))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var w = new BinaryWriter(gzip))
            {
                w.Write(Magic);
                w.Write(FormatVersion);
                w.Write(s.DbStamp);
                w.Write(s.DbLength);
                w.Write(s.BuiltAtUtc.Ticks);
                w.Write(s.BuildSeconds);

                w.Write(s.Players.Length);
                foreach (var p in s.Players) { w.Write(p.Id); w.Write(p.Name); w.Write(p.Rank); }
                w.Write(s.Maps.Length);
                foreach (var m in s.Maps) { w.Write(m.Id); w.Write(m.Name); w.Write(m.Mapper); w.Write(m.Difficulty); w.Write(m.Mode); }
                w.Write(s.Modifiers.Length);
                foreach (var m in s.Modifiers) w.Write(m);

                w.Write(s.Scores.Length);
                foreach (var r in s.Scores)
                {
                    w.Write(r.Player); w.Write(r.Map); w.Write(r.Mods);
                    w.Write(r.Stars); w.Write(r.Accuracy); w.Write(r.PredictedAcc);
                    w.Write(r.Pp); w.Write(r.AccPp); w.Write(r.PassPp); w.Write(r.TechPp); w.Write(r.Weight);
                    w.Write(r.MapRank); w.Write(r.Timepost); w.Write(r.FC);
                }
            }
            File.Move(temp, path, true);
        }

        // null when the file is missing, unreadable, an older format or made from another version of the database
        private static Snapshot? TryRead(string path, long stamp, long length)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using var file = File.OpenRead(path);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var r = new BinaryReader(new BufferedStream(gzip, 1 << 16));
                if (r.ReadString() != Magic || r.ReadInt32() != FormatVersion) return null;
                var s = new Snapshot { DbStamp = r.ReadInt64(), DbLength = r.ReadInt64() };
                if (s.DbStamp != stamp || s.DbLength != length) return null;
                s.BuiltAtUtc = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
                s.BuildSeconds = r.ReadDouble();
                s.LoadedFromFile = true;

                s.Players = new PlayerInfo[r.ReadInt32()];
                for (int i = 0; i < s.Players.Length; i++) s.Players[i] = new PlayerInfo(r.ReadString(), r.ReadString(), r.ReadInt32());
                s.Maps = new MapInfo[r.ReadInt32()];
                for (int i = 0; i < s.Maps.Length; i++) s.Maps[i] = new MapInfo(r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString(), r.ReadString());
                s.Modifiers = new string[r.ReadInt32()];
                for (int i = 0; i < s.Modifiers.Length; i++) s.Modifiers[i] = r.ReadString();

                s.Scores = new ScoreRow[r.ReadInt32()];
                for (int i = 0; i < s.Scores.Length; i++)
                {
                    s.Scores[i] = new ScoreRow
                    {
                        Player = r.ReadInt32(), Map = r.ReadInt32(), Mods = r.ReadInt32(),
                        Stars = r.ReadSingle(), Accuracy = r.ReadSingle(), PredictedAcc = r.ReadSingle(),
                        Pp = r.ReadSingle(), AccPp = r.ReadSingle(), PassPp = r.ReadSingle(), TechPp = r.ReadSingle(), Weight = r.ReadSingle(),
                        MapRank = r.ReadInt32(), Timepost = r.ReadInt32(), FC = r.ReadBoolean(),
                    };
                }
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
