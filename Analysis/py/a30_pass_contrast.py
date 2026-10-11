"""A30: fixing the top of the pass-rating list: per-swing contrast, cut-direction terms, how the terms combine.

Attempt replays (a29) showed that within a run hard and easy swings differ far less than pass v2's per-note slope (2.5) implies,
that diagonals cost less than v2's x1.29, and that v2 stacks all its multipliers on a map's hardest section (Calamitous Demise E+,
Godspeed Expert at the top of the list, attempts #31 / #46). Variants of the energy-bar model, each scored on:
  - R2 of a cubic fit of the attempts' pass difficulty b on ln(rating), maps off by > 1 / > 2 logits (a19 yardstick);
  - the top of the list: overlap of the rating's top 50 / 100 / 200 with b's, Spearman inside the rating's own top 50, and the same
    against b_top (pass difficulty from the attempts of the strongest 5 % of players, maps with >= 100 of their attempts);
  - the named outliers' residuals and the top 10.
The One Saber factor is refitted per variant (as a23); the scale does not matter for any of these scores.

Stage 2 changes two rules of the simulation instead of constants: run form (each attempt plays at the player's level + a N(0, form)
offset, so surviving one hard section makes surviving the next more likely) and swing-level draws (a swing is hit or missed as a whole:
a missed stack loses all its notes).

Usage: python a30_pass_contrast.py --swings <swings_prod.csv.gz> --b <a30_b_top.parquet> --maps-db <portaBLe DB for names>
       [--grid stage1|stage2|...] [--workers 6] [--out <json>]
"""
import argparse, json, os, sqlite3, sys
from concurrent.futures import ProcessPoolExecutor
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import energylib as el

V2 = dict(el.V2, additive=False)
GRIDS = {
    "stage1": {  # per-swing contrast and the cut-direction terms
        "v2": {},
        "slope 2.0": {"slope": 2.0}, "slope 1.6": {"slope": 1.6}, "slope 1.3": {"slope": 1.3},
        "diagonal 0.15": {"diagonal": 0.15}, "diagonal 0": {"diagonal": 0.0},
        "additive terms": {"additive": True},
        "slope 1.6, diagonal 0.15": {"slope": 1.6, "diagonal": 0.15},
        "slope 1.6, additive": {"slope": 1.6, "additive": True},
    },
    "stage2": {  # run form and swing-level draws
        "v2": {},
        "form 0.3": {"form": 0.3},
        "swing draws": {"swing_draw": True},
        "form 0.3 + swing draws": {"form": 0.3, "swing_draw": True},
        "form 0.5": {"form": 0.5},
        "form 0.3 + swing draws, slope 2.0": {"form": 0.3, "swing_draw": True, "slope": 2.0},
    },
}
# 5-point Gauss-Hermite for N(0, 1): run form (a player's skill on one attempt = their level + form)
GH_X = np.array([-2.856970, -1.355626, 0.0, 1.355626, 2.856970]); GH_W = np.array([0.011257, 0.222076, 0.533333, 0.222076, 0.011257])

def swing_matrix(ld, notes, codes):
    """(maps x swings) ln PassDiff and note count per swing (0 = inactive)"""
    cnt = np.bincount(codes); L = cnt.max()
    pos = np.arange(len(codes)) - np.repeat(np.r_[0, np.cumsum(cnt)[:-1]], cnt)
    LD = np.full((len(cnt), L), -20.0, np.float32); LD[codes, pos] = ld
    NN = np.zeros((len(cnt), L), np.int8); NN[codes, pos] = notes
    return LD, NN

def p_clear_swings(LD, NN, theta, slope):
    """energy-bar DP with one draw per swing: hit = +1 % per note, miss = -15 % per note (a missed stack loses all its notes)"""
    M, L = LD.shape
    E = np.zeros((M, 101), np.float32); E[:, 50] = 1.0
    th = theta.astype(np.float32)
    for j in range(L):
        nn = NN[:, j]
        if not nn.any(): break
        p = (1.0 / (1.0 + np.exp(-slope * (LD[:, j] - th)))).astype(np.float32)[:, None]
        for n in (1, 2, 3, 4):
            m = nn == n
            if not m.any(): continue
            e = E[m]; hit = e * (1 - p[m]); miss = e * p[m]
            N = np.zeros_like(e)
            N[:, n:100] += hit[:, :100 - n]; N[:, 100] += hit[:, 100 - n:].sum(axis=1)
            N[:, 1:101 - 15 * n] += miss[:, 1 + 15 * n:]
            E[m] = N
    return E.sum(axis=1)

def skill50_general(p, ld, notes, codes, t0):
    """skill at which the map is cleared half the time, with run form ~ N(0, form) and per-swing or per-note draws; bisection around t0"""
    if p.get("swing_draw"):
        A, B = swing_matrix(ld, notes, codes); clear = lambda th: p_clear_swings(A, B, th, p["slope"])
    else:
        A, B = el.note_matrix(ld, notes, codes); clear = lambda th: el.p_clear(A, B, th, p["slope"])
    sd = p.get("form", 0.0)
    mix = (lambda th: sum(w * clear(th + sd * x) for x, w in zip(GH_X, GH_W))) if sd > 0 else clear
    lo, hi = t0 - 2.0, t0 + 2.0
    for _ in range(11):
        mid = (lo + hi) / 2; ok = mix(mid) >= 0.5
        hi = np.where(ok, mid, hi); lo = np.where(ok, lo, mid)
    return (lo + hi) / 2

def pass_diff(S, p):
    k = p["stress_scale"]
    sm = 2.0 * k * S.stress.values / (k * S.stress.values + 2.0) + 1.0
    cross = (((S.hand == 0) & (S.x == 3)) | ((S.hand == 1) & (S.x == 0))).values
    horiz = S.cut_direction.isin([2, 3]).values; diag = S.cut_direction.isin([4, 5, 6, 7]).values; par = S.parity_error.values
    base = (S.swing_speed * S.low_speed_falloff).values * (S.njs_buff * np.where(S.is_stream == 1, 1.05, 1.0) * S.wall_buff).values
    if p.get("additive"):   # pattern terms add up instead of multiplying (no stacking on the same swing)
        pat = 1 + (sm - 1) + p["crossover"] * cross + p["horizontal"] * horiz + p["diagonal"] * diag + p["parity"] * par
    else:
        pat = sm * (1 + p["crossover"] * cross) * (1 + p["horizontal"] * horiz) * (1 + p["diagonal"] * diag) * (1 + p["parity"] * par)
    return base * np.maximum(pat, 0.05)

_S = None
def _load(path, lbs):
    global _S
    if _S is None:
        S = pd.read_csv(path, dtype={"lb_id": str}, usecols=["lb_id", "swing_i", "seconds", "hand", "x", "cut_direction", "n_cubes", "parity_error",
                                                             "is_stream", "swing_speed", "stress", "low_speed_falloff", "njs_buff", "wall_buff"])
        _S = S[S.lb_id.isin(lbs)].sort_values(["lb_id", "seconds", "swing_i"], kind="stable").reset_index(drop=True)
    return _S

def run(task):
    name, params, path, lbs = task
    S = _load(path, lbs)
    codes, ids = pd.factorize(S.lb_id)
    p = dict(V2, **params)
    ld = np.log(np.maximum(pass_diff(S, p), 1e-3)); notes = S.n_cubes.clip(1, 4).values
    if p.get("form") or p.get("swing_draw"):
        LD, ACT = el.note_matrix(ld, notes, codes)
        t0 = el.skill50(LD, ACT, p["slope"], iters=8)                    # start of the bisection
        t50 = skill50_general(p, ld, notes, codes, t0)
    else:
        LD, ACT = el.note_matrix(ld, notes, codes)
        t50 = el.skill50(LD, ACT, p["slope"])
    one = (S.groupby("lb_id", sort=False).hand.nunique().reindex(ids) == 1).values
    return name, pd.DataFrame({"lb_id": ids, "t50": t50, "one": one})

def spear(a, b): return float(pd.Series(a).rank().corr(pd.Series(b).rank()))

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--swings", required=True); ap.add_argument("--b", required=True); ap.add_argument("--maps-db", dest="maps_db", required=True)
    ap.add_argument("--grid", default="stage1"); ap.add_argument("--workers", type=int, default=6); ap.add_argument("--out", default=None)
    ap.add_argument("--variant", action="append", default=[], help='extra variant as JSON, e.g. {"name": "x", "slope": 1.8}')
    args = ap.parse_args()
    B = pd.read_parquet(args.b).set_index("lb_id")
    B = B[B.index.isin(pd.read_csv("Analysis/out/pass_v2_maps.csv", dtype={"lb_id": str}).lb_id)]      # the a19 yardstick's 3 732 maps
    names = pd.read_sql("select Id, Name, DifficultyName d from Leaderboards", sqlite3.connect(args.maps_db)).set_index("Id")
    grid = dict(GRIDS.get(args.grid, {}))
    for v in args.variant:
        d = json.loads(v); grid[d.pop("name")] = d
    tasks = [(n, p, args.swings, list(B.index)) for n, p in grid.items()]
    with ProcessPoolExecutor(max_workers=args.workers) as ex:
        results = dict(ex.map(run, tasks))
    NAMED = {"46f03xx91": "Calamitous Demise E+", "4c5e3xx71": "Godspeed Ex", "4c5e3xx91": "Godspeed E+", "3f829xxxxxx91": "Extratongue E+",
             "290c991": "Superluminal E+", "338afxx91": "Feral E+", "179eb91": "Kannabis E+", "3d6abxxxxx91": "414 PER SPEED E+", "3e2c2xxxxx91": "Dual Doom E+"}
    rows, tops = [], {}
    for name, R in results.items():
        R = R.set_index("lb_id").join(B, how="inner")
        b = R.b.values; one = R.one.values
        c0 = np.polyfit(R.t50[~one], b[~one], 3)          # One Saber factor refitted (a23)
        f = min(np.linspace(0.3, 1.2, 91), key=lambda f: abs(np.mean(b[one] - np.polyval(c0, R.t50[one] + np.log(f)))))
        t = R.t50.values + np.where(one, np.log(f), 0)
        e = b - np.polyval(np.polyfit(t, b, 3), t)
        R["t"] = t; R["res"] = e
        row = {"variant": name, "R2": 1 - e.var() / b.var(), "off>1": int((np.abs(e) > 1).sum()), "off>2": int((np.abs(e) > 2).sum()), "one saber f": round(f, 2)}
        for target, sub in [("b", R), ("b_top", R[R.top_attempts >= 100])]:
            for n in [50, 100, 200]:
                row[f"{target} top{n}"] = len(set(sub.t.nlargest(n).index) & set(sub[target].nlargest(n).index)) / n
            tt = sub.t.nlargest(50).index
            row[f"{target} order in top50"] = spear(sub.t[tt].values, sub[target][tt].values)
        for k, v in NAMED.items():
            if k in R.index: row[v] = round(float(R.res[k]), 2)
        rows.append(row)
        R["brank"] = R.b.rank(ascending=False).astype(int)
        tops[name] = "; ".join(f"{names.Name.get(i, i)[:16]} {str(names.d.get(i, ''))[:2]} #{R.brank[i]}" for i in R.t.nlargest(10).index)
    T = pd.DataFrame(rows).set_index("variant")
    pd.set_option("display.width", 250); pd.set_option("display.max_columns", 40)
    cols1 = ["R2", "off>1", "off>2", "one saber f", "b top50", "b top100", "b top200", "b order in top50", "b_top top50", "b_top top100", "b_top top200", "b_top order in top50"]
    print(T[cols1].round(3).to_string())
    print("\nnamed maps, residual b - fit (+ = harder to pass than rated):")
    print(T[[c for c in T.columns if c not in cols1]].to_string())
    print("\ntop 10 (attempts rank):")
    for k, v in tops.items(): print(f"  {k:28s} {v}")
    if args.out: json.dump({"scores": T.reset_index().to_dict("records"), "top10": tops}, open(args.out, "w"), indent=1, default=float)
