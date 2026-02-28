using Microsoft.EntityFrameworkCore;
using portaBLe.MapRecommendation;
using System.Text.Encodings.Web;
using System.Text.Json;

internal class CandidateLeaderboard
{
    public string Id { get; set; }
    public string Name { get; set; }
    public float Stars { get; set; }
    public float AccRating { get; set; }
    public float TechRating { get; set; }
    public float PassRating { get; set; }
    public string Hash { get; set; }
    public string Mapper { get; set; }
    public string DifficultyName { get; set; }
    public string ModeName { get; set; }
}

internal class ScoredLeaderboard
{
    public string Id { get; set; }
    public string Name { get; set; }
    public float FinalScore { get; set; }
    public string Hash { get; set; }
    public string Mapper { get; set; }
    public string DifficultyName { get; set; }
    public string ModeName { get; set; }
}

internal record NormalizedRatings(float Acc, float Tech, float Pass);

internal static class StringExtensions
{
    public static string LowercaseFirstChar(this string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return char.ToLower(input[0]) + input.Substring(1);
    }
}

public class RecommendationService
{
    // Returns top N maps predicted for playerId and exports as JSON playlist
    public static async void GetRecommendationsAsync(
        portaBLe.AppContext _db,
        string playerId,
        List<string> selectedMaps,
        int mapCount = 20,
        bool unplayed = true)
    {
        // 1) Load selected maps
        var maps = await _db.Leaderboards
            .Where(x => selectedMaps.Contains(x.Id))
            .ToListAsync();

        if (maps.Count == 0)
        {
            Console.WriteLine("No selected maps found in the database.");
            return;
        }

        // 2) Maps the player already played
        List<string> playedMapIds = new List<string>();
        if (unplayed)
        {
            playedMapIds = await _db.Scores
                .Where(s => s.PlayerId == playerId)
                .Select(s => s.Leaderboard.Id)
                .Distinct()
                .ToListAsync();
        }

        // TODO: Add date filter (ie. remove old maps from candidate)
        // 3) Candidate leaderboards
        var candidates = await _db.Leaderboards
            .Where(lb => !playedMapIds.Contains(lb.Id))
            .Select(lb => new CandidateLeaderboard
            {
                Id = lb.Id,
                Name = lb.Name,
                Stars = lb.Stars,
                AccRating = lb.AccRating,
                TechRating = lb.TechRating,
                PassRating = lb.PassRating,
                Hash = lb.Hash,
                Mapper = lb.Mapper,
                DifficultyName = lb.DifficultyName,
                ModeName = lb.ModeName
            })
            .ToListAsync();

        // 4) Score candidates
        const float maxRating = 15.0f;
        const float initialTolerance = 0.15f;
        const float toleranceIncrement = 0.05f;
        const float maxTolerance = 0.50f;

        var scored = ScoreCandidatesWithAdaptiveTolerance(
            candidates, maps, mapCount,
            initialTolerance, toleranceIncrement, maxTolerance, maxRating);

        if (scored.Count == 0)
        {
            Console.WriteLine("No recommendations could be generated.");
            return;
        }

        // Build songs list from scored recommendations
        var songs = scored
            .Select(item => new Song
            {
                hash = item.Hash,
                songName = item.Name,
                levelAuthorName = item.Mapper,
                levelid = "custom_level_" + item.Hash,
                difficulties = new Difficulty[]
                {
                    new Difficulty
                    {
                        name = item.DifficultyName.LowercaseFirstChar(),
                        characteristic = item.ModeName
                    }
                }
            })
            .ToArray();

        // Create playlist object
        var playlist = new Playlist
        {
            playlistTitle = $"Recommendations for {playerId}",
            playlistAuthor = "BeatLeader",
            songs = songs,
            customData = new Customdata
            {
                syncURL = "",
                owner = playerId,
                id = "",
                hash = "",
                shared = false
            },
            image = ""
        };

        await ExportPlaylistToJsonAsync(playlist, playerId);
    }

    private static async Task ExportPlaylistToJsonAsync(Playlist playlist, string playerId)
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            string json = JsonSerializer.Serialize(playlist, options);

            string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recommendations");
            Directory.CreateDirectory(outputDir);

            string filename = Path.Combine(outputDir, $"recommendation_{playerId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");

            await File.WriteAllTextAsync(filename, json);
            Console.WriteLine($"Playlist exported to: {filename}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error exporting playlist: {ex.Message}");
        }
    }

    private static List<ScoredLeaderboard> ScoreCandidatesWithAdaptiveTolerance(
        List<CandidateLeaderboard> candidates,
        List<portaBLe.DB.Leaderboard> maps,
        int mapCount,
        float initialTolerance,
        float toleranceIncrement,
        float maxTolerance,
        float maxRating)
    {
        // Pre-compute normalized ratings for selected maps once — they don't change between retries
        var normalizedMaps = maps
            .Select(m => new NormalizedRatings(
                Acc: m.AccRating / maxRating,
                Tech: m.TechRating / maxRating,
                Pass: m.PassRating / maxRating))
            .ToList();

        // Pre-compute normalized ratings for each candidate once as well
        var normalizedCandidates = candidates
            .Select(lb => (
                Candidate: lb,
                Ratings: new NormalizedRatings(
                    Acc: lb.AccRating / maxRating,
                    Tech: lb.TechRating / maxRating,
                    Pass: lb.PassRating / maxRating)))
            .ToList();

        float currentTolerance = initialTolerance;
        List<ScoredLeaderboard> bestResults = new List<ScoredLeaderboard>();

        while (currentTolerance <= maxTolerance)
        {
            var scored = normalizedCandidates
                .Select(entry =>
                {
                    var (lb, lbRatings) = entry;
                    float bestSimilarityScore = 0.0f;
                    int matchCount = 0;

                    foreach (var mapRatings in normalizedMaps)
                    {
                        float accDiff = Math.Abs(lbRatings.Acc - mapRatings.Acc);
                        float techDiff = Math.Abs(lbRatings.Tech - mapRatings.Tech);
                        float passDiff = Math.Abs(lbRatings.Pass - mapRatings.Pass);

                        bool accSimilar = accDiff <= currentTolerance;
                        bool techSimilar = techDiff <= currentTolerance;
                        bool passSimilar = passDiff <= currentTolerance;

                        int similarityCount = (accSimilar ? 1 : 0)
                                            + (techSimilar ? 1 : 0)
                                            + (passSimilar ? 1 : 0);

                        if (similarityCount >= 2)
                        {
                            // Divide by number of matched dimensions, not a fixed 3,
                            // so partial matches aren't unfairly penalized
                            float weightedDiffSum = accDiff + techDiff + passDiff;
                            float similarityScore = 1.0f - (weightedDiffSum / similarityCount);

                            if (similarityScore > bestSimilarityScore)
                                bestSimilarityScore = similarityScore;

                            matchCount++;
                        }
                    }

                    if (matchCount > 0)
                    {
                        return new ScoredLeaderboard
                        {
                            Id = lb.Id,
                            Name = lb.Name,
                            FinalScore = bestSimilarityScore,
                            Hash = lb.Hash,
                            Mapper = lb.Mapper,
                            DifficultyName = lb.DifficultyName,
                            ModeName = lb.ModeName
                        };
                    }

                    return null;
                })
                .OfType<ScoredLeaderboard>()           // Cleaner null removal than Where(x => x != null)
                .OrderByDescending(x => x.FinalScore)
                .Take(mapCount)
                .ToList();

            // Keep the best result set seen so far across all tolerance levels,
            // so we don't silently discard e.g. 18 results found at a lower tolerance
            // when a higher tolerance only yields 15.
            if (scored.Count > bestResults.Count)
                bestResults = scored;

            if (scored.Count >= mapCount)
            {
                Console.WriteLine($"Found {scored.Count} recommendations with tolerance {currentTolerance:F2}");
                return scored;
            }

            currentTolerance += toleranceIncrement;
        }

        if (bestResults.Count > 0)
        {
            Console.WriteLine($"Found {bestResults.Count} recommendations (less than {mapCount} requested) at best tolerance reached.");
            return bestResults;
        }

        Console.WriteLine($"No recommendations found even at max tolerance {maxTolerance:F2}");
        return new List<ScoredLeaderboard>();
    }
}