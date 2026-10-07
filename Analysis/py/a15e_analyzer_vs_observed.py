"""A15e: does the analyzer read the maps that disagree with their scores the way players actually play them?

Per map, from the ReplayStudy swings table (analyzer prediction pred_* next to what the replays show, obs_*): observed saber speed,
resets, repeated directions, transition angles, cut-angle error and misses, each as the excess over what the analyzer's prediction
and the map's predicted difficulty imply; then their relation to the disagreement e (a15_disagreement.py).

Usage: python a15e_analyzer_vs_observed.py --replays <snap dir with swings.parquet> --maps <a15_maps.parquet>
"""
import argparse, os
import numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument("--replays", required=True); ap.add_argument("--maps", required=True)
args = ap.parse_args()
S = pd.read_parquet(os.path.join(args.replays, "swings.parquet")); S["lb_id"] = S.lb_id.astype(str)
S["obs_speed"] = S.obs_saber_speed.where(S.obs_saber_speed < 100)      # a few rows carry unconverted values
S["miss_rate"] = S.obs_misses / S.obs_n; S["same_dir"] = S.obs_same_direction / S.obs_n
g = S.groupby("lb_id")
A = pd.DataFrame({"obs_speed": g.obs_speed.median(), "pred_speed": g.pred_swing_speed.median(), "angle_err": g.obs_angle_error.mean(),
                  "reset_obs": g.obs_reset_frac.mean(), "frame_reset": g.frame_reset.mean(), "parity_pred": g.pred_parity_error.mean(),
                  "trans_angle": g.obs_trans_angle_mean.mean(), "min_speed": g.obs_min_speed_mean.mean(), "miss_rate": g.miss_rate.mean(),
                  "same_dir": g.same_dir.mean(), "pred_freq": g.pred_frequency.mean(), "pred_strain": g.pred_angle_strain.mean()})
J = pd.read_parquet(args.maps)[["lb_id", "name", "difficulty", "n", "d", "d_cv", "e"]].merge(A.reset_index(), on="lb_id")
J = J[J.n >= 100].copy()

def excess(y, xs):
    X = np.column_stack([np.ones(len(J))] + [J[x] for x in xs]); ok = np.isfinite(y) & np.all(np.isfinite(X), axis=1)
    b, *_ = np.linalg.lstsq(X[ok], y[ok], rcond=None); r = np.full(len(J), np.nan); r[ok] = y[ok] - X[ok] @ b
    return r
J["x_speed"] = excess(np.log(J.obs_speed), ["pred_speed", "pred_freq", "d_cv"])
J["x_reset"] = excess(J.reset_obs + 0.0, ["parity_pred", "d_cv"])
J["x_frame_reset"] = excess(J.frame_reset + 0.0, ["parity_pred", "d_cv"])
J["x_angle_err"] = excess(J.angle_err, ["pred_strain", "d_cv"])
J["x_trans"] = excess(J.trans_angle, ["d_cv"])
J["x_min_speed"] = excess(J.min_speed, ["pred_speed", "d_cv"])
J["x_same_dir"] = excess(J.same_dir, ["parity_pred", "d_cv"])
J["x_miss"] = excess(np.log(J.miss_rate + 1e-3), ["d_cv"])
xs = [c for c in J.columns if c.startswith("x_")]
print(f"{len(J)} maps with replays and >= 100 scores")
print("corr with the disagreement e:", {x: round(float(np.corrcoef(J[x].fillna(0), J.e)[0, 1]), 3) for x in xs})
Z = J[xs + ["e"]].dropna(); X = np.column_stack([np.ones(len(Z)), Z[xs]]); b, *_ = np.linalg.lstsq(X, Z.e, rcond=None)
print(f"together: R2 {1 - np.var(Z.e - X @ b) / np.var(Z.e):.1%}")
pd.set_option("display.width", 250)
K = J.sort_values("e")
print(pd.concat([K.head(8), K.tail(8)])[["name", "difficulty", "e"] + xs].assign(name=lambda x: x.name.str[:30]).round(3).to_string(index=False))
J.to_parquet(os.path.join(os.path.dirname(args.maps), "a15e_analyzer_vs_observed.parquet"))
