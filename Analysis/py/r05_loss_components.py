"""R5: what drives each component of accuracy loss at the map level (replay-based)?

Per replay: centre loss, pre-swing loss, post-swing loss (points per note, of 15/70/30), miss+bad rate. Per map the ML predicted centre error
(from the per-note predictions), pass, tech, notes, length. Regressions control for player skill (latent skill a_i from the score-dump fit,
or replay accuracy where unknown). Answers: why do pass/tech carry information about total-accuracy difficulty beyond the ML's centre-accuracy prediction?
Usage: python r05_loss_components.py <replay-study dir> [tag]
"""
import os, sys, json
import numpy as np, pandas as pd
import statsmodels.api as sm
sys.path.insert(0, os.path.dirname(__file__))
import replays, data

d = sys.argv[1]
tag = sys.argv[2] if len(sys.argv) > 2 else "r05"
OUT = os.path.join(os.path.dirname(__file__), "..", "out")
rp, sw, n = replays.load(d)
nn = n[n.scoring_type == replays.SCORING_NORMAL]
g = nn.groupby("score_id")
good = nn.event_type == 0
rep = pd.DataFrame({
    "notes": g.size(),
    "c": nn.assign(v=(15 - nn.acc).where(good, np.nan)).groupby("score_id").v.mean(),
    "pre": nn.assign(v=(70 - nn.pre).where(good, np.nan)).groupby("score_id").v.mean(),
    "post": nn.assign(v=(30 - nn.post).where(good, np.nan)).groupby("score_id").v.mean(),
    "bad": nn.assign(v=(~good).astype(float)).groupby("score_id").v.mean(),
}).reset_index().merge(rp[["score_id", "lb_id", "player_id", "accuracy", "stratum_kind"]], on="score_id")
sk = pd.read_csv(os.path.join(OUT, "a01_player_skill_prod.csv"), dtype={"player": str}).set_index("player").skill
fit = rep.assign(skill=rep.player_id.map(sk)).dropna(subset=["skill"])
R = data.load_ratings("prod").set_index("lb_id")
ai = pd.read_csv(os.path.join(os.path.dirname(d), "ratings_v2", "ai_notes_prod.csv.gz"), dtype={"lb_id": str, "key": str}) if os.path.exists(os.path.join(os.path.dirname(d), "ratings_v2")) else None
mlerr = (1 - ai.groupby("lb_id").acc.mean()).rename("ml_err") if ai is not None else None
fit = fit.join(R[["pass", "tech", "n_swings", "length", "njs_mean", "stars"]], on="lb_id").join(mlerr, on="lb_id").dropna(subset=["pass", "ml_err"])
fit["ml_lerr"] = np.log(fit.ml_err); fit["lnotes"] = np.log(fit.n_swings); fit["llen"] = np.log(fit.length.clip(lower=5))
print("replays used", len(fit), "players with latent skill", fit.player_id.nunique(), "maps", fit.lb_id.nunique())
out = {}
for strat in ("all", "mid", "top"):
    sub = fit if strat == "all" else fit[fit.stratum_kind == strat]
    if len(sub) < 200: continue
    print(f"\n=== {strat}: {len(sub)} replays")
    rows = []
    for comp, lab, transform in (("c", "centre loss (pts/note of 15)", np.log), ("pre", "pre-swing loss (of 70)", lambda v: np.log(v + 0.05)), ("post", "post-swing loss (of 30)", lambda v: np.log(v + 0.02)), ("bad", "miss+bad-cut rate", lambda v: np.log(v + 0.002))):
        y = transform(sub[comp].clip(lower=1e-4))
        X = sub[["skill", "ml_lerr", "pass", "tech", "lnotes"]]
        Xs = (X - X.mean()) / X.std()
        f = sm.OLS(y, sm.add_constant(Xs)).fit()
        Xr = sm.add_constant(Xs[["skill"]]); f0 = sm.OLS(y, Xr).fit()
        rows.append(dict(component=lab, R2_skill_only=f0.rsquared, R2_full=f.rsquared, **{f"b_{c}": f.params[c] for c in Xs.columns}, **{f"t_{c}": f.tvalues[c] for c in Xs.columns}))
    T = pd.DataFrame(rows).set_index("component")
    pd.set_option("display.width", 250)
    print("standardised coefficients on log(loss):")
    print(T[["R2_skill_only", "R2_full", "b_skill", "b_ml_lerr", "b_pass", "b_tech", "b_lnotes"]].round(3).to_string())
    out[strat] = T.round(4).to_dict()
json.dump(out, open(os.path.join(OUT, f"{tag}.json"), "w"), indent=1)
