"""A18: do players' timing and per-hand accuracy explain why some maps score worse (or better) than the acc model predicts?

The attempts export carries, per attempt, the mean cut score per hand (AccLeft / AccRight, of 115) and the mean timing deviation per
hand (LeftTiming / RightTiming, seconds; filled from 2024-25 on). For clean clears: each player's timing on a map relative to their
own average timing (skill and hardware removed), averaged per map, against the acc model's out-of-fold disagreement e
(a15_disagreement.py). If maps with poor sync / hard-to-follow rhythm lose accuracy, timing should rise with e.

Usage: python a18_attempts_timing.py --attempts <dir with attempts-*.parquet> --maps <a15_maps.parquet> [--content <a15b_maps.parquet>]
"""
import argparse, glob, os
import numpy as np, pandas as pd, pyarrow.parquet as pq

ap = argparse.ArgumentParser()
ap.add_argument("--attempts", required=True); ap.add_argument("--maps", required=True); ap.add_argument("--content", default=None)
args = ap.parse_args()
cols = ["PlayerId", "LeaderboardId", "Type", "Modifiers", "Accuracy", "AccLeft", "AccRight", "LeftTiming", "RightTiming"]
parts = []
for f in sorted(glob.glob(os.path.join(args.attempts, "attempts-*.parquet"))):
    t = pq.read_table(f, columns=cols).to_pandas()
    t = t[(t.Type == 1) & (t.LeftTiming > 0) & (t.RightTiming > 0) & t.Modifiers.fillna("").isin(["", "IF", "BE", "IF,BE", "BE,IF"])]
    parts.append(t)
A = pd.concat(parts, ignore_index=True)
A["timing"] = (A.LeftTiming + A.RightTiming) / 2
A["hand_gap"] = A.AccLeft - A.AccRight
A["log_err"] = np.log(np.clip(1 - A.Accuracy, 5e-4, 1))
# per player: their own average timing / error over all maps -> relative values per attempt
g = A.groupby("PlayerId")
A["timing_rel"] = np.log(A.timing) - g.timing.transform(lambda v: np.log(v).mean())
A["err_rel"] = A.log_err - g.log_err.transform("mean")
A = A[g.timing.transform("size") >= 20]
print(f"{len(A):,} clean clears with timing by {A.PlayerId.nunique():,} players on {A.LeaderboardId.nunique():,} maps")
print(f"within player: corr(log timing, log error) over attempts {np.corrcoef(A.timing_rel, A.err_rel)[0, 1]:.3f}")
Mp = A.groupby("LeaderboardId").agg(timing_rel=("timing_rel", "mean"), err_rel=("err_rel", "mean"), n=("timing_rel", "size"),
                                    timing_ms=("timing", lambda v: 1000 * v.median()), hand_gap=("hand_gap", "mean"))
M = pd.read_parquet(args.maps)[["lb_id", "name", "difficulty", "n", "d", "d_cv", "e"]].rename(columns={"n": "n_scores"})
J = M.merge(Mp.reset_index().rename(columns={"LeaderboardId": "lb_id"}), on="lb_id")
J = J[(J.n >= 50) & (J.n_scores >= 100)]
print(f"{len(J)} maps with >= 50 timed clears")
# timing relative to what the map's predicted difficulty implies (harder maps are hit with worse timing anyway)
X = np.column_stack([np.ones(len(J)), J.d_cv, J.d_cv ** 2]); b, *_ = np.linalg.lstsq(X, J.timing_rel, rcond=None)
J["timing_excess"] = J.timing_rel - X @ b
r = np.corrcoef(J.timing_excess, J.e)[0, 1]
print(f"timing (players vs their own average) beyond the predicted difficulty vs disagreement e: r {r:+.3f}  (R2 {r * r:.1%})")
print(f"  ... raw timing vs e r {np.corrcoef(J.timing_rel, J.e)[0, 1]:+.3f}; vs d_cv r {np.corrcoef(J.timing_rel, J.d_cv)[0, 1]:+.3f}")
print(f"left-right cut score gap vs e r {np.corrcoef(J.hand_gap, J.e)[0, 1]:+.3f}")
J["q"] = pd.qcut(J.e, 5, labels=["easier ++", "easier", "~", "harder", "harder ++"])
print(J.groupby("q", observed=True)[["timing_excess", "timing_ms", "e"]].mean().round(4).to_string())
if args.content:
    C = pd.read_parquet(args.content)
    K = J.merge(C, on="lb_id", how="left", suffixes=("", "_c"))
    for c in ["offgrid4", "offgrid8", "tempo_change", "bpm_dev", "gap_cv"]:
        if c in K: print(f"  rhythm feature {c:13s} vs timing_excess r {np.corrcoef(K[c].fillna(K[c].median()), K.timing_excess)[0, 1]:+.3f}")
pd.set_option("display.width", 220)
show = J.sort_values("e")
print(pd.concat([show.head(8), show.tail(8)])[["name", "difficulty", "e", "timing_ms", "timing_excess", "n"]].assign(name=lambda x: x.name.str[:28]).round(3).to_string(index=False))
J.to_parquet(os.path.join(os.path.dirname(args.maps), "a18_timing_maps.parquet"))
