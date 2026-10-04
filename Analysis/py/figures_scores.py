"""Report figures from the score-dump analyses (a01-a10). Palette: validated reference palette (blue/orange/aqua = first three slots, safe for all-pairs)."""
import os, sys, json
import numpy as np, pandas as pd
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
sys.path.insert(0, os.path.dirname(__file__))
import ppmodel as pm, latent

OUT = os.path.join(os.path.dirname(__file__), "..", "out")
FIG = os.path.join(os.path.dirname(__file__), "..", "figures")
os.makedirs(FIG, exist_ok=True)
BLUE, ORANGE, AQUA = "#2a78d6", "#eb6834", "#1baf7a"
INK, INK2, GRID, SURF = "#0b0b0b", "#52514e", "#e6e5e1", "#fcfcfb"
plt.rcParams.update({"figure.facecolor": SURF, "axes.facecolor": SURF, "savefig.facecolor": SURF, "axes.edgecolor": GRID, "axes.labelcolor": INK2,
                     "xtick.color": INK2, "ytick.color": INK2, "text.color": INK, "axes.grid": True, "grid.color": GRID, "grid.linewidth": 0.8,
                     "axes.spines.top": False, "axes.spines.right": False, "font.size": 10, "axes.titlesize": 11, "axes.titleweight": "bold",
                     "axes.titlelocation": "left", "legend.frameon": False})

def save(fig, name):
    fig.savefig(os.path.join(FIG, name), dpi=150, bbox_inches="tight"); plt.close(fig); print("saved", name)

# ---- F1: data-implied difficulty vs ML prediction + where it misses
m = pd.read_csv(os.path.join(OUT, "a03_resid_prod.csv"), dtype={"lb_id": str})
m = m[m.n >= 100]
fig, ax = plt.subplots(1, 2, figsize=(11, 4.2), gridspec_kw={"width_ratios": [1.15, 1]})
a = ax[0]
low = m.n_notes <= 150
a.scatter(m.ai_err[~low], m.d[~low], s=7, color=BLUE, alpha=.35, linewidths=0, label="maps")
a.scatter(m.ai_err[low], m.d[low], s=14, color=ORANGE, alpha=.8, linewidths=0, label="<=150 notes")
b1, b0 = np.polyfit(m.ai_err, m.d, 1)
xs = np.linspace(m.ai_err.min(), m.ai_err.max(), 50)
a.plot(xs, b0 + b1 * xs, color=INK, lw=1.4)
a.set_xlabel("ML predicted error, log(1 - predicted acc)"); a.set_ylabel("difficulty implied by scores, d_j")
a.set_title(f"Score-implied map difficulty tracks the ML (R² {np.corrcoef(m.ai_err, m.d)[0,1]**2:.2f})")
a.legend(loc="upper left")
a = ax[1]
g = m.groupby(pd.cut(m.n_notes, [0, 150, 250, 400, 600, 900, 1500, 1e5]), observed=True).resid.agg(["mean", "std", "size"])
x = np.arange(len(g)); a.bar(x, g["mean"], color=[ORANGE] + [BLUE] * (len(g) - 1), width=0.65)
a.errorbar(x, g["mean"], yerr=g["std"] / np.sqrt(g["size"]), fmt="none", ecolor=INK2, lw=1)
a.set_xticks(x); a.set_xticklabels(["<=150", "150-250", "250-400", "400-600", "600-900", "900-1500", ">1500"], rotation=30)
a.axhline(0, color=INK2, lw=.8); a.set_xlabel("notes in map"); a.set_ylabel("residual (+ = harder than ML says)")
a.set_title("Short maps are harder than the ML predicts")
save(fig, "f1_ml_vs_scores.png")

# ---- F2: how much of the score-implied difficulty each estimator explains (song-grouped CV)
r6 = json.load(open(os.path.join(OUT, "a06_prod.json")))
cf = None
items = [("ML predicted acc, linear", r6["ML_linear"], INK2), ("ML predicted acc, flexible", r6["ML_nonlinear_gbm"], INK2),
         ("pass + tech + ln(notes), closed form", 0.905, BLUE), ("8 swing features, linear", r6["COMPACT (8 features)|lin"], BLUE),
         ("all swing aggregates, linear", r6["all swing aggregates + base|lin"], BLUE), ("8 swing features + ML", r6["compact + ML predAcc|lin"], AQUA)]
fig, ax = plt.subplots(figsize=(8.2, 3.6))
y = np.arange(len(items))[::-1]
for yi, (lab, v, c) in zip(y, items):
    ax.barh(yi, v, color=c, height=0.6); ax.text(v + 0.004, yi, f"{v:.3f}", va="center", color=INK, fontsize=9)
ax.set_yticks(y); ax.set_yticklabels([i[0] for i in items]); ax.set_xlim(0.8, 1.0)
ax.set_xlabel("cross-validated R² for score-implied map difficulty (folds grouped by song)")
ax.set_title("A transparent model on analyzer outputs matches or beats the ML")
save(fig, "f2_estimators_r2.png")

# ---- F3: acc-PP bias vs player skill
b = pd.read_csv(os.path.join(OUT, "a07_accpp_bias_prod.csv"))
skills = [c for c in b.columns if c not in ("curve", "source")]
fig, ax = plt.subplots(figsize=(8, 4.2))
styles = {"ML (production predictedAcc)": (BLUE, "ML"), "algo: 8 swing features, linear": (ORANGE, "algorithm (8 swing features)"), "algo: all swing aggregates, linear": (AQUA, "algorithm (all swing aggregates)")}
x = [float(s) for s in skills]
for src, (c, lab) in styles.items():
    for cv, ls, suf in (("Curve2 (production)", "-", " + Curve2"), ("power law gamma=0.57", "--", " + power-law curve")):
        row = b[(b.curve == cv) & (b.source == src)].iloc[0]
        ax.plot(x, [row[s] for s in skills], color=c, ls=ls, lw=2, marker="o", ms=4)
        if ls == "--": ax.text(x[-1] + 0.001, row[skills[-1]], lab, color=INK, fontsize=8.5, va="center")
ax.plot([], [], color=INK2, ls="-", label="production Curve2"); ax.plot([], [], color=INK2, ls="--", label="power law in error (γ=0.57)")
ax.legend(loc="upper right"); ax.set_xlim(0.895, 0.995)
ax.set_xlabel("player accuracy on an average map (skill)"); ax.set_ylabel("SD across maps of log(acc PP)")
ax.set_title("Acc-PP bias between maps: a power-law curve removes the skill wobble")
save(fig, "f3_accpp_bias.png")

# ---- F4: FC difficulty vs pass rating by length (endurance)
fc = pd.read_csv(os.path.join(OUT, "a09_fc_maps_prod.csv"), dtype={"lb_id": str})
fc = fc[fc.n_swings > 0]
fc["lenq"] = pd.qcut(fc.n_swings, 4, labels=["shortest 25%", "short-mid", "long-mid", "longest 25%"])
shades = ["#cfe0f7", "#92b9ec", "#4f90de", "#1d5aa8"]
fig, ax = plt.subplots(figsize=(8, 4.2))
edges = [0, 2, 4, 6, 8, 10, 30]
for q, c in zip(fc.lenq.cat.categories, shades):
    s = fc[fc.lenq == q]
    gg = s.groupby(pd.cut(s["pass"], edges), observed=True).b_fc.agg(["mean", "size"])
    gg = gg[gg["size"] >= 8]
    xs = [iv.mid for iv in gg.index]
    ax.plot(xs, gg["mean"], color=c, lw=2.2, marker="o", ms=4, label=q)
ax.set_xlabel("pass rating"); ax.set_ylabel("difficulty of a full combo (logit units)")
ax.set_title("At equal pass rating, longer maps are far harder to full-combo")
ax.legend(title="swings in map", loc="upper left")
save(fig, "f4_endurance.png")

# ---- F6: PP composition by star bucket at 96%
m2 = m.copy()
pa, aa, ta = pm.pp_components(np.full(len(m2), 0.96), m2.acc_rating.values, m2["pass"].values, m2.tech.values)
aa = pm.curve2(np.full(len(m2), 0.96)) * m2.acc_rating.values * 34
tot = pa + aa + ta
cmp_ = pd.DataFrame({"stars": pd.cut(m2.stars, [0, 3, 5, 7, 9, 11, 20]), "pass": pa / tot, "acc": aa / tot, "tech": ta / tot}).groupby("stars", observed=True).mean()
fig, ax = plt.subplots(figsize=(7.5, 3.8))
bottom = np.zeros(len(cmp_)); xs = np.arange(len(cmp_))
for k, c in (("acc", BLUE), ("pass", ORANGE), ("tech", AQUA)):
    ax.bar(xs, cmp_[k], bottom=bottom, color=c, width=0.7, label=k, edgecolor=SURF, linewidth=2)
    for xi, v, bt in zip(xs, cmp_[k], bottom):
        if v > 0.06: ax.text(xi, bt + v / 2, f"{v:.0%}", ha="center", va="center", color="white", fontsize=9, fontweight="bold")
    bottom += cmp_[k].values
ax.set_xticks(xs); ax.set_xticklabels(["<=3", "3-5", "5-7", "7-9", "9-11", ">11"]); ax.set_xlabel("star rating"); ax.set_ylabel("share of PP at 96% accuracy")
ax.legend(ncol=3, loc="upper center", bbox_to_anchor=(.5, 1.12)); ax.set_title("Where PP comes from", pad=24)
ax.grid(axis="x", visible=False)
save(fig, "f6_pp_composition.png")
print("done")
