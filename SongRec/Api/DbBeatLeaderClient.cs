using Microsoft.EntityFrameworkCore;
using beatleader_songrec.Models;
using DbContextType = Microsoft.EntityFrameworkCore.DbContext;
using DbLeaderboard = portaBLe.DB.Leaderboard;
using DbScore = portaBLe.DB.Score;

namespace beatleader_songrec.Api;

/// <summary>
/// <see cref="IBeatLeaderClient"/> implementation backed directly by portaBLe's local EF Core
/// database instead of the BeatLeader HTTP API. Consumes whichever <see cref="DbContext"/> the
/// caller is currently working with (portaBLe supports multiple selectable database files), so
/// this class is constructed per-request rather than resolved from DI.
/// </summary>
public sealed class DbBeatLeaderClient : IBeatLeaderClient
{
    private readonly DbContextType _dbContext;

    public DbBeatLeaderClient(DbContextType dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<Score>> GetPlayerTopScoresAsync(
        string playerId,
        int count,
        CancellationToken cancellationToken = default)
    {
        var scores = await _dbContext.Set<DbScore>()
            .Include(s => s.Leaderboard).ThenInclude(l => l!.ModifiersRating)
            .Include(s => s.Player)
            .Where(s => s.PlayerId == playerId)
            .OrderByDescending(s => s.Pp)
            .Take(count)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return scores.Select(s => s.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<Score>> GetLeaderboardScoresAsync(
        string beatSaverId,
        int reuploadCount,
        Difficulty difficulty,
        Characteristic characteristic,
        CancellationToken cancellationToken = default)
    {
        var leaderboardId = BuildLeaderboardId(beatSaverId, reuploadCount, difficulty, characteristic);
        return await GetLeaderboardScoresByIdAsync(leaderboardId, count: int.MaxValue, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Score>> GetLeaderboardScoresByIdAsync(
        string leaderboardId,
        int count,
        CancellationToken cancellationToken = default)
    {
        var scores = await _dbContext.Set<DbScore>()
            .Include(s => s.Leaderboard).ThenInclude(l => l!.ModifiersRating)
            .Include(s => s.Player)
            .Where(s => s.LeaderboardId == leaderboardId)
            .OrderByDescending(s => s.Pp)
            .Take(count)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return scores.Select(s => s.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<Score>> GetLeaderboardScoresNearPpAsync(
        string leaderboardId,
        double pp,
        int count,
        string? excludePlayerId,
        CancellationToken cancellationToken = default)
    {
        if (pp <= 0)
        {
            return (await GetLeaderboardScoresByIdAsync(leaderboardId, count + 1, cancellationToken).ConfigureAwait(false))
                .Where(s => s.PlayerId != excludePlayerId)
                .Take(count)
                .ToList();
        }

        // The nearest are among the `count` just above and the `count` just below
        var scores = _dbContext.Set<DbScore>()
            .Include(s => s.Leaderboard).ThenInclude(l => l!.ModifiersRating)
            .Include(s => s.Player)
            .Where(s => s.LeaderboardId == leaderboardId && s.Pp > 0 && s.PlayerId != excludePlayerId);
        var target = (float)pp;
        var above = await scores.Where(s => s.Pp >= target).OrderBy(s => s.Pp).Take(count)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var below = await scores.Where(s => s.Pp < target).OrderByDescending(s => s.Pp).Take(count)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return above.Concat(below)
            .OrderBy(s => Math.Abs(Math.Log(s.Pp / pp)))
            .ThenBy(s => s.Id)
            .Take(count)
            .OrderByDescending(s => s.Pp)
            .Select(s => s.ToModel())
            .ToList();
    }

    public async Task<IReadOnlyList<Score>> GetLeaderboardScoresNearPlayerPpAsync(
        string leaderboardId,
        double playerPp,
        int count,
        string? excludePlayerId,
        CancellationToken cancellationToken = default)
    {
        // The nearest are among the `count` just above and the `count` just below
        var scores = _dbContext.Set<DbScore>()
            .Include(s => s.Leaderboard).ThenInclude(l => l!.ModifiersRating)
            .Include(s => s.Player)
            .Where(s => s.LeaderboardId == leaderboardId && s.Pp > 0 && s.PlayerId != excludePlayerId && s.Player.Pp > 0);
        var target = (float)playerPp;
        var above = await scores.Where(s => s.Player.Pp >= target).OrderBy(s => s.Player.Pp).Take(count)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var below = await scores.Where(s => s.Player.Pp < target).OrderByDescending(s => s.Player.Pp).Take(count)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return above.Concat(below)
            .OrderBy(s => playerPp > 0 ? Math.Abs(Math.Log(s.Player.Pp / playerPp)) : 0)
            .ThenBy(s => s.Id)
            .Take(count)
            .OrderByDescending(s => s.Pp)
            .Select(s => s.ToModel())
            .ToList();
    }

    public async Task<IReadOnlyList<Song>> GetRankedMapsAsync(IReadOnlyCollection<string> leaderboardIds, CancellationToken cancellationToken = default)
    {
        if (leaderboardIds.Count == 0)
        {
            return [];
        }

        var leaderboards = await _dbContext.Set<DbLeaderboard>()
            .Include(l => l.ModifiersRating)
            .Where(l => l.Stars > 0 && leaderboardIds.Contains(l.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return leaderboards.Select(l => l.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<Song>> GetAllRankedMapsAsync(CancellationToken cancellationToken = default)
    {
        var leaderboards = await _dbContext.Set<DbLeaderboard>()
            .Include(l => l.ModifiersRating)
            .Where(l => l.Stars > 0)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return leaderboards.Select(l => l.ToModel()).ToList();
    }

    /// <summary>
    /// Builds a BeatLeader leaderboard id from its components: BeatSaver map id, reupload
    /// count (encoded as repeated 'x' characters), difficulty digit, and characteristic digit.
    /// Example: "24d9bx91" = map "24d9b", reuploaded once, ExpertPlus (9), Standard (1).
    /// </summary>
    private static string BuildLeaderboardId(string beatSaverId, int reuploadCount, Difficulty difficulty, Characteristic characteristic)
    {
        if (reuploadCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reuploadCount), reuploadCount, "Reupload count cannot be negative.");
        }

        var reuploadMarker = reuploadCount > 0 ? new string('x', reuploadCount) : string.Empty;
        return $"{beatSaverId}{reuploadMarker}{difficulty.ToLeaderboardDigit()}{characteristic.ToLeaderboardDigit()}";
    }
}
