namespace beatleader_songrec.Models;

/// <summary>
/// A player discovered to share at least one seed song with the target player, along with
/// the seed <see cref="Song"/>s that established the link.
/// </summary>
public sealed class LinkedPlayer
{
    public required Player Player { get; init; }

    /// <summary>
    /// The seed songs (from the target player's top plays) on which this player was also
    /// found among the leaderboard's top scorers.
    /// </summary>
    public required IReadOnlyList<Song> SharedSeedSongs { get; init; }
}
