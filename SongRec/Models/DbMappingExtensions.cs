using DbLeaderboard = portaBLe.DB.Leaderboard;
using DbPlayer = portaBLe.DB.Player;
using DbScore = portaBLe.DB.Score;

namespace beatleader_songrec.Models;

/// <summary>
/// Maps portaBLe's local EF Core database entities onto the domain models used by the
/// recommendation pipeline (replacing the original BeatLeader-API-DTO-based mapping).
/// </summary>
public static class DbMappingExtensions
{
    public static Player ToModel(this DbPlayer player) => new()
    {
        Id = player.Id,
        Name = player.Name,
        AvatarUrl = player.Avatar,
        Country = player.Country,
        Pp = player.Pp,
        Rank = player.Rank,
        CountryRank = player.CountryRank
    };

    public static Song ToModel(this DbLeaderboard leaderboard, SpeedModifier modifier = SpeedModifier.None) => new()
    {
        LeaderboardId = leaderboard.Id,
        Modifier = modifier,
        Hash = leaderboard.Hash ?? string.Empty,
        Name = leaderboard.Name ?? string.Empty,
        Author = null,
        Mapper = leaderboard.Mapper,
        CoverImageUrl = leaderboard.Cover,
        DifficultyName = leaderboard.DifficultyName,
        ModeName = leaderboard.ModeName,
        Stars = leaderboard.Stars
    };

    /// <summary>
    /// Maps a DB score row to the domain model. The associated <see cref="Song"/> is built from
    /// the row's related <see cref="DbLeaderboard"/> when loaded, and the song's identity
    /// incorporates the score's speed modifier (SS/FS/SFS), if any, treating a speed-modified
    /// play as a distinct song entity from the unmodified chart.
    /// </summary>
    public static Score ToModel(this DbScore score)
    {
        var modifier = SpeedModifierExtensions.ParseSpeedModifier(score.Modifiers);

        var song = score.Leaderboard is not null
            ? score.Leaderboard.ToModel(modifier)
            : new Song
            {
                LeaderboardId = score.LeaderboardId ?? string.Empty,
                Modifier = modifier,
                Hash = string.Empty,
                Name = string.Empty
            };

        return new Score
        {
            Id = score.Id,
            PlayerId = score.PlayerId ?? score.Player?.Id ?? string.Empty,
            Player = score.Player?.ToModel(),
            Song = song,
            BaseScore = 0,
            ModifiedScore = 0,
            Accuracy = score.Accuracy,
            Pp = score.Pp,
            Rank = score.Rank,
            Modifiers = score.Modifiers,
            SetAt = ParseUnixTimestamp(score.Timepost)
        };
    }

    private static DateTimeOffset? ParseUnixTimestamp(int timepost) =>
        timepost > 0 ? DateTimeOffset.FromUnixTimeSeconds(timepost) : null;
}
