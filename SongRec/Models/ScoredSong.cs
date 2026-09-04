namespace beatleader_songrec.Models;

/// <summary>
/// A candidate song with its final weighted recommendation score and the individual
/// factor subscores that produced it, useful for debugging/tuning weights.
/// </summary>
public sealed class ScoredSong
{
    public required Song Song { get; init; }

    /// <summary>The final combined, weighted score used for ranking (higher is better).</summary>
    public required double Score { get; init; }

    /// <summary>Normalized [0,1] subscore for the Distance factor (closer to seed avg PP = higher).</summary>
    public double DistanceScore { get; init; }

    /// <summary>Normalized [0,1] subscore for the Style factor (above-average linked-player share = higher).</summary>
    public double StyleScore { get; init; }

    /// <summary>Normalized [0,1] subscore for the Overweight factor (lower average rank among linked players = higher).</summary>
    public double OverweightScore { get; init; }

    /// <summary>Number of linked players who have a score on this candidate.</summary>
    public int LinkedPlayerCount { get; init; }

    /// <summary>Average PP across the linked players' scores on this candidate.</summary>
    public double AveragePp { get; init; }

    /// <summary>Average rank across the linked players' scores on this candidate.</summary>
    public double AverageRank { get; init; }
}
