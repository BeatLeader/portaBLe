using beatleader_songrec.Models;
using beatleader_songrec.Requests;

namespace beatleader_songrec.Core;

/// <summary>
/// Scores the candidate song pool produced by <see cref="ILinkedPlayerFinder"/> using a
/// SmartSongSuggest-style multiplicative Style x Overweight model and returns the top-ranked
/// candidates.
/// </summary>
public interface ISongScorer
{
    /// <summary>
    /// Scores every candidate in <paramref name="candidates"/> and returns the top
    /// <paramref name="topN"/> (default 50), sorted descending by final score.
    /// </summary>
    /// <param name="candidates">The candidate pool from <see cref="ILinkedPlayerFinder"/>.</param>
    /// <param name="seedScores">The target player's seed scores (kept for interface/API compatibility; not used in the SmartSongSuggest-style score).</param>
    /// <param name="weights">User-configurable weights; only <see cref="ScoringWeights.Style"/> and <see cref="ScoringWeights.Overweight"/> affect the final score.</param>
    /// <param name="topN">Maximum number of results to return (default 50).</param>
    IReadOnlyList<ScoredSong> ScoreCandidates(
        IReadOnlyDictionary<Song, List<CandidateEntry>> candidates,
        IReadOnlyList<Score> seedScores,
        ScoringWeights weights,
        int topN = 50);
}

/// <inheritdoc cref="ISongScorer"/>
public sealed class SongScorer : ISongScorer
{
    /// <summary>
    /// Minimum number of links required before a candidate's average rank is treated at face
    /// value; below this, the average is padded toward the "neutral" rank (10.5, the mean of
    /// ranks 1-20) so sparsely-linked songs aren't unfairly rewarded/penalized. Mirrors
    /// SmartSongSuggest's SongEndPoint.SetRelevance minimum-link padding.
    /// </summary>
    private const double MinRankLinks = 20d;

    /// <summary>The "neutral" average rank used to pad sparsely-linked candidates.</summary>
    private const double NeutralRank = 10.5d;

    /// <summary>Compressed multiplicative range for the Style and Overweight factors.</summary>
    private const double FactorRangeMin = 0.5d;
    private const double FactorRangeMax = 1.5d;

    public IReadOnlyList<ScoredSong> ScoreCandidates(
        IReadOnlyDictionary<Song, List<CandidateEntry>> candidates,
        IReadOnlyList<Score> seedScores,
        ScoringWeights weights,
        int topN = 50)
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

        // Overweight: average rank among linked players, padded toward the neutral rank when a
        // candidate has few links, mirroring SongEndPoint.SetRelevance.
        var averageRanks = new List<double>(stats.Count);
        for (var i = 0; i < stats.Count; i++)
        {
            var entries = stats[i].Entries;
            var rankSum = (double)entries.Sum(e => e.Score.Rank);
            var rankLinks = entries.Count;
            rankSum += Math.Max(MinRankLinks - rankLinks, 0d) * NeutralRank;
            var averageRank = rankSum / Math.Max(MinRankLinks, rankLinks);
            averageRanks.Add(averageRank);
        }

        var minStyle = proportionalStyles.Count > 0 ? proportionalStyles.Min() : 0d;
        var maxStyle = proportionalStyles.Count > 0 ? proportionalStyles.Max() : 0d;
        var minRank = averageRanks.Count > 0 ? averageRanks.Min() : 0d;
        var maxRank = averageRanks.Count > 0 ? averageRanks.Max() : 0d;

        var scored = new List<ScoredSong>(stats.Count);

        for (var i = 0; i < stats.Count; i++)
        {
            var stat = stats[i];

            // Normalize each factor to [0,1], then compress into [0.5, 1.5] so the two factors
            // combine multiplicatively without either one being able to zero out the score.
            var styleNormalized = Normalize(proportionalStyles[i], minStyle, maxStyle, invert: false);
            var overweightNormalized = Normalize(averageRanks[i], minRank, maxRank, invert: true);

            var styleFactor = Compress(styleNormalized, weights.Style);
            var overweightFactor = Compress(overweightNormalized, weights.Overweight);

            var finalScore = styleFactor * overweightFactor;

            scored.Add(new ScoredSong
            {
                Song = stat.Song,
                Score = finalScore,
                DistanceScore = 0d,
                StyleScore = styleNormalized,
                OverweightScore = overweightNormalized,
                LinkedPlayerCount = stat.LinkedPlayerCount,
                AveragePp = stat.AveragePp,
                AverageRank = averageRanks[i]
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
    /// When <paramref name="invert"/> is true, a smaller raw value yields a larger normalized
    /// score (used for "lower rank is better"). When the pool has no spread (min == max), every
    /// candidate is treated as equally favorable (normalized to 1).
    /// </summary>
    private static double Normalize(double value, double min, double max, bool invert)
    {
        var range = max - min;
        if (range <= 0)
        {
            return 1d;
        }

        var normalized = (value - min) / range;
        return invert ? 1d - normalized : normalized;
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
