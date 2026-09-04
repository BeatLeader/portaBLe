namespace beatleader_songrec.Models;

/// <summary>
/// Beat Saber difficulty levels, as encoded by the single digit used in BeatLeader
/// leaderboard ids (e.g. the "9" in "24d9bx91").
/// </summary>
public enum Difficulty
{
    Easy = 1,
    Normal = 3,
    Hard = 5,
    Expert = 7,
    ExpertPlus = 9
}

/// <summary>
/// Helpers for converting <see cref="Difficulty"/> to/from the digit used in leaderboard ids.
/// </summary>
public static class DifficultyExtensions
{
    public static char ToLeaderboardDigit(this Difficulty difficulty) =>
        difficulty switch
        {
            Difficulty.Easy => '1',
            Difficulty.Normal => '3',
            Difficulty.Hard => '5',
            Difficulty.Expert => '7',
            Difficulty.ExpertPlus => '9',
            _ => throw new ArgumentOutOfRangeException(nameof(difficulty), difficulty, "Unknown difficulty.")
        };
}
