using System.IO.Compression;
using System.Text.Json;

namespace ReplayStudy
{
    /// <summary>
    /// Same maps folder convention as RatingAPI's Downloader: {mapsDir}/{HASH} with the
    /// extracted map inside. Reuses existing folders, downloads from BeatSaver when missing.
    /// </summary>
    public class MapCache
    {
        private readonly string _mapsDirectory;
        private readonly HttpClient _client;

        public MapCache(string mapsDirectory)
        {
            _mapsDirectory = mapsDirectory;
            Directory.CreateDirectory(mapsDirectory);
            _client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            _client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; BeatSaverDownloader/1.0)");
        }

        public async Task<string?> Map(string hash)
        {
            string lowerCaseDir = Path.Combine(_mapsDirectory, hash.ToLower());
            if (Directory.Exists(lowerCaseDir))
            {
                return lowerCaseDir;
            }

            string mapDir = Path.Combine(_mapsDirectory, hash.ToUpper());
            if (Directory.Exists(mapDir))
            {
                return mapDir;
            }

            byte[]? zipData = null;

            // Direct CDN URL works for most maps and doesn't need the BeatSaver API
            try
            {
                zipData = await _client.GetByteArrayAsync($"https://r2cdn.beatsaver.com/{hash.ToLower()}.zip");
            }
            catch (Exception) { }

            if (zipData == null)
            {
                try
                {
                    var response = await _client.GetStringAsync($"https://beatsaver.com/api/maps/hash/{hash}");
                    using var doc = JsonDocument.Parse(response);
                    foreach (var version in doc.RootElement.GetProperty("versions").EnumerateArray())
                    {
                        if (string.Equals(version.GetProperty("hash").GetString(), hash, StringComparison.OrdinalIgnoreCase))
                        {
                            var downloadUrl = version.GetProperty("downloadURL").GetString();
                            if (!string.IsNullOrEmpty(downloadUrl))
                            {
                                zipData = await _client.GetByteArrayAsync(downloadUrl);
                            }
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    return null;
                }
            }

            if (zipData == null) return null;

            try
            {
                using var zipStream = new MemoryStream(zipData);
                using var zipArchive = new ZipArchive(zipStream);
                Directory.CreateDirectory(mapDir);
                zipArchive.ExtractToDirectory(mapDir);
            }
            catch (Exception)
            {
                try { Directory.Delete(mapDir, true); } catch (Exception) { }
                return null;
            }

            return mapDir;
        }
    }
}
