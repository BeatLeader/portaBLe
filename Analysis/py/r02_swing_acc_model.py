"""R2: can a defined model from analyzer swing features predict per-note centre accuracy as well as the ML model?

Unit: one good cut of one replay (normal notes). Target: centre error e = 1 - acc/15.
Skill: player skill a_i from the score-dump latent model (when available) else the replay's own accuracy (clipped).
Features: per-swing analyzer predictions (swings.csv) + local context. Evaluation: song-grouped CV, R2 on
  (a) replay-note level, (b) per-note mean over top-8 replays (the target the ML was built for), with the ML as comparator.
Usage: python r02_swing_acc_model.py <replay-study dir> <ai_notes csv.gz> <player_skill csv> [out tag]
"""
import os, sys, json
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import replays
from sklearn.ensemble import HistGradientBoostingRegressor
from sklearn.model_selection import GroupKFold

d, ai_path, skill_path = sys.argv[1:4]
tag = sys.argv[4] if len(sys.argv) > 4 else "r02"
OUT = os.path.join(os.path.dirname(__file__), "..", "out")
MAXMAPS = int(sys.argv[5]) if len(sys.argv) > 5 else 0
rp, sw, n = replays.load(d)
if MAXMAPS:
    keep = pd.Series(sorted(rp.lb_id.unique())).sample(min(MAXMAPS, rp.lb_id.nunique()), random_state=0)
    rp, sw, n = rp[rp.lb_id.isin(keep)], sw[sw.lb_id.isin(keep)], n[n.lb_id.isin(keep)]
    print("subsampled to", len(keep), "maps")
ai = pd.read_csv(ai_path, dtype={"lb_id": str, "key": str})
skill = pd.read_csv(skill_path, dtype={"player": str}).set_index("player").skill

# --- swing context features
sw = sw.sort_values(["lb_id", "hand", "seconds"]).copy()
g = sw.groupby(["lb_id", "hand"])
sw["gap_prev_same"] = (sw.seconds - g.seconds.shift(1)).clip(upper=3)
sw["prev_diff"] = g.pred_swing_diff.shift(1)
sw["next_gap_same"] = (g.seconds.shift(-1) - sw.seconds).clip(upper=3)
sw = sw.sort_values(["lb_id", "seconds"])
sw["gap_prev_any"] = (sw.seconds - sw.groupby("lb_id").seconds.shift(1)).clip(upper=3)
sw["is_dot"] = sw.is_dot.astype(int)
feat_sw = ["pred_swing_diff", "pred_swing_speed", "pred_frequency", "pred_hit_distance", "pred_angle_strain", "pred_reposition", "pred_rotation",
           "pred_stress", "pred_njs_buff", "pred_swing_tech", "pred_parity_error", "pred_is_stream", "pred_forehand", "is_dot", "is_chain", "note_count",
           "njs", "cut_direction", "gap_prev_same", "next_gap_same", "gap_prev_any", "prev_diff"]
sw_cols = ["lb_id", "swing_i"] + feat_sw
sw_small = sw[sw_cols]

nn = n[(n.event_type == 0) & (n.scoring_type == replays.SCORING_NORMAL) & (n.swing_i >= 0)].copy()
nn = nn.merge(sw_small, on=["lb_id", "swing_i"], how="inner")
nn = replays.attach_ai(nn, ai)
nn = nn.merge(rp[["score_id", "player_id", "accuracy", "stratum_kind"]], on="score_id")
nn["skill"] = nn.player_id.map(skill)
nn["skill_proxy"] = np.where(nn.skill.notna(), nn.skill, np.nan)
nn["lacc"] = np.log(1 - nn.accuracy.clip(upper=0.9995))
nn["e"] = 1 - nn.acc / 15
nn = nn[nn.ai_acc.notna()].reset_index(drop=True)
print("note rows", len(nn), "with latent skill", nn.skill.notna().mean().round(3))
hashes = pd.read_csv(os.path.join(d, "leaderboards.csv"), dtype={"lb_id": str}).set_index("lb_id").hash
groups = nn.lb_id.map(hashes).values

# skill input: latent skill when known, else replay accuracy transformed (fit a linear map skill ~ log(1-acc) on known rows)
known = nn.skill.notna()
if known.sum() > 100:
    cf = np.polyfit(nn.lacc[known], nn.skill[known], 1)
    nn["skill_in"] = np.where(known, nn.skill, np.polyval(cf, nn.lacc))
else:
    nn["skill_in"] = -nn.lacc
nn["ml"] = nn.ai_acc
y = nn.e.values

def cv_predict(feats, mono=None, folds=5, additive=False):
    X = nn[feats].values
    pred = np.zeros(len(nn))
    for trn, tst in GroupKFold(folds).split(X, y, groups):
        m = HistGradientBoostingRegressor(max_iter=400 if additive else 250, learning_rate=0.08, max_leaf_nodes=24, min_samples_leaf=200,
                                          l2_regularization=1.0, monotonic_cst=mono, random_state=0,
                                          interaction_cst="no_interactions" if additive else None)
        m.fit(X[trn], y[trn]); pred[tst] = m.predict(X[tst])
    return pred

def report(name, pred):
    r2 = 1 - np.mean((y - pred) ** 2) / np.var(y)
    # per-note top-8 mean level
    t = nn.assign(p=pred)[nn.stratum_kind == "top"]
    key = ["lb_id", "tkey", "x", "y", "color"]
    a = t.groupby(key).agg(e=("e", "mean"), p=("p", "mean"), k=("e", "size")).reset_index()
    a = a[a.k >= 5]
    r2n = np.corrcoef(a.e, a.p)[0, 1] ** 2
    w = a.groupby("lb_id").apply(lambda x: np.corrcoef(x.e, x.p)[0, 1] if len(x) > 20 else np.nan).mean()
    print(f"  {name:48s} replay-note R2 {r2:.3f} | top8-mean note R2 {r2n:.3f} | within-map corr {w:.3f}")
    return dict(r2_replay_note=r2, r2_top8_note=r2n, within_map_corr=w)

NOTE = ["x", "y", "color"]
res = {}
skill_only = ["skill_in"]
# ML as given (1-pred is its error estimate) -> its own R2 on top-8 means; then ML + skill scaling
ml_err = 1 - nn.ml.values
print("\n--- comparators / candidates (song-grouped 5-fold CV)")
if len(sys.argv) > 6 and sys.argv[6] == "gam":
    res["GAM swing feats + skill"] = report("ADDITIVE (GAM) swing features + skill (NO ML)", cv_predict(feat_sw + NOTE + ["skill_in"], [0] * (len(feat_sw) + len(NOTE)) + [-1], additive=True))
    res["GAM ML + skill"] = report("ADDITIVE ML pred + skill", cv_predict(["ml", "skill_in"], [-1, -1], additive=True))
    json.dump(res, open(os.path.join(OUT, f"{tag}_gam.json"), "w"), indent=1, default=float)
    sys.exit(0)
res["ML raw (1-pred)"] = report("ML raw predicted error (no skill scaling)", ml_err)
mono_skill = [-1]
res["skill only"] = report("skill only", cv_predict(skill_only, mono_skill))
res["ML + skill"] = report("ML pred + skill (calibrated)", cv_predict(["ml", "skill_in"], [-1, -1]))
res["swing feats + skill"] = report("swing features + skill (NO ML)", cv_predict(feat_sw + NOTE + ["skill_in"], [0] * (len(feat_sw) + len(NOTE)) + [-1]))
res["swing feats + ML + skill"] = report("swing features + ML + skill", cv_predict(feat_sw + NOTE + ["ml", "skill_in"], [0] * (len(feat_sw) + len(NOTE)) + [-1, -1]))
small = ["pred_swing_diff", "pred_swing_speed", "pred_hit_distance", "pred_angle_strain", "pred_njs_buff", "is_dot", "gap_prev_same", "pred_parity_error"]
res["small swing + skill"] = report("8 swing features + skill (NO ML)", cv_predict(small + ["skill_in"], [0] * len(small) + [-1]))
json.dump(res, open(os.path.join(OUT, f"{tag}.json"), "w"), indent=1, default=float)
nn[["lb_id", "score_id", "tkey", "x", "y", "color", "e", "ml", "skill_in", "stratum_kind"] + feat_sw].to_parquet(os.path.join(OUT, f"{tag}_rows.parquet"))
