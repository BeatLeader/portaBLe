namespace portaBLe.Refresh
{
    // The parts of the pp formula (ReplayUtils.GetPp, Inflate, ToStars). PpFormula.Current is what ReplayUtils uses; the
    // pp formula sweeps (BalanceTuner.RunPpFormula) swap it temporarily to try other values. The defaults are the formula
    // as it was hard-coded:
    //   pass pp = PassScale * e^(pass^(1/PassExponent)) - PassOffset   (0 when negative)
    //   acc pp  = curve(acc) * accRating * AccWeight
    //   tech pp = e^(TechAccExponent * acc) * TechWeight * tech
    //   pp      = InflateBase * (pass + acc + tech)^InflateExponent / InflateBase^InflateExponent
    //   pp      = pp * PpMultiplier
    //   pp      = pp * (1 + LowStarBonus * (LowStarBonusEnd - stars)^LowStarBonusPower)   (only under LowStarBonusEnd stars)
    //   stars   = pp at 96% / StarDivisor (before PpMultiplier, so the multiplier doesn't move stars)
    public class PpFormula
    {
        public double PassScale { get; set; } = 12.3;
        public double PassExponent { get; set; } = 2.62;
        public double PassOffset { get; set; } = 30;
        public double AccWeight { get; set; } = 34;
        public double TechWeight { get; set; } = 0.60;
        public double TechAccExponent { get; set; } = 1.9;
        public double InflateBase { get; set; } = 650;
        public double InflateExponent { get; set; } = 1.3;
        public double StarDivisor { get; set; } = 49.13;
        public double PpMultiplier { get; set; } = 1.050;

        // Low star bonus: maps under LowStarBonusEnd stars pay more, without changing their stars (+45% at 1 star, +17% at 2,
        // +6% at 3, nothing from 4). Top players farm the easiest maps to 99%+, but stars are measured at 96%, so the
        // unweighted pp vs stars graph fell off steeply under 4 stars. A soft floor on the acc rating (tried, then removed)
        // lifted their stars too, which squeezed them into 2.5-4 stars where they paid more than real 3-4 star maps (a bump
        // on the graph). Raising pp at a given star value keeps maps where they are: simulated on the graph (players ranked
        // top 1000, 100+ ranked plays), the rolling average rises 243, 274, 278, 293, 301, 313, 329 from 1 to 4 stars (was
        // 194, 229, 244, 270, 288, 310, 329), unchanged from 4 stars; #1 and #100 player totals unchanged. Set LowStarBonus
        // to 0 to turn it off.
        public double LowStarBonus { get; set; } = 0.06;
        public double LowStarBonusEnd { get; set; } = 4;
        public double LowStarBonusPower { get; set; } = 1.5;

        public static PpFormula Current { get; set; } = new();

        public PpFormula Clone() => (PpFormula)MemberwiseClone();

        // passRating is the displayed rating; the formula uses the internal pass (Analyzer Ratings.PassDisplayScale)
        public float PassPp(float passRating)
        {
            float internalPass = (float)global::beatleader_analyzer.BeatmapScanner.Data.Ratings.InternalPass(passRating);
            float passPP = (float)PassScale * MathF.Exp(MathF.Pow(internalPass, 1 / (float)PassExponent)) - (float)PassOffset;
            if (float.IsInfinity(passPP) || float.IsNaN(passPP) || passPP < 0) passPP = 0;
            return passPP;
        }

        public float AccPp(float curveValue, float accRating) => curveValue * accRating * (float)AccWeight;

        public float TechPp(float accuracy, float techRating) => MathF.Exp((float)TechAccExponent * accuracy) * (float)TechWeight * techRating;

        public float LowStarMultiplier(float stars)
        {
            if (LowStarBonus <= 0 || float.IsNaN(stars) || stars >= LowStarBonusEnd) return 1;
            return 1 + (float)(LowStarBonus * Math.Pow(LowStarBonusEnd - Math.Max(stars, 0), LowStarBonusPower));
        }

        public float Inflate(float pp) => (float)PpMultiplier * (float)InflateBase * MathF.Pow(pp, (float)InflateExponent) / MathF.Pow((float)InflateBase, (float)InflateExponent);

        public override string ToString() => FormattableString.Invariant(
            $"pass {PassScale}*e^(p^(1/{PassExponent}))-{PassOffset}, acc x{AccWeight}, tech e^({TechAccExponent}a)*{TechWeight}, inflate ^{InflateExponent}, x{PpMultiplier}, stars /{StarDivisor}, low star bonus {LowStarBonus}*({LowStarBonusEnd}-stars)^{LowStarBonusPower}");
    }
}
