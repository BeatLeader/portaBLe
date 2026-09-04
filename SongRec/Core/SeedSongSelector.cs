using beatleader_songrec.Api;
using beatleader_songrec.Models;

namespace beatleader_songrec.Core;

/// <summary>
/// Selects the target player's seed songs using a SmartSongSuggest-style "comparative best"
/// approach: rather than simply taking the player's raw top-N plays by PP, this drops the
/// weakest quarter of their plays, then reorders the remaining candidates by how competitive
/// each score is on its own leaderboard (i.e. how many other scorers on that leaderboard beat
/// the player's PP), favoring songs where the player is comparatively strong.
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
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<IReadOnlyList<Score>> SelectSeedSongsAsync(
        string targetPlayerId,
        int seedCount = 50,
        int extraSongs = 25,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISeedSongSelector"/>
public sealed class SeedSongSelector : ISeedSongSelector
{
    /// <summary>Fraction of the player's worst plays (by PP) dropped before comparative-best selection.</summary>
    private const double WorstPlaysDropFraction = 0.25;

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
        CancellationToken cancellationToken = default)
    {
        var poolTarget = seedCount + Math.Max(0, extraSongs);

        onProgress?.Invoke($"Fetching played scores for player {targetPlayerId} to build the seed candidate pool...");

        // The API returns scores ordered by PP descending; fetch a generous multiple of the
        // pool target so dropping the worst quarter still leaves enough candidates.
        var played = await _client.GetPlayerTopScoresAsync(targetPlayerId, Math.Max(poolTarget * 2, 1), cancellationToken)
            .ConfigureAwait(false);

        if (played.Count == 0)
        {
            return [];
        }

        var orderedByPp = played.OrderByDescending(s => s.Pp).ToList();

        // Drop the worst 25% of plays (rounded down, keeping at least 1) so weak scores don't
        // drag down the seed pool, then cap to the evaluation pool size.
        var keepCount = Math.Max(1, (int)(orderedByPp.Count * (1.0 - WorstPlaysDropFraction)));
        var candidatePool = orderedByPp.Take(Math.Min(keepCount, poolTarget)).ToList();

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
