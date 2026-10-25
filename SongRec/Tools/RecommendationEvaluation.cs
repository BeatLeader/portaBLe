using beatleader_songrec.Api;
using beatleader_songrec.Core;
using beatleader_songrec.Models;
using Microsoft.EntityFrameworkCore;
using DbContextType = Microsoft.EntityFrameworkCore.DbContext;
using DbLeaderboard = portaBLe.DB.Leaderboard;
using DbPlayer = portaBLe.DB.Player;
using DbScore = portaBLe.DB.Score;

namespace beatleader_songrec.Tools;

/// <summary>
/// Offline evaluation of the recommendations (temporal holdout): for players sampled across rank bands,
/// hides each player's <see cref="HeldOutScores"/> most recent scores, runs the real
/// <see cref="RecommendationPipeline"/> on what's left, and measures how its top 50 matches what the
/// player went on to play. Each variant of a set (<see cref="VariantSets"/>) is compared, with a naive baseline (the most played
/// unplayed maps within a star of the player's level), and against <see cref="Reference"/> player by player
/// (the mean difference and its 95% interval: an interval across 0 is noise at this sample size).
///
/// Metrics, averaged over the players:
/// - hit@50: share of the 50 suggestions the player played afterwards.
/// - gainHit@50: share of the 50 suggestions played afterwards for more pp than the player's #50 play
///   before (a play that entered their top 50).
/// - gainRecall: share of the maps played afterwards that entered the top 50 which were suggested.
/// - stars-level: median stars of the suggestions (nomod) minus the player's level
///   (<see cref="RecommendationPipeline.PlayerLevel"/>).
///
/// Limits: the database keeps one score per player and leaderboard, so a map improved after the cutoff
/// looks like a new play (and is hidden); other players' later scores stay in (the same for every
/// variant); "played afterwards" partly reflects what the player would have played anyway.
/// Run: dotnet run -c Release -- songreceval &lt;wwwroot/file.db&gt; [players].
/// </summary>
public static class RecommendationEvaluation
{
    public const int HeldOutScores = 40;
    private const int SuggestionCount = 50;
    private const int TopPlays = 50;
    private const int MinGainMaps = 3;
    private static readonly (int From, int To)[] RankBands = [(1, 500), (500, 2000), (2000, 6000), (6000, 15000), (15000, 40000)];
    private const string Baseline = "Popular at level";

    // Sets of the pipeline's options to compare (songreceval's [set] argument), each with the current defaults
    // as the reference.
    // - default: the current defaults without Easier, and the current defaults. Tried and rejected on 200 players
    //   (gainRecall vs the defaults then, before removal; see CHANGELOG.md for the numbers): the leaderboard's top
    //   scorers as linked players, SmartSongSuggest's Overweight factor (by leaderboard rank or top play position),
    //   one candidate per chart with its speed-modified plays merged, seeds by leaderboard percentile, Style
    //   normalized by rank instead of min-max, the weakest quarter of the plays left out of the seeds.
    //   New players (firstPlays, 100 players each), share of their next 40 plays suggested, current / popular at
    //   level: 1 play 6.3% / 7.8% (+1.5 [+0.1, +3.0] for popular), 3 plays 6.2 / 6.7, 5 plays 7.8 / 7.3, 10 plays
    //   8.9 / 7.7 (-1.2 [-2.3, -0.1]), 20 plays 8.6 / 8.0.
    // - easier, weights, keep, offset: sweeps of the Easier weight, of Style x Easier, of LinkKeepPercent and of the
    //   level offset (how far each star of offset moves the suggestions: their stars-level against Current's).
    public const string Reference = "Current";
    private static readonly RecommendationOptions Current = new();
    private static readonly double[] WeightSteps = [0, 25, 50, 75, 100];
    public static readonly Dictionary<string, (string Name, RecommendationOptions Options)[]> VariantSets = new()
    {
        ["default"] =
        [
            ("No easier", Current with { EasierWeight = 0 }),
            (Reference, Current),
        ],
        ["easier"] = WeightSteps.Select(w => ($"Easier {w}", Current with { EasierWeight = w }))
            .Prepend((Reference, Current)).ToArray(),
        ["weights"] = WeightSteps.SelectMany(style => WeightSteps.Select(easier =>
                ($"Style {style} Easier {easier}", Current with { StyleWeight = style, EasierWeight = easier })))
            .Prepend((Reference, Current)).ToArray(),
        ["keep"] = new[] { 0.25, 0.5, 0.75, 1.0 }.Select(k => ($"Keep {k}", Current with { LinkKeepPercent = k }))
            .Prepend((Reference, Current)).ToArray(),
        ["links"] = new[] { 10, 20, 40 }.SelectMany(peers => new[] { 10, 20, 40 }.Select(plays =>
                ($"Peers {peers} plays {plays}", Current with { PeersPerSeed = peers, PlaysPerPeer = plays })))
            .Prepend((Reference, Current)).ToArray(),
        ["plays"] = new[] { 20, 40, 60, 80, 120 }.Select(plays => ($"Plays {plays}", Current with { PlaysPerPeer = plays }))
            .Prepend((Reference, Current)).ToArray(),
        ["seeds"] = new[] { 25, 50, 75, 100 }.Select(n => ($"Seeds {n}", Current with { SeedCount = n }))
            .Prepend((Reference, Current)).ToArray(),
        ["offset"] = new double[] { -2, -1, 1, 2 }.Select(o => ($"Offset {o:+0;-0}", Current with { LevelOffset = o }))
            .Prepend((Reference, Current)).ToArray(),
    };

    private sealed record Result(double Hit, double GainHit, double GainRecall, double StarsMinusLevel);

    /// <param name="firstPlays">
    /// Null: each player's <see cref="HeldOutScores"/> most recent scores are hidden (established players). A number:
    /// new players, simulated: only each player's first this-many ranked plays are kept, and the suggestions are
    /// measured on their next <see cref="HeldOutScores"/> plays (all of which enter their top 50 when they have fewer).
    /// </param>
    public static async Task Run(DbContextType context, int players = 50, string set = "default", int sampleSeed = 7, int? firstPlays = null)
    {
        var variantSet = VariantSets.TryGetValue(set, out var found)
            ? found
            : throw new ArgumentException($"No variant set {set} (sets: {string.Join(", ", VariantSets.Keys)})");
        Console.WriteLine($"Song recommendation evaluation: set {set}, {players} players, sample seed {sampleSeed}"
            + (firstPlays is int n ? $", new players: their first {n} plays kept" : $", their {HeldOutScores} most recent scores hidden"));
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var leaderboards = await context.Set<DbLeaderboard>()
            .Where(l => l.Stars > 0)
            .Select(l => new { l.Id, l.Stars, l.Count })
            .ToDictionaryAsync(l => l.Id, l => (Stars: (double)l.Stars, l.Count));

        var client = new HoldoutClient(new DbBeatLeaderClient(context));
        var pipeline = new RecommendationPipeline(client);
        var variants = variantSet.Select(v => v.Name).Append(Baseline).ToList();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var results = new Dictionary<string, Dictionary<string, List<Result>>>();
        var random = new Random(sampleSeed);
        int perBand = Math.Max(1, players / RankBands.Length);

        foreach (var (from, to) in RankBands)
        {
            var band = $"{from}-{to}";
            results[band] = variants.ToDictionary(v => v, _ => new List<Result>());
            var ids = await context.Set<DbPlayer>()
                .Where(p => p.Rank >= from && p.Rank < to && p.RankedPlayCount >= HeldOutScores + (firstPlays ?? 120))
                .OrderBy(p => p.Rank)
                .Select(p => p.Id)
                .ToListAsync();
            Shuffle(ids, random);

            int done = 0;
            foreach (var playerId in ids)
            {
                if (done >= perBand) break;
                var scores = await context.Set<DbScore>()
                    .Where(s => s.PlayerId == playerId)
                    .Select(s => new { s.LeaderboardId, s.Pp, s.Timepost })
                    .ToListAsync();
                var times = scores.Select(s => s.Timepost).OrderByDescending(t => t).ToList();
                if (times.Count < HeldOutScores + (firstPlays ?? 1)) continue;
                int cutoff = firstPlays is int kept ? times[times.Count - kept] : times[HeldOutScores];

                var past = scores.Where(s => s.Timepost <= cutoff).OrderByDescending(s => s.Pp).ToList();
                if (firstPlays == null && past.Count < TopPlays) continue;
                double topPlaysPp = past.Count >= TopPlays ? past[TopPlays - 1].Pp : 0;
                var future = scores.Where(s => s.Timepost > cutoff).OrderBy(s => s.Timepost).Take(HeldOutScores).ToList();
                var played = future.Select(s => s.LeaderboardId).ToHashSet();
                var gain = future.Where(s => s.Pp > topPlaysPp).Select(s => s.LeaderboardId).ToHashSet();
                if (gain.Count < MinGainMaps) continue;

                client.Hide(playerId, cutoff);
                var level = RecommendationPipeline.PlayerLevel(
                    await client.GetPlayerTopScoresAsync(playerId, RecommendationPipeline.LevelTopPlays)) ?? 0;
                var lists = new Dictionary<string, List<string>>();
                foreach (var (name, options) in variantSet)
                {
                    var result = await pipeline.RunAsync(playerId, options);
                    lists[name] = result.Results.Select(s => s.Song.LeaderboardId).Take(SuggestionCount).ToList();
                }
                var pastIds = past.Select(s => s.LeaderboardId).ToHashSet();
                lists[Baseline] = leaderboards
                    .Where(l => Math.Abs(l.Value.Stars - level) <= 1 && !pastIds.Contains(l.Key))
                    .OrderByDescending(l => l.Value.Count)
                    .ThenBy(l => l.Key, StringComparer.Ordinal)
                    .Take(SuggestionCount)
                    .Select(l => l.Key)
                    .ToList();

                foreach (var (variant, list) in lists)
                {
                    var stars = list.Select(id => leaderboards.TryGetValue(id, out var l) ? l.Stars : 0).OrderBy(s => s).ToList();
                    results[band][variant].Add(new Result(
                        list.Count(played.Contains) / (double)SuggestionCount,
                        list.Count(gain.Contains) / (double)SuggestionCount,
                        list.Count(gain.Contains) / (double)gain.Count,
                        stars.Count > 0 ? stars[stars.Count / 2] - level : 0));
                }
                done++;
                Console.WriteLine($"[{band}] {done}/{perBand} player {playerId}: level {level:0.00}, {gain.Count} maps entered the top {TopPlays} after the cutoff ({stopwatch.Elapsed:hh\\:mm\\:ss})");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{"band",-12} {"variant",-18} {"n",3} {"hit@50",7} {"gainHit@50",10} {"gainRecall",10} {"stars-level",11}   {"gainRecall vs " + Reference,-26}");
        foreach (var (band, byVariant) in results.Append(new("ALL", variants.ToDictionary(v => v, v => results.Values.SelectMany(r => r[v]).ToList()))))
        {
            foreach (var variant in variants)
            {
                var r = byVariant[variant];
                if (r.Count == 0) continue;
                Console.WriteLine($"{band,-12} {variant,-18} {r.Count,3} {r.Average(x => x.Hit),7:0.000} {r.Average(x => x.GainHit),10:0.000} {r.Average(x => x.GainRecall),10:0.000} {r.Average(x => x.StarsMinusLevel),11:+0.00;-0.00;0.00}   {PairedDifference(r, byVariant[Reference])}");
            }
        }
        Console.WriteLine($"{stopwatch.Elapsed:hh\\:mm\\:ss}");
    }

    // Mean gainRecall difference with the reference over the same players, with its 95% interval
    private static string PairedDifference(List<Result> variant, List<Result> reference)
    {
        var d = variant.Zip(reference, (a, b) => a.GainRecall - b.GainRecall).ToList();
        if (d.Count < 2) return "";
        double mean = d.Average();
        double se = Math.Sqrt(d.Sum(x => (x - mean) * (x - mean)) / (d.Count - 1) / d.Count);
        return $"{mean:+0.000;-0.000;0.000} [{mean - 1.96 * se:+0.000;-0.000;0.000}, {mean + 1.96 * se:+0.000;-0.000;0.000}]";
    }

    private static void Shuffle<T>(IList<T> list, Random random)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// The database as it was at a player's cutoff, for that player: their scores set after it are hidden
    /// (other players' are not). Caches other players' and leaderboards' scores across players, since the
    /// evaluation asks for the same ones many times.
    /// </summary>
    private sealed class HoldoutClient(IBeatLeaderClient inner) : IBeatLeaderClient
    {
        private readonly Dictionary<(string, int), IReadOnlyList<Score>> _playerTop = new();
        private readonly Dictionary<(string, int), IReadOnlyList<Score>> _leaderboardTop = new();
        private readonly Dictionary<(string, double, int, string?), IReadOnlyList<Score>> _nearPp = new();
        private string? _hiddenPlayerId;
        private DateTimeOffset _cutoff;
        private IReadOnlyList<Score> _hiddenPlayerScores = [];

        public void Hide(string playerId, int cutoffUnixSeconds)
        {
            _hiddenPlayerId = playerId;
            _cutoff = DateTimeOffset.FromUnixTimeSeconds(cutoffUnixSeconds);
            _hiddenPlayerScores = [];
        }

        private bool IsHidden(Score score) => score.PlayerId == _hiddenPlayerId && score.SetAt > _cutoff;

        public async Task<IReadOnlyList<Score>> GetPlayerTopScoresAsync(string playerId, int count, CancellationToken cancellationToken = default)
        {
            if (playerId == _hiddenPlayerId)
            {
                if (_hiddenPlayerScores.Count == 0)
                {
                    _hiddenPlayerScores = (await inner.GetPlayerTopScoresAsync(playerId, int.MaxValue, cancellationToken).ConfigureAwait(false))
                        .Where(s => !IsHidden(s))
                        .ToList();
                }
                return _hiddenPlayerScores.Take(count).ToList();
            }

            if (!_playerTop.TryGetValue((playerId, count), out var scores))
            {
                _playerTop[(playerId, count)] = scores = await inner.GetPlayerTopScoresAsync(playerId, count, cancellationToken).ConfigureAwait(false);
            }
            return scores;
        }

        public async Task<IReadOnlyList<Score>> GetLeaderboardScoresByIdAsync(string leaderboardId, int count, CancellationToken cancellationToken = default)
        {
            // One more, in case the hidden player's score is among them
            if (!_leaderboardTop.TryGetValue((leaderboardId, count), out var scores))
            {
                _leaderboardTop[(leaderboardId, count)] = scores = await inner.GetLeaderboardScoresByIdAsync(leaderboardId, count + 1, cancellationToken).ConfigureAwait(false);
            }
            return scores.Where(s => !IsHidden(s)).Take(count).ToList();
        }

        // The pipeline excludes the target player (the hidden one) here, so nothing is left to hide
        public async Task<IReadOnlyList<Score>> GetLeaderboardScoresNearPpAsync(string leaderboardId, double pp, int count, string? excludePlayerId, CancellationToken cancellationToken = default)
        {
            if (!_nearPp.TryGetValue((leaderboardId, pp, count, excludePlayerId), out var scores))
            {
                _nearPp[(leaderboardId, pp, count, excludePlayerId)] = scores =
                    await inner.GetLeaderboardScoresNearPpAsync(leaderboardId, pp, count, excludePlayerId, cancellationToken).ConfigureAwait(false);
            }
            return scores;
        }

        // The pipeline excludes the target player (the hidden one) here too
        public Task<IReadOnlyList<Score>> GetLeaderboardScoresNearPlayerPpAsync(string leaderboardId, double playerPp, int count, string? excludePlayerId, CancellationToken cancellationToken = default) =>
            inner.GetLeaderboardScoresNearPlayerPpAsync(leaderboardId, playerPp, count, excludePlayerId, cancellationToken);

        public Task<IReadOnlyList<Song>> GetRankedMapsAsync(IReadOnlyCollection<string> leaderboardIds, CancellationToken cancellationToken = default) =>
            inner.GetRankedMapsAsync(leaderboardIds, cancellationToken);

        public Task<IReadOnlyList<Score>> GetLeaderboardScoresAsync(string beatSaverId, int reuploadCount, Difficulty difficulty, Characteristic characteristic, CancellationToken cancellationToken = default) =>
            inner.GetLeaderboardScoresAsync(beatSaverId, reuploadCount, difficulty, characteristic, cancellationToken);

        public Task<IReadOnlyList<Song>> GetAllRankedMapsAsync(CancellationToken cancellationToken = default) =>
            inner.GetAllRankedMapsAsync(cancellationToken);
    }
}
