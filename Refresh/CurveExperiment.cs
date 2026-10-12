using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using portaBLe.DB;
using RatingAPI.Controllers;

namespace portaBLe.Refresh
{
    // Tries several per-map acc curve settings (RatingAPI CurveSettings) on the scores in Database.db, entirely in memory,
    // and saves the comparison to wwwroot/curve-experiments.json for the Curve Experiments page. Nothing is written to the DB.
    //
    // Curves only use map data (RatingAPI Curve.GetCurve). Scores are only used here, to judge each candidate:
    // every score is re-scored with the candidate's curves, every player's weighted pp is rebuilt, then the metrics below
    // are computed. The first candidate is always today's curve and is the reference for the comparisons.
    public static class CurveExperiment
    {
        public const string FileName = "curve-experiments.json";

        private const int MinScoresPerMap = 20;      // maps with fewer scores are left out of the per-map metrics
        private const int MinBandScoresPerMap = 5;   // same, for one rank band on one map
        private const int ProfileTop = 50;           // "worth pp" = in the player's top 50 scores
        private const int MegametricMinPlays = 75;   // as LeaderboardsRefresh's Megametric75
        private const int StarBandCount = 14;        // 0-1 ... 12-13, 13+

        public static readonly string[] RankBands = { "1-100", "101-1000", "1001-10000", "10001+" };
        private static int RankBandOf(int rank) => rank <= 100 ? 0 : rank <= 1000 ? 1 : rank <= 10000 ? 2 : 3;
        public static string StarBandName(int band) => band >= StarBandCount - 1 ? $"{StarBandCount - 1}+" : $"{band}-{band + 1}";
        private static int StarBandOf(float stars) => Math.Clamp((int)MathF.Floor(stars), 0, StarBandCount - 1);

        // Round 5: tapered relative gain above the prediction, and stronger fills aiming at a similar share of 700pp+ maps
        // in every star range. Pass your own list to Run to try others.
        public static List<CurveSettings> DefaultCandidates()
        {
            CurveSettings Tapered(string name, double fill, double above = 0.9, double capShare = 0, double lambda = 0) => new()
            {
                Name = name, Fill = fill, AboveStrength = above, AboveTaperAcc = 0.985, AboveTaperMin = 0.1,
                CapShare = capShare, CapKeep = capShare > 0 ? 0.3 : 1, Lambda = lambda,
            };
            return new()
            {
                new CurveSettings { Name = "Current" },
                new CurveSettings { Name = "Fill 0.3 + above 0.9 (round 4)", Fill = 0.3, AboveStrength = 0.9 },
                Tapered("Fill 0.3 + tapered above 0.9", 0.3),
                Tapered("Fill 0.5 + tapered above 0.9", 0.5),
                Tapered("Fill 0.7 + tapered above 0.9", 0.7),
                Tapered("Fill 0.9 + tapered above 0.9", 0.9),
                Tapered("Fill 1.0 + tapered above 0.9", 1.0),
                Tapered("Fill 0.7 + tapered above 0.6", 0.7, above: 0.6),
                Tapered("Fill 0.5 + tapered above 0.9 + cap 30%", 0.5, capShare: 0.3),
                Tapered("Fill 0.7 + tapered above 0.9 + cap 30%", 0.7, capShare: 0.3),
                Tapered("Fill 0.7 + tapered above 0.9 + rel 50%", 0.7, lambda: 0.5),
                new CurveSettings { Name = "Fill 0.7 + cap 15%", Fill = 0.7, CapShare = 0.15, CapKeep = 0.15 },
            };
        }

        private const int EvennessMinMaps = 30;   // star ranges with fewer maps are left out of the 700pp+ evenness

        private const int MoversFromTop = 1000;  // movers are picked among the reference top 1000
        private const int MoversCount = 20;

        // ------------------------------------------------------------------
        // Data loaded once
        // ------------------------------------------------------------------

        private sealed class MapInfo
        {
            public Leaderboard Leaderboard = null!;
            public float Stars;
            public int StarBand;
            public bool HalfA;
            public int[] Scores = Array.Empty<int>();
        }

        private sealed class Data
        {
            public MapInfo[] Maps = Array.Empty<MapInfo>();
            public int[] ScoreMap = Array.Empty<int>();
            public int[] ScorePlayer = Array.Empty<int>();
            public float[] ScoreAcc = Array.Empty<float>();
            public string[] ScoreModifiers = Array.Empty<string>();
            public int[][] PlayerScores = Array.Empty<int[]>();
            public string[] PlayerIds = Array.Empty<string>();
            public string[] PlayerNames = Array.Empty<string>();
        }

        private sealed class Evaluation
        {
            public float[] Pp = Array.Empty<float>();
            public float[] Weight = Array.Empty<float>();
            public int[] Position = Array.Empty<int>();   // 0-based position of the score in its player's profile
            public float[] PlayerPp = Array.Empty<float>();
            public float[] PlayerTopPp = Array.Empty<float>();
            public int[] PlayerRank = Array.Empty<int>();   // 1-based
            public List<System.Numerics.Vector2>[] Curves = Array.Empty<List<System.Numerics.Vector2>>();
        }

        // ------------------------------------------------------------------
        // Run
        // ------------------------------------------------------------------

        public static async Task Run(AppContext dbContext, string webRootPath, List<CurveSettings>? candidates = null)
        {
            candidates ??= DefaultCandidates();
            if (candidates.Count == 0 || !candidates[0].IsBase)
            {
                candidates.Insert(0, new CurveSettings { Name = "Current" });
            }

            var timer = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"Curve experiments: {candidates.Count} candidates, loading scores...");
            var data = await Load(dbContext);
            Console.WriteLine($"Curve experiments: {data.Maps.Length} leaderboards, {data.ScoreMap.Length} scores, {data.PlayerIds.Length} players loaded in {timer.Elapsed:mm\\:ss}");

            var result = new CurveExperimentResult
            {
                GeneratedAt = DateTime.UtcNow,
                Leaderboards = data.Maps.Length,
                Scores = data.ScoreMap.Length,
                Players = data.PlayerIds.Length,
                RankBands = RankBands.ToList(),
                StarBands = Enumerable.Range(0, StarBandCount).Select(StarBandName).ToList(),
                MapsPerStarBand = Enumerable.Range(0, StarBandCount).Select(b => data.Maps.Count(m => m.StarBand == b)).ToList(),
                CurveSamples = CurveSamples(candidates),
            };

            Evaluation? reference = null;
            foreach (var settings in candidates)
            {
                var candidateTimer = System.Diagnostics.Stopwatch.StartNew();
                var evaluation = Evaluate(data, settings);
                reference ??= evaluation;
                result.Candidates.Add(Measure(data, settings, evaluation, reference));
                Console.WriteLine($"Curve experiments: {settings} done in {candidateTimer.Elapsed:mm\\:ss}");
            }

            var path = Path.Combine(webRootPath, FileName);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            }));
            Console.WriteLine($"Curve experiments saved to {path} ({timer.Elapsed:hh\\:mm\\:ss})");
        }

        public static CurveExperimentResult? Load(string webRootPath)
        {
            var path = Path.Combine(webRootPath, FileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CurveExperimentResult>(File.ReadAllText(path), new JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            });
        }

        private static async Task<Data> Load(AppContext dbContext)
        {
            var leaderboards = await dbContext.Leaderboards.AsNoTracking().Include(l => l.ModifiersRating).ToListAsync();
            var mapIndex = new Dictionary<string, int>(leaderboards.Count);
            var maps = new MapInfo[leaderboards.Count];
            for (int i = 0; i < leaderboards.Count; i++)
            {
                var lb = leaderboards[i];
                mapIndex[lb.Id] = i;
                // Stars from the base curve, the same for every candidate
                float stars = ReplayUtils.ToStars(lb.AccRating, lb.PassRating, lb.TechRating);
                maps[i] = new MapInfo { Leaderboard = lb, Stars = stars, StarBand = StarBandOf(stars), HalfA = StableHash(lb.Id) % 2 == 0 };
            }

            var scores = await dbContext.Scores.AsNoTracking()
                .Select(s => new { s.LeaderboardId, s.PlayerId, s.Accuracy, s.Modifiers })
                .ToListAsync();

            var playerIndex = new Dictionary<string, int>();
            var modifierStrings = new Dictionary<string, string>();
            var scoreMap = new List<int>(scores.Count);
            var scorePlayer = new List<int>(scores.Count);
            var scoreAcc = new List<float>(scores.Count);
            var scoreModifiers = new List<string>(scores.Count);
            foreach (var s in scores)
            {
                if (s.LeaderboardId == null || s.PlayerId == null || !mapIndex.TryGetValue(s.LeaderboardId, out int map)) continue;
                if (!playerIndex.TryGetValue(s.PlayerId, out int player))
                {
                    player = playerIndex.Count;
                    playerIndex[s.PlayerId] = player;
                }
                var modifiers = s.Modifiers ?? "";
                if (!modifierStrings.TryGetValue(modifiers, out var interned))
                {
                    modifierStrings[modifiers] = interned = modifiers;
                }
                scoreMap.Add(map);
                scorePlayer.Add(player);
                scoreAcc.Add(s.Accuracy);
                scoreModifiers.Add(interned);
            }

            var data = new Data
            {
                Maps = maps,
                ScoreMap = scoreMap.ToArray(),
                ScorePlayer = scorePlayer.ToArray(),
                ScoreAcc = scoreAcc.ToArray(),
                ScoreModifiers = scoreModifiers.ToArray(),
                PlayerIds = new string[playerIndex.Count],
            };
            foreach (var (id, index) in playerIndex) data.PlayerIds[index] = id;

            var names = await dbContext.Players.AsNoTracking().Select(p => new { p.Id, p.Name }).ToListAsync();
            var nameById = new Dictionary<string, string>();
            foreach (var n in names) if (n.Id != null) nameById[n.Id] = n.Name ?? n.Id;
            data.PlayerNames = data.PlayerIds.Select(id => nameById.TryGetValue(id, out var name) ? name : id).ToArray();

            var byMap = new List<int>[maps.Length];
            var byPlayer = new List<int>[playerIndex.Count];
            for (int i = 0; i < maps.Length; i++) byMap[i] = new List<int>();
            for (int i = 0; i < byPlayer.Length; i++) byPlayer[i] = new List<int>();
            for (int s = 0; s < data.ScoreMap.Length; s++)
            {
                byMap[data.ScoreMap[s]].Add(s);
                byPlayer[data.ScorePlayer[s]].Add(s);
            }
            for (int i = 0; i < maps.Length; i++) maps[i].Scores = byMap[i].ToArray();
            data.PlayerScores = byPlayer.Select(l => l.ToArray()).ToArray();
            return data;
        }

        // FNV-1a, so the two halves are the same on every run (string.GetHashCode is randomized per process).
        private static uint StableHash(string text)
        {
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash;
        }

        // ------------------------------------------------------------------
        // Re-score everything with one candidate
        // ------------------------------------------------------------------

        private static Evaluation Evaluate(Data data, CurveSettings settings)
        {
            var generator = new Curve();
            int mapCount = data.Maps.Length;
            var curves = new List<System.Numerics.Vector2>[mapCount];
            var modifierRatings = new ModifiersRating?[mapCount];

            Parallel.For(0, mapCount, i =>
            {
                var lb = data.Maps[i].Leaderboard;
                curves[i] = Generate(generator, lb, settings, lb.PassRating, lb.TechRating, lb.PredictedAcc, lb.AccRating);
                var mod = lb.ModifiersRating;
                if (mod != null)
                {
                    modifierRatings[i] = new ModifiersRating
                    {
                        Id = mod.Id,
                        FSPredictedAcc = mod.FSPredictedAcc, FSPassRating = mod.FSPassRating, FSAccRating = mod.FSAccRating, FSTechRating = mod.FSTechRating, FSStars = mod.FSStars,
                        SSPredictedAcc = mod.SSPredictedAcc, SSPassRating = mod.SSPassRating, SSAccRating = mod.SSAccRating, SSTechRating = mod.SSTechRating, SSStars = mod.SSStars,
                        SFPredictedAcc = mod.SFPredictedAcc, SFPassRating = mod.SFPassRating, SFAccRating = mod.SFAccRating, SFTechRating = mod.SFTechRating, SFStars = mod.SFStars,
                        SSCurve = Generate(generator, lb, settings, mod.SSPassRating, mod.SSTechRating, mod.SSPredictedAcc, mod.SSAccRating),
                        FSCurve = Generate(generator, lb, settings, mod.FSPassRating, mod.FSTechRating, mod.FSPredictedAcc, mod.FSAccRating),
                        SFCurve = Generate(generator, lb, settings, mod.SFPassRating, mod.SFTechRating, mod.SFPredictedAcc, mod.SFAccRating),
                    };
                }
            });

            int scoreCount = data.ScoreMap.Length;
            var e = new Evaluation
            {
                Pp = new float[scoreCount],
                Weight = new float[scoreCount],
                Position = new int[scoreCount],
                PlayerPp = new float[data.PlayerIds.Length],
                PlayerTopPp = new float[data.PlayerIds.Length],
                PlayerRank = new int[data.PlayerIds.Length],
                Curves = curves,
            };

            Parallel.For(0, scoreCount, new ParallelOptions { MaxDegreeOfParallelism = Program.CoreCount }, s =>
            {
                int m = data.ScoreMap[s];
                var lb = data.Maps[m].Leaderboard;
                float pp = ReplayUtils.PpFromScore(data.ScoreAcc[s], data.ScoreModifiers[s], modifierRatings[m],
                    lb.AccRating, lb.PassRating, lb.TechRating, curves[m]).Item1;
                e.Pp[s] = float.IsFinite(pp) ? pp : 0;
            });

            // Weighted pp per player, as PlayersRefresh: 0.965^position
            Parallel.For(0, data.PlayerIds.Length, p =>
            {
                var list = data.PlayerScores[p];
                var ordered = list.OrderByDescending(s => e.Pp[s]).ToArray();
                float total = 0, weight = 1;
                for (int i = 0; i < ordered.Length; i++)
                {
                    int s = ordered[i];
                    e.Weight[s] = weight;
                    e.Position[s] = i;
                    total += e.Pp[s] * weight;
                    weight *= 0.965f;
                }
                e.PlayerPp[p] = total;
                e.PlayerTopPp[p] = ordered.Length > 0 ? e.Pp[ordered[0]] : 0;
            });

            var byPp = Enumerable.Range(0, data.PlayerIds.Length).OrderByDescending(p => e.PlayerPp[p]).ToArray();
            for (int i = 0; i < byPp.Length; i++) e.PlayerRank[byPp[i]] = i + 1;
            return e;
        }

        private static List<System.Numerics.Vector2> Generate(Curve generator, Leaderboard lb, CurveSettings settings,
            float passRating, float techRating, float predictedAcc, float accRating)
        {
            var lack = new LackMapCalculation
            {
                PassRating = passRating,
                TechRating = techRating,
                LowNoteNerf = 1,
                MultiPercentage = lb.MultiPercentage,
                LinearPercentage = lb.LinearPercent,
                Statistics = new Analyzer.BeatmapScanner.Data.Statistics
                {
                    DodgeWalls = lb.DodgeWalls,
                    CrouchWalls = lb.CrouchWalls,
                    ParityErrors = (int)lb.ParityErrors,
                    BombAvoidances = (int)lb.BombAvoidances,
                },
            };
            return generator.GetCurve(lack, predictedAcc, accRating, settings);
        }

        // ------------------------------------------------------------------
        // Metrics
        // ------------------------------------------------------------------

        private static CandidateResult Measure(Data data, CurveSettings settings, Evaluation e, Evaluation reference)
        {
            int mapCount = data.Maps.Length;
            var megametric = new double?[mapCount];
            var maxPp = new double[mapCount];
            var top50Share = new double?[mapCount];
            var bandShare = new double?[mapCount, RankBands.Length];
            // pp from the ratings and the candidate's curve (no scores): at the predicted acc and with 30% fewer misses
            var ppAtPredicted = new double?[mapCount];
            var ppAbovePredicted = new double?[mapCount];
            for (int m = 0; m < mapCount; m++)
            {
                var lb = data.Maps[m].Leaderboard;
                if (lb.PredictedAcc <= 0 || lb.PredictedAcc >= 1) continue;
                ppAtPredicted[m] = ReplayUtils.PpFromScore(lb.PredictedAcc, "", null, lb.AccRating, lb.PassRating, lb.TechRating, e.Curves[m]).Item1;
                ppAbovePredicted[m] = ReplayUtils.PpFromScore(lb.PredictedAcc + 0.3f * (1f - lb.PredictedAcc), "", null, lb.AccRating, lb.PassRating, lb.TechRating, e.Curves[m]).Item1;
            }

            Parallel.For(0, mapCount, m =>
            {
                var scores = data.Maps[m].Scores;
                if (scores.Length == 0) return;
                maxPp[m] = scores.Max(s => e.Pp[s]);
                if (scores.Length < MinScoresPerMap) return;

                top50Share[m] = scores.Count(s => e.Position[s] < ProfileTop) / (double)scores.Length;

                // Rank bands use the reference ranking, so the same players are compared across candidates
                var bandTotal = new int[RankBands.Length];
                var bandTop = new int[RankBands.Length];
                foreach (var s in scores)
                {
                    int band = RankBandOf(reference.PlayerRank[data.ScorePlayer[s]]);
                    bandTotal[band]++;
                    if (e.Position[s] < ProfileTop) bandTop[band]++;
                }
                for (int b = 0; b < RankBands.Length; b++)
                {
                    if (bandTotal[b] >= MinBandScoresPerMap) bandShare[m, b] = bandTop[b] / (double)bandTotal[b];
                }

                // Megametric75, as LeaderboardsRefresh: top 33% by weight of the players with 75+ plays, pp / top pp * weight
                var withTop = scores.Where(s => e.PlayerTopPp[data.ScorePlayer[s]] > 0).ToList();
                int topCount = (int)(withTop.Count * 0.33);
                var filtered = withTop
                    .Where(s => data.PlayerScores[data.ScorePlayer[s]].Length > MegametricMinPlays)
                    .OrderByDescending(s => e.Weight[s])
                    .Take(topCount)
                    .ToList();
                if (filtered.Count > 10)
                {
                    megametric[m] = filtered.Average(s => e.Pp[s] / e.PlayerTopPp[data.ScorePlayer[s]] * e.Weight[s]);
                }
            });

            var result = new CandidateResult { Settings = settings };

            // Per star band
            var topPlayerPp = new List<float>[StarBandCount];
            for (int b = 0; b < StarBandCount; b++) topPlayerPp[b] = new List<float>();
            for (int s = 0; s < data.ScoreMap.Length; s++)
            {
                if (reference.PlayerRank[data.ScorePlayer[s]] <= 100) topPlayerPp[data.Maps[data.ScoreMap[s]].StarBand].Add(e.Pp[s]);
            }

            for (int b = 0; b < StarBandCount; b++)
            {
                var maps = Enumerable.Range(0, mapCount).Where(m => data.Maps[m].StarBand == b).ToList();
                var played = maps.Where(m => data.Maps[m].Scores.Length > 0).ToList();
                var mega = maps.Where(m => megametric[m] != null).Select(m => megametric[m]!.Value).ToList();
                var maxes = played.Select(m => maxPp[m]).ToList();
                double nearTopLine = maxes.Count > 0 ? 0.9 * Percentile(maxes, 0.9) : 0;
                var shares = maps.Where(m => top50Share[m] != null).Select(m => top50Share[m]!.Value).ToList();

                result.ByStars.Add(new StarBandStats
                {
                    Band = StarBandName(b),
                    Maps = maps.Count,
                    MegametricMaps = mega.Count,
                    MegametricMean = Mean(mega),
                    MegametricCv = Cv(mega),
                    MegametricGini = Gini(mega),
                    MaxPpMedian = Median(maxes),
                    MaxPpGini = Gini(maxes),
                    Maps500 = maxes.Count(v => v >= 500),
                    Maps600 = maxes.Count(v => v >= 600),
                    Maps700 = maxes.Count(v => v >= 700),
                    Share700 = maxes.Count > 0 ? maxes.Count(v => v >= 700) / (double)maxes.Count : 0,
                    MapsNearTop = maxes.Count(v => v >= nearTopLine),
                    Top50ShareMedian = Median(shares),
                    PpAtPredictedMedian = Median(maps.Where(m => ppAtPredicted[m] != null).Select(m => ppAtPredicted[m]!.Value).ToList()),
                    PpAbovePredictedMedian = Median(maps.Where(m => ppAbovePredicted[m] != null).Select(m => ppAbovePredicted[m]!.Value).ToList()),
                    TopPlayersMedianPp = Median(topPlayerPp[b].Select(v => (double)v).ToList()),
                    TopPlayersScores = topPlayerPp[b].Count,
                    Top50ShareByRank = Enumerable.Range(0, RankBands.Length)
                        .Select(r => Median(maps.Where(m => bandShare[m, r] != null).Select(m => bandShare[m, r]!.Value).ToList()))
                        .ToList(),
                });
            }

            // Summary
            var sortedPp = e.PlayerPp.OrderByDescending(v => v).ToArray();
            double At(int i) => i < sortedPp.Length ? sortedPp[i] : 0;
            var usedBands = result.ByStars.Where(b => b.MegametricMaps >= 10).ToList();

            int inversions = 0;
            double? previous = null;
            foreach (var band in result.ByStars.Where(b => b.TopPlayersScores >= 30))
            {
                if (previous != null && band.TopPlayersMedianPp < previous) inversions++;
                previous = band.TopPlayersMedianPp;
            }

            int ppInversions = 0, ppAboveInversions = 0;
            double? previousAt = null, previousAbove = null;
            foreach (var band in result.ByStars.Where(b => b.Maps >= 10))
            {
                if (previousAt != null && band.PpAtPredictedMedian < previousAt) ppInversions++;
                if (previousAbove != null && band.PpAbovePredictedMedian < previousAbove) ppAboveInversions++;
                previousAt = band.PpAtPredictedMedian;
                previousAbove = band.PpAbovePredictedMedian;
            }

            result.Summary = new CandidateSummary
            {
                Top1Pp = At(0),
                Top10Pp = At(9),
                Top100Pp = At(99),
                Top1000Pp = At(999),
                RankCorrelationTop1000 = RankCorrelation(reference, e, 1000),
                RankCorrelationAll = RankCorrelation(reference, e, int.MaxValue),
                AverageRankChangeTop100 = AverageRankChange(reference, e, 100),
                MegametricCv = WeightedByMaps(usedBands, b => b.MegametricCv),
                MegametricGini = WeightedByMaps(usedBands, b => b.MegametricGini),
                MaxPpGini = WeightedByMaps(usedBands, b => b.MaxPpGini),
                Top50Share = WeightedByMaps(usedBands, b => b.Top50ShareMedian),
                ProgressionInversions = inversions,
                PpAtPredictedInversions = ppInversions,
                PpAbovePredictedInversions = ppAboveInversions,
                MapsUnder10Stars700 = result.ByStars.Take(10).Sum(b => b.Maps700),
                Share700Spread = Share700Spread(result.ByStars),
                Share700Min = result.ByStars.Where(b => b.Maps >= EvennessMinMaps).Select(b => b.Share700).DefaultIfEmpty(0).Min(),
                Share700Max = result.ByStars.Where(b => b.Maps >= EvennessMinMaps).Select(b => b.Share700).DefaultIfEmpty(0).Max(),
                EasyProfilesTop100 = EasyProfiles(data, e, 100),
                EasyProfilesTop1000 = EasyProfiles(data, e, 1000),
                Top10PpChange = PpChange(reference, e, 10),
                Top50PpChange = PpChange(reference, e, 50),
                Top1000PpChange = PpChange(reference, e, 1000),
                Top10RankChange = SignedRankChange(reference, e, 10),
                Top50RankChange = SignedRankChange(reference, e, 50),
                EliteMargin = EliteMargin(data, reference, 50, 20),
                HalfA = HalfSummary(data, megametric, maxPp, top50Share, true),
                HalfB = HalfSummary(data, megametric, maxPp, top50Share, false),
            };
            result.Risers = Movers(data, e, reference, risers: true);
            result.Fallers = Movers(data, e, reference, risers: false);
            return result;
        }

        // Biggest rank changes among the reference top 1000, with what their top scores are made of before and after.
        private static List<Mover> Movers(Data data, Evaluation e, Evaluation reference, bool risers)
        {
            if (ReferenceEquals(e, reference)) return new();
            var candidates = Enumerable.Range(0, data.PlayerIds.Length)
                .Where(p => reference.PlayerRank[p] <= MoversFromTop)
                .Select(p => (Player: p, Change: reference.PlayerRank[p] - e.PlayerRank[p]))
                .Where(x => risers ? x.Change > 0 : x.Change < 0)
                .OrderByDescending(x => risers ? x.Change : -x.Change)
                .Take(MoversCount);

            var movers = new List<Mover>();
            foreach (var (player, _) in candidates)
            {
                var scores = data.PlayerScores[player];
                movers.Add(new Mover
                {
                    PlayerId = data.PlayerIds[player],
                    Name = data.PlayerNames[player],
                    RankBefore = reference.PlayerRank[player],
                    RankAfter = e.PlayerRank[player],
                    PpBefore = reference.PlayerPp[player],
                    PpAfter = e.PlayerPp[player],
                    StarsSplitBefore = StarsSplit(data, reference, scores),
                    StarsSplitAfter = StarsSplit(data, e, scores),
                    BiggestChanges = scores
                        .OrderByDescending(s => risers ? e.Pp[s] * e.Weight[s] - reference.Pp[s] * reference.Weight[s]
                                                       : reference.Pp[s] * reference.Weight[s] - e.Pp[s] * e.Weight[s])
                        .Take(3)
                        .Select(s =>
                        {
                            var map = data.Maps[data.ScoreMap[s]];
                            return new MoverScore
                            {
                                LeaderboardId = map.Leaderboard.Id,
                                Map = $"{map.Leaderboard.Name} [{map.Leaderboard.DifficultyName}]",
                                Stars = Math.Round(map.Stars, 2),
                                Accuracy = Math.Round(data.ScoreAcc[s] * 100, 2),
                                PredictedAcc = Math.Round(map.Leaderboard.PredictedAcc * 100, 2),
                                PpBefore = Math.Round(reference.Pp[s], 1),
                                PpAfter = Math.Round(e.Pp[s], 1),
                                WeightedBefore = Math.Round(reference.Pp[s] * reference.Weight[s], 1),
                                WeightedAfter = Math.Round(e.Pp[s] * e.Weight[s], 1),
                            };
                        })
                        .ToList(),
                });
            }
            return movers;
        }

        // Share of a player's weighted pp coming from maps under 5 stars, 5 to 9, and 9+.
        private static double[] StarsSplit(Data data, Evaluation e, int[] scores)
        {
            var split = new double[3];
            foreach (var s in scores)
            {
                float stars = data.Maps[data.ScoreMap[s]].Stars;
                split[stars < 5 ? 0 : stars < 9 ? 1 : 2] += e.Pp[s] * e.Weight[s];
            }
            double total = split.Sum();
            return total > 0 ? split.Select(v => Math.Round(v / total * 100, 1)).ToArray() : split;
        }

        // Same per-map metrics on one half of the leaderboards, to check an improvement isn't carried by a few maps.
        private static HalfSummary HalfSummary(Data data, double?[] megametric, double[] maxPp, double?[] top50Share, bool halfA)
        {
            var cvs = new List<(double Value, int Maps)>();
            var ginis = new List<(double Value, int Maps)>();
            var shares = new List<(double Value, int Maps)>();
            for (int b = 0; b < StarBandCount; b++)
            {
                var maps = Enumerable.Range(0, data.Maps.Length).Where(m => data.Maps[m].StarBand == b && data.Maps[m].HalfA == halfA).ToList();
                var mega = maps.Where(m => megametric[m] != null).Select(m => megametric[m]!.Value).ToList();
                if (mega.Count < 10) continue;
                cvs.Add((Cv(mega), mega.Count));
                ginis.Add((Gini(maps.Where(m => data.Maps[m].Scores.Length > 0).Select(m => maxPp[m]).ToList()), mega.Count));
                shares.Add((Median(maps.Where(m => top50Share[m] != null).Select(m => top50Share[m]!.Value).ToList()), mega.Count));
            }
            double Weighted(List<(double Value, int Maps)> list) => list.Sum(x => x.Maps) > 0 ? list.Sum(x => x.Value * x.Maps) / list.Sum(x => x.Maps) : 0;
            return new HalfSummary { MegametricCv = Weighted(cvs), MaxPpGini = Weighted(ginis), Top50Share = Weighted(shares) };
        }

        private static double WeightedByMaps(List<StarBandStats> bands, Func<StarBandStats, double> value)
        {
            int total = bands.Sum(b => b.MegametricMaps);
            return total > 0 ? bands.Sum(b => value(b) * b.MegametricMaps) / total : 0;
        }

        // Spearman correlation between the reference ranking and the candidate's, over the reference's top N players.
        private static double RankCorrelation(Evaluation reference, Evaluation e, int topN)
        {
            var players = Enumerable.Range(0, reference.PlayerRank.Length).Where(p => reference.PlayerRank[p] <= topN).ToArray();
            if (players.Length < 3) return 1;
            var candidateOrder = players.OrderByDescending(p => e.PlayerPp[p]).ToArray();
            var candidateRank = new Dictionary<int, int>(players.Length);
            for (int i = 0; i < candidateOrder.Length; i++) candidateRank[candidateOrder[i]] = i + 1;
            var referenceOrder = players.OrderBy(p => reference.PlayerRank[p]).ToArray();
            double n = players.Length, sum = 0;
            for (int i = 0; i < referenceOrder.Length; i++)
            {
                double d = (i + 1) - candidateRank[referenceOrder[i]];
                sum += d * d;
            }
            return 1 - 6 * sum / (n * (n * n - 1));
        }

        // Coefficient of variation of the share of 700pp+ maps across star ranges with enough maps (0 = the same everywhere)
        private static double Share700Spread(List<StarBandStats> bands)
        {
            var shares = bands.Where(b => b.Maps >= EvennessMinMaps).Select(b => b.Share700).ToList();
            return Cv(shares);
        }

        // Players in the candidate's top N with more than 90% of their weighted pp from maps under 5 stars
        private static int EasyProfiles(Data data, Evaluation e, int topN)
        {
            int count = 0;
            for (int p = 0; p < e.PlayerRank.Length; p++)
            {
                if (e.PlayerRank[p] > topN) continue;
                if (StarsSplit(data, e, data.PlayerScores[p])[0] > 90) count++;
            }
            return count;
        }

        // Average % change of pp for the reference top N players
        private static double PpChange(Evaluation reference, Evaluation e, int topN)
        {
            var players = Enumerable.Range(0, reference.PlayerRank.Length).Where(p => reference.PlayerRank[p] <= topN && reference.PlayerPp[p] > 0).ToList();
            return players.Count > 0 ? players.Average(p => (e.PlayerPp[p] / reference.PlayerPp[p] - 1) * 100) : 0;
        }

        // Average places gained (+) or lost (-) by the reference top N players
        private static double SignedRankChange(Evaluation reference, Evaluation e, int topN)
        {
            var players = Enumerable.Range(0, reference.PlayerRank.Length).Where(p => reference.PlayerRank[p] <= topN).ToList();
            return players.Count > 0 ? players.Average(p => (double)(reference.PlayerRank[p] - e.PlayerRank[p])) : 0;
        }

        // How far the reference top N players are above the predicted acc on their top K scores (today's weighting):
        // average points above, and average share of the predicted misses removed. Same for every candidate.
        private static EliteMarginStats EliteMargin(Data data, Evaluation reference, int topN, int topK)
        {
            var points = new List<double>();
            var shares = new List<double>();
            for (int p = 0; p < reference.PlayerRank.Length; p++)
            {
                if (reference.PlayerRank[p] > topN) continue;
                foreach (var s in data.PlayerScores[p].Where(x => reference.Position[x] < topK))
                {
                    double predicted = data.Maps[data.ScoreMap[s]].Leaderboard.PredictedAcc;
                    if (predicted <= 0 || predicted >= 1) continue;
                    double acc = data.ScoreAcc[s];
                    points.Add((acc - predicted) * 100);
                    shares.Add((acc - predicted) / (1 - predicted));
                }
            }
            return new EliteMarginStats
            {
                Scores = points.Count,
                MeanPoints = points.Count > 0 ? points.Average() : 0,
                MeanMissShare = shares.Count > 0 ? shares.Average() : 0,
                AbovePredictedShare = points.Count > 0 ? points.Count(v => v > 0) / (double)points.Count : 0,
            };
        }

        private static double AverageRankChange(Evaluation reference, Evaluation e, int topN)
        {
            var players = Enumerable.Range(0, reference.PlayerRank.Length).Where(p => reference.PlayerRank[p] <= topN).ToList();
            return players.Count > 0 ? players.Average(p => Math.Abs(e.PlayerRank[p] - reference.PlayerRank[p])) : 0;
        }

        // Shape of each candidate's curve for a few predicted accuracies, relative to today's value at the predicted acc
        // (1 = what a player hitting the predicted acc gets today).
        private static List<CurveSample> CurveSamples(List<CurveSettings> candidates)
        {
            var generator = new Curve();
            var samples = new List<CurveSample>();
            foreach (var predicted in new[] { 0.995, 0.99, 0.985, 0.98, 0.97, 0.96, 0.95 })
            {
                var sample = new CurveSample { PredictedAcc = predicted };
                // A map with an acc rating only (no pass or tech), rated as RatingAPI would: the boost is at its largest there
                double accRating = new AccRating().GetRating(predicted, 0, 0);
                double anchor = ReplayUtils.Curve2((float)predicted, Curve.BaseCurve);
                foreach (var settings in candidates)
                {
                    var curve = generator.GetCurve(new LackMapCalculation(), predicted, accRating, settings);
                    sample.Curves.Add(curve
                        .Where(p => p.X >= predicted - 0.05 && p.X > 0)
                        .OrderBy(p => p.X)
                        .Select(p => new[] { Math.Round(p.X * 100, 2), Math.Round(p.Y / anchor, 4) })
                        .ToList());
                }
                samples.Add(sample);
            }
            return samples;
        }

        // ------------------------------------------------------------------
        // Statistics helpers
        // ------------------------------------------------------------------

        private static double Mean(List<double> values) => values.Count > 0 ? values.Average() : 0;

        private static double Cv(List<double> values)
        {
            if (values.Count < 2) return 0;
            double mean = values.Average();
            if (mean == 0) return 0;
            double variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
            return Math.Sqrt(variance) / mean;
        }

        // 0 = every map gives the same, 1 = one map has everything
        private static double Gini(List<double> values)
        {
            if (values.Count < 2) return 0;
            var sorted = values.Select(v => Math.Max(0, v)).OrderBy(v => v).ToArray();
            double total = sorted.Sum();
            if (total == 0) return 0;
            double weighted = 0;
            for (int i = 0; i < sorted.Length; i++) weighted += (i + 1) * sorted[i];
            int n = sorted.Length;
            return (2 * weighted) / (n * total) - (n + 1.0) / n;
        }

        private static double Median(List<double> values) => Percentile(values, 0.5);

        private static double Percentile(List<double> values, double q)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToArray();
            double position = q * (sorted.Length - 1);
            int low = (int)Math.Floor(position);
            int high = Math.Min(low + 1, sorted.Length - 1);
            return sorted[low] + (position - low) * (sorted[high] - sorted[low]);
        }
    }

    // ------------------------------------------------------------------
    // Saved results (wwwroot/curve-experiments.json)
    // ------------------------------------------------------------------

    public class CurveExperimentResult
    {
        public DateTime GeneratedAt { get; set; }
        public int Leaderboards { get; set; }
        public int Scores { get; set; }
        public int Players { get; set; }
        public List<string> RankBands { get; set; } = new();
        public List<string> StarBands { get; set; } = new();
        public List<int> MapsPerStarBand { get; set; } = new();
        public List<CandidateResult> Candidates { get; set; } = new();
        public List<CurveSample> CurveSamples { get; set; } = new();
    }

    public class CandidateResult
    {
        public CurveSettings Settings { get; set; } = new();
        public CandidateSummary Summary { get; set; } = new();
        public List<StarBandStats> ByStars { get; set; } = new();
        public List<Mover> Risers { get; set; } = new();
        public List<Mover> Fallers { get; set; } = new();
    }

    public class Mover
    {
        public string PlayerId { get; set; } = "";
        public string Name { get; set; } = "";
        public int RankBefore { get; set; }
        public int RankAfter { get; set; }
        public double PpBefore { get; set; }
        public double PpAfter { get; set; }
        // % of weighted pp from maps under 5 stars, 5 to 9, and 9+
        public double[] StarsSplitBefore { get; set; } = Array.Empty<double>();
        public double[] StarsSplitAfter { get; set; } = Array.Empty<double>();
        public List<MoverScore> BiggestChanges { get; set; } = new();
    }

    public class MoverScore
    {
        public string LeaderboardId { get; set; } = "";
        public string Map { get; set; } = "";
        public double Stars { get; set; }
        public double Accuracy { get; set; }
        public double PredictedAcc { get; set; }
        public double PpBefore { get; set; }
        public double PpAfter { get; set; }
        public double WeightedBefore { get; set; }
        public double WeightedAfter { get; set; }
    }

    public class CandidateSummary
    {
        public double Top1Pp { get; set; }
        public double Top10Pp { get; set; }
        public double Top100Pp { get; set; }
        public double Top1000Pp { get; set; }
        public double RankCorrelationTop1000 { get; set; }
        public double RankCorrelationAll { get; set; }
        public double AverageRankChangeTop100 { get; set; }
        // Averages over the star bands with 10+ maps, weighted by maps. Lower CV / Gini = value spread more evenly over maps.
        public double MegametricCv { get; set; }
        public double MegametricGini { get; set; }
        public double MaxPpGini { get; set; }
        // Median share of a map's scores that sit in their player's top 50. Higher = the map is worth pp to more players.
        public double Top50Share { get; set; }
        // Star bands where the top 100 players' median pp is lower than in the band below (should be 0)
        public int ProgressionInversions { get; set; }
        // Same check on the pp a map gives at its predicted acc, and with 30% fewer misses, from its ratings (should be 0)
        public int PpAtPredictedInversions { get; set; }
        public int PpAbovePredictedInversions { get; set; }
        // Maps under 10 stars whose best score is worth 700pp+
        public int MapsUnder10Stars700 { get; set; }
        // Elite check: average % pp change and average places gained (+) / lost (-) of today's top 10 / top 50 / top 1000
        public double Top10PpChange { get; set; }
        public double Top50PpChange { get; set; }
        public double Top1000PpChange { get; set; }
        public double Top10RankChange { get; set; }
        public double Top50RankChange { get; set; }
        public EliteMarginStats EliteMargin { get; set; } = new();
        // Spread (CV) of the share of 700pp+ maps across star ranges with 30+ maps, lower = more similar; and its range
        public double Share700Spread { get; set; }
        public double Share700Min { get; set; }
        public double Share700Max { get; set; }
        // Players in the candidate's top 100 / top 1000 with more than 90% of their weighted pp from maps under 5 stars
        public int EasyProfilesTop100 { get; set; }
        public int EasyProfilesTop1000 { get; set; }
        public HalfSummary HalfA { get; set; } = new();
        public HalfSummary HalfB { get; set; } = new();
    }

    // How far today's top 50 are above the predicted acc on their top 20 scores
    public class EliteMarginStats
    {
        public int Scores { get; set; }
        public double MeanPoints { get; set; }
        public double MeanMissShare { get; set; }
        public double AbovePredictedShare { get; set; }
    }

    public class HalfSummary
    {
        public double MegametricCv { get; set; }
        public double MaxPpGini { get; set; }
        public double Top50Share { get; set; }
    }

    public class StarBandStats
    {
        public string Band { get; set; } = "";
        public int Maps { get; set; }
        public int MegametricMaps { get; set; }
        public double MegametricMean { get; set; }
        public double MegametricCv { get; set; }
        public double MegametricGini { get; set; }
        // Best pp set on each map
        public double MaxPpMedian { get; set; }
        public double MaxPpGini { get; set; }
        public int Maps500 { get; set; }
        public int Maps600 { get; set; }
        public int Maps700 { get; set; }
        // Share of the range's played maps whose best score is worth 700pp+
        public double Share700 { get; set; }
        // Maps whose best pp is within 10% of the band's 90th percentile
        public int MapsNearTop { get; set; }
        public double Top50ShareMedian { get; set; }
        public List<double> Top50ShareByRank { get; set; } = new();
        public double TopPlayersMedianPp { get; set; }
        public int TopPlayersScores { get; set; }
        // Median pp at the predicted acc and with 30% fewer misses than predicted, from the ratings and the curve
        public double PpAtPredictedMedian { get; set; }
        public double PpAbovePredictedMedian { get; set; }
    }

    public class CurveSample
    {
        public double PredictedAcc { get; set; }
        // One list per candidate, same order as Candidates: [accuracy %, value / value at the predicted acc]
        public List<List<double[]>> Curves { get; set; } = new();
    }
}
