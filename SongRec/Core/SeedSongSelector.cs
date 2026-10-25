using beatleader_songrec.Api;
using beatleader_songrec.Models;

namespace beatleader_songrec.Core;

/// <summary>
/// Selects the target player's seed songs using a SmartSongSuggest-style "comparative best"
/// approach: rather than simply taking the player's raw top-N plays by PP, this reorders their
/// top plays by how competitive each score is on its own leaderboard (i.e. how many other scorers
/// on that leaderboard beat the player's PP), favoring songs where the player is comparatively
/// strong. (SmartSongSuggest also drops the weakest quarter of the plays first: that only did
/// anything for players with fewer than 100 plays, and there it found slightly fewer of the maps
/// they went on to play, so it was dropped.)
/// </summary>
public interface ISeedSongSelector
{
    /// <summary>
    /// Selects up to <paramref name="seedCount"/> seed scores for <paramref name="targetPlayerId"/>.
    /// </summary>
    /// <param name="targetPlayerId">The BeatLeader id of the target player.</param>
    /// <param name="seedCount">How many seed scores to return (default 50).</param>
    /// <param name="extraSongs">
    /// How many additional candidates beyond <paramref name="seedCount"/> to pull into the
    /// comparative-best evaluation pool before reducing back down to <paramref name="seedCount"/>.
    /// </param>
    /// <param name="onProgress">Optional callback invoked with human-readable progress messages.</param>
    /// <param name="playedScores">
    /// The target player's scores when the caller already has them (at least their top
    /// <paramref name="seedCount"/> + <paramref name="extraSongs"/> by PP), else null to fetch them.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
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

        // Comparative-best: for each remaining candidate, look at its own leaderboard to see
        // how many scorers beat the player's PP there - a lower count means the play is
        // comparatively stronger relative to that leaderboard's population.
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
                // If the leaderboard can't be fetched, fall back to worst-case standing so the
                // song sinks to the bottom of the comparative ordering rather than aborting.
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
