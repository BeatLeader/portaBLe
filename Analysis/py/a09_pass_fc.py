"""A9: pass-side validity from full-combo flags.

P(FC_ij) = sigmoid(a_i - b_j): a_i = player cleanliness skill, b_j = map "FC difficulty" (IRLS, alternating Fisher steps).
Compared with: pass rating, tech rating, stars, the acc-difficulty d_j (is clean-play difficulty a different axis from acc difficulty?),
and with what swing aggregates / analyzer variants (prod vs corpus) explain of b_j (song-grouped CV).
"""
import os, sys, json
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import data, latent
from sklearn.linear_model import RidgeCV
from sklearn.ensemble import HistGradientBoostingRegressor
from sklearn.model_selection import GroupKFold
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler

OUT = os.path.join(os.path.dirname(__file__), "..", "out")
tag = sys.argv[1] if len(sys.argv) > 1 else "prod"
df = data.scores_with_ratings(tag)
d, players, maps = latent.prepare(df)
pi, mi = d.pi.values, d.mi.values
y = d.FC.astype(float).values
P, M = len(players), len(maps)
print("scores", len(d), "FC rate", y.mean().round(3))

def sig(x): return 1 / (1 + np.exp(-x))
a = np.zeros(P); b = np.zeros(M)
cp = np.bincount(pi, minlength=P); cm = np.bincount(mi, minlength=M)
for it in range(40):
    p = sig(a[pi] - b[mi])
    # map step (difficulty b: higher = fewer FCs)
    g = np.bincount(mi, weights=(p - y), minlength=M); h = np.bincount(mi, weights=p * (1 - p), minlength=M) + 1.0
    b += np.clip(g / h, -1, 1)
    p = sig(a[pi] - b[mi])
    g = np.bincount(pi, weights=(y - p), minlength=P); h = np.bincount(pi, weights=p * (1 - p), minlength=P) + 1.0
    a += np.clip(g / h, -1, 1)
    b -= b.mean()
p = sig(a[pi] - b[mi])
ll = -np.mean(y * np.log(p + 1e-9) + (1 - y) * np.log(1 - p + 1e-9))
ll0 = -np.mean(y * np.log(y.mean()) + (1 - y) * np.log(1 - y.mean()))
print(f"logistic additive: logloss {ll:.4f} vs base {ll0:.4f} (McFadden R2 {1-ll/ll0:.3f}); AUC-like corr(p,y)={np.corrcoef(p, y)[0,1]:.3f}")

mp = pd.DataFrame({"lb_id": maps, "b_fc": b, "n": cm}).merge(data.load_ratings(tag), on="lb_id").merge(data.load_maps(), on="lb_id")
acc_d = pd.read_csv(os.path.join(OUT, f"a01_map_effects_{tag}.csv"), dtype={"lb_id": str})[["lb_id", "d"]]
mp = mp.merge(acc_d, on="lb_id")
mp = mp[mp.n >= 100].copy()
mp["log_len"] = np.log(mp.length.clip(lower=5)); mp["log_notes"] = np.log(mp.n_notes); mp["density"] = mp.n_notes / mp.length.clip(lower=1)
print(f"\nmaps {len(mp)}; SD of FC difficulty b_j = {mp.b_fc.std():.2f} (logit units)")
print("corr(b_fc, x):", {c: round(float(np.corrcoef(mp.b_fc, mp[c])[0, 1]), 3) for c in ["pass", "tech", "stars", "d", "acc_rating", "n_notes", "log_len", "density", "njs_mean", "parity_errors", "multi_pct", "linear_pct", "n_bombs", "n_walls", "dodge_walls", "crouch_walls", "bomb_avoid", "peak_ebpm"]})
print(f"corr(b_fc, d_acc)= {np.corrcoef(mp.b_fc, mp.d)[0,1]:.3f}  -> clean-play difficulty vs accuracy difficulty (1.0 would mean one axis)")

groups = mp["hash"].values
def gcv(feats, target="b_fc"):
    X, yy = mp[feats].values, mp[target].values
    pred = np.zeros(len(mp))
    for trn, tst in GroupKFold(5).split(X, yy, groups):
        m = make_pipeline(StandardScaler(), RidgeCV(alphas=np.logspace(-2, 3, 11))).fit(X[trn], yy[trn]); pred[tst] = m.predict(X[tst])
    return 1 - np.mean((yy - pred) ** 2) / np.var(yy)
base = ["log_notes", "log_len", "density", "njs_mean"]
res = {}
for name, feats in (("pass only", ["pass"]), ("tech only", ["tech"]), ("pass+tech", ["pass", "tech"]), ("pass+tech+shape", ["pass", "tech"] + base),
                    ("+ walls/bombs/dodge", ["pass", "tech"] + base + ["n_bombs", "n_walls", "dodge_walls", "crouch_walls", "bomb_avoid"]),
                    ("+ parity/linear/multi", ["pass", "tech"] + base + ["n_bombs", "n_walls", "dodge_walls", "crouch_walls", "bomb_avoid", "parity_errors", "linear_pct", "multi_pct", "peak_ebpm"]),
                    ("stars", ["stars"])):
    res[name] = gcv(feats)
    print(f"  CV R2 of FC difficulty from [{name}]: {res[name]:.3f}")

# compare analyzer variants on the same target
mp_c = pd.DataFrame({"lb_id": maps, "b_fc": b}).merge(data.load_ratings("corpus")[["lb_id", "pass", "tech", "peak_ebpm", "parity_errors"]].add_suffix("_c").rename(columns={"lb_id_c": "lb_id"}), on="lb_id")
mm = mp.merge(mp_c[["lb_id", "pass_c", "tech_c", "peak_ebpm_c", "parity_errors_c"]], on="lb_id")
heavy = mm.parity_errors >= 50
print(f"\nparity-heavy maps (>=50 flagged parity errors): n={heavy.sum()}")
for lab, sub in (("all maps", mm), ("parity-heavy", mm[heavy])):
    r = lambda c: np.corrcoef(sub.b_fc, sub[c])[0, 1]
    print(f"  {lab:14s} corr(b_fc, pass) prod {r('pass'):.3f} corpus {r('pass_c'):.3f} | corr(b_fc, tech) prod {r('tech'):.3f} corpus {r('tech_c'):.3f}")
json.dump(res, open(os.path.join(OUT, f"a09_{tag}.json"), "w"), indent=1)
mp.to_csv(os.path.join(OUT, f"a09_fc_maps_{tag}.csv"), index=False)
pd.DataFrame({"player": players, "a_fc": a}).to_csv(os.path.join(OUT, f"a09_player_fc_{tag}.csv"), index=False)
