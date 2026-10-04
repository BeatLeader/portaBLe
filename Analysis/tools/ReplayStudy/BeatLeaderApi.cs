using System.Text.Json;

namespace ReplayStudy
{
    public class ScoreInfo
    {
        public int Id { get; set; }
        public string PlayerId { get; set; } = "";
        public int Rank { get; set; }
        public float Accuracy { get; set; }
        public string Modifiers { get; set; } = "";
        public string Replay { get; set; } = "";
        public int Timepost { get; set; }
        public int BadCuts { get; set; }
        public int MissedNotes { get; set; }
        public int BombCuts { get; set; }
        public int WallsHit { get; set; }
        public int Pauses { get; set; }
        public int MaxCombo { get; set; }
        public float FcAccuracy { get; set; }
        public bool FullCombo { get; set; }
        public string Stratum { get; set; } = "";
        public string Context { get; set; } = "general";
    }

    /// <summary>Thin BeatLeader API client. Point --api at http://127.0.0.1:5000 on the server to bypass the CDN.</summary>
    public class BeatLeaderApi
    {
        private readonly HttpClient _client;
        private readonly string _baseUrl;

        public BeatLeaderApi(string baseUrl)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            _client.DefaultRequestHeaders.Add("User-Agent", "ReplayStudy/1.0 (BeatLeader difficulty-model validation)");
        }

        private async Task<string?> GetString(string url)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    var response = await _client.GetAsync(url);
                    if (response.IsSuccessStatusCode) return await response.Content.ReadAsStringAsync();
                    if ((int)response.StatusCode == 404) return null;
                    await Task.Delay(TimeSpan.FromSeconds(3 * (attempt + 1)));
                }
                catch (Exception) { await Task.Delay(TimeSpan.FromSeconds(3 * (attempt + 1))); }
            }
            return null;
        }

        /// <summary>One page of scores. Returns (scores, total score count for the context).</summary>
        public async Task<(List<ScoreInfo> scores, int total)> GetScores(string leaderboardId, string context, int count, int page)
        {
            var url = $"{_baseUrl}/leaderboard/scores/{leaderboardId}?count={count}&page={page}&sortBy=rank&leaderboardContext={context}";
            var json = await GetString(url);
            var result = new List<ScoreInfo>();
            if (json == null) return (result, 0);

            using var doc = JsonDocument.Parse(json);
            int total = 0;
            if (doc.RootElement.TryGetProperty("plays", out var plays) && plays.ValueKind == JsonValueKind.Number)
                total = plays.GetInt32();
            if (!doc.RootElement.TryGetProperty("scores", out var scores)) return (result, total);

            foreach (var item in scores.EnumerateArray())
            {
                var player = item.GetProperty("player");
                result.Add(new ScoreInfo
                {
                    Id = item.GetProperty("id").GetInt32(),
                    PlayerId = player.GetProperty("id").GetString() ?? "",
                    Rank = item.GetProperty("rank").GetInt32(),
                    Accuracy = item.GetProperty("accuracy").GetSingle(),
                    Modifiers = item.GetProperty("modifiers").GetString() ?? "",
                    Replay = item.GetProperty("replay").GetString() ?? "",
                    Timepost = Int(item, "timepost"),
                    BadCuts = Int(item, "badCuts"),
                    MissedNotes = Int(item, "missedNotes"),
                    BombCuts = Int(item, "bombCuts"),
                    WallsHit = Int(item, "wallsHit"),
                    Pauses = Int(item, "pauses"),
                    MaxCombo = Int(item, "maxCombo"),
                    FcAccuracy = item.TryGetProperty("fcAccuracy", out var fa) && fa.ValueKind == JsonValueKind.Number ? fa.GetSingle() : 0,
                    FullCombo = item.TryGetProperty("fullCombo", out var fc) && fc.ValueKind == JsonValueKind.True,
                    Context = context.ToLowerInvariant(),
                });
            }
            return (result, total);
        }

        private static int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        public async Task<byte[]?> DownloadReplay(string url)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { return await _client.GetByteArrayAsync(url); }
                catch (Exception) { await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1))); }
            }
            return null;
        }
    }
}
