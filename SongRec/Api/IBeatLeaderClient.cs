using beatleader_songrec.Models;

namespace beatleader_songrec.Api;

/// <summary>
/// Abstraction over the BeatLeader data source, so the <see cref="Core"/> pipeline steps can run against
/// fakes. portaBLe implements it against its local database instead of the BeatLeader HTTP API.
/// </summary>
public interface IBeatLeaderClient
{
    /// <summary>Gets a player's top <paramref name="count"/> ranked scores, best PP first.</summary>
    Task<IReadOnlyList<Score>> GetPlayerTopScoresAsync(
        string playerId,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets several players' top ranked scores at once (each player's ordered by PP), as
    /// <see cref="GetPlayerTopScoresAsync"/> for each: every player asked for is a key, with no
    /// scores when they have none.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<Score>>> GetPlayersTopScoresAsync(
        IReadOnlyCollection<string> playerIds,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the top scorers of a leaderboard.</summary>
    Task<IReadOnlyList<Score>> GetLeaderboardScoresAsync(
        string beatSaverId,
        int reuploadCount,
        Difficulty difficulty,
        Characteristic characteristic,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the top <paramref name="count"/> scorers of a leaderboard by its BeatLeader leaderboard id.</summary>
    Task<IReadOnlyList<Score>> GetLeaderboardScoresByIdAsync(
        string leaderboardId,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the scorers of a leaderboard whose PP is nearest <paramref name="pp"/> (by PP ratio, above
    /// or below), i.e. the players at about the same skill on that map, ordered by PP descending.
    /// </summary>
    /// <param name="pp">The PP to look around (typically the target player's own score there).</param>
    /// <param name="excludePlayerId">A player left out of the results (typically the target player), or null.</param>
    Task<IReadOnlyList<Score>> GetLeaderboardScoresNearPpAsync(
        string leaderboardId,
        double pp,
        int count,
        string? excludePlayerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the scorers of a leaderboard whose overall PP (their profile's, not their score's) is nearest
    /// <paramref name="playerPp"/>, ordered by score PP descending: players at about a given overall skill
    /// who played the map. For maps the target player hasn't played, where there is no score to compare.
    /// </summary>
    Task<IReadOnlyList<Score>> GetLeaderboardScoresNearPlayerPpAsync(
        string leaderboardId,
        double playerPp,
        int count,
        string? excludePlayerId,
        CancellationToken cancellationToken = default);

    /// <summary>The ranked maps with these leaderboard ids (unknown or unranked ids are left out).</summary>
    Task<IReadOnlyList<Song>> GetRankedMapsAsync(IReadOnlyCollection<string> leaderboardIds, CancellationToken cancellationToken = default);

    /// <summary>Gets every currently ranked map.</summary>
    Task<IReadOnlyList<Song>> GetAllRankedMapsAsync(CancellationToken cancellationToken = default);
}
