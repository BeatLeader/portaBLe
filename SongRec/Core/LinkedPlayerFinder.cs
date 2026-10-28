using System.Text.Json;
using beatleader_songrec.Api;
using beatleader_songrec.Models;

namespace beatleader_songrec.Core;

/// <summary>
/// A single candidate song entry produced by <see cref="ILinkedPlayerFinder"/>: the linked
/// player who has this song, paired with their score on it, and the seed song that linked them.
/// </summary>
public sealed record CandidateEntry(LinkedPlayer LinkedPlayer, Score Score, Song OriginSong);

/// <summary>
/// Finds players linked to a target player via shared top plays, and aggregates the
/// candidate song pool from those linked players' own top scores.
/// </summary>
public interface ILinkedPlayerFinder
{
    /// <summary>
    /// Runs the player-linking pipeline:
    /// 1. Takes the target player's top <paramref name="seedCount"/> ranked scores as seeds.
    /// 2. For each seed song, collects the <paramref name="peersPerSeed"/> scorers of its
    ///    leaderboard whose PP there is nearest the target player's own (above or below) as linked
    ///    players: peers at about the same skill, so their top plays are maps within the target
    ///    player's reach. (A leaderboard's top scorers, as SmartSongSuggest uses, mostly play maps
    ///    well above the target player's level.)
    /// 3. For each linked player, fetches their own top <paramref name="playsPerPeer"/>
    ///    scores and aggregates all songs into a candidate pool.
    /// </summary>
    /// <param name="seedCount">How many of the target player's top scores to use as seeds.</param>
    /// <param name="peersPerSeed">How many peers each seed song links.</param>
    /// <param name="playsPerPeer">How many of each linked player's top plays become candidates.</param>
    /// <param name="seedScores">Pre-fetched seed scores; null to fetch the target player's top scores.</param>
    /// <param name="peerPpScale">
    /// The peers' PP looked for, as a multiple of the target player's on each seed: below 1 for weaker
    /// players, whose top plays are easier maps; 1 for the player's own skill.
    /// </param>
    /// <param name="extraSeedSongs">
    /// Seed songs the target player has no score on (maps they added): their peers are the scorers whose
    /// overall PP is nearest <paramref name="targetPlayerPp"/> (times <paramref name="peerPpScale"/>).
    /// </param>
    /// <param name="targetPlayerPp">The target player's overall PP, for <paramref name="extraSeedSongs"/>.</param>
    /// <returns>
    /// A dictionary from candidate <see cref="Song"/> (speed-modifier-aware identity) to the
    /// list of linked players who have a score on it, each paired with their score.
    /// </returns>
    Task<IReadOnlyDictionary<Song, List<CandidateEntry>>> FindCandidateSongsAsync(
        string targetPlayerId,
        int seedCount = 50,
        int peersPerSeed = 20,
        int playsPerPeer = 20,
        Action<string>? onProgress = null,
        IReadOnlyList<Score>? seedScores = null,
        double peerPpScale = 1,
        IReadOnlyList<Song>? extraSeedSongs = null,
        double targetPlayerPp = 0,
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
        int peersPerSeed = 20,
        int playsPerPeer = 20,
        Action<string>? onProgress = null,
        IReadOnlyList<Score>? seedScores = null,
        double peerPpScale = 1,
        IReadOnlyList<Song>? extraSeedSongs = null,
        double targetPlayerPp = 0,
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

        // Step 2: each seed song's peers become linked players, tracking which seeds linked them. Seeds the
        // player has a score on link the scorers nearest that score; added seeds (no Pp) the scorers nearest
        // the player's overall PP.
        var linkedPlayers = new Dictionary<string, (Player Player, List<Song> SeedSongs)>();
        var seeds = seedScores.Select(s => (Song: s.GetEffectiveSong(), Pp: (double?)s.Pp))
            .Concat((extraSeedSongs ?? []).Select(song => (Song: song, Pp: (double?)null)))
            .ToList();
        var seedIndex = 0;

        foreach (var (seedSong, seedPp) in seeds)
        {
            seedIndex++;
            onProgress?.Invoke($"[{seedIndex}/{seeds.Count}] Fetching leaderboard for '{seedSong.Name}' ({seedSong.LeaderboardId})...");
            IReadOnlyList<Score> leaderboardScores;
            try
            {
                leaderboardScores = seedPp is double pp
                    ? await _client.GetLeaderboardScoresNearPpAsync(
                        seedSong.LeaderboardId,
                        pp * peerPpScale,
                        peersPerSeed,
                        targetPlayerId,
                        cancellationToken).ConfigureAwait(false)
                    : await _client.GetLeaderboardScoresNearPlayerPpAsync(
                        seedSong.LeaderboardId,
                        targetPlayerPp * peerPpScale,
                        peersPerSeed,
                        targetPlayerId,
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
                    continue;
                }

                var player = leaderboardScore.Player ?? new Player
                {
                    Id = leaderboardScore.PlayerId,
                    Name = leaderboardScore.PlayerId
                };

                if (linkedPlayers.TryGetValue(player.Id, out var existing))
                {
                    existing.SeedSongs.Add(seedSong);
                }
                else
                {
                    linkedPlayers[player.Id] = (player, [seedSong]);
                }
            }
        }

        var linkedPlayersBySeedSong = linkedPlayers.ToDictionary(
            pair => pair.Key,
            pair => new LinkedPlayer { Player = pair.Value.Player, SharedSeedSongs = pair.Value.SeedSongs });
        onProgress?.Invoke($"Found {linkedPlayersBySeedSong.Count} linked players.");

        // Step 3: fetch every linked player's top scores in one query and link each seed song they share with
        // the target player to each of their *other* top scores (origin -> linked player -> target song, as
        // SmartSongSuggest's GenerateLinks). No closest-link filtering: the peers are already the scorers
        // nearest the player's pp. Speed-modified plays are distinct candidates (Song equality includes the modifier).
        onProgress?.Invoke($"Fetching the top {playsPerPeer} scores of {linkedPlayersBySeedSong.Count} linked players...");
        var topScoresByPlayer = await _client.GetPlayersTopScoresAsync(linkedPlayersBySeedSong.Keys, playsPerPeer, cancellationToken)
            .ConfigureAwait(false);

        var candidates = new Dictionary<Song, List<CandidateEntry>>();

        foreach (var (playerId, linkedPlayer) in linkedPlayersBySeedSong)
        {
            var linkedPlayerScores = topScoresByPlayer.GetValueOrDefault(playerId) ?? [];

            foreach (var originSong in linkedPlayer.SharedSeedSongs)
            {
                foreach (var targetScore in linkedPlayerScores)
                {
                    // Skip the self-link (linking a song back to itself), whatever speed modifier
                    // either player used on it.
                    if (targetScore.Song.LeaderboardId == originSong.LeaderboardId)
                    {
                        continue;
                    }

                    var song = targetScore.GetEffectiveSong();

                    if (!candidates.TryGetValue(song, out var entries))
                    {
                        entries = [];
                        candidates[song] = entries;
                    }

                    entries.Add(new CandidateEntry(linkedPlayer, targetScore, originSong));
                }
            }
        }

        onProgress?.Invoke($"Aggregated {candidates.Count} candidate songs.");

        return candidates;
    }
}
