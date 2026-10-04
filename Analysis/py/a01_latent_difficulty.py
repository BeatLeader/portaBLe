"""A1: How well do the algorithm's map ratings explain the difficulty implied by actual scores?

Fits z = log(1-acc) = d_map - a_player on 2.7M clean scores, then:
  * variance decomposition + held-out score prediction (noise ceiling for any map-level rating)
  * correlation of data-implied map difficulty d_j with predicted acc / acc rating / pass / tech / stars
  * held-out prediction using ONLY algorithm ratings as map info (how much of the map effect they recover)
"""
import json, os, sys
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import data, latent, ppmodel as pm

OUT = os.path.join(os.path.dirname(__file__), "..", "out")
os.makedirs(OUT, exist_ok=True)
tag = sys.argv[1] if len(sys.argv) > 1 else "prod"

df = data.scores_with_ratings(tag)
print("scores", len(df), "maps", df.lb_id.nunique(), "players", df.player.nunique())
d, players, maps = latent.prepare(df)
print("after filtering: scores", len(d), "maps", len(maps), "players", len(players))
z = latent.err_transform(d.acc.values)
pi, mi = d.pi.values, d.mi.values

# ---- split by score for held-out evaluation
rng = np.random.default_rng(0)
test = rng.random(len(d)) < 0.15
tr = ~test
a, dj = latent.fit_additive(pi[tr], mi[tr], z[tr], len(players), len(maps))
pred_te = dj[mi[test]] - a[pi[test]]
pred_player_only = -a[pi[test]]
res_te = z[test] - pred_te
print(f"z variance {z.var():.4f} | held-out RMSE additive {np.sqrt(np.mean(res_te**2)):.4f}")
print(f"  held-out R2 additive {1-np.mean(res_te**2)/z[test].var():.4f}")

# full-data fit for map effects
a, dj = latent.fit_additive(pi, mi, z, len(players), len(maps))
fit = dj[mi] - a[pi]
print(f"in-sample variance share: player {np.var(a[pi])/z.var():.3f} map {np.var(dj[mi])/z.var():.3f} resid {np.var(z-fit)/z.var():.3f}")

pd.DataFrame({"player": players, "skill": a, "n_scores": np.bincount(pi)}).to_csv(os.path.join(OUT, f"a01_player_skill_{tag}.csv"), index=False)
mp = pd.DataFrame({"lb_id": maps, "d": dj, "n": np.bincount(mi)}).merge(
    data.load_ratings(tag), on="lb_id").merge(data.load_maps(), on="lb_id")
mp["d_noise_sd"] = np.sqrt(np.var(z - fit) / mp.n)           # sampling noise of each map effect
mp["log_err_pred"] = latent.err_transform(mp.predicted_acc)
mp["log_err_pred_raw"] = latent.err_transform(mp.ai_raw_acc)
mp["duration"] = mp.length
mp["density"] = mp.n_notes / mp.length.clip(lower=1)

def corr(a_, b_):
    return float(np.corrcoef(a_, b_)[0, 1])
def spearman(a_, b_):
    return float(pd.Series(a_).rank().corr(pd.Series(b_).rank()))

print("\ncorrelation of data-implied map difficulty d_j (error-rate space) with algorithm outputs (pearson / spearman):")
rows = {}
for col in ["log_err_pred", "log_err_pred_raw", "acc_rating", "pass", "tech", "stars", "n_swings", "length", "density", "njs_mean", "linear_pct", "multi_pct", "parity_errors", "low_note_nerf"]:
    rows[col] = (corr(mp.d, mp[col]), spearman(mp.d, mp[col]))
    print(f"  {col:18s} {rows[col][0]:+.3f} / {rows[col][1]:+.3f}")

# slope of d_j on predicted log-error
X = np.c_[np.ones(len(mp)), mp.log_err_pred]
beta = np.linalg.lstsq(X, mp.d, rcond=None)[0]
r = mp.d - X @ beta
print(f"\nd_j ~ a + b*log(1-predAcc): b={beta[1]:.3f} (1.0 = AI acc differences map 1:1 onto score-error differences), resid SD {r.std():.3f}, "
      f"map-effect SD {mp.d.std():.3f}, median sampling SD of d_j {mp.d_noise_sd.median():.3f}")

# held-out score prediction using ratings only: map term = f(ratings) fitted on train maps' d_j
from sklearn.ensemble import GradientBoostingRegressor
from sklearn.model_selection import KFold
feats_alg = ["log_err_pred", "pass", "tech", "acc_rating", "low_note_nerf"]
feats_all = feats_alg + ["n_swings", "length", "density", "njs_mean", "linear_pct", "multi_pct", "parity_errors", "bpm", "n_bombs", "n_walls", "stacks", "towers", "sliders", "windows", "chains", "dodge_walls", "crouch_walls", "bomb_avoid"]
def cv_map_model(feats, model_fn):
    pred = np.zeros(len(mp))
    for trn, tst in KFold(5, shuffle=True, random_state=1).split(mp):
        m = model_fn().fit(mp.iloc[trn][feats], mp.d.iloc[trn])
        pred[tst] = m.predict(mp.iloc[tst][feats])
    return pred
from sklearn.linear_model import RidgeCV
def lin(): return RidgeCV(alphas=np.logspace(-3, 3, 13))
def gbm(): return GradientBoostingRegressor(n_estimators=250, max_depth=3, learning_rate=0.05, subsample=0.8, random_state=0)
w = 1 / (mp.d_noise_sd ** 2 + 1e-6)
tot_var = np.average((mp.d - np.average(mp.d, weights=w)) ** 2, weights=w)
noise_var = np.average(mp.d_noise_sd ** 2, weights=w)
print(f"\nmap effect variance {mp.d.var():.4f}; avg sampling-noise variance {np.mean(mp.d_noise_sd**2):.5f} (=> reliability {1-np.mean(mp.d_noise_sd**2)/mp.d.var():.3f})")
summary = {}
for name, feats in (("predAcc only", ["log_err_pred"]), ("alg ratings (pred,pass,tech,accR,nerf)", feats_alg), ("alg ratings + raw map stats", feats_all)):
    for mname, fn in (("linear", lin), ("gbm", gbm)):
        p_ = cv_map_model(feats, fn)
        r2 = 1 - np.mean((mp.d - p_) ** 2) / np.var(mp.d)
        # in score space: held-out RMSE if the map term were predicted by ratings instead of fitted from scores
        print(f"  CV R2 of d_j from [{name}] ({mname}): {r2:.3f}")
        summary[f"{name}|{mname}"] = r2
json.dump({"corr": rows, "cv_r2": summary, "beta_pred": float(beta[1]),
           "var_share": {"player": float(np.var(a[pi]) / z.var()), "map": float(np.var(dj[mi]) / z.var())},
           "heldout_rmse_additive": float(np.sqrt(np.mean(res_te ** 2)))},
          open(os.path.join(OUT, f"a01_{tag}.json"), "w"), indent=1)
mp.to_csv(os.path.join(OUT, f"a01_map_effects_{tag}.csv"), index=False)
