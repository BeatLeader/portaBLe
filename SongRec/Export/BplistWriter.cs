using System.Text.Json;
using beatleader_songrec.Models;
using beatleader_songrec.Requests;

namespace beatleader_songrec.Export;

/// <summary>
/// Builds and writes .bplist playlist files from scored song recommendations.
/// </summary>
public interface IBplistWriter
{
    /// <summary>
    /// Builds a .bplist file from the given scored songs and writes it to
    /// <see cref="ExportOptions.OutputPath"/>.
    /// </summary>
    Task WriteAsync(IReadOnlyList<ScoredSong> scoredSongs, ExportOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a .bplist file from the given scored songs and returns its raw UTF-8 JSON bytes,
    /// without writing anything to disk. Useful for serving the playlist as a web download.
    /// </summary>
    byte[] BuildBytes(IReadOnlyList<ScoredSong> scoredSongs, ExportOptions options);
}

/// <inheritdoc cref="IBplistWriter"/>
public sealed class BplistWriter : IBplistWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    public async Task WriteAsync(IReadOnlyList<ScoredSong> scoredSongs, ExportOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scoredSongs);
        ArgumentNullException.ThrowIfNull(options);

        var bplist = Build(scoredSongs, options);

        var directory = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(options.OutputPath);
        await JsonSerializer.SerializeAsync(stream, bplist, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public byte[] BuildBytes(IReadOnlyList<ScoredSong> scoredSongs, ExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(scoredSongs);
        ArgumentNullException.ThrowIfNull(options);

        var bplist = Build(scoredSongs, options);
        return JsonSerializer.SerializeToUtf8Bytes(bplist, SerializerOptions);
    }

    /// <summary>
    /// Builds the in-memory .bplist representation, grouping candidate songs by hash so a
    /// song with multiple recommended difficulties appears once with all its difficulties listed.
    /// </summary>
    private static BplistFile Build(IReadOnlyList<ScoredSong> scoredSongs, ExportOptions options)
    {
        var bplist = new BplistFile
        {
            PlaylistTitle = options.PlaylistTitle,
            PlaylistAuthor = options.PlaylistAuthor,
            CustomData = new BplistCustomData
            {
                SyncUrl = null,
                Owner = null,
                Id = null,
                Hash = null,
                Shared = false
            }
        };

        if (!string.IsNullOrEmpty(options.ImagePath) && File.Exists(options.ImagePath))
        {
            bplist.Image = ImageUtils.ToSquareImageDataUri(options.ImagePath);
        }

        // Preserve the scored/ranked order of first appearance, but merge difficulties for
        // songs that share the same hash (e.g. a song recommended at multiple difficulties).
        var songsByHash = new Dictionary<string, BplistSong>();
        var orderedHashes = new List<string>();

        foreach (var scoredSong in scoredSongs)
        {
            var song = scoredSong.Song;
            if (string.IsNullOrEmpty(song.Hash))
            {
                continue;
            }

            if (!songsByHash.TryGetValue(song.Hash, out var bplistSong))
            {
                bplistSong = new BplistSong
                {
                    Hash = song.Hash,
                    Key = null,
                    SongName = song.Name,
                    LevelAuthorName = song.Mapper ?? song.Author ?? string.Empty
                };
                songsByHash[song.Hash] = bplistSong;
                orderedHashes.Add(song.Hash);
            }

            var difficultyName = MapDifficultyName(song.DifficultyName);
            var characteristic = song.ModeName ?? "Standard";

            if (!bplistSong.Difficulties.Any(d => d.Name == difficultyName && d.Characteristic == characteristic))
            {
                bplistSong.Difficulties.Add(new BplistDifficulty
                {
                    Name = difficultyName,
                    Characteristic = characteristic
                });
            }
        }

        bplist.Songs = orderedHashes.Select(hash => songsByHash[hash]).ToList();

        return bplist;
    }

    /// <summary>
    /// Maps a BeatLeader difficulty name (e.g. "ExpertPlus") to the lowerCamelCase form used
    /// by .bplist files (e.g. "expertPlus").
    /// </summary>
    private static string MapDifficultyName(string? difficultyName)
    {
        if (string.IsNullOrEmpty(difficultyName))
        {
            return string.Empty;
        }

        return char.ToLowerInvariant(difficultyName[0]) + difficultyName[1..];
    }
}
