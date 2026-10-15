"""A32: which of v6's refitted pass constants (a31) costs order at the top of the list? Each constant (or group) is set back to its
v2 value one at a time, the rest kept at v6; scored on R2, order inside the rating's own top 50 (vs b and vs the strongest players'
b_top), top-50 / 200 overlap and rank correlation inside the hardest 100 / 200 maps.

Usage: python a32_pass_ablation.py --swings <swings_v6a.csv.gz> --b <a30_b_top.parquet> --v6 <a31_v6_pass.json> --out <json>
"""
import argparse, json, os, sys, tempfile
from concurrent.futures import ProcessPoolExecutor
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import a31_v6_pass as A

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--swings", required=True); ap.add_argument("--b", required=True); ap.add_argument("--v6", required=True)
    ap.add_argument("--out", required=True); ap.add_argument("--workers", type=int, default=12)
    args = ap.parse_args()
    B = pd.read_parquet(args.b).set_index("lb_id")
    B = B[B.index.isin(pd.read_csv("Analysis/out/pass_v2_maps.csv", dtype={"lb_id": str}).lb_id)]
    S = pd.read_csv(args.swings, dtype={"lb_id": str}, usecols=A.COLS)
    S = S[S.lb_id.isin(B.index)].sort_values(["lb_id", "seconds", "swing_i"], kind="stable").reset_index(drop=True)
    sw = A.Swings(S); B = B.loc[sw.ids]; b = B.b.values
    v6 = json.load(open(args.v6))["constants"]; x6 = np.array([v6[n] for n in A.NAMES]); x2 = A.X0.copy()
    groups = {"distance falloff": ["distance_falloff"], "low-speed falloff": ["speed_falloff_base"],
              "stress weights": ["w_angle_strain", "w_reposition", "w_rotation"], "crossover": ["crossover"], "horizontal": ["horizontal"],
              "diagonal": ["diagonal"], "parity": ["parity"], "stream": ["stream"], "walls": ["dodge_wall", "crouch_wall"]}
    variants = {"v2 constants": x2, "v6": x6}
    for g, names in groups.items():
        x = x6.copy()
        for n in names: x[A.NAMES.index(n)] = x2[A.NAMES.index(n)]
        variants[f"v6, {g} back to v2"] = x
    with tempfile.TemporaryDirectory(dir=os.path.dirname(os.path.abspath(args.out))) as tmp, ProcessPoolExecutor(args.workers) as pool:
        ev = A.Evaluator(S, sw.ids, pool, tmp)
        T = dict(zip(variants, ev(list(variants.values()), sw.ids)))
    sp = lambda x, y: float(pd.Series(x).rank().corr(pd.Series(y).rank()))
    H = (B.top_attempts >= 100) & (B.b_top > -6)
    rows = {}
    for name, t in T.items():
        sc, _ = A.scores(t, b, sw.one, B)
        R = pd.Series(t, index=sw.ids)
        r = {"R2": sc["R2"], "own top50 order vs b": sc["b order50"], "own top50 order vs b_top": sc["b_top order50"],
             "top50 overlap b": sc["b top50"], "top200 overlap b": sc["b top200"]}
        for n in [100, 200]:
            top = B.b.nlargest(n).index; r[f"rank corr in hardest {n}"] = sp(R[top], B.b[top])
        top = R.nlargest(50).index
        r["median attempts rank of own top 50"] = float(B.b.rank(ascending=False)[top].median())
        r["own top 50 outside attempts top 150"] = int((B.b.rank(ascending=False)[top] > 150).sum())
        rows[name] = r
    D = pd.DataFrame(rows).T
    pd.set_option("display.width", 250)
    print(D.round(3).to_string())
    json.dump(rows, open(args.out, "w"), indent=1)
