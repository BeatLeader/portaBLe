"""A21: improving the analyzer's pass rating against pass difficulty measured from attempts (a19 Rasch model), keeping it an
algorithm (per-swing difficulty -> aggregation) with a few named constants.

Variants (on the analyzer's per-swing table; passlib.py reproduces production exactly up to the analyzer's own tie order):
  base     production: SwingDiff, peak rolling means over 8..128 swings, equal weights
  windows  weights over 8..512-swing windows (how much bursts vs sustained sections should count)
  swing    per-swing multipliers: crossover, horizontal cut, diagonal, parity break, and the stress (tech) scale inside SwingDiff
  both     swing + windows
Score: R2 of a cubic fit of the rating to the measured pass difficulty b (logit fail rate of an average player); constants are fitted
on half of the songs and scored on the other half (and vice versa). The fitted benchmark (ridge on 87 map features) is printed for
comparison.

Usage: python a21_pass_rating.py --swings <swings_pass.parquet from passlib.load_swings> --pass-maps <a19_pass_maps.parquet> --out <dir>
"""
import argparse, json, os
import numpy as np, pandas as pd
from scipy.optimize import minimize

ap = argparse.ArgumentParser()
ap.add_argument("--swings", required=True); ap.add_argument("--pass-maps", required=True); ap.add_argument("--out", required=True)
args = ap.parse_args()
S = pd.read_parquet(args.swings)
PM = pd.read_parquet(args.pass_maps).set_index("lb_id")
S = S[S.lb_id.isin(PM.index)].sort_values(["lb_id", "seconds", "hand"], kind="stable").reset_index(drop=True)
lbs = S.lb_id.unique(); PM = PM.loc[lbs]
b = PM.b.values
song = PM.Hash.str.upper().values
half = np.array([int(s[:6], 16) % 2 for s in song])                  # deterministic song split
starts = np.r_[0, np.flatnonzero(S.lb_id.values[1:] != S.lb_id.values[:-1]) + 1, len(S)]

speed = (S.swing_speed * S.low_speed_falloff).values                 # speed part of SwingDiff
stress = S.stress.values
other = (S.njs_buff * np.where(S.is_stream == 1, 1.05, 1.0) * S.wall_buff).values
hand = S.hand.values
cross = (((S.hand == 0) & (S.x == 3)) | ((S.hand == 1) & (S.x == 0))).values.astype(float)
horiz = S.cut_direction.isin([2, 3]).values.astype(float)
diag = S.cut_direction.isin([4, 5, 6, 7]).values.astype(float)
parity = S.parity_error.values.astype(float)
WIN = np.array([8, 16, 32, 64, 128, 256, 512])

def swing_diff(p):
    k, cc, ch, cd, cp = p
    sm = 2.0 * k * stress / (k * stress + 2.0) + 1.0
    return speed * sm * other * (1 + cc * cross) * (1 + ch * horiz) * (1 + cd * diag) * (1 + cp * parity)

# segments for vectorized peak rolling means: maps (both hands) and (map, hand) runs in time order
mapid = np.repeat(np.arange(len(starts) - 1), np.diff(starts))
perm = np.lexsort((hand, mapid))                                      # stable: time order kept within (map, hand)
key = mapid[perm] * 2 + hand[perm]
hstarts = np.r_[0, np.flatnonzero(key[1:] != key[:-1]) + 1, len(perm)]
hseg_map, hseg_hand = mapid[perm][hstarts[:-1]], hand[perm][hstarts[:-1]]

POS = np.arange(len(S)) - np.repeat(starts[:-1], np.diff(starts))       # position within the map
HPOS = np.arange(len(S)) - np.repeat(hstarts[:-1], np.diff(hstarts))    # position within the (map, hand) run

def seg_peaks(c, pos, st, w):
    """peak over each segment of the rolling mean of the last w values, evaluated from index w on (Difficulty.CalcAverage);
    c = cumulative sum with a leading 0, pos = position within the segment"""
    idx = np.arange(len(pos))
    means = np.where(pos >= w, (c[idx + 1] - c[np.maximum(idx + 1 - w, 0)]) / w, 0.0)
    return np.maximum.reduceat(means, st[:-1])

def all_peaks(v):
    out = np.zeros((len(starts) - 1, 3, len(WIN)))
    c = np.concatenate([[0.0], np.cumsum(v)]); ch = np.concatenate([[0.0], np.cumsum(v[perm])])
    for j, w in enumerate(WIN):
        out[:, 0, j] = seg_peaks(c, POS, starts, w)
        hp = seg_peaks(ch, HPOS, hstarts, w // 2)
        for r, hv in ((1, 0), (2, 1)):
            sel = hseg_hand == hv
            out[hseg_map[sel], r, j] = hp[sel]
    return out

def rating(P, wts):
    wts = np.asarray(wts) / np.sum(wts)
    comb, red, blue = (P[:, r, :] @ wts for r in range(3))
    easier, harder = np.minimum(red, blue), np.maximum(red, blue)
    ratio = np.minimum(easier / np.maximum(np.minimum(harder, comb), 1e-4), 1.0)
    return comb * (1 - (1 - ratio) * 0.5) * 0.825

def r2(x, y):
    c = np.polyfit(x, y, 3); return 1 - np.var(y - np.polyval(c, x)) / np.var(y)

BASE_P = [1.0, 0, 0, 0, 0]
BASE_W = [1, 1, 1, 1, 1, 0, 0]
P0 = all_peaks(swing_diff(BASE_P))
print(f"{len(lbs)} maps; production pass rating R2 {r2(rating(P0, BASE_W), b):.4f}")

def fit(kind, mask):
    """fit constants on maps in mask; returns (swing params, window weights)"""
    def unpack(x):
        sp, w = list(BASE_P), list(BASE_W)
        if kind in ("swing", "both"): sp = [np.exp(x[0]), x[1], x[2], x[3], x[4]]
        if kind in ("windows", "both"): w = list(np.exp(x[-7:]))
        return sp, w
    cache = {}
    def loss(x):
        sp, w = unpack(x)
        key = tuple(np.round(sp, 6))
        if key not in cache: cache.clear(); cache[key] = all_peaks(swing_diff(sp))
        R = rating(cache[key], w)
        return -r2(R[mask], b[mask])
    x0 = ([0.0, 0, 0, 0, 0] if kind in ("swing", "both") else []) + ([0.0] * 5 + [-3.0, -3.0] if kind in ("windows", "both") else [])
    # explicit initial simplex: Nelder-Mead's default steps are tiny around zero and never leave the production constants
    steps = ([0.5, 0.5, 0.5, 0.3, 0.5] if kind in ("swing", "both") else []) + ([1.0] * 7 if kind in ("windows", "both") else [])
    sim = np.vstack([x0] + [np.array(x0) + np.eye(len(x0))[i] * st for i, st in enumerate(steps)])
    res = minimize(loss, x0, method="Nelder-Mead", options={"maxiter": 400 if kind != "both" else 700, "xatol": 1e-3, "fatol": 1e-5, "initial_simplex": sim})
    return unpack(res.x)

results = {}
for kind in ["windows", "swing", "both"]:
    scores = []
    for h in (0,):                                                   # fit on one half of the songs, score on the other
        sp, w = fit(kind, half == h)
        R = rating(all_peaks(swing_diff(sp)), w)
        scores.append(r2(R[half != h], b[half != h]))
    sp, w = fit(kind, np.ones(len(b), bool))
    R = rating(all_peaks(swing_diff(sp)), w)
    results[kind] = {"holdout_r2": float(np.mean(scores)), "all_r2": float(r2(R, b)), "swing_params": [float(x) for x in sp],
                     "window_weights": dict(zip(map(int, WIN), [float(x) for x in np.asarray(w) / np.sum(w)]))}
    print(f"{kind:8s} held-out R2 {np.mean(scores):.4f} (fit on all {r2(R, b):.4f}); stress scale {sp[0]:.2f}, crossover {sp[1]:+.2f}, "
          f"horizontal {sp[2]:+.2f}, diagonal {sp[3]:+.2f}, parity {sp[4]:+.2f}; window weights " +
          ", ".join(f"{int(a)}:{x:.2f}" for a, x in zip(WIN, np.asarray(w) / np.sum(w))), flush=True)
    PM[f"rating_{kind}"] = R
PM["rating_base"] = rating(P0, BASE_W)
# outliers before / after (residual of the cubic fit, logit units)
for k in ["base", "both"]:
    c = np.polyfit(PM[f"rating_{k}"], PM.b, 3); PM[f"res_{k}"] = PM.b - np.polyval(c, PM[f"rating_{k}"])
print(f"\nmaps off by > 1 logit: base {(PM.res_base.abs() > 1).sum()}, both {(PM.res_both.abs() > 1).sum()}; > 2: {(PM.res_base.abs() > 2).sum()} -> {(PM.res_both.abs() > 2).sum()}")
pd.set_option("display.width", 220)
cols = ["Name", "DifficultyName", "pass", "rating_both", "b", "res_base", "res_both"]
O = PM.assign(Name=PM.Name.str[:26]).sort_values("res_base")
print(pd.concat([O.head(8), O.tail(8)])[cols].round(2).to_string())
PM.reset_index().to_parquet(os.path.join(args.out, "a21_pass_variants.parquet"))
json.dump(results, open(os.path.join(args.out, "a21_results.json"), "w"), indent=1)
