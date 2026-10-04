"""R1: replay-side check of the ML accuracy model and of where accuracy is actually lost.

  * accuracy-loss decomposition by player stratum (centre cut / pre-swing / post-swing / misses+bad cuts)
  * ML per-note centre-accuracy vs observed top-8 mean: map level and note level
  * skill scaling of observed centre accuracy (how the reference skill of the ML maps onto the leaderboard)
Usage: python r01_note_acc.py <replay-study dir> <ai_notes csv.gz> [out tag]
"""
import os, sys, json
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import replays

d, ai_path = sys.argv[1], sys.argv[2]
tag = sys.argv[3] if len(sys.argv) > 3 else "r01"
OUT = os.path.join(os.path.dirname(__file__), "..", "out")
rp, sw, n = replays.load(d)
ai = pd.read_csv(ai_path, dtype={"lb_id": str, "key": str})
print("replays", len(rp), "maps", rp.lb_id.nunique(), "note events", len(n))

# ---- loss decomposition per replay (unweighted note points; combo multipliers ignored)
nn = n[n.scoring_type == replays.SCORING_NORMAL]
g = nn.groupby("score_id")
dec = pd.DataFrame({
    "notes": g.size(),
    "loss_center": g.apply(lambda x: ((15 - x.acc).where(x.event_type == 0, 0)).sum()),
    "loss_pre": g.apply(lambda x: ((70 - x.pre).where(x.event_type == 0, 0)).sum()),
    "loss_post": g.apply(lambda x: ((30 - x.post).where(x.event_type == 0, 0)).sum()),
    "n_miss": g.apply(lambda x: (x.event_type == 2).sum()),
    "n_bad": g.apply(lambda x: (x.event_type == 1).sum()),
}).reset_index()
dec["loss_miss"] = (dec.n_miss + dec.n_bad) * 115
dec = dec.merge(rp[["score_id", "lb_id", "stratum_kind", "pctile", "accuracy"]], on="score_id")
tot = dec[["loss_center", "loss_pre", "loss_post", "loss_miss"]].sum(axis=1)
for c in ["loss_center", "loss_pre", "loss_post", "loss_miss"]:
    dec["sh_" + c[5:]] = dec[c] / tot
dec["acc_band"] = pd.cut(dec.accuracy, [0, 0.8, 0.9, 0.94, 0.96, 0.97, 0.98, 1.0])
print("\nShare of lost note points by cause, per accuracy band (unweighted):")
sh = dec.groupby("acc_band", observed=True)[["sh_center", "sh_pre", "sh_post", "sh_miss"]].mean().round(3)
sh["n"] = dec.groupby("acc_band", observed=True).size()
print(sh.to_string())
dec.to_csv(os.path.join(OUT, f"{tag}_loss_decomposition.csv"), index=False)

# ---- ML vs observed centre accuracy
j = replays.attach_ai(nn[nn.event_type == 0], ai)
j = j.merge(rp[["score_id", "stratum_kind", "pctile", "accuracy"]], on="score_id")
j["obs"] = j.acc / 15
print("\nML note join rate:", j.ai_acc.notna().mean().round(4))
j = j[j.ai_acc.notna()]
mm = j.groupby(["lb_id", "stratum_kind"]).agg(obs=("obs", "mean"), ai=("ai_acc", "mean"), n=("obs", "size")).reset_index()
pv = mm.pivot(index="lb_id", columns="stratum_kind", values=["obs", "ai"])
top = pd.DataFrame({"obs_top": pv[("obs", "top")], "obs_mid": pv[("obs", "mid")] if ("obs", "mid") in pv else np.nan, "ml": pv[("ai", "top")]}).dropna(subset=["obs_top", "ml"])
print(f"map level ({len(top)} maps): corr(ML, top8 mean centre acc) {np.corrcoef(top.ml, top.obs_top)[0,1]:.3f} | mean bias (obs-ML) {np.mean(top.obs_top-top.ml):+.4f} | SD of diff {np.std(top.obs_top-top.ml):.4f}")
print(f"  SD of ML across maps {top.ml.std():.4f}; SD of obs-top8 across maps {top.obs_top.std():.4f}")

# note level: mean over the top-8 replays per note
key = ["lb_id", "tkey", "x", "y", "color"]
notes_top = j[j.stratum_kind == "top"].groupby(key).agg(obs=("obs", "mean"), ai=("ai_acc", "mean"), k=("obs", "size")).reset_index()
notes_top = notes_top[notes_top.k >= 5]
r_all = np.corrcoef(notes_top.obs, notes_top.ai)[0, 1]
# split-half reliability of the observed note means (noise ceiling)
rng = np.random.default_rng(0)
tj = j[j.stratum_kind == "top"].copy()
tj["half"] = tj.groupby(key).cumcount() % 2
h = tj.groupby(key + ["half"]).obs.mean().unstack().dropna()
rel = np.corrcoef(h[0], h[1])[0, 1]; rel_sb = 2 * rel / (1 + rel)
print(f"note level (top8-mean centre acc, {len(notes_top)} notes): corr with ML {r_all:.3f} (R2 {r_all**2:.3f}); split-half reliability of the observed means {rel_sb:.3f} -> ML explains {r_all**2/rel_sb:.2f} of the explainable variance")
within = notes_top.groupby("lb_id").apply(lambda x: np.corrcoef(x.obs, x.ai)[0, 1] if len(x) > 20 else np.nan)
print(f"  within-map corr: mean {within.mean():.3f}  (this is what a per-note graph must get right)")

# ---- skill scaling of centre accuracy: observed mean centre acc vs stratum percentile
print("\nobserved mean centre accuracy (0-1) by leaderboard percentile stratum (top = ranks 1-8):")
bypct = j.groupby("stratum_kind" if False else j.stratum_kind.where(j.stratum_kind == "top", "mid") ).obs.mean()
rp2 = rp.copy(); rp2["pc"] = rp2.pctile.round(2)
jj = j.merge(rp2[["score_id", "pc"]], on="score_id")
print(jj.groupby("pc").obs.agg(["mean", "size"]).round(3).to_string())
top.to_csv(os.path.join(OUT, f"{tag}_map_ml_vs_obs.csv"))
notes_top.to_csv(os.path.join(OUT, f"{tag}_notes_top.csv.gz"), index=False)
