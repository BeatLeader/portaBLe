using beatleader_songrec.Api;
using beatleader_songrec.Models;
using beatleader_songrec.Requests;

namespace beatleader_songrec.Core;

/// <summary>
/// Settings of one <see cref="RecommendationPipeline"/> run (the Song Recommendations page's form).
/// </summary>
public sealed record RecommendationOptions
{
    /// <summary>
    /// The player's plays the peers are found from (SeedSongSelector). Seeds 25 / 50 / 75 / 100 (songreceval seeds, 200
    /// players): −1.4 [−2.6, −0.3] / 0 / −0.8 / −1.2 [−2.2, −0.1] points of the maps that enter players' top 50 found.
    /// </summary>
    public int SeedCount { get; init; } = 50;

    // Peers linked per seed song (the scorers nearest the player's pp on it), and how many of each peer's top plays become
    // candidates (one setting, Top-K 20, before). Peers x plays 10 / 20 / 40 each (songreceval links, 200 players):
    // more plays found more of the maps that enter players' top 50, more peers barely (40 x 40 within noise of
    // 20 x 40, at twice the queries). Plays 20 / 40 / 60 / 80 / 120 (songreceval plays): 0 / +2.0 [+0.8, +3.1] /
    // +1.4 / +1.6 / +0.8 points, so 40, the cheapest of the plateau.
    public int PeersPerSeed { get; init; } = 20;

    public int PlaysPerPeer { get; init; } = 40;

    // Style and Easier weights (Easier: maps under the player's level over maps above it, ScoringWeights.Easier): the
    // best of Style x Easier 0-100 by 25 (songreceval weights, 200 players): 24.5% of the maps that enter players'
    // top 50 found, the suggestions' median 0.3 stars above the level (where those maps are: +0.1). Without Easier
    // 22.2% and +1.3; more Easier than half of Style pulled them under the level and found fewer (Style 100 Easier
    // 100: 18.4%, -1.9).
    public double StyleWeight { get; init; } = 100;

    public double EasierWeight { get; init; } = 50;

    /// <summary>
    /// Stars added to the player's level to get the level the suggestions aim at (the page's level offset slider,
    /// <see cref="MinLevelOffset"/> to <see cref="MaxLevelOffset"/>): negative for easier suggestions, positive for
    /// harder ones. 0 is where players gain (see <see cref="RecommendationPipeline.PlayerLevel"/>). The aimed level
    /// moves three things: the peers (players about that much weaker or stronger), the star window
    /// (<see cref="RecommendationPipeline.StarWindow"/>) and the Easier factor's center. Each star of offset moved the
    /// suggestions' median 0.6-0.7 stars (songreceval offset, 100 players: -2 / -1 / +1 / +2 → -1.44 / -0.71 / +0.58 /
    /// +1.24); without the window only 0.3-0.5, and moving only the Easier factor's center 0.15.
    /// </summary>
    public double LevelOffset { get; init; } = 0;

    /// <summary>
    /// The offset's range: 3 stars either way, about twice the spread of the maps that enter players' top 50
    /// around their level (middle half 1.5 stars wide), so from clearly easier to clearly harder than usual.
    /// </summary>
    public const double MinLevelOffset = -3, MaxLevelOffset = 3;

    /// <summary>
    /// Fraction of links kept, closest first (<see cref="LinkedPlayerFinder"/>). 1, every link: peers' links are
    /// already close, so pruning only threw evidence away (0.25 / 0.5 / 0.75 / 1: -1.8 / 0 / +0.9 / +1.7 points of the
    /// maps that enter players' top 50 found against 0.5, songreceval keep, 200 players).
    /// </summary>
    public double LinkKeepPercent { get; init; } = 1;

    public bool ExcludeAlreadyPlayed { get; init; } = true;

    /// <summary>
    /// The leaderboards of the player's own plays to seed from, chosen by the player; null to pick them
    /// (<see cref="SeedSongSelector"/>, <see cref="SeedCount"/> of them). Ids the player has no score on are ignored.
    /// </summary>
    public IReadOnlyCollection<string>? SeedLeaderboardIds { get; init; }

    /// <summary>
    /// Leaderboards the player added as seeds. Those they have a score on seed like their other plays; on the others
    /// the peers are the scorers whose overall pp is nearest theirs. Never suggested back. That's weaker than their own
    /// score: with every seed linked that way, 20.9% of the maps that enter players' top 50 were found against 26.1%
    /// (−5.2 [−6.8, −3.6] points, every rank band lower; the most played maps at their level 13.1%; 200 players).
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

    /// <summary>The level the suggestions aim at: the level plus <see cref="RecommendationOptions.LevelOffset"/>.</summary>
    public double? TargetLevel { get; init; }

    /// <summary>Whether the suggestions are within <see cref="RecommendationPipeline.StarWindow"/> of <see cref="TargetLevel"/>.</summary>
    public bool InStarWindow { get; init; }

    /// <summary>All the player's ranked plays, best PP first.</summary>
    public IReadOnlyList<Score> PlayerScores { get; init; } = [];

    /// <summary>The player's plays the suggestions were seeded from.</summary>
    public IReadOnlyList<Score> Seeds { get; init; } = [];

    /// <summary>The added seeds the player has no score on.</summary>
    public IReadOnlyList<Song> AddedSeeds { get; init; } = [];
}

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
    /// Suggestions are maps within this many stars of the aimed level (when any candidate is). 79% of the maps that
    /// went on to enter players' top 50 were within it of their level (63% within 1, 89% within 2; holdout, 274
    /// players). At offset 0 it changed nothing measurable (gainRecall −0.1 [−2.0, +1.7] points; 1 star: −1.2,
    /// 2: −0.3), and it's what makes the level offset move the suggestions (see
    /// <see cref="RecommendationOptions.LevelOffset"/>; with 1 star 0.8 stars per star of offset, with 2 0.55).
    /// </summary>
    public const double StarWindow = 1.5;

    /// <summary>
    /// Below this many ranked plays the level and the suggestions are rough: new players simulated with their first
    /// 1, 3, 5 plays got no better suggestions than the most played maps around their level (1 play: 6.3% of their
    /// next 40 plays suggested against 7.8%), with 10 better (8.9% against 7.7%; songreceval with first plays, 100
    /// players each).
    /// </summary>
    public const int FewPlays = 10;

    /// <summary>Plays the player's level is taken from: their best by PP.</summary>
    public const int LevelTopPlays = 50;

    /// <summary>
    /// A player's level in stars: the median stars (each map with its speed modifier) of their
    /// <see cref="LevelTopPlays"/> best plays, or of all their plays with fewer. Null without plays.
    /// Half of their best plays are on harder maps, half on easier ones; one or two lucky passes don't
    /// move it. It's where players gain: the maps that went on to enter players' top 50 were +0.1 stars
    /// above it (median), against -1.3 under the balance tuner's level (the 5th hardest of the 50,
    /// BalanceTuner.PlayerLevels: the top of a player's range, not what they usually play), and it
    /// varies less between players (sd of each player's median gain 0.75 stars against 0.89; holdout,
    /// 274 players).
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
        var targetLevel = playerLevel + levelOffset;
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
            linkKeepPercent: options.LinkKeepPercent,
            peerPpScale: peerPpScale,
            extraSeedSongs: addedSongs,
            targetPlayerPp: ProfilePp(playerScores),
            cancellationToken: cancellationToken).ConfigureAwait(false);

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
            return result with { Error = options.ExcludeAlreadyPlayed ? "No candidate songs remain after excluding already-played songs." : "No candidate songs found." };
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
        var scored = scorer.ScoreCandidates(candidates, seedScores, weights, int.MaxValue, targetLevel)
            .DistinctBy(s => s.Song.LeaderboardId)
            .Take(options.TopN)
            .ToList();
        return result with { Results = scored, InStarWindow = inStarWindow };
    }
}
