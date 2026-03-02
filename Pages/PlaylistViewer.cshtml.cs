using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using portaBLe.MapRecommendation;
using System.Text.Json;

namespace portaBLe.Pages
{
    public static class StringExtensions
    {
        public static string FirstCharToUpper(this string input) =>
            input switch
            {
                null => throw new ArgumentNullException(nameof(input)),
                "" => throw new ArgumentException($"{nameof(input)} cannot be empty", nameof(input)),
                _ => string.Concat(input[0].ToString().ToUpper(), input.AsSpan(1))
            };
    }
    public class PlaylistViewerModel : PageModel
    {
        private readonly AppContext _context;
        private readonly IWebHostEnvironment _environment;

        public PlaylistViewerModel(AppContext context, IWebHostEnvironment environment)
        {
            _context = context;
            _environment = environment;
        }

        public Playlist Playlist { get; set; }
        public List<PlaylistSongDetail> SongDetails { get; set; } = new();
        public string PlaylistTitle { get; set; }
        public string PlaylistAuthor { get; set; }
        public string PlayerId { get; set; }
        public string GeneratedTime { get; set; }
        public int TotalSongs { get; set; }
        public string ErrorMessage { get; set; }
        public string PlaylistSource { get; set; } = "Unknown";

        public class PlaylistSongDetail
        {
            public string SongName { get; set; }
            public string Hash { get; set; }
            public string Mapper { get; set; }
            public string Difficulty { get; set; }
            public string Characteristic { get; set; }
            public float Stars { get; set; }
            public float AccRating { get; set; }
            public float TechRating { get; set; }
            public float PassRating { get; set; }
            public int Rank { get; set; }
        }

        public async Task<IActionResult> OnGetAsync(string playlistFile = null)
        {
            try
            {
                string filePath = null;
                string fileName = null;

                if (!string.IsNullOrEmpty(playlistFile))
                {
                    // Load specific playlist file
                    filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recommendations", playlistFile);
                    fileName = playlistFile;
                }
                else
                {
                    // Load the latest playlist file
                    var recommendationsDir = Path.Combine(_environment.ContentRootPath, "Recommendations");
                    
                    if (!Directory.Exists(recommendationsDir))
                    {
                        // Try alternate location (AppDomain.CurrentDomain.BaseDirectory)
                        recommendationsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recommendations");
                    }

                    if (!Directory.Exists(recommendationsDir))
                    {
                        ErrorMessage = "No recommendations directory found. Please generate a playlist first using RecommendationService or RankedSongSuggest.";
                        return Page();
                    }

                    var files = Directory.GetFiles(recommendationsDir, "*.json")
                        .OrderByDescending(f => System.IO.File.GetLastWriteTime(f))
                        .FirstOrDefault();

                    if (files == null)
                    {
                        ErrorMessage = "No playlist files found in the recommendations directory. Please generate a playlist first.";
                        return Page();
                    }

                    filePath = files;
                    fileName = Path.GetFileName(files);
                }

                // Check if file exists
                if (!System.IO.File.Exists(filePath))
                {
                    ErrorMessage = $"Playlist file not found: {Path.GetFileName(filePath)}";
                    return Page();
                }

                // Detect source based on filename pattern
                if (fileName.Contains("recommendation_"))
                {
                    PlaylistSource = "RecommendationService";
                }
                else if (fileName.Contains("BeatLeader"))
                {
                    PlaylistSource = "RankedSongSuggest";
                }

                // Parse the playlist JSON
                var json = await System.IO.File.ReadAllTextAsync(filePath);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                Playlist = JsonSerializer.Deserialize<Playlist>(json, options);

                if (Playlist == null || Playlist.songs == null || Playlist.songs.Length == 0)
                {
                    ErrorMessage = "Playlist file is empty or invalid.";
                    return Page();
                }

                PlaylistTitle = Playlist.playlistTitle;
                PlaylistAuthor = Playlist.playlistAuthor;
                PlayerId = Playlist.customData?.owner ?? "Unknown";
                GeneratedTime = System.IO.File.GetLastWriteTime(filePath).ToString("yyyy-MM-dd HH:mm:ss");
                TotalSongs = Playlist.songs.Length;

                // Load leaderboard details for each song
                for (int i = 0; i < Playlist.songs.Length; i++)
                {
                    var song = Playlist.songs[i];
                    var difficulty = StringExtensions.FirstCharToUpper(song.difficulties?.FirstOrDefault()?.name);
                    var characteristic = song.difficulties?.FirstOrDefault()?.characteristic;

                    var leaderboard = await _context.Leaderboards
                        .FirstOrDefaultAsync(l => l.Hash == song.hash && l.DifficultyName == difficulty && l.ModeName == characteristic);
                     
                    var detail = new PlaylistSongDetail
                    {
                        SongName = song.songName,
                        Hash = song.hash,
                        Mapper = song.levelAuthorName,
                        Difficulty = difficulty ?? "?",
                        Characteristic = characteristic ?? "?",
                        Stars = leaderboard?.Stars ?? 0,
                        AccRating = leaderboard?.AccRating ?? 0,
                        TechRating = leaderboard?.TechRating ?? 0,
                        PassRating = leaderboard?.PassRating ?? 0,
                        Rank = i + 1
                    };

                    SongDetails.Add(detail);
                }

                return Page();
            }
            catch (JsonException ex)
            {
                ErrorMessage = $"Error parsing playlist file: {ex.Message}";
                return Page();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error loading playlist: {ex.Message}";
                return Page();
            }
        }

        public async Task<IActionResult> OnGetListAsync()
        {
            try
            {
                var recommendationsDir = Path.Combine(_environment.ContentRootPath, "Recommendations");
                
                if (!Directory.Exists(recommendationsDir))
                {
                    // Try alternate location (AppDomain.CurrentDomain.BaseDirectory)
                    recommendationsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recommendations");
                }

                if (!Directory.Exists(recommendationsDir))
                {
                    return new JsonResult(new { files = new List<object>() });
                }

                var files = Directory.GetFiles(recommendationsDir, "*.json")
                    .OrderByDescending(f => System.IO.File.GetLastWriteTime(f))
                    .Take(20)
                    .Select(f => new
                    {
                        filename = Path.GetFileName(f),
                        displayName = Path.GetFileNameWithoutExtension(f),
                        lastModified = System.IO.File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm:ss")
                    })
                    .ToList();

                return new JsonResult(new { files = files });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { error = ex.Message });
            }
        }
    }
}
