"""A15b: candidate map-content features (beyond the 76 AccDifficultyFeatures) from the analyzer's per-swing table, and whether they
explain the acc model's disagreement with the scores (song-grouped CV, same target as a15_disagreement.py).

Usage: python a15b_content_features.py --swings <swings_prod.csv.gz> --maps <a15_maps.parquet> [--out <dir>]
Writes <out>/a15_content_features.parquet (per map) and prints the group-wise CV gains and the strongest single features.
"""
import argparse, os
import numpy as np, pandas as pd
from sklearn.linear_model import RidgeCV
from sklearn.model_selection import GroupKFold
from sklearn.preprocessing import StandardScaler
from sklearn.pipeline import make_pipeline

ap = argparse.ArgumentParser()
ap.add_argument("--swings", required=True); ap.add_argument("--maps", required=True)
ap.add_argument("--out", default=None)
args = ap.parse_args()
out = args.out or os.path.dirname(args.maps)
cache = os.path.join(out, "a15_content_features.parquet")

def circ(a):  # absolute circular difference of angles in degrees, 0..180
    d = np.abs(np.mod(a, 360.0)); return np.minimum(d, 360.0 - d)

if os.path.exists(cache):
    C = pd.read_parquet(cache)
else:
    cols = ["lb_id", "bpm_time", "seconds", "hand", "x", "y", "cut_direction", "n_cubes", "pattern_type", "njs", "direction", "forehand",
            "parity_error", "bomb_avoidance", "is_linear", "is_stream", "wall_buff", "entry_x", "entry_y", "exit_x", "exit_y",
            "swing_speed", "low_speed_falloff", "swing_diff", "frequency"]
    s = pd.read_csv(args.swings, usecols=cols, dtype={"lb_id": str, "pattern_type": "category"})
    s = s.sort_values(["lb_id", "seconds", "hand"], kind="stable").reset_index(drop=True)
    print(f"{len(s):,} swings on {s.lb_id.nunique():,} maps")
    lb = s.lb_id.values
    same_lb = np.r_[False, lb[1:] == lb[:-1]]
    # any-hand gaps in beats and seconds (rhythm), and same-hand transitions (flow)
    gap_b = np.where(same_lb, np.r_[np.nan, np.diff(s.bpm_time.values)], np.nan)
    gap_s = np.where(same_lb, np.r_[np.nan, np.diff(s.seconds.values)], np.nan)
    s["gap_b"], s["gap_s"] = gap_b, gap_s
    g4 = gap_b * 4; s["offgrid4"] = np.where(gap_b > 1e-3, np.abs(g4 - np.round(g4)) > 0.1, np.nan)        # not on a 1/4-beat step
    g8 = gap_b * 8; s["offgrid8"] = np.where(gap_b > 1e-3, np.abs(g8 - np.round(g8)) > 0.1, np.nan)        # not even on 1/8 (triplets etc.)
    spb = gap_s / gap_b; med_spb = pd.Series(spb).where(gap_b > 0.05).groupby(lb).transform("median").values
    s["bpm_dev"] = np.where(gap_b > 0.05, np.abs(spb / med_spb - 1) > 0.05, np.nan)                       # local BPM differs from the map's
    h = s.groupby(["lb_id", "hand"], sort=False)
    prev = {c: h[c].shift(1).values for c in ["seconds", "direction", "exit_x", "exit_y", "x", "y"]}
    hg = s.seconds.values - prev["seconds"]
    s["hand_gap"] = hg
    prev_hg = pd.Series(hg).groupby([s.lb_id, s.hand]).shift(1).values
    ratio = hg / prev_hg
    s["tempo_change"] = np.where(np.isfinite(ratio) & (hg < 2) & (prev_hg < 2), (ratio < 0.75) | (ratio > 1.33), np.nan)
    turn = circ(s.direction.values - prev["direction"])
    s["turn_off180"] = np.where(np.isfinite(turn), np.abs(180 - turn), np.nan)                            # 0 = clean up/down alternation
    s["awkward_turn"] = np.where(np.isfinite(turn), turn < 112.5, np.nan)                                  # next swing < 112.5 deg from last
    s["travel"] = np.hypot(s.entry_x - prev["exit_x"], s.entry_y - prev["exit_y"])                         # exit of last -> entry of this
    s["arc"] = np.hypot(s.exit_x - s.entry_x, s.exit_y - s.entry_y)
    s["top_row"] = s.y == 2; s["bottom_row"] = s.y == 0; s["outer"] = s.x.isin([0, 3])
    s["cross"] = ((s.hand == 0) & (s.x == 3)) | ((s.hand == 1) & (s.x == 0))
    s["dot"] = s.cut_direction == 8; s["horizontal"] = s.cut_direction.isin([2, 3]); s["diagonal"] = s.cut_direction.isin([4, 5, 6, 7])
    s["multi"] = s.n_cubes > 1; s["slider"] = s.pattern_type.astype(str).str.contains("Slider"); s["window"] = s.pattern_type.astype(str).str.contains("Window")
    s["wall"] = s.wall_buff > 1; s["slow"] = s.low_speed_falloff < 0.5
    s["long_rest"] = s.hand_gap > 2.0
    agg = s.groupby("lb_id").agg(
        offgrid4=("offgrid4", "mean"), offgrid8=("offgrid8", "mean"), bpm_dev=("bpm_dev", "mean"), tempo_change=("tempo_change", "mean"),
        gap_cv=("gap_s", lambda v: np.nanstd(v[v < 2]) / max(np.nanmean(v[v < 2]), 1e-6)),
        turn_off180=("turn_off180", "mean"), awkward_turn=("awkward_turn", "mean"), travel_mean=("travel", "mean"),
        travel_p90=("travel", lambda v: np.nanquantile(v, 0.9)), arc_mean=("arc", "mean"), short_arc=("arc", lambda v: np.mean(v < 0.6)),
        top_row=("top_row", "mean"), bottom_row=("bottom_row", "mean"), outer=("outer", "mean"), cross=("cross", "mean"),
        dot=("dot", "mean"), horizontal=("horizontal", "mean"), diagonal=("diagonal", "mean"), multi=("multi", "mean"),
        slider=("slider", "mean"), window=("window", "mean"), parity=("parity_error", "mean"), bomb=("bomb_avoidance", "mean"),
        wall=("wall", "mean"), linear=("is_linear", "mean"), stream=("is_stream", "mean"), slow=("slow", "mean"),
        long_rest=("long_rest", "mean"), hand_balance=("hand", lambda v: abs(v.mean() - 0.5)),
        duration=("seconds", lambda v: v.max() - v.min()))
    # density over time: 10 s windows of swing_diff, how uneven (sustained vs spiky) and how long the hard part lasts
    s["win"] = (s.seconds // 10).astype(int)
    W = s.groupby(["lb_id", "win"]).swing_diff.sum().groupby("lb_id")
    agg["density_cv"] = W.std() / W.mean()
    agg["hard_minutes"] = W.apply(lambda v: (v > 0.7 * v.max()).sum() / 6.0)
    C = agg.reset_index()
    C.to_parquet(cache)
    print("wrote", cache)

M = pd.read_parquet(args.maps)
C = C.rename(columns={c: f"{c}_new" for c in C.columns if c != "lb_id" and c in M.columns})   # e.g. hand_balance exists already
M = M.merge(C, on="lb_id", how="inner")
base = [c for c in pd.read_parquet(args.maps).select_dtypes(np.number).columns if c not in
        ("d", "n", "se", "mean_skill", "skill_slope", "d_top25", "n_top25", "d_cv", "e", "year", "log_n") and not c.startswith("grind_")]
for c in sum([["offgrid4", "offgrid8", "bpm_dev", "tempo_change", "gap_cv", "turn_off180", "awkward_turn", "travel_mean", "travel_p90",
               "arc_mean", "short_arc", "density_cv", "hard_minutes"]], []):
    M[c] = M[c].fillna(M[c].median())
groups_by_song = M.hash.str.upper().values
def gcv(cols, y=None, folds=5):
    X = M[cols].values; t = M.d.values if y is None else y
    pred = np.zeros(len(t))
    for trn, tst in GroupKFold(folds).split(X, t, groups_by_song):
        m = make_pipeline(StandardScaler(), RidgeCV(alphas=np.logspace(-2, 4, 25))).fit(X[trn], t[trn]); pred[tst] = m.predict(X[tst])
    return pred
w = (M.n >= 100).values
p0 = gcv(base); r0 = M.d.values - p0
def report(name, cols):
    p = gcv(base + cols); r = M.d.values - p
    print(f"  {name:34s} CV R2 {1 - np.var(r) / np.var(M.d):.4f}  disagreement SD {r[w].std():.4f} (base {r0[w].std():.4f}), "
          f"explains {1 - r[w].var() / r0[w].var():.1%} of it")
    return r
print(f"{len(M)} maps; base: {len(base)} existing features, CV R2 {1 - np.var(r0) / np.var(M.d):.4f}")
G = {"rhythm (offgrid, bpm, tempo, gap cv)": ["offgrid4", "offgrid8", "bpm_dev", "tempo_change", "gap_cv"],
     "flow (turns, travel, arcs)": ["turn_off180", "awkward_turn", "travel_mean", "travel_p90", "arc_mean", "short_arc"],
     "layout (rows, lanes, cross, cut dirs)": ["top_row", "bottom_row", "outer", "cross", "dot", "horizontal", "diagonal", "hand_balance_new"],
     "patterns (multi, sliders, windows...)": ["multi", "slider", "window", "parity", "bomb", "wall", "linear", "stream", "slow"],
     "time (duration, rests, density)": ["duration", "long_rest", "density_cv", "hard_minutes"]}
for k, v in G.items(): report(k, v)
allc = sum(G.values(), [])
r_all = report("all candidates", allc)
# single features: partial correlation with the base model's out-of-fold disagreement
print("\nstrongest single candidates (corr with the base disagreement, maps with >= 100 scores):")
cc = pd.Series({c: np.corrcoef(M.loc[w, c].fillna(M[c].median()), r0[w])[0, 1] for c in allc}).sort_values(key=np.abs, ascending=False)
print(cc.head(15).round(3).to_string())
M["e_new"] = r_all
named = M[M.name.str.contains("My Album|Bruises|Burn|doodle|MariannE|Chrome Vox|OKAY|keep out|Get Get Down|Deja Vu|toromi hearts|Tool-Assisted|Pink Soldiers", regex=True) & w]
print("\nnamed maps, disagreement before -> after (all candidates):")
print(named[["name", "difficulty", "n", "e", "e_new"]].assign(e=lambda x: x.e.round(3), e_new=lambda x: x.e_new.round(3)).sort_values("e").to_string(index=False))
M[["lb_id", "e", "e_new"] + allc].to_parquet(os.path.join(out, "a15b_maps.parquet"))
