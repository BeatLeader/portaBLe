namespace beatleader_songrec.Models;

/// <summary>
/// Speed-altering score modifiers. Beat Saber treats a speed-modified play as effectively a
/// different chart, so a song played with a speed modifier is treated as a distinct entity
/// from its unmodified version throughout the recommendation pipeline.
/// </summary>
public enum SpeedModifier
{
    /// <summary>No speed modifier applied.</summary>
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
