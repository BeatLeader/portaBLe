"""A20: how much a map rewards practice (learning curves from the attempts export), what that does to its scores, and whether the
map itself predicts it.

Learnability L_j: within each (player, map) with >= 3 clean clears, the slope of log error rate on log(clear number); per map the
mean over players, negated (larger = accuracy improves more per doubling of clears). Then:
  - against the acc model's disagreement e (a15): do learnable maps look easier than predicted because they get practised?
  - against Megametric (farm signal, from a portaBLe DB) at equal predicted difficulty
  - predictable from the map? repetition (share of 4-swing sequences that already appeared earlier in the map, by hand / lane /
    layer / cut direction), length, density, and the 87 acc features (song-grouped CV)

Usage: python a20_learning.py --attempts <dir> --maps <a15_maps.parquet> --swings <a17_swings.parquet> --features <features_feat2.csv> --db <portaBLe DB>
"""
import argparse, glob, os, sqlite3
import numpy as np, pandas as pd, pyarrow.parquet as pq
from sklearn.linear_model import RidgeCV
from sklearn.model_selection import GroupKFold
from sklearn.preprocessing import StandardScaler
from sklearn.pipeline import make_pipeline

ap = argparse.ArgumentParser()
for a in ["attempts", "maps", "swings", "features", "db"]: ap.add_argument(f"--{a}", required=True)
args = ap.parse_args()
out = os.path.dirname(args.maps)
cache = os.path.join(out, "a20_clears.parquet")
if os.path.exists(cache): C = pd.read_parquet(cache)
else:
    parts = []
    for f in sorted(glob.glob(os.path.join(args.attempts, "attempts-*.parquet"))):
        t = pq.read_table(f, columns=["PlayerId", "LeaderboardId", "Type", "Modifiers", "Accuracy", "Timeset"]).to_pandas()
        parts.append(t[(t.Type == 1) & (t.Modifiers.fillna("") == "") & (t.Accuracy > 0)].drop(columns=["Type", "Modifiers"]))
    C = pd.concat(parts, ignore_index=True); C.to_parquet(cache)
C = C.sort_values(["PlayerId", "LeaderboardId", "Timeset"], kind="stable")
C["k"] = C.groupby(["PlayerId", "LeaderboardId"]).cumcount() + 1
C["nk"] = C.groupby(["PlayerId", "LeaderboardId"]).k.transform("max")
C = C[C.nk >= 3].copy()
C["z"] = np.log(np.clip(1 - C.Accuracy, 5e-4, 1)); C["lk"] = np.log(C.k)
g = C.groupby(["PlayerId", "LeaderboardId"])
C["zc"] = C.z - g.z.transform("mean"); C["lkc"] = C.lk - g.lk.transform("mean")
pair = C.assign(xy=C.zc * C.lkc, xx=C.lkc ** 2).groupby(["PlayerId", "LeaderboardId"]).agg(xy=("xy", "sum"), xx=("xx", "sum"), n=("z", "size")).reset_index()
pair["slope"] = pair.xy / pair.xx
print(f"{len(pair):,} player-map pairs with >= 3 clean clears ({len(C):,} clears); median slope {pair.slope.median():+.3f} log error per log clear "
      f"(= x{np.exp(pair.slope.median() * np.log(2)):.3f} error per doubling)")
Lm = pair.groupby("LeaderboardId").agg(slope=("slope", "mean"), pairs=("slope", "size"), clears=("n", "sum")).reset_index().rename(columns={"LeaderboardId": "lb_id"})
Lm["learn"] = -Lm.slope
Lm = Lm[Lm.pairs >= 30]
# split-half reliability of the per-map learnability
half = pair.assign(h=pd.factorize(pair.PlayerId)[0] % 2).groupby(["LeaderboardId", "h"]).slope.mean().unstack()
half = half[pair.groupby("LeaderboardId").size().reindex(half.index) >= 60].dropna()
print(f"{len(Lm)} maps with >= 30 pairs; learnability split-half r {half[0].corr(half[1]):.3f}")

# repetition: share of 4-swing sequences (hand, lane, layer, cut) already seen earlier in the same map
S = pd.read_parquet(args.swings, columns=["lb_id", "seconds", "hand", "x", "y", "cut_direction"]).sort_values(["lb_id", "seconds"], kind="stable")
tok = (S.hand.astype(np.int64) * 1000 + S.x * 100 + S.y * 10 + S.cut_direction).values
rep = {}
for lb, idx in S.groupby("lb_id", sort=False).indices.items():
    t = tok[idx]
    if len(t) < 8: continue
    seq = t[:-3] * 10**12 + t[1:-2] * 10**8 + t[2:-1] * 10**4 + t[3:]
    _, first = np.unique(seq, return_index=True)
    rep[lb] = 1 - len(first) / len(seq)
R = pd.Series(rep, name="repetition")

M = pd.read_parquet(args.maps)[["lb_id", "name", "difficulty", "n", "d", "d_cv", "e", "hash"]].merge(Lm, on="lb_id").merge(R, left_on="lb_id", right_index=True, how="left")
mega = pd.read_sql("select Id lb_id, Megametric from Leaderboards", sqlite3.connect(args.db))
M = M.merge(mega, on="lb_id", how="left")
w = M.n >= 100
def partial(y, x, ctrl):
    X = np.column_stack([np.ones(len(M))] + [M[c] for c in ctrl])
    ry = M[y] - X @ np.linalg.lstsq(X, M[y], rcond=None)[0]; rx = M[x] - X @ np.linalg.lstsq(X, M[x], rcond=None)[0]
    return np.corrcoef(rx[w], ry[w])[0, 1]
print(f"learnability vs predicted difficulty d_cv r {np.corrcoef(M.learn[w], M.d_cv[w])[0, 1]:+.3f}")
print(f"learnability vs acc disagreement e, at equal predicted difficulty: partial r {partial('e', 'learn', ['d_cv']):+.3f}  "
      f"(negative = learnable maps look easier than predicted)")
print(f"learnability vs Megametric at equal predicted difficulty: partial r {partial('Megametric', 'learn', ['d_cv']):+.3f}")
print(f"repetition vs learnability r {np.corrcoef(M.repetition.fillna(M.repetition.median())[w], M.learn[w])[0, 1]:+.3f}; "
      f"repetition vs e (equal difficulty) partial r {partial('e', 'repetition', ['d_cv']) if M.repetition.notna().all() else np.nan:+.3f}")
F = pd.read_csv(args.features, dtype={"lb_id": str}); F = F[F["mod"] == "none"].drop(columns="mod")
feat = [c for c in F.columns if c != "lb_id"]
K = M.merge(F, on="lb_id").replace([np.inf, -np.inf], np.nan)
K["repetition"] = K.repetition.fillna(K.repetition.median())
def cvr2(cols, y):
    X = K[cols].fillna(0).values; t = K[y].values; p = np.zeros(len(t))
    for a, b in GroupKFold(5).split(X, t, K.hash.str.upper().values):
        p[b] = make_pipeline(StandardScaler(), RidgeCV(alphas=np.logspace(-2, 4, 25))).fit(X[a], t[a]).predict(X[b])
    return 1 - np.var(t - p) / np.var(t)
print(f"learnability predicted from the map (song-grouped CV R2): 87 features {cvr2(feat, 'learn'):.3f}; + repetition {cvr2(feat + ['repetition'], 'learn'):.3f}; "
      f"repetition alone {cvr2(['repetition'], 'learn'):.3f}")
print(f"acc disagreement e with repetition added to the 87 features: CV R2 of e {cvr2(feat + ['repetition'], 'e'):.4f} vs {cvr2(feat, 'e'):.4f} without")
pd.set_option("display.width", 220)
S2 = M[w].sort_values("learn")
print("\nleast learnable:"); print(S2.head(8)[["name", "difficulty", "learn", "pairs", "repetition", "e"]].assign(name=lambda x: x.name.str[:26]).round(3).to_string(index=False))
print("most learnable:"); print(S2.tail(8)[["name", "difficulty", "learn", "pairs", "repetition", "e"]].assign(name=lambda x: x.name.str[:26]).round(3).to_string(index=False))
M.to_parquet(os.path.join(out, "a20_learning_maps.parquet"))
