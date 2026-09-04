namespace beatleader_songrec.Models;

/// <summary>
/// A single ranked play by a player on a specific <see cref="Song"/> difficulty.
/// </summary>
public sealed class Score
{
    public required long Id { get; init; }

    public required string PlayerId { get; init; }

    /// <summary>
    /// The player who set this score, when available (e.g. leaderboard score entries embed
    /// the scoring player). May be null for scores fetched from a player's own score list.
    /// </summary>
    public Player? Player { get; init; }

    public required Song Song { get; init; }

    public long BaseScore { get; init; }

    public long ModifiedScore { get; init; }

    public double Accuracy { get; init; }

    public double Pp { get; init; }

    public int Rank { get; init; }

    public string? Modifiers { get; init; }

    public DateTimeOffset? SetAt { get; init; }
}
