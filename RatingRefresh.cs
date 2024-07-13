using Newtonsoft.Json;
using beatleader_parser;
using beatleader_analyzer;
using Parser.Map;
using System.IO.Compression;
using Microsoft.EntityFrameworkCore;

namespace portaBLe
{
    public class RatingRefresh
    {
        public static async Task RefreshMaps(AppContext dbContext) {
            var ai = new InferPublish();

            var lbs = dbContext.Leaderboards.Include(lb => lb.ModifiersRating).ToList();
            foreach (var lb in lbs)
            {
                try
                {
                    var response = Get(lb.Hash, lb.ModeName, lb.DifficultyName, ai);

                    lb.PassRating = response["none"].LackMapCalculation.PassRating;
                    lb.TechRating = response["none"].LackMapCalculation.TechRating;
                    lb.PredictedAcc = response["none"].PredictedAcc;
                    lb.AccRating = response["none"].AccRating;
                    lb.Stars = ReplayUtils.ToStars(lb.AccRating, lb.PassRating, lb.TechRating);

                    var modrating = lb.ModifiersRating = new ModifiersRating
                    {
                        SSPassRating = response["SS"].LackMapCalculation.PassRating,
                        SSTechRating = response["SS"].LackMapCalculation.TechRating,
                        SSPredictedAcc = response["SS"].PredictedAcc,
                        SSAccRating = response["SS"].AccRating,

                        FSPassRating = response["FS"].LackMapCalculation.PassRating,
                        FSTechRating = response["FS"].LackMapCalculation.TechRating,
                        FSPredictedAcc = response["FS"].PredictedAcc,
                        FSAccRating = response["FS"].AccRating,

                        SFPassRating = response["SFS"].LackMapCalculation.PassRating,
                        SFTechRating = response["SFS"].LackMapCalculation.TechRating,
                        SFPredictedAcc = response["SFS"].PredictedAcc,
                        SFAccRating = response["SFS"].AccRating,
                    };

                    modrating.SFStars = ReplayUtils.ToStars(modrating.SFAccRating, modrating.SFPassRating, modrating.SFTechRating);
                    modrating.FSStars = ReplayUtils.ToStars(modrating.FSAccRating, modrating.FSPassRating, modrating.FSTechRating);
                    modrating.SSStars = ReplayUtils.ToStars(modrating.SSAccRating, modrating.SSPassRating, modrating.SSTechRating);
                } catch (Exception e)
                {
                    Console.WriteLine($"{e.Message}");
                }
            }

            dbContext.BulkSaveChanges();
        }

        public static Dictionary<string, RatingResult> Get(string hash, string mode, string diff, InferPublish ai)
        {
            var modifiers = new List<(string, float)>() {
                ("SS", 0.85f),
                ("none", 1),
                ("FS", 1.2f),
                ("SFS", 1.5f),
            };
            var results = new Dictionary<string, RatingResult>();
            var mapset = new Parse().TryLoadPath(DownloadMap(hash), mode, diff);
            if (mapset != null)
            {
                var beatmapSets = mapset.Info._difficultyBeatmapSets.FirstOrDefault();
                if (beatmapSets == null) return results;
                var data = beatmapSets._difficultyBeatmaps.FirstOrDefault();
                if (data == null) return results;
                var map = mapset.Difficulty;
                if (map == null) return results;
                foreach ((var name, var timescale) in modifiers)
                {
                    results[name] = GetBLRatings(map, mode, diff, mapset.Info._beatsPerMinute, data._noteJumpMovementSpeed, timescale, ai);
                }
            }

            return results;
        }

        public static RatingResult GetBLRatings(DifficultySet map, string characteristic, string difficulty, float bpm, float njs, float timescale, InferPublish ai) {
            
            var ratings = new Analyze().GetRating(map.Data, characteristic, difficulty, bpm, njs, timescale).FirstOrDefault();
            if (ratings == null) return new();
            var predictedAcc = ai.GetAIAcc(map, bpm, njs, timescale);
            var lack = new LackMapCalculation
            {
                PassRating = (float)ratings.Pass,
                TechRating = (float)ratings.Tech * 10,
                LowNoteNerf = (float)ratings.Nerf,
                LinearRating = (float)ratings.Linear,
                MultiRating = (float)ratings.Multi
            };
            var accRating = ReplayUtils.AccRating((float)predictedAcc, (float)ratings.Pass, (float)ratings.Tech);
            lack = ModifyRatings(lack, njs * timescale, timescale);
            var pointList = ReplayUtils.GetCurve(predictedAcc, accRating, lack);
            var star = ReplayUtils.ToStars(accRating, (float)ratings.Pass, (float)ratings.Tech * 10);
            RatingResult result = new()
            {
                PredictedAcc = (float)predictedAcc,
                AccRating = accRating,
                LackMapCalculation = lack,
                PointList = pointList,
                StarRating = star
            };
            return result;
        }

        public static LackMapCalculation ModifyRatings(LackMapCalculation ratings, float njs, double timescale)
        {
            if(timescale > 1)
            {
                float buff = 1f;
                if (njs > 20)
                {
                    buff = 1 + 0.01f * (njs - 20);
                }

                ratings.PassRating *= buff;
                ratings.TechRating *= buff;
            }

            return ratings;
        }

        public static string DownloadMap(string hash)
        {
            string _mapsDirectory = "maps";
            string lowerCaseDir = Path.Combine(_mapsDirectory, hash.ToLower());
            if (Directory.Exists(lowerCaseDir))
            {
                if (File.Exists(Path.Combine(lowerCaseDir, "info.dat")) || File.Exists(Path.Combine(lowerCaseDir, "Info.dat"))) {
                    return lowerCaseDir;
                } else {
                    Directory.Delete(lowerCaseDir, true);
                }
            }

            string mapDir = Path.Combine(_mapsDirectory, hash.ToUpper());

            if (Directory.Exists(mapDir))
            {
                if (File.Exists(Path.Combine(mapDir, "info.dat")) || File.Exists(Path.Combine(mapDir, "Info.dat"))) {
                    return mapDir;
                } else {
                    Directory.Delete(mapDir, true);
                }
            }

            string beatsaverUrl = $"https://beatsaver.com/api/maps/hash/{hash}";
            using var httpClient = new HttpClient();
            var response = httpClient.GetStringAsync(beatsaverUrl).Result ?? throw new Exception("Error during API request");
            dynamic? beatsaverData = JsonConvert.DeserializeObject(response) ?? throw new Exception("Error during deserialization");
            string downloadURL = string.Empty;

            foreach (var version in beatsaverData.versions)
            {
                if (version.hash.ToString().ToLower() == hash.ToLower())
                {
                    downloadURL = version.downloadURL;
                    break;
                }
            }

            if (string.IsNullOrEmpty(downloadURL))
            {
                throw new Exception("Map download URL not found.");
            }

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; BeatSaverDownloader/1.0)");
            var data = client.GetByteArrayAsync(downloadURL);

            using var zipStream = new MemoryStream(data.Result);
            using var zipArchive = new ZipArchive(zipStream);
            Directory.CreateDirectory(mapDir);
            zipArchive.ExtractToDirectory(mapDir);

            string[] extractedFiles = Directory.GetFiles(mapDir);
            foreach (string extractedFile in extractedFiles)
            {
                if (!extractedFile.EndsWith(".dat"))
                {
                    try
                    {
                        File.Delete(extractedFile);
                    }
                    catch
                    {
                        // Handle exceptions if required or continue
                    }
                }
            }

            return mapDir;
        }
    }

    public class LackMapCalculation
    {
        public float MultiRating { get; set; } = 0;
        public float PassRating { get; set; } = 0;
        public float LinearRating { get; set; } = 0;

        public float TechRating { get; set; } = 0;
        public float LowNoteNerf { get; set; } = 0;
    }

    public class RatingResult
    {
        public float PredictedAcc { get; set; } = 0;
        public float AccRating { get; set; } = 0;
        public float StarRating { get; set; } = 0;
        public LackMapCalculation LackMapCalculation { get; set; } = new();
        public List<Point> PointList { get; set; } = new();
    }

    public class Point
    {
        public double x { get; set; } = 0;
        public double y { get; set; } = 0;

        public Point()
        {

        }

        public Point(double x, double y)
        {
            this.x = x;
            this.y = y;
        }

        public List<Point> ToPoints(List<(double x, double y)> curve)
        {
            List<Point> points = new();

            foreach (var p in curve)
            {
                points.Add(new(p.x, p.y));
            }

            return points;
        }
    }
}
