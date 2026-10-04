"""Map fairness of a rating configuration: SD across maps of log(acc PP) for a player of fixed skill, and its slope against
score-implied difficulty (negative = hard maps underpaid). Uses out-of-fold algorithm predictions (fit_acc_model_maps.csv).

Usage: python fairness_options.py [--scale 1.0] [--center <d mean>] [--reference-skill <from acc_model.json>] [--gamma 0.601] [--min-n 100]
Prints rows for ML and the algorithm under Curve2 and under the power-law curve with the given gamma.
"""
import argparse, json, os, sys
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import ppmodel as pm

here = os.path.dirname(__file__)
ap = argparse.ArgumentParser()
ap.add_argument("--maps", default=os.path.join(here, "..", "out", "fit_acc_model_maps.csv"))
ap.add_argument("--model", default=os.path.join(here, "..", "..", "RatingAPI", "acc_model.json"))
ap.add_argument("--scale", type=float, default=1.0)
ap.add_argument("--center", type=float, default=None)
ap.add_argument("--reference-skill", type=float, default=None)
ap.add_argument("--gamma", type=float, default=0.601)
ap.add_argument("--min-n", type=int, default=100)
args = ap.parse_args()

spec = json.load(open(args.model))
a_ref = args.reference_skill if args.reference_skill is not None else spec["reference_skill"]
F = pd.read_csv(args.maps, dtype={"lb_id": str})
F = F[F.n >= args.min_n]
c = args.center if args.center is not None else F.d.mean()
LEVELS = (0.90, 0.95, 0.98)

def power(gamma):
    return lambda x: np.power(np.maximum(1 - x + 0.0016, 1e-6) / 0.0516, -gamma)

def fairness(pred, curve):
    out = []
    for x in LEVELS:  # player who scores x on an average map
        ac = np.clip(1 - np.exp(F.d.values - (F.d.mean() - np.log(1 - x))), 0.3, 0.9995)
        lp = np.log(curve(ac) / curve(pred))
        out.append((np.std(lp), np.polyfit(F.d.values, lp, 1)[0]))
    return out

algo = np.clip(1 - np.exp(c + args.scale * (F.d_hat_cv.values - c) - a_ref), 0.5, 0.9995)
print(f"maps: {len(F)}; algorithm scale {args.scale} around {c:.4f}, reference_skill {a_ref:.4f}")
print("source / curve".ljust(34) + "".join(f"  SD@{x:.2f} (slope)".ljust(20) for x in LEVELS))
for cname, curve in (("Curve2", pm.curve2), (f"power-law g={args.gamma}", power(args.gamma))):
    for sname, pred in (("ML", F.predicted_acc.values), ("algorithm (CV)", algo)):
        print(f"{sname} / {cname}".ljust(34) + "".join(f"  {sd:.3f} ({b:+.3f})".ljust(20) for sd, b in fairness(pred, curve)))
