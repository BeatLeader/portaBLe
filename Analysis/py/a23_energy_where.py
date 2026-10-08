"""A23: does the energy-bar pass model predict *where* players fail? And the One Saber factor.

For maps with an observed fail hazard (a19), run the energy-bar DP at the map's own pass threshold theta50 (a player who clears it
half the time) and record where the probability mass dies; predicted hazard per ~5 s section = mass dying there / mass alive at its
start. Compared within map (Spearman) with the observed hazard, next to the analyzer's swing difficulty (the a19 baseline).
Also fits the One Saber factor: the energy model has no one-handed nerf, so maps whose other hand has no swings get theta50 + log f.

Usage: python a23_energy_where.py --swings <swings_prod.csv.gz> --pass-maps <a19_pass_maps.parquet> --hazard <a19_hazard_windows.parquet>
       --a21 <a21_results.json> --a22 <a22_energy_maps.parquet> --maps-db <maps.parquet> [--maps 1200]
"""
import argparse, json
import numpy as np, pandas as pd

ap = argparse.ArgumentParser()
for a in ["swings", "pass_maps", "hazard", "a21", "a22", "maps_db"]: ap.add_argument(f"--{a.replace('_', '-')}", dest=a, required=True)
ap.add_argument("--maps", type=int, default=1200); ap.add_argument("--slope", type=float, default=2.5)
args = ap.parse_args()
A = args.slope
E = pd.read_parquet(args.a22).set_index("lb_id")
mode = pd.read_parquet(args.maps_db, columns=["Id", "ModeName"]).set_index("Id").ModeName
b = E.b; t50 = np.log(E.energy_rating)
one = (mode.reindex(E.index) == "OneSaber").values
c = np.polyfit(t50[~one], b[~one], 3)
best = min(((f, abs(np.mean(b[one] - np.polyval(c, t50[one] + np.log(f))))) for f in np.linspace(0.3, 1.0, 71)), key=lambda x: x[1])
t_adj = t50 + np.where(one, np.log(best[0]), 0)
c2 = np.polyfit(t_adj, b, 3); e2 = b - np.polyval(c2, t_adj)
print(f"One Saber factor {best[0]:.2f}: R2 {1 - e2.var() / b.var():.4f}; One Saber mean residual {e2[one].mean():+.2f}; maps off > 1 logit {(np.abs(e2) > 1).sum()}, > 2 {(np.abs(e2) > 2).sum()}")

# ---- where: DP with absorbed mass per note, at theta50 of each map
H = pd.read_parquet(args.hazard)
cand = H.groupby("lb_id").fails.sum(); cand = cand[cand >= 100].index.intersection(E.index)
rng = np.random.default_rng(0); pick = np.sort(rng.choice(np.array(cand), size=min(args.maps, len(cand)), replace=False))
S = pd.read_csv(args.swings, usecols=["lb_id", "seconds", "hand", "x", "cut_direction", "n_cubes", "parity_error", "stress", "swing_speed",
                                      "low_speed_falloff", "njs_buff", "wall_buff", "is_stream"], dtype={"lb_id": str})
S = S[S.lb_id.isin(pick)].sort_values(["lb_id", "seconds", "hand"], kind="stable").reset_index(drop=True)
k, cc, ch, cd, cp = json.load(open(args.a21))["swing"]["swing_params"]
cross = (((S.hand == 0) & (S.x == 3)) | ((S.hand == 1) & (S.x == 0))).values
sm = 2.0 * k * S.stress.values / (k * S.stress.values + 2.0) + 1.0
d = (S.swing_speed * S.low_speed_falloff).values * sm * (S.njs_buff * np.where(S.is_stream == 1, 1.05, 1.0) * S.wall_buff).values \
    * (1 + cc * cross) * (1 + ch * S.cut_direction.isin([2, 3]).values) * (1 + cd * S.cut_direction.isin([4, 5, 6, 7]).values) * (1 + cp * S.parity_error.values)
geo = H.drop_duplicates("lb_id").set_index("lb_id")
rows = []
for lb, g in S.groupby("lb_id", sort=False):
    gi = g.index.values; n = g.n_cubes.clip(1, 4).values
    ld = np.repeat(np.log(np.maximum(d[gi], 1e-3)), n); ts = np.repeat(g.seconds.values, n)
    th = t50.get(lb, np.nan)
    if not np.isfinite(th): continue
    p = 1 / (1 + np.exp(-A * (ld - th)))
    Ev = np.zeros(101); Ev[50] = 1.0; dead = np.zeros(len(ld))
    for j in range(len(ld)):
        hit, miss = Ev * (1 - p[j]), Ev * p[j]
        N = np.zeros(101); N[1:] = hit[:-1]; N[100] += hit[100]
        N[1:86] += miss[16:]; dead[j] = miss[1:16].sum()
        Ev = N
    alive_before = 1 - np.r_[0, np.cumsum(dead)[:-1]]
    # sections as in a19 (t0 = first swing, width = max(5 s, span / 60))
    t0 = g.seconds.min(); width = max(5.0, max(g.seconds.max() - t0, 1) / 60)
    w = np.floor((ts - t0) / width).astype(int)
    dw = np.bincount(w, weights=dead); first = pd.Series(alive_before).groupby(w).first()
    sec = pd.DataFrame({"win": np.arange(len(dw)), "pred_hazard": dw / np.maximum(first.reindex(np.arange(len(dw))).values, 1e-9)})
    obs = H[H.lb_id == lb][["win", "hazard", "at_risk", "sdiff"]]
    j = obs.merge(sec, on="win"); j = j[j.at_risk >= 100]
    if len(j) < 8: continue
    rows.append({"lb_id": lb, "energy": j.hazard.corr(j.pred_hazard, method="spearman"), "swing_diff": j.hazard.corr(j.sdiff, method="spearman")})
W = pd.DataFrame(rows)
print(f"where players fail, {len(W)} maps (median within-map Spearman with the observed fail hazard): energy model {W.energy.median():.3f}, "
      f"analyzer swing difficulty {W.swing_diff.median():.3f}; energy better on {np.mean(W.energy > W.swing_diff):.0%} of maps")
