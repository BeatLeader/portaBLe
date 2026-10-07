using portaBLe.DB;
using System.ComponentModel;
using System.Reflection;

namespace portaBLe.Refresh
{
    public enum CurveMode
    {
        /// <summary>Production point lists (Curve for ratings, Curve2 for PP).</summary>
        Classic,
        /// <summary>Constant-exponent curve in error rate: ((1 - acc + Epsilon) / (0.05 + Epsilon))^-Gamma (= 1 at 95 %).</summary>
        PowerLaw,
    }

    /// <summary>
    /// Accuracy curve used for acc ratings and acc PP. PowerLaw makes acc PP skill-consistent across maps (skill acts
    /// multiplicatively on error rate, see Analysis/REPORT.md section 5); Gamma/Epsilon are the best constant-exponent
    /// fit to Curve2, AccScale keeps the top-1000 players' PP level unchanged.
    /// With RelativeEpsilon k > 0 the curve is per map: its offset is Epsilon + k * (1 - predictedAcc), so acc PP depends on
    /// the error ratio r = (1 - acc) / (1 - predictedAcc) alone (up to Epsilon) and saturates smoothly at ((1 + k) / k)^Gamma
    /// times the PP at the predicted accuracy, instead of being cut off by AccCap. AccRating is then anchored at the
    /// predicted accuracy itself (no +0.0022 shift, which would cost easy maps up to 15 %).
    /// </summary>
    public static class PpCurve
    {
        public static CurveMode Mode = CurveMode.Classic;
        public static float Gamma = 0.601f;
        public static float Epsilon = 0.0016f;
        public static float RelativeEpsilon = 0f;
        public static float AccScale = 1.052f;

        public static bool PerMap => RelativeEpsilon > 0;

        public static float PowerLaw(float acc, float predictedAcc = 0)
        {
            float eps = Epsilon + (PerMap && predictedAcc > 0 ? RelativeEpsilon * (1f - predictedAcc) : 0f);
            return MathF.Pow(MathF.Max(1f - acc + eps, 1e-6f) / (0.05f + eps), -Gamma);
        }
    }

    /// <summary>
    /// Pass PP rewards getting through a map; when a score's accuracy shows that passing was not the limit, that reward fades.
    /// h = log((1 - predictedAcc) / (1 - accuracy)) is how much lower the score's error rate is than the map's predicted error
    /// rate (the reference player of the acc model; speed modifiers use their own prediction). Pass PP is kept in full for
    /// h <= Center and decays as Floor + (1 - Floor) * exp(-Strength * (h - Center)) above it. Off unless enabled.
    /// </summary>
    public static class PassFade
    {
        public static bool Enabled = false;
        public static float Strength = 1f;
        public static float Floor = 0f;
        public static float Center = -1.5f;

        public static float Weight(float accuracy, float predictedAcc)
        {
            if (!Enabled || predictedAcc <= 0 || accuracy <= 0) return 1f;
            float h = MathF.Log(MathF.Max(1f - predictedAcc, 1e-4f) / MathF.Max(1f - accuracy, 1e-4f));
            if (h <= Center) return 1f;
            return Floor + (1f - Floor) * MathF.Exp(-Strength * (h - Center));
        }
    }

    /// <summary>
    /// How pass PP combines with acc + tech PP. P = 1 is the production sum. P > 1 takes the p-norm
    /// (pass^P + (acc + tech)^P)^(1/P): pass PP dominates while acc/tech PP is small (low accuracy) and merges into it as accuracy
    /// rises, so a score is paid mainly for what it shows instead of for passing and accuracy on top of each other. It is monotone in
    /// accuracy by construction (both terms grow or stay) and smooth, with no trigger point. It replaces PassFade, whose subtractive decay
    /// let PP fall as accuracy rose (up to 90 PP on SF ratings) and flattened the curve where the fade began. The parts are reported as
    /// their Euler shares, pass^P / total^(P-1) and acc/tech * ((acc + tech) / total)^(P-1), which add up to the total. Off unless P > 1.
    /// </summary>
    public static class PassBlend
    {
        public static float P = 1f;
        public static bool Enabled => P > 1f;

        public static (float, float, float) Apply(float passPP, float accPP, float techPP)
        {
            float rest = accPP + techPP;
            if (!Enabled || passPP <= 0 || rest <= 0) return (passPP, accPP, techPP);
            float total = MathF.Pow(MathF.Pow(passPP, P) + MathF.Pow(rest, P), 1f / P);
            float restShare = MathF.Pow(rest / total, P - 1f);
            return (passPP * MathF.Pow(passPP / total, P - 1f), accPP * restShare, techPP * restShare);
        }
    }

    /// <summary>
    /// Caps how much skill one score can show on one map: accuracy beyond an error rate MaxAdvantage (log units) below the
    /// map's predicted error rate earns no further acc/tech PP. Without it, near-perfect scores on short easy maps (best of many
    /// tries, few notes) outscore the hardest plays: h > 0.75 occurs only on Easy/Normal maps, while the best hard-map top
    /// plays reach about 0.58. Off unless enabled.
    /// </summary>
    public static class AccCap
    {
        public static bool Enabled = false;
        public static float MaxAdvantage = 0.6f;

        public static float Apply(float accuracy, float predictedAcc)
        {
            if (!Enabled || predictedAcc <= 0) return accuracy;
            return MathF.Min(accuracy, 1f - (1f - predictedAcc) * MathF.Exp(-MaxAdvantage));
        }
    }

    static class ReplayUtils
    {
        static List<(double, double)> pointList = new List<(double, double)> { 
                (1.0, 7.424),
                (0.999, 6.241),
                (0.9975, 5.158),
                (0.995, 4.010),
                (0.9925, 3.241),
                (0.99, 2.700),
                (0.9875, 2.303),
                (0.985, 2.007),
                (0.9825, 1.786),
                (0.98, 1.618),
                (0.9775, 1.490),
                (0.975, 1.392),
                (0.9725, 1.315),
                (0.97, 1.256),
                (0.965, 1.167),
                (0.96, 1.101),
                (0.955, 1.047),
                (0.95, 1.000),
                (0.94, 0.919),
                (0.93, 0.847),
                (0.92, 0.786),
                (0.91, 0.734),
                (0.9, 0.692),
                (0.875, 0.606),
                (0.85, 0.537),
                (0.825, 0.480),
                (0.8, 0.429),
                (0.75, 0.345),
                (0.7, 0.286),
                (0.65, 0.246),
                (0.6, 0.217),
                (0.0, 0.000) };

        static List<(double, double)> pointList2 = new List<(double, double)> { 
                (1.0, 7.424),
                (0.999, 6.241),
                (0.9975, 5.158),
                (0.995, 4.010),
                (0.9925, 3.241),
                (0.99, 2.700),
                (0.9875, 2.303),
                (0.985, 2.007),
                (0.9825, 1.786),
                (0.98, 1.618),
                (0.9775, 1.490),
                (0.975, 1.392),
                (0.9725, 1.315),
                (0.97, 1.256),
                (0.965, 1.167),
                (0.96, 1.094),
                (0.955, 1.039),
                (0.95, 1.000),
                (0.94, 0.931),
                (0.93, 0.867),
                (0.92, 0.813),
                (0.91, 0.768),
                (0.9, 0.729),
                (0.875, 0.650),
                (0.85, 0.581),
                (0.825, 0.522),
                (0.8, 0.473),
                (0.75, 0.404),
                (0.7, 0.345),
                (0.65, 0.296),
                (0.6, 0.256),
                (0.0, 0.000), };

        public static float Curve(float acc, float predictedAcc = 0)
        {
            if (PpCurve.Mode == CurveMode.PowerLaw) return PpCurve.PowerLaw(acc, predictedAcc);
            int i = 0;
            for (; i < pointList.Count; i++)
            {
                if (pointList[i].Item1 <= acc) {
                    break;
                }
            }
    
            if (i == 0) {
                i = 1;
            }
    
            double middle_dis = (acc - pointList[i-1].Item1) / (pointList[i].Item1 - pointList[i-1].Item1);
            return (float)(pointList[i-1].Item2 + middle_dis * (pointList[i].Item2 - pointList[i-1].Item2));
        }

        public static float Curve2(float acc, float predictedAcc = 0)
        {
            if (PpCurve.Mode == CurveMode.PowerLaw) return PpCurve.PowerLaw(acc, predictedAcc);
            int i = 0;
            for (; i < pointList2.Count; i++)
            {
                if (pointList2[i].Item1 <= acc) {
                    break;
                }
            }
    
            if (i == 0) {
                i = 1;
            }
    
            double middle_dis = (acc - pointList2[i-1].Item1) / (pointList2[i].Item1 - pointList2[i-1].Item1);
            return (float)(pointList2[i-1].Item2 + middle_dis * (pointList2[i].Item2 - pointList2[i-1].Item2));
        }

        public static float AccRating(float? predictedAcc, float? passRating, float? techRating) {
            float difficulty_to_acc;
            if (predictedAcc > 0) {
                float pred = predictedAcc ?? 0;
                difficulty_to_acc = 15.5f / Curve(pred + (PpCurve.Mode == CurveMode.PowerLaw && PpCurve.PerMap ? 0f : 0.0022f), pred);
                if (PpCurve.Mode == CurveMode.PowerLaw) difficulty_to_acc *= PpCurve.AccScale;
            } else {
                float tiny_tech = 0.0208f * (techRating ?? 0) + 1.1284f;
                difficulty_to_acc = (-MathF.Pow(tiny_tech, -(passRating ?? 0)) + 1) * 8 + 2 + 0.01f * (techRating ?? 0) * (passRating ?? 0);
            }
            if (float.IsInfinity(difficulty_to_acc) || float.IsNaN(difficulty_to_acc) || float.IsNegativeInfinity(difficulty_to_acc)) {
                difficulty_to_acc = 0;
            }
            return difficulty_to_acc;
        }

        private static float Inflate(float peepee) {
            return 650f * MathF.Pow(peepee, 1.3f) / MathF.Pow(650f, 1.3f);
        }

        /// <summary>
        /// Pass/acc/tech PP of an accuracy on a map whose acc model predicts <paramref name="predictedAcc"/> (0 = unknown):
        /// the map's curve, AccCap on acc/tech and PassFade on pass, both judged on the capped accuracy (so pass PP stops fading
        /// where acc PP stops growing), then PassBlend.
        /// </summary>
        private static (float, float, float) GetPp(float accuracy, float accRating, float passRating, float techRating, float predictedAcc = 0) {

            float passPP = 15.2f * MathF.Exp(MathF.Pow(passRating, 1 / 2.62f)) - 30f;
            if (float.IsInfinity(passPP) || float.IsNaN(passPP) || float.IsNegativeInfinity(passPP) || passPP < 0)
            {
                passPP = 0;
            }
            accuracy = AccCap.Apply(accuracy, predictedAcc);
            passPP *= PassFade.Weight(accuracy, predictedAcc);
            float accPP = Curve2(accuracy, predictedAcc) * accRating * 34f;
            // https://www.desmos.com/calculator/jdpmaozieo
            float techPP = MathF.Exp(1.9f * accuracy) * 1.08f * techRating;

            return PassBlend.Apply(passPP, accPP, techPP);
        }

        /// <summary>PP of a 96 % score / 52, with the same curve, fade and cap as scores on this map.</summary>
        public static float ToStars(float accRating, float passRating, float techRating, float predictedAcc = 0) {
            (float passPP, float accPP, float techPP) = GetPp(0.96f, accRating, passRating, techRating, predictedAcc);

            return Inflate(passPP + accPP + techPP) / 52f;
        }

        public static (float, float, float, float, float) PpFromScore(
            float accuracy, 
            string modifiers, 
            ModifiersRating? modifiersRating,
            float accRating, 
            float passRating, 
            float techRating,
            float predictedAcc = 0)
        {
            if (accuracy <= 0 || accuracy > 1) return (0, 0, 0, 0, 0);

            float mp = ModifiersMap.RankedMap().GetTotalMultiplier(modifiers, modifiersRating == null);

            if (accuracy < 0) {
                accuracy = 0;
            }

            float rawPP = 0; float fullPP = 0; float passPP = 0; float accPP = 0; float techPP = 0; float increase = 0; 
            if (!modifiers.Contains("NF"))
            {
                (passPP, accPP, techPP) = GetPp(accuracy, accRating, passRating, techRating, predictedAcc);

                rawPP = Inflate(passPP + accPP + techPP);
                if (modifiersRating != null) {
                    var modifiersMap = modifiersRating.ToDictionary<float>();
                    foreach (var modifier in modifiers.ToUpper().Split(","))
                    {
                        if (modifiersMap.ContainsKey(modifier + "AccRating")) { 
                            accRating = modifiersMap[modifier + "AccRating"]; 
                            passRating = modifiersMap[modifier + "PassRating"]; 
                            techRating = modifiersMap[modifier + "TechRating"];
                            if (modifiersMap.TryGetValue(modifier + "PredictedAcc", out var modPredictedAcc) && modPredictedAcc > 0) {
                                predictedAcc = modPredictedAcc;
                            }

                            break;
                        }
                    }
                }
                (passPP, accPP, techPP) = GetPp(accuracy, accRating * mp, passRating * mp, techRating * mp, predictedAcc);
                fullPP = Inflate(passPP + accPP + techPP);
                if (passPP + accPP + techPP > 0) {
                    increase = fullPP / (passPP + accPP + techPP);
                }
            }

            if (float.IsInfinity(rawPP) || float.IsNaN(rawPP) || float.IsNegativeInfinity(rawPP)) {
                rawPP = 0;
            }

            if (float.IsInfinity(fullPP) || float.IsNaN(fullPP) || float.IsNegativeInfinity(fullPP)) {
                fullPP = 0;
            }

            return (fullPP, fullPP - rawPP, passPP * increase, accPP * increase, techPP * increase);
        }

        public static int MaxScoreForNote(int count) {
          int note_score = 115;

          if (count <= 1) // x1 (+1 note)
              return note_score * (0 + (count - 0) * 1);
          if (count <= 5) // x2 (+4 notes)
              return note_score * (1 + (count - 1) * 2);
          if (count <= 13) // x4 (+8 notes)
              return note_score * (9 + (count - 5) * 4);
          // x8
          return note_score * (41 + (count - 13) * 8);
        }

        private static readonly PropertyInfo[] aModifiers = typeof(ModifiersMap).GetProperties().Where(p => p.PropertyType == typeof(float) && p.Name.Length == 2).ToArray();

        public static float GetTotalMultiplier(this ModifiersMap modifiersObject, string modifiers, bool speedModifiers)
		{
			float multiplier = 1;

            foreach (PropertyInfo modifierProp in aModifiers)
            {
                if (!speedModifiers && modifierProp.Name is "SF" or "SS" or "FS")
                    continue;

                if (modifiers.Contains(modifierProp.Name, StringComparison.Ordinal))
                {
                    multiplier += (float)modifierProp.GetValue(modifiersObject)!;
                }
            }
            

			return multiplier;
		}

        public static float GetPositiveMultiplier(this ModifiersMap modifiersObject, string modifiers)
        {
            float multiplier = 1;

            var modifiersMap = modifiersObject.ToDictionary<float>();
            foreach (var modifier in modifiersMap.Keys)
            {
                if (modifiers.Contains(modifier) && modifiersMap[modifier] > 0) { multiplier += modifiersMap[modifier]; }
            }

            return multiplier;
        }

        public static float GetNegativeMultiplier(this ModifiersMap modifiersObject, string modifiers, bool ignoreNF = false)
        {
            float multiplier = 1;

            var modifiersMap = modifiersObject.ToDictionary<float>();
            foreach (var modifier in modifiersMap.Keys)
            {
                if (ignoreNF && modifier == "NF") continue;
                if (modifiers.Contains(modifier) && modifiersMap[modifier] < 0) { multiplier += modifiersMap[modifier]; }
            }

            return multiplier;
        }

        public static (string, float) GetNegativeMultipliers(this ModifiersMap modifiersObject, string modifiers)
        {
            float multiplier = 1;
            List<string> modifierArray = new List<string>();

            var modifiersMap = modifiersObject.ToDictionary<float>();
            foreach (var modifier in modifiersMap.Keys)
            {
                if (modifiers.Contains(modifier)) {
                    if (modifiersMap[modifier] < 0) {
                        multiplier += modifiersMap[modifier]; 
                        modifierArray.Add(modifier);
                    }
                }
            }

            return (string.Join(",", modifierArray), multiplier);
        }

        public static int ScoreForRank(int rank) {
            switch (rank) {
                case 1:
                    return 5;
                case 2:
                    return 3;
                case 3:
                    return 1;
                default:
                    return 0;
            }
        }

        public static int UpdateRankScore(int oldScore, int? oldRank, int newRank) {
            int result = oldScore;
            if (oldRank != null) {
                result -= ScoreForRank(oldRank ?? 4);
            }
            result += ScoreForRank(newRank);

            return result; 
        }

        public static string SafeSubstring(this string text, int start, int length)
        {
            return text.Length <= start ? ""
                : text.Length - start <= length ? text.Substring(start)
                : text.Substring(start, length);
        }
    }

    public class ModifiersMap
    {
        public int ModifierId { get; set; }

        public float DA { get; set; } = 0.0f;
        public float FS { get; set; } = 0.0f;
        public float SF { get; set; } = 0.0f;
        public float SS { get; set; } = 0.0f;
        public float GN { get; set; } = 0.0f;
        public float NA { get; set; } = -0.3f;
        public float NB { get; set; } = -0.2f;
        public float NF { get; set; } = -0.5f;
        public float NO { get; set; } = -0.2f;
        public float PM { get; set; } = 0.0f;
        public float SC { get; set; } = 0.0f;
        public float SA { get; set; } = 0.0f;
        public float OP { get; set; } = -0.5f;

        public static ModifiersMap RankedMap() {
            return new ModifiersMap {
                DA = 0.0f,
                FS = 0.0f,
                SF = 0.0f,
                SS = 0.0f,
                GN = 0.0f,
                NA = -0.3f,
                NB = -0.2f,
                NF = -1.0f,
                NO = -0.2f,
                PM = 0.0f,
                SC = 0.0f,
                SA = 0.0f,
                OP = -0.5f,
            };
        }

        public bool EqualTo(ModifiersMap? other) {
            return other != null && DA == other.DA && FS == other.FS && SS == other.SS && SF == other.SF && GN == other.GN && NA == other.NA && NB == other.NB && NF == other.NF && NO == other.NO && PM == other.PM && SC == other.SC && SA == other.SA && OP == other.OP;
        }
    }

    public static class ObjectToDictionaryHelper
    {
        public static IDictionary<string, object> ToDictionary(this object source)
        {
            return source.ToDictionary<object>();
        }

        public static IDictionary<string, T> ToDictionary<T>(this object source)
        {
            if (source == null)
                ThrowExceptionWhenSourceArgumentIsNull();

            var dictionary = new Dictionary<string, T>();
            foreach (PropertyDescriptor property in TypeDescriptor.GetProperties(source))
                AddPropertyToDictionary(property, source, dictionary);
            return dictionary;
        }

        private static void AddPropertyToDictionary<T>(PropertyDescriptor property, object source, Dictionary<string, T> dictionary)
        {
            object value = property.GetValue(source);
            if (IsOfType<T>(value))
                dictionary.Add(property.Name, (T)value);
        }

        private static bool IsOfType<T>(object value)
        {
            return value is T;
        }

        private static void ThrowExceptionWhenSourceArgumentIsNull()
        {
            throw new ArgumentNullException("source", "Unable to convert object to a dictionary. The source object is null.");
        }
    }
}
