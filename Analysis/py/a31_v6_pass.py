"""A31: pass rating for v6 = Loloppe's analyzer (parity rewrite, refit classic constants) + the energy-bar pass model.

PassDiff is rebuilt from the new analyzer's raw per-swing metrics with its own constants (not the classic SwingDiff's):
  distance  = hit_distance / (hit_distance + DISTANCE_FALLOFF) + 1
  speed     = frequency * distance;  falloff = 1 - SPEED_FALLOFF_BASE ** -speed
  stress    = (angle_strain * w_as + reposition * w_rd + rotation * w_rot) * distance;  stress mult = 2 s / (s + 2) + 1
  PassDiff  = speed * falloff * stress mult * crossover / horizontal / diagonal / parity terms * NJS * stream * wall (dodge / crouch)
and the constants are fitted with the exact energy-bar model (energylib) against pass difficulty from attempts (b, a19/a30 yardstick):
least squares on the residuals of a cubic fit of b on ln rating, fitted on half of the maps, scored on the other half, then refitted
on all. One Saber maps are left out of the fit and get their own factor afterwards (as a23).

Usage: python a31_v6_pass.py --swings <swings_v6a.csv.gz> --b <a30_b_top.parquet> --out <json> [--fit-maps 1200] [--old-swings <swings_prod.csv.gz>]
"""
import argparse, json, os, sys, tempfile, time
from concurrent.futures import ProcessPoolExecutor
import numpy as np, pandas as pd
from scipy.optimize import least_squares
sys.path.insert(0, os.path.dirname(__file__))
import energylib as el

# name, start (v2 / old-analyzer definitions), lower, upper
PARAMS = [("distance_falloff", 2.668, 0.2, 8.0), ("speed_falloff_base", 1.4, 1.05, 4.0),
          ("w_angle_strain", 0.05 * 6.52, 0.0, 6.0), ("w_reposition", 0.3 * 6.52, 0.0, 8.0), ("w_rotation", 0.2 * 6.52, 0.0, 6.0),
          ("crossover", 0.59, -0.3, 1.5), ("horizontal", 0.37, -0.3, 1.5), ("diagonal", 0.29, -0.3, 1.5), ("parity", -0.14, -0.6, 1.5),
          ("stream", 0.05, -0.3, 0.6), ("dodge_wall", 0.1, -0.3, 1.0), ("crouch_wall", 0.2, -0.3, 3.0)]
NAMES = [p[0] for p in PARAMS]; X0 = np.array([p[1] for p in PARAMS]); LO = np.array([p[2] for p in PARAMS]); HI = np.array([p[3] for p in PARAMS])
SLOPE = 2.5

class Swings:
    def __init__(self, S):
        self.lb = S.lb_id.values
        self.codes, self.ids = pd.factorize(S.lb_id)
        f = lambda c: S[c].values.astype(np.float64)
        self.freq, self.hd, self.as_, self.rd, self.ra = f("frequency"), f("hit_distance"), f("angle_strain"), f("reposition"), f("rotation")
        self.njs = f("njs_buff")
        self.cross = (((S.hand == 0) & (S.x == 3)) | ((S.hand == 1) & (S.x == 0))).values
        self.horiz = S.cut_direction.isin([2, 3]).values; self.diag = S.cut_direction.isin([4, 5, 6, 7]).values
        self.par = S.parity_error.values == 1; self.stream = S.is_stream.values == 1
        wb = f("wall_buff"); self.dodge = (wb > 1.01) & (wb < wb.max() - 0.01) if wb.max() > 1.15 else (wb > 1.01)
        self.crouch = wb >= max(wb.max() - 0.01, 1.15)
        self.notes = S.n_cubes.clip(1, 4).values
        self.one = (S.groupby("lb_id", sort=False).hand.nunique().reindex(self.ids) == 1).values

    def logd(self, x):
        p = dict(zip(NAMES, x))
        dist = self.hd / (self.hd + p["distance_falloff"]) + 1
        speed = self.freq * dist
        fall = 1 - np.power(p["speed_falloff_base"], -speed)
        s = (self.as_ * p["w_angle_strain"] + self.rd * p["w_reposition"] + self.ra * p["w_rotation"]) * dist
        sm = 2 * s / (s + 2) + 1
        d = speed * fall * sm * self.njs * (1 + p["crossover"] * self.cross) * (1 + p["horizontal"] * self.horiz) \
            * (1 + p["diagonal"] * self.diag) * (1 + p["parity"] * self.par) * (1 + p["stream"] * self.stream) \
            * (1 + p["dodge_wall"] * self.dodge) * (1 + p["crouch_wall"] * self.crouch)
        return np.log(np.maximum(d, 1e-3)).astype(np.float32)

def skill50_smooth(LD, ACT, iters=10):
    """skill50 by bisection, then linear interpolation of logit P(clear) inside the last bracket (smooth in the constants)"""
    lo = np.full(LD.shape[0], -2.0); hi = np.full(LD.shape[0], 5.0)
    for _ in range(iters):
        mid = (lo + hi) / 2; ok = el.p_clear(LD, ACT, mid, SLOPE) >= 0.5
        hi = np.where(ok, mid, hi); lo = np.where(ok, lo, mid)
    lg = lambda p: np.log(np.clip(p, 1e-6, 1 - 1e-6) / (1 - np.clip(p, 1e-6, 1 - 1e-6)))
    a, c = lg(el.p_clear(LD, ACT, lo, SLOPE)), lg(el.p_clear(LD, ACT, hi, SLOPE))
    w = np.where(np.abs(a - c) > 1e-9, a / (a - c), 0.5)
    return lo + np.clip(w, 0, 1) * (hi - lo)

def t50_maps(ld, sw, rows):
    """skill50 per map for the maps in rows (codes), chunked by length so short maps are not padded to the longest"""
    keep = np.isin(sw.codes, rows)
    codes = sw.codes[keep]; ldk = ld[keep]; notes = sw.notes[keep]
    out = np.empty(len(rows)); remap = {c: i for i, c in enumerate(rows)}
    order = np.argsort(np.bincount(codes, minlength=sw.codes.max() + 1)[rows])
    for chunk in np.array_split(order, max(1, len(rows) // 150)):
        cs = np.sort(rows[chunk]); m = np.isin(codes, cs)
        cc, _ = pd.factorize(codes[m], sort=True)       # sorted by original code -> same order as cs
        LD, ACT = el.note_matrix(ldk[m], notes[m], cc)
        for c, v in zip(cs, skill50_smooth(LD, ACT)): out[remap[c]] = v
    return out

def cubic_res(t, b):
    return b - np.polyval(np.polyfit(t, b, 3), t)

def scores(t, b, one, B):
    """R2 (One Saber factor refitted), maps off by > 1, top-of-list overlap and order vs b and b_top"""
    c0 = np.polyfit(t[~one], b[~one], 3)
    f = min(np.linspace(0.3, 1.2, 91), key=lambda f: abs(np.mean(b[one] - np.polyval(c0, t[one] + np.log(f))))) if one.any() else 1.0
    tt = t + np.where(one, np.log(f), 0); e = cubic_res(tt, b)
    R = B.assign(t=tt)
    out = {"R2": 1 - e.var() / b.var(), "off>1": int((np.abs(e) > 1).sum()), "one_saber": float(f)}
    for target, sub in [("b", R), ("b_top", R[R.top_attempts >= 100])]:
        for n in [50, 200]:
            out[f"{target} top{n}"] = len(set(sub.t.nlargest(n).index) & set(sub[target].nlargest(n).index)) / n
        top = sub.t.nlargest(50).index
        out[f"{target} order50"] = float(sub.t[top].rank().corr(sub[target][top].rank()))
    return out, e

COLS = ["lb_id", "swing_i", "seconds", "hand", "x", "cut_direction", "n_cubes", "parity_error", "is_stream", "frequency", "hit_distance",
        "angle_strain", "reposition", "rotation", "njs_buff", "wall_buff"]
_CHUNKS = {}
def _t50_chunk(task):
    """worker: skill50 of every map in one chunk (parquet of its swings) for each constants vector"""
    path, xs = task
    if path not in _CHUNKS: _CHUNKS[path] = Swings(pd.read_parquet(path))
    sw = _CHUNKS[path]; rows = np.arange(len(sw.ids))
    return [t50_maps(sw.logd(np.asarray(x)), sw, rows) for x in xs]

class Evaluator:
    """skill50 of a set of maps for many constants vectors at once, maps split in chunks over worker processes"""
    def __init__(self, S, ids, pool, tmp, n_chunks=16):
        S = S[S.lb_id.isin(ids)]
        sizes = S.groupby("lb_id", sort=True).size()
        order = sizes.sort_values(ascending=False).index; bins = [[] for _ in range(n_chunks)]; load = np.zeros(n_chunks)
        for lb in order:                       # greedy balance by swing count
            k = int(np.argmin(load)); bins[k].append(lb); load[k] += sizes[lb]
        self.paths, self.ids = [], []
        for k, lbs in enumerate(bins):
            if not lbs: continue
            path = os.path.join(tmp, f"chunk_{id(self)}_{k}.parquet")
            S[S.lb_id.isin(lbs)].to_parquet(path); self.paths.append(path)
            self.ids.append(pd.factorize(S[S.lb_id.isin(lbs)].lb_id)[1])
        self.order = pd.Index(np.concatenate(self.ids)); self.pool = pool
    def __call__(self, xs, ids):
        res = list(self.pool.map(_t50_chunk, [(p, [list(x) for x in xs]) for p in self.paths]))
        return [pd.Series(np.concatenate([r[i] for r in res]), index=self.order).reindex(ids).values for i in range(len(xs))]

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--swings", required=True); ap.add_argument("--b", required=True); ap.add_argument("--out", required=True)
    ap.add_argument("--fit-maps", type=int, default=1200); ap.add_argument("--old-swings", default=None)
    ap.add_argument("--fix", action="append", default=[], help="name=value: keep a constant fixed")
    ap.add_argument("--workers", type=int, default=12); ap.add_argument("--iters", type=int, default=10)
    ap.add_argument("--top-weight", type=float, default=0.0, help="weight of the strong players' pass difficulty (b_top) on the hardest maps")
    ap.add_argument("--top-maps", type=int, default=800, help="the hardest maps by b where b_top joins the objective")
    ap.add_argument("--no-holdout", action="store_true", help="skip the half / held-out fit, fit all maps from the start")
    ap.add_argument("--start", default=None, help="JSON file with starting constants ({'constants': {...}} as written by this script)")
    args = ap.parse_args()
    B = pd.read_parquet(args.b).set_index("lb_id")
    B = B[B.index.isin(pd.read_csv("Analysis/out/pass_v2_maps.csv", dtype={"lb_id": str}).lb_id)]
    S = pd.read_csv(args.swings, dtype={"lb_id": str}, usecols=COLS)
    S = S[S.lb_id.isin(B.index)].sort_values(["lb_id", "seconds", "swing_i"], kind="stable").reset_index(drop=True)
    sw = Swings(S); B = B.loc[sw.ids]; b = B.b.values
    print(f"{len(sw.ids)} maps, {len(S):,} swings, {sw.one.sum()} One Saber; walls: {sw.dodge.sum():,} dodge / {sw.crouch.sum():,} crouch swings", flush=True)
    tmp = tempfile.mkdtemp(prefix="a31_", dir=os.path.dirname(os.path.abspath(args.out)))
    pool = ProcessPoolExecutor(max_workers=args.workers)
    ev_all = Evaluator(S, sw.ids, pool, tmp)
    result = {}
    t0 = time.time(); t_v2 = ev_all([X0], sw.ids)[0]
    sc, _ = scores(t_v2, b, sw.one, B); result["v2 constants, new analyzer"] = sc
    print(f"v2 constants on the new analyzer: {json.dumps({k: round(v, 3) for k, v in sc.items()})} ({time.time() - t0:.0f} s)", flush=True)
    if args.old_swings:
        So = pd.read_csv(args.old_swings, dtype={"lb_id": str}, usecols=COLS)
        So = So[So.lb_id.isin(B.index)].sort_values(["lb_id", "seconds", "swing_i"], kind="stable").reset_index(drop=True)
        ids_o = pd.factorize(So.lb_id)[1]
        to = Evaluator(So, ids_o, pool, tmp)([X0], ids_o)[0]
        one_o = (So.groupby("lb_id", sort=False).hand.nunique().reindex(ids_o) == 1).values
        sc, _ = scores(to, B.loc[ids_o].b.values, one_o, B.loc[ids_o]); result["v2, old analyzer"] = sc
        print(f"v2 on the old analyzer (v5):      {json.dumps({k: round(v, 3) for k, v in sc.items()})}", flush=True)

    fixed = {kv.split("=")[0]: float(kv.split("=")[1]) for kv in args.fix}
    free = [i for i, n in enumerate(NAMES) if n not in fixed]
    x_start = X0.copy()
    if args.start:
        for n, v in json.load(open(args.start))["constants"].items(): x_start[NAMES.index(n)] = v
    for n, v in fixed.items(): x_start[NAMES.index(n)] = v

    brank = B.b.rank(ascending=False)
    top_set = set(B.index[(brank <= args.top_maps) & (B.top_attempts >= 100) & (B.b_top > -6)])
    def fit(ids, x_init, max_iter):
        """least squares on the residuals of the cubic fit of b on skill50 (Gauss-Newton / trf, forward-difference Jacobian in parallel);
        with --top-weight, plus the residuals of b_top (strong players) on the hardest maps"""
        bb = B.b.reindex(ids).values; cache = {}
        tsel = np.array([i in top_set for i in ids]); bt = B.b_top.reindex(ids).values[tsel]
        def res_of(t):
            r = cubic_res(t, bb)
            if args.top_weight > 0 and tsel.sum() > 20: r = np.concatenate([r, args.top_weight * cubic_res(t[tsel], bt)])
            return r
        def tvals(z_list):
            xs = []
            for z in z_list:
                x = x_init.copy(); x[free] = z; xs.append(x)
            return ev_all(xs, ids)
        def resid(z):
            key = tuple(np.round(z, 12))
            if key not in cache: cache[key] = res_of(tvals([z])[0])
            return cache[key]
        def jac(z, *a):
            r0 = resid(z); hs = 0.02 * np.maximum(np.abs(z), 0.1)
            zs = []
            for j, h in enumerate(hs):
                zz = z.copy(); zz[j] = z[j] + h if z[j] + h <= HI[free][j] else z[j] - h; zs.append(zz)
            ts = tvals(zs)
            return np.column_stack([(res_of(t) - r0) / (zz[j] - z[j]) for j, (t, zz) in enumerate(zip(ts, zs))])
        t0 = time.time()
        r = least_squares(resid, x_init[free], jac=jac, bounds=(LO[free], HI[free]), x_scale=np.maximum(np.abs(x_init[free]), 0.1), max_nfev=max_iter)
        x = x_init.copy(); x[free] = r.x
        print(f"   fit on {len(ids)} maps ({tsel.sum()} with b_top, weight {args.top_weight}): {r.nfev} steps, R2 {1 - np.var(r.fun[:len(bb)]) / np.var(bb):.4f} ({time.time() - t0:.0f} s)", flush=True)
        return x

    rng = np.random.default_rng(0)
    two = sw.ids[~sw.one]
    perm = rng.permutation(len(two)); half = two[np.sort(perm[: min(args.fit_maps, len(two) // 2)])]
    if args.no_holdout:
        xh = x_start
    else:
        xh = fit(half, x_start, args.iters)
        th = ev_all([xh], sw.ids)[0]; m = ~np.isin(sw.ids, half)
        sc, _ = scores(th[m], b[m], sw.one[m], B[m]); result["refit, held-out maps"] = sc
        print(f"refit on {len(half)} maps, scored on the other {m.sum()}: {json.dumps({k: round(v, 3) for k, v in sc.items()})}", flush=True)
        print("   constants: " + ", ".join(f"{n} {v:.3f}" for n, v in zip(NAMES, xh)), flush=True)
    xa = fit(two, xh, args.iters if args.no_holdout else max(3, args.iters // 2))
    ta = ev_all([xa], sw.ids)[0]
    sc, e = scores(ta, b, sw.one, B); result["refit, all maps"] = sc
    result["constants"] = dict(zip(NAMES, map(float, xa))); result["constants_half"] = dict(zip(NAMES, map(float, xh)))
    print("refit constants: " + ", ".join(f"{n} {v:.3f}" for n, v in zip(NAMES, xa)))
    print(f"refit on all: {json.dumps({k: round(v, 3) for k, v in sc.items()})}")
    pd.DataFrame({"lb_id": sw.ids, "t50": ta, "t50_v2": t_v2, "b": b, "res": e, "one": sw.one}).to_parquet(args.out.replace(".json", "_maps.parquet"))
    json.dump(result, open(args.out, "w"), indent=1)
    pool.shutdown()
    import shutil; shutil.rmtree(tmp, ignore_errors=True)
