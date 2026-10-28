using beatleader_songrec.Api;
using beatleader_songrec.Models;
using beatleader_songrec.Requests;

namespace beatleader_songrec.Core;

/// <summary>
/// Settings of one <see cref="RecommendationPipeline"/> run (the Song Recommendations page's form).
/// </summary>
public sealed record RecommendationOptions
{
    /// <summary>How many of the player's plays the peers are found from (<see cref="SeedSongSelector"/>).</summary>
    public int SeedCount { get; init; } = 50;

    // Peers linked per seed song (the scorers nearest the player's pp on it), and how many of each peer's top plays
    // become candidates. More plays find more maps; more peers barely help at more queries. 40 plays is the cheapest
    // point of the plateau.
    public int PeersPerSeed { get; init; } = 20;

    public int PlaysPerPeer { get; init; } = 40;

    // Style and Easier weights (ScoringWeights). With the star window, Easier 100 finds as many maps as 50 while keeping
    // most suggestions under the player's level, so it's the default; the page's "Easier maps" slider sets it 0-100.
    public double StyleWeight { get; init; } = 100;

    public double EasierWeight { get; init; } = 100;

    /// <summary>
    /// Neutral linked players added to each candidate's Style (<see cref="SongScorer"/>): a map few peers link has its
    /// Style pulled toward the pool's mean, so one peer sharing many seeds with the player can't push their outliers to
    /// the top on their own (with 3, a map one peer links keeps a quarter of its pull). 0 for none. A safeguard: it
    /// doesn't measurably change how many good maps are found.
    /// </summary>
    public double NeutralPlayers { get; init; } = 3;

    /// <summary>
    /// Stars added to the player's level to get the level the suggestions aim at (the page's level offset slider,
    /// <see cref="MinLevelOffset"/> to <see cref="MaxLevelOffset"/>): negative for easier suggestions, positive for
    /// harder ones. 0 is where players gain (see <see cref="RecommendationPipeline.PlayerLevel"/>). The aimed level
    /// moves three things: the peers (players about that much weaker or stronger), the star window
    /// (<see cref="RecommendationPipeline.StarWindow"/>) and the Easier factor's center. Each star of offset moves the
    /// suggestions' median about 0.6-0.7 stars.
    /// </summary>
    public double LevelOffset { get; init; } = 0;

    /// <summary>
    /// The offset's range: 3 stars either way, about twice the spread of the maps that enter players' top 50
    /// around their level (middle half 1.5 stars wide), so from clearly easier to clearly harder than usual.
    /// </summary>
    public const double MinLevelOffset = -3, MaxLevelOffset = 3;

    /// <summary>
    /// The level the suggestions aim at, in stars, chosen by the player (the page's level slider, 0 to the hardest
    /// ranked map); null for the player's level plus <see cref="LevelOffset"/>. Moves the same three things as the offset.
    /// </summary>
    public double? TargetLevel { get; init; }

    public bool ExcludeAlreadyPlayed { get; init; } = true;

    /// <summary>
    /// The leaderboards of the player's own plays to seed from, chosen by the player; null to pick them
    /// (<see cref="SeedSongSelector"/>, <see cref="SeedCount"/> of them). Ids the player has no score on are ignored.
    /// </summary>
    public IReadOnlyCollection<string>? SeedLeaderboardIds { get; init; }

    /// <summary>
    /// Leaderboards the player added as seeds. Those they have a score on seed like their other plays; on the others
    /// the peers are the scorers whose overall pp is nearest theirs, a weaker link than their own score. Never
    /// suggested back.
    /// </summary>
    public IReadOnlyCollection<string> AddedSeedLeaderboardIds { get; init; } = [];

    /// <summary>How many scored songs to return (one per leaderboard).</summary>
    public int TopN { get; init; } = 50;
}

/// <summary>A <see cref="RecommendationPipeline"/> run.</summary>
public sealed record RecommendationResult
{
    /// <summary>The scored songs, best first; empty with an <see cref="Error"/>.</summary>
    public IReadOnlyList<ScoredSong> Results { get; init; } = [];

    public string? Error { get; init; }

    /// <summary>The player's level in stars (<see cref="RecommendationPipeline.PlayerLevel"/>); null without plays.</summary>
    public double? PlayerLevel { get; init; }

    /// <summary>How many plays the level rests on (up to <see cref="RecommendationPipeline.LevelTopPlays"/>).</summary>
    public int LevelPlays { get; init; }

    /// <summary>
    /// The level the suggestions aim at: <see cref="RecommendationOptions.TargetLevel"/>, else the level plus
    /// <see cref="RecommendationOptions.LevelOffset"/>.
    /// </summary>
    public double? TargetLevel { get; init; }

    /// <summary>Whether the suggestions are within <see cref="RecommendationPipeline.StarWindow"/> of <see cref="TargetLevel"/>.</summary>
    public bool InStarWindow { get; init; }

    /// <summary>All the player's ranked plays, best PP first.</summary>
    public IReadOnlyList<Score> PlayerScores { get; init; } = [];

    /// <summary>The player's plays the suggestions were seeded from.</summary>
    public IReadOnlyList<Score> Seeds { get; init; } = [];

    /// <summary>The added seeds the player has no score on.</summary>
    public IReadOnlyList<Song> AddedSeeds { get; init; } = [];

    /// <summary>The players the seeds linked the player to, the most linked first.</summary>
    public IReadOnlyList<PeerLink> Peers { get; init; } = [];
}

/// <summary>A peer of a <see cref="RecommendationPipeline"/> run: a player the seeds linked the player to.</summary>
/// <param name="PlayerId">The peer's player id.</param>
/// <param name="SeedSongs">The seeds the peer was found on: the more, the closer the link.</param>
/// <param name="Suggestions">How many of the run's suggestions are among the peer's top plays.</param>
public sealed record PeerLink(string PlayerId, IReadOnlyList<Song> SeedSongs, int Suggestions);

/// <summary>
/// The whole recommendation pipeline: seed songs (<see cref="SeedSongSelector"/>), linked players'
/// candidate songs (<see cref="LinkedPlayerFinder"/>), optionally without the songs the player already
/// played, then scored (<see cref="SongScorer"/>). Shared by the Song Recommendations page and the
/// offline evaluation (<c>songreceval</c>).
/// </summary>
public sealed class RecommendationPipeline
{
    private readonly IBeatLeaderClient _client;

    public RecommendationPipeline(IBeatLeaderClient client)
    {
        _client = client;
    }

    /// <summary>The lowest level the suggestions aim at, whatever the offset: about the easiest ranked maps.</summary>
    public const double MinTargetStars = 1;

    /// <summary>
    /// Suggestions are maps within this many stars of the aimed level (when any candidate is). About 80% of the maps
    /// that enter players' top 50 are within it of their level; it's what makes
    /// <see cref="RecommendationOptions.LevelOffset"/> move the suggestions.
    /// </summary>
    public const double StarWindow = 1.5;

    /// <summary>
    /// Below this many ranked plays the level and the suggestions are rough: with fewer, suggestions are no better
    /// than the most played maps around the player's level.
    /// </summary>
    public const int FewPlays = 10;

    /// <summary>Plays the player's level is taken from: their best by PP.</summary>
    public const int LevelTopPlays = 50;

    /// <summary>
    /// A player's level in stars: the median stars (each map with its speed modifier) of their
    /// <see cref="LevelTopPlays"/> best plays, or of all their plays with fewer. Null without plays.
    /// One or two lucky passes don't move it, and it's where players gain: the maps that enter their top 50
    /// center on it, unlike BalanceTuner.PlayerLevels (the top of a player's range, not what they usually play).
    /// </summary>
    public static double? PlayerLevel(IEnumerable<Score> topPlays)
    {
        var stars = topPlays
            .Take(LevelTopPlays)
            .Select(s => s.Song.ModifierStars ?? s.Song.Stars)
            .OfType<double>()
            .Order()
            .ToList();
        if (stars.Count == 0) return null;
        var middle = stars.Count / 2;
        return stars.Count % 2 == 1 ? stars[middle] : (stars[middle - 1] + stars[middle]) / 2;
    }

    /// <summary>A player's overall PP from their plays, as PlayersRefresh: each play's PP x 0.965^its position, best first.</summary>
    public static double ProfilePp(IEnumerable<Score> plays) =>
        plays.Select(s => s.Pp).OrderByDescending(pp => pp).Take(10000).Select((pp, i) => pp * Math.Pow(0.965, i)).Sum();

    public async Task<RecommendationResult> RunAsync(
        string playerId,
        RecommendationOptions options,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var finder = new LinkedPlayerFinder(_client);
        var scorer = new SongScorer();

        // Recommendations start from the player's own ranked plays: their level, their overall pp, and the seeds
        var playerScores = (await _client.GetPlayerTopScoresAsync(playerId, int.MaxValue, cancellationToken).ConfigureAwait(false))
            .OrderByDescending(s => s.Pp)
            .ToList();
        if (playerScores.Count == 0)
        {
            return new() { Error = "No ranked plays found for this player in this database: the player id is unknown, or they have no ranked scores yet. Suggestions need at least one ranked play." };
        }

        var playerLevel = PlayerLevel(playerScores);
        var levelOffset = Math.Clamp(options.LevelOffset, RecommendationOptions.MinLevelOffset, RecommendationOptions.MaxLevelOffset);
        var targetLevel = options.TargetLevel is double chosenLevel ? Math.Max(chosenLevel, 0) : playerLevel + levelOffset;
        var playedById = playerScores.ToDictionary(s => s.Song.LeaderboardId, StringComparer.Ordinal);

        // Seeds: the player's choice of their plays (or picked), plus the leaderboards they added (their score when they
        // have one, else the map)
        IReadOnlyList<Score> seedScores = options.SeedLeaderboardIds is { } chosen
            ? playerScores.Where(s => chosen.Contains(s.Song.LeaderboardId)).ToList()
            : await new SeedSongSelector(_client).SelectSeedSongsAsync(
                playerId, options.SeedCount, onProgress: onProgress, playedScores: playerScores, cancellationToken: cancellationToken).ConfigureAwait(false);
        var addedIds = options.AddedSeedLeaderboardIds.Distinct(StringComparer.Ordinal).ToList();
        seedScores = seedScores
            .Concat(addedIds.Where(playedById.ContainsKey).Select(id => playedById[id]))
            .DistinctBy(s => s.Song.LeaderboardId)
            .OrderByDescending(s => s.Pp)
            .ToList();
        var addedSongs = await _client.GetRankedMapsAsync(addedIds.Where(id => !playedById.ContainsKey(id)).ToList(), cancellationToken).ConfigureAwait(false);

        var result = new RecommendationResult
        {
            PlayerLevel = playerLevel,
            LevelPlays = Math.Min(playerScores.Count, LevelTopPlays),
            TargetLevel = targetLevel,
            PlayerScores = playerScores,
            Seeds = seedScores,
            AddedSeeds = addedSongs
        };
        if (seedScores.Count == 0 && addedSongs.Count == 0)
        {
            return result with { Error = "No seeds: tick at least one of your plays, or add a map." };
        }

        // With an offset, the peers are the players about that much stronger or weaker: at the same acc, pp grows about
        // in proportion to stars, so their pp on each seed is the player's times target / level (the target at least
        // MinTargetStars, the easiest ranked maps)
        var peerPpScale = playerLevel > 0 && targetLevel.HasValue
            ? Math.Max(targetLevel.Value, MinTargetStars) / playerLevel.Value
            : 1d;

        var candidates = await finder.FindCandidateSongsAsync(
            playerId,
            options.SeedCount,
            options.PeersPerSeed,
            options.PlaysPerPeer,
            onProgress: onProgress,
            seedScores: seedScores,
            peerPpScale: peerPpScale,
            extraSeedSongs: addedSongs,
            targetPlayerPp: ProfilePp(playerScores),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var linked = candidates;

        // Never the seeds themselves; with ExcludeAlreadyPlayed, nothing the player has played
        var excluded = new HashSet<string>(seedScores.Select(s => s.Song.LeaderboardId).Concat(addedSongs.Select(s => s.LeaderboardId)), StringComparer.Ordinal);
        if (options.ExcludeAlreadyPlayed)
        {
            excluded.UnionWith(playedById.Keys);
        }
        candidates = candidates
            .Where(pair => !excluded.Contains(pair.Key.LeaderboardId))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        if (candidates.Count == 0)
        {
            return result with { Error = options.ExcludeAlreadyPlayed ? "No candidate songs remain after excluding already-played songs." : "No candidate songs found.", Peers = Peers(linked, []) };
        }

        // Only maps within StarWindow of the aimed level, unless none is (then every candidate, said so)
        var inStarWindow = false;
        if (targetLevel is double aim)
        {
            var inWindow = candidates
                .Where(pair => (pair.Key.ModifierStars ?? pair.Key.Stars) is double stars && Math.Abs(stars - aim) <= StarWindow)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            if (inWindow.Count > 0)
            {
                candidates = inWindow;
                inStarWindow = true;
            }
            else
            {
                onProgress?.Invoke($"No candidate within {StarWindow} stars of {aim:0.00}: suggesting from all of them.");
            }
        }

        // A chart can be a candidate with and without a speed modifier: only its best scored one is kept
        var weights = new ScoringWeights { Style = options.StyleWeight, Easier = options.EasierWeight };
        var scored = scorer.ScoreCandidates(candidates, seedScores, weights, int.MaxValue, targetLevel, options.NeutralPlayers)
            .DistinctBy(s => s.Song.LeaderboardId)
            .Take(options.TopN)
            .ToList();
        return result with { Results = scored, InStarWindow = inStarWindow, Peers = Peers(linked, scored) };
    }

    /// <summary>Every peer with a candidate (also the excluded ones), the most linked first, with how many suggestions each brought.</summary>
    private static List<PeerLink> Peers(IReadOnlyDictionary<Song, List<CandidateEntry>> candidates, IReadOnlyList<ScoredSong> scored)
    {
        var suggestions = scored
            .SelectMany(s => candidates.GetValueOrDefault(s.Song) ?? [])
            .GroupBy(e => e.LinkedPlayer.Player.Id)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Score.Song.LeaderboardId).Distinct().Count());
        return candidates.Values
            .SelectMany(entries => entries)
            .Select(e => e.LinkedPlayer)
            .DistinctBy(p => p.Player.Id)
            .Select(p => new PeerLink(p.Player.Id, p.SharedSeedSongs.Distinct().ToList(), suggestions.GetValueOrDefault(p.Player.Id)))
            .OrderByDescending(p => p.SeedSongs.Count)
            .ThenByDescending(p => p.Suggestions)
            .ToList();
    }
}
