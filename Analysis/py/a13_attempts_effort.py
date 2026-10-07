"""A13: attempts data (/admin/attemptsexport) - coverage, effort behind each best score, and whether unequal effort biases
score-implied map difficulty.

Usage: python a13_attempts_effort.py --data <dir with scores.parquet, maps.parquet and attempts/*.parquet> [--out Analysis/out]

1. Coverage: attempts by type and year; which player-map histories are complete (the first tracked attempt is no later than
   the player's best score, on maps whose first score falls inside the tracked period).
2. Effort per best score (complete histories, standard plays only: no practice start, speed 1): attempts and playtime before
   the best, tries to the first clear (struggling to pass), clears after the first clear (grinding), and luck (best vs the
   player's median clear, in log error-rate units).
3. Effort bias: latent model log(1 - acc) = d_map - skill on (a) best scores (today's target), (b) each player's FIRST clear
   (fixed effort), (c) best scores with an effort term -beta * log(clears before the best). Map-level differences between
   them are what "all scores" hides; compared with the algorithm's fit and with burstiness.
"""
import argparse, glob, json, os, sys
import numpy as np, pandas as pd, pyarrow.parquet as pq
sys.path.insert(0, os.path.dirname(__file__))
import data, latent

ap = argparse.ArgumentParser()
ap.add_argument("--data", required=True)
ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "..", "out"))
ap.add_argument("--fit", default=os.path.join(os.path.dirname(__file__), "..", "out", "fit_acc_model_maps.csv"))
args = ap.parse_args()
data.DATA = args.data
res = {}
CLEAR, FAIL, RESTART, QUIT, PRACTICE = 1, 2, 3, 4, 5

# ---------------- load (only the columns needed; strings as categories)
cols = ["Id", "PlayerId", "LeaderboardId", "Type", "Timeset", "Time", "StartTime", "Speed", "Accuracy", "AttemptsCount", "ScoreId", "Modifiers"]
parts = []
for f in sorted(glob.glob(os.path.join(args.data, "attempts", "attempts-*.parquet"))):
    t = pq.read_table(f, columns=cols).to_pandas()
    for c in ("PlayerId", "LeaderboardId", "Modifiers"):
        t[c] = t[c].astype("category")
    parts.append(t)
A = pd.concat(parts, ignore_index=True)
for c in ("PlayerId", "LeaderboardId", "Modifiers"):
    A[c] = A[c].astype(str).astype("category")
A["Type"] = A.Type.astype("int8"); A["Time"] = A.Time.astype("float32"); A["Accuracy"] = A.Accuracy.astype("float32")
del parts
maps = data.load_maps(); maps["lb_id"] = maps.lb_id.astype(str)
scores = pd.read_parquet(data.p("scores.parquet")).rename(columns={"LeaderboardId": "lb_id", "PlayerId": "player", "Accuracy": "acc"})
scores["lb_id"] = scores.lb_id.astype(str); scores["player"] = scores.player.astype(str); scores["Modifiers"] = scores.Modifiers.fillna("")

# ---------------- 1. coverage
A["year"] = pd.to_datetime(A.Timeset, unit="s").dt.year
types = {0: "unknown", 1: "clear", 2: "fail", 3: "restart", 4: "quit", 5: "practice"}
res["attempts"] = int(len(A)); res["players"] = int(A.PlayerId.nunique()); res["leaderboards"] = int(A.LeaderboardId.nunique())
res["by_type"] = {types.get(int(k), str(k)): int(v) for k, v in A.Type.value_counts().items()}
res["by_year"] = {int(k): int(v) for k, v in A.year.value_counts().sort_index().items()}
res["first_attempt"] = str(pd.to_datetime(A.Timeset.min(), unit="s")); res["last_attempt"] = str(pd.to_datetime(A.Timeset.max(), unit="s"))
print(f"attempts {len(A):,} | players {res['players']:,} | leaderboards {res['leaderboards']:,} | {res['first_attempt']} .. {res['last_attempt']}")
print("by type:", res["by_type"]); print("by year:", res["by_year"])
mods = A.Modifiers.astype(str)
standard = (A.StartTime.fillna(0) <= 0) & ((A.Speed.fillna(0) == 0) | (A.Speed == 1)) & (A.Type != PRACTICE)
print(f"standard plays (no practice start/speed): {standard.mean():.1%}")
# tracking start: first month with at least 10 % of the median monthly volume
month = pd.to_datetime(A.Timeset, unit="s").dt.to_period("M")
vol = month.value_counts().sort_index()
t0 = vol.index[vol >= 0.1 * vol.median()][0].to_timestamp()
res["tracking_start"] = str(t0.date()); print("tracking effectively from", t0.date())

S = A[standard].copy()
S["clean"] = S.Modifiers.astype(str).isin(["", "IF", "BE", "IF,BE", "BE,IF"])
key = ["PlayerId", "LeaderboardId"]
g = S.groupby(key, observed=True)
pair = pd.DataFrame({"first_ts": g.Timeset.min(), "n_att": g.size(),
                     "n_clear": (S.Type == CLEAR).groupby([S.PlayerId, S.LeaderboardId], observed=True).sum()})
pair = pair.reset_index().rename(columns={"PlayerId": "player", "LeaderboardId": "lb_id"})
pair["player"] = pair.player.astype(str); pair["lb_id"] = pair.lb_id.astype(str)
best = scores[["player", "lb_id", "Id", "acc", "Timepost", "Modifiers"]].rename(columns={"Id": "score_id", "acc": "best_acc"})
first_score = scores.groupby("lb_id").Timepost.min().rename("map_first_score")
pair = pair.merge(best, on=["player", "lb_id"], how="inner").merge(first_score, left_on="lb_id", right_index=True)
t0s = int(t0.timestamp())
pair["complete"] = (pair.first_ts <= pair.Timepost) & (pair.map_first_score >= t0s)
res["pairs_with_best"] = int(len(pair)); res["pairs_complete"] = int(pair.complete.sum())
res["maps_complete"] = int(pair[pair.complete].lb_id.nunique())
print(f"player-map pairs with a best score and tracked attempts: {len(pair):,}; complete histories: {pair.complete.sum():,} on {res['maps_complete']} maps")

# ---------------- 2. effort per best score (complete histories)
C = S[S.LeaderboardId.astype(str).isin(set(pair[pair.complete].lb_id))].copy()
C["player"] = C.PlayerId.astype(str); C["lb_id"] = C.LeaderboardId.astype(str)
C = C.merge(pair.loc[pair.complete, ["player", "lb_id", "score_id", "best_acc", "Timepost"]], on=["player", "lb_id"], how="inner")
C = C.sort_values(["player", "lb_id", "Timeset", "Id"])
C["is_best"] = C.ScoreId == C.score_id
C["seq"] = C.groupby(["player", "lb_id"]).cumcount() + 1
C["clear_seq"] = (C.Type == CLEAR).astype(int).groupby([C.player, C.lb_id]).cumsum()
bestrow = C[C.is_best].drop_duplicates(["player", "lb_id"], keep="first").set_index(["player", "lb_id"])
first_clear = C[C.Type == CLEAR].groupby(["player", "lb_id"]).agg(first_clear_seq=("seq", "min"), first_clear_acc=("Accuracy", "first"),
                                                                  first_clear_clean=("clean", "first"))
C = C.join(bestrow.seq.rename("best_seq"), on=["player", "lb_id"])
playtime = C[C.seq <= C.best_seq].groupby(["player", "lb_id"]).Time.sum().rename("playtime_to_best")
med_clear = C[C.Type == CLEAR].groupby(["player", "lb_id"]).Accuracy.median().rename("median_clear_acc")
E = bestrow[["seq", "clear_seq", "best_acc", "clean"]].rename(columns={"seq": "attempts_to_best", "clear_seq": "clears_to_best", "clean": "best_clean"}) \
    .join(first_clear).join(playtime).join(med_clear)
E = E[E.first_clear_seq.notna()]
E["clears_after_first"] = E.clears_to_best - 1
le = lambda x: np.log(np.clip(1 - np.asarray(x, float), 5e-4, 1))
E["luck"] = le(E.median_clear_acc) - le(E.best_acc)          # > 0: best is better than the player's typical clear
E["gain_first_to_best"] = le(E.first_clear_acc) - le(E.best_acc)
q = lambda s: {p: round(float(s.quantile(p)), 3) for p in (0.1, 0.25, 0.5, 0.75, 0.9)}
res["effort"] = {"pairs": int(len(E)), "attempts_to_best": q(E.attempts_to_best), "clears_to_best": q(E.clears_to_best),
                 "tries_to_first_clear": q(E.first_clear_seq), "playtime_to_best_min": q(E.playtime_to_best / 60),
                 "luck_log_err": q(E.luck), "gain_first_clear_to_best_log_err": q(E.gain_first_to_best)}
print(json.dumps(res["effort"], indent=1))

# ---------------- 3. effort bias in score-implied difficulty
E = E.reset_index()
E.to_parquet(os.path.join(args.data, "attempts_effort_pairs.parquet"))   # per player-map effort table, reused by a14
E = E.merge(maps[["lb_id", "mode"]], on="lb_id")
E = E[(E["mode"] == "Standard") & E.best_clean & E.first_clear_clean.astype(bool)]
def fit(df, accol, extra=None):
    d, players, mids = latent.prepare(df.rename(columns={accol: "acc"}), 10, 30)
    z = latent.err_transform(d.acc.values)
    if extra is not None:
        z = z + d[extra].values                               # fold a fixed effort adjustment into the target
    a, dj = latent.fit_additive(d.pi.values, d.mi.values, z, len(players), len(mids))
    return pd.Series(dj, index=mids), pd.Series(a, index=players), d
d_best, a_best, Db = fit(E, "best_acc")
d_first, a_first, Df = fit(E, "first_clear_acc")
# effort elasticity: within player and map, how much does log(clears before the best) lower the error rate?
Db["res"] = latent.err_transform(Db.acc.values) - (d_best.reindex(Db.lb_id).to_numpy() - a_best.reindex(Db.player).to_numpy())
Db["lc"] = np.log(Db.clears_to_best.clip(lower=1))
lc_c = Db.lc - Db.groupby("player").lc.transform("mean")
beta = float(-(Db.res * lc_c).sum() / (lc_c ** 2).sum())
Db["adj"] = beta * Db.lc                                      # add back what extra clears bought
_, dj_adj = latent.fit_additive(Db.pi.values, Db.mi.values, latent.err_transform(Db.acc.values) + Db.adj.values,
                                int(Db.pi.max()) + 1, int(Db.mi.max()) + 1)
d_adj = pd.Series(dj_adj, index=d_best.index)
M = pd.DataFrame({"d_best": d_best, "d_first": d_first, "d_effort_adj": d_adj}).dropna()
M["effort_intensity"] = Db.groupby("lb_id").lc.mean()
M["n"] = Db.groupby("lb_id").size()
for c in ("d_first", "d_effort_adj"):
    M[c] = M[c] - M[c].mean() + M.d_best.mean()
res["effort_bias"] = {"maps": int(len(M)), "beta_per_log_clear": round(beta, 4),
                      "corr_best_vs_first": round(float(M.d_best.corr(M.d_first)), 4),
                      "corr_best_vs_effort_adj": round(float(M.d_best.corr(M.d_effort_adj)), 4),
                      "sd_d_best": round(float(M.d_best.std()), 3),
                      "sd_best_minus_first": round(float((M.d_best - M.d_first).std()), 3),
                      "sd_best_minus_adj": round(float((M.d_best - M.d_effort_adj).std()), 3),
                      "effort_intensity_sd": round(float(M.effort_intensity.std()), 3)}
fitm = pd.read_csv(args.fit, dtype={"lb_id": str}).set_index("lb_id")
M = M.join(fitm[["d_hat_cv"]])
def r2(y, x):
    k = pd.concat([y, x], axis=1).dropna(); b = np.polyfit(k.iloc[:, 1], k.iloc[:, 0], 1)
    return float(1 - np.var(k.iloc[:, 0] - np.polyval(b, k.iloc[:, 1])) / np.var(k.iloc[:, 0]))
res["effort_bias"]["algo_r2_on_best"] = round(r2(M.d_best, M.d_hat_cv), 3)
res["effort_bias"]["algo_r2_on_first_clear"] = round(r2(M.d_first, M.d_hat_cv), 3)
res["effort_bias"]["algo_r2_on_effort_adj"] = round(r2(M.d_effort_adj, M.d_hat_cv), 3)
burst_path = os.path.join(args.data, "..", "burst_maps.csv")
if os.path.exists(burst_path):
    b = pd.read_csv(burst_path, index_col=0); b.index = b.index.astype(str)
    M = M.join(b[["burst"]])
    res["effort_bias"]["corr_burst_vs_best_minus_first"] = round(float(M.burst.corr(M.d_best - M.d_first)), 3)
    res["effort_bias"]["corr_burst_vs_effort_intensity"] = round(float(M.burst.corr(M.effort_intensity)), 3)
print(json.dumps(res["effort_bias"], indent=1))
os.makedirs(args.out, exist_ok=True)
M.to_csv(os.path.join(args.out, "a13_maps.csv"))
E.describe().to_csv(os.path.join(args.out, "a13_effort_summary.csv"))
json.dump(res, open(os.path.join(args.out, "a13.json"), "w"), indent=1)
print("wrote", os.path.join(args.out, "a13.json"))
