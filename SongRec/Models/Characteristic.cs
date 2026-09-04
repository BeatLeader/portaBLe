namespace beatleader_songrec.Models;

/// <summary>
/// Beat Saber game mode characteristics, as encoded by the single digit used in BeatLeader
/// leaderboard ids (e.g. the trailing "1" in "24d9bx91"). Only Standard and OneSaber are
/// currently rankable.
/// </summary>
public enum Characteristic
{
    Standard = 1,
    OneSaber = 2
}

/// <summary>
/// Helpers for converting <see cref="Characteristic"/> to/from the digit used in leaderboard ids.
/// </summary>
public static class CharacteristicExtensions
{
    public static char ToLeaderboardDigit(this Characteristic characteristic) =>
        characteristic switch
        {
            Characteristic.Standard => '1',
            Characteristic.OneSaber => '2',
            _ => throw new ArgumentOutOfRangeException(nameof(characteristic), characteristic, "Unknown characteristic.")
        };
}
