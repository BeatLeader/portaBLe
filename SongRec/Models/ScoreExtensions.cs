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
    /// The <see cref="Song"/> this score links to: <see cref="Score.Song"/>, which already carries
    /// the speed modifier in its identity, so a speed-modified play is its own candidate.
    /// </summary>
    public static Song GetEffectiveSong(this Score score) => score.Song;
}
