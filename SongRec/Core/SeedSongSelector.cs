using beatleader_songrec.Api;
using beatleader_songrec.Models;

namespace beatleader_songrec.Core;

/// <summary>
/// Selects the target player's seed songs SmartSongSuggest-style ("comparative best"): their top
/// plays reordered by how many scorers on each leaderboard beat the player's PP, favoring songs
/// where the player is comparatively strong. SmartSongSuggest's dropping of the weakest quarter
/// of plays is omitted (it didn't help).
/// </summary>
public interface ISeedSongSelector
{
    /// <summary>
    /// Selects up to <paramref name="seedCount"/> seed scores for <paramref name="targetPlayerId"/>.
    /// </summary>
    /// <param name="extraSongs">
    /// How many additional candidates beyond <paramref name="seedCount"/> to pull into the
    /// comparative-best evaluation pool before reducing back down to <paramref name="seedCount"/>.
    /// </param>
    /// <param name="playedScores">
    /// The target player's scores when the caller already has them (at least their top
    /// <paramref name="seedCount"/> + <paramref name="extraSongs"/> by PP), else null to fetch them.
    /// </param>
    Task<IReadOnlyList<Score>> SelectSeedSongsAsync(
        string targetPlayerId,
        int seedCount = 50,
        int extraSongs = 25,
        Action<string>? onProgress = null,
        IReadOnlyList<Score>? playedScores = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISeedSongSelector"/>
public sealed class SeedSongSelector : ISeedSongSelector
{
    /// <summary>How many top scorers to sample per leaderboard when computing comparative standing.</summary>
    private const int ComparativeSampleSize = 100;

    private readonly IBeatLeaderClient _client;

    public SeedSongSelector(IBeatLeaderClient client)
    {
        _client = client;
    }

    public async Task<IReadOnlyList<Score>> SelectSeedSongsAsync(
        string targetPlayerId,
        int seedCount = 50,
        int extraSongs = 25,
        Action<string>? onProgress = null,
        IReadOnlyList<Score>? playedScores = null,
        CancellationToken cancellationToken = default)
    {
        var poolTarget = seedCount + Math.Max(0, extraSongs);

        var played = playedScores;
        if (played is null)
        {
            onProgress?.Invoke($"Fetching played scores for player {targetPlayerId} to build the seed candidate pool...");

            // The API returns scores ordered by PP descending
            played = await _client.GetPlayerTopScoresAsync(targetPlayerId, Math.Max(poolTarget, 1), cancellationToken)
                .ConfigureAwait(false);
        }

        if (played.Count == 0)
        {
            return [];
        }

        var candidatePool = played.OrderByDescending(s => s.Pp).Take(poolTarget).ToList();

        onProgress?.Invoke($"Evaluating comparative standing for {candidatePool.Count} candidate seed songs...");

        // Comparative standing: how many scorers beat the player's PP on each leaderboard (fewer = stronger play)
        var comparativeIndex = new Dictionary<Score, int>();
        foreach (var score in candidatePool)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var song = score.GetEffectiveSong();
            try
            {
                var leaderboardScores = await _client.GetLeaderboardScoresByIdAsync(
                    song.LeaderboardId,
                    ComparativeSampleSize,
                    cancellationToken).ConfigureAwait(false);

                comparativeIndex[score] = leaderboardScores.Count(s => s.Pp > score.Pp);
            }
            catch (Exception)
            {
                // Unfetchable leaderboard: worst standing, so the song sinks instead of aborting
                comparativeIndex[score] = int.MaxValue;
            }
        }

        var selected = candidatePool
            .OrderBy(s => comparativeIndex[s])
            .ThenByDescending(s => s.Pp)
            .Take(seedCount)
            .OrderByDescending(s => s.Pp)
            .ToList();

        onProgress?.Invoke($"Selected {selected.Count} seed songs via comparative-best ordering.");

        return selected;
    }
}
