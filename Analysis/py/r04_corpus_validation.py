"""R4: validate the SwingCorpus-driven analyzer changes (RatingAPI-corpus) against observed player motion.

Compares the production analyzer (parity-error flag, x2 frequency, /2 tech discount, EBPM false-positive halving)
with the corpus analyzer (direction repeat, expectation-weighted frequency/tech via TransitionCosts) on
  T1 reset/roll/alternation ground truth by transition gap
  T2 physical work: tip travel and peak speed of direction-repeating transitions vs alternations at matched gap
  T3 precision cost: centre/pre/post accuracy loss and miss rate of repeats vs alternations at matched gap and skill
  T4 stratum dependence (top-8 vs mid-field players) of the reset/roll behaviour the cost model was fitted on
  T5 dot-note direction error (heuristic vs model)
Usage: python r04_corpus_validation.py <replay-study dir> <ratings dir with swings_{prod,corpus}.csv.gz>
"""
import os, sys, json
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import replays

d, rdir = sys.argv[1], sys.argv[2]
tag = sys.argv[3] if len(sys.argv) > 3 else "r04"
variant = sys.argv[4] if len(sys.argv) > 4 else "corpusdot"
OUT = os.path.join(os.path.dirname(__file__), "..", "out")
rp, sw, n = replays.load(d)
res = {}

def reset_prob(g): return 0.6347 / (1.0 + np.exp(-9.6468 * (g - 0.6349)))
def roll_prob(g):
    g = np.maximum(g, 1e-6); z = np.log(g) + 1.0318
    return 0.5094 * np.exp(-(z * z) / (2 * 0.6462 ** 2))
def tech_mult(g): return 1 + 0.30 * roll_prob(g) + 0.50 * reset_prob(g)

# ---- corpus predictions joined to the replay-study swing table (head cube key)
cs = pd.read_csv(os.path.join(rdir, f"swings_{variant}.csv.gz"), dtype={"lb_id": str},
                 usecols=["lb_id", "swing_i", "seconds", "hand", "x", "y", "direction", "direction_repeat", "transition_gap", "frequency", "swing_tech", "parity_error", "cut_direction"])
cs["tk"] = (cs.seconds * 100).round().astype(int)
sw["tk"] = (sw.seconds * 100).round().astype(int)
cols = ["lb_id", "hand", "x", "y", "tk"]
j = sw.merge(cs.drop_duplicates(cols)[cols + ["direction", "direction_repeat", "transition_gap", "frequency", "swing_tech"]]
             .rename(columns={"direction": "c_direction", "direction_repeat": "c_repeat", "transition_gap": "c_gap", "frequency": "c_frequency", "swing_tech": "c_tech"}),
             on=cols, how="left")
print("swings", len(sw), "joined to corpus variant", j.c_direction.notna().mean().round(4))

# ---- T1: transitions with frame-verified outcome
tr = j[(j.frame_reset + j.frame_roll + j.frame_alternated) > 0].copy()
tr["n_fr"] = tr.frame_reset + tr.frame_roll + tr.frame_alternated
tr["obs_reset"] = tr.frame_reset / tr.n_fr; tr["obs_roll"] = tr.frame_roll / tr.n_fr; tr["obs_alt"] = tr.frame_alternated / tr.n_fr
tr["obs_repeat"] = tr.obs_reset + tr.obs_roll
tr["gap"] = tr.obs_cut_dt_mean
tr["gap_bin"] = pd.cut(tr.gap, [0, 0.1, 0.15, 0.2, 0.3, 0.4, 0.5, 0.65, 0.8, 1.01])
print(f"\nT1: {len(tr)} swings with frame-verified transitions")
t = tr.groupby("gap_bin", observed=True).apply(lambda g: pd.Series({
    "n": len(g), "obs_reset": np.average(g.obs_reset, weights=g.n_fr), "obs_roll": np.average(g.obs_roll, weights=g.n_fr),
    "model_reset": reset_prob(g.gap).mean(), "model_roll": roll_prob(g.gap).mean(),
    "prod_flag_rate": g.pred_parity_error.mean(), "corpus_repeat_rate": g.c_repeat.mean()}), include_groups=False)
t.to_csv(os.path.join(OUT, f"{tag}_T1_by_gap.csv"))
print(t.round(3).to_string())
# confusion of the production parity flag vs observed physical reset
flag = tr.pred_parity_error == 1
# observed outcome mix among swings the corpus analyzer flags as direction repeats (the population its cost model is applied to), by gap
rp_ = tr[tr.c_repeat == 1]
tc = rp_.groupby("gap_bin", observed=True).apply(lambda g: pd.Series({"n": len(g), "obs_reset": np.average(g.obs_reset, weights=g.n_fr), "obs_roll": np.average(g.obs_roll, weights=g.n_fr),
                                                                   "obs_alt": np.average(g.obs_alt, weights=g.n_fr), "model_reset": reset_prob(g.gap).mean(), "model_roll": roll_prob(g.gap).mean()}), include_groups=False)
tc.to_csv(os.path.join(OUT, f"{tag}_T1_repeat_by_gap.csv"))
print("\noutcome mix among corpus-flagged direction repeats by gap:\n", tc.round(3).to_string())
print("\nproduction parity-error flag vs observed physical reset (swing-level, weighted by replays):")
for lab, m in (("flagged", flag), ("not flagged", ~flag)):
    g = tr[m]
    print(f"  {lab:12s} n={len(g):6d}  observed reset {np.average(g.obs_reset, weights=g.n_fr):.3f}  roll {np.average(g.obs_roll, weights=g.n_fr):.3f}  alternation {np.average(g.obs_alt, weights=g.n_fr):.3f}")
res["T1_flagged_reset_rate"] = float(np.average(tr[flag].obs_reset, weights=tr[flag].n_fr)) if flag.any() else None
res["T1_unflagged_reset_rate"] = float(np.average(tr[~flag].obs_reset, weights=tr[~flag].n_fr))
same = tr.cut_direction.between(0, 7)
# identical arrows with the previous same-hand swing (corpus rule)
rep = tr[tr.c_repeat == 1]
print(f"corpus direction-repeat (parity flag OR identical arrows): n={len(rep)}  observed repeat (roll+reset) {np.average(rep.obs_repeat, weights=rep.n_fr):.3f}; "
      f"non-repeat swings observed repeat {np.average(tr[tr.c_repeat != 1].obs_repeat, weights=tr[tr.c_repeat != 1].n_fr):.3f}")
res["T1_corpus_repeat_precision"] = float(np.average(rep.obs_repeat, weights=rep.n_fr))
res["T1_corpus_nonrepeat_repeat_rate"] = float(np.average(tr[tr.c_repeat != 1].obs_repeat, weights=tr[tr.c_repeat != 1].n_fr))
# calibration of the expectation: sum predicted vs observed resets among corpus repeats
print(f"expected resets (corpus model) {reset_prob(rep.gap).sum():.0f} vs observed {np.sum(rep.obs_reset):.0f}; "
      f"prod model (x2 on every flagged swing) would predict {flag.sum()} resets among flagged vs observed {np.sum(tr[flag].obs_reset):.0f}")

# ---- T2/T3: per-note physical + precision cost, repeats vs alternations at matched gap (from notes_obs)
nn = n[(n.event_type == 0) & (n.scoring_type == replays.SCORING_NORMAL) & (n.swing_i >= 0) & n.gap.between(0.05, 1.0)].copy()
nn = nn.merge(rp[["score_id", "stratum_kind", "accuracy"]], on="score_id")
sjoin = j[["lb_id", "swing_i", "pred_parity_error", "c_repeat", "cut_direction"]]
nn = nn.merge(sjoin, on=["lb_id", "swing_i"], how="left")
# observed repeat per note: angle between this and previous cut of the same hand is unknown here -> use swing-level class
# per swing, majority class from frame data
cls = tr.set_index(["lb_id", "swing_i"])[["obs_reset", "obs_roll", "obs_alt"]]
nn = nn.join(cls, on=["lb_id", "swing_i"], how="inner")
nn["cls"] = np.where(nn.obs_reset >= 0.5, "reset", np.where(nn.obs_roll >= 0.5, "roll", np.where(nn.obs_alt >= 0.5, "alt", "mixed")))
nn["gap_bin"] = pd.cut(nn.gap, [0.05, 0.15, 0.25, 0.35, 0.5, 0.7, 1.0])
nn["loss_center"] = 15 - nn.acc; nn["loss_pre"] = 70 - nn.pre; nn["loss_post"] = 30 - nn.post
print("\nT2/T3: per-note physical and precision outcomes by observed class and gap (top-8 stratum only)")
tt = nn[nn.stratum_kind == "top"]
pv = tt.pivot_table(index="gap_bin", columns="cls", values=["tip_len", "tip_peak", "loss_center"], aggfunc="mean", observed=True)
print(pv.round(3).to_string())
ct = tt.groupby(["gap_bin", "cls"], observed=True).size().unstack()
print("counts:\n", ct.to_string())
pv.to_csv(os.path.join(OUT, f"{tag}_T2_by_gap.csv"))
ratio = (pv["tip_len"]["reset"] / pv["tip_len"]["alt"]).round(2) if "reset" in pv["tip_len"] else None
print("tip-travel ratio reset/alternation by gap:", ratio.to_dict() if ratio is not None else None)
roll_ratio = (pv["tip_len"]["roll"] / pv["tip_len"]["alt"]).round(2) if "roll" in pv["tip_len"] else None
print("tip-travel ratio roll/alternation by gap:", roll_ratio.to_dict() if roll_ratio is not None else None)
print("model frequency multipliers: prod x2 for every flagged swing; corpus 1+P(reset|gap) =", {str(k): round(float(1 + reset_prob(k.mid)), 2) for k in pv.index})
res["T2_tip_ratio_reset"] = {str(k): v for k, v in ratio.to_dict().items()} if ratio is not None else None
res["T2_tip_ratio_roll"] = {str(k): v for k, v in roll_ratio.to_dict().items()} if roll_ratio is not None else None

# precision cost relative to alternation (loss ratio) at matched gap, with a skill control (replay accuracy)
tt = tt.assign(sk=pd.cut(tt.accuracy, [0, 0.95, 0.97, 0.98, 1.0]))
base = tt[tt.cls == "alt"].groupby(["gap_bin", "sk"], observed=True)[["loss_center", "loss_pre", "loss_post"]].mean()
out = {}
for c in ("roll", "reset"):
    g = tt[tt.cls == c]
    if len(g) < 50: continue
    m = g.groupby(["gap_bin", "sk"], observed=True)[["loss_center", "loss_pre", "loss_post"]].agg(["mean", "size"])
    w = m[("loss_center", "size")]
    num = (m[("loss_center", "mean")] + m[("loss_pre", "mean")] + m[("loss_post", "mean")])
    den = (base.loss_center + base.loss_pre + base.loss_post).reindex(num.index)
    ok = den.notna() & (w >= 20)
    out[c] = float(np.average(num[ok] / den[ok], weights=w[ok])) if ok.any() else np.nan
    cen = float(np.average(m[("loss_center", "mean")][ok] / base.loss_center.reindex(num.index)[ok], weights=w[ok])) if ok.any() else np.nan
    print(f"  {c}: swing precision loss (centre+pre+post points) vs alternation at matched gap/skill: x{out[c]:.2f} (centre only x{cen:.2f}); claimed cut-precision degradation 25-30% (roll), ~50% (reset)")
res["T3_precision_loss_ratio"] = out

# miss/bad cut rates need the per-swing counts: obs_misses/obs_bad_cuts over obs_n
mm = j.merge(tr[["lb_id", "swing_i", "obs_reset", "obs_roll", "obs_alt"]], on=["lb_id", "swing_i"], how="inner")
mm["cls"] = np.where(mm.obs_reset >= 0.5, "reset", np.where(mm.obs_roll >= 0.5, "roll", np.where(mm.obs_alt >= 0.5, "alt", "mixed")))
mm["bad"] = (mm.obs_misses + mm.obs_bad_cuts) / (mm.obs_n + mm.obs_misses + mm.obs_bad_cuts).clip(lower=1)
mm["gap_bin"] = pd.cut(mm.obs_cut_dt_mean, [0.05, 0.15, 0.25, 0.35, 0.5, 0.7, 1.0])
r = mm.pivot_table(index="gap_bin", columns="cls", values="bad", aggfunc="mean", observed=True)
print("\nmiss+bad-cut rate by observed class and gap:\n", (r * 100).round(2).to_string())
r.to_csv(os.path.join(OUT, f"{tag}_T3_miss_by_gap.csv"))

# ---- T4: stratum dependence of reset / roll probabilities (swing-level, by replay stratum needs per-replay data -> use notes_obs gap + tip metrics)
nn2 = n[(n.event_type == 0) & (n.scoring_type == replays.SCORING_NORMAL) & (n.swing_i >= 0) & n.gap.between(0.05, 1.0)].merge(rp[["score_id", "stratum_kind"]], on="score_id")
nn2 = nn2.merge(sjoin, on=["lb_id", "swing_i"], how="left")
rep2 = nn2[nn2.c_repeat == 1].copy()
rep2["two_turn"] = (rep2.tip_turn >= 2)
rep2["gap_bin"] = pd.cut(rep2.gap, [0.05, 0.15, 0.25, 0.4, 0.6, 0.8, 1.0])
t4 = rep2.pivot_table(index="gap_bin", columns="stratum_kind", values="two_turn", aggfunc="mean", observed=True)
print("\nT4: share of corpus-flagged direction repeats executed with >=2 arm turnarounds (physical reset proxy), by player stratum:\n", t4.round(3).to_string())
res["T4_two_turn_by_stratum"] = {str(c): {str(k): v for k, v in col.items()} for c, col in t4.round(3).to_dict().items()}

# ---- T6: production EBPM false-positive halving: are FP-flagged parity errors really alternations?
fp = tr[tr.pred_parity_error == 1]
for lab, m in (("FP-flagged (frequency halved)", fp.pred_false_positive == 1), ("kept as resets (frequency x2)", fp.pred_false_positive == 0)):
    g = fp[m]
    if len(g): print(f"T6: {lab:34s} n={len(g):5d}  observed reset {np.average(g.obs_reset, weights=g.n_fr):.3f} roll {np.average(g.obs_roll, weights=g.n_fr):.3f} alternation {np.average(g.obs_alt, weights=g.n_fr):.3f}")
res["T6_fp_alt_rate"] = float(np.average(fp[fp.pred_false_positive == 1].obs_alt, weights=fp[fp.pred_false_positive == 1].n_fr)) if (fp.pred_false_positive == 1).any() else None
res["T6_kept_alt_rate"] = float(np.average(fp[fp.pred_false_positive == 0].obs_alt, weights=fp[fp.pred_false_positive == 0].n_fr)) if (fp.pred_false_positive == 0).any() else None

# ---- T5: dot-note direction error
dots = j[(j.is_dot == 1) & (j.note_count == 1) & j.obs_angle_mean.notna() & j.c_direction.notna()].copy()
def adiff(a, b): x = np.abs(a - b) % 360; return np.where(x > 180, 360 - x, x)
dots["err_prod"] = adiff(dots.obs_angle_mean, dots.pred_direction); dots["err_corpus_nomodel"] = np.nan
dots["err_corpus"] = adiff(dots.obs_angle_mean, dots.c_direction)
print(f"\nT5: single dot notes with observed direction: n={len(dots)}")
print(f"  production heuristic error: median {dots.err_prod.median():.1f} deg, mean {dots.err_prod.mean():.1f}")
print(f"  corpus (analyzer + ONNX model when active) error: median {dots.err_corpus.median():.1f} deg, mean {dots.err_corpus.mean():.1f}  (NB model was trained on ranked-pool replays: in-sample on these maps)")
res["T5_dot_error_prod_median"] = float(dots.err_prod.median()); res["T5_dot_error_corpus_median"] = float(dots.err_corpus.median())
nd = j[(j.is_dot == 0) & j.obs_angle_mean.notna() & j.c_direction.notna()]
print(f"  non-dot swings (sanity): prod median {adiff(nd.obs_angle_mean, nd.pred_direction).__array__().__class__ and np.median(adiff(nd.obs_angle_mean, nd.pred_direction)):.1f}, corpus {np.median(adiff(nd.obs_angle_mean, nd.c_direction)):.1f}")
json.dump(res, open(os.path.join(OUT, f"{tag}.json"), "w"), indent=1, default=float)
