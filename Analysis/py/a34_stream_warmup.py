"""A34: two per-swing ideas for the long stream maps the v6 pass rating over-rates (Kill Kill Kill Kill E+, Godspeed Expert):
- regular streams: a swing in a steady alternating-hands stream (time gaps around it nearly constant) is easier per note,
  PassDiff x (1 + REGULAR) on those swings (REGULAR < 0);
- settling in: the player's effective level rises by WARMUP per minute into the map (attempts: the fail hazard falls x0.55 per
  minute), i.e. ln PassDiff - WARMUP * minutes.
Both on top of the v6 constants, scored like a33 (whole list + top of the list + named maps).

Usage: python a34_stream_warmup.py --swings <swings_v6a.csv.gz> --b <a30_b_top.parquet> --v6 <a31_v6_pass.json> --out <json>
"""
import argparse, json, os, sys
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import a31_v6_pass as A
from a33_pass_target import top_metrics

NAMED = {"Kill Kill Kill Kill E+": "1ae1291", "Godspeed Ex": "4c5e3xx71", "Calamitous E+": "46f03xx91", "Unwelcome School": "41a23xxxxx91",
         "SUPER EMULATOR": "38606xxx91", "Extratongue": "3f829xxxxxx91", "Feral": "338afxx91", "SONG REQUEST": "40370xxxxxxxx91"}

def regularity(S, window=4, cv_max=0.15):
    """1 for swings inside a steady alternating-hands stream: both hands, time order, gap CV over +-window swings below cv_max"""
    reg = np.zeros(len(S), bool)
    for lb, g in S.groupby("lb_id", sort=False):
        t = g.seconds.values; gaps = np.diff(t)
        if len(gaps) < 2 * window: continue
        s = pd.Series(gaps)
        m = s.rolling(2 * window, center=True, min_periods=window).mean(); sd = s.rolling(2 * window, center=True, min_periods=window).std()
        cv = (sd / m).values
        r = np.r_[False, cv < cv_max] & (g.is_stream.values == 1)
        reg[g.index.values] = r                      # S has a 0..n-1 index
    return reg

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--swings", required=True); ap.add_argument("--b", required=True); ap.add_argument("--v6", required=True); ap.add_argument("--out", required=True)
    args = ap.parse_args()
    B = pd.read_parquet(args.b).set_index("lb_id")
    B = B[B.index.isin(pd.read_csv("Analysis/out/pass_v2_maps.csv", dtype={"lb_id": str}).lb_id)]
    S = pd.read_csv(args.swings, dtype={"lb_id": str}, usecols=A.COLS)
    S = S[S.lb_id.isin(B.index)].sort_values(["lb_id", "seconds", "swing_i"], kind="stable").reset_index(drop=True)
    sw = A.Swings(S); B = B.loc[sw.ids]
    x6 = np.array([json.load(open(args.v6))["constants"][n] for n in A.NAMES])
    base = sw.logd(x6).astype(np.float64)
    reg = regularity(S)
    minutes = (S.seconds - S.groupby("lb_id").seconds.transform("min")).values / 60
    print(f"regular-stream swings: {reg.mean():.1%}; on Kill Kill Kill Kill {reg[S.lb_id.values == '1ae1291'].mean():.0%}, Godspeed Ex {reg[S.lb_id.values == '4c5e3xx71'].mean():.0%}, "
          f"Unwelcome School {reg[S.lb_id.values == '41a23xxxxx91'].mean():.0%}", flush=True)
    variants = {"v6": base}
    for r in [-0.1, -0.2, -0.3]: variants[f"regular stream x{1 + r:.1f}"] = base + np.log(1 + r) * reg
    for k in [0.05, 0.1, 0.2]: variants[f"settling in {k}/min"] = base - k * minutes
    rows = {}
    allrows = np.arange(len(sw.ids))
    for name, ld in variants.items():
        t = A.t50_maps(ld.astype(np.float32), sw, allrows)
        r = top_metrics(t, sw, B)
        tr = pd.Series(t, index=sw.ids).rank(ascending=False); br = B.b.rank(ascending=False)
        for k, i in NAMED.items():
            if i in tr.index: r[k] = f"#{int(tr[i])} (att #{int(br[i])})"
        rows[name] = r
        print(name, "done", flush=True)
    D = pd.DataFrame(rows).T
    pd.set_option("display.width", 300); pd.set_option("display.max_colwidth", 18)
    num = [c for c in D.columns if c not in NAMED]
    print(D[num].astype(float).round(3).to_string()); print(D[list(NAMED)].to_string())
    json.dump(rows, open(args.out, "w"), indent=1, default=str)
