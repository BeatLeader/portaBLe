"""A16 (feasibility): a bottom-up accuracy model like pass rating — expected point loss per swing as a function of the swing's
pattern/context and the player's skill, fitted on replays, summed over the map into a predicted accuracy for every skill level.

Target per (replay, swing): share of the 115 points lost on the swing's normal notes (miss / bad cut = all of it).
Features: analyzer per-swing quantities (ReplayStudy swings table, pred_*), layout (cut direction class, lane, layer, crossover),
context (same-hand gap, direction change, travel from the last swing of that hand, local density, position in the map) and skill
(latent model, Analysis/py/latent.py). Model: gradient boosting with a log link, song-grouped 5-fold CV.
Map level: predicted acc A_j(s) for s on a grid; latent model says log(1 - A_j(s)) = d_j - s, so D_j(s) = log(1 - A_j(s)) + s is
compared with the score-implied d_j and with the map-level ridge (fit_acc_model.py) on the same maps.

Usage: python a16_swing_loss.py --replays <snap dir> --skill <player_skill.parquet> --maps <map_d.parquet> --fit <fit_acc_model_maps.csv>
       [--a15 <a15_maps.parquet>] [--max-rows 4000000]
"""
import argparse, os
import numpy as np, pandas as pd, pyarrow.parquet as pq
from sklearn.ensemble import HistGradientBoostingRegressor
from sklearn.model_selection import GroupKFold

ap = argparse.ArgumentParser()
ap.add_argument("--replays", required=True); ap.add_argument("--skill", required=True); ap.add_argument("--maps", required=True)
ap.add_argument("--fit", required=True); ap.add_argument("--a15", default=None); ap.add_argument("--max-rows", type=int, default=4_000_000)
args = ap.parse_args()
out_dir = os.path.dirname(args.skill)
rng = np.random.default_rng(0)

# ---- replays + skill
R = pd.read_parquet(os.path.join(args.replays, "replays.parquet"), columns=["lb_id", "score_id", "player_id", "modifiers", "stratum_kind"])
R = R[R.modifiers.isna() | R.modifiers.fillna("").isin(["", "IF", "BE"])]
R["player"] = R.player_id.astype(str); R["lb_id"] = R.lb_id.astype(str)
R = R.merge(pd.read_parquet(args.skill)[["player", "skill"]], on="player")

# ---- per (replay, swing) loss on normal notes
cache = os.path.join(out_dir, "a16_swing_obs.parquet")
if os.path.exists(cache):
    O = pd.read_parquet(cache)
else:
    f = pq.ParquetFile(os.path.join(args.replays, "notes.parquet")); parts = []
    for rg in range(f.num_row_groups):
        n = f.read_row_group(rg, columns=["lb_id", "score_id", "swing_i", "scoring_type", "event_type", "pre", "post", "acc"]).to_pandas()
        n = n[(n.scoring_type == 3) & (n.swing_i >= 0)]
        n["loss"] = np.where(n.event_type == 0, (115 - n.pre.clip(upper=70) - n.post.clip(upper=30) - n.acc.clip(upper=15)) / 115, 1.0)
        parts.append(n.groupby(["lb_id", "score_id", "swing_i"], observed=True).loss.agg(["sum", "size"]))
    O = pd.concat(parts).groupby(level=[0, 1, 2]).sum().reset_index()
    O["lb_id"] = O.lb_id.astype(str); O.to_parquet(cache)
O = O.merge(R[["score_id", "skill"]], on="score_id")
print(f"{len(O):,} (replay, swing) observations on {O.lb_id.nunique():,} maps, {O.score_id.nunique():,} replays")

# ---- swing features (whole maps, also swings no replay observed) + context
S = pd.read_parquet(os.path.join(args.replays, "swings.parquet"))
S["lb_id"] = S.lb_id.astype(str)
S = S.sort_values(["lb_id", "seconds", "swing_i"]).reset_index(drop=True)
S["horizontal"] = S.cut_direction.isin([2, 3]).astype(int); S["diagonal"] = S.cut_direction.isin([4, 5, 6, 7]).astype(int)
S["cross"] = (((S.hand == 0) & (S.x == 3)) | ((S.hand == 1) & (S.x == 0))).astype(int); S["outer"] = S.x.isin([0, 3]).astype(int)
g = S.groupby(["lb_id", "hand"], sort=False)
S["hand_gap"] = (S.seconds - g.seconds.shift(1)).clip(upper=5).fillna(5)
dd = np.abs(np.mod(S.pred_direction - g.pred_direction.shift(1), 360)); S["turn"] = np.minimum(dd, 360 - dd).fillna(180)
S["travel"] = np.hypot(S.x - g.x.shift(1), S.y - g.y.shift(1)).fillna(0)
S["any_gap"] = (S.seconds - S.groupby("lb_id").seconds.shift(1)).clip(upper=5).fillna(5)
t0 = S.groupby("lb_id").seconds.transform("min"); t1 = S.groupby("lb_id").seconds.transform("max")
S["map_pos"] = (S.seconds - t0) / (t1 - t0).clip(lower=1)
S["minutes_in"] = (S.seconds - t0) / 60
# local density: swings within +-2 s
sec = S.seconds.values; lbv = S.lb_id.values; dens = np.zeros(len(S))
for lb, idx in S.groupby("lb_id", sort=False).indices.items():
    t = sec[idx]; dens[idx] = np.searchsorted(t, t + 2.0) - np.searchsorted(t, t - 2.0)
S["density4s"] = dens
pt = S.pattern_type.astype(str)
for p in ["Stack", "Slider", "Window", "Tower", "Multi"]: S[f"pat_{p.lower()}"] = pt.str.contains(p).astype(int)
pred_cols = [c for c in S.columns if c.startswith("pred_") and S[c].dtype != object]
FEAT = pred_cols + ["x", "y", "hand", "is_dot", "is_chain", "note_count", "njs", "horizontal", "diagonal", "cross", "outer", "hand_gap",
                    "turn", "travel", "any_gap", "map_pos", "minutes_in", "density4s"] + [c for c in S.columns if c.startswith("pat_")]
for c in FEAT: S[c] = pd.to_numeric(S[c], errors="coerce").astype("float32")

D = O.merge(S[["lb_id", "swing_i"] + FEAT], on=["lb_id", "swing_i"])
D["target"] = D["sum"] / D["size"]          # not "y": that is the note layer feature
if len(D) > args.max_rows: D = D.sample(args.max_rows, random_state=0)
# song groups: lb_id = song id + difficulty/mode suffix; the song id is the leading part of the leaderboard id
fitm = pd.read_csv(args.fit, dtype={"lb_id": str})
song_of = dict(zip(fitm.lb_id, fitm.lb_id.str[:-2]))
D["song"] = D.lb_id.map(lambda x: song_of.get(x, x[:-2]))
S["song"] = S.lb_id.map(lambda x: song_of.get(x, x[:-2]))
print(f"training rows {len(D):,}; features {len(FEAT) + 1}; mean loss {np.average(D.target, weights=D['size']):.4f}")

grid = np.array([2.0, 2.5, 3.0, 3.5, 4.17])
songs = D.song.unique(); fold_of = {s: i % 5 for i, s in enumerate(rng.permutation(songs))}
D["fold"] = D.song.map(fold_of); S["fold"] = S.song.map(fold_of)
rows = []
for k in range(5):
    tr = D[D.fold != k]
    m = HistGradientBoostingRegressor(loss="poisson", max_iter=400, learning_rate=0.08, max_leaf_nodes=63, min_samples_leaf=200,
                                      l2_regularization=1.0, random_state=0)
    m.fit(tr[FEAT + ["skill"]].values, tr.target.values, sample_weight=tr["size"].values)
    te = S[(S.fold == k) & S.lb_id.isin(D.lb_id[D.fold == k].unique())]
    X = te[FEAT].values; w = te.note_count.clip(lower=1).values
    for s in grid:
        p = m.predict(np.column_stack([X, np.full(len(X), s)]))
        acc = 1 - pd.Series(p * w).groupby(te.lb_id.values).sum() / pd.Series(w).groupby(te.lb_id.values).sum()
        rows.append(pd.DataFrame({"lb_id": acc.index, "s": s, "acc": acc.values}))
    print(f"  fold {k}: trained on {len(tr):,} rows", flush=True)
A = pd.concat(rows).pivot(index="lb_id", columns="s", values="acc")
A.columns = [f"acc_{c:g}" for c in A.columns]
for c in A.columns: A["D_" + c[4:]] = np.log((1 - A[c]).clip(lower=5e-4)) + float(c[4:])
A = A.reset_index().merge(pd.read_parquet(args.maps), on="lb_id").merge(fitm[["lb_id", "n", "d_hat_cv"]], on="lb_id")
w = A.n >= 100
print(f"\n{w.sum()} replay maps with >= 100 scores (out-of-fold by song for both models):")
for s in ["2.5", "3", "4.17"]:
    x = A.loc[w, f"D_{s}"]; y = A.loc[w, "d"]; b = np.polyfit(x, y, 1); r2 = 1 - np.var(y - np.polyval(b, x)) / np.var(y)
    print(f"  bottom-up, evaluated at skill {s:>4}: R2 vs score-implied d {r2:.4f} (linear calibration slope {b[0]:.2f})")
y = A.loc[w, "d"]; print(f"  map-level ridge (87 features):      R2 {1 - np.var(y - A.loc[w, 'd_hat_cv']) / np.var(y):.4f}")
Z = A[w]; X2 = np.column_stack([np.ones(len(Z)), Z.d_hat_cv, Z["D_3"]]); b, *_ = np.linalg.lstsq(X2, Z.d, rcond=None)
print(f"  ridge + bottom-up (in-sample blend): R2 {1 - np.var(Z.d - X2 @ b) / np.var(Z.d):.4f}, weights ridge {b[1]:.2f}, bottom-up {b[2]:.2f}")
if args.a15:
    E = pd.read_parquet(args.a15)[["lb_id", "name", "difficulty", "e", "skill_slope", "d_top25"]]
    A = A.merge(E, on="lb_id", how="left"); w = A.n >= 100
    bb = np.polyfit(A.loc[w, "D_3"], A.loc[w, "d"], 1); A["e_bottomup"] = A.d - np.polyval(bb, A.D_3)
    A["e_ridge"] = A.d - A.d_hat_cv
    print(f"  corr of the two models' disagreements: {np.corrcoef(A.e_bottomup[w], A.e_ridge[w])[0, 1]:.3f}")
    # shape: does the model's skill dependence match the scores'? (map-specific: how much more top players gain here)
    A["pred_gain"] = (A["D_4.17"] - A["D_2.5"]); A["obs_gain"] = A.d_top25 - A.d
    ok = w & A.obs_gain.notna()
    print(f"  shape: predicted top-vs-mid gain per map vs observed (top-quarter map term - all) r {np.corrcoef(A.pred_gain[ok], A.obs_gain[ok])[0, 1]:.3f}")
    pd.set_option("display.width", 220)
    show = A[w & A.name.fillna("").str.contains("My Album|let you|Burn|OKAY|Chrome Vox|keep out|Deja Vu|toromi hearts|Bruises|Legend of Millennium|Speedcore")]
    print(show[["name", "difficulty", "n", "e_ridge", "e_bottomup", "acc_2.5", "acc_3", "acc_4.17"]].assign(name=lambda x: x.name.str[:24]).round(3).sort_values("e_ridge").to_string(index=False))
A.to_parquet(os.path.join(out_dir, "a16_bottomup_maps.parquet"))
