using System.Text;
using beatleader_songrec.Core;
using beatleader_songrec.Models;
using beatleader_songrec.Requests;

namespace beatleader_songrec.Export;

/// <summary>
/// Writes a human-readable .html report explaining why each recommended song was selected:
/// its final score, the individual factor subscores, and the linked players (with their own
/// score/pp/rank) that contributed to the candidate's aggregation.
/// </summary>
public interface IReportWriter
{
    /// <summary>
    /// Builds an .html report for <paramref name="scoredSongs"/> and writes it to
    /// <see cref="ExportOptions.HtmlReportPath"/>. Does nothing if that path is null/empty.
    /// </summary>
    /// <param name="scoredSongs">The scored/ranked recommendation results.</param>
    /// <param name="candidates">
    /// The full candidate pool (song -> linked players/scores) produced by
    /// <see cref="ILinkedPlayerFinder"/>, used to explain each recommendation's linked players.
    /// </param>
    /// <param name="options">Export options, including the report's output path.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task WriteAsync(
        IReadOnlyList<ScoredSong> scoredSongs,
        IReadOnlyDictionary<Song, List<CandidateEntry>> candidates,
        ExportOptions options,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IReportWriter"/>
public sealed class HtmlReportWriter : IReportWriter
{
    public async Task WriteAsync(
        IReadOnlyList<ScoredSong> scoredSongs,
        IReadOnlyDictionary<Song, List<CandidateEntry>> candidates,
        ExportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scoredSongs);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.HtmlReportPath))
        {
            return;
        }

        var html = Build(scoredSongs, candidates, options);

        var directory = Path.GetDirectoryName(options.HtmlReportPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(options.HtmlReportPath, html, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private static string Build(
        IReadOnlyList<ScoredSong> scoredSongs,
        IReadOnlyDictionary<Song, List<CandidateEntry>> candidates,
        ExportOptions options)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine($"<title>{Escape(options.PlaylistTitle)} - Recommendation Report</title>");
        sb.AppendLine("""
            <style>
                body { font-family: Segoe UI, Arial, sans-serif; margin: 2rem; background: #121212; color: #e0e0e0; }
                h1 { margin-bottom: 0.25rem; }
                .subtitle { color: #9a9a9a; margin-top: 0; }
                table.songs { border-collapse: collapse; width: 100%; margin-top: 1.5rem; }
                table.songs > thead > tr { background: #1f1f1f; }
                table.songs td, table.songs th { padding: 0.5rem 0.75rem; border-bottom: 1px solid #333; text-align: left; }
                table.songs > tbody > tr:hover { background: #1a1a1a; }
                .rank { color: #9a9a9a; width: 2.5rem; }
                .score { font-weight: bold; color: #6fc2ff; }
                details { margin: 0; }
                summary { cursor: pointer; }
                table.linked { border-collapse: collapse; margin: 0.5rem 0 0.5rem 1.5rem; }
                table.linked td, table.linked th { padding: 0.25rem 0.6rem; border-bottom: 1px solid #2a2a2a; font-size: 0.9rem; }
                .factor { display: inline-block; min-width: 6rem; }
                .bar-bg { display: inline-block; width: 100px; height: 8px; background: #2a2a2a; border-radius: 4px; vertical-align: middle; margin-right: 0.4rem; }
                .bar-fill { display: block; height: 8px; background: #6fc2ff; border-radius: 4px; }
            </style>
            """);
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine($"<h1>{Escape(options.PlaylistTitle)}</h1>");
        sb.AppendLine($"<p class=\"subtitle\">Recommendation report generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm} - {scoredSongs.Count} songs</p>");

        sb.AppendLine("<table class=\"songs\">");
        sb.AppendLine("<thead><tr><th>#</th><th>Song</th><th>Difficulty</th><th>Stars</th><th>Score</th><th>Distance</th><th>Style</th><th>Overweight</th><th>Linked players</th></tr></thead>");
        sb.AppendLine("<tbody>");

        for (var i = 0; i < scoredSongs.Count; i++)
        {
            var scoredSong = scoredSongs[i];
            var song = scoredSong.Song;
            candidates.TryGetValue(song, out var entries);
            entries ??= [];

            sb.AppendLine("<tr>");
            sb.AppendLine($"<td class=\"rank\">{i + 1}</td>");
            sb.AppendLine($"<td>{Escape(song.Name)}{FormatModifier(song.Modifier)}</td>");
            sb.AppendLine($"<td>{Escape(song.DifficultyName)} {Escape(song.ModeName)}</td>");
            sb.AppendLine($"<td>{(song.Stars.HasValue ? $"{song.Stars.Value:0.00}★" : "-")}</td>");
            sb.AppendLine($"<td class=\"score\">{scoredSong.Score:0.00}</td>");
            sb.AppendLine($"<td>{Bar(scoredSong.DistanceScore)}</td>");
            sb.AppendLine($"<td>{Bar(scoredSong.StyleScore)}</td>");
            sb.AppendLine($"<td>{Bar(scoredSong.OverweightScore)}</td>");
            sb.AppendLine("<td>");
            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>{scoredSong.LinkedPlayerCount} player(s), avg PP {scoredSong.AveragePp:0.0}, avg rank {scoredSong.AverageRank:0.0}</summary>");
            sb.AppendLine("<table class=\"linked\">");
            sb.AppendLine("<thead><tr><th>Player</th><th>PP</th><th>Rank</th><th>Accuracy</th></tr></thead>");
            sb.AppendLine("<tbody>");

            // A single player can contribute multiple raw links to the same candidate (one per
            // seed song they share with the target player) - de-duplicate by player here so the
            // report lists each linked player only once.
            var distinctEntries = entries
                .GroupBy(e => e.LinkedPlayer.Player.Id)
                .Select(g => g.OrderBy(e => e.Score.Rank).First());

            foreach (var entry in distinctEntries.OrderBy(e => e.Score.Rank))
            {
                sb.AppendLine("<tr>");
                sb.AppendLine($"<td>{Escape(entry.LinkedPlayer.Player.Name)}</td>");
                sb.AppendLine($"<td>{entry.Score.Pp:0.0}</td>");
                sb.AppendLine($"<td>{entry.Score.Rank}</td>");
                sb.AppendLine($"<td>{entry.Score.Accuracy:0.00}%</td>");
                sb.AppendLine("</tr>");
            }

            sb.AppendLine("</tbody></table>");
            sb.AppendLine("</details>");
            sb.AppendLine("</td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</tbody>");
        sb.AppendLine("</table>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private static string FormatModifier(SpeedModifier modifier) =>
        modifier == SpeedModifier.None ? string.Empty : $" <em>({modifier})</em>";

    private static string Bar(double normalizedScore)
    {
        var clamped = Math.Clamp(normalizedScore, 0d, 1d);
        var width = (int)Math.Round(clamped * 100);
        return $"<span class=\"bar-bg\"><span class=\"bar-fill\" style=\"width:{width}px\"></span></span>{clamped:0.00}";
    }

    private static string Escape(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : System.Net.WebUtility.HtmlEncode(value);
}
