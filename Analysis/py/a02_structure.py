"""A2: structure left in the scores after additive skill + difficulty.

  * baselines (global / player-only / map-only / additive) on held-out scores
  * extra latent dimensions (rank 1..3 interaction) -> is difficulty one-dimensional?
  * per-map slope model z = c_j + s_j * (-a_i): how much does a per-map *curve* (offset + slope) help held-out,
    how much of slope variance is real (vs noise), and is it predictable from map features / algorithm ratings?
  * skill-band consistency: do maps keep their difficulty ordering for top vs mid players?
"""
import json, os, sys
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import data, latent

OUT = os.path.join(os.path.dirname(__file__), "..", "out")
tag = sys.argv[1] if len(sys.argv) > 1 else "prod"

df = data.scores_with_ratings(tag)
d, players, maps = latent.prepare(df)
z = latent.err_transform(d.acc.values)
pi, mi = d.pi.values, d.mi.values
P, M = len(players), len(maps)
rng = np.random.default_rng(0)
test = rng.random(len(d)) < 0.15
tr = ~test

def r2(pred, mask=test):
    return 1 - np.mean((z[mask] - pred) ** 2) / np.var(z[mask])
res = {}

# --- baselines
gm = z[tr].mean()
p_mean = np.bincount(pi[tr], weights=z[tr], minlength=P) / np.maximum(np.bincount(pi[tr], minlength=P), 1)
m_mean = np.bincount(mi[tr], weights=z[tr], minlength=M) / np.maximum(np.bincount(mi[tr], minlength=M), 1)
res["global_mean"] = r2(np.full(test.sum(), gm))
res["player_only"] = r2(p_mean[pi[test]])
res["map_only"] = r2(m_mean[mi[test]])
a, dj = latent.fit_additive(pi[tr], mi[tr], z[tr], P, M)
base_te = dj[mi[test]] - a[pi[test]]
res["additive"] = r2(base_te)
print({k: round(v, 4) for k, v in res.items()})

# --- extra latent dimensions on additive residuals
resid_tr = z[tr] - (dj[mi[tr]] - a[pi[tr]])
for k in (1, 2, 3):
    U, V = latent.fit_lowrank(pi[tr], mi[tr], resid_tr, P, M, rank=k, iters=12, lam=8.0)
    pred = base_te + np.einsum("ij,ij->i", U[pi[test]], V[mi[test]])
    res[f"additive+rank{k}"] = r2(pred)
    print(f"additive + rank-{k} interaction: held-out R2 {res[f'additive+rank{k}']:.4f}")
    if k == 2:
        U2, V2 = U, V

# --- per-map slope model: z_ij = c_j + s_j * (-a_i)
x = -a[pi]
cnt = np.bincount(mi[tr], minlength=M).astype(float)
sx = np.bincount(mi[tr], weights=x[tr], minlength=M); sz = np.bincount(mi[tr], weights=z[tr], minlength=M)
sxx = np.bincount(mi[tr], weights=x[tr] ** 2, minlength=M); sxz = np.bincount(mi[tr], weights=x[tr] * z[tr], minlength=M)
den = cnt * sxx - sx ** 2
s_j = np.where(den > 1e-9, (cnt * sxz - sx * sz) / np.maximum(den, 1e-9), 1.0)
c_j = (sz - s_j * sx) / np.maximum(cnt, 1)
pred_slope = c_j[mi[test]] + s_j[mi[test]] * x[test]
res["per_map_slope"] = r2(pred_slope)
print(f"per-map offset+slope: held-out R2 {res['per_map_slope']:.4f} (additive {res['additive']:.4f})")

# noise level of slopes -> true slope heterogeneity
resid_s = z[tr] - (c_j[mi[tr]] + s_j[mi[tr]] * x[tr])
sigma2 = np.bincount(mi[tr], weights=resid_s ** 2, minlength=M) / np.maximum(cnt - 2, 1)
var_s = sigma2 * cnt / np.maximum(den, 1e-9)
good = (cnt >= 100) & (den > 1e-9)
print(f"slope s_j: mean {s_j[good].mean():.3f} SD {s_j[good].std():.3f}; mean sampling variance {var_s[good].mean():.5f} -> true SD ~ {np.sqrt(max(s_j[good].var()-var_s[good].mean(),0)):.3f}")
res["slope_sd_obs"] = float(s_j[good].std()); res["slope_sd_true"] = float(np.sqrt(max(s_j[good].var() - var_s[good].mean(), 0)))

# --- are slopes predictable from the algorithm's outputs / map stats?  (CV on maps)
mp = pd.DataFrame({"lb_id": maps, "c": c_j, "s": s_j, "n": cnt, "d": dj, "s_var": var_s}).merge(data.load_ratings(tag), on="lb_id").merge(data.load_maps(), on="lb_id")
mp = mp[(mp.n >= 100)].copy()
mp["log_err_pred"] = latent.err_transform(mp.predicted_acc)
mp["density"] = mp.n_notes / mp.length.clip(lower=1)
feats = ["log_err_pred", "pass", "tech", "acc_rating", "low_note_nerf", "n_swings", "length", "density", "njs_mean", "linear_pct", "multi_pct", "parity_errors", "bpm", "n_bombs", "n_walls", "stacks", "towers", "sliders", "windows", "chains", "dodge_walls", "crouch_walls", "bomb_avoid"]
from sklearn.ensemble import GradientBoostingRegressor
from sklearn.model_selection import KFold
pred = np.zeros(len(mp))
w = 1 / (mp.s_var + 0.01)
for trn, tst in KFold(5, shuffle=True, random_state=1).split(mp):
    m = GradientBoostingRegressor(n_estimators=200, max_depth=3, learning_rate=0.05, subsample=0.8, random_state=0).fit(mp.iloc[trn][feats], mp.s.iloc[trn], sample_weight=w.iloc[trn])
    pred[tst] = m.predict(mp.iloc[tst][feats])
true_var = max(mp.s.var() - mp.s_var.mean(), 1e-9)
res["slope_cv_explained_true_var"] = float((mp.s.var() - np.mean((mp.s - pred) ** 2)) / true_var)
print(f"CV: explained share of TRUE slope variance by algorithm ratings + map stats: {res['slope_cv_explained_true_var']:.3f}")
print("corr(slope, ...):", {c: round(float(np.corrcoef(mp.s, mp[c])[0, 1]), 3) for c in ["log_err_pred", "pass", "tech", "acc_rating", "n_swings", "density", "njs_mean", "length", "low_note_nerf"]})

# --- skill-band consistency: refit d_j within skill terciles of players
pa = pd.Series(a)  # a_i = skill (higher = better)
terc = pd.qcut(pa.rank(method="first"), 3, labels=False).values
dband = []
for t in range(3):
    sel = terc[pi] == t
    # skill fixed from the global fit, map effect re-estimated inside the band
    cnt_b = np.bincount(mi[sel], minlength=M)
    dm = np.bincount(mi[sel], weights=(z[sel] + a[pi[sel]]), minlength=M) / np.maximum(cnt_b, 1)
    dband.append(np.where(cnt_b >= 30, dm, np.nan))
B = pd.DataFrame(np.array(dband).T, columns=["low", "mid", "high"]).dropna()
print("map-difficulty correlation between skill bands:", B.corr().round(3).to_dict())
res["band_corr_low_high"] = float(B.corr().loc["low", "high"])
print("mean offset (bands):", (B.mean()).round(3).to_dict(), "| regression slope high~low:", round(np.polyfit(B.low, B.high, 1)[0], 3))

json.dump(res, open(os.path.join(OUT, f"a02_{tag}.json"), "w"), indent=1)
mp.to_csv(os.path.join(OUT, f"a02_slopes_{tag}.csv"), index=False)
