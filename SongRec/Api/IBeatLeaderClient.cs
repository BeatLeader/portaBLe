using beatleader_songrec.Models;

namespace beatleader_songrec.Api;

/// <summary>
/// Abstraction over the BeatLeader data source, allowing consumers (e.g.
/// <see cref="Core"/> pipeline steps) to be unit tested against mocked/fake implementations.
/// portaBLe implements this against its own local EF Core database instead of the BeatLeader
/// HTTP API.
/// </summary>
public interface IBeatLeaderClient
{
    /// <summary>
    /// Gets a player's top ranked scores (ordered by PP, as returned by the API), across
    /// as many pages as requested.
    /// </summary>
    Task<IReadOnlyList<Score>> GetPlayerTopScoresAsync(
        string playerId,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the top scorers for a specific leaderboard.
    /// </summary>
    Task<IReadOnlyList<Score>> GetLeaderboardScoresAsync(
        string beatSaverId,
        int reuploadCount,
        Difficulty difficulty,
        Characteristic characteristic,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the top scorers for a specific leaderboard, identified directly by its
    /// BeatLeader leaderboard id (e.g. as returned by <see cref="Score.Song"/>'s
    /// <c>LeaderboardId</c>).
    /// </summary>
    /// <param name="leaderboardId">The raw BeatLeader leaderboard id.</param>
    /// <param name="count">Maximum number of top scorers to return.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<IReadOnlyList<Score>> GetLeaderboardScoresByIdAsync(
        string leaderboardId,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the scorers of a leaderboard whose PP is nearest <paramref name="pp"/> (by PP ratio, above
    /// or below), i.e. the players at about the same skill on that map, ordered by PP descending.
    /// </summary>
    /// <param name="leaderboardId">The raw BeatLeader leaderboard id.</param>
    /// <param name="pp">The PP to look around (typically the target player's own score there).</param>
    /// <param name="count">Maximum number of scorers to return.</param>
    /// <param name="excludePlayerId">A player left out of the results (typically the target player), or null.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
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

    /// <summary>
    /// Gets every currently ranked map, paging through the BeatLeader ranked map list until
    /// all pages have been fetched.
    /// </summary>
    Task<IReadOnlyList<Song>> GetAllRankedMapsAsync(CancellationToken cancellationToken = default);
}
