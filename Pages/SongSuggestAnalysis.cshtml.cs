using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using portaBLe.MapRecommendation.Ranked;

namespace portaBLe.Pages
{
    public class SongSuggestAnalysisModel : PageModel
    {
        private readonly AppContext _context;
        private readonly SongSuggestDataService _dataService;

        public SongSuggestAnalysisModel(AppContext context, SongSuggestDataService dataService)
        {
            _context = context;
            _dataService = dataService;
        }

        public string ActiveTab { get; set; } = "playlist";
        public string SongID { get; set; }
        public string SongSearchQuery { get; set; }
        public string ErrorMessage { get; set; }
        public bool HasData => _dataService.Data != null;

        // Song search results for songlinks/leaderboard tabs
        public List<SongIdOption> SongSearchResults { get; set; } = new();

        // Playlist Suggestions
        public List<PlaylistSuggestionItem> PlaylistSuggestions { get; set; } = new();

        // Origin Leaderboards
        public List<OriginSongItem> OriginSongs { get; set; } = new();

        // Origin Filtering
        public OriginFilteringData OriginFiltering { get; set; }

        // Song Link Details (for a single song)
        public List<SongLinkDetailItem> SongLinkDetails { get; set; } = new();
        public string SongLinkSongName { get; set; }

        // All Song Links summary
        public List<SongLinkSummaryItem> AllSongLinksSummary { get; set; } = new();

        // Leaderboard Details (players on a song)
        public List<LeaderboardPlayerItem> LeaderboardPlayers { get; set; } = new();
        public string LeaderboardSongName { get; set; }

        // Leaderboard Links
        public LeaderboardLinksData LeaderboardLinks { get; set; }

        // Available target song IDs for dropdowns
        public List<SongIdOption> AvailableTargetSongs { get; set; } = new();
        public List<SongIdOption> AvailableLeaderboardSongs { get; set; } = new();

        public IActionResult OnGet(string tab = "playlist", string songId = null, string songSearch = null)
        {
            ActiveTab = tab;
            SongID = songId;
            SongSearchQuery = songSearch;

            if (!HasData)
            {
                ErrorMessage = "Song suggestion data has not been computed yet. The analysis runs at startup in Program.cs.";
                return Page();
            }

            var data = _dataService.Data;

            // If a text search was provided but no songId, try to resolve it
            if (string.IsNullOrEmpty(songId) && !string.IsNullOrEmpty(songSearch) && (tab == "songlinks" || tab == "leaderboard"))
            {
                var resolved = ResolveSongSearch(songSearch, data, tab);
                if (resolved != null)
                {
                    SongID = resolved;
                }
                else
                {
                    // Show search results
                    BuildSongSearchResults(songSearch, data, tab);
                }
            }

            // Build dropdown options
            BuildDropdownOptions(data);

            switch (tab)
            {
                case "playlist":
                    BuildPlaylistSuggestions(data);
                    break;
                case "origins":
                    BuildOriginSongs(data);
                    break;
                case "filtering":
                    BuildOriginFiltering(data);
                    break;
                case "songlinks":
                    if (!string.IsNullOrEmpty(SongID))
                        BuildSongLinkDetails(data, SongID);
                    else if (SongSearchResults.Count == 0)
                        BuildAllSongLinksSummary(data);
                    break;
                case "leaderboard":
                    if (!string.IsNullOrEmpty(SongID))
                        BuildLeaderboardDetails(data, SongID);
                    break;
                case "links":
                    BuildLeaderboardLinks(data);
                    break;
            }

            return Page();
        }

        private string ResolveSongSearch(string query, SongSuggestData data, string tab)
        {
            query = query.Trim();

            // Check if it's a direct ID match
            if (tab == "songlinks" && data.targetLeaderboards?.endPoints?.ContainsKey(query) == true)
                return query;
            if (tab == "leaderboard" && data.leaderboards?.top10kLeaderboardMeta?.ContainsKey(query) == true)
                return query;

            // Try DB lookup by exact ID
            var lb = _context.Leaderboards.FirstOrDefault(l => l.Id == query);
            if (lb != null)
            {
                if (tab == "songlinks" && data.targetLeaderboards?.endPoints?.ContainsKey(lb.Id) == true)
                    return lb.Id;
                if (tab == "leaderboard" && data.leaderboards?.top10kLeaderboardMeta?.ContainsKey(lb.Id) == true)
                    return lb.Id;
            }

            // Try name search — if exactly one result, auto-select it
            var candidates = GetSongCandidates(query, data, tab);
            if (candidates.Count == 1)
                return candidates[0].Id;

            return null;
        }

        private void BuildSongSearchResults(string query, SongSuggestData data, string tab)
        {
            SongSearchResults = GetSongCandidates(query.Trim(), data, tab);
        }

        private List<SongIdOption> GetSongCandidates(string query, SongSuggestData data, string tab)
        {
            // Get the set of valid IDs for this tab
            HashSet<string> validIds;
            if (tab == "songlinks")
                validIds = data.targetLeaderboards?.endPoints?.Keys.ToHashSet() ?? new();
            else
                validIds = data.leaderboards?.top10kLeaderboardMeta?.Keys.ToHashSet() ?? new();

            // Search by name in DB, filtered to valid IDs
            return _context.Leaderboards
                .Where(l => validIds.Contains(l.Id) && (l.Name.Contains(query) || l.Id.Contains(query)))
                .OrderByDescending(l => l.Stars)
                .Take(20)
                .AsEnumerable()
                .Select(l => new SongIdOption
                {
                    Id = l.Id,
                    Name = $"{l.Name} ({l.DifficultyName} - {l.ModeName} - {l.Id})"
                })
                .ToList();
        }

        private void BuildDropdownOptions(SongSuggestData data)
        {
            if (data.targetLeaderboards?.endPoints != null)
            {
                AvailableTargetSongs = data.targetLeaderboards.endPoints.Keys
                    .Take(500)
                    .Select(id => new SongIdOption
                    {
                        Id = id,
                        Name = SongNameHelper.GetSongName(id, _context) ?? id
                    })
                    .ToList();
            }

            if (data.leaderboards?.top10kLeaderboardMeta != null)
            {
                AvailableLeaderboardSongs = data.leaderboards.top10kLeaderboardMeta.Keys
                    .Take(500)
                    .Select(id => new SongIdOption
                    {
                        Id = id,
                        Name = SongNameHelper.GetSongName(id, _context) ?? id
                    })
                    .ToList();
            }
        }

        private void BuildPlaylistSuggestions(SongSuggestData data)
        {
            if (data.sortedSuggestions == null || data.sortedSuggestions.Count == 0) return;

            var top50 = data.sortedSuggestions.Take(50).ToList();
            for (int i = 0; i < top50.Count; i++)
            {
                string songID = top50[i];
                string songName = SongNameHelper.GetSongName(songID, _context) ?? songID;

                float maxScore = 0;
                double averageScore = 0;
                int playerCount = 0;
                if (data.leaderboards?.top10kLeaderboardMeta.TryGetValue(songID, out var meta) == true)
                {
                    maxScore = (float)meta.maxScore;
                    averageScore = meta.averageScore;
                    playerCount = (int)meta.count;
                }

                float playerScore = 0;
                var playerScoreData = data.activePlayer?.top10kScore.FirstOrDefault(s => s.songID == songID);
                if (playerScoreData != null) playerScore = playerScoreData.pp;

                double advantage = maxScore > 0 ? Math.Max(0, (1 - playerScore / maxScore)) * 100 : 100;

                int linkedPlayers = 0;
                if (data.targetLeaderboards?.endPoints.TryGetValue(songID, out var endpoint) == true)
                    linkedPlayers = endpoint.songLinks.Select(l => l.playerID).Distinct().Count();

                PlaylistSuggestions.Add(new PlaylistSuggestionItem
                {
                    Position = i + 1,
                    SongID = songID,
                    SongName = songName,
                    PlayerScore = playerScore,
                    MaxScore = maxScore,
                    AverageScore = averageScore,
                    Advantage = advantage,
                    PlayerCount = playerCount,
                    LinkedPlayers = linkedPlayers,
                    IsUnplayed = playerScore == 0
                });
            }
        }

        private void BuildOriginSongs(SongSuggestData data)
        {
            if (data.activePlayer == null || data.leaderboards == null) return;

            var playerScores = data.activePlayer.top10kScore ?? new List<Top10kScore>();
            var playerScoreDict = playerScores.ToDictionary(s => s.songID, s => s);

            // The set of IDs actually kept as origin leaderboards (the final 50)
            var keptOriginIds = new HashSet<string>(data.originleaderboardIDs ?? new());

            // Rebuild the initial candidate list: top (originLeaderboardsCount + extraLeaderboardsCount)
            // songs ordered by weighted score, matching the selection logic in SelectPlayedOriginSongs
            int candidateCount = data.originLeaderboardsCount + data.extraLeaderboardsCount;
            var initialCandidates = playerScores
                .OrderByDescending(v => RankedSongSuggest.PlayerWeightedScoreValue(v.pp, v.rank))
                .Where(v => data.leaderboards.top10kLeaderboardMeta.ContainsKey(v.songID))
                .Take(candidateCount)
                .ToList();

            foreach (var score in initialCandidates)
            {
                string songID = score.songID;
                string songName = SongNameHelper.GetSongName(songID, _context) ?? songID;

                float maxScore = 0;
                int playerCount = 0;
                if (data.leaderboards.top10kLeaderboardMeta.TryGetValue(songID, out var meta))
                {
                    maxScore = (float)meta.maxScore;
                    playerCount = (int)meta.count;
                }

                bool isKept = keptOriginIds.Contains(songID);

                float playerScore = score.pp;
                double weightedPP = RankedSongSuggest.PlayerWeightedScoreValue(playerScore, score.rank);

                // Advantage% = PlayerRelativeScoreValue * 100 (player score as % of max)
                double advantagePercent = RankedSongSuggest.PlayerRelativeScoreValue(playerScore, songID, data.leaderboards) * 100;

                int comparativeRank = 0;
                if (playerScore > 0 && maxScore > 0 && playerCount > 0)
                    comparativeRank = (int)((1 - playerScore / maxScore) * playerCount);

                OriginSongs.Add(new OriginSongItem
                {
                    SongID = songID,
                    SongName = songName,
                    LinkedCount = data.originLeaderboards?.endPoints?.TryGetValue(songID, out var ep) == true ? ep.songLinks.Count : 0,
                    PlayerScore = playerScore,
                    WeightedPP = weightedPP,
                    PlayerCount = playerCount,
                    AdvantagePercent = (float)advantagePercent,
                    ComparativeRank = comparativeRank,
                    IsKept = isKept
                });
            }
        }

        private void BuildOriginFiltering(SongSuggestData data)
        {
            if (data.activePlayer == null || data.leaderboards == null) return;

            var result = new OriginFilteringData
            {
                PlayerID = data.playerID
            };

            var allSongs = data.activePlayer.top10kScore
                .OrderByDescending(v => RankedSongSuggest.PlayerWeightedScoreValue(v.pp, v.rank))
                .ToList();

            result.TotalSongs = allSongs.Count;

            double maxKeepPercentage = 0.75;
            int valueSongCount = (int)(maxKeepPercentage * allSongs.Count);
            if (valueSongCount == 0) valueSongCount = 1;

            var step1Songs = allSongs.Take(valueSongCount).ToList();
            var filteredStep1 = allSongs.Skip(valueSongCount).ToList();

            result.Step2Kept = step1Songs.Count;
            result.Step2Filtered = filteredStep1.Count;

            result.Step2FilteredSongs = filteredStep1.Take(20).Select(s => new FilteredSongItem
            {
                SongID = s.songID,
                SongName = SongNameHelper.GetSongName(s.songID, _context) ?? s.songID,
                PP = s.pp,
                Rank = s.rank,
                WeightedScore = RankedSongSuggest.PlayerWeightedScoreValue(s.pp, s.rank)
            }).ToList();

            double percentToKeep = 50.0 / (data.originLeaderboardsCount + data.extraLeaderboardsCount);
            int comparativeBestCount = (int)Math.Ceiling(percentToKeep * valueSongCount);

            var step2Songs = step1Songs
                .OrderByDescending(c => RankedSongSuggest.PlayerRelativeScoreValue(c.pp, c.songID, data.leaderboards))
                .ToList();

            var filteredStep2 = step2Songs.Skip(comparativeBestCount).ToList();
            var step2Best = step2Songs.Take(comparativeBestCount).ToList();

            result.PercentToKeep = percentToKeep;
            result.Step3Kept = step2Best.Count;
            result.Step3Filtered = filteredStep2.Count;

            result.Step3FilteredSongs = filteredStep2.Take(20).Select(s =>
            {
                float maxScore = 0;
                if (data.leaderboards.top10kLeaderboardMeta.TryGetValue(s.songID, out var meta))
                    maxScore = (float)meta.maxScore;

                return new FilteredRelativeSongItem
                {
                    SongID = s.songID,
                    SongName = SongNameHelper.GetSongName(s.songID, _context) ?? s.songID,
                    PP = s.pp,
                    MaxScore = maxScore,
                    RelativeScore = RankedSongSuggest.PlayerRelativeScoreValue(s.pp, s.songID, data.leaderboards),
                    Advantage = maxScore > 0 ? ((maxScore - s.pp) / s.pp) * 100 : 0
                };
            }).ToList();

            var step4Songs = step2Best.Take(50).ToList();
            result.FinalCount = step4Songs.Count;

            result.SelectedSongs = step4Songs.Select((s, i) => new SelectedOriginSongItem
            {
                Position = i + 1,
                SongID = s.songID,
                SongName = SongNameHelper.GetSongName(s.songID, _context) ?? s.songID,
                PP = s.pp,
                Rank = s.rank,
                WeightedScore = RankedSongSuggest.PlayerWeightedScoreValue(s.pp, s.rank),
                RelativeScore = RankedSongSuggest.PlayerRelativeScoreValue(s.pp, s.songID, data.leaderboards)
            }).ToList();

            OriginFiltering = result;
        }

        private void BuildSongLinkDetails(SongSuggestData data, string songID)
        {
            if (data.targetLeaderboards?.endPoints == null) return;
            if (!data.targetLeaderboards.endPoints.TryGetValue(songID, out var targetEndPoint)) return;

            SongLinkSongName = SongNameHelper.GetSongName(songID, _context) ?? songID;
            var allLinks = targetEndPoint.songLinks;
            if (allLinks.Count == 0) return;

            var players = data.leaderboards.top10kPlayers
                .Where(p => p.id != data.playerID)
                .Where(p => p.top10kScore.Any(s => s.songID == songID))
                .ToList();

            foreach (var player in players)
            {
                var playerOriginSongs = player.top10kScore
                    .Where(s => data.originleaderboardIDs.Contains(s.songID))
                    .OrderByDescending(s => s.pp)
                    .ToList();

                foreach (var originSong in playerOriginSongs)
                {
                    bool isUsed = allLinks.Any(l =>
                        l.playerID == player.id &&
                        l.originSongScore.songID == originSong.songID &&
                        l.targetSongScore.songID == songID);

                    SongLinkDetails.Add(new SongLinkDetailItem
                    {
                        PlayerID = player.id,
                        PlayerName = player.name,
                        PlayerRank = player.rank,
                        OriginSongID = originSong.songID,
                        OriginSongName = SongNameHelper.GetSongName(originSong.songID, _context) ?? originSong.songID,
                        PP = originSong.pp,
                        IsUsed = isUsed
                    });
                }
            }
        }

        private void BuildAllSongLinksSummary(SongSuggestData data)
        {
            if (data.targetLeaderboards?.endPoints == null) return;

            AllSongLinksSummary = data.targetLeaderboards.endPoints
                .Select(kvp => new SongLinkSummaryItem
                {
                    SongID = kvp.Key,
                    SongName = SongNameHelper.GetSongName(kvp.Key, _context) ?? kvp.Key,
                    TotalLinks = kvp.Value.songLinks.Count,
                    UniquePlayers = kvp.Value.songLinks.Select(l => l.playerID).Distinct().Count(),
                    UniqueOriginSongs = kvp.Value.songLinks.Select(l => l.originSongScore.songID).Distinct().Count(),
                    AverageDistance = kvp.Value.songLinks.Count > 0 ? kvp.Value.songLinks.Average(l => l.distance) : 0,
                    AverageRank = kvp.Value.averageRank
                })
                .OrderByDescending(x => x.TotalLinks)
                .Take(200)
                .ToList();
        }

        private void BuildLeaderboardDetails(SongSuggestData data, string songID)
        {
            if (data.leaderboards?.top10kPlayers == null) return;

            LeaderboardSongName = SongNameHelper.GetSongName(songID, _context) ?? songID;

            LeaderboardPlayers = data.leaderboards.top10kPlayers
                .SelectMany(player => player.top10kScore
                    .Where(score => score.songID == songID)
                    .Select(score => new LeaderboardPlayerItem
                    {
                        PlayerID = player.id,
                        PlayerName = player.name,
                        PlayerRank = player.rank,
                        PP = score.pp,
                        ScoreRank = score.rank
                    }))
                .OrderBy(x => x.PlayerRank)
                .ToList();
        }

        private void BuildLeaderboardLinks(SongSuggestData data)
        {
            if (data.originLeaderboards?.endPoints == null) return;

            var result = new LeaderboardLinksData();

            var allLinks = new List<LeaderboardLink>();
            foreach (var endPoint in data.originLeaderboards.endPoints.Values)
                allLinks.AddRange(endPoint.songLinks);

            result.TotalLinks = allLinks.Count;
            result.UniquePlayers = allLinks.Select(l => l.playerID).Distinct().Count();
            result.UniqueOriginSongs = allLinks.Select(l => l.originSongScore.songID).Distinct().Count();
            result.UniqueTargetSongs = allLinks.Select(l => l.targetSongScore.songID).Distinct().Count();
            result.MinDistance = allLinks.Count > 0 ? allLinks.Min(l => l.distance) : 0;
            result.MaxDistance = allLinks.Count > 0 ? allLinks.Max(l => l.distance) : 0;

            result.TargetGroups = allLinks
                .GroupBy(l => l.targetSongScore.songID)
                .OrderByDescending(g => g.Count())
                .Take(100)
                .Select(g =>
                {
                    var distances = g.Select(l => l.distance).ToList();
                    return new TargetGroupItem
                    {
                        SongID = g.Key,
                        SongName = SongNameHelper.GetSongName(g.Key, _context) ?? g.Key,
                        TotalLinks = g.Count(),
                        UniquePlayers = g.Select(l => l.playerID).Distinct().Count(),
                        UniqueOriginSongs = g.Select(l => l.originSongScore.songID).Distinct().Count(),
                        AvgDistance = distances.Average(),
                        MinDistance = distances.Min(),
                        MaxDistance = distances.Max()
                    };
                })
                .ToList();

            result.DistributionBuckets = allLinks
                .GroupBy(l => Math.Round(l.distance, 2))
                .OrderBy(g => g.Key)
                .Select(g => new DistanceBucket
                {
                    Distance = g.Key,
                    Count = g.Count(),
                    Percentage = allLinks.Count > 0 ? (double)g.Count() / allLinks.Count * 100 : 0
                })
                .ToList();

            LeaderboardLinks = result;
        }

        // View model classes
        public class SongIdOption { public string Id { get; set; } public string Name { get; set; } }

        public class PlaylistSuggestionItem
        {
            public int Position { get; set; }
            public string SongID { get; set; }
            public string SongName { get; set; }
            public float PlayerScore { get; set; }
            public float MaxScore { get; set; }
            public double AverageScore { get; set; }
            public double Advantage { get; set; }
            public int PlayerCount { get; set; }
            public int LinkedPlayers { get; set; }
            public bool IsUnplayed { get; set; }
        }

        public class OriginSongItem
        {
            public string SongID { get; set; }
            public string SongName { get; set; }
            public int LinkedCount { get; set; }
            public float PlayerScore { get; set; }
            public double WeightedPP { get; set; }
            public int PlayerCount { get; set; }
            public float AdvantagePercent { get; set; }
            public int ComparativeRank { get; set; }
            public bool IsKept { get; set; }
        }

        public class OriginFilteringData
        {
            public string PlayerID { get; set; }
            public int TotalSongs { get; set; }
            public int Step2Kept { get; set; }
            public int Step2Filtered { get; set; }
            public List<FilteredSongItem> Step2FilteredSongs { get; set; } = new();
            public double PercentToKeep { get; set; }
            public int Step3Kept { get; set; }
            public int Step3Filtered { get; set; }
            public List<FilteredRelativeSongItem> Step3FilteredSongs { get; set; } = new();
            public int FinalCount { get; set; }
            public List<SelectedOriginSongItem> SelectedSongs { get; set; } = new();
        }

        public class FilteredSongItem
        {
            public string SongID { get; set; }
            public string SongName { get; set; }
            public float PP { get; set; }
            public int Rank { get; set; }
            public double WeightedScore { get; set; }
        }

        public class FilteredRelativeSongItem
        {
            public string SongID { get; set; }
            public string SongName { get; set; }
            public float PP { get; set; }
            public float MaxScore { get; set; }
            public double RelativeScore { get; set; }
            public double Advantage { get; set; }
        }

        public class SelectedOriginSongItem
        {
            public int Position { get; set; }
            public string SongID { get; set; }
            public string SongName { get; set; }
            public float PP { get; set; }
            public int Rank { get; set; }
            public double WeightedScore { get; set; }
            public double RelativeScore { get; set; }
        }

        public class SongLinkDetailItem
        {
            public string PlayerID { get; set; }
            public string PlayerName { get; set; }
            public int PlayerRank { get; set; }
            public string OriginSongID { get; set; }
            public string OriginSongName { get; set; }
            public float PP { get; set; }
            public bool IsUsed { get; set; }
        }

        public class SongLinkSummaryItem
        {
            public string SongID { get; set; }
            public string SongName { get; set; }
            public int TotalLinks { get; set; }
            public int UniquePlayers { get; set; }
            public int UniqueOriginSongs { get; set; }
            public double AverageDistance { get; set; }
            public double AverageRank { get; set; }
        }

        public class LeaderboardPlayerItem
        {
            public string PlayerID { get; set; }
            public string PlayerName { get; set; }
            public int PlayerRank { get; set; }
            public float PP { get; set; }
            public int ScoreRank { get; set; }
        }

        public class LeaderboardLinksData
        {
            public int TotalLinks { get; set; }
            public int UniquePlayers { get; set; }
            public int UniqueOriginSongs { get; set; }
            public int UniqueTargetSongs { get; set; }
            public double MinDistance { get; set; }
            public double MaxDistance { get; set; }
            public List<TargetGroupItem> TargetGroups { get; set; } = new();
            public List<DistanceBucket> DistributionBuckets { get; set; } = new();
        }

        public class TargetGroupItem
        {
            public string SongID { get; set; }
            public string SongName { get; set; }
            public int TotalLinks { get; set; }
            public int UniquePlayers { get; set; }
            public int UniqueOriginSongs { get; set; }
            public double AvgDistance { get; set; }
            public double MinDistance { get; set; }
            public double MaxDistance { get; set; }
        }

        public class DistanceBucket
        {
            public double Distance { get; set; }
            public int Count { get; set; }
            public double Percentage { get; set; }
        }
    }
}
