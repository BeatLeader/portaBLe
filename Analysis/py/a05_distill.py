"""A5: can a transparent algorithm built from analyzer swing features replace the ML acc model?

Teacher targets: (1) the ML model's raw predicted accuracy (log error), (2) the data-implied map difficulty d_j.
Student: map-aggregated swing features (the same quantities the pass/tech algorithm already computes).
Reported: 5-fold CV R2 on maps for linear (closed-form-able) and GBM (ceiling) students, vs ML itself on d_j.
"""
import os, sys, json
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import data, latent, mapfeatures
from sklearn.linear_model import RidgeCV
from sklearn.ensemble import GradientBoostingRegressor
from sklearn.model_selection import KFold
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler

OUT = os.path.join(os.path.dirname(__file__), "..", "out")
tag = sys.argv[1] if len(sys.argv) > 1 else "prod"
cache = os.path.join(OUT, f"mapfeat_{tag}.parquet")
if os.path.exists(cache):
    F = pd.read_parquet(cache)
else:
    F = mapfeatures.aggregate(data.load_swings(tag)); F.to_parquet(cache)
mp = pd.read_csv(os.path.join(OUT, f"a01_map_effects_{tag}.csv"), dtype={"lb_id": str})
F = F.rename(columns={"njs_mean": "sw_njs_mean"}); mp = mp.merge(F, on="lb_id")
mp = mp[mp["mode"] == "Standard"].reset_index(drop=True)
mp["ai_err_raw"] = latent.err_transform(mp.ai_raw_acc)
mp["ai_err"] = latent.err_transform(mp.predicted_acc)
mp["log_len"] = np.log(mp.length.clip(lower=5)); mp["log_notes"] = np.log(mp.n_notes)
mp["density"] = mp.n_notes / mp.length.clip(lower=1)
print("maps", len(mp), "swing features", F.shape[1] - 1)

swing_feats = [c for c in F.columns if c != "lb_id"]
base_feats = ["log_len", "log_notes", "density", "bpm", "njs_mean"]
analyzer_out = ["pass", "tech", "low_note_nerf", "linear_pct", "multi_pct", "parity_errors"]

def cv(feats, target, model="lin", seeds=2):
    X, y = mp[feats].values, mp[target].values
    pred = np.zeros(len(mp))
    for s in range(seeds):
        for trn, tst in KFold(5, shuffle=True, random_state=s).split(X):
            if model == "lin":
                m = make_pipeline(StandardScaler(), RidgeCV(alphas=np.logspace(-2, 3, 11)))
            else:
                m = GradientBoostingRegressor(n_estimators=400, max_depth=3, learning_rate=0.04, subsample=0.8, random_state=s)
            m.fit(X[trn], y[trn]); pred[tst] += m.predict(X[tst]) / seeds
    return 1 - np.mean((y - pred) ** 2) / np.var(y), pred

res = {}
print("\n--- (1) distillation fidelity: predict ML raw log-error from analyzer features")
for name, feats in (("analyzer outputs only (pass,tech,...)", analyzer_out + base_feats), ("swing aggregates", swing_feats + base_feats), ("swing aggregates + outputs", swing_feats + base_feats + analyzer_out)):
    for model in ("lin", "gbm"):
        r2, _ = cv(feats, "ai_err_raw", model)
        res[f"distill|{name}|{model}"] = r2
        print(f"  {name:40s} {model}: CV R2 {r2:.3f}")

print("\n--- (2) explain data-implied map difficulty d_j (reliability ~0.999)")
r2_ai = 1 - np.var(mp.d - np.polyval(np.polyfit(mp.ai_err, mp.d, 1), mp.ai_err)) / np.var(mp.d)
print(f"  ML predAcc (linear fit)                                R2 {r2_ai:.3f}")
res["d|ML linear"] = r2_ai
for name, feats in (("ML + pass,tech,stats", ["ai_err"] + analyzer_out + base_feats),
                    ("analyzer outputs only", analyzer_out + base_feats),
                    ("swing aggregates (NO ML)", swing_feats + base_feats),
                    ("swing aggregates + outputs (NO ML)", swing_feats + base_feats + analyzer_out),
                    ("swing aggregates + outputs + ML", swing_feats + base_feats + analyzer_out + ["ai_err"])):
    for model in ("lin", "gbm"):
        r2, pred = cv(feats, "d", model)
        res[f"d|{name}|{model}"] = r2
        print(f"  {name:40s} {model}: CV R2 {r2:.3f}   resid SD {np.sqrt(np.mean((mp.d-pred)**2)):.3f}")
        if name == "swing aggregates + outputs (NO ML)" and model == "gbm":
            mp["d_hat_algo_gbm"] = pred
        if name == "swing aggregates + outputs (NO ML)" and model == "lin":
            mp["d_hat_algo_lin"] = pred
        if name == "ML + pass,tech,stats" and model == "lin":
            mp["d_hat_ml_corr"] = pred
print("\nreference: ML alone has R2", round(r2_ai, 3), "; map-effect SD", round(mp.d.std(), 3))

# which swing aggregates matter (GBM importance on d)
m = GradientBoostingRegressor(n_estimators=400, max_depth=3, learning_rate=0.04, subsample=0.8, random_state=0).fit(mp[swing_feats + base_feats + analyzer_out], mp.d)
imp = pd.Series(m.feature_importances_, index=swing_feats + base_feats + analyzer_out).sort_values(ascending=False)
print("\ntop drivers of d_j (GBM importance):\n", imp.head(15).round(3).to_string())
json.dump(res, open(os.path.join(OUT, f"a05_{tag}.json"), "w"), indent=1)
mp.to_csv(os.path.join(OUT, f"a05_maps_{tag}.csv"), index=False)
