"""A11: pass-side calibration from per-map attempt statistics (/leaderboard/scorestats/{id}: attempts, success rate, failure-point histogram).

NB these statistics only cover players whose attempts BeatLeader records (supporters), so they are a biased sample of attempts - fine for
relative comparisons between maps, not for absolute pass rates.
Map level: logit(success rate) vs pass / tech / notes / length / score-implied difficulty (does pass rating capture length/endurance?).
Within map: failure hazard along the song vs the analyzer's local swing difficulty and vs time into the song (fatigue).
Usage: python a11_attempts.py <scorestats.jsonl> [tag]
"""
import os, sys, json
import numpy as np, pandas as pd
import statsmodels.api as sm
sys.path.insert(0, os.path.dirname(__file__))
import data

path = sys.argv[1]
tag = sys.argv[2] if len(sys.argv) > 2 else "prod"
OUT = os.path.join(os.path.dirname(__file__), "..", "out")
rows = [json.loads(l) for l in open(path)]
S = pd.DataFrame([{k: r.get(k) for k in ("lb_id", "count", "successRate", "duration")} for r in rows])
S["nseg"] = [len(r.get("failurePoints") or []) for r in rows]
print("maps with stats", len(S), "| attempts total", int(S["count"].sum()), "| segments per map", S.nseg.value_counts().head(3).to_dict())
R = data.load_ratings(tag)
acc_d = pd.read_csv(os.path.join(OUT, f"a01_map_effects_{tag}.csv"), dtype={"lb_id": str})[["lb_id", "d", "stars", "name", "difficulty"]]
m = S.merge(R, on="lb_id").merge(acc_d, on="lb_id", suffixes=("", "_x"))
m = m[(m["count"] >= 200) & (m.successRate > 0) & (m.successRate < 1)].copy()
m["logit_s"] = np.log(m.successRate / (1 - m.successRate))
m["lnotes"] = np.log(m.n_swings); m["llen"] = np.log(m.length.clip(lower=5))
print(f"maps analysed (>=200 recorded attempts): {len(m)}; success rate quartiles {m.successRate.quantile([.25,.5,.75]).round(3).tolist()}")
print("corr(logit success, x):", {c: round(float(np.corrcoef(m.logit_s, m[c])[0, 1]), 3) for c in ["pass", "tech", "stars", "d", "lnotes", "llen", "njs_mean", "peak_ebpm", "count"]})
def r2(cols):
    return sm.OLS(m.logit_s, sm.add_constant(m[cols])).fit().rsquared
res = {k: r2(v) for k, v in {"pass": ["pass"], "pass+tech": ["pass", "tech"], "stars": ["stars"], "pass+tech+ln notes": ["pass", "tech", "lnotes"], "pass+tech+ln notes+ln length": ["pass", "tech", "lnotes", "llen"],
                              "score-implied acc difficulty d": ["d"], "d + pass + length": ["d", "pass", "llen"]}.items()}
print("R2 of logit(success rate):", {k: round(v, 3) for k, v in res.items()})
f = sm.OLS(m.logit_s, sm.add_constant(m[["pass", "tech", "lnotes"]])).fit()
print("coef:", f.params.round(3).to_dict(), " t:", f.tvalues.round(1).to_dict(), "(negative ln-notes coefficient = longer maps are failed more often at equal pass/tech)")
m["popularity"] = np.log(m["count"])
print(f"corr(popularity, stars) {np.corrcoef(m.popularity, m.stars)[0,1]:+.2f}  -> attempts concentrate on lower/mid stars; success rates are relative to who attempts")

# ---- within-map hazard profile vs local analyzer difficulty
sw = data.load_swings(tag)
keep = set(m.lb_id)
sw = sw[sw.lb_id.isin(keep)]
byswing = {lb: g.sort_values("seconds") for lb, g in sw.groupby("lb_id")}
prof = []
for r in rows:
    lb = r["lb_id"]
    if lb not in keep or lb not in byswing or not r.get("failurePoints"): continue
    fp = r["failurePoints"]; k = len(fp); dur = r["duration"]
    fails = np.array([x["fails"] for x in fp]); other = np.array([x["restarts"] + x["quits"] for x in fp])
    alive = 1 - np.concatenate([[0], np.cumsum(fails + other)[:-1]])
    haz = np.where(alive > 0.05, fails / np.maximum(alive, 1e-9), np.nan)
    g = byswing[lb]; t = g.seconds.values; first = t.min()
    edges = np.linspace(0, dur, k + 1)
    for i in range(k):
        sel = (t >= edges[i]) & (t < edges[i + 1])
        if sel.sum() < 3: continue
        prof.append((lb, i, (i + 0.5) / k, haz[i], g.swing_diff.values[sel].mean(), np.quantile(g.swing_diff.values[sel], .9), sel.sum() / ((edges[i + 1] - edges[i]) + 1e-9),
                     g.swing_tech.values[sel].mean(), alive[i]))
P = pd.DataFrame(prof, columns=["lb_id", "seg", "t_frac", "haz", "sd_mean", "sd_p90", "density", "tech_mean", "alive"]).dropna()
print(f"\nsegments with hazard: {len(P)} from {P.lb_id.nunique()} maps")
P["lhaz"] = np.log(P.haz.clip(lower=1e-4)); P["lsd"] = np.log(P.sd_mean.clip(lower=1e-2))
# within-map demeaned regression of log hazard on local difficulty and time
for c in ["lhaz", "lsd", "t_frac", "tech_mean", "density"]:
    P[c + "_w"] = P[c] - P.groupby("lb_id")[c].transform("mean")
f2 = sm.OLS(P.lhaz_w, P[["lsd_w", "t_frac_w", "tech_mean_w", "density_w"]]).fit()
print("within-map log-hazard ~ local swing difficulty + position in song + tech + density (weighted by nothing):")
print(pd.DataFrame({"coef": f2.params, "t": f2.tvalues}).round(3).to_string(), f"\n  within-map R2 {f2.rsquared:.3f}")
per = P.groupby("lb_id").apply(lambda g: g.lhaz.corr(g.lsd, method="spearman") if len(g) >= 8 else np.nan)
print(f"per-map Spearman(hazard, local swing difficulty): mean {per.mean():.3f}, share of maps > 0: {(per > 0).mean():.2f}")
per_t = P.groupby("lb_id").apply(lambda g: g.lhaz.corr(g.t_frac, method="spearman") if len(g) >= 8 else np.nan)
print(f"per-map Spearman(hazard, time into song): mean {per_t.mean():.3f}  (positive = hazard rises late: endurance)")
json.dump(dict(r2=res, within_r2=float(f2.rsquared), within_coef=f2.params.round(4).to_dict(), per_map_sp_lsd=float(per.mean()), per_map_sp_time=float(per_t.mean())),
          open(os.path.join(OUT, f"a11_{tag}.json"), "w"), indent=1)
m.to_csv(os.path.join(OUT, f"a11_attempt_maps_{tag}.csv"), index=False)
