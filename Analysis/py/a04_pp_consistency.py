"""A4: Does the PP formula reward equal demonstrated skill equally?

Demonstrated skill of a score:  p_ij = d_j - log(1-acc_ij)   (data-implied map difficulty minus the score's error rate;
equivalent to the player skill that this single score implies).  A reward that depended only on demonstrated skill would
have zero map bias. We measure how far the current formula is from that, and for which kinds of maps.
"""
import os, sys, json
import numpy as np, pandas as pd
import statsmodels.api as sm
sys.path.insert(0, os.path.dirname(__file__))
import data, latent, ppmodel as pm

OUT = os.path.join(os.path.dirname(__file__), "..", "out")
tag = sys.argv[1] if len(sys.argv) > 1 else "prod"

df = data.scores_with_ratings(tag)
d, players, maps = latent.prepare(df)
z = latent.err_transform(d.acc.values)
a, dj = latent.fit_additive(d.pi.values, d.mi.values, z, len(players), len(maps))
d["d_map"] = dj[d.mi.values]
d["a_player"] = a[d.pi.values]
d["p_skill"] = d.d_map - z                       # skill demonstrated by this score
full, ppass, pacc, ptech = pm.pp(d.acc.values, d.acc_rating.values, d["pass"].values, d.tech.values)
d["pp"], d["pp_pass"], d["pp_acc"], d["pp_tech"] = full, ppass, pacc, ptech
d["lpp"] = np.log(d.pp.clip(lower=1e-3))
d["stars_calc"] = d.stars

print("== PP composition (no-mod scores of players with >=15 scores)")
d["star_bucket"] = pd.cut(d.stars, [0, 3, 5, 7, 9, 11, 20])
comp = d.groupby("star_bucket", observed=True)[["pp", "pp_pass", "pp_acc", "pp_tech"]].mean()
comp_share = comp[["pp_pass", "pp_acc", "pp_tech"]].div(comp.pp, axis=0)
comp_share.columns = ["pass", "acc", "tech"]
print(pd.concat([comp.pp.round(1), comp_share.round(3)], axis=1).to_string())

# top players' PP composition (portaBLe weighting)
tot, _ = pm.player_totals(d, "pp", "player", extra_cols=("pp_pass", "pp_acc", "pp_tech"))
tot = tot.sort_values("pp", ascending=False)
for k in (100, 1000, 5000):
    t = tot.head(k)
    print(f"top{k:5d} players: mean PP {t.pp.mean():7.1f} | share pass {t.pp_pass.sum()/t.pp.sum():.3f} acc {t.pp_acc.sum()/t.pp.sum():.3f} tech {t.pp_tech.sum()/t.pp.sum():.3f}")

# ---- map bias in log PP (additive player + map model in PP space)
pa, mb = latent.fit_additive(d.pi.values, d.mi.values, -d.lpp.values, len(players), len(maps))  # z' = lpp -> use -lpp so sign: a is skill
mapb = mb * -1   # fit_additive models z = d - a ; with z=-lpp -> d = -b  => b = -d
fitp = (-mb[d.mi.values] + pa[d.pi.values])
print(f"\nlog PP: additive player+map R2 = {1-np.var(d.lpp.values-fitp)/np.var(d.lpp.values):.3f}; SD of map effects b_j = {mapb.std():.3f} (i.e. maps pay {np.exp(mapb.std())-1:.0%} more/less PP at equal skill, 1 SD)")

# ---- PP vs demonstrated skill: global monotone map and residual by map
# isotonic-ish: bin demonstrated skill, take mean lpp in bin
bins = pd.qcut(d.p_skill, 200, duplicates="drop")
f = d.groupby(bins, observed=True).lpp.mean()
mid = d.groupby(bins, observed=True).p_skill.mean()
d["lpp_hat"] = np.interp(d.p_skill, mid.values, f.values)
d["lpp_resid"] = d.lpp - d.lpp_hat
print(f"log PP explained by demonstrated skill alone (p_skill): R2 = {1-d.lpp_resid.var()/d.lpp.var():.3f}; residual SD {d.lpp_resid.std():.3f}")
mres = d.groupby("lb_id").lpp_resid.mean().rename("pp_bias").reset_index()
mp = pd.DataFrame({"lb_id": maps, "d": dj, "n": np.bincount(d.mi.values)}).merge(mres, on="lb_id").merge(data.load_ratings(tag), on="lb_id").merge(data.load_maps(), on="lb_id")
mp["share_pass"] = np.nan
agg = d.groupby("lb_id")[["pp", "pp_pass", "pp_acc", "pp_tech"]].mean()
mp = mp.merge((agg[["pp_pass", "pp_acc", "pp_tech"]].div(agg.pp, axis=0)).add_prefix("share_").reset_index(), on="lb_id", suffixes=("_x", ""))
mp["log_len"] = np.log(mp.length.clip(lower=5)); mp["density"] = mp.n_notes / mp.length.clip(lower=1)
print(f"map-level PP bias (mean residual) SD = {mp.pp_bias.std():.3f}; maps with |bias|>0.15: {(mp.pp_bias.abs()>0.15).mean():.1%}")
cols = ["pass", "tech", "acc_rating", "stars", "share_pp_pass", "share_pp_tech", "share_pp_acc", "log_len", "density", "njs_mean", "multi_pct", "linear_pct", "low_note_nerf", "predicted_acc"]
print("corr of map PP bias with:", {c: round(float(np.corrcoef(mp.pp_bias, mp[c])[0, 1]), 3) for c in cols})
X = sm.add_constant(mp[["stars", "share_pp_pass", "share_pp_tech", "log_len", "density", "njs_mean", "multi_pct", "low_note_nerf"]])
m = sm.OLS(mp.pp_bias, X).fit()
print("bias ~ features: R2 %.3f" % m.rsquared); print(pd.DataFrame({"coef": m.params, "t": m.tvalues}).round(3).to_string())
print("\nPP bias by star bucket / pass share bucket:")
print(mp.groupby(pd.cut(mp.stars, [0, 3, 5, 7, 9, 11, 20]), observed=True).pp_bias.agg(["mean", "std", "size"]).round(3).to_string())
print(mp.groupby(pd.cut(mp.share_pp_pass, [0, .15, .25, .35, .45, .6, 1]), observed=True).pp_bias.agg(["mean", "std", "size"]).round(3).to_string())

# ---- skill dependence of the bias: split scores by demonstrated skill level
d["skill_band"] = pd.qcut(d.a_player, 3, labels=["low", "mid", "high"])
bands = {}
for bnd, g in d.groupby("skill_band", observed=True):
    bands[bnd] = g.groupby("lb_id").lpp_resid.mean()
B = pd.DataFrame(bands).dropna()
print("\nmap PP bias per player-skill band: corr", B.corr().round(3).to_dict())
print("  SD per band", B.std().round(3).to_dict(), "| mean (low/high)", B.mean().round(3).to_dict())
dd = (B["high"] - B["low"])
print(f"  high-low skill bias difference: SD {dd.std():.3f}, corr with predicted_acc {np.corrcoef(dd.loc[mp.set_index('lb_id').index.intersection(dd.index)], mp.set_index('lb_id').loc[dd.index.intersection(mp.lb_id), 'predicted_acc'])[0,1]:.3f}")

# acc-component only: is the acc curve itself skill-consistent?
d["l_accpp"] = np.log(d.pp_acc.clip(lower=1e-3))
accf = d.groupby(pd.qcut(d.p_skill, 200, duplicates="drop"), observed=True).l_accpp.mean()
accm = d.groupby(pd.qcut(d.p_skill, 200, duplicates="drop"), observed=True).p_skill.mean()
d["acc_resid"] = d.l_accpp - np.interp(d.p_skill, accm.values, accf.values)
bands2 = {b: g.groupby("lb_id").acc_resid.mean() for b, g in d.groupby("skill_band", observed=True)}
B2 = pd.DataFrame(bands2).dropna()
print(f"\nacc-PP only: map bias SD per band {B2.std().round(3).to_dict()} ; high-minus-low SD {(B2.high-B2.low).std():.3f}; corr low/high {B2.corr().loc['low','high']:.3f}")
mp.to_csv(os.path.join(OUT, f"a04_map_pp_bias_{tag}.csv"), index=False)
