namespace beatleader_songrec.Models;

/// <summary>
/// A ranked Beat Saber map (song + difficulty + mode combination), identified by its
/// BeatLeader leaderboard id and speed <see cref="Modifier"/>. A speed-modified play (SS/FS/SFS)
/// is treated as a distinct song entity from the unmodified chart, so equality/hashing is based
/// solely on <see cref="LeaderboardId"/> + <see cref="Modifier"/> - other metadata (name, stars,
/// etc.) does not affect identity.
/// </summary>
public sealed class Song : IEquatable<Song>
{
    public required string LeaderboardId { get; init; }

    /// <summary>
    /// The speed modifier under which this song entity was played, if any. Defaults to
    /// <see cref="SpeedModifier.None"/> for the unmodified chart.
    /// </summary>
    public SpeedModifier Modifier { get; init; } = SpeedModifier.None;

    public required string Hash { get; init; }

    public required string Name { get; init; }

    public string? Author { get; init; }

    public string? Mapper { get; init; }

    public string? CoverImageUrl { get; init; }

    public string? DifficultyName { get; init; }

    public string? ModeName { get; init; }

    public double? Stars { get; init; }

    public bool Equals(Song? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return LeaderboardId == other.LeaderboardId && Modifier == other.Modifier;
    }

    public override bool Equals(object? obj) => Equals(obj as Song);

    public override int GetHashCode() => HashCode.Combine(LeaderboardId, Modifier);
}
