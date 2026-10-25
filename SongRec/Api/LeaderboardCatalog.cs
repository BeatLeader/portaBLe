using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using beatleader_songrec.Models;
using DbContextType = Microsoft.EntityFrameworkCore.DbContext;
using DbLeaderboard = portaBLe.DB.Leaderboard;

namespace beatleader_songrec.Api;

/// <summary>
/// Every leaderboard of a database file as <see cref="Song"/>s, one per speed modifier, loaded once per file and
/// shared by all requests: the scores' songs come from here instead of joining each score row to its leaderboard and
/// its modifier ratings (a few thousand leaderboards, about 2 MB). Reloaded when the file changes (a refresh rewrote it).
/// </summary>
internal sealed class LeaderboardCatalog
{
    private static readonly SpeedModifier[] Modifiers = Enum.GetValues<SpeedModifier>();

    // Per file: its last write time and the catalog loading or loaded from it
    private static readonly ConcurrentDictionary<string, (DateTime WrittenAt, Lazy<Task<LeaderboardCatalog>> Catalog)> ByFile = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Song[]> _songs;

    /// <summary>The ranked leaderboards (stars above 0), unmodified, in the table's order.</summary>
    public IReadOnlyList<Song> Ranked { get; }

    private LeaderboardCatalog(List<DbLeaderboard> leaderboards)
    {
        _songs = leaderboards.ToDictionary(l => l.Id, l => Modifiers.Select(m => l.ToModel(m)).ToArray(), StringComparer.Ordinal);
        Ranked = leaderboards.Where(l => l.Stars > 0).Select(l => _songs[l.Id][(int)SpeedModifier.None]).ToList();
    }

    /// <summary>The leaderboard's song under a speed modifier (a bare one when the leaderboard isn't in the database).</summary>
    public Song Song(string leaderboardId, SpeedModifier modifier) =>
        _songs.TryGetValue(leaderboardId, out var songs)
            ? songs[(int)modifier]
            : new Song { LeaderboardId = leaderboardId, Modifier = modifier, Hash = string.Empty, Name = string.Empty };

    public bool TryGetRanked(string leaderboardId, out Song song)
    {
        song = null!;
        if (!_songs.TryGetValue(leaderboardId, out var songs) || !(songs[(int)SpeedModifier.None].Stars > 0)) return false;
        song = songs[(int)SpeedModifier.None];
        return true;
    }

    /// <summary>The catalog of the context's database file, loaded with that context the first time it's asked for.</summary>
    public static Task<LeaderboardCatalog> ForAsync(DbContextType dbContext)
    {
        var path = dbContext.Database.GetDbConnection().DataSource;
        var writtenAt = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        var entry = ByFile.AddOrUpdate(path,
            _ => (writtenAt, NewLoad(dbContext)),
            (_, existing) => existing.WrittenAt == writtenAt && !existing.Catalog.Value.IsFaulted ? existing : (writtenAt, NewLoad(dbContext)));
        return entry.Catalog.Value;
    }

    // Shared by the requests that wait for it: not cancelled with the request that started it
    private static Lazy<Task<LeaderboardCatalog>> NewLoad(DbContextType dbContext) => new(async () =>
    {
        var leaderboards = await dbContext.Set<DbLeaderboard>()
            .AsNoTracking()
            .Include(l => l.ModifiersRating)
            .ToListAsync()
            .ConfigureAwait(false);
        return new LeaderboardCatalog(leaderboards);
    });
}
