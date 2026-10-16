"""A33: pass rating variants scored on the whole list and the top of it. A variant = a constants file (a31 JSON) and the clear
probability the rating is defined at (A31_CLEAR_TARGET, 0.5 in production: the skill at which a map is cleared half the time).
Strong players care about the upper end of a map's clear-probability curve, and maps with one hard section have flatter curves
than maps with sustained difficulty, so the target can change the order at the top.

Usage: A31_CLEAR_TARGET=0.7 python a33_pass_target.py --swings <swings_v6a.csv.gz> --b <a30_b_top.parquet> --out <json> name=<a31 json | v2> ...
"""
import argparse, json, os, sys, tempfile
from concurrent.futures import ProcessPoolExecutor
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import a31_v6_pass as A

def top_metrics(t, sw, B):
    b = B.b.values
    sc, _ = A.scores(t, b, sw.one, B)
    R = pd.Series(t, index=sw.ids); rank = B.b.rank(ascending=False)
    sp = lambda x, y: float(pd.Series(x).rank().corr(pd.Series(y).rank()))
    r = {"R2": sc["R2"], "off>1": sc["off>1"], "own top50 order vs b": sc["b order50"], "own top50 order vs b_top": sc["b_top order50"],
         "top50 overlap": sc["b top50"], "top200 overlap": sc["b top200"]}
    for n in [100, 200]:
        top = B.b.nlargest(n).index; r[f"rank corr hardest {n}"] = sp(R[top], B.b[top])
    r["median attempts rank own top 15"] = float(rank[R.nlargest(15).index].median())
    r["median attempts rank own top 50"] = float(rank[R.nlargest(50).index].median())
    r["own top 50 outside attempts top 100"] = int((rank[R.nlargest(50).index] > 100).sum())
    return r

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--swings", required=True); ap.add_argument("--b", required=True); ap.add_argument("--out", required=True)
    ap.add_argument("--workers", type=int, default=12); ap.add_argument("variants", nargs="+")
    args = ap.parse_args()
    B = pd.read_parquet(args.b).set_index("lb_id")
    B = B[B.index.isin(pd.read_csv("Analysis/out/pass_v2_maps.csv", dtype={"lb_id": str}).lb_id)]
    S = pd.read_csv(args.swings, dtype={"lb_id": str}, usecols=A.COLS)
    S = S[S.lb_id.isin(B.index)].sort_values(["lb_id", "seconds", "swing_i"], kind="stable").reset_index(drop=True)
    sw = A.Swings(S); B = B.loc[sw.ids]
    xs = {}
    for v in args.variants:
        name, path = v.split("=", 1)
        xs[name] = A.X0.copy() if path == "v2" else np.array([json.load(open(path))["constants"][n] for n in A.NAMES])
    with tempfile.TemporaryDirectory(dir=os.path.dirname(os.path.abspath(args.out))) as tmp, ProcessPoolExecutor(args.workers) as pool:
        ev = A.Evaluator(S, sw.ids, pool, tmp)
        T = dict(zip(xs, ev(list(xs.values()), sw.ids)))
    rows = {f"{n} @ {A.CLEAR_TARGET:.2f}": top_metrics(t, sw, B) for n, t in T.items()}
    D = pd.DataFrame(rows).T
    pd.set_option("display.width", 260)
    print(D.round(3).to_string(), flush=True)
    pd.DataFrame({n: t for n, t in T.items()}, index=sw.ids).to_parquet(args.out.replace(".json", ".parquet"))
    json.dump(rows, open(args.out, "w"), indent=1)
