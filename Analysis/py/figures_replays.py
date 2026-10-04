"""Report figures from the replay-study analyses (r01, r04, r05). Usage: python figures_replays.py <r01 tag> <r04 tag>"""
import os, sys
import numpy as np, pandas as pd
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

OUT = os.path.join(os.path.dirname(__file__), "..", "out")
FIG = os.path.join(os.path.dirname(__file__), "..", "figures")
os.makedirs(FIG, exist_ok=True)
t1, t4 = (sys.argv[1], sys.argv[2]) if len(sys.argv) > 2 else ("r01snap1", "r04snap1")
BLUE, ORANGE, AQUA = "#2a78d6", "#eb6834", "#1baf7a"
INK, INK2, GRID, SURF = "#0b0b0b", "#52514e", "#e6e5e1", "#fcfcfb"
plt.rcParams.update({"figure.facecolor": SURF, "axes.facecolor": SURF, "savefig.facecolor": SURF, "axes.edgecolor": GRID, "axes.labelcolor": INK2,
                     "xtick.color": INK2, "ytick.color": INK2, "text.color": INK, "axes.grid": True, "grid.color": GRID, "grid.linewidth": 0.8,
                     "axes.spines.top": False, "axes.spines.right": False, "font.size": 10, "axes.titlesize": 11, "axes.titleweight": "bold",
                     "axes.titlelocation": "left", "legend.frameon": False})
def save(fig, name):
    fig.savefig(os.path.join(FIG, name), dpi=150, bbox_inches="tight"); plt.close(fig); print("saved", name)

# ---- F7: loss decomposition + ML vs observed (map level)
dec = pd.read_csv(os.path.join(OUT, f"{t1}_loss_decomposition.csv"))
dec["band"] = pd.cut(dec.accuracy, [0, 0.8, 0.9, 0.94, 0.96, 0.97, 0.98, 1.0])
sh = dec.groupby("band", observed=True)[["sh_center", "sh_pre", "sh_post", "sh_miss"]].mean()
mm = pd.read_csv(os.path.join(OUT, f"{t1}_map_ml_vs_obs.csv"))
fig, ax = plt.subplots(1, 2, figsize=(11.5, 4.2), gridspec_kw={"width_ratios": [1.1, 1]})
a = ax[0]
xs = np.arange(len(sh)); bot = np.zeros(len(sh))
for k, c, lab in (("sh_center", BLUE, "centre cut (15 pts)"), ("sh_pre", ORANGE, "pre-swing (70)"), ("sh_post", AQUA, "post-swing (30)"), ("sh_miss", "#8a8984", "miss / bad cut")):
    a.bar(xs, sh[k], bottom=bot, color=c, width=0.72, label=lab, edgecolor=SURF, linewidth=2)
    for xi, v, b0 in zip(xs, sh[k], bot):
        if v > 0.08: a.text(xi, b0 + v / 2, f"{v:.0%}", ha="center", va="center", color="white", fontsize=8.5, fontweight="bold")
    bot += sh[k].values
a.set_xticks(xs); a.set_xticklabels(["<80", "80-90", "90-94", "94-96", "96-97", "97-98", ">98"]); a.set_xlabel("replay accuracy (%)"); a.set_ylabel("share of lost note points")
a.set_title("Above 96%, nearly all loss is the centre cut")
a.legend(ncol=4, loc="upper center", fontsize=8.5, bbox_to_anchor=(0.5, -0.2)); a.grid(axis="x", visible=False)
a = ax[1]
a.scatter(mm.ml, mm.obs_top, s=9, color=BLUE, alpha=.45, linewidths=0)
lo, hi = min(mm.ml.min(), mm.obs_top.min()), max(mm.ml.max(), mm.obs_top.max())
a.plot([lo, hi], [lo, hi], color=INK, lw=1.2)
r = np.corrcoef(mm.ml, mm.obs_top)[0, 1]
a.set_xlabel("ML mean predicted centre accuracy"); a.set_ylabel("observed, mean of top-8 replays")
a.set_title(f"ML vs observed top-8 centre accuracy (r={r:.2f}, {len(mm)} maps)")
save(fig, "f7_replays_ml_and_loss.png")

# ---- F8: what happens on swings the corpus analyzer flags as direction repeats, vs its cost model
T = pd.read_csv(os.path.join(OUT, f"{t4}_T1_repeat_by_gap.csv"))
T = T[T.n >= 100]
mid = [float(g.strip("()[]").split(",")[0]) / 2 + float(g.strip("()[]").split(",")[1]) / 2 for g in T.gap_bin]
fig, ax = plt.subplots(figsize=(8.4, 4.4))
ax.plot(mid, T.obs_reset, color=BLUE, lw=2.4, marker="o", ms=5, label="observed: physical reset")
ax.plot(mid, T.model_reset, color=BLUE, lw=1.6, ls="--", label="corpus model P(reset)")
ax.plot(mid, T.obs_roll, color=ORANGE, lw=2.4, marker="o", ms=5, label="observed: wrist roll")
ax.plot(mid, T.model_roll, color=ORANGE, lw=1.6, ls="--", label="corpus model P(roll)")
ax.plot(mid, T.obs_alt, color=AQUA, lw=2.4, marker="o", ms=5, label="observed: plain alternation")
ax.set_ylim(0, 1.02); ax.set_xlabel("seconds between cuts (same hand)"); ax.set_ylabel("share of flagged direction repeats")
ax.set_title("Flagged direction repeats: outcomes vs the corpus cost model")
ax.legend(fontsize=8.5, loc="upper right", ncol=1)
save(fig, "f8_reset_roll_by_gap.png")

# ---- F9: physical work and precision of rolls/resets vs alternation
P = pd.read_csv(os.path.join(OUT, f"{t4}_T2_by_gap.csv"), header=[0, 1], index_col=0)
fig, ax = plt.subplots(1, 2, figsize=(11.5, 4.0))
gaps = [str(i) for i in P.index]
x = np.arange(len(gaps))
for a, metric, title, ylab in ((ax[0], "tip_len", "Saber-tip travel between cuts vs alternation", "ratio to alternation at same gap"),
                               (ax[1], "loss_center", "Centre-cut loss vs alternation (top-8 players)", "ratio to alternation at same gap")):
    for cls, c in (("roll", ORANGE), ("reset", BLUE)):
        r_ = (P[(metric, cls)] / P[(metric, "alt")]).values
        a.plot(x, r_, color=c, lw=2.2, marker="o", ms=5, label=cls)
    a.axhline(1.0, color=INK2, lw=1)
    a.set_xticks(x); a.set_xticklabels([g.replace("(", "").replace("]", "").replace(", ", "-") for g in gaps], rotation=20)
    a.set_xlabel("seconds between cuts"); a.set_ylabel(ylab); a.set_title(title); a.legend()
save(fig, "f9_roll_reset_cost.png")
print("done")
