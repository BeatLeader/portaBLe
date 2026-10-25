using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using beatleader_songrec.Models;
using DbContextType = Microsoft.EntityFrameworkCore.DbContext;

namespace beatleader_songrec.Api;

/// <summary>
/// <see cref="IBeatLeaderClient"/> implementation backed directly by portaBLe's local SQLite database instead of the
/// BeatLeader HTTP API. Consumes whichever <see cref="DbContext"/> the caller is currently working with (portaBLe
/// supports multiple selectable database files), so this class is constructed per request rather than resolved from DI.
///
/// Built for speed, as a run asks for about a thousand score lists: plain SQL reading only the score columns
/// (the songs come from the file's <see cref="LeaderboardCatalog"/>, not a join per row), several players' top plays
/// in one query (<see cref="GetPlayersTopScoresAsync"/>), and each leaderboard read once and kept for the client's
/// life: the seed selection and the peer search ask for the same ones, and without an index on (LeaderboardId, Pp)
/// SQLite reads a leaderboard's every score for each of them anyway. (Such indexes made those queries 10-20x faster
/// but made rewriting every score's PP 20x slower, 43 s against 2 s, which the rating pipeline does on each refresh.)
/// Scores come without their <see cref="Score.Player"/>.
/// </summary>
public sealed class DbBeatLeaderClient : IBeatLeaderClient
{
    private const string ScoreColumns = "s.Id, s.PlayerId, s.LeaderboardId, s.Pp, s.Accuracy, s.Rank, s.Modifiers, s.Timepost";

    private readonly DbContextType _dbContext;
    private LeaderboardCatalog? _catalog;

    // Each leaderboard's scores, best PP first (then by id), once read. A page's run reads up to about 80 (the seed
    // pool's 75 and the maps added); a client kept longer (songreceval's) drops them all past MaxKeptLeaderboards
    private const int MaxKeptLeaderboards = 256;
    private readonly Dictionary<string, ScoreRow[]> _leaderboardRows = new(StringComparer.Ordinal);

    public DbBeatLeaderClient(DbContextType dbContext)
    {
        _dbContext = dbContext;
    }

    // A score row: the columns the pipeline uses (the leaderboard's come from the catalog)
    private readonly record struct ScoreRow(int Id, string PlayerId, string LeaderboardId, float Pp, float Accuracy, int Rank, string? Modifiers, int Timepost);

    public async Task<IReadOnlyList<Score>> GetPlayerTopScoresAsync(
        string playerId,
        int count,
        CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync(
            $"SELECT {ScoreColumns} FROM Scores s WHERE s.PlayerId = $player ORDER BY s.Pp DESC, s.Id LIMIT $count",
            [("$player", playerId), ("$count", count)],
            cancellationToken).ConfigureAwait(false);
        return await ToModelsAsync(rows, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Score>>> GetPlayersTopScoresAsync(
        IReadOnlyCollection<string> playerIds,
        int count,
        CancellationToken cancellationToken = default)
    {
        // Each player's top `count` through the PlayerId index, all in one statement
        var rows = await QueryAsync(
            $"SELECT {ScoreColumns} FROM json_each($players) p JOIN Scores s ON s.Id IN " +
            "(SELECT t.Id FROM Scores t WHERE t.PlayerId = p.value ORDER BY t.Pp DESC, t.Id LIMIT $count)",
            [("$players", JsonSerializer.Serialize(playerIds.Distinct())), ("$count", count)],
            cancellationToken).ConfigureAwait(false);
        var catalog = await CatalogAsync().ConfigureAwait(false);
        var byPlayer = rows
            .GroupBy(r => r.PlayerId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<Score>)g.OrderByDescending(r => r.Pp).ThenBy(r => r.Id).Select(r => ToModel(r, catalog)).ToList(),
                StringComparer.Ordinal);
        foreach (var id in playerIds)
        {
            byPlayer.TryAdd(id, []);
        }
        return byPlayer;
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
        var rows = await LeaderboardRowsAsync(leaderboardId, cancellationToken).ConfigureAwait(false);
        return await ToModelsAsync(rows.Take(count), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Score>> GetLeaderboardScoresNearPpAsync(
        string leaderboardId,
        double pp,
        int count,
        string? excludePlayerId,
        CancellationToken cancellationToken = default)
    {
        var rows = await LeaderboardRowsAsync(leaderboardId, cancellationToken).ConfigureAwait(false);
        if (pp <= 0)
        {
            return await ToModelsAsync(rows.Take(count + 1).Where(r => r.PlayerId != excludePlayerId).Take(count), cancellationToken).ConfigureAwait(false);
        }

        // The nearest are among the `count` just above and the `count` just below (rows are best first)
        var target = (float)pp;
        var scorers = rows.Where(r => r.Pp > 0 && r.PlayerId != excludePlayerId).ToList();
        var split = scorers.FindIndex(r => r.Pp < target);
        if (split < 0) split = scorers.Count;
        var above = scorers.Take(split).TakeLast(count);
        var below = scorers.Skip(split).Take(count);

        return await ToModelsAsync(above.Concat(below)
            .OrderBy(r => Math.Abs(Math.Log(r.Pp / pp)))
            .ThenBy(r => r.Id)
            .Take(count)
            .OrderByDescending(r => r.Pp), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Score>> GetLeaderboardScoresNearPlayerPpAsync(
        string leaderboardId,
        double playerPp,
        int count,
        string? excludePlayerId,
        CancellationToken cancellationToken = default)
    {
        // The scorers' profile PP, for the maps players add (a few per run): not kept
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {ScoreColumns}, p.Pp FROM Scores s JOIN Players p ON p.Id = s.PlayerId " +
            "WHERE s.LeaderboardId = $leaderboard AND s.Pp > 0 AND p.Pp > 0 AND ($exclude IS NULL OR s.PlayerId <> $exclude)";
        AddParameter(command, "$leaderboard", leaderboardId);
        AddParameter(command, "$exclude", excludePlayerId);
        var scorers = new List<(ScoreRow Row, float PlayerPp)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                scorers.Add((ReadRow(reader), (float)reader.GetDouble(8)));
            }
        }

        // The nearest are among the `count` just above and the `count` just below
        var target = (float)playerPp;
        var above = scorers.Where(s => s.PlayerPp >= target).OrderBy(s => s.PlayerPp).ThenBy(s => s.Row.Id).Take(count);
        var below = scorers.Where(s => s.PlayerPp < target).OrderByDescending(s => s.PlayerPp).ThenBy(s => s.Row.Id).Take(count);

        return await ToModelsAsync(above.Concat(below)
            .OrderBy(s => playerPp > 0 ? Math.Abs(Math.Log(s.PlayerPp / playerPp)) : 0)
            .ThenBy(s => s.Row.Id)
            .Take(count)
            .Select(s => s.Row)
            .OrderByDescending(r => r.Pp), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Song>> GetRankedMapsAsync(IReadOnlyCollection<string> leaderboardIds, CancellationToken cancellationToken = default)
    {
        if (leaderboardIds.Count == 0)
        {
            return [];
        }

        var catalog = await CatalogAsync().ConfigureAwait(false);
        var ids = leaderboardIds.ToHashSet(StringComparer.Ordinal);
        return catalog.Ranked.Where(song => ids.Contains(song.LeaderboardId)).ToList();
    }

    public async Task<IReadOnlyList<Song>> GetAllRankedMapsAsync(CancellationToken cancellationToken = default) =>
        (await CatalogAsync().ConfigureAwait(false)).Ranked;

    private async Task<LeaderboardCatalog> CatalogAsync() =>
        _catalog ??= await LeaderboardCatalog.ForAsync(_dbContext).ConfigureAwait(false);

    private async Task<ScoreRow[]> LeaderboardRowsAsync(string leaderboardId, CancellationToken cancellationToken)
    {
        if (!_leaderboardRows.TryGetValue(leaderboardId, out var rows))
        {
            // Sorted here: SQLite would sort them too (no index on PP), after reading them all just the same
            rows = (await QueryAsync(
                    $"SELECT {ScoreColumns} FROM Scores s WHERE s.LeaderboardId = $leaderboard",
                    [("$leaderboard", leaderboardId)],
                    cancellationToken,
                    leaderboardId).ConfigureAwait(false))
                .OrderByDescending(r => r.Pp)
                .ThenBy(r => r.Id)
                .ToArray();
            if (_leaderboardRows.Count >= MaxKeptLeaderboards)
            {
                _leaderboardRows.Clear();
            }
            _leaderboardRows[leaderboardId] = rows;
        }
        return rows;
    }

    // knownLeaderboardId: every row's, so not read thousands of times
    private async Task<List<ScoreRow>> QueryAsync(string sql, (string Name, object? Value)[] parameters, CancellationToken cancellationToken, string? knownLeaderboardId = null)
    {
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        var rows = new List<ScoreRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadRow(reader, knownLeaderboardId));
        }
        return rows;
    }

    // The columns of ScoreColumns, in order
    private static ScoreRow ReadRow(DbDataReader reader, string? knownLeaderboardId = null) => new(
        reader.GetInt32(0),
        reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
        knownLeaderboardId ?? (reader.IsDBNull(2) ? string.Empty : reader.GetString(2)),
        (float)reader.GetDouble(3),
        (float)reader.GetDouble(4),
        reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetInt32(7));

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    // Opened through the context, which closes it when it's disposed. Memory-mapped: SQLite reads the file's pages in
    // place instead of copying each one into its own cache, which halved the queries' time (the pages are the
    // system's file cache, shared by every connection, so no memory per connection)
    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await _dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA mmap_size = {MemoryMapBytes}";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return connection;
    }

    // Larger than the databases (about 0.8 GB): address space only
    private const long MemoryMapBytes = 4L << 30;

    private async Task<IReadOnlyList<Score>> ToModelsAsync(IEnumerable<ScoreRow> rows, CancellationToken cancellationToken)
    {
        var catalog = await CatalogAsync().ConfigureAwait(false);
        return rows.Select(r => ToModel(r, catalog)).ToList();
    }

    private static Score ToModel(ScoreRow row, LeaderboardCatalog catalog) => new()
    {
        Id = row.Id,
        PlayerId = row.PlayerId,
        Song = catalog.Song(row.LeaderboardId, SpeedModifierExtensions.ParseSpeedModifier(row.Modifiers)),
        Accuracy = row.Accuracy,
        Pp = row.Pp,
        Rank = row.Rank,
        Modifiers = row.Modifiers,
        SetAt = row.Timepost > 0 ? DateTimeOffset.FromUnixTimeSeconds(row.Timepost) : null
    };

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
