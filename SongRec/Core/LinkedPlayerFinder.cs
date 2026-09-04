using System.Text.Json;
using beatleader_songrec.Api;
using beatleader_songrec.Models;

namespace beatleader_songrec.Core;

/// <summary>
/// A single candidate song entry produced by <see cref="ILinkedPlayerFinder"/>: the linked
/// player who has this song, paired with their score on it, and the SmartSongSuggest-style
/// link distance between the seed song and this candidate (smaller = stronger link).
/// </summary>
public sealed record CandidateEntry(LinkedPlayer LinkedPlayer, Score Score, Song OriginSong, double Distance = 0d);

/// <summary>
/// Finds players linked to a target player via shared top plays, and aggregates the
/// candidate song pool from those linked players' own top scores.
/// </summary>
public interface ILinkedPlayerFinder
{
    /// <summary>
    /// Runs the player-linking pipeline:
    /// 1. Takes the target player's top <paramref name="seedCount"/> ranked scores as seeds.
    /// 2. For each seed song, fetches its leaderboard and collects the top
    ///    <paramref name="linkedPlayerScoreCount"/> scorers as linked players.
    /// 3. For each linked player, fetches their own top <paramref name="linkedPlayerScoreCount"/>
    ///    scores and aggregates all songs into a candidate pool.
    /// </summary>
    /// <param name="targetPlayerId">The BeatLeader id of the target player.</param>
    /// <param name="seedCount">How many of the target player's top scores to use as seeds (default 50).</param>
    /// <param name="linkedPlayerScoreCount">
    /// How many top scores to consider per song leaderboard / per linked player (default 20).
    /// </param>
    /// <param name="onProgress">Optional callback invoked with human-readable progress messages.</param>
    /// <param name="seedScores">
    /// Optional pre-fetched seed scores. When supplied, the target player's top scores are not
    /// re-fetched, avoiding a redundant API call when the caller already retrieved them.
    /// </param>
    /// <param name="linkKeepPercent">
    /// Fraction (0-1) of seed-song-to-linked-player links to keep after pruning by PP-distance,
    /// mirroring SmartSongSuggest's <c>LinkKeepPercent</c>. Links whose distance is largest are
    /// discarded first; defaults to 0.5 (keep the closest half).
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// A dictionary from candidate <see cref="Song"/> (speed-modifier-aware identity) to the
    /// list of linked players who have a score on it, each paired with their score.
    /// </returns>
    Task<IReadOnlyDictionary<Song, List<CandidateEntry>>> FindCandidateSongsAsync(
        string targetPlayerId,
        int seedCount = 50,
        int linkedPlayerScoreCount = 20,
        Action<string>? onProgress = null,
        IReadOnlyList<Score>? seedScores = null,
        double linkKeepPercent = 0.5,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ILinkedPlayerFinder"/>
public sealed class LinkedPlayerFinder : ILinkedPlayerFinder
{
    private readonly IBeatLeaderClient _client;

    public LinkedPlayerFinder(IBeatLeaderClient client)
    {
        _client = client;
    }

    public async Task<IReadOnlyDictionary<Song, List<CandidateEntry>>> FindCandidateSongsAsync(
        string targetPlayerId,
        int seedCount = 50,
        int linkedPlayerScoreCount = 20,
        Action<string>? onProgress = null,
        IReadOnlyList<Score>? seedScores = null,
        double linkKeepPercent = 0.5,
        CancellationToken cancellationToken = default)
    {
        // Step 1: seed songs from the target player's top plays.
        if (seedScores is null)
        {
            onProgress?.Invoke($"Fetching top {seedCount} seed scores for player {targetPlayerId}...");
            seedScores = await _client.GetPlayerTopScoresAsync(targetPlayerId, seedCount, cancellationToken)
                .ConfigureAwait(false);
            onProgress?.Invoke($"Fetched {seedScores.Count} seed scores.");
        }

        var seedPpBySong = seedScores
            .GroupBy(s => s.GetEffectiveSong())
            .ToDictionary(g => g.Key, g => g.Max(s => s.Pp));

        // Step 2: for each seed song, fetch its leaderboard's top scorers and treat them as
        // linked players, tracking which seed song(s) established each link.
        var linkedPlayersBySeedSong = new Dictionary<string, LinkedPlayer>();
        var linkedPlayerScoresOnSeed = new Dictionary<string, List<(Song SeedSong, Score Score)>>();
        var seedIndex = 0;

        foreach (var seedScore in seedScores)
        {
            seedIndex++;
            var seedSong = seedScore.GetEffectiveSong();
            onProgress?.Invoke($"[{seedIndex}/{seedScores.Count}] Fetching leaderboard for '{seedSong.Name}' ({seedSong.LeaderboardId})...");
            IReadOnlyList<Score> leaderboardScores;
            try
            {
                leaderboardScores = await _client.GetLeaderboardScoresByIdAsync(
                    seedSong.LeaderboardId,
                    linkedPlayerScoreCount,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                onProgress?.Invoke($"  Skipping '{seedSong.Name}': failed to fetch leaderboard ({ex.Message}).");
                continue;
            }

            foreach (var leaderboardScore in leaderboardScores)
            {
                if (leaderboardScore.PlayerId == targetPlayerId)
                {
                    // Skip the target player themselves.
                    continue;
                }

                var player = leaderboardScore.Player ?? new Player
                {
                    Id = leaderboardScore.PlayerId,
                    Name = leaderboardScore.PlayerId
                };

                if (linkedPlayersBySeedSong.TryGetValue(player.Id, out var existing))
                {
                    var mergedSongs = new List<Song>(existing.SharedSeedSongs) { seedSong };
                    linkedPlayersBySeedSong[player.Id] = new LinkedPlayer
                    {
                        Player = existing.Player,
                        SharedSeedSongs = mergedSongs
                    };
                }
                else
                {
                    linkedPlayersBySeedSong[player.Id] = new LinkedPlayer
                    {
                        Player = player,
                        SharedSeedSongs = [seedSong]
                    };
                }

                if (!linkedPlayerScoresOnSeed.TryGetValue(player.Id, out var seedHits))
                {
                    seedHits = [];
                    linkedPlayerScoresOnSeed[player.Id] = seedHits;
                }

                seedHits.Add((seedSong, leaderboardScore));
            }
        }

        onProgress?.Invoke($"Found {linkedPlayersBySeedSong.Count} linked players.");

        // Step 3: for each linked player, fetch their own top scores and build a raw link for
        // every (origin seed song -> target song) pair, mirroring SmartSongSuggest's
        // GenerateLinks: origin -> matching leaderboard player -> other top songs.
        var rawLinks = new List<(Song OriginSong, LinkedPlayer LinkedPlayer, Score TargetScore, double Distance)>();
        var linkedPlayerIndex = 0;
        var totalLinkedPlayers = linkedPlayersBySeedSong.Count;

        foreach (var (playerId, linkedPlayer) in linkedPlayersBySeedSong)
        {
            linkedPlayerIndex++;
            onProgress?.Invoke($"[{linkedPlayerIndex}/{totalLinkedPlayers}] Fetching top scores for linked player '{linkedPlayer.Player.Name}'...");
            IReadOnlyList<Score> linkedPlayerScores;
            try
            {
                linkedPlayerScores = await _client.GetPlayerTopScoresAsync(
                    linkedPlayer.Player.Id,
                    linkedPlayerScoreCount,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                onProgress?.Invoke($"  Skipping linked player '{linkedPlayer.Player.Name}': {ex.Message}.");
                continue;
            }

            if (!linkedPlayerScoresOnSeed.TryGetValue(playerId, out var seedHits))
            {
                continue;
            }

            // Origin -> target links: for every seed song this player shares with the target
            // player, link it to every *other* top score this player has (their "target" songs).
            foreach (var (originSong, originScore) in seedHits)
            {
                var originPp = seedPpBySong.GetValueOrDefault(originSong, originScore.Pp);

                foreach (var targetScore in linkedPlayerScores)
                {
                    var targetSong = targetScore.GetEffectiveSong();

                    // Skip the self-link (linking a song back to itself).
                    if (targetSong.Equals(originSong))
                    {
                        continue;
                    }

                    // SmartSongSuggest distance: |(originSong.pp / playerScore)^2.5 - 1|,
                    // where playerScore falls back to the origin leaderboard score's PP when
                    // the seed itself has no recorded PP (e.g. an unranked/liked seed).
                    var playerScoreValue = originPp != 0 ? originPp : originScore.Pp;
                    var distance = playerScoreValue != 0
                        ? Math.Abs(Math.Pow(originScore.Pp / playerScoreValue, 2.5) - 1)
                        : 0d;

                    rawLinks.Add((originSong, linkedPlayer, targetScore, distance));
                }
            }
        }

        onProgress?.Invoke($"Generated {rawLinks.Count} raw song links.");

        // Step 4: prune links by distance, keeping only the closest `linkKeepPercent` fraction,
        // mirroring SmartSongSuggest's LinkKeepPercent-based pruning in GenerateLinks.
        var keepFraction = Math.Clamp(linkKeepPercent, 0d, 1d);
        var keepCount = (int)Math.Ceiling(keepFraction * rawLinks.Count);
        var prunedLinks = rawLinks
            .OrderBy(l => l.Distance)
            .Take(keepCount)
            .ToList();

        onProgress?.Invoke($"Kept {prunedLinks.Count} links after distance-based pruning ({keepFraction:P0}).");

        // Step 5: aggregate the pruned links into the candidate song pool. Speed-modified plays
        // are naturally treated as distinct song entities because Song equality incorporates
        // the modifier.
        var candidates = new Dictionary<Song, List<CandidateEntry>>();

        foreach (var (originSong, linkedPlayer, targetScore, distance) in prunedLinks)
        {
            var song = targetScore.GetEffectiveSong();

            if (!candidates.TryGetValue(song, out var entries))
            {
                entries = [];
                candidates[song] = entries;
            }

            entries.Add(new CandidateEntry(linkedPlayer, targetScore, originSong, distance));
        }

        onProgress?.Invoke($"Aggregated {candidates.Count} candidate songs.");

        return candidates;
    }
}
