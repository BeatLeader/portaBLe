"""A15c: how players lose accuracy on the maps where scores and the acc model disagree (replay study notes, ReplayStudy snapshot).

Per map and replay stratum (top = best clean scores, mid = 12th-88th percentile), the error rate 1 - score/max is split into
centre-cut, pre-swing, post-swing and miss/bad-cut parts (normal notes: 15 / 70 / 30 points, a miss loses all 115), plus timing and
cut-direction spread. Each part's log is regressed on the model's out-of-fold predicted difficulty; the residual ("excess") says
which kind of loss a map has more of than its predicted difficulty implies. Then: which excess goes with the disagreement e.

Usage: python a15c_replay_mechanism.py --replays <snap dir with notes.parquet, replays.parquet> --maps <a15_maps.parquet>
       [--content <a15b_maps.parquet>]
"""
import argparse, os
import numpy as np, pandas as pd, pyarrow.parquet as pq

ap = argparse.ArgumentParser()
ap.add_argument("--replays", required=True); ap.add_argument("--maps", required=True); ap.add_argument("--content", default=None)
args = ap.parse_args()

R = pd.read_parquet(os.path.join(args.replays, "replays.parquet"), columns=["lb_id", "score_id", "stratum_kind", "modifiers", "accuracy", "hmd", "pauses"])
R = R[R.modifiers.isna() | R.modifiers.fillna("").isin(["", "IF", "BE"])]
cols = ["lb_id", "score_id", "scoring_type", "event_type", "pre", "post", "acc", "time_dev", "cutdir_dev"]
parts = []
for rg in range(pq.ParquetFile(os.path.join(args.replays, "notes.parquet")).num_row_groups):
    n = pq.ParquetFile(os.path.join(args.replays, "notes.parquet")).read_row_group(rg, columns=cols).to_pandas()
    n = n[n.scoring_type == 3]                                                         # normal notes (chains/arcs score differently)
    good = n.event_type == 0
    n["l_centre"] = np.where(good, (15 - n.acc) / 115, 0); n["l_pre"] = np.where(good, (70 - n.pre) / 115, 0)
    n["l_post"] = np.where(good, (30 - n.post) / 115, 0); n["l_miss"] = np.where(good, 0, 1.0)
    n["td"] = np.where(good, n.time_dev, 0.0); n["td2"] = n.td ** 2; n["cd"] = np.where(good, np.abs(n.cutdir_dev), 0.0)
    n["good"] = good.astype(float); n["one"] = 1.0
    parts.append(n.groupby(["lb_id", "score_id"], observed=True)[["l_centre", "l_pre", "l_post", "l_miss", "td", "td2", "cd", "good", "one"]].sum())
P = pd.concat(parts).groupby(level=[0, 1]).sum()                    # sums, so a replay split across row groups adds up exactly
for c in ["l_centre", "l_pre", "l_post", "l_miss"]: P[c] /= P.one
P["td_sd"] = np.sqrt(np.maximum(P.td2 / P.good - (P.td / P.good) ** 2, 0)); P["cd_mean"] = P.cd / P.good
P = P.reset_index()
P["lb_id"] = P.lb_id.astype(str)
P = P.merge(R, on=["lb_id", "score_id"])
P["stratum"] = np.where(P.stratum_kind.astype(str).str.startswith("top"), "top", "mid")
comp = ["l_centre", "l_pre", "l_post", "l_miss"]
P["l_total"] = P[comp].sum(axis=1)
S = P.groupby(["lb_id", "stratum"])[comp + ["l_total", "td_sd", "cd_mean"]].mean().reset_index()
M = pd.read_parquet(args.maps)[["lb_id", "name", "difficulty", "n", "d", "d_cv", "e"]]
S = S.merge(M, on="lb_id")
print(f"{P.score_id.nunique():,} clean replays on {S.lb_id.nunique():,} maps with a disagreement estimate")

for st in ["top", "mid"]:
    X = S[S.stratum == st].copy()
    print(f"\n== {st} replays ({len(X)} maps): share of the error rate by part: " +
          ", ".join(f"{c[2:]} {X[c].sum() / X.l_total.sum():.1%}" for c in comp))
    out = {}
    for c in comp + ["l_total", "td_sd", "cd_mean"]:
        y = np.log(X[c].clip(lower=1e-5)) if c.startswith("l_") else X[c]
        A = np.column_stack([np.ones(len(X)), X.d_cv, X.d_cv ** 2])
        ok = np.isfinite(y)
        b, *_ = np.linalg.lstsq(A[ok], y[ok], rcond=None)
        X[f"x_{c}"] = y - A @ b                                                         # excess given the predicted difficulty
        out[c] = np.corrcoef(X.loc[ok, f"x_{c}"], X.loc[ok, "e"])[0, 1]
    print("corr of the excess with the disagreement e: " + ", ".join(f"{k[2:] if k.startswith('l_') else k} {v:+.2f}" for k, v in out.items()))
    # joint: how much of e do the excess parts explain together
    xs = [f"x_{c}" for c in comp + ["td_sd", "cd_mean"]]
    Z = X[xs + ["e"]].dropna()
    A = np.column_stack([np.ones(len(Z)), Z[xs].values]); b, *_ = np.linalg.lstsq(A, Z.e.values, rcond=None)
    print(f"excess parts together explain {1 - np.var(Z.e - A @ b) / np.var(Z.e):.1%} of e; coefficients: " +
          ", ".join(f"{k[2:]} {v:+.3f}" for k, v in zip(xs, b[1:])))
    X["e_bin"] = pd.qcut(X.e, 5, labels=["easier ++", "easier", "~", "harder", "harder ++"])
    print("mean excess by disagreement quintile (log units):")
    print(X.groupby("e_bin", observed=True)[[f"x_{c}" for c in comp] + ["x_td_sd"]].mean().round(3).to_string())
    if st == "top":
        Top = X
names = Top[Top.n >= 300].sort_values("e")
show = ["name", "difficulty", "e"] + [f"x_{c}" for c in comp] + ["x_td_sd"]
pd.set_option("display.width", 250)
print("\ntop replays, the most disagreeing maps (excess per loss part):")
print(pd.concat([names.head(10), names.tail(10)])[show].round(2).to_string(index=False))
Top.to_parquet(os.path.join(os.path.dirname(args.maps), "a15c_maps_top.parquet"))
