using beatleader_songrec.Models;
using beatleader_songrec.Requests;

namespace beatleader_songrec.Core;

/// <summary>
/// Scores the candidate song pool produced by <see cref="ILinkedPlayerFinder"/> using a
/// SmartSongSuggest-style multiplicative model, Style x Easier, and returns the top-ranked
/// candidates. SmartSongSuggest's Overweight factor is omitted: it favors harder maps and hurts
/// results once Easier is on.
/// </summary>
public interface ISongScorer
{
    /// <summary>
    /// Scores every candidate in <paramref name="candidates"/> and returns the top
    /// <paramref name="topN"/>, best first.
    /// </summary>
    /// <param name="seedScores">The target player's seed scores; validated but unused by the score.</param>
    /// <param name="playerLevel">
    /// The player's level in stars (<see cref="RecommendationPipeline.PlayerLevel"/>), for the Easier
    /// factor; null leaves it neutral.
    /// </param>
    /// <param name="neutralPlayers">
    /// Neutral linked players added to each candidate's Style (<see cref="RecommendationOptions.NeutralPlayers"/>);
    /// 0 for none.
    /// </param>
    IReadOnlyList<ScoredSong> ScoreCandidates(
        IReadOnlyDictionary<Song, List<CandidateEntry>> candidates,
        IReadOnlyList<Score> seedScores,
        ScoringWeights weights,
        int topN = 50,
        double? playerLevel = null,
        double neutralPlayers = 0);
}

/// <inheritdoc cref="ISongScorer"/>
public sealed class SongScorer : ISongScorer
{
    /// <summary>
    /// Stars over which the Easier factor goes from neutral (a map at the player's level) to 73% / 27%
    /// of its range (1 star under / above it): the logistic 1 / (1 + e^(stars above level / scale)).
    /// About the spread of the maps that enter players' top 50 around their level.
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
        double? playerLevel = null,
        double neutralPlayers = 0)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(seedScores);
        ArgumentNullException.ThrowIfNull(weights);

        if (candidates.Count == 0)
        {
            return [];
        }

        // `Entries` may hold several links from the same linked player (one per shared seed); Style measures that
        // repetition. Player-facing stats (count, PP) are de-duplicated by player.
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

        // Style (as SmartSongSuggest's SongEndPoint.SetStyle): for each origin song linking to this candidate, add
        // (links from that origin to this candidate) / (all links from that origin across the pool).
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

        // Neutral players: pull each Style toward the pool's mean, (style x n + mean x k) / (n + k) with n its distinct
        // linked players, so a candidate few peers link keeps only part of its signal.
        if (neutralPlayers > 0)
        {
            var meanStyle = proportionalStyles.Average();
            for (var i = 0; i < stats.Count; i++)
            {
                var n = stats[i].LinkedPlayerCount;
                proportionalStyles[i] = (proportionalStyles[i] * n + meanStyle * neutralPlayers) / (n + neutralPlayers);
            }
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
                StyleScore = styleNormalized,
                EasierScore = easierNormalized,
                StarsAboveLevel = starsAboveLevel,
                LinkedPlayerCount = stat.LinkedPlayerCount,
                AveragePp = stat.AveragePp
            });
        }

        // Ties broken by leaderboard id and modifier so results are stable across runs
        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Song.LeaderboardId, StringComparer.Ordinal)
            .ThenBy(s => (int)s.Song.Modifier)
            .Take(topN)
            .ToList();
    }

    /// <summary>
    /// Min-max normalizes <paramref name="value"/> into [0,1]; 1 when the pool has no spread.
    /// </summary>
    private static double Normalize(double value, double min, double max)
    {
        var range = max - min;
        return range > 0 ? (value - min) / range : 1d;
    }

    /// <summary>
    /// Maps a normalized [0,1] value into [0.5, 1.5], blended toward neutral (1.0) by the 0-100 weight:
    /// 0 flattens the factor, 100 applies the full spread.
    /// </summary>
    private static double Compress(double normalizedValue, double weight)
    {
        var strength = Math.Clamp(weight, 0, 100) / 100d;
        var fullSpread = FactorRangeMin + (normalizedValue * (FactorRangeMax - FactorRangeMin));
        return 1d + ((fullSpread - 1d) * strength);
    }
}
