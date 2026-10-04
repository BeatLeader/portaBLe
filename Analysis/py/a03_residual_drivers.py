"""A3: where does the algorithm's map-difficulty estimate deviate from score data, and why?

Uses the data-implied map difficulty d_j (a01_map_effects) and regresses it on predicted acc + candidate drivers.
Also: does the farm adjustment (ScaleFarmability) help, do pass/tech carry acc-difficulty information, etc.
"""
import os, sys, json
import numpy as np, pandas as pd
import statsmodels.api as sm
sys.path.insert(0, os.path.dirname(__file__))
import latent

OUT = os.path.join(os.path.dirname(__file__), "..", "out")
tag = sys.argv[1] if len(sys.argv) > 1 else "prod"
mp = pd.read_csv(os.path.join(OUT, f"a01_map_effects_{tag}.csv"), dtype={"lb_id": str})
mp = mp[mp["mode"] == "Standard"].copy()
mp["log_len"] = np.log(mp.length.clip(lower=5))
mp["log_notes"] = np.log(mp.n_notes)
mp["density"] = mp.n_notes / mp.length.clip(lower=1)
mp["ai_err_raw"] = latent.err_transform(mp.ai_raw_acc)
mp["ai_err"] = latent.err_transform(mp.predicted_acc)

def ols(y, cols, label, w=None):
    X = sm.add_constant(mp[cols])
    m = sm.OLS(mp[y], X).fit()
    print(f"{label:60s} R2={m.rsquared:.4f}  resid SD={np.sqrt(m.mse_resid):.4f}")
    return m

print("target: data-implied map difficulty d_j (higher = harder). SD(d_j)=%.3f\n" % mp.d.std())
m0 = ols("d", ["ai_err"], "d ~ log(1-predAcc)  [production, farm-adjusted]")
m1 = ols("d", ["ai_err_raw"], "d ~ log(1-AI acc)  [raw, before farm scaling]")
m2 = ols("d", ["ai_err_raw", "log_len", "log_notes"], "d ~ raw AI + log(length) + log(notes)")
print(m2.params.round(3).to_dict())
m3 = ols("d", ["ai_err", "pass", "tech"], "d ~ predAcc + pass + tech")
print(m3.params.round(3).to_dict(), " t:", m3.tvalues.round(1).to_dict())
m4 = ols("d", ["ai_err", "pass", "tech", "log_len", "log_notes", "njs_mean", "density", "linear_pct", "multi_pct", "low_note_nerf"], "d ~ predAcc + pass + tech + shape stats")
print(m4.params.round(3).to_dict()); print(" t:", m4.tvalues.round(1).to_dict())

# residual after production predAcc: biggest outliers + category means
mp["resid"] = m0.resid
mp["resid_z"] = mp.resid / mp.resid.std()
print("\nresidual by predicted-acc decile (mean resid, n):")
print(mp.groupby(pd.qcut(mp.predicted_acc, 10), observed=True).resid.agg(["mean", "std", "size"]).round(3).to_string())
print("\nresidual by n_notes bucket:")
print(mp.groupby(pd.cut(mp.n_notes, [0, 150, 250, 400, 600, 900, 1500, 1e5]), observed=True).resid.agg(["mean", "std", "size"]).round(3).to_string())
print("\nresidual by pass-rating bucket:")
print(mp.groupby(pd.cut(mp["pass"], [0, 1, 2, 3, 4, 5, 6, 8, 20]), observed=True).resid.agg(["mean", "std", "size"]).round(3).to_string())
print("\nresidual by tech-rating bucket:")
print(mp.groupby(pd.cut(mp["tech"], [0, 1, 2, 3, 4, 6, 8, 20]), observed=True).resid.agg(["mean", "std", "size"]).round(3).to_string())

cols = ["lb_id", "name", "difficulty", "stars", "predicted_acc", "pass", "tech", "n_notes", "length", "n", "resid"]
print("\nmaps the AI finds EASIER than players do (resid > 0, larger d = harder in practice):")
print(mp.sort_values("resid", ascending=False)[cols].head(12).round(3).to_string(index=False))
print("\nmaps the AI finds HARDER than players do:")
print(mp.sort_values("resid")[cols].head(12).round(3).to_string(index=False))
mp.to_csv(os.path.join(OUT, f"a03_resid_{tag}.csv"), index=False)
