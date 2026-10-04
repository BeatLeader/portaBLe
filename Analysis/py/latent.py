"""Latent skill/difficulty models fitted on the score dump.

z_ij = g(acc_ij) is modelled as  d_j - a_i  (+ low-rank interaction), where a_i is player skill and d_j is the
data-implied difficulty of map j. The map effects d_j are the "ground truth" the algorithm's ratings are compared with.
"""
import numpy as np
import pandas as pd


def err_transform(acc):
    """Error-rate space: log(1-acc). Multiplicative skill differences become additive."""
    return np.log(np.clip(1.0 - np.asarray(acc, dtype=float), 5e-4, 1.0))


def fit_additive(pi, mi, z, n_p, n_m, iters=40, min_p=1, ridge=0.0):
    """Alternating least squares for z = d[m] - a[p]. Returns a, d (d centered)."""
    a = np.zeros(n_p)
    d = np.zeros(n_m)
    cp = np.bincount(pi, minlength=n_p) + ridge
    cm = np.bincount(mi, minlength=n_m) + ridge
    cp[cp == 0] = 1
    cm[cm == 0] = 1
    for _ in range(iters):
        d = np.bincount(mi, weights=z + a[pi], minlength=n_m) / cm
        d -= d.mean()
        a = np.bincount(pi, weights=d[mi] - z, minlength=n_p) / cp
    return a, d


def fit_lowrank(pi, mi, resid, n_p, n_m, rank=2, iters=25, lam=5.0, seed=0):
    """ALS for resid_ij ~ U[p] . V[m] (ridge-regularised); returns U, V."""
    rng = np.random.default_rng(seed)
    U = rng.normal(0, 0.1, (n_p, rank))
    V = rng.normal(0, 0.1, (n_m, rank))
    order_p = np.argsort(pi, kind="stable")
    order_m = np.argsort(mi, kind="stable")
    bp = np.searchsorted(pi[order_p], np.arange(n_p + 1))
    bm = np.searchsorted(mi[order_m], np.arange(n_m + 1))
    for _ in range(iters):
        # update U
        for i in range(n_p):
            s, e = bp[i], bp[i + 1]
            if e == s:
                continue
            idx = order_p[s:e]
            Vm = V[mi[idx]]
            A = Vm.T @ Vm + lam * np.eye(rank)
            U[i] = np.linalg.solve(A, Vm.T @ resid[idx])
        for j in range(n_m):
            s, e = bm[j], bm[j + 1]
            if e == s:
                continue
            idx = order_m[s:e]
            Um = U[pi[idx]]
            A = Um.T @ Um + lam * np.eye(rank)
            V[j] = np.linalg.solve(A, Um.T @ resid[idx])
    return U, V


def prepare(df, min_player_scores=15, min_map_scores=40, mode="Standard"):
    d = df[df["mode"] == mode] if "mode" in df else df
    for _ in range(3):
        d = d[d.groupby("player", observed=True)["acc"].transform("size") >= min_player_scores]
        d = d[d.groupby("lb_id")["acc"].transform("size") >= min_map_scores]
    d = d.copy()
    d["pi"], players = pd.factorize(d["player"].astype(str))
    d["mi"], maps = pd.factorize(d["lb_id"].astype(str))
    return d.reset_index(drop=True), players, maps
