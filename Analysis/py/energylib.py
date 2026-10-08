"""Energy-bar pass model (analyzer PassEnergy) in numpy: per-swing pass difficulty, notes matrix, exact clear probability, skill50.

Game rules: energy starts at 0.5, +0.01 per hit note, -0.15 per missed note, fail at 0, capped at 1 (1 % steps, index 0..100).
Every note is missed with probability sigmoid(slope * (ln d - skill)).
"""
import numpy as np, pandas as pd

V2 = dict(stress_scale=6.52, crossover=0.59, horizontal=0.37, diagonal=0.29, parity=-0.14, slope=2.5, scale=0.2332, one_saber=0.74)

def pass_diff(S, p=V2):
    """PassDiff per swing from the analyzer's per-swing table (swing_speed, low_speed_falloff, stress, njs_buff, is_stream, wall_buff, ...)"""
    k = p["stress_scale"]
    sm = 2.0 * k * S.stress.values / (k * S.stress.values + 2.0) + 1.0
    cross = (((S.hand == 0) & (S.x == 3)) | ((S.hand == 1) & (S.x == 0))).values
    return ((S.swing_speed * S.low_speed_falloff).values * sm * (S.njs_buff * np.where(S.is_stream == 1, 1.05, 1.0) * S.wall_buff).values
            * (1 + p["crossover"] * cross) * (1 + p["horizontal"] * S.cut_direction.isin([2, 3]).values)
            * (1 + p["diagonal"] * S.cut_direction.isin([4, 5, 6, 7]).values) * (1 + p["parity"] * S.parity_error.values))

def note_matrix(logd, notes, map_codes):
    """(maps x notes) matrix of ln d per note (each swing repeated for its notes, 1..4) and the active mask"""
    rep = np.repeat(np.arange(len(logd)), notes)
    mid = map_codes[rep]; ld = logd[rep]
    cnt = np.bincount(mid); L = cnt.max()
    pos = np.arange(len(rep)) - np.repeat(np.r_[0, np.cumsum(cnt)[:-1]], cnt)
    LD = np.full((len(cnt), L), -20.0, np.float32); LD[mid, pos] = ld
    ACT = np.zeros((len(cnt), L), bool); ACT[mid, pos] = True
    return LD, ACT

def p_clear(LD, ACT, theta, slope):
    M, L = LD.shape
    E = np.zeros((M, 101), np.float32); E[:, 50] = 1.0
    th = theta.astype(np.float32)
    for j in range(L):
        act = ACT[:, j]
        if not act.any(): break
        p = np.where(act, 1.0 / (1.0 + np.exp(-slope * (LD[:, j] - th))), 0.0).astype(np.float32)[:, None]
        hit = E * (1 - p); miss = E * p
        N = np.zeros_like(E)
        N[:, 1:] = hit[:, :-1]; N[:, 100] += hit[:, 100]
        N[:, 1:86] += miss[:, 16:]
        E = np.where(act[:, None], N, E)
    return E.sum(axis=1)

def skill50(LD, ACT, slope, iters=13):
    lo = np.full(LD.shape[0], -2.0); hi = np.full(LD.shape[0], 5.0)
    for _ in range(iters):
        mid = (lo + hi) / 2
        ok = p_clear(LD, ACT, mid, slope) >= 0.5
        hi = np.where(ok, mid, hi); lo = np.where(ok, lo, mid)
    return (lo + hi) / 2

def r2_cubic(x, y):
    c = np.polyfit(x, y, 3); return 1 - np.var(y - np.polyval(c, x)) / np.var(y)
