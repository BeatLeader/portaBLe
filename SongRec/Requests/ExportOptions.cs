namespace beatleader_songrec.Requests;

/// <summary>
/// User-configurable options for exporting the recommendation results to a .bplist file.
/// </summary>
public sealed class ExportOptions
{
    /// <summary>Title shown for the playlist in-game.</summary>
    public string PlaylistTitle { get; init; } = "BeatLeader Song Recommendations";

    /// <summary>Author attribution shown for the playlist in-game.</summary>
    public string PlaylistAuthor { get; init; } = "beatleader-songrec";

    /// <summary>
    /// Path to a square cover image (PNG or JPEG) to embed in the playlist. Defaults to the
    /// bundled placeholder cover under Assets/.
    /// </summary>
    public string ImagePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "SongRec", "Assets", "playlist-cover.png");

    /// <summary>Path the .bplist file should be written to.</summary>
    public string OutputPath { get; init; } = "playlist.bplist";

    /// <summary>
    /// Optional path for an .html report explaining why each song was recommended (final score,
    /// factor subscores, and the linked players/scores that contributed to it). No report is
    /// written when this is null or empty.
    /// </summary>
    public string? HtmlReportPath { get; init; }
}
