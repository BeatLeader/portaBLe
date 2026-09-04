namespace beatleader_songrec.Models;

/// <summary>
/// A Beat Saber player, as needed by the recommendation pipeline.
/// </summary>
public sealed class Player
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? AvatarUrl { get; init; }

    public string? Country { get; init; }

    public double Pp { get; init; }

    public int Rank { get; init; }

    public int CountryRank { get; init; }
}
