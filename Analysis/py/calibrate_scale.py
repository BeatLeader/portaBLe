"""Calibrate the algorithmic acc model's level (reference_skill) and spread (difficulty_scale) against the ML ratings.

The algorithm reproduces the full spread of score-implied difficulty, which is ~1.2x wider than the ML's. Anchoring only the level
(reference_skill) at the top-1000 players therefore deflates everyone else. This fits two parameters so that the median player PP is
unchanged in two rank bands (default: top 1000 and ranks 10 001-50 000), i.e. today's PP-versus-skill profile is kept and only the
distribution between maps of similar difficulty changes:  d' = center + scale * (d - center);  predictedAcc = 1 - exp(d' - reference_skill).

Usage: python calibrate_scale.py --data <dir> [--algo-tag algo] [--write]
       (ratings_<algo-tag>.csv must come from a RatingsDump run with the current acc_model.json; its algo_difficulty column is used)
"""
import argparse, json, os, sys
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import data, ppmodel as pm

ap = argparse.ArgumentParser()
ap.add_argument("--data", required=True)
ap.add_argument("--ml-tag", default="prod")
ap.add_argument("--algo-tag", default="algo")
ap.add_argument("--model", default=os.path.join(os.path.dirname(__file__), "..", "..", "RatingAPI", "acc_model.json"))
ap.add_argument("--bands", default="1-1000,10001-50000")
ap.add_argument("--write", action="store_true")
ap.add_argument("--power-check", action="store_true", help="also solve the power-law curve's acc scale (PpCurve.AccScale) for the calibrated model")
ap.add_argument("--fixed", default=None, help="skip the search: 'scale,reference_skill'")
ap.add_argument("--check-totals", action="store_true", help="verify the vectorized player totals against ppmodel.player_totals")
ap.add_argument("--mode", choices=("spread", "gamma"), default="spread",
                help="spread: solve difficulty_scale + reference_skill (classic curves); "
                     "gamma: keep the model as is and solve the power-law curve's gamma + acc scale (PpCurve.Gamma / AccScale)")
args = ap.parse_args()
data.DATA = args.data
spec = json.load(open(args.model))

sc = data.load_scores(clean_only=True); sc["lb_id"] = sc.lb_id.astype(str); sc["player"] = sc.player.astype(str)
R = pd.read_csv(data.p("ratings", f"ratings_{args.ml_tag}.csv"), dtype={"lb_id": str}); Rn = R[R["mod"] == "none"].set_index("lb_id")
A = pd.read_csv(data.p("ratings", f"ratings_{args.algo_tag}.csv"), dtype={"lb_id": str}); An = A[A["mod"] == "none"].set_index("lb_id")
sc = sc[sc.lb_id.isin(Rn.index) & sc.lb_id.isin(An.index[An.algo_difficulty.notna()])]
acc = sc.acc.values
p_, t_, n_ = (Rn.loc[sc.lb_id, c].values for c in ("pass", "tech", "low_note_nerf"))
d_raw = An.loc[sc.lb_id, "algo_difficulty"].values
center = float(An.algo_difficulty[An.algo_difficulty.notna()].mean())

pcode, players = pd.factorize(sc.player.values)

def totals(acc_rating):
    """Player PP as pm.player_totals (sum of pp * 0.965^rank), vectorized on integer player codes (called hundreds of times)."""
    full, *_ = pm.pp(acc, acc_rating, p_, t_)
    o = np.lexsort((-full, pcode))
    pc, f = pcode[o], full[o]
    start = np.r_[0, np.flatnonzero(np.diff(pc)) + 1]
    rank_in = np.arange(len(pc)) - np.repeat(start, np.diff(np.r_[start, len(pc)]))
    return pd.Series(np.bincount(pc, weights=f * pm.PLAYER_WEIGHT ** rank_in, minlength=len(players)), index=players)

base = totals(Rn.loc[sc.lb_id, "acc_rating"].values)
if args.check_totals:
    full, *_ = pm.pp(acc, Rn.loc[sc.lb_id, "acc_rating"].values, p_, t_)
    ref, _ = pm.player_totals(pd.DataFrame({"player": sc.player.values, "pp": full}), "pp", "player")
    print("max |fast - reference| player PP:", float((base - ref.pp.reindex(base.index)).abs().max()))
rank = base.rank(ascending=False, method="first")
bands = [tuple(int(x) for x in b.split("-")) for b in args.bands.split(",")]
band_ids = [rank.index[(rank >= lo) & (rank <= hi)] for lo, hi in bands]

def ratios(scale, a_ref):
    d = center + scale * (d_raw - center)
    pred = np.clip(1 - np.exp(d - a_ref), spec.get("min_predicted_acc", 0.5), spec.get("max_predicted_acc", 0.9995))
    t = totals(pm.acc_rating_from_predicted(pred) * n_)
    return [float((t.loc[ids] / base.loc[ids]).median()) for ids in band_ids], t

def solve_a(scale):  # level that keeps band 0 median unchanged
    lo, hi = 2.5, 6.0
    for _ in range(22):
        mid = (lo + hi) / 2
        r, _ = ratios(scale, mid)
        if r[0] > 1: lo = mid
        else: hi = mid
    return (lo + hi) / 2

def report(t):
    for (lo_, hi_) in ((1, 100), (1, 1000), (1001, 10000), (10001, 50000), (50001, 10 ** 7)):
        ids = rank.index[(rank >= lo_) & (rank <= hi_)]
        rr = t.loc[ids] / base.loc[ids]
        print(f"  rank {lo_}-{hi_}: median {rr.median()-1:+.1%}  p10 {rr.quantile(.1)-1:+.1%}  p90 {rr.quantile(.9)-1:+.1%}")

if args.mode == "gamma":
    # With full-spread ratings and a power-law curve, acc PP at equal skill no longer depends on the map, so the PP-versus-skill
    # profile is set by gamma alone (the same exponent that rewards accuracy within a map); acc scale sets the level.
    pm.CURVE_MODE = "power"
    scale0, a_ref0 = spec.get("difficulty_scale", 1.0), spec["reference_skill"]
    def ratios_g(gamma, acc_scale):
        pm.POWER["gamma"], pm.POWER["acc_scale"] = gamma, acc_scale
        return ratios(scale0, a_ref0)
    def solve_s(gamma):
        lo, hi = 0.5, 2.5
        for _ in range(14):
            mid = (lo + hi) / 2
            r, _ = ratios_g(gamma, mid)
            if r[0] > 1: hi = mid
            else: lo = mid
        return (lo + hi) / 2
    lo, hi = 0.3, 0.75
    for _ in range(14):
        g = (lo + hi) / 2; s = solve_s(g)
        r, _ = ratios_g(g, s)
        print(f"  gamma {g:.4f} acc_scale {s:.4f} -> band medians {[round(x, 4) for x in r]}", flush=True)
        # larger gamma steepens the profile: the top band gains relative to lower bands
        if r[1] > 1: lo = g
        else: hi = g
    g = (lo + hi) / 2; s = solve_s(g)
    r, t = ratios_g(g, s)
    print(f"\ncalibrated power-law: --gamma {g:.4f} --acc-scale {s:.4f} (model unchanged: scale {scale0}, reference_skill {a_ref0:.4f}); band medians {r}")
    report(t)
    sys.exit(0)

if args.fixed:
    scale, a_ref = (float(x) for x in args.fixed.split(","))
else:
    # outer search on scale so that band 1 median is also unchanged
    lo, hi = 0.5, 1.2
    for _ in range(14):
        mid = (lo + hi) / 2
        a = solve_a(mid)
        r, _ = ratios(mid, a)
        print(f"  scale {mid:.4f} a_ref {a:.4f} -> band medians {[round(x, 4) for x in r]}", flush=True)
        # larger scale widens the spread: easy maps lose, so the lower band loses relative to the top band
        if r[1] > 1: lo = mid
        else: hi = mid
    scale = (lo + hi) / 2; a_ref = solve_a(scale)
r, t = ratios(scale, a_ref)
print(f"\ncalibrated: difficulty_scale={scale:.4f}, reference_skill={a_ref:.4f}, center={center:.4f}; band medians {r}")
report(t)
if args.power_check:
    # Same model, power-law curve: the curve's own redistribution stays; only its level (acc scale) keeps the top-1000 median
    pm.CURVE_MODE = "power"
    lo, hi = 0.7, 1.5
    for _ in range(25):
        pm.POWER["acc_scale"] = mid = (lo + hi) / 2
        r, t = ratios(scale, a_ref)
        if r[0] > 1: hi = mid
        else: lo = mid
    print(f"\npower-law curve: acc_scale={pm.POWER['acc_scale']:.4f} (PpCurve.AccScale); band medians {r}")
    report(t)
    pm.CURVE_MODE = "classic"
if args.write:
    spec["difficulty_center"] = center
    spec["difficulty_scale"] = scale
    spec["reference_skill"] = a_ref
    spec["description"] += f" Calibrated with Analysis/py/calibrate_scale.py: spread x{scale:.3f} around {center:.3f}, reference_skill {a_ref:.3f} keep the median PP of ranks {args.bands} unchanged."
    json.dump(spec, open(args.model, "w"), indent=1)
    print("wrote", args.model)
