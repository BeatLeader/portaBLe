namespace beatleader_songrec.Models;

/// <summary>
/// Convenience helpers for working with <see cref="Score"/> instances.
/// </summary>
public static class ScoreExtensions
{
    /// <summary>
    /// Gets the speed modifier (SS/FS/SFS) applied to this score, parsed from
    /// <see cref="Score.Modifiers"/>.
    /// </summary>
    public static SpeedModifier GetSpeedModifier(this Score score) =>
        SpeedModifierExtensions.ParseSpeedModifier(score.Modifiers);

    /// <summary>
    /// Gets the effective <see cref="Song"/> entity this score should be linked to. Since
    /// <see cref="Score.Song"/> already carries the score's speed modifier as part of its
    /// identity (see <see cref="Song.Modifier"/>), this simply returns that song - a
    /// speed-modified play never links to the unmodified song's candidate pool entry.
    /// </summary>
    public static Song GetEffectiveSong(this Score score) => score.Song;
}
