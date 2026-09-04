namespace beatleader_songrec.Requests;

/// <summary>
/// User-configurable weights (0-100 each) controlling how much each scoring factor
/// contributes to a candidate song's final recommendation score. Values outside [0,100]
/// are clamped rather than throwing, since this will eventually be populated from
/// external/web input.
/// </summary>
public sealed class ScoringWeights
{
    private readonly double _style = 50;
    private readonly double _overweight = 50;

    /// <summary>
    /// Weight (0-100) controlling how strongly the Style factor (proportional share of
    /// origin-song links pointing at a candidate, relative to the whole pool) influences the
    /// final multiplicative score. 0 makes Style neutral; 100 applies its full effect.
    /// </summary>
    public double Style
    {
        get => _style;
        init => _style = Clamp(value);
    }

    /// <summary>
    /// Weight (0-100) controlling how strongly the Overweight factor (average rank among
    /// linked players, better/lower rank favored) influences the final multiplicative score.
    /// 0 makes Overweight neutral; 100 applies its full effect.
    /// </summary>
    public double Overweight
    {
        get => _overweight;
        init => _overweight = Clamp(value);
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 100);
}
