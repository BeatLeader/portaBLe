"""Score-informed acc difficulty: z_ij = log(1-acc) = delta_j + c_j - s_i, delta_j = log(1 - predictedAcc_j) from the DB,
correction c_j ~ N(0, tau^2) (the algorithm's error size). Alternating least squares; maps without scores keep c_j = 0."""
import sys, sqlite3, numpy as np, pandas as pd
CLEAN = {"", "IF", "BE", "IF,BE", "BE,IF"}
def correct(db, tau=0.09, min_player_scores=15, iters=40, verbose=True):
    c = sqlite3.connect(db)
    lb = pd.read_sql("select Id lb_id, PredictedAcc pred from Leaderboards", c).set_index("lb_id")
    s = pd.read_sql("select PlayerId player, LeaderboardId lb_id, Accuracy acc, Modifiers mods from Scores", c)
    s = s[s.mods.fillna("").isin(CLEAN) & (s.acc > 0) & (s.acc <= 1)]
    s = s[s.player.map(s.player.value_counts()) >= min_player_scores]
    mi, mids = pd.factorize(s.lb_id); pi, pids = pd.factorize(s.player)
    z = np.log(np.clip(1 - s.acc.to_numpy(), 5e-4, 1))
    delta = np.log(1 - lb.pred.reindex(mids).to_numpy())
    nm = np.bincount(mi, minlength=len(mids)).astype(float); npl = np.bincount(pi, minlength=len(pids)).astype(float)
    cj = np.zeros(len(mids)); si = np.zeros(len(pids)); n0 = None
    for it in range(iters):
        si = np.bincount(pi, weights=delta[mi] + cj[mi] - z, minlength=len(pids)) / npl
        r = z - delta[mi] + si[pi]                                  # what the scores say beyond the rating
        if n0 is None or it % 10 == 0:
            var_e = float(np.var(r - np.bincount(mi, weights=r, minlength=len(mids))[mi] / nm[mi]))
            n0 = var_e / tau ** 2
        cj = np.bincount(mi, weights=r, minlength=len(mids)) / (nm + n0)
    if verbose:
        print(f"{len(s):,} clean scores, {len(pids):,} players, {len(mids):,} maps; per-score residual SD {var_e ** 0.5:.3f}, tau {tau} -> n0 = {n0:.1f} scores")
    out = pd.DataFrame({"correction": cj, "n": nm, "weight": nm / (nm + n0)}, index=mids)
    return out, pd.Series(si, index=pids)
if __name__ == "__main__":
    out, _ = correct(sys.argv[1], float(sys.argv[2]) if len(sys.argv) > 2 else 0.09)
    out.to_csv(sys.argv[3] if len(sys.argv) > 3 else "score_correction.csv")
    print(out.correction.describe().round(4).to_string())
