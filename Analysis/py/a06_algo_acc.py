"""A6: honest (song-grouped) test of an ML-free, closed-form-able acc-difficulty estimate, and what it does to PP consistency.

Student models predict the data-implied map difficulty d_j from analyzer swing aggregates.
CV folds are grouped by SONG HASH (several difficulties of one song share notes/timing, so ungrouped CV leaks).
Then the predicted difficulty replaces the ML predicted accuracy inside the acc rating and PP consistency is re-measured.
"""
import os, sys, json
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import data, latent, ppmodel as pm
from sklearn.linear_model import RidgeCV
from sklearn.ensemble import GradientBoostingRegressor
from sklearn.model_selection import GroupKFold
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler

OUT = os.path.join(os.path.dirname(__file__), "..", "out")
tag = sys.argv[1] if len(sys.argv) > 1 else "prod"
mp = pd.read_csv(os.path.join(OUT, f"a05_maps_{tag}.csv"), dtype={"lb_id": str})
mp["log_swing_diff"] = np.log(mp.swing_diff_mean.clip(lower=1e-3))
mp["log_swing_diff_p99"] = np.log(mp.swing_diff_p99.clip(lower=1e-3))
groups = mp["hash"].values

def gcv(feats, target="d", model="lin", folds=5):
    X, y = mp[feats].values, mp[target].values
    pred = np.zeros(len(mp))
    for trn, tst in GroupKFold(folds).split(X, y, groups):
        if model == "lin":
            m = make_pipeline(StandardScaler(), RidgeCV(alphas=np.logspace(-2, 3, 11)))
        else:
            m = GradientBoostingRegressor(n_estimators=150, max_depth=3, learning_rate=0.06, subsample=0.8, random_state=0)
        m.fit(X[trn], y[trn]); pred[tst] = m.predict(X[tst])
    return 1 - np.mean((y - pred) ** 2) / np.var(y), pred

swing_feats = [c for c in pd.read_parquet(os.path.join(OUT, f"mapfeat_{tag}.parquet")).columns if c not in ("lb_id", "njs_mean")] + ["sw_njs_mean"]
swing_feats = [c for c in swing_feats if c in mp.columns]
base = ["log_len", "log_notes", "density", "bpm", "njs_mean"]
outs = ["pass", "tech", "low_note_nerf", "linear_pct", "multi_pct", "parity_errors"]
compact = ["log_swing_diff", "log_swing_diff_p99", "tech", "pass", "log_notes", "log_len", "njs_mean", "low_note_nerf"]
tiny = ["log_swing_diff", "tech", "log_notes"]
res = {}
r2_ai = 1 - np.var(mp.d - np.polyval(np.polyfit(mp.ai_err, mp.d, 1), mp.ai_err)) / np.var(mp.d)
print(f"ML predAcc -> d_j (2-param linear fit; no leakage possible): R2 {r2_ai:.3f}")
res["ML_linear"] = r2_ai
# ML + nonlinearity (spline-ish via GBM on one feature) for fairness
r2_ml_gbm, _ = gcv(["ai_err"], "d", "gbm"); res["ML_nonlinear_gbm"] = r2_ml_gbm
print(f"ML predAcc -> d_j, flexible 1-D GBM (song-grouped CV): R2 {r2_ml_gbm:.3f}")
for name, feats in (("TINY: log mean swing_diff + tech + log notes", tiny),
                    ("COMPACT (8 features)", compact),
                    ("all swing aggregates + base", swing_feats + base),
                    ("compact + ML predAcc", compact + ["ai_err"])):
    for model in ("lin", "gbm"):
        if name.startswith("all") and model == "gbm":
            continue
        r2, pred = gcv(feats, "d", model)
        res[f"{name}|{model}"] = r2
        print(f"  {name:48s} {model}: song-grouped CV R2 {r2:.3f}  resid SD {np.sqrt(np.mean((mp.d-pred)**2)):.3f}")
        if name.startswith("COMPACT (8") and model == "lin": mp["d_hat_compact_lin"] = pred
        if name.startswith("TINY") and model == "lin": mp["d_hat_tiny_lin"] = pred
        if name.startswith("all") and model == "lin": mp["d_hat_all_lin"] = pred
        if name.startswith("compact + ML") and model == "lin": mp["d_hat_ml_plus"] = pred
m = make_pipeline(StandardScaler(), RidgeCV(alphas=np.logspace(-2, 3, 11))).fit(mp[tiny], mp.d)
coef = dict(zip(tiny, (m[-1].coef_ / m[0].scale_).round(4)))
print("TINY model (closed form):  d_hat = %.4f + " % (m[-1].intercept_ - np.sum(m[-1].coef_ * m[0].mean_ / m[0].scale_)) + " + ".join(f"{v}*{k}" for k, v in coef.items()))

# --- distillation of the ML itself with song-grouped CV
mp["ai_err_raw_t"] = mp.ai_err_raw
for name, feats in (("TINY", tiny), ("COMPACT", compact), ("ALL", swing_feats + base)):
    r2, _ = gcv(feats, "ai_err_raw", "lin")
    res[f"distill_ML|{name}"] = r2
    print(f"  distill ML raw log-error from {name}: grouped CV R2 {r2:.3f}")
json.dump(res, open(os.path.join(OUT, f"a06_{tag}.json"), "w"), indent=1)
mp.to_csv(os.path.join(OUT, f"a06_maps_{tag}.csv"), index=False)
