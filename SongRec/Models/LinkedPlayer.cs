namespace beatleader_songrec.Models;

/// <summary>
/// A player discovered to share at least one seed song with the target player, along with
/// the seed <see cref="Song"/>s that established the link.
/// </summary>
public sealed class LinkedPlayer
{
    public required Player Player { get; init; }

    /// <summary>
    /// The seed songs on which this player was found as a peer of the target player.
    /// </summary>
    public required IReadOnlyList<Song> SharedSeedSongs { get; init; }
}
