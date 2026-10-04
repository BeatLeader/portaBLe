"""R3: which analyzer quantities predict where players actually miss or bad-cut?

Unit: a note event (good / bad / miss) of one replay, normal notes only. Target: bad = bad cut or miss.
Reports AUC pooled and within-map for single features (swing_diff, swing_speed, tech parts, ML accuracy error, ...),
windowed versions (the pass rating is a sliding-window average), and a song-grouped GBM, per player stratum.
Usage: python r03_pass_validity.py <replay-study dir> <ai_notes csv.gz> [tag]
"""
import os, sys, json
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import replays
from sklearn.metrics import roc_auc_score
from sklearn.ensemble import HistGradientBoostingClassifier
from sklearn.model_selection import GroupKFold

d, ai_path = sys.argv[1:3]
tag = sys.argv[3] if len(sys.argv) > 3 else "r03"
OUT = os.path.join(os.path.dirname(__file__), "..", "out")
MAXMAPS = int(sys.argv[4]) if len(sys.argv) > 4 else 0
rp, sw, n = replays.load(d)
if MAXMAPS:
    keep = pd.Series(sorted(rp.lb_id.unique())).sample(min(MAXMAPS, rp.lb_id.nunique()), random_state=1)
    rp, sw, n = rp[rp.lb_id.isin(keep)], sw[sw.lb_id.isin(keep)], n[n.lb_id.isin(keep)]
    print("subsampled to", len(keep), "maps", flush=True)
ai = pd.read_csv(ai_path, dtype={"lb_id": str, "key": str})

sw = sw.sort_values(["lb_id", "seconds"]).copy()
for w in (4, 8, 16):
    sw[f"diff_win{w}"] = sw.groupby("lb_id").pred_swing_diff.transform(lambda s: s.rolling(w, min_periods=1, center=True).mean())
g = sw.groupby(["lb_id", "hand"])
sw["gap_prev_same"] = (sw.seconds - g.seconds.shift(1)).clip(upper=3)
sw["gap_prev_any"] = (sw.seconds - sw.groupby("lb_id").seconds.shift(1)).clip(upper=3)
sw["t_frac"] = sw.seconds / sw.groupby("lb_id").seconds.transform("max")
sw["t_abs"] = sw.seconds
feat = ["pred_swing_diff", "diff_win4", "diff_win8", "diff_win16", "pred_swing_speed", "pred_frequency", "pred_swing_tech", "pred_hit_distance",
        "pred_angle_strain", "pred_reposition", "pred_rotation", "pred_stress", "pred_parity_error", "pred_njs_buff", "gap_prev_same", "gap_prev_any", "is_dot", "note_count", "njs"]
nn = n[(n.scoring_type == replays.SCORING_NORMAL) & n.event_type.isin([0, 1, 2]) & (n.swing_i >= 0)].copy()
nn = nn.merge(sw[["lb_id", "swing_i", "t_frac", "t_abs"] + feat], on=["lb_id", "swing_i"], how="inner")
nn = replays.attach_ai(nn, ai)
nn = nn.merge(rp[["score_id", "stratum_kind", "accuracy"]], on="score_id")
nn["bad"] = (nn.event_type != 0).astype(int)
nn["ml_err"] = 1 - nn.ai_acc
nn["dens_local"] = 1 / nn.gap_prev_any.clip(lower=0.03)
nn["inv_gap_same"] = 1 / nn.gap_prev_same.clip(lower=0.05)
print("events", len(nn), "bad rate by stratum:", nn.groupby("stratum_kind").bad.mean().round(4).to_dict())
hashes = pd.read_csv(os.path.join(d, "leaderboards.csv"), dtype={"lb_id": str}).set_index("lb_id").hash

def auc_pooled(df, col):
    d_ = df[[col, "bad"]].dropna()
    return roc_auc_score(d_.bad, d_[col]) if d_.bad.nunique() == 2 else np.nan
def auc_within(df, col, min_pos=15):
    vals, ws = [], []
    for lb, gg in df.groupby("lb_id"):
        dd = gg[[col, "bad"]].dropna()
        if dd.bad.sum() >= min_pos and dd.bad.nunique() == 2:
            vals.append(roc_auc_score(dd.bad, dd[col])); ws.append(dd.bad.sum())
    return np.average(vals, weights=ws) if vals else np.nan

def auc_within_replay(df, col, min_pos=8):
    """AUC of a feature for ranking bad/miss notes WITHIN each replay: player skill is removed by construction."""
    vals, ws = [], []
    for sid, gg in df.groupby("score_id"):
        if gg.bad.sum() >= min_pos and gg.bad.nunique() == 2:
            dd = gg[[col, "bad"]].dropna()
            if dd.bad.nunique() == 2:
                vals.append(roc_auc_score(dd.bad, dd[col])); ws.append(dd.bad.sum())
    return np.average(vals, weights=ws) if vals else np.nan

res = {}
single = ["pred_swing_diff", "diff_win4", "diff_win8", "diff_win16", "pred_swing_speed", "pred_frequency", "inv_gap_same", "dens_local", "pred_swing_tech",
          "pred_hit_distance", "pred_angle_strain", "pred_reposition", "pred_rotation", "pred_stress", "pred_parity_error", "ml_err", "t_frac", "t_abs"]
for st in ("mid", "top"):
    sub = nn[nn.stratum_kind == st]
    if sub.bad.sum() < 50: continue
    print(f"\n=== stratum {st}: {len(sub)} events, {sub.bad.sum()} bad/miss")
    rows = []
    for c in single:
        rows.append((c, auc_pooled(sub, c), auc_within(sub, c), auc_within_replay(sub, c)))
    R = pd.DataFrame(rows, columns=["feature", "AUC pooled", "AUC within-map (>=15 events)", "AUC within-replay (>=8 events)"]).sort_values("AUC within-replay (>=8 events)", ascending=False)
    print(R.round(3).to_string(index=False))
    res[f"single_{st}"] = R.set_index("feature").round(4).to_dict()
    # GBM song-grouped
    X = sub[feat + ["ml_err", "t_frac", "dens_local", "accuracy"]].values.astype(float)
    y = sub.bad.values; groups = sub.lb_id.map(hashes).values
    p = np.zeros(len(sub))
    for trn, tst in GroupKFold(5).split(X, y, groups):
        m = HistGradientBoostingClassifier(max_iter=200, learning_rate=0.07, max_leaf_nodes=24, min_samples_leaf=100, l2_regularization=1.0, random_state=0).fit(X[trn], y[trn])
        p[tst] = m.predict_proba(X[tst])[:, 1]
    sub = sub.assign(p=p)
    res[f"gbm_{st}"] = dict(auc_pooled=float(roc_auc_score(y, p)), auc_within=float(auc_within(sub, "p")), auc_within_replay=float(auc_within_replay(sub, "p")))
    print(f"GBM (swing feats + ML + time + skill proxy), song-grouped CV: AUC pooled {res[f'gbm_{st}']['auc_pooled']:.3f}, within-map {res[f'gbm_{st}']['auc_within']:.3f}, within-replay {res[f'gbm_{st}']['auc_within_replay']:.3f}")
    X2 = sub[["pred_swing_diff", "diff_win8", "accuracy"]].values.astype(float)
    p2 = np.zeros(len(sub))
    for trn, tst in GroupKFold(5).split(X2, y, groups):
        m = HistGradientBoostingClassifier(max_iter=150, learning_rate=0.07, max_leaf_nodes=12, min_samples_leaf=200, random_state=0).fit(X2[trn], y[trn]); p2[tst] = m.predict_proba(X2[tst])[:, 1]
    res[f"gbm_swingdiff_only_{st}"] = dict(auc_pooled=float(roc_auc_score(y, p2)), auc_within=float(auc_within(sub.assign(p=p2), "p")), auc_within_replay=float(auc_within_replay(sub.assign(p=p2), "p")))
    print(f"GBM on swing_diff + window + skill proxy only: pooled {res[f'gbm_swingdiff_only_{st}']['auc_pooled']:.3f}, within-map {res[f'gbm_swingdiff_only_{st}']['auc_within']:.3f}, within-replay {res[f'gbm_swingdiff_only_{st}']['auc_within_replay']:.3f}")
json.dump(res, open(os.path.join(OUT, f"{tag}.json"), "w"), indent=1, default=float)
