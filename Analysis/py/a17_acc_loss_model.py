"""A17: interpretable bottom-up accuracy model — where on a map accuracy is lost, and why.

Every swing gets an expected point loss (share of the 115 points of its notes) in three components, each a product of named
factors times a skill curve, fitted on replays (Poisson GLMs on one-hot binned factors):
  precision  centre-cut points (15)            swing  pre/post-swing angle points (70 + 30)            misses  misses / bad cuts
  loss_c(swing, s) = exp(b_c + sum_k f_ck(bin_k(swing)) + h_c(s)),  f_ck = 0 at the factor's most common ("plain") level.
Factors are the analyzer's per-swing quantities plus context (time since this hand's last swing, direction change, travel,
crossover, cut direction, lane/layer, pattern, density, NJS, jump distance, minutes into the map, ...).

Validation (song-grouped hold-out): per-swing deviance explained vs gradient boosting on the same inputs; map level vs the
score-implied difficulty; and "where": within-map rank correlation of predicted vs replay-observed loss over sections, against
the analyzer's per-swing difficulty (a pass-style graph) and against the replays' own split-half agreement (ceiling).

Exports Analysis/models/acc_loss_model.json (bins, coefficients, skill curves, percentile table, calibration) and per-map profiles
(time windows: expected loss per component at a base skill, top factors, observed replay loss where available) to a parquet that
export_acc_loss_profiles.py writes into a portaBLe DB.

Usage: python a17_acc_loss_model.py --replays <snap dir> --swings <swings_prod.csv.gz> --skill <player_skill.parquet>
        --maps <map_d.parquet> --mapfile <a15_mapfile_features.parquet> --out <dir> [--rows 3000000]
"""
import argparse, json, os
import numpy as np, pandas as pd, pyarrow.parquet as pq, scipy.sparse as sp
from sklearn.linear_model import PoissonRegressor
from sklearn.ensemble import HistGradientBoostingRegressor

ap = argparse.ArgumentParser()
for a in ["replays", "swings", "skill", "maps", "mapfile", "out"]: ap.add_argument(f"--{a}", required=True)
ap.add_argument("--rows", type=int, default=3_000_000)
ap.add_argument("--model-out", default=os.path.join(os.path.dirname(__file__), "..", "models", "acc_loss_model.json"))
args = ap.parse_args()
os.makedirs(args.out, exist_ok=True)
COMP = ["precision", "swing", "misses"]

# ------------------------------------------------------------------ factors (name, label, column, bin edges or categories)
FACTORS = [
    ("speed", "Fast swings", "swing_speed", [0, 1.5, 2.5, 3.5, 4.5, 5.5, 6.5, 7.5, 9, 11, 14, np.inf]),
    ("hand_gap", "Little time between swings of a hand", "hand_gap", [0, 0.1, 0.13, 0.17, 0.21, 0.26, 0.33, 0.45, 0.7, 1.2, np.inf]),
    ("any_gap", "Dense timing (both hands)", "any_gap", [0, 0.04, 0.08, 0.12, 0.17, 0.25, 0.4, 0.8, np.inf]),
    ("turn", "Direction change from the last swing", "turn", [-0.1, 22.5, 67.5, 112.5, 157.5, 180.1]),
    ("travel", "Hand movement between swings", "travel", [-0.1, 0.5, 1.2, 2.0, 3.0, np.inf]),
    ("angle_strain", "Angle strain", "angle_strain", [-0.1, 0.01, 0.05, 0.15, 0.3, 0.6, np.inf]),
    ("reposition", "Repositioning", "reposition", [-0.1, 0.01, 0.5, 1.0, 1.5, np.inf]),
    ("hit_distance", "Hit distance", "hit_distance", [-0.1, 0.3, 0.5, 0.7, 0.9, 1.2, np.inf]),
    ("cut", "Cut direction", "cut_class", ["vertical", "horizontal", "diagonal", "dot"]),
    ("lane", "Lane", "lane", ["inner", "outer"]),
    ("layer", "Row", "layer", ["bottom", "middle", "top"]),
    ("cross", "Crossover", "cross", ["no", "yes"]),
    ("pattern", "Multi-note pattern", "pattern", ["single", "stack", "slider", "window", "tower", "multi"]),
    ("chain", "Chain head", "is_chain", [0, 1]),
    ("parity", "Parity break (reset)", "parity_error", [0, 1]),
    ("bomb", "Bomb avoidance", "bomb_avoidance", [0, 1]),
    ("wall", "Walls nearby", "wall", [0, 1]),
    ("njs", "Note jump speed", "njs", [0, 10, 13, 16, 19, 22, 26, np.inf]),
    ("jd", "Jump distance", "jd", [0, 16, 19, 22, 25, 28, np.inf]),
    ("density", "Density (swings within 2 s)", "density", [0, 4, 7, 10, 14, 19, 25, np.inf]),
    ("minutes", "Minutes into the map", "minutes", [-0.1, 1, 2, 3, 4.5, 6.5, np.inf]),
]
SKILL_EDGES = [-np.inf, 1.25, 1.75, 2.25, 2.6, 2.9, 3.2, 3.45, 3.7, 3.95, 4.2, np.inf]

def swing_table(path):
    cols = ["lb_id", "swing_i", "seconds", "hand", "x", "y", "cut_direction", "n_cubes", "pattern_type", "is_chain", "njs", "direction",
            "parity_error", "bomb_avoidance", "wall_buff", "swing_speed", "angle_strain", "reposition", "hit_distance", "swing_diff"]
    s = pd.read_csv(path, usecols=cols, dtype={"lb_id": str, "pattern_type": "category"})
    s = s.sort_values(["lb_id", "seconds", "swing_i"], kind="stable").reset_index(drop=True)
    g = s.groupby(["lb_id", "hand"], sort=False)
    s["hand_gap"] = (s.seconds - g.seconds.shift(1)).fillna(9.0)
    d = np.abs(np.mod(s.direction - g.direction.shift(1), 360)); s["turn"] = np.minimum(d, 360 - d).fillna(180)
    s["travel"] = np.hypot(s.x - g.x.shift(1), s.y - g.y.shift(1)).fillna(0)
    s["any_gap"] = (s.seconds - s.groupby("lb_id").seconds.shift(1)).fillna(9.0)
    s["minutes"] = (s.seconds - s.groupby("lb_id").seconds.transform("min")) / 60
    dens = np.zeros(len(s)); t = s.seconds.values
    for _, idx in s.groupby("lb_id", sort=False).indices.items():
        tt = t[idx]; dens[idx] = np.searchsorted(tt, tt + 2.0) - np.searchsorted(tt, tt - 2.0)
    s["density"] = dens
    s["cut_class"] = np.select([s.cut_direction.isin([2, 3]), s.cut_direction.isin([4, 5, 6, 7]), s.cut_direction == 8], ["horizontal", "diagonal", "dot"], "vertical")
    s["lane"] = np.where(s.x.isin([0, 3]), "outer", "inner"); s["layer"] = np.select([s.y == 0, s.y == 2], ["bottom", "top"], "middle")
    s["cross"] = np.where(((s.hand == 0) & (s.x == 3)) | ((s.hand == 1) & (s.x == 0)), "yes", "no")
    pt = s.pattern_type.astype(str)
    s["pattern"] = np.select([pt == "Single", pt == "Stack", pt.str.contains("Slider"), pt.str.contains("Window"), pt == "Tower"],
                             ["single", "stack", "slider", "window", "tower"], "multi")
    s["wall"] = (s.wall_buff > 1).astype(int)
    return s

def is_cat(f): return isinstance(f[3][0], str) or list(f[3]) == [0, 1]

def levels(f):
    spec = f[3]
    return [str(v) for v in spec] if is_cat(f) else [f"{spec[i]:g}-{spec[i + 1]:g}" for i in range(len(spec) - 1)]

def nlevels(f): return len(levels(f))

def codes(df, f):
    """level index of every swing for factor f"""
    v = df[f[2]]
    if is_cat(f): return pd.Categorical(v, categories=list(f[3])).codes.astype(np.int32)
    return np.clip(np.digitize(v.values.astype(float), f[3][1:-1], right=False), 0, len(f[3]) - 2).astype(np.int32)

# ------------------------------------------------------------------ data
cache_sw = os.path.join(args.out, "a17_swings.parquet")
if os.path.exists(cache_sw): S = pd.read_parquet(cache_sw)
else:
    S = swing_table(args.swings)
    S = S.merge(pd.read_parquet(args.mapfile)[["lb_id", "jd"]], on="lb_id", how="left"); S["jd"] = S.jd.fillna(21.0)
    S.to_parquet(cache_sw)
CODES = np.column_stack([codes(S, f) for f in FACTORS])
REF = np.array([np.bincount(CODES[:, k], minlength=nlevels(f)).argmax() for k, f in enumerate(FACTORS)])   # "plain swing" level
print(f"{len(S):,} swings on {S.lb_id.nunique():,} maps; plain swing = " + ", ".join(f"{f[0]} {levels(f)[REF[k]]}" for k, f in enumerate(FACTORS)))

cache_obs = os.path.join(args.out, "a17_obs.parquet")
if os.path.exists(cache_obs): O = pd.read_parquet(cache_obs)
else:
    f = pq.ParquetFile(os.path.join(args.replays, "notes.parquet")); parts = []
    for rg in range(f.num_row_groups):
        n = f.read_row_group(rg, columns=["lb_id", "score_id", "swing_i", "scoring_type", "event_type", "pre", "post", "acc"]).to_pandas()
        n = n[(n.scoring_type == 3) & (n.swing_i >= 0)]
        good = n.event_type == 0
        n["precision"] = np.where(good, (15 - n.acc.clip(upper=15)) / 115, 0.0)
        n["swing"] = np.where(good, (100 - n.pre.clip(upper=70) - n.post.clip(upper=30)) / 115, 0.0)
        n["misses"] = np.where(good, 0.0, 1.0); n["one"] = 1.0
        parts.append(n.groupby(["lb_id", "score_id", "swing_i"], observed=True)[COMP + ["one"]].sum())
    O = pd.concat(parts).groupby(level=[0, 1, 2]).sum().reset_index(); O["lb_id"] = O.lb_id.astype(str)
    O.to_parquet(cache_obs)
R = pd.read_parquet(os.path.join(args.replays, "replays.parquet"), columns=["score_id", "player_id", "modifiers", "stratum_kind"])
R = R[R.modifiers.isna() | R.modifiers.fillna("").isin(["", "IF", "BE"])]
R["player"] = R.player_id.astype(str); R["stratum"] = np.where(R.stratum_kind.astype(str).str.startswith("top"), "top", "mid")
R = R.merge(pd.read_parquet(args.skill)[["player", "skill"]], on="player")
O = O.merge(R[["score_id", "skill", "stratum"]], on="score_id")
row_of = pd.Series(np.arange(len(S)), index=pd.MultiIndex.from_arrays([S.lb_id.values, S.swing_i.values]))
O["row"] = row_of.reindex(pd.MultiIndex.from_arrays([O.lb_id.values, O.swing_i.values])).values
O = O.dropna(subset=["row"]); O["row"] = O.row.astype(np.int64)
song = S.lb_id.str[:-2]
O["song"] = song.values[O.row.values]
print(f"{len(O):,} (replay, swing) observations, {O.score_id.nunique():,} replays, {O.lb_id.nunique():,} maps")

offsets = np.r_[0, np.cumsum([nlevels(f) for f in FACTORS])]
n_feat = offsets[-1]; n_skill = len(SKILL_EDGES) - 1
def design(rows, skill):
    """one-hot factors (reference level dropped) + one-hot skill bins"""
    C = CODES[rows]; n = len(rows)
    cols = (C + offsets[:-1]).ravel(); keep = (C != REF).ravel()
    r = np.repeat(np.arange(n), len(FACTORS))[keep]; cols = cols[keep]
    sk = np.clip(np.digitize(skill, SKILL_EDGES[1:-1]), 0, n_skill - 1) + n_feat
    r = np.r_[r, np.arange(n)]; cols = np.r_[cols, sk]
    return sp.csr_matrix((np.ones(len(r), np.float32), (r, cols)), shape=(n, n_feat + n_skill))

rng = np.random.default_rng(0)
songs = O.song.unique(); test_songs = set(rng.choice(songs, size=len(songs) // 5, replace=False))
O["test"] = O.song.isin(test_songs)
TR = O[~O.test].sample(min(args.rows, (~O.test).sum()), random_state=0); TE = O[O.test].sample(min(args.rows // 3, O.test.sum()), random_state=1)

def fit(df):
    X = design(df.row.values, df.skill.values); w = df.one.values
    models = {}
    for c in COMP:
        m = PoissonRegressor(alpha=1e-7, max_iter=500, tol=1e-6).fit(X, (df[c] / df.one).values, sample_weight=w)
        models[c] = m
    return models
def predict(models, rows, skill):
    X = design(rows, skill)
    return {c: models[c].predict(X) for c in COMP}
def d2(y, mu, w):
    """share of Poisson deviance explained vs the weighted mean"""
    def dev(y, mu): return 2 * np.sum(w * (np.where(y > 0, y * np.log(np.maximum(y, 1e-12) / mu), 0) - (y - mu)))
    return 1 - dev(y, mu) / dev(y, np.full_like(y, np.average(y, weights=w)))

M = fit(TR)
P = predict(M, TE.row.values, TE.skill.values)
print("\nper-swing deviance explained on held-out songs (GLM):", {c: round(d2((TE[c] / TE.one).values, P[c], TE.one.values), 4) for c in COMP},
      "total", round(d2((TE[COMP].sum(axis=1) / TE.one).values, sum(P.values()), TE.one.values), 4))
raw_cols = ["swing_speed", "hand_gap", "any_gap", "turn", "travel", "angle_strain", "reposition", "hit_distance", "x", "y", "hand", "cut_direction",
            "n_cubes", "is_chain", "parity_error", "bomb_avoidance", "wall", "njs", "jd", "density", "minutes"]
Xtr = np.column_stack([S[raw_cols].values[TR.row.values].astype(np.float32), TR.skill.values]); Xte = np.column_stack([S[raw_cols].values[TE.row.values].astype(np.float32), TE.skill.values])
gb = HistGradientBoostingRegressor(loss="poisson", max_iter=400, learning_rate=0.08, max_leaf_nodes=63, min_samples_leaf=200, random_state=0)
gb.fit(Xtr, (TR[COMP].sum(axis=1) / TR.one).values, sample_weight=TR.one.values)
print("gradient boosting (black box, same inputs), total:", round(d2((TE[COMP].sum(axis=1) / TE.one).values, gb.predict(Xte), TE.one.values), 4))

# ------------------------------------------------------------------ map level + "where", held-out songs
mapd = pd.read_parquet(args.maps).set_index("lb_id").d
test_maps = [lb for lb in O.lb_id[O.test].unique() if lb in mapd.index]
rows_t = np.flatnonzero(S.lb_id.isin(test_maps).values)
w_notes = S.n_cubes.clip(lower=1).values[rows_t]
res = []
for s_ in [2.5, 3.0, 3.8]:
    pr = predict(M, rows_t, np.full(len(rows_t), s_)); tot = sum(pr.values())
    acc = 1 - pd.Series(tot * w_notes).groupby(S.lb_id.values[rows_t]).sum() / pd.Series(w_notes).groupby(S.lb_id.values[rows_t]).sum()
    D = np.log((1 - acc).clip(lower=5e-4)) + s_
    j = pd.concat([D.rename("D"), mapd.reindex(D.index).rename("d")], axis=1).dropna()
    b = np.polyfit(j.D, j.d, 1); r2 = 1 - np.var(j.d - np.polyval(b, j.D)) / np.var(j.d)
    res.append((s_, r2, b[0], (j.d - j.D).mean()))
    print(f"map level, held-out maps, skill {s_}: R2 vs score-implied d {r2:.3f} (slope {b[0]:.2f}, mean offset {(j.d - j.D).mean():+.3f})")

def window_ids(lbs, secs):
    """~5 s windows (at least 12 per map)"""
    df = pd.DataFrame({"lb": lbs, "t": secs})
    t0 = df.groupby("lb").t.transform("min"); span = (df.groupby("lb").t.transform("max") - t0).clip(lower=1)
    width = np.maximum(5.0, span / 60)
    return np.floor((df.t - t0) / width).astype(int).values, width.values, t0.values
OT = O[O.test].copy()
OT["win"], _, _ = window_ids(OT.lb_id.values, S.seconds.values[OT.row.values])
OT["loss"] = OT[COMP].sum(axis=1) / OT.one
pr = predict(M, OT.row.values, OT.skill.values); OT["pred"] = sum(pr.values())
OT["sdiff"] = S.swing_diff.values[OT.row.values]
OT["half"] = OT.score_id % 2
out = []
for (lb, st), g in OT.groupby(["lb_id", "stratum"]):
    W = g.groupby("win").agg(obs=("loss", "mean"), pred=("pred", "mean"), sdiff=("sdiff", "mean"), n=("loss", "size"))
    W = W[W.n >= 20]
    if len(W) < 8: continue
    h = g.groupby(["win", "half"]).loss.mean().unstack().reindex(W.index).dropna()
    out.append({"lb": lb, "stratum": st, "model": W.obs.corr(W.pred, method="spearman"), "swing_diff": W.obs.corr(W.sdiff, method="spearman"),
                "split_half": h[0].corr(h[1], method="spearman") if len(h) >= 8 and {0, 1} <= set(h.columns) else np.nan})
Wq = pd.DataFrame(out)
print("\n'where' (within-map Spearman over ~5 s sections, held-out maps), median over maps:")
print(Wq.groupby("stratum")[["model", "swing_diff", "split_half"]].median().round(3).to_string())

# ------------------------------------------------------------------ final fit on everything, calibration, export
M = fit(O.sample(min(args.rows, len(O)), random_state=2))
coef = {c: M[c].coef_ for c in COMP}; icpt = {c: float(M[c].intercept_) for c in COMP}
# calibration: the summed bottom-up loss vs the scores' latent model (log(1 - acc) = d - s) over all maps, per skill bin
allrows = np.flatnonzero(S.lb_id.isin(mapd.index).values); wn = S.n_cubes.clip(lower=1).values[allrows]
skill_mid = [1.0, 1.5, 2.0, 2.45, 2.75, 3.05, 3.33, 3.58, 3.83, 4.07, 4.35]
calib = []
for s_ in skill_mid:
    tot = sum(predict(M, allrows, np.full(len(allrows), s_)).values())
    acc = 1 - pd.Series(tot * wn).groupby(S.lb_id.values[allrows]).sum() / pd.Series(wn).groupby(S.lb_id.values[allrows]).sum()
    dd = mapd.reindex(acc.index)
    calib.append(float(np.mean(dd - s_) - np.mean(np.log((1 - acc).clip(lower=5e-4)))))    # log multiplier to apply to all losses
print("calibration log-multiplier per skill bin (scores vs replays):", np.round(calib, 3))
P_sk = pd.read_parquet(args.skill)
pct = [0.5, 0.75, 0.9, 0.95, 0.98, 0.99, 0.995, 0.999]
spec = {
    "version": 1,
    "description": "Bottom-up expected accuracy loss per swing (Analysis/py/a17_acc_loss_model.py): loss_c = exp(intercept_c + sum of factor "
                   "terms + skill_c[bin]) * calibration[bin], share of the 115 points of the swing's notes; components precision (centre), "
                   "swing (pre/post angle), misses. Fitted on ReplayStudy replays (top-8 and 12th-88th percentile scores, 1 721 maps).",
    "components": COMP,
    "factors": [{"name": f[0], "label": f[1], "column": f[2], "levels": levels(f), "reference": int(REF[k]),
                 "edges": None if is_cat(f) else [None if not np.isfinite(e) else float(e) for e in f[3]],
                 "coef": {c: [0.0 if i == REF[k] else float(coef[c][offsets[k] + i]) for i in range(nlevels(f))] for c in COMP}}
                for k, f in enumerate(FACTORS)],
    "intercept": icpt,
    "skill_edges": [None if not np.isfinite(e) else float(e) for e in SKILL_EDGES],
    "skill_coef": {c: [float(x) for x in coef[c][n_feat:]] for c in COMP},
    "skill_mid": skill_mid, "calibration": calib,
    "percentiles": {f"{p:g}": float(P_sk.skill.quantile(p)) for p in pct},
    "validation": {"map_r2": {f"{s:g}": round(r2, 4) for s, r2, _, _ in res},
                   "where_median_spearman": Wq.groupby("stratum")[["model", "swing_diff", "split_half"]].median().round(3).to_dict("index")},
}
os.makedirs(os.path.dirname(os.path.abspath(args.model_out)), exist_ok=True)
json.dump(spec, open(args.model_out, "w"), indent=1)
print("wrote", os.path.abspath(args.model_out))
print("\nlargest factor effects (multiplier vs the plain swing level):")
for c in COMP:
    eff = [(f[0], levels(f)[i], float(np.exp(coef[c][offsets[k] + i]))) for k, f in enumerate(FACTORS) for i in range(nlevels(f)) if i != REF[k]]
    eff.sort(key=lambda x: -abs(np.log(x[2])))
    print(f"  {c:9s} " + ", ".join(f"{n} {l}: x{m:.2f}" for n, l, m in eff[:8]))
