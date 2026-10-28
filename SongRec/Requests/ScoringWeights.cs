namespace beatleader_songrec.Requests;

/// <summary>
/// User-configurable weights (0-100 each) controlling how much each scoring factor contributes
/// to a candidate's final score. Out-of-range values are clamped rather than rejected, as they
/// come from web input.
/// </summary>
public sealed class ScoringWeights
{
    private readonly double _style = 50;
    private readonly double _easier = 0;

    /// <summary>
    /// Strength of the Style factor (share of origin-song links pointing at a candidate, relative
    /// to the whole pool). 0 makes it neutral; 100 applies its full effect.
    /// </summary>
    public double Style
    {
        get => _style;
        init => _style = Clamp(value);
    }

    /// <summary>
    /// Strength of the Easier factor (maps under the player's level favored over maps above it,
    /// see <see cref="Core.SongScorer"/>). 0 makes it neutral; 100 applies its full effect.
    /// </summary>
    public double Easier
    {
        get => _easier;
        init => _easier = Clamp(value);
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 100);
}
