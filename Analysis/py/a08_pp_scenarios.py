"""A8: full-PP scenarios. What changes for maps and players if
  (a) the ML acc rating is replaced by an algorithmic one (swing features, song-grouped CV predictions),
  (b) the acc curve becomes a power law in error,
  (c) the pass / tech weights in the PP sum are rescaled (the "ratio redistribution")?
Metrics (all on the 2.5M clean scores of players with >=15 scores):
  * map bias in log PP at equal player skill (additive player+map fit), reported WITHIN star buckets (stars of that scenario),
    plus the stars gradient of the bias
  * player ranking agreement with production (Spearman of weighted totals), top-100 overlap, PP-share of pass/acc/tech for top 1000
"""
import os, sys, json, itertools
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import data, latent, ppmodel as pm

OUT = os.path.join(os.path.dirname(__file__), "..", "out")
tag = sys.argv[1] if len(sys.argv) > 1 else "prod"
mp = pd.read_csv(os.path.join(OUT, f"a06_maps_{tag}.csv"), dtype={"lb_id": str})
w_maps = (mp.n >= 100).values
a_ref = mp.d[w_maps].mean() - mp.ai_err[w_maps].mean()
pred_of = lambda dh: np.clip(1 - np.exp(np.asarray(dh) - a_ref), 0.5, 0.9995)
GAMMA = 0.57

df = data.scores_with_ratings(tag)
d, players, maps = latent.prepare(df)
mpi = mp.set_index("lb_id")
lb_ids = pd.Index(maps)
def col(c): return mpi.loc[lb_ids, c].values
M = len(maps)
pass_r, tech_r, nerf = col("pass"), col("tech"), col("low_note_nerf")
pred_sources = {"ML": col("predicted_acc"), "algo8": pred_of(col("d_hat_compact_lin")), "algoAll": pred_of(col("d_hat_all_lin"))}
mi, pi = d.mi.values, d.pi.values
acc = d.acc.values
P = len(players)
keep = np.arange(len(d))
rng = np.random.default_rng(0)
sub = rng.random(len(d)) < 0.35          # subsample rows for the ALS inside the scenario loop (speed); totals use all rows

def scenario(src, curve, mp_, mt_):
    pred = pred_sources[src]
    ar = pm.acc_rating_from_predicted(pred) * nerf
    if curve == "curve2":
        cf = pm.curve2
        pass_pp, _, tech_pp = pm.pp_components(acc, ar[mi], pass_r[mi] * mp_, tech_r[mi] * mt_)
        acc_pp = pm.curve2(acc) * ar[mi] * 34.0
        # stars at 0.96
        a96 = np.full(M, 0.96)
        sp, sa, st = pm.pp_components(a96, ar, pass_r * mp_, tech_r * mt_)
        sa = pm.curve2(a96) * ar * 34.0
    else:
        # power-law acc curve scaled so that, at the ML-predicted accuracy, acc PP matches the production value (34*15.5)
        g = lambda x: np.power(np.maximum(1 - x, 1e-4), -GAMMA)
        rating = 1.0 / g(pred)
        k = 34.0 * 15.5 / 1.0
        acc_pp = g(acc) * rating[mi] * k * nerf[mi]
        pass_pp, _, tech_pp = pm.pp_components(acc, ar[mi], pass_r[mi] * mp_, tech_r[mi] * mt_)
        a96 = np.full(M, 0.96)
        sp, _, st = pm.pp_components(a96, ar, pass_r * mp_, tech_r * mt_)
        sa = g(a96) * rating * k * nerf
    tot = pass_pp + acc_pp + tech_pp
    pp = pm.inflate(tot)
    inc = np.where(tot > 0, pp / tot, 0)
    stars = pm.inflate(sp + sa + st) / 52.0
    return pp, pass_pp * inc, acc_pp * inc, tech_pp * inc, stars

def metrics(pp, stars):
    l = np.log(np.maximum(pp, 1e-3))
    # additive fit  -l = d' - a'   on the subsample
    a_, b_ = latent.fit_additive(pi[sub], mi[sub], -l[sub], P, M, iters=20)
    bias = -b_                                   # per-map log-PP effect at equal skill
    nj = np.bincount(mi, minlength=M)
    ok = nj >= 100
    sb = pd.cut(stars, [0, 3, 5, 7, 9, 11, 30])
    df_ = pd.DataFrame({"b": bias[ok], "sb": sb[ok], "st": stars[ok]})
    within = df_.groupby("sb", observed=True).b.transform(lambda x: x - x.mean())
    sd_within = within[df_.st >= 3].std()
    slope = np.polyfit(df_.st[df_.st >= 3], df_.b[df_.st >= 3], 1)[0]
    return sd_within, slope, a_.std()

def totals(pp, parts):
    t = pd.DataFrame({"player": pi, "pp": pp, "pass": parts[0], "acc": parts[1], "tech": parts[2]})
    tt, _ = pm.player_totals(t, "pp", "player", extra_cols=("pass", "acc", "tech"))
    return tt

base_pp, bp, ba, bt, base_stars = scenario("ML", "curve2", 1.0, 1.0)
base_tot = totals(base_pp, (bp, ba, bt))
base_rank = base_tot.pp.rank(ascending=False)
rows = []
grid = [("ML", "curve2", 1, 1)]
for src in ("ML", "algo8", "algoAll"):
    for curve in ("curve2", "power"):
        for mp_, mt_ in ((1, 1), (0.9, 1), (0.8, 1), (1, 0.7), (0.9, 0.7)):
            grid.append((src, curve, mp_, mt_))
seen = set()
for src, curve, mp_, mt_ in grid:
    key = (src, curve, mp_, mt_)
    if key in seen: continue
    seen.add(key)
    pp, ps, ac, te, stars = scenario(src, curve, mp_, mt_)
    sd_w, slope, sd_a = metrics(pp, stars)
    tt = totals(pp, (ps, ac, te))
    common = base_tot.index.intersection(tt.index)
    sp = pd.Series(tt.pp.loc[common].rank()).corr(pd.Series(base_tot.pp.loc[common].rank()), method="spearman")
    top_new = set(tt.pp.sort_values(ascending=False).head(100).index); top_old = set(base_tot.pp.sort_values(ascending=False).head(100).index)
    t1000 = tt.sort_values("pp", ascending=False).head(1000)
    scale = tt.pp.sort_values(ascending=False).head(100).mean() / base_tot.pp.sort_values(ascending=False).head(100).mean()
    rel = (tt.pp.loc[common] / base_tot.pp.loc[common]).loc[base_tot.pp.loc[common].sort_values(ascending=False).head(1000).index]
    rows.append(dict(source=src, curve=curve, pass_mult=mp_, tech_mult=mt_, within_star_bias_sd=sd_w, bias_vs_stars_slope=slope,
                     spearman_vs_prod=sp, top100_overlap=len(top_new & top_old), top100_pp_ratio=scale,
                     top1000_change_sd=rel.std(), top1000_change_p5=rel.quantile(.05), top1000_change_p95=rel.quantile(.95),
                     share_pass=t1000["pass"].sum() / t1000.pp.sum(), share_acc=t1000.acc.sum() / t1000.pp.sum(), share_tech=t1000.tech.sum() / t1000.pp.sum()))
    print(rows[-1], flush=True)
R = pd.DataFrame(rows)
R.to_csv(os.path.join(OUT, f"a08_scenarios_{tag}.csv"), index=False)
pd.set_option("display.width", 250); pd.set_option("display.max_columns", 30)
print(R.round(3).to_string(index=False))
