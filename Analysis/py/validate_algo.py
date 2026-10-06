"""Validate the exported algorithmic acc model against the fresh score dump (independent of portaBLe's DB pipeline).

  1. speed modifiers: real per-map difficulty shift (SS/FS/SF scores vs clean skill) vs ML / algorithm predicted shift
  2. acc-PP bias between maps at equal skill (CV predictions for the algorithm; production Curve2 and power-law curve)
  3. player impact of swapping the source (ML -> algorithm, PP-preserving calibration), Curve2 and power-law

Usage: python validate_algo.py --data <dir> [--ml-tag prod] [--algo-tag algo]
"""
import argparse, json, os, sys
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import data, latent, ppmodel as pm

ap = argparse.ArgumentParser()
ap.add_argument("--data", required=True)
ap.add_argument("--ml-tag", default="prod")
ap.add_argument("--algo-tag", default="algo")
ap.add_argument("--fit", default=os.path.join(os.path.dirname(__file__), "..", "out", "fit_acc_model_maps.csv"))
ap.add_argument("--write-speed-scale", action="store_true", help="store the fitted shrink factors in acc_model.json")
ap.add_argument("--model", default=os.path.join(os.path.dirname(__file__), "..", "..", "RatingAPI", "acc_model.json"))
args = ap.parse_args()
data.DATA = args.data
OUT = os.path.join(os.path.dirname(__file__), "..", "out")
spec = json.load(open(args.model))
a_ref = spec["reference_skill"]
res = {}

R_ml = pd.read_csv(data.p("ratings", f"ratings_{args.ml_tag}.csv"), dtype={"lb_id": str})
R_al = pd.read_csv(data.p("ratings", f"ratings_{args.algo_tag}.csv"), dtype={"lb_id": str})
fit = pd.read_csv(args.fit, dtype={"lb_id": str}).set_index("lb_id")

# ---- latent fit on fresh clean scores (skills + d_j)
sc = data.load_scores(clean_only=True); sc["lb_id"] = sc.lb_id.astype(str)
maps = data.load_maps()
sc = sc.merge(maps[["lb_id", "mode"]], on="lb_id")
d, players, map_ids = latent.prepare(sc)
z = latent.err_transform(d.acc.values)
a, dj = latent.fit_additive(d.pi.values, d.mi.values, z, len(players), len(map_ids))
skill = pd.Series(a, index=players.astype(str)); dmap = pd.Series(dj, index=map_ids.astype(str))

# ---- 1. speed modifiers
allsc = pd.read_parquet(data.p("scores.parquet")).rename(columns={"LeaderboardId": "lb_id", "PlayerId": "player", "Accuracy": "acc"})
allsc["Modifiers"] = allsc.Modifiers.fillna(""); allsc["lb_id"] = allsc.lb_id.astype(str); allsc["player"] = allsc.player.astype(str)
allsc = allsc[(allsc.acc > 0) & (allsc.acc <= 1)]
def speed_of(m):
    parts = set(x.strip() for x in m.split(",") if x.strip())
    if parts - {"FS", "SF", "SS", "IF", "BE"}: return None
    for k in ("SF", "FS", "SS"):
        if k in parts: return k
allsc["spd"] = allsc.Modifiers.map(speed_of)
sp = allsc[allsc.spd.notna()].copy()
sp["skill"] = sp.player.map(skill); sp["d"] = sp.lb_id.map(dmap)
sp = sp.dropna(subset=["skill", "d"])
sp["shift"] = latent.err_transform(sp.acc) - (sp.d - sp.skill)
print("speed-modifier scores:", len(sp), sp.spd.value_counts().to_dict())
def le(x): return latent.err_transform(x)
for spd, key in (("SS", "SS"), ("FS", "FS"), ("SF", "SFS")):
    g = sp[sp.spd == spd].groupby("lb_id").agg(shift=("shift", "mean"), n=("shift", "size"))
    g = g[g.n >= 30]
    ml_m = R_ml[R_ml["mod"] == key].set_index("lb_id").predicted_acc; ml_n = R_ml[R_ml["mod"] == "none"].set_index("lb_id").predicted_acc
    al_m = R_al[R_al["mod"] == key].set_index("lb_id").algo_predicted_acc; al_n = R_al[R_al["mod"] == "none"].set_index("lb_id").algo_predicted_acc
    g["ml"] = le(ml_m.reindex(g.index)) - le(ml_n.reindex(g.index)); g["algo"] = le(al_m.reindex(g.index)) - le(al_n.reindex(g.index))
    g = g.dropna()
    r_ml = np.corrcoef(g["shift"], g.ml)[0, 1]; r_al = np.corrcoef(g["shift"], g.algo)[0, 1]
    b_al = np.polyfit(g.algo, g["shift"], 1)[0]
    print(f"  {spd}: {len(g)} maps | R2 of real per-map shift: ML {r_ml**2:.3f}, algorithm {r_al**2:.3f} (slope {b_al:.2f}) | mean shift real {g['shift'].mean():+.3f} ML {g.ml.mean():+.3f} algo {g.algo.mean():+.3f}")
    k = float(np.sum(g.n * g.algo * g["shift"]) / np.sum(g.n * g.algo ** 2))   # weighted regression through the origin
    # range in which real scores confirm the (scaled) shift; beyond it the model only extrapolates
    limit = float(abs((k * g.algo).quantile(0.01 if spd == "SS" else 0.99)))
    res[f"speed_{spd}"] = dict(maps=len(g), r2_ml=r_ml ** 2, r2_algo=r_al ** 2, slope_algo=b_al, mean_real=g["shift"].mean(), mean_ml=g.ml.mean(), mean_algo=g.algo.mean(), shrink=k, limit=limit)
    print(f"     shrink factor (real ~ k * algorithm shift, weighted, through origin): k = {k:.3f}; confirmed range |k * shift| <= {limit:.3f}")

if args.write_speed_scale:
    # only meaningful when ratings_<algo-tag> were produced WITHOUT a speed_shift_scale already in the model
    spec["speed_shift_scale"] = [[0.85, res["speed_SS"]["shrink"]], [1.0, 1.0], [1.2, res["speed_FS"]["shrink"]], [1.5, res["speed_SF"]["shrink"]]]
    spec["speed_shift_limit"] = [[0.85, res["speed_SS"]["limit"]], [1.2, res["speed_FS"]["limit"]], [1.5, res["speed_SF"]["limit"]]]
    json.dump(spec, open(args.model, "w"), indent=1)
    print("wrote speed_shift_scale / speed_shift_limit to", args.model, spec["speed_shift_scale"], spec["speed_shift_limit"])

# ---- 2. acc-PP bias at equal skill (algorithm uses out-of-fold predictions)
F = fit.copy()
F["n_ok"] = F.n >= 100
w = F.n_ok.values
nerf = R_ml[R_ml["mod"] == "none"].set_index("lb_id").low_note_nerf.reindex(F.index).values
def pred_from(dh, aref): return np.clip(1 - np.exp(dh - aref), 0.5, 0.9995)
sources = {"ML": F.predicted_acc.values, "algorithm (CV)": pred_from(F.d_hat_cv.values, a_ref), "oracle": pred_from(F.d.values, a_ref)}
G = 0.601; E0 = 0.0016
curves = {"Curve2": pm.curve2, "power-law": lambda x: np.power((1 - x + E0) / (0.05 + E0), -G)}
dmean = F.d[w].mean()
rows = []
for cn, cf in curves.items():
    for sn, pred in sources.items():
        rating = 1 / cf(pred)
        r = {"curve": cn, "source": sn}
        for x in (0.90, 0.93, 0.95, 0.97, 0.98):
            ac = np.clip(1 - np.exp(F.d.values - (dmean - np.log(1 - x))), 0.3, 0.9995)
            r[f"{x:.2f}"] = np.std(np.log(cf(ac) * rating)[w])
        rows.append(r)
B = pd.DataFrame(rows); print("\nSD across maps of log(acc PP) at equal skill:\n", B.round(3).to_string(index=False))
res["accpp_bias"] = B.round(4).to_dict(orient="records")

# ---- 3. player impact (all clean scores, PP-preserving calibration of the exported model)
s = sc[sc.lb_id.isin(F.index)].copy(); s["player"] = s.player.astype(str)
Rn = R_ml[R_ml["mod"] == "none"].set_index("lb_id")
pass_r, tech_r, nerf_s = Rn.loc[s.lb_id, "pass"].values, Rn.loc[s.lb_id, "tech"].values, Rn.loc[s.lb_id, "low_note_nerf"].values
ml_rating = Rn.loc[s.lb_id, "acc_rating"].values
al_pred = R_al[R_al["mod"] == "none"].set_index("lb_id").algo_predicted_acc.loc[s.lb_id].values
def totals(acc_rating, curve="Curve2"):
    if curve == "Curve2":
        full, *_ = pm.pp(s.acc.values, acc_rating, pass_r, tech_r)
    else:
        p_, _, t_ = pm.pp_components(s.acc.values, acc_rating, pass_r, tech_r)
        a_ = curves["power-law"](s.acc.values) * acc_rating * 34.0
        full = pm.inflate(p_ + a_ + t_)
    t, _ = pm.player_totals(pd.DataFrame({"player": s.player.values, "pp": full}), "pp", "player")
    return t.pp
base = totals(ml_rating)
def power_rating(pred): return 15.5 / curves["power-law"](np.clip(pred + 0.0022, 0, 1)) * nerf_s
scen = {"algorithm + Curve2": totals(pm.acc_rating_from_predicted(al_pred) * nerf_s),
        "ML + power-law": totals(power_rating(Rn.loc[s.lb_id, "predicted_acc"].values), "power"),
        "algorithm + power-law": totals(power_rating(al_pred), "power")}
top_b = base.sort_values(ascending=False)
print("\nplayer impact vs production (ML + Curve2):")
for k, t in scen.items():
    common = base.index.intersection(t.index)
    spm = base.loc[common].rank().corr(t.loc[common].rank())
    o100 = len(set(top_b.head(100).index) & set(t.sort_values(ascending=False).head(100).index))
    rel = (t / base).loc[top_b.head(1000).index]
    ratio = t.sort_values(ascending=False).head(1000).mean() / top_b.head(1000).mean()
    print(f"  {k:24s} Spearman {spm:.4f} | top-100 overlap {o100} | top-1000 mean PP ratio {ratio:.3f} | top-1000 change p5/p50/p95 {rel.quantile(.05):.3f}/{rel.median():.3f}/{rel.quantile(.95):.3f}")
    res[f"impact_{k}"] = dict(spearman=spm, top100_overlap=o100, top1000_ratio=ratio, p5=rel.quantile(.05), p95=rel.quantile(.95))
json.dump(res, open(os.path.join(OUT, "validate_algo.json"), "w"), indent=1, default=float)
