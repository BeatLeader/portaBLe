"""A10: do the SS / FS / SF rating variants track how much harder (easier) the map really is under a speed modifier?

Player skill a_i (and map difficulty d_j) come from the clean-score fit. For scores that used a speed modifier,
shift_ij = log(1-acc_ij) - (d_j - a_i) estimates the modifier's difficulty change on map j (plus an offset for self-selection:
people choose FS on maps they find easy). We compare per-map shifts with the shift implied by the ML predicted accuracy.
"""
import os, sys, json
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import data, latent

OUT = os.path.join(os.path.dirname(__file__), "..", "out")
tag = sys.argv[1] if len(sys.argv) > 1 else "prod"
sk = pd.read_csv(os.path.join(OUT, f"a01_player_skill_{tag}.csv"), dtype={"player": str}).set_index("player").skill
mp = pd.read_csv(os.path.join(OUT, f"a01_map_effects_{tag}.csv"), dtype={"lb_id": str}).set_index("lb_id")
R = pd.read_csv(data.p("ratings", f"ratings_{tag}.csv"), dtype={"lb_id": str})
s = pd.read_parquet(data.p("scores.parquet")).rename(columns={"LeaderboardId": "lb_id", "PlayerId": "player", "Accuracy": "acc"})
s["Modifiers"] = s.Modifiers.fillna(""); s["lb_id"] = s.lb_id.astype(str); s["player"] = s.player.astype(str)
s = s[(s.acc > 0) & (s.acc <= 1)]
res = {}
# modifier combos reduced to the speed modifier plus harmless ones
def speed_of(m):
    parts = set(x.strip() for x in m.split(",") if x.strip())
    if parts - {"FS", "SF", "SS", "IF", "BE"}: return None
    for k in ("SF", "FS", "SS"):
        if k in parts: return k
    return None
s["spd"] = s.Modifiers.map(speed_of)
s = s[s.spd.notna()]
s["skill"] = s.player.map(sk); s["d"] = s.lb_id.map(mp.d)
s = s.dropna(subset=["skill", "d"])
s["shift"] = latent.err_transform(s.acc) - (s.d - s.skill)
print("speed-modifier scores with known skill/map:", len(s), s.spd.value_counts().to_dict())
rn = R[R["mod"] == "none"].set_index("lb_id")
for spd, key in (("SS", "SS"), ("FS", "FS"), ("SF", "SFS")):
    rm = R[R["mod"] == key].set_index("lb_id")
    g = s[s.spd == spd].groupby("lb_id").agg(shift=("shift", "mean"), n=("shift", "size"), accm=("acc", "mean"))
    g = g[g.n >= 30].join(rm[["predicted_acc", "pass", "tech", "stars"]].add_suffix("_m")).join(rn[["predicted_acc", "pass", "tech", "stars"]].add_suffix("_n")).join(mp[["stars", "d"]].add_suffix("_clean"), how="inner")
    g["pred_shift"] = latent.err_transform(g.predicted_acc_m) - latent.err_transform(g.predicted_acc_n)
    g["dstars"] = g.stars_m - g.stars_n
    if len(g) < 20: continue
    b = np.polyfit(g.pred_shift, g["shift"], 1)
    w = g.n
    print(f"\n{spd}: {len(g)} maps (>=30 scores). mean data shift {np.average(g['shift'], weights=w):+.3f} (log-error units; + = harder) | mean ML-predicted shift {np.average(g.pred_shift, weights=w):+.3f}")
    print(f"  per-map data shift ~ ML-predicted shift: slope {b[0]:.2f}, corr {np.corrcoef(g.pred_shift, g['shift'])[0,1]:.3f}; "
          f"SD of data shift {g['shift'].std():.3f}, residual SD {np.std(g['shift'] - np.polyval(b, g.pred_shift)):.3f}, sampling SD ~{(0.3/np.sqrt(g.n)).mean():.3f}")
    # how does the shift depend on stars / pass?
    g["dpass"] = g.pass_m - g.pass_n; g["dtech"] = g.tech_m - g.tech_n; g["lpass"] = np.log(g.pass_m.clip(lower=0.05)) - np.log(g.pass_n.clip(lower=0.05))
    import statsmodels.api as sm
    for lab, cols in (("ML shift only", ["pred_shift"]), ("analyzer: d pass, d tech", ["dpass", "dtech"]), ("analyzer + ML shift", ["dpass", "dtech", "pred_shift"]), ("analyzer + map difficulty (clean pass,tech)", ["dpass", "dtech", "pass_n", "tech_n"])):
        f = sm.OLS(g["shift"], sm.add_constant(g[cols])).fit()
        print(f"  data shift ~ [{lab}]: R2 {f.rsquared:.3f}")
    print(f"  corr(data shift, analyzer d_pass) {np.corrcoef(g['shift'], g.dpass)[0,1]:+.3f}, d_tech {np.corrcoef(g['shift'], g.dtech)[0,1]:+.3f}, d_stars {np.corrcoef(g['shift'], g.dstars)[0,1]:+.3f}")
    for c in ("pass_n", "stars_n", "tech_n"):
        print(f"  corr(data shift, {c}) {np.corrcoef(g['shift'], g[c])[0,1]:+.3f}   corr(ML pred shift, {c}) {np.corrcoef(g.pred_shift, g[c])[0,1]:+.3f}")
    res[spd] = dict(n_maps=len(g), data_shift=float(np.average(g["shift"], weights=w)), ml_shift=float(np.average(g.pred_shift, weights=w)), slope=float(b[0]), corr=float(np.corrcoef(g.pred_shift, g["shift"])[0, 1]))
json.dump(res, open(os.path.join(OUT, f"a10_{tag}.json"), "w"), indent=1)
