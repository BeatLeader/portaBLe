"""R6: which predicted swing quantities explain observed precision loss (swing-level, within map)?

For every analyzer swing, observed mean centre / pre-swing / post-swing loss and miss+bad rate over the replays of one stratum
(top-8, mid-field). Spearman correlation with each predicted quantity, computed WITHIN map (map mean removed) and, to remove the effect of
swing speed, also partial on pred_swing_speed. Answers whether `angle strain`, `repositioning`, `rotation` (the tech parts), `hit distance`,
`speed`, `frequency` carry information about real accuracy loss.
Usage: python r06_tech_components.py <replay-study dir> [max maps] [tag]
"""
import os, sys, json
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import replays

d = sys.argv[1]
MAXMAPS = int(sys.argv[2]) if len(sys.argv) > 2 else 0
tag = sys.argv[3] if len(sys.argv) > 3 else "r06"
OUT = os.path.join(os.path.dirname(__file__), "..", "out")
rp, sw, n = replays.load(d)
if MAXMAPS:
    keep = pd.Series(sorted(rp.lb_id.unique())).sample(min(MAXMAPS, rp.lb_id.nunique()), random_state=2)
    rp, sw, n = rp[rp.lb_id.isin(keep)], sw[sw.lb_id.isin(keep)], n[n.lb_id.isin(keep)]
nn = n[(n.scoring_type == replays.SCORING_NORMAL) & n.event_type.isin([0, 1, 2]) & (n.swing_i >= 0)].merge(rp[["score_id", "stratum_kind"]], on="score_id")
good = nn.event_type == 0
nn["c"] = np.where(good, 15 - nn.acc, np.nan); nn["pre_l"] = np.where(good, 70 - nn.pre, np.nan); nn["post_l"] = np.where(good, 30 - nn.post, np.nan)
nn["bad"] = (~good).astype(float)
agg = nn.groupby(["lb_id", "swing_i", "stratum_kind"]).agg(c=("c", "mean"), pre_l=("pre_l", "mean"), post_l=("post_l", "mean"), bad=("bad", "mean"), k=("c", "size")).reset_index()
agg = agg[agg.k >= 4]
sw = sw.sort_values(["lb_id", "hand", "seconds"]).copy()
g = sw.groupby(["lb_id", "hand"])
sw["gap_prev_same"] = (sw.seconds - g.seconds.shift(1)).clip(upper=3)
sw["inv_gap"] = 1 / sw.gap_prev_same.clip(lower=0.05)
sw["is_dot"] = sw.is_dot.astype(float)
feats = ["pred_swing_speed", "pred_swing_diff", "pred_frequency", "inv_gap", "pred_hit_distance", "pred_angle_strain", "pred_reposition", "pred_rotation", "pred_swing_tech",
         "pred_stress", "pred_njs_buff", "pred_parity_error", "is_dot", "note_count", "njs"]
A = agg.merge(sw[["lb_id", "swing_i"] + feats], on=["lb_id", "swing_i"])
print("swing-strata rows", len(A), "maps", A.lb_id.nunique())

def within_spearman(df, x, y):
    r = df[[x, y, "lb_id"]].dropna()
    rx = r.groupby("lb_id")[x].rank(pct=True); ry = r.groupby("lb_id")[y].rank(pct=True)
    rx = rx - rx.groupby(r.lb_id).transform("mean"); ry = ry - ry.groupby(r.lb_id).transform("mean")
    return float(np.corrcoef(rx, ry)[0, 1]) if len(r) > 100 else np.nan

res = {}
for strat in ("top", "mid"):
    S = A[A.stratum_kind == strat]
    rows = []
    for f in feats:
        rows.append({"feature": f, **{o: within_spearman(S, f, o) for o in ("c", "pre_l", "post_l", "bad")}})
    T = pd.DataFrame(rows).set_index("feature")
    print(f"\n=== {strat}: within-map Spearman between predicted swing quantity and observed loss (rows: {len(S)})")
    print(T.round(3).to_string())
    res[strat] = T.round(4).to_dict()
json.dump(res, open(os.path.join(OUT, f"{tag}.json"), "w"), indent=1)
