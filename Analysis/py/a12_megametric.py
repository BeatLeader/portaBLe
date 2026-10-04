"""A12: the existing yardstick (Megametric, LeaderboardsRefresh.CalculateMegametric) under alternative rating sources/curves.

Megametric_j(N) = mean over the top 33 % (by weight) of the map's scores of players with > N ranked scores of  pp/topPp * weight.
High = the map feeds many players' best PP (generous); low = rarely counts. Autoreweight nerfs maps with mm125 >= 0.65 and buffs maps whose top-33 % mean weight < 0.5,
i.e. the spread of this metric is what manual reweighting tries to flatten. We recompute it for clean scores under (a) production ratings and (b) scenarios.
"""
import os, sys
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import data, ppmodel as pm

OUT = os.path.join(os.path.dirname(__file__), "..", "out")
mp = pd.read_csv(os.path.join(OUT, "a06_maps_prod.csv"), dtype={"lb_id": str}).set_index("lb_id")
w = mp.n >= 100
a_ref = mp.d[w].mean() - mp.ai_err[w].mean()
pred_of = lambda dh: np.clip(1 - np.exp(np.asarray(dh) - a_ref), 0.5, 0.9995)
GAMMA = 0.57

s = data.load_scores(); s["lb_id"] = s.lb_id.astype(str); s["player"] = s.player.astype(str)
s = s[s.lb_id.isin(mp.index)].copy()
s["lb"] = s.lb_id
R = mp.loc[s.lb]
nerf = R.low_note_nerf.values

def scenario_pp(pred, curve):
    ar = pm.acc_rating_from_predicted(pred) * nerf
    acc = s.acc.values
    p_, _, t_ = pm.pp_components(acc, ar, R["pass"].values, R.tech.values)
    if curve == "curve2":
        a_ = pm.curve2(acc) * ar * 34.0
    else:
        g = lambda x: np.power(np.maximum(1 - x, 1e-4), -GAMMA)
        a_ = g(acc) * (1.0 / g(pred)) * 34.0 * 15.5 * nerf
    tot = p_ + a_ + t_
    return pm.inflate(tot)

scen = {"production (ML + Curve2)": scenario_pp(R.predicted_acc.values, "curve2"),
        "ML + power-law curve": scenario_pp(R.predicted_acc.values, "power"),
        "algorithm (8 feat) + power-law": scenario_pp(pred_of(R.d_hat_compact_lin.values), "power"),
        "algorithm (all feat) + power-law": scenario_pp(pred_of(R.d_hat_all_lin.values), "power")}

def megametric(pp):
    d = pd.DataFrame({"player": s.player.values, "lb": s.lb.values, "pp": pp})
    d = d.sort_values(["player", "pp"], ascending=[True, False])
    d["rank"] = d.groupby("player").cumcount(); d["weight"] = 0.965 ** d["rank"]
    top = d.groupby("player").pp.max().rename("top"); cnt = d.groupby("player").size().rename("cnt")
    d = d.join(top, on="player").join(cnt, on="player")
    out = {}
    for N in (125, 75, 40):
        e = d[(d.cnt > N) & (d.top > 0)]
        def f(g):
            k = int(len(g) * 0.33)
            gg = g.nlargest(k, "weight")
            return (gg.pp / gg.top * gg.weight).mean() if len(gg) > 10 else np.nan
        out[f"mm{N}"] = e.groupby("lb").apply(f, include_groups=False)
    base = d[d.top > 0].groupby("lb").apply(lambda g: g.nlargest(int(len(g) * 0.33), "weight").weight.mean() if len(g) > 30 else np.nan, include_groups=False)
    out["top33_weight"] = base
    return pd.DataFrame(out)

rows = []
res = {}
for name, pp in scen.items():
    M = megametric(pp)
    M = M[M.index.isin(mp.index[w])]
    res[name] = M
    m = M.mm125.dropna()
    rows.append(dict(scenario=name, maps=len(m), mean_mm125=m.mean(), sd_mm125=m.std(), cv=m.std() / m.mean(), share_ge_065=(m >= 0.65).mean(), p5=m.quantile(.05), p95=m.quantile(.95),
                     share_top33w_lt05=(M.top33_weight.dropna() < 0.5).mean(), corr_stars=np.corrcoef(m, mp.loc[m.index, "stars"])[0, 1]))
    print(rows[-1], flush=True)
R_ = pd.DataFrame(rows)
pd.set_option("display.width", 220)
print(R_.round(3).to_string(index=False))
R_.to_csv(os.path.join(OUT, "a12_megametric.csv"), index=False)
