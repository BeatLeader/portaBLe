"""Pass rating building blocks on the analyzer's per-swing table (RatingsDump swings_<tag>.csv.gz), mirroring
beatleader-analyzer AnalyzeMap / Difficulty.CalcAverage exactly, plus the variants studied in a21_pass_rating.py.

Production (analyzer 0d47de6): pass = 0.825 * hands_nerf * mean over w in {8,16,32,64,128} of the peak rolling mean of SwingDiff
over w swings (both hands; per hand with w/2 for the one-saber nerf).
"""
import numpy as np, pandas as pd

WINDOWS = (8, 16, 32, 64, 128)
CAL = 0.825

def peak_rolling(v, w):
    """Difficulty.CalcAverage(..., w).Max(): mean of the last w values, evaluated from index w on (0 when there are < 2 swings)"""
    n = len(v)
    if n < 2: return 0.0
    if n <= w: return 0.0
    c = np.cumsum(np.insert(v, 0, 0.0))
    means = (c[w + 1:] - c[1:n - w + 1]) / w          # windows ending at i = w .. n-1 hold values i-w+1 .. i
    return float(means.max())

def pass_rating(diff, hand, windows=WINDOWS, weights=None):
    """diff: per-swing difficulty in time order (both hands), hand: 0/1 per swing"""
    weights = np.ones(len(windows)) / len(windows) if weights is None else np.asarray(weights) / np.sum(weights)
    comb = sum(wt * peak_rolling(diff, w) for w, wt in zip(windows, weights))
    red = sum(wt * peak_rolling(diff[hand == 0], w // 2) for w, wt in zip(windows, weights)) if (hand == 0).sum() > 1 else 0.0
    blue = sum(wt * peak_rolling(diff[hand == 1], w // 2) for w, wt in zip(windows, weights)) if (hand == 1).sum() > 1 else 0.0
    easier, harder = min(red, blue), max(red, blue)
    ratio = min(easier / max(min(harder, comb), 1e-4), 1.0) if (harder > 0 or comb > 0) else 1.0
    return comb * (1.0 - (1.0 - ratio) * 0.5) * CAL

def load_swings(path, lbs=None):
    cols = ["lb_id", "swing_i", "seconds", "bpm_time", "hand", "x", "y", "cut_direction", "n_cubes", "pattern_type", "direction", "parity_error",
            "frequency", "hit_distance", "angle_strain", "reposition", "rotation", "swing_speed", "stress", "low_speed_falloff", "stress_mult",
            "njs_buff", "wall_buff", "is_stream", "swing_diff", "bomb_avoidance"]
    s = pd.read_csv(path, usecols=cols, dtype={"lb_id": str, "pattern_type": "category"})
    if lbs is not None: s = s[s.lb_id.isin(lbs)]
    return s.sort_values(["lb_id", "seconds", "swing_i"], kind="stable").reset_index(drop=True)
