namespace portaBLe.Refresh
{
    // The parts of the pp formula (ReplayUtils.GetPp, Inflate, ToStars). PpFormula.Current is what ReplayUtils uses; the
    // pp formula sweeps (BalanceTuner.RunPpFormula) swap it temporarily to try other values. The defaults are the formula
    // as it was hard-coded:
    //   pass pp = PassScale * e^(PassInner * pass^(1/PassExponent)) - PassOffset   (0 when negative; above PassTopKnee
    //             each point counts PassTopSlope)
    //   acc pp  = curve(acc) * AccWeight * accRating^AccRatingPower
    //   tech pp = e^(TechAccExponent * acc) * TechWeight * tech^TechRatingPower
    //   pp      = InflateBase * (pass + acc + tech)^InflateExponent / InflateBase^InflateExponent
    //   pp      = pp * PpMultiplier
    //   pp      = pp * (1 + LowStarBonus * (LowStarBonusEnd - stars)^LowStarBonusPower)   (only under LowStarBonusEnd stars)
    //   stars   = StarScale * (pp at 96% / StarDivisor)^StarExponent (before PpMultiplier, so the multiplier doesn't move
    //             stars)
    public class PpFormula
    {
        public double PassScale { get; set; } = 12.3;
        public double PassExponent { get; set; } = 2.62;
        public double PassOffset { get; set; } = 30;
        // 1.4862^(-1 / 2.62): the pass formula was written for a pass 1.4862 times smaller (before the pass display curve was
        // folded into AnalyzeMap.PASS_CALIBRATION_FACTOR), so the same map gets the same pass pp
        public double PassInner { get; set; } = 0.85966;
        // AccRatingPower / TechRatingPower: the acc and tech ratings are on prod's scale, which is flatter at the top than what acc
        // and tech pp need (AccRating.ACC_EXPONENT 0.8222 and Analyzer Ratings.TechExponent 0.8015 make them so; these are
        // their inverses). With pp proportional to the ratings instead, a star range's acc-heavy maps lost acc pp against its
        // pass-heavy ones (pass share slope +0.03 -> +0.11, tech share slope +0.01 -> -0.15) and no PassScale / TechWeight
        // undid it. AccWeight = 34 / 1.4528^AccRatingPower, TechWeight below the same: the same pp as before the change.
        public double AccWeight { get; set; } = 21.584;
        public double AccRatingPower { get; set; } = 1.2163;
        // 0.2193 -> 0.1874 (BalanceTuner) after DENSITY_CAP 1: dense low-tech maps lost pass, so tech-heavy maps were worth
        // more than others of their stars. -> 0.1695 after the acc rating's movement correction (AccRating.EffectiveAcc),
        // which raised the acc of high-movement maps (tech share slope 0.047 -> 0.007). -> 0.1485 after the 4-32 s pass
        // windows. -> 0.1348 with the prod rescale: tech ratings x1.102 (AnalyzeMap.BALANCED_TECH_SCALER), same tech pp.
        // -> 0.0938 with TechRatingPower (0.1348 / 1.3374^TechRatingPower, see AccRatingPower): the same tech pp.
        public double TechWeight { get; set; } = 0.0938;
        public double TechRatingPower { get; set; } = 1.2477;
        public double TechAccExponent { get; set; } = 3.5;
        public double InflateBase { get; set; } = 650;
        public double InflateExponent { get; set; } = 1.3;
        // Stars and players' pp on prod's scale. StarScale / StarExponent: pp at 96% is steeper at the top than prod's stars;
        // fitted on the quantiles of prod's ranked stars (median 6.89 vs 6.97, p95 11.75 vs 11.87, p99 13.08 vs 13.17).
        // PpMultiplier: top 1000 players (#1 24580 vs prod 24140, #1000 14573 vs 14693).
        public double StarDivisor { get; set; } = 45.25;
        public double StarScale { get; set; } = 1.2131;
        public double StarExponent { get; set; } = 0.9039;
        public double PpMultiplier { get; set; } = 1.146;

        // Low star bonus: maps under LowStarBonusEnd stars pay more, without changing their stars (+45% at 1 star, +17% at 2,
        // +6% at 3, nothing from 4). Top players farm the easiest maps to 99%+, but stars are measured at 96%, so the
        // unweighted pp vs stars graph fell off steeply under 4 stars. A soft floor on the acc rating (tried, then removed)
        // lifted their stars too, which squeezed them into 2.5-4 stars where they paid more than real 3-4 star maps (a bump
        // on the graph). Raising pp at a given star value keeps maps where they are: simulated on the graph (players ranked
        // top 1000, 100+ ranked plays), the rolling average rises 243, 274, 278, 293, 301, 313, 329 from 1 to 4 stars (was
        // 194, 229, 244, 270, 288, 310, 329), unchanged from 4 stars; #1 and #100 player totals unchanged. Set LowStarBonus
        // to 0 to turn it off.
        // Refitted on the stars of each change since (StarDivisor, then StarScale / StarExponent), so the same maps get the
        // same bonus (within 0.4%): the percentages above are in the stars of the time.
        public double LowStarBonus { get; set; } = 0.0642;
        public double LowStarBonusEnd { get; set; } = 4.49;
        public double LowStarBonusPower { get; set; } = 1.36;

        public static PpFormula Current { get; set; } = new();

        // Pass pp top compression: above PassTopKnee pass, each point counts PassTopSlope. Maps with more pass than others of
        // their stars were worth more above 10 stars only (Megametric75 +0.18 to +0.31 above their star range from about 11
        // pass then, 13.4 now; prod alike). 1 = off.
        public double PassTopKnee { get; set; } = 13.375;
        public double PassTopSlope { get; set; } = 0.5;

        public PpFormula Clone() => (PpFormula)MemberwiseClone();

        public float PassPp(float passRating)
        {
            float pass = passRating;
            if (pass > PassTopKnee) pass = (float)(PassTopKnee + (pass - PassTopKnee) * PassTopSlope);
            float passPP = (float)PassScale * MathF.Exp((float)PassInner * MathF.Pow(pass, 1 / (float)PassExponent)) - (float)PassOffset;
            if (float.IsInfinity(passPP) || float.IsNaN(passPP) || passPP < 0) passPP = 0;
            return passPP;
        }

        public float AccPp(float curveValue, float accRating) =>
            accRating > 0 ? curveValue * (float)AccWeight * MathF.Pow(accRating, (float)AccRatingPower) : 0;

        public float TechPp(float accuracy, float techRating) =>
            techRating > 0 ? MathF.Exp((float)TechAccExponent * accuracy) * (float)TechWeight * MathF.Pow(techRating, (float)TechRatingPower) : 0;

        // Stars from pp at 96% (already inflated, before PpMultiplier)
        public float Stars(float ppAt96)
        {
            double basis = ppAt96 / StarDivisor;
            return basis > 0 ? (float)(StarScale * Math.Pow(basis, StarExponent)) : (float)basis;
        }

        public float LowStarMultiplier(float stars)
        {
            if (LowStarBonus <= 0 || float.IsNaN(stars) || stars >= LowStarBonusEnd) return 1;
            return 1 + (float)(LowStarBonus * Math.Pow(LowStarBonusEnd - Math.Max(stars, 0), LowStarBonusPower));
        }

        public float Inflate(float pp) => (float)PpMultiplier * (float)InflateBase * MathF.Pow(pp, (float)InflateExponent) / MathF.Pow((float)InflateBase, (float)InflateExponent);

        public override string ToString() => FormattableString.Invariant(
            $"pass {PassScale}*e^({PassInner}*p^(1/{PassExponent}))-{PassOffset} (x{PassTopSlope} above {PassTopKnee}), acc x{AccWeight}*a^{AccRatingPower}, tech e^({TechAccExponent}a)*{TechWeight}*t^{TechRatingPower}, inflate ^{InflateExponent}, x{PpMultiplier}, stars {StarScale}*(pp/{StarDivisor})^{StarExponent}, low star bonus {LowStarBonus}*({LowStarBonusEnd}-stars)^{LowStarBonusPower}");
    }
}
