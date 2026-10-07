"""A15: maps where the scores disagree with the algorithmic acc rating — how much is real, and what explains it.

Disagreement e_j = score-implied difficulty d_j (latent model on clean Standard scores) minus the acc model's song-grouped
out-of-fold prediction (the same ridge on AccDifficultyFeatures as fit_acc_model.py), in log error rate (+ = scores say harder).

Stage 1 (this file, writes <out>/a15_maps.parquet for the later stages):
  - reproduce the model's CV fit, sampling noise of d_j, real-disagreement SD
  - the largest disagreements both ways
  - non-map explanations: popularity, upload year, who plays the map, grind (attempts), skill slope
  - nonlinearity: gradient boosting on the same 76 features (song-grouped CV)

Usage: python a15_disagreement.py --data <dir with scores/maps parquet + ratings/features_algo.csv> [--beatsaver ...] [--attempts ...]
"""
import argparse, os, sys
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import data, latent
from sklearn.linear_model import RidgeCV
from sklearn.model_selection import GroupKFold
from sklearn.preprocessing import StandardScaler
from sklearn.pipeline import make_pipeline
from sklearn.ensemble import HistGradientBoostingRegressor

ap = argparse.ArgumentParser()
ap.add_argument("--data", required=True)
ap.add_argument("--features", default=None)
ap.add_argument("--beatsaver", default=os.path.join(os.path.dirname(__file__), "..", "out", "beatsaver_maps.csv"))
ap.add_argument("--attempts", default=None, help="attempts_effort_pairs.parquet (a13) for grind per map")
ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "..", "out"))
ap.add_argument("--min-scores", type=int, default=40)
args = ap.parse_args()
data.DATA = args.data

scores = data.load_scores(clean_only=True); scores["lb_id"] = scores.lb_id.astype(str)
maps = pd.read_parquet(data.p("maps.parquet")).rename(columns={"Id": "lb_id", "Hash": "hash", "ModeName": "mode", "DifficultyName": "difficulty", "Name": "name"})
scores = scores.merge(maps[["lb_id", "mode", "hash"]], on="lb_id")
d, players, map_ids = latent.prepare(scores, min_player_scores=15, min_map_scores=args.min_scores)
z = latent.err_transform(d.acc.values)
skill, dj = latent.fit_additive(d.pi.values, d.mi.values, z, len(players), len(map_ids))
resid = z - (dj[d.mi.values] - skill[d.pi.values])
nm = np.bincount(d.mi.values, minlength=len(map_ids))
sd_j = np.sqrt(np.bincount(d.mi.values, weights=resid ** 2, minlength=len(map_ids)) / np.maximum(nm - 1, 1))
T = pd.DataFrame({"lb_id": map_ids, "d": dj, "n": nm, "se": sd_j / np.sqrt(nm),
                  "mean_skill": np.bincount(d.mi.values, weights=skill[d.pi.values], minlength=len(map_ids)) / nm})
# skill slope: does the residual depend on the player's skill on this map? (+ = strong players do relatively worse here)
sk = skill[d.pi.values] - T.mean_skill.values[d.mi.values]
T["skill_slope"] = np.bincount(d.mi.values, weights=resid * sk, minlength=len(map_ids)) / np.maximum(np.bincount(d.mi.values, weights=sk ** 2, minlength=len(map_ids)), 1e-9)
# the same map term from the top quarter of players only (robustness: is the disagreement there for strong players too?)
q = pd.Series(skill).rank(pct=True).values
top = q[d.pi.values] >= 0.75
nt = np.bincount(d.mi.values[top], minlength=len(map_ids))
T["d_top25"] = np.where(nt >= 15, np.bincount(d.mi.values[top], weights=(z + skill[d.pi.values])[top], minlength=len(map_ids)) / np.maximum(nt, 1), np.nan)
T["n_top25"] = nt

F = pd.read_csv(args.features or data.p("ratings", "features_algo.csv"), dtype={"lb_id": str})
feat = [c for c in F.columns if c not in ("lb_id", "mod")]
Fn = F[F["mod"] == "none"].drop(columns="mod").drop_duplicates("lb_id")
M = T.merge(Fn, on="lb_id").merge(maps[["lb_id", "hash", "name", "difficulty", "Mapper"]], on="lb_id")
M = M.replace([np.inf, -np.inf], np.nan).dropna(subset=feat).reset_index(drop=True)
groups = M.hash.str.upper().values

def gcv(make, X, y, folds=5):
    pred = np.zeros(len(y))
    for trn, tst in GroupKFold(folds).split(X, y, groups):
        pred[tst] = make().fit(X[trn], y[trn]).predict(X[tst])
    return pred
ridge = lambda: make_pipeline(StandardScaler(), RidgeCV(alphas=np.logspace(-2, 4, 25)))
X = M[feat].values; y = M.d.values
M["d_cv"] = gcv(ridge, X, y)
M["e"] = M.d - M.d_cv
r2 = 1 - np.var(M.e) / np.var(y)
w = M.n >= 100
noise = np.mean(M.se[w] ** 2)
print(f"{len(M)} maps; ridge on {len(feat)} features, song-grouped CV R2 {r2:.4f}")
print(f"disagreement SD (maps with >= 100 scores): {M.e[w].std():.4f}; sampling noise SD {np.sqrt(noise):.4f}; "
      f"real disagreement SD {np.sqrt(max(M.e[w].var() - noise, 0)):.4f}")
gb = gcv(lambda: HistGradientBoostingRegressor(max_iter=600, learning_rate=0.04, max_leaf_nodes=15, min_samples_leaf=20, l2_regularization=1.0), X, y)
print(f"gradient boosting on the same features: CV R2 {1 - np.var(y - gb) / np.var(y):.4f}; "
      f"ridge + boosting on ridge residual: R2 {1 - np.var(M.e - gcv(lambda: HistGradientBoostingRegressor(max_iter=300, learning_rate=0.03, max_leaf_nodes=15, min_samples_leaf=30), X, M.e.values)) / np.var(y):.4f}")

# non-map explanations
if os.path.exists(args.beatsaver):
    bs = pd.read_csv(args.beatsaver, dtype={"hash": str})
    bs["year"] = pd.to_datetime(bs.uploaded, format="ISO8601").dt.year
    M = M.merge(bs[["hash", "year"]].assign(hash=lambda x: x.hash.str.upper()), left_on=M.hash.str.upper(), right_on="hash", how="left", suffixes=("", "_bs")).drop(columns=["key_0", "hash_bs"], errors="ignore")
if args.attempts and os.path.exists(args.attempts):
    A = pd.read_parquet(args.attempts, columns=["lb_id", "attempts_to_best", "gain_first_to_best", "luck"])
    G = A.groupby("lb_id").agg(grind_attempts=("attempts_to_best", "median"), grind_gain=("gain_first_to_best", "mean"),
                               grind_luck=("luck", "mean"), grind_players=("attempts_to_best", "size"))
    G["grind_log_attempts"] = np.log(G.grind_attempts)
    M = M.merge(G.drop(columns="grind_attempts"), left_on="lb_id", right_index=True, how="left")
M["log_n"] = np.log(M.n)
cands = [c for c in ["log_n", "year", "mean_skill", "skill_slope"] + [c for c in M.columns if c.startswith("grind_")] if c in M]
print("\ncorrelation of the disagreement with non-map factors (maps with >= 100 scores):")
for c in cands:
    v = M.loc[w, [c, "e"]].dropna()
    print(f"  {c:28s} r = {v.corr().iloc[0, 1]:+.3f}  (n {len(v)})")
print("\nby difficulty:", M[w].groupby("difficulty").e.agg(["mean", "std", "size"]).round(3).to_dict("index"))
if "year" in M:
    print("by upload year:", M[w].groupby(pd.cut(M.year[w], [0, 2019, 2021, 2023, 2100])).e.agg(["mean", "std", "size"]).round(3).to_dict("index"))
top_agree = M[w & M.d_top25.notna()]
print(f"\ntop-25 % players' map term vs all players': r {np.corrcoef(top_agree.d_top25, top_agree.d)[0, 1]:.3f}; "
      f"disagreement from top-25 % only vs all: r {np.corrcoef(top_agree.d_top25 - top_agree.d_cv - (top_agree.d_top25 - top_agree.d).mean(), top_agree.e)[0, 1]:.3f}")
show = ["name", "difficulty", "n", "e", "se", "skill_slope"] + (["year"] if "year" in M else [])
pd.set_option("display.width", 250)
print("\nscores say HARDER than the algorithm (largest e, >= 100 scores):")
print(M[w].sort_values("e", ascending=False).head(25)[show].round(3).to_string(index=False))
print("\nscores say EASIER than the algorithm:")
print(M[w].sort_values("e").head(25)[show].round(3).to_string(index=False))
os.makedirs(args.out, exist_ok=True)
M.to_parquet(os.path.join(args.out, "a15_maps.parquet"))
print("wrote", os.path.join(args.out, "a15_maps.parquet"))
