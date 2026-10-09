"""A28: do pass v2's cut-direction terms over-rate fast diagonal streams?

Pass v2 multiplies every diagonal swing's PassDiff by 1.29 and every horizontal one by 1.37, at any speed. Community reports
(Godspeed Expert rated 1.4 above its E+, 414 PER SPEED / Dual Doom above Extratongue) point at fast streams: there the diagonal
term lands on the hardest swings of the map. Variants: the terms apply only below an eBPM threshold (30 x swing frequency), or
fade out linearly between two eBPMs. Scored like a19/a22: R2 of a cubic fit of the attempts' pass difficulty b on ln(rating),
maps off by > 1 / > 2 logits, and the named maps' residuals.

--set flow instead tests whether the terms should skip swings that reverse straight back (a flowing diagonal stream).
Result (out/a28_fast_diagonals.json, out/a28_flow.json): every variant fixes some of the named maps but fits the attempts worse
overall (R2 0.907-0.915 vs 0.9166), so v2 keeps the terms on every swing.

Usage: python a28_fast_diagonals.py --swings <swings_prod.csv.gz> --pass-maps <a19_pass_maps.parquet> [--set speed|flow] [--out <json>]
"""
import argparse, json, os, sys
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import energylib as el

ap = argparse.ArgumentParser()
ap.add_argument("--swings", required=True); ap.add_argument("--pass-maps", dest="pass_maps", required=True); ap.add_argument("--out", default=None)
ap.add_argument("--set", default="speed", choices=["speed", "flow"], help="speed: eBPM cut-offs; flow: skip straight-back reversals")
args = ap.parse_args()
S = pd.read_csv(args.swings, dtype={"lb_id": str}, usecols=["lb_id", "swing_i", "seconds", "hand", "x", "cut_direction", "n_cubes", "parity_error",
                                                             "is_stream", "swing_speed", "stress", "low_speed_falloff", "njs_buff", "wall_buff", "frequency", "direction"])
B = pd.read_parquet(args.pass_maps)[["lb_id", "b"]]
S = S[S.lb_id.isin(B.lb_id)].sort_values(["lb_id", "seconds", "swing_i"], kind="stable").reset_index(drop=True)
codes, lbs = pd.factorize(S.lb_id); notes = S.n_cubes.clip(1, 4).values
b = B.set_index("lb_id").b.reindex(lbs).values
one = (S.groupby("lb_id", sort=False).hand.nunique().reindex(lbs) == 1).values
ebpm = 30 * S.frequency.values
diag = S.cut_direction.isin([4, 5, 6, 7]).values; horiz = S.cut_direction.isin([2, 3]).values
v2 = el.V2
base = el.pass_diff(S, {**v2, "horizontal": 0, "diagonal": 0})
NAMED = {"4c5e3xx71": "Godspeed Ex", "4c5e3xx91": "Godspeed E+", "3f829xxxxxx91": "Extratongue E+", "3d6abxxxxx91": "414 PER SPEED E+",
         "3e2c2xxxxx91": "Dual Doom E+", "47c48xxxx91": "SLIDE THE BPM E+", "179eb91": "Kannabis E+", "4cbd0xxxx91": "Sound Chimera 4cbd0",
         "2c00e91": "Sound Chimera 2c00e", "338afxx91": "Feral E+", "46f03xx91": "Calamitous Demise E+"}

def run(weight):
    """weight: per-swing share (0..1) of the cut-direction terms"""
    d = base * (1 + v2["horizontal"] * horiz * weight) * (1 + v2["diagonal"] * diag * weight)
    LD, ACT = el.note_matrix(np.log(np.maximum(d, 1e-3)), notes, codes)
    t = el.skill50(LD, ACT, v2["slope"]) + np.where(one, np.log(v2["one_saber"]), 0)
    e = b - np.polyval(np.polyfit(t, b, 3), t)
    rating = pd.Series(np.exp(t) * v2["scale"], index=lbs)
    return {"r2": float(1 - e.var() / b.var()), "off1": int((np.abs(e) > 1).sum()), "off2": int((np.abs(e) > 2).sum()),
            "named": {NAMED[k]: [round(float(rating[k]), 2), round(float(e[lbs.get_loc(k)]), 2)] for k in NAMED if k in lbs}}

# angle change from the same hand's previous swing (180 = straight back, the natural flow of a stream)
d = np.abs(np.mod(S.direction - S.groupby(["lb_id", "hand"]).direction.shift(1), 360)); turn = np.minimum(d, 360 - d).fillna(180).values
straight = turn >= 157.5
if args.set == "flow":
    variants = {"v2 (terms on every swing)": np.ones(len(S)),
                "terms skip straight-back reversals": (~straight).astype(float),
                "terms skip straight-back reversals at >= 200 eBPM": (~(straight & (ebpm >= 200))).astype(float),
                "terms halved on straight-back reversals": np.where(straight, 0.5, 1.0)}
else:
  variants = {"v2 (terms at every speed)": np.ones(len(S)),
            "terms only below 300 eBPM": (ebpm < 300).astype(float),
            "terms only below 250 eBPM": (ebpm < 250).astype(float),
            "terms only below 200 eBPM": (ebpm < 200).astype(float),
            "terms fade out 200 -> 400 eBPM": np.clip((400 - ebpm) / 200, 0, 1),
            "no cut-direction terms": np.zeros(len(S))}
print(f"swings that reverse straight back (turn >= 157.5 deg): {straight.mean():.0%}; of diagonal swings {straight[diag].mean():.0%}, of horizontal {straight[horiz].mean():.0%}")
out = {}
for name, w in variants.items():
    r = run(w); out[name] = r
    print(f"{name:34s} R2 {r['r2']:.4f}   off by > 1 / > 2 logits: {r['off1']:4d} / {r['off2']:3d}", flush=True)
print("\nnamed maps, rating (residual b - fit; + = harder to pass than rated):")
names = list(NAMED.values())
print(pd.DataFrame({k: {n: f"{v['named'][n][0]:.2f} ({v['named'][n][1]:+.2f})" for n in names if n in v["named"]} for k, v in out.items()}).to_string())
if args.out: json.dump(out, open(args.out, "w"), indent=1)
