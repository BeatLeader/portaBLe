using beatleader_songrec.Models;
using beatleader_songrec.Requests;

namespace beatleader_songrec.Core;

/// <summary>
/// Scores the candidate song pool produced by <see cref="ILinkedPlayerFinder"/> using a
/// SmartSongSuggest-style multiplicative model, Style x Easier, and returns the top-ranked
/// candidates. (SmartSongSuggest's Overweight factor was dropped: it favors harder maps, and any
/// weight on it found fewer of the maps that enter players' top 50 once Easier was on.)
/// </summary>
public interface ISongScorer
{
    /// <summary>
    /// Scores every candidate in <paramref name="candidates"/> and returns the top
    /// <paramref name="topN"/> (default 50), sorted descending by final score.
    /// </summary>
    /// <param name="candidates">The candidate pool from <see cref="ILinkedPlayerFinder"/>.</param>
    /// <param name="seedScores">The target player's seed scores (kept for interface/API compatibility; not used in the SmartSongSuggest-style score).</param>
    /// <param name="weights">User-configurable weights; the weights of <see cref="ScoringWeights.Style"/> and <see cref="ScoringWeights.Easier"/>.</param>
    /// <param name="topN">Maximum number of results to return (default 50).</param>
    /// <param name="playerLevel">
    /// The player's level in stars (<see cref="RecommendationPipeline.PlayerLevel"/>), for the Easier
    /// factor; null leaves it neutral.
    /// </param>
    IReadOnlyList<ScoredSong> ScoreCandidates(
        IReadOnlyDictionary<Song, List<CandidateEntry>> candidates,
        IReadOnlyList<Score> seedScores,
        ScoringWeights weights,
        int topN = 50,
        double? playerLevel = null);
}

/// <inheritdoc cref="ISongScorer"/>
public sealed class SongScorer : ISongScorer
{
    /// <summary>
    /// Stars over which the Easier factor goes from neutral (a map at the player's level) to 73% / 27%
    /// of its range (1 star under / above it): the logistic 1 / (1 + e^(stars above level / scale)).
    /// About the spread of the maps that enter players' top 50: their middle half spans 1.5 stars, centered
    /// on the player's level (holdout, 274 players).
    /// </summary>
    private const double EasierScaleStars = 1d;

    /// <summary>Compressed multiplicative range for the Style and Easier factors.</summary>
    private const double FactorRangeMin = 0.5d;
    private const double FactorRangeMax = 1.5d;

    public IReadOnlyList<ScoredSong> ScoreCandidates(
        IReadOnlyDictionary<Song, List<CandidateEntry>> candidates,
        IReadOnlyList<Score> seedScores,
        ScoringWeights weights,
        int topN = 50,
        double? playerLevel = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(seedScores);
        ArgumentNullException.ThrowIfNull(weights);

        if (candidates.Count == 0)
        {
            return [];
        }

        // Raw per-candidate stats. Note that `Entries` may contain multiple raw links for the
        // same linked player (one per shared seed/origin song), by design - that repetition is
        // what the Style factor below measures. For player-facing stats (count/PP/rank) we
        // de-duplicate by player so a single player isn't counted multiple times.
        var stats = candidates
            .Select(pair =>
            {
                var distinctPlayerEntries = pair.Value
                    .GroupBy(e => e.LinkedPlayer.Player.Id)
                    .Select(g => g.First())
                    .ToList();

                return new
                {
                    Song = pair.Key,
                    Entries = pair.Value,
                    AveragePp = distinctPlayerEntries.Count > 0 ? distinctPlayerEntries.Average(e => e.Score.Pp) : 0d,
                    LinkedPlayerCount = distinctPlayerEntries.Count
                };
            })
            .ToList();

        // Style: proportional share of origin-song links pointing at this candidate, summed
        // across each distinct origin song, mirroring SongEndPoint.SetStyle. For each origin
        // song that links to this candidate, add (links from that origin to this candidate) /
        // (total links originating from that origin song across the whole pool).
        var linksByOriginSong = candidates.Values
            .SelectMany(entries => entries)
            .GroupBy(e => e.OriginSong)
            .ToDictionary(g => g.Key, g => g.Count());

        var proportionalStyles = new List<double>(stats.Count);
        foreach (var stat in stats)
        {
            var style = 0d;
            var originGroups = stat.Entries.GroupBy(e => e.OriginSong);
            foreach (var originGroup in originGroups)
            {
                var originTotal = linksByOriginSong.GetValueOrDefault(originGroup.Key, originGroup.Count());
                if (originTotal > 0)
                {
                    style += (double)originGroup.Count() / originTotal;
                }
            }

            proportionalStyles.Add(style);
        }

        var minStyle = proportionalStyles.Count > 0 ? proportionalStyles.Min() : 0d;
        var maxStyle = proportionalStyles.Count > 0 ? proportionalStyles.Max() : 0d;

        var scored = new List<ScoredSong>(stats.Count);

        for (var i = 0; i < stats.Count; i++)
        {
            var stat = stats[i];

            // Normalize each factor to [0,1], then compress into [0.5, 1.5] so the two factors
            // combine multiplicatively without either one being able to zero out the score.
            var styleNormalized = Normalize(proportionalStyles[i], minStyle, maxStyle);
            var styleFactor = Compress(styleNormalized, weights.Style);

            // Easier: maps under the player's level favored, maps above it held back
            var stars = stat.Song.ModifierStars ?? stat.Song.Stars;
            double? starsAboveLevel = stars.HasValue && playerLevel.HasValue ? stars.Value - playerLevel.Value : null;
            var easierNormalized = starsAboveLevel.HasValue ? 1d / (1d + Math.Exp(starsAboveLevel.Value / EasierScaleStars)) : 0.5d;
            var easierFactor = Compress(easierNormalized, weights.Easier);

            var finalScore = styleFactor * easierFactor;

            scored.Add(new ScoredSong
            {
                Song = stat.Song,
                Score = finalScore,
                DistanceScore = 0d,
                StyleScore = styleNormalized,
                EasierScore = easierNormalized,
                StarsAboveLevel = starsAboveLevel,
                LinkedPlayerCount = stat.LinkedPlayerCount,
                AveragePp = stat.AveragePp
            });
        }

        // Sort descending by final score; break ties deterministically by leaderboard id so
        // results are stable across runs (e.g. when all candidates are tied).
        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Song.LeaderboardId, StringComparer.Ordinal)
            .ThenBy(s => (int)s.Song.Modifier)
            .Take(topN)
            .ToList();
    }

    /// <summary>
    /// Min-max normalizes <paramref name="value"/> into [0,1] given the pool's [min,max] range.
    /// When the pool has no spread (min == max), every candidate is treated as equally favorable
    /// (normalized to 1).
    /// </summary>
    private static double Normalize(double value, double min, double max)
    {
        var range = max - min;
        return range > 0 ? (value - min) / range : 1d;
    }

    /// <summary>
    /// Compresses a normalized [0,1] value into the [0.5, 1.5] multiplicative range used by
    /// SmartSongSuggest-style scoring, scaled by the corresponding 0-100 weight slider so a
    /// weight of 0 flattens the factor to neutral (1.0) and a weight of 100 applies the full
    /// compressed spread.
    /// </summary>
    private static double Compress(double normalizedValue, double weight)
    {
        var strength = Math.Clamp(weight, 0, 100) / 100d;
        var fullSpread = FactorRangeMin + (normalizedValue * (FactorRangeMax - FactorRangeMin));
        // Blend between neutral (1.0, weight = 0) and the full compressed spread (weight = 100).
        return 1d + ((fullSpread - 1d) * strength);
    }
}
