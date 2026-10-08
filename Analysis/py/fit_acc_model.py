"""Fit the algorithmic (ML-free) accuracy-difficulty model and export RatingAPI/acc_model.json.

Target: score-implied map difficulty d_j from the additive skill/difficulty model on modifier-free scores
        (log(1-acc_ij) = d_j - a_i), i.e. *all* clean scores, each player's skill estimated jointly.
Inputs: features_<tag>.csv from RatingsDump (C# AccDifficultyFeatures => training uses exactly what RatingAPI computes),
        ratings_<mltag>.csv (production ML ratings, for comparison and PP-scale calibration).
Model:  standardized ridge regression on all features (alpha by inner CV), evaluated with song-grouped CV.
Calibration: predictedAcc = 1 - exp(d_hat - a_ref); a_ref chosen so the weighted PP of the top 1000 players is
        unchanged versus the ML ratings (PP scale preserved; only the distribution across maps changes).

Usage: python fit_acc_model.py --data <dir> --features <features csv> [--out RatingAPI/acc_model.json]
"""
import argparse, json, os, sys, datetime
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import data, latent, ppmodel as pm
from sklearn.linear_model import RidgeCV, LassoCV
from sklearn.model_selection import GroupKFold
from sklearn.preprocessing import StandardScaler
from sklearn.pipeline import make_pipeline

ap = argparse.ArgumentParser()
ap.add_argument("--data", required=True)
ap.add_argument("--features", required=True)
ap.add_argument("--ratings", default=None, help="ML ratings csv (default <data>/ratings/ratings_prod.csv)")
ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "..", "..", "RatingAPI", "acc_model.json"))
ap.add_argument("--report", default=os.path.join(os.path.dirname(__file__), "..", "out", "fit_acc_model.json"))
ap.add_argument("--min-scores", type=int, default=40)
ap.add_argument("--one-saber", action="store_true", help="also train on One Saber maps, with their own skill sensitivity (mode_skill_scale)")
args = ap.parse_args()
os.environ["ANALYSIS_DATA"] = args.data
data.DATA = args.data

# ---------------- target: score-implied difficulty
scores = data.load_scores(clean_only=True)
scores["lb_id"] = scores.lb_id.astype(str)
maps = data.load_maps()
scores = scores.merge(maps[["lb_id", "mode", "hash"]], on="lb_id")
d, players, map_ids = latent.prepare(scores, min_player_scores=15, min_map_scores=args.min_scores)
z = latent.err_transform(d.acc.values)
a, dj = latent.fit_additive(d.pi.values, d.mi.values, z, len(players), len(map_ids))
target = pd.DataFrame({"lb_id": map_ids, "d": dj, "n": np.bincount(d.mi.values), "mode": "Standard"})
print(f"target: {len(d)} clean scores, {len(players)} players, {len(map_ids)} maps (Standard)")
mode_scale = {}
if args.one_saber:
    # One Saber: log(1 - acc) = d_j - beta * skill_i, skill from the Standard fit; beta pooled within maps
    sk = pd.Series(a, index=pd.Index(players).astype(str))
    o = scores[(scores["mode"] == "OneSaber")].copy(); o["skill"] = o.player.astype(str).map(sk); o = o.dropna(subset=["skill"])
    o = o[o.lb_id.map(o.lb_id.value_counts()) >= args.min_scores]
    o["z"] = latent.err_transform(o.acc.values)
    zc = o.z - o.groupby("lb_id").z.transform("mean"); sc = o.skill - o.groupby("lb_id").skill.transform("mean")
    beta = float(-np.sum(zc * sc) / np.sum(sc * sc))
    mode_scale["OneSaber"] = beta
    od = (o.z + beta * o.skill).groupby(o.lb_id).agg(["mean", "size"])
    target = pd.concat([target, pd.DataFrame({"lb_id": od.index, "d": od["mean"].values, "n": od["size"].values, "mode": "OneSaber"})], ignore_index=True)
    print(f"One Saber: {len(od)} maps, {len(o)} clean scores; skill sensitivity beta {beta:.3f}")

# ---------------- features (C#) + ML ratings
F = pd.read_csv(args.features, dtype={"lb_id": str})
feat_names = [c for c in F.columns if c not in ("lb_id", "mod")]
Fn = F[F["mod"] == "none"].drop(columns="mod").drop_duplicates("lb_id")
R = pd.read_csv(args.ratings or os.path.join(args.data, "ratings", "ratings_prod.csv"), dtype={"lb_id": str})
Rn = R[R["mod"] == "none"].drop_duplicates("lb_id")
M = target.merge(Fn, on="lb_id").merge(Rn[["lb_id", "predicted_acc", "acc_rating", "pass", "tech", "low_note_nerf", "stars"]].rename(
    columns={"pass": "pass_r", "tech": "tech_r", "low_note_nerf": "nerf_r"}), on="lb_id").merge(maps[["lb_id", "hash"]], on="lb_id")
M = M.replace([np.inf, -np.inf], np.nan).dropna(subset=feat_names + ["predicted_acc"]).reset_index(drop=True)
M["ml_err"] = latent.err_transform(M.predicted_acc)
M["beta"] = M["mode"].map(lambda m: mode_scale.get(m, 1.0)).astype(float)
groups = M["hash"].str.upper().values
print(f"training maps: {len(M)}; features: {len(feat_names)}")

def gcv(make_model, cols, y="d", folds=5):
    X, t = M[cols].values, M[y].values
    pred = np.zeros(len(M))
    for trn, tst in GroupKFold(folds).split(X, t, groups):
        m = make_model().fit(X[trn], t[trn]); pred[tst] = m.predict(X[tst])
    return 1 - np.mean((t - pred) ** 2) / np.var(t), pred

ridge = lambda: make_pipeline(StandardScaler(), RidgeCV(alphas=np.logspace(-2, 4, 25)))
res = {}
r2_ml = np.corrcoef(M.ml_err, M.d)[0, 1] ** 2
res["ML_linear_r2"] = r2_ml
print(f"ML predicted acc, linear fit: R2 {r2_ml:.4f}")
r2_all, pred_all = gcv(ridge, feat_names)
res["ridge_all_cv_r2"] = r2_all
print(f"ridge, all {len(feat_names)} features, song-grouped CV: R2 {r2_all:.4f}  resid SD {np.std(M.d - pred_all):.4f}")
if args.one_saber:
    os_ = (M["mode"] == "OneSaber").values
    res["one_saber_cv_resid_sd"] = float(np.std((M.d - pred_all)[os_]))
    # the same maps predicted by a model that never saw One Saber (the previous setup: extrapolation)
    std_only = ridge().fit(M.loc[~os_, feat_names].values, M.loc[~os_, "d"].values)
    ext = M.d[os_] - std_only.predict(M.loc[os_, feat_names].values)
    print(f"One Saber maps: CV resid SD {res['one_saber_cv_resid_sd']:.4f} trained with them vs {ext.std():.4f} extrapolated from Standard (mean {ext.mean():+.3f})")
small = ["pass", "tech", "log_swings"]
r2_small, _ = gcv(ridge, small); res["closed_form_3_cv_r2"] = r2_small
print(f"3-term closed form (pass, tech, ln swings): R2 {r2_small:.4f}")
# stability: lasso-selected subset
lasso = make_pipeline(StandardScaler(), LassoCV(cv=GroupKFold(5).split(M[feat_names].values, M.d.values, groups), random_state=0, max_iter=20000)).fit(M[feat_names].values, M.d.values)
sel = [f for f, c in zip(feat_names, lasso[-1].coef_) if abs(c) > 1e-6]
r2_sel, _ = gcv(ridge, sel); res["lasso_subset_cv_r2"] = r2_sel; res["lasso_subset"] = sel
print(f"lasso-selected subset ({len(sel)} features), ridge refit CV: R2 {r2_sel:.4f}")

# ---------------- final fit (all features)
final = ridge().fit(M[feat_names].values, M.d.values)
scaler, rr = final[0], final[-1]
M["d_hat"] = final.predict(M[feat_names].values)
M["d_hat_cv"] = pred_all
print(f"final ridge alpha {rr.alpha_:.3g}; in-sample R2 {1-np.var(M.d-M.d_hat)/np.var(M.d):.4f}")

# ---------------- calibration of the reference skill (preserve the top-1000 players' PP)
s = scores[scores.lb_id.isin(M.lb_id)].copy()
s["player"] = s.player.astype(str)
Mi = M.set_index("lb_id")
acc = s.acc.values
pass_r, tech_r, nerf = Mi.loc[s.lb_id, "pass_r"].values, Mi.loc[s.lb_id, "tech_r"].values, Mi.loc[s.lb_id, "nerf_r"].values

def top_pp(acc_rating):
    full, *_ = pm.pp(acc, acc_rating, pass_r, tech_r)
    t, _ = pm.player_totals(pd.DataFrame({"player": s.player.values, "pp": full}), "pp", "player")
    return t.pp.sort_values(ascending=False).head(1000).mean()

target_pp = top_pp(Mi.loc[s.lb_id, "acc_rating"].values)
dhat_s = Mi.loc[s.lb_id, "d_hat"].values
beta_s = Mi.loc[s.lb_id, "beta"].values
def algo_rating(a_ref):
    pred = np.clip(1 - np.exp(dhat_s - beta_s * a_ref), 0.5, 0.9995)
    return pm.acc_rating_from_predicted(pred) * nerf
lo, hi = 2.5, 6.0
for _ in range(40):
    mid = (lo + hi) / 2
    # larger a_ref => higher predicted acc => lower acc rating => lower PP
    if top_pp(algo_rating(mid)) > target_pp: lo = mid
    else: hi = mid
a_ref = (lo + hi) / 2
naive = M.d[M.n >= 100].mean() - M.ml_err[M.n >= 100].mean()
print(f"reference skill: PP-preserving a_ref={a_ref:.4f} (mean-error matching would be {naive:.4f}); top-1000 mean PP {target_pp:.1f}")
res.update(a_ref=a_ref, a_ref_mean_error=naive, top1000_pp=target_pp, alpha=float(rr.alpha_))

spec = {
    "version": 1,
    "description": ("Algorithmic accuracy difficulty (ML-free). Linear (ridge) model over AccDifficultyFeatures predicting the score-implied "
                    "map difficulty d = log error rate; predictedAcc = 1 - exp(d - reference_skill). "
                    f"Fitted {datetime.date.today()} on {len(d)} modifier-free scores / {len(M)} maps; song-grouped CV R2 {r2_all:.3f} "
                    f"(ML predicted acc: {r2_ml:.3f}). reference_skill preserves the top-1000 players' PP. See Analysis/REPORT.md."),
    "features": feat_names,
    "mean": [float(x) for x in scaler.mean_],
    "scale": [float(x) for x in scaler.scale_],
    "coef": [float(x) for x in rr.coef_],
    "intercept": float(rr.intercept_),
    "reference_skill": float(a_ref),
    "min_predicted_acc": 0.5,
    "max_predicted_acc": 0.9995,
}
if mode_scale: spec["mode_skill_scale"] = mode_scale
os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
json.dump(spec, open(args.out, "w"), indent=1)
print("wrote", os.path.abspath(args.out))
top = pd.Series(np.abs(rr.coef_), index=feat_names).sort_values(ascending=False)
print("largest standardized weights:\n", pd.Series(rr.coef_, index=feat_names)[top.index[:15]].round(4).to_string())
os.makedirs(os.path.dirname(os.path.abspath(args.report)), exist_ok=True)
json.dump(res, open(args.report, "w"), indent=1, default=float)
M[["lb_id", "d", "n", "d_hat", "d_hat_cv", "ml_err", "predicted_acc", "acc_rating", "stars"]].to_csv(os.path.splitext(args.report)[0] + "_maps.csv", index=False)
