namespace beatleader_songrec.Models;

/// <summary>
/// A ranked Beat Saber map (song + difficulty + mode). Equality is <see cref="LeaderboardId"/> +
/// <see cref="Modifier"/> only, so a speed-modified play (SS/FS/SFS) is a distinct song from the
/// unmodified chart; other metadata doesn't affect identity.
/// </summary>
public sealed class Song : IEquatable<Song>
{
    public required string LeaderboardId { get; init; }

    /// <summary>The speed modifier played under; <see cref="SpeedModifier.None"/> for the unmodified chart.</summary>
    public SpeedModifier Modifier { get; init; } = SpeedModifier.None;

    public required string Hash { get; init; }

    public required string Name { get; init; }

    public string? Author { get; init; }

    public string? Mapper { get; init; }

    public string? CoverImageUrl { get; init; }

    public string? DifficultyName { get; init; }

    public string? ModeName { get; init; }

    /// <summary>Stars of the unmodified chart.</summary>
    public double? Stars { get; init; }

    /// <summary>
    /// Stars under <see cref="Modifier"/> (the speed modifier's own rating), or <see cref="Stars"/> when
    /// unmodified or when the modifier isn't rated.
    /// </summary>
    public double? ModifierStars { get; init; }

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
