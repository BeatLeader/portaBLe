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
    //   stars   = pp at 96% / StarDivisor (before PpMultiplier, so the multiplier doesn't move stars)
    public class PpFormula
    {
        public double PassScale { get; set; } = 15.2;
        public double PassExponent { get; set; } = 2.62;
        public double PassOffset { get; set; } = 30;
        public double AccWeight { get; set; } = 34;
        // 1.08 * 9 / 14: the tech pp of BALANCED_TECH_SCALER 9 with tech ratings at their usual BALANCED_TECH_SCALER 14 scale
        public double TechWeight { get; set; } = 0.69429;
        public double TechAccExponent { get; set; } = 1.9;
        public double InflateBase { get; set; } = 650;
        public double InflateExponent { get; set; } = 1.3;
        public double StarDivisor { get; set; } = 52;
        public double PpMultiplier { get; set; } = 1.075;

        public static PpFormula Current { get; set; } = new();

        public PpFormula Clone() => (PpFormula)MemberwiseClone();

        public float PassPp(float passRating)
        {
            float passPP = (float)PassScale * MathF.Exp(MathF.Pow(passRating, 1 / (float)PassExponent)) - (float)PassOffset;
            if (float.IsInfinity(passPP) || float.IsNaN(passPP) || passPP < 0) passPP = 0;
            return passPP;
        }

        public float AccPp(float curveValue, float accRating) => curveValue * accRating * (float)AccWeight;

        public float TechPp(float accuracy, float techRating) => MathF.Exp((float)TechAccExponent * accuracy) * (float)TechWeight * techRating;

        public float Inflate(float pp) => (float)PpMultiplier * (float)InflateBase * MathF.Pow(pp, (float)InflateExponent) / MathF.Pow((float)InflateBase, (float)InflateExponent);

        public override string ToString() => FormattableString.Invariant(
            $"pass {PassScale}*e^(p^(1/{PassExponent}))-{PassOffset}, acc x{AccWeight}, tech e^({TechAccExponent}a)*{TechWeight}, inflate ^{InflateExponent}, x{PpMultiplier}, stars /{StarDivisor}");
    }
}
