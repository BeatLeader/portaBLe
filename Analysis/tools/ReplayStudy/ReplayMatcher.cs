using Analyzer.BeatmapScanner.Data;
using ReplayDecoder;

namespace ReplayStudy
{
    /// <summary>Observed data for one predicted swing, aggregated across replays.</summary>
    public class SwingObservation
    {
        public List<double> CutAngles = new();        // one circular-mean angle per replay that good-cut this swing
        public List<double> TransitionAngles = new(); // angle between this and the previous same-hand swing, per replay

        // Transition classification vs the previous same-hand swing.
        // A physical reset repeats the direction (~0deg), clean alternation reverses it (~180deg);
        // the 45..135deg middle is a tech transition that cut data alone cannot classify.
        public int AlternatedCount;                   // transition > 135deg (clear alternation)
        public int SameDirectionCount;                // transition < 45deg  (clear physical reset)
        public int AmbiguousCount;                    // 45..135deg

        // Frame-verified classification from direction-aware arm turnarounds between
        // the two cuts (a speed dip where the velocity direction flips > 90deg across
        // it): >=2 turnarounds = physical double-swing (reset), <=1 = continuous
        // motion. A "roll" is continuous motion that still repeats the cut direction
        // (wrist loop) — parity-legal, not a reset.
        public int FrameAlternated;
        public int FrameReset;
        public int FrameRoll;
        public List<double> TurnaroundCounts = new();  // per replay, for offline recalibration
        public List<double> MinSpeeds = new();         // per replay: min tip speed between the cuts

        // Slider/grouping ground truth: how players actually time the cuts.
        public List<double> CutGaps = new();          // per replay: seconds between this and the previous same-hand cut
        public List<double> IntraSwingSpread = new(); // per replay: eventTime spread across this swing's own notes (multi-note swings)

        public List<double> SaberSpeeds = new();
        public List<double> TimeDeviations = new();
        public List<double> CutDirDeviations = new();
        public List<double> BeforeCutRatings = new();
        public List<double> AfterCutRatings = new();
        public List<double> DistancesToCenter = new();
        public int MissCount;
        public int BadCutCount;
    }

    /// <summary>One note event of one replay (good / bad / miss) with the raw cut data.</summary>
    public class NoteObsRow
    {
        public float Spawn, EventTime;
        public int Color, X, Y, CutDir, ScoringType, EventType, SwingIndex = -1;
        public bool HasCut;
        public int Pre, Post, Acc;
        public float BeforeR, AfterR, Dist, SaberSpeed, TimeDev, CutDirDev, Angle, AngleZ;
        public float TipLen = float.NaN, TipPeak = float.NaN, Gap = float.NaN;
        public int TipTurn = -1;
    }

    public class ReplayMatchResult
    {
        public int MatchedGoodCuts;
        public int TotalGoodCuts;
        public List<NoteObsRow> Rows = new();
    }

    public class ReplayMatcher
    {
        private const float MATCH_TOLERANCE_SECONDS = 0.02f;
        private const double CLEAR_RESET_MAX_ANGLE = 45;
        private const double CLEAR_ALTERNATION_MIN_ANGLE = 135;

        // Beyond this gap the hand moves freely between notes (idle waving, repositioning);
        // reset-vs-alternation is not well defined and the rating impact is negligible.
        private const float MAX_TRANSITION_GAP_SECONDS = 1.0f;

        private readonly List<SwingData> _swings;
        private readonly List<(float seconds, int x, int y, int swingIndex)>[] _cubesByColor;
        public readonly SwingObservation[] Observations;

        public ReplayMatcher(List<SwingData> swings)
        {
            _swings = swings;
            Observations = new SwingObservation[swings.Count];
            for (int i = 0; i < swings.Count; i++) Observations[i] = new SwingObservation();

            _cubesByColor = new[] { new List<(float, int, int, int)>(), new List<(float, int, int, int)>() };
            for (int i = 0; i < swings.Count; i++)
            {
                foreach (var cube in swings[i].Cubes)
                {
                    if (cube.Type == 0 || cube.Type == 1)
                    {
                        _cubesByColor[cube.Type].Add((cube.Seconds, cube.X, cube.Y, i));
                    }
                }
            }
            foreach (var list in _cubesByColor) list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        }

        private int FindSwing(int color, float spawnTime, int lineIndex, int lineLayer)
        {
            var cubes = _cubesByColor[color];
            int lo = 0, hi = cubes.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (cubes[mid].seconds < spawnTime - MATCH_TOLERANCE_SECONDS) lo = mid + 1;
                else hi = mid;
            }
            for (int i = lo; i < cubes.Count && cubes[i].seconds <= spawnTime + MATCH_TOLERANCE_SECONDS; i++)
            {
                if (cubes[i].x == lineIndex && cubes[i].y == lineLayer) return cubes[i].swingIndex;
            }
            return -1;
        }

        /// <summary>Match one replay's note events onto the predicted swings and add to the aggregates.</summary>
        public ReplayMatchResult ProcessReplay(Replay replay)
        {
            var result = new ReplayMatchResult();

            // Per-swing cut data for this single replay
            var goodCuts = new Dictionary<int, List<NoteEvent>>();

            foreach (var e in replay.notes)
            {
                if (e.eventType == NoteEventType.bomb) continue;

                var p = new NoteParams(e.noteID, e.eventType);
                if (p.colorType != 0 && p.colorType != 1) continue;

                int swingIndex = FindSwing(p.colorType, e.spawnTime, p.lineIndex, p.noteLineLayer);

                var row = new NoteObsRow
                {
                    Spawn = e.spawnTime, EventTime = e.eventTime, Color = p.colorType, X = p.lineIndex, Y = p.noteLineLayer,
                    CutDir = p.cutDirection, ScoringType = (int)p.scoringType, EventType = (int)e.eventType, SwingIndex = swingIndex,
                };
                if (e.eventType == NoteEventType.good && e.noteCutInfo != null)
                {
                    var ci = e.noteCutInfo;
                    var sc = NoteScore.CalculateNoteScore(ci, p.scoringType);
                    row.HasCut = true;
                    row.Pre = sc.pre_score; row.Post = sc.post_score; row.Acc = sc.acc_score;
                    row.BeforeR = ci.beforeCutRating; row.AfterR = ci.afterCutRating; row.Dist = ci.cutDistanceToCenter;
                    row.SaberSpeed = ci.saberSpeed; row.TimeDev = ci.timeDeviation; row.CutDirDev = ci.cutDirDeviation;
                    double a = Math.Atan2(ci.saberDir.y, ci.saberDir.x) * 180.0 / Math.PI;
                    row.Angle = (float)(a < 0 ? a + 360 : a);
                    row.AngleZ = ci.saberDir.z;
                }
                result.Rows.Add(row);

                if (swingIndex < 0) continue;

                switch (e.eventType)
                {
                    case NoteEventType.good:
                        result.TotalGoodCuts++;
                        if (e.noteCutInfo == null) continue;
                        result.MatchedGoodCuts++;
                        if (!goodCuts.TryGetValue(swingIndex, out var events))
                        {
                            events = new List<NoteEvent>();
                            goodCuts[swingIndex] = events;
                        }
                        events.Add(e);
                        break;

                    case NoteEventType.miss:
                        Observations[swingIndex].MissCount++;
                        break;

                    case NoteEventType.bad:
                        Observations[swingIndex].BadCutCount++;
                        break;
                }
            }

            // Aggregate per swing, and classify the transition vs the immediately previous
            // same-hand swing (only when both were good-cut in this replay, so a miss
            // in between doesn't produce a bogus transition).
            var prevByHand = new (double angle, float cutTime, bool valid)[2];

            for (int i = 0; i < _swings.Count; i++)
            {
                int hand = _swings[i].Cubes[0].Type;
                if (hand != 0 && hand != 1) continue;

                if (!goodCuts.TryGetValue(i, out var events))
                {
                    prevByHand[hand] = (0, 0, false);
                    continue;
                }

                var angles = new List<double>(events.Count);
                float lastCutTime = 0;
                var obs = Observations[i];

                if (events.Count > 1)
                {
                    float minT = float.MaxValue, maxT = float.MinValue;
                    foreach (var e in events)
                    {
                        if (e.eventTime < minT) minT = e.eventTime;
                        if (e.eventTime > maxT) maxT = e.eventTime;
                    }
                    obs.IntraSwingSpread.Add(maxT - minT);
                }

                foreach (var e in events)
                {
                    var info = e.noteCutInfo;
                    double angle = Math.Atan2(info.saberDir.y, info.saberDir.x) * 180.0 / Math.PI;
                    if (angle < 0) angle += 360;
                    angles.Add(angle);

                    obs.SaberSpeeds.Add(info.saberSpeed);
                    obs.TimeDeviations.Add(info.timeDeviation);
                    obs.CutDirDeviations.Add(info.cutDirDeviation);
                    obs.BeforeCutRatings.Add(info.beforeCutRating);
                    obs.AfterCutRatings.Add(info.afterCutRating);
                    obs.DistancesToCenter.Add(info.cutDistanceToCenter);

                    if (e.eventTime > lastCutTime) lastCutTime = e.eventTime;
                }

                double meanAngle = CircularMean(angles);
                obs.CutAngles.Add(meanAngle);

                var lastEvent = events[events.Count - 1];
                if (prevByHand[hand].valid && lastEvent.eventTime - prevByHand[hand].cutTime <= MAX_TRANSITION_GAP_SECONDS)
                {
                    double diff = AngleDifference(meanAngle, prevByHand[hand].angle);
                    obs.TransitionAngles.Add(diff);
                    obs.CutGaps.Add(lastEvent.eventTime - prevByHand[hand].cutTime);
                    if (diff > CLEAR_ALTERNATION_MIN_ANGLE) obs.AlternatedCount++;
                    else if (diff < CLEAR_RESET_MAX_ANGLE) obs.SameDirectionCount++;
                    else obs.AmbiguousCount++;

                    // Frame-verified classification: count arm turnarounds between the cuts.
                    // A map-demanded reset must also REPEAT the direction (diff < 90) —
                    // extra turnarounds on a direction-changing transition are comfort
                    // motion (returning to rest in a generous gap), not a reset.
                    var motion = AnalyzeTipMotion(replay, hand, prevByHand[hand].cutTime, lastEvent.eventTime);
                    if (motion.turnarounds >= 0)
                    {
                        if (motion.turnarounds >= 2 && diff < 90) obs.FrameReset++;
                        else if (motion.turnarounds < 2 && diff < CLEAR_RESET_MAX_ANGLE) obs.FrameRoll++;
                        else obs.FrameAlternated++;
                        obs.TurnaroundCounts.Add(motion.turnarounds);
                        obs.MinSpeeds.Add(motion.minSpeed);
                    }
                }

                prevByHand[hand] = (meanAngle, lastCutTime, true);
            }

            AddTipMetrics(replay, result.Rows);
            return result;
        }

        /// <summary>
        /// For every good cut with a previous same-hand good cut less than 1s earlier (and more than 50ms, i.e. a
        /// different swing), records the physical tip travel between the two cuts: path length (m), peak speed (m/s)
        /// and the number of direction-reversing turnarounds. This is the replay-side ground truth for the
        /// analyzer's geometric swing features (hit distance, repositioning, rotation, frequency).
        /// </summary>
        private static void AddTipMetrics(Replay replay, List<NoteObsRow> rows)
        {
            for (int hand = 0; hand < 2; hand++)
            {
                var cuts = rows.Where(r => r.HasCut && r.Color == hand).OrderBy(r => r.EventTime).ToList();
                for (int i = 1; i < cuts.Count; i++)
                {
                    float gap = cuts[i].EventTime - cuts[i - 1].EventTime;
                    cuts[i].Gap = gap;
                    if (gap < 0.05f || gap > MAX_TRANSITION_GAP_SECONDS) continue;
                    var (len, peak) = TipTravel(replay, hand, cuts[i - 1].EventTime, cuts[i].EventTime);
                    cuts[i].TipLen = len;
                    cuts[i].TipPeak = peak;
                    cuts[i].TipTurn = AnalyzeTipMotion(replay, hand, cuts[i - 1].EventTime, cuts[i].EventTime).turnarounds;
                }
            }
        }

        private static (float length, float peak) TipTravel(Replay replay, int hand, float t0, float t1)
        {
            var frames = replay.frames;
            if (frames == null || frames.Count < 3 || t1 <= t0) return (float.NaN, float.NaN);
            int lo = 0, hi = frames.Count;
            while (lo < hi) { int mid = (lo + hi) / 2; if (frames[mid].time < t0) lo = mid + 1; else hi = mid; }
            if (lo <= 0) lo = 1;
            var prev = TipPosition(hand == 0 ? frames[lo - 1].leftHand : frames[lo - 1].rightHand);
            float prevT = frames[lo - 1].time, length = 0, peak = 0;
            int n = 0;
            for (int i = lo; i < frames.Count && frames[i].time <= t1; i++)
            {
                var tip = TipPosition(hand == 0 ? frames[i].leftHand : frames[i].rightHand);
                float dx = tip.x - prev.x, dy = tip.y - prev.y, dz = tip.z - prev.z;
                float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                float dt = frames[i].time - prevT;
                length += d;
                if (dt > 1e-5f) { float v = d / dt; if (v > peak) peak = v; }
                prev = tip; prevT = frames[i].time; n++;
            }
            return n < 2 ? (float.NaN, float.NaN) : (length, peak);
        }

        /// <summary>
        /// Counts direction-aware arm turnarounds between two cut times. The motion is
        /// segmented at speed dips (thresholds relative to the window's peak speed so
        /// slow play doesn't over-trigger); a turnaround is counted only when the mean
        /// velocity direction flips more than 90 degrees across the dip. A pause that
        /// continues in the same direction is not a turnaround; a continuous roll has
        /// none; a physical reset has two. Returns turnarounds = -1 when unusable.
        /// </summary>
        private static (int turnarounds, double minSpeed) AnalyzeTipMotion(Replay replay, int hand, float t0, float t1)
        {
            var frames = replay.frames;
            if (frames == null || frames.Count < 3 || t1 <= t0) return (-1, 0);

            // Binary search for the first frame at or after t0
            int lo = 0, hi = frames.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (frames[mid].time < t0) lo = mid + 1;
                else hi = mid;
            }
            if (lo <= 0) lo = 1;
            int end = lo;
            while (end < frames.Count && frames[end].time <= t1) end++;
            if (end - lo < 2) return (-1, 0);

            // Pass 1: velocities and peak speed
            int n = end - (lo - 1) - 1;
            Span<float> vx = n <= 256 ? stackalloc float[n] : new float[n];
            Span<float> vy = n <= 256 ? stackalloc float[n] : new float[n];
            Span<float> vz = n <= 256 ? stackalloc float[n] : new float[n];
            Span<float> speed = n <= 256 ? stackalloc float[n] : new float[n];

            float peak = 0;
            double minSpeed = double.MaxValue;
            var prevTip = TipPosition(hand == 0 ? frames[lo - 1].leftHand : frames[lo - 1].rightHand);
            float prevTime = frames[lo - 1].time;
            int count = 0;

            for (int i = lo; i < end; i++)
            {
                var hd = hand == 0 ? frames[i].leftHand : frames[i].rightHand;
                var tip = TipPosition(hd);
                float dt = frames[i].time - prevTime;
                if (dt > 1e-5f)
                {
                    vx[count] = (tip.x - prevTip.x) / dt;
                    vy[count] = (tip.y - prevTip.y) / dt;
                    vz[count] = (tip.z - prevTip.z) / dt;
                    speed[count] = MathF.Sqrt(vx[count] * vx[count] + vy[count] * vy[count] + vz[count] * vz[count]);
                    if (speed[count] > peak) peak = speed[count];
                    if (speed[count] < minSpeed) minSpeed = speed[count];
                    count++;
                }
                prevTip = tip;
                prevTime = frames[i].time;
            }

            if (count < 2 || peak < 2.5f) return (-1, 0);

            float enter = Math.Clamp(0.15f * peak, 1.5f, 4.0f);
            float exit = Math.Min(2f * enter, 0.6f * peak);

            // Pass 2: segment at dips, accumulate speed-weighted mean direction per segment
            var segments = new List<(float x, float y, float z)>();
            float sx = 0, sy = 0, sz = 0;
            bool inDip = false, hasSegment = false;

            for (int i = 0; i < count; i++)
            {
                if (!inDip)
                {
                    if (speed[i] < enter)
                    {
                        if (hasSegment)
                        {
                            segments.Add((sx, sy, sz));
                            sx = sy = sz = 0;
                            hasSegment = false;
                        }
                        inDip = true;
                    }
                    else
                    {
                        sx += vx[i]; sy += vy[i]; sz += vz[i];
                        hasSegment = true;
                    }
                }
                else if (speed[i] > exit)
                {
                    inDip = false;
                    sx += vx[i]; sy += vy[i]; sz += vz[i];
                    hasSegment = true;
                }
            }
            if (hasSegment) segments.Add((sx, sy, sz));

            int turnarounds = 0;
            for (int i = 1; i < segments.Count; i++)
            {
                var a = segments[i - 1];
                var b = segments[i];
                float la = MathF.Sqrt(a.x * a.x + a.y * a.y + a.z * a.z);
                float lb = MathF.Sqrt(b.x * b.x + b.y * b.y + b.z * b.z);
                if (la < 1e-3f || lb < 1e-3f) continue;
                float dot = (a.x * b.x + a.y * b.y + a.z * b.z) / (la * lb);
                if (dot < 0) turnarounds++;  // direction flipped more than 90 degrees
            }

            return (turnarounds, minSpeed == double.MaxValue ? 0 : minSpeed);
        }

        private static (float x, float y, float z) TipPosition(Transform hand)
        {
            // tip = position + rotation * (0, 0, 1)
            var q = hand.rotation;
            // rotate forward vector by quaternion: v' = v + 2q.w*(q.xyz x v) + 2*(q.xyz x (q.xyz x v))
            float tx = 2f * q.y;          // 2 * (q.xyz x (0,0,1)) = 2 * (q.y, -q.x, 0)
            float ty = 2f * -q.x;
            float fx = tx * q.w + (q.y * 0f - q.z * ty);
            float fy = ty * q.w + (q.z * tx - q.x * 0f);
            float fz = 1f + (q.x * ty - q.y * tx);
            return (hand.position.x + fx, hand.position.y + fy, hand.position.z + fz);
        }

        public static double CircularMean(List<double> anglesDeg)
        {
            double sx = 0, sy = 0;
            foreach (var a in anglesDeg)
            {
                sx += Math.Cos(a * Math.PI / 180);
                sy += Math.Sin(a * Math.PI / 180);
            }
            double mean = Math.Atan2(sy, sx) * 180 / Math.PI;
            return mean < 0 ? mean + 360 : mean;
        }

        public static double CircularStd(List<double> anglesDeg)
        {
            if (anglesDeg.Count < 2) return 0;
            double sx = 0, sy = 0;
            foreach (var a in anglesDeg)
            {
                sx += Math.Cos(a * Math.PI / 180);
                sy += Math.Sin(a * Math.PI / 180);
            }
            double r = Math.Sqrt(sx * sx + sy * sy) / anglesDeg.Count;
            if (r <= 0.0001) return 180;
            if (r >= 1) return 0;
            return Math.Sqrt(-2 * Math.Log(r)) * 180 / Math.PI;
        }

        public static double AngleDifference(double a, double b)
        {
            double diff = Math.Abs(a - b) % 360;
            return diff > 180 ? 360 - diff : diff;
        }
    }
}
