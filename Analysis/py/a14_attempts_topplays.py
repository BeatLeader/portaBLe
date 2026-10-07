"""A14: effort behind top plays, grind per map, and pass difficulty measured as tries to the first clear.

Usage: python a14_attempts_topplays.py --data <dir with attempts_effort_pairs.parquet (a13)> --db wwwroot/test-ml.db
                                       [--burst <burst_maps.csv>] [--out Analysis/out]

A. Grinding and luck behind scores that carry PP: complete-history scores split by player tier (rank in --db) and by the score's
   weight in the player's list (top plays: weight >= 0.8, i.e. the player's ~6 best; filler: weight < 0.2).
B. Grind per map among top-1000 players vs Megametric, stars and burstiness.
C. Pass difficulty: log(tries to the first clear) vs pass rating per skill tier, and whether burst maps need more or fewer tries
   than their pass rating says.
"""
import argparse, json, os, sqlite3
import numpy as np, pandas as pd, numpy.linalg as la

ap = argparse.ArgumentParser()
ap.add_argument("--data", required=True)
ap.add_argument("--db", default="wwwroot/test-ml.db")
ap.add_argument("--burst", default=None)
ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "..", "out"))
args = ap.parse_args()
pd.set_option("display.width", 250)
res = {}

E = pd.read_parquet(os.path.join(args.data, "attempts_effort_pairs.parquet"))
c = sqlite3.connect(args.db)
S = pd.read_sql("select PlayerId player, LeaderboardId lb_id, Pp, Weight from Scores where Pp > 0", c)
P = pd.read_sql("select Id player, Rank from Players where Pp > 0", c)
L = pd.read_sql("select Id lb_id, Name, DifficultyName diff, Stars, PassRating, AccRating, TechRating, Megametric, Count from Leaderboards", c)
E = E.merge(S, on=["player", "lb_id"], how="inner").merge(P, on="player", how="inner").merge(L, on="lb_id", how="inner")
E["tier"] = pd.cut(E.Rank, [0, 100, 1000, 10000, 50000, 10**9], labels=["1-100", "101-1k", "1k-10k", "10k-50k", "50k+"])
E["role"] = np.select([E.Weight >= 0.8, E.Weight < 0.2], ["top play", "filler"], "middle")
E["log_clears"] = np.log(E.clears_to_best.clip(lower=1))
print(f"complete-history scores matched to {args.db}: {len(E):,}")

# ---------------- A. effort behind top plays
def summary(g):
    return pd.Series({"scores": len(g), "attempts_p50": g.attempts_to_best.median(), "attempts_p90": g.attempts_to_best.quantile(.9),
                      "clears_p50": g.clears_to_best.median(), "clears_p90": g.clears_to_best.quantile(.9),
                      "minutes_p50": g.playtime_to_best.median() / 60, "minutes_p90": g.playtime_to_best.quantile(.9) / 60,
                      "best_is_first_clear": (g.clears_to_best == 1).mean(), "luck_p90": g.luck.quantile(.9),
                      "gain_p90": g.gain_first_to_best.quantile(.9)})
A = E[E.role != "middle"].groupby(["tier", "role"], observed=True).apply(summary)
print("\nA. effort behind the best score, by player tier and role of the score in the player's list"); print(A.round(2).to_string())
res["top_plays"] = A.round(3).reset_index().to_dict(orient="records")

# ---------------- B. grind per map among top-1000 players
T = E[E.Rank <= 1000]
G = T.groupby("lb_id").agg(top_scores=("player", "size"), grind=("log_clears", "mean"), luck=("luck", "mean"), gain=("gain_first_to_best", "mean"))
G = G[G.top_scores >= 10].join(L.set_index("lb_id"))
if args.burst:
    b = pd.read_csv(args.burst, index_col=0); b.index = b.index.astype(str); G = G.join(b[["burst"]])
def ols(df, y, xs):
    d = df.dropna(subset=[y] + xs)
    X = np.column_stack([np.ones(len(d))] + [d[x].to_numpy(float) for x in xs]); Y = d[y].to_numpy(float)
    beta, *_ = la.lstsq(X, Y, rcond=None); r = Y - X @ beta
    se = np.sqrt(np.diag(np.sum(r ** 2) / (len(Y) - X.shape[1]) * la.inv(X.T @ X)))
    return {x: (round(float(bb), 4), round(float(ss), 4)) for x, bb, ss in zip(xs, beta[1:], se[1:])}, len(d)
G["stars2"] = G.Stars ** 2
res["grind_maps"] = {"maps": int(len(G)), "corr_grind_megametric": round(float(G.grind.corr(G.Megametric)), 3),
                     "megametric_on_grind_given_stars": ols(G, "Megametric", ["Stars", "stars2", "grind"])[0]["grind"]}
if "burst" in G:
    res["grind_maps"]["corr_grind_burst"] = round(float(G.grind.corr(G.burst)), 3)
print("\nB.", json.dumps(res["grind_maps"]))
print("most grinded maps among top-1000 players (mean log clears before the best):")
print(G.sort_values("grind", ascending=False).head(12)[["Name", "diff", "Stars", "top_scores", "grind", "luck", "gain", "Megametric"]].round(3).to_string())

# ---------------- C. pass difficulty: tries to the first clear
E["log_tries"] = np.log(E.first_clear_seq.clip(lower=1))
rows = []
for tier, g in E.groupby("tier", observed=True):
    g = g.assign(pass2=g.PassRating ** 2)
    if args.burst:
        g = g.join(b[["burst"]], on="lb_id")
        coef, n = ols(g, "log_tries", ["PassRating", "pass2", "burst"])
        sdb = float(b.burst.std())
        rows.append({"tier": tier, "pairs": n, "corr_tries_pass": round(float(g.log_tries.corr(g.PassRating)), 3),
                     "burst_effect_per_sd": round(coef["burst"][0] * sdb, 4), "se": round(coef["burst"][1] * sdb, 4),
                     "mean_tries": round(float(g.first_clear_seq.mean()), 2)})
C = pd.DataFrame(rows)
print("\nC. log(tries to the first clear) ~ pass rating (+ burstiness), per tier"); print(C.to_string(index=False))
res["pass_tries"] = C.to_dict(orient="records")
E["pass_bin"] = pd.cut(E.PassRating, [0, 2, 4, 6, 8, 10, 13, 20])
tab = E.groupby(["pass_bin", "tier"], observed=True).first_clear_seq.mean().unstack().round(2)
print("\nmean tries to the first clear, by pass rating (rows) and player tier (columns)"); print(tab.to_string())
res["tries_by_pass_and_tier"] = {str(k): v for k, v in tab.to_dict(orient="index").items()}
os.makedirs(args.out, exist_ok=True)
G.to_csv(os.path.join(args.out, "a14_grind_maps.csv"))
json.dump(res, open(os.path.join(args.out, "a14.json"), "w"), indent=1, default=str)
print("wrote", os.path.join(args.out, "a14.json"))
