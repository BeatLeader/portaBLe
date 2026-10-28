namespace beatleader_songrec.Models;

/// <summary>
/// Speed-altering score modifiers. A speed-modified play is effectively a different chart, so the
/// pipeline treats it as a distinct song from the unmodified version.
/// </summary>
public enum SpeedModifier
{
    None,

    /// <summary>Slower Song (0.8x speed).</summary>
    SlowerSong,

    /// <summary>Faster Song (1.2x speed).</summary>
    FasterSong,

    /// <summary>Super Faster Song (1.5x speed).</summary>
    SuperFasterSong
}

/// <summary>
/// Helpers for parsing <see cref="SpeedModifier"/> from BeatLeader's modifier strings.
/// </summary>
public static class SpeedModifierExtensions
{
    /// <summary>
    /// Parses the speed modifier out of a BeatLeader modifiers string (e.g. "SS", "DA,FS",
    /// "SF,GN"). Super Faster Song is stored as "SF" (see portaBLe's ScoreModifierFilter); "SFS"
    /// is accepted too. Returns <see cref="SpeedModifier.None"/> when no speed modifier is present.
    /// </summary>
    public static SpeedModifier ParseSpeedModifier(string? modifiers)
    {
        if (string.IsNullOrWhiteSpace(modifiers))
        {
            return SpeedModifier.None;
        }

        var tokens = modifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Whole tokens only, so "SF" isn't mistaken for "SS"/"FS".
        if (tokens.Any(t => t.Equals("SF", StringComparison.OrdinalIgnoreCase) || t.Equals("SFS", StringComparison.OrdinalIgnoreCase)))
        {
            return SpeedModifier.SuperFasterSong;
        }

        if (tokens.Any(t => t.Equals("FS", StringComparison.OrdinalIgnoreCase)))
        {
            return SpeedModifier.FasterSong;
        }

        if (tokens.Any(t => t.Equals("SS", StringComparison.OrdinalIgnoreCase)))
        {
            return SpeedModifier.SlowerSong;
        }

        return SpeedModifier.None;
    }
}
