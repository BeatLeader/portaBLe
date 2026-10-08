"""A19: pass difficulty measured from attempts (fails vs clears) — against pass rating, and where in a map players fail.

1. Rasch-type model on clean attempts (no modifiers; clears and fails): logit P(fail) = b_j - theta_i per attempt, fitted on per
   (player, map) counts with weak N(0, 2^2) priors. b_j is the empirical pass difficulty; compared with the analyzer's pass rating
   (smooth fit), and the residual ("pass disagreement") diagnosed with map features (song-grouped CV ridge, a15-style).
2. Fail hazard along the map: per ~5 s section (same windows as the acc-loss profiles), fails / attempts still alive there (quits and
   restarts count as alive until they end). Within-map rank correlation with the analyzer's swing difficulty and with the acc-loss
   model's misses component; pooled: does the hazard rise with minutes into the map at equal local difficulty (endurance)?

Usage: python a19_pass_attempts.py --attempts <dir> --features <features_feat2.csv> --maps-db <maps.parquet> --swings <a17_swings.parquet>
       --profiles-db <portaBLe DB with AccLossProfiles> [--burst burst_maps.csv] [--mapfile a15_mapfile_features.parquet] --out <dir>
"""
import argparse, glob, json, os, sqlite3
import numpy as np, pandas as pd, pyarrow.parquet as pq
from sklearn.linear_model import RidgeCV
from sklearn.model_selection import GroupKFold
from sklearn.preprocessing import StandardScaler
from sklearn.pipeline import make_pipeline

ap = argparse.ArgumentParser()
for a in ["attempts", "features", "maps_db", "swings", "profiles_db", "out"]: ap.add_argument(f"--{a.replace('_', '-')}", dest=a, required=True)
ap.add_argument("--burst", default=None); ap.add_argument("--mapfile", default=None)
args = ap.parse_args()
os.makedirs(args.out, exist_ok=True)
CLEAR, FAIL, RESTART, QUIT, PRACTICE = 1, 2, 3, 4, 5

# ------------------------------------------------------------------ attempts (clean, not practice)
cache = os.path.join(args.out, "a19_attempts.parquet")
if os.path.exists(cache): A = pd.read_parquet(cache)
else:
    parts = []
    for f in sorted(glob.glob(os.path.join(args.attempts, "attempts-*.parquet"))):
        t = pq.read_table(f, columns=["PlayerId", "LeaderboardId", "Type", "Modifiers", "Time", "Timeset"]).to_pandas()
        t = t[t.Type.isin([CLEAR, FAIL, RESTART, QUIT]) & (t.Modifiers.fillna("") == "")].drop(columns="Modifiers")
        parts.append(t)
    A = pd.concat(parts, ignore_index=True)
    A["PlayerId"] = A.PlayerId.astype("category"); A["LeaderboardId"] = A.LeaderboardId.astype("category")
    A.to_parquet(cache)
print(f"{len(A):,} clean attempts (clear/fail/restart/quit): " + ", ".join(f"{n} {(A.Type == t).mean():.1%}" for n, t in
      [("clear", CLEAR), ("fail", FAIL), ("restart", RESTART), ("quit", QUIT)]))

# ------------------------------------------------------------------ 1. Rasch pass difficulty
CF = A[A.Type.isin([CLEAR, FAIL])]
P = CF.groupby(["PlayerId", "LeaderboardId"], observed=True).Type.agg(n="size", f=lambda v: (v == FAIL).sum()).reset_index()
pa_ = P.groupby("PlayerId", observed=True).n.transform("sum"); mp_ = P.groupby("LeaderboardId", observed=True).n.transform("sum")
P = P[(pa_ >= 20) & (mp_ >= 50)]
pi, pids = pd.factorize(P.PlayerId); mi, mids = pd.factorize(P.LeaderboardId)
n, f = P.n.values.astype(float), P.f.values.astype(float)
theta = np.zeros(len(pids)); b = np.zeros(len(mids))
rate = np.bincount(mi, weights=f) / np.bincount(mi, weights=n); b = np.log(np.clip(rate, 1e-3, 1 - 1e-3) / (1 - np.clip(rate, 1e-3, 1 - 1e-3)))
for it in range(60):
    p = 1 / (1 + np.exp(-(b[mi] - theta[pi]))); w = n * p * (1 - p)
    theta += (np.bincount(pi, weights=n * p - f) - theta / 4) / (np.bincount(pi, weights=w) + 1 / 4)
    theta -= theta.mean()
    p = 1 / (1 + np.exp(-(b[mi] - theta[pi]))); w = n * p * (1 - p)
    b += (np.bincount(mi, weights=f - n * p) - (b - b.mean()) / 4) / (np.bincount(mi, weights=w) + 1 / 4)
se_b = 1 / np.sqrt(np.bincount(mi, weights=n * p * (1 - p)) + 1 / 4)
B = pd.DataFrame({"lb_id": mids.astype(str), "b": b, "se_b": se_b, "attempts": np.bincount(mi, weights=n), "fails": np.bincount(mi, weights=f),
                  "players": np.bincount(mi)})
print(f"pass model: {len(P):,} player-map pairs, {len(pids):,} players, {len(mids):,} maps; b SD {B.b.std():.3f}, median SE {B.se_b.median():.3f}")

F = pd.read_csv(args.features, dtype={"lb_id": str}); F = F[F["mod"] == "none"].drop(columns="mod")
feat = [c for c in F.columns if c != "lb_id"]
DM = pd.read_parquet(args.maps_db)[["Id", "Name", "DifficultyName", "Hash", "PassRating"]].rename(columns={"Id": "lb_id"})
M = B.merge(F, on="lb_id").merge(DM, on="lb_id")
M = M[(M.fails >= 30) & (M.attempts >= 200)].reset_index(drop=True)
reliab = 1 - (M.se_b ** 2).mean() / M.b.var()
c1 = np.polyfit(M["pass"], M.b, 3); M["b_from_pass"] = np.polyval(c1, M["pass"])
r2_pass = 1 - np.var(M.b - M.b_from_pass) / np.var(M.b)
print(f"{len(M)} maps with >= 200 clean clear/fail attempts and >= 30 fails; reliability of b {reliab:.3f}")
print(f"analyzer pass rating (cubic) explains R2 {r2_pass:.3f} of empirical pass difficulty (production PassRating: "
      f"{1 - np.var(M.b - np.polyval(np.polyfit(M.PassRating, M.b, 3), M.PassRating)) / np.var(M.b):.3f})")
groups = M.Hash.str.upper().values
def cv(cols, y):
    X = M[cols].values; t = M[y].values; p = np.zeros(len(t))
    for a, bb in GroupKFold(5).split(X, t, groups):
        p[bb] = make_pipeline(StandardScaler(), RidgeCV(alphas=np.logspace(-2, 4, 25))).fit(X[a], t[a]).predict(X[bb])
    return 1 - np.var(t - p) / np.var(t), t - p
r2_all, _ = cv(feat, "b")
print(f"ridge on all {len(feat)} acc features (incl. pass, tech): CV R2 {r2_all:.3f} — what the analyzer's map data can say about passing")
M["e_pass"] = M.b - M.b_from_pass
# diagnose the pass disagreement
extra = {}
if args.burst:
    bu = pd.read_csv(args.burst, index_col=0).burst; bu.index = bu.index.astype(str); M["burst"] = M.lb_id.map(bu); extra["burst"] = "burstiness (peak / median swing difficulty)"
if args.mapfile:
    mf = pd.read_parquet(args.mapfile)[["lb_id", "bombs_per_s"]]                     # JD, reaction time, walls are in the 87 features
    M = M.merge(mf, on="lb_id", how="left"); extra["bombs_per_s"] = "bombs per second"
M["duration_min"] = np.exp(M.log_len) / 60; extra["duration_min"] = "map length (min)"
M["sustain"] = M.swing_diff_peak128 / M.swing_diff_peak8.clip(lower=1e-6); extra["sustain"] = "sustained / peak difficulty (128 vs 8 swings)"
print("\npass disagreement (empirical difficulty - what pass rating implies), correlations:")
for c, lab in list(extra.items()) + [(c, c) for c in ["swing_diff_peak8", "swing_diff_peak32", "swing_diff_peak128", "swing_diff_mean", "njs_mean",
                                                      "frac_bomb", "frac_cross", "frac_horizontal", "tech", "log_swings", "note_density", "jump_distance", "reaction_time", "walls_per_s", "wall_cover"] if c in M]:
    v = M[[c, "e_pass"]].replace([np.inf, -np.inf], np.nan).dropna()
    print(f"  {lab:48s} r {np.corrcoef(v[c], v.e_pass)[0, 1]:+.3f}")
for c in extra: M[c] = M[c].replace([np.inf, -np.inf], np.nan).fillna(M[c].median())
r2_e, _ = cv(feat + list(extra), "e_pass")
print(f"all features predict the pass disagreement out of fold: R2 {r2_e:.3f}")
pd.set_option("display.width", 220)
show = M.sort_values("e_pass")
cols = ["Name", "DifficultyName", "pass", "b", "e_pass", "attempts", "fails", "duration_min"] + (["burst"] if "burst" in M else [])
print("\npassing is EASIER than the pass rating says:"); print(show.head(12)[cols].assign(Name=lambda x: x.Name.str[:26]).round(3).to_string(index=False))
print("passing is HARDER than the pass rating says:"); print(show.tail(12)[cols].assign(Name=lambda x: x.Name.str[:26]).round(3).to_string(index=False))
M.to_parquet(os.path.join(args.out, "a19_pass_maps.parquet"))

# ------------------------------------------------------------------ 2. fail hazard along the map
S = pd.read_parquet(args.swings, columns=["lb_id", "seconds", "swing_diff", "n_cubes"])
g = S.groupby("lb_id")
geo = pd.DataFrame({"t0": g.seconds.min(), "t1": g.seconds.max()})
geo["width"] = np.maximum(5.0, (geo.t1 - geo.t0).clip(lower=1) / 60); geo["nw"] = (np.floor((geo.t1 - geo.t0) / geo.width) + 1).astype(int)
S = S.join(geo, on="lb_id"); S["win"] = np.floor((S.seconds - S.t0) / S.width).astype(int)
SW = S.groupby(["lb_id", "win"]).agg(sdiff=("swing_diff", "mean"), sdiff_max=("swing_diff", "max"), notes=("n_cubes", "sum")).reset_index()
con = sqlite3.connect(args.profiles_db)
prof = {lb: json.loads(js) for lb, js in con.execute("select LeaderboardId, Json from AccLossProfiles where LeaderboardId != '__model__'")}
rows = []
for lb, pj in prof.items():
    W = pj["windows"]
    for i in range(len(W["n"])):
        if W["n"][i] > 0: rows.append((lb, i, W["misses"][i] / W["n"][i], (W["precision"][i] + W["swing"][i] + W["misses"][i]) / W["n"][i]))
PW = pd.DataFrame(rows, columns=["lb_id", "win", "miss_pred", "loss_pred"])
H = A[A.LeaderboardId.astype(str).isin(geo.index)].copy(); H["lb_id"] = H.LeaderboardId.astype(str)
H = H.join(geo, on="lb_id")
H["end"] = np.where(H.Type == CLEAR, H.t1 + 1, H.Time)
H["ewin"] = np.clip(np.floor((H.end - H.t0) / H.width), -1, H.nw - 1).astype(int)          # -1: ended before the first note
H = H[H.ewin >= 0]
E = H.groupby(["lb_id", "ewin"]).Type.agg(ended="size", fails=lambda v: (v == FAIL).sum()).reset_index().rename(columns={"ewin": "win"})
full = geo.reset_index().rename(columns={"index": "lb_id"})
grid = pd.DataFrame({"lb_id": np.repeat(full.lb_id.values, full.nw.values), "win": np.concatenate([np.arange(k) for k in full.nw.values])})
G = grid.merge(E[["lb_id", "win", "ended", "fails"]], on=["lb_id", "win"], how="left").fillna({"ended": 0, "fails": 0})
G = G.sort_values(["lb_id", "win"])
G["at_risk"] = G.groupby("lb_id").ended.transform(lambda v: v[::-1].cumsum()[::-1])                # still playing at the window's start
G["hazard"] = G.fails / G.at_risk.clip(lower=1)
G = G.merge(SW, on=["lb_id", "win"], how="left").merge(PW, on=["lb_id", "win"], how="left").merge(geo[["width"]], left_on="lb_id", right_index=True)
G["minutes"] = G.win * G.width / 60
out = []
for lb, gg in G.groupby("lb_id"):
    gg = gg[(gg.at_risk >= 100) & gg.notes.gt(0)]
    if len(gg) < 8 or gg.fails.sum() < 100: continue
    out.append({"lb_id": lb, "fails": gg.fails.sum(), "windows": len(gg), "sdiff": gg.hazard.corr(gg.sdiff, method="spearman"),
                "sdiff_max": gg.hazard.corr(gg.sdiff_max, method="spearman"), "miss_pred": gg.hazard.corr(gg.miss_pred, method="spearman"),
                "top_window_share": gg.fails.max() / gg.fails.sum(), "minutes": gg.hazard.corr(gg.minutes, method="spearman")})
Q = pd.DataFrame(out)
print(f"\nwhere players fail: {len(Q)} maps with >= 100 clean fails in >= 8 sections; median within-map Spearman of the fail hazard with")
print(f"  analyzer swing difficulty (section mean) {Q.sdiff.median():.3f}, (section max) {Q.sdiff_max.median():.3f}, acc-loss model misses {Q.miss_pred.median():.3f}, "
      f"minutes into the map {Q.minutes.median():.3f}; the worst section holds a median {Q.top_window_share.median():.1%} of a map's fails")
# endurance: pooled log hazard ~ log local difficulty + minutes, within map
Z = G[(G.at_risk >= 100) & (G.fails > 0) & G.sdiff.gt(0) & G.miss_pred.gt(0)].copy()
Z["lh"] = np.log(Z.hazard); Z["ls"] = np.log(Z.sdiff); Z["lm"] = np.log(Z.miss_pred)
for c in ["lh", "ls", "lm", "minutes"]: Z[c + "_w"] = Z[c] - Z.groupby("lb_id")[c].transform("mean")
X = np.column_stack([Z.ls_w, Z.lm_w, Z.minutes_w]); bb, *_ = np.linalg.lstsq(X, Z.lh_w, rcond=None)
print(f"pooled within-map fit of log fail hazard: log swing difficulty {bb[0]:+.3f}, log predicted misses {bb[1]:+.3f}, per minute into the map {bb[2]:+.3f} "
      f"(x{np.exp(bb[2]):.2f} per minute at equal local difficulty)")
G.to_parquet(os.path.join(args.out, "a19_hazard_windows.parquet")); Q.to_parquet(os.path.join(args.out, "a19_hazard_maps.parquet"))
