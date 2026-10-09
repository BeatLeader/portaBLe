"""A27: do deliberate fails (a "fail button" mod, or walking into a wall when the accuracy is bad) pollute the attempts data?

A real fail needs the energy bar (start 50 %) to reach 0: misses, bad cuts and bombs cost at most 15 % each, walls drain over time.
So a fail with <= 3 misses / bad cuts / bombs cannot be an energy fail without walls ("impossible"), and with a wall hit it was
failed by walls alone ("wall-only"). Both are suspect. Records uploaded without client data (empty Platform, 2022-03 .. 2023-08)
carry no counters at all and cannot be checked; they are compared through the accuracy at the fail vs the same player's clears.
Robustness: the Rasch pass difficulty b (a19) refitted without the suspect fails and/or without the counterless records, and the R2
of pass rating v2 against each.

Usage: python a27_fake_fails.py --attempts <dir with attempts-*.parquet> --pass-v2 <out/pass_v2_maps.csv>
"""
import argparse, glob, os
import numpy as np, pandas as pd, pyarrow.parquet as pq

ap = argparse.ArgumentParser()
ap.add_argument("--attempts", required=True); ap.add_argument("--pass-v2", dest="pass_v2", required=True)
args = ap.parse_args()
CLEAR, FAIL = 1, 2
cols = ["PlayerId", "LeaderboardId", "Type", "Modifiers", "Accuracy", "MissedNotes", "BadCuts", "BombCuts", "WallsHit", "Platform"]
parts = []
for f in sorted(glob.glob(os.path.join(args.attempts, "attempts-*.parquet"))):
    t = pq.read_table(f, columns=cols, filters=[("Type", "in", [CLEAR, FAIL])]).to_pandas()
    parts.append(t[t.Modifiers.fillna("") == ""].drop(columns="Modifiers"))
A = pd.concat(parts, ignore_index=True)
A["rec"] = A.Platform.fillna("") != ""
A["k"] = A.MissedNotes + A.BadCuts + A.BombCuts
F = A[A.Type == FAIL]
print(f"{len(A):,} clean clears + fails; {len(F):,} fails, {(~F.rec).mean():.1%} of them without client data (no counters)")
Fr = F[F.rec]
imp = (Fr.k <= 3) & (Fr.WallsHit == 0); wall = (Fr.k <= 3) & (Fr.WallsHit > 0)
print(f"fails with counters: impossible as an energy fail {imp.sum():,} ({imp.mean():.2%}), wall-only {wall.sum():,} ({wall.mean():.2%})")
print("misses + bad cuts + bombs at the fail (no wall hit): " + "  ".join(
    f"{int(i)}{'+' if i == 10 else ''}: {x:.1%}" for i, x in Fr[Fr.WallsHit == 0].k.clip(upper=10).value_counts(normalize=True).sort_index().items()))
sus = Fr[imp | wall].groupby("PlayerId").size().sort_values(ascending=False)
print(f"suspect fails by {len(sus):,} players; the top 20 make {sus.iloc[:20].sum() / sus.sum():.0%}")
C = A[A.Type == CLEAR].groupby(["PlayerId", "LeaderboardId"]).Accuracy.median().rename("clear_acc")
for name, g in [("without counters", F[~F.rec]), ("with counters", Fr)]:
    j = g.join(C, on=["PlayerId", "LeaderboardId"]).dropna(subset=["clear_acc"])
    print(f"accuracy at the fail vs the same player's clears on the map, fails {name}: {j.Accuracy.median():.3f} vs {j.clear_acc.median():.3f}")

A["suspect"] = (A.Type == FAIL) & A.rec & (A.k <= 3)
def rasch(D):
    P = D.groupby(["PlayerId", "LeaderboardId"], observed=True).Type.agg(n="size", f=lambda v: (v == FAIL).sum()).reset_index()
    pa_ = P.groupby("PlayerId", observed=True).n.transform("sum"); mp_ = P.groupby("LeaderboardId", observed=True).n.transform("sum")
    P = P[(pa_ >= 20) & (mp_ >= 50)]
    pi, pids = pd.factorize(P.PlayerId); mi, mids = pd.factorize(P.LeaderboardId)
    n, f = P.n.values.astype(float), P.f.values.astype(float)
    theta = np.zeros(len(pids)); rate = np.clip(np.bincount(mi, weights=f) / np.bincount(mi, weights=n), 1e-3, 1 - 1e-3)
    b = np.log(rate / (1 - rate))
    for _ in range(60):
        p = 1 / (1 + np.exp(-(b[mi] - theta[pi]))); w = n * p * (1 - p)
        theta += (np.bincount(pi, weights=n * p - f) - theta / 4) / (np.bincount(pi, weights=w) + 1 / 4); theta -= theta.mean()
        p = 1 / (1 + np.exp(-(b[mi] - theta[pi]))); w = n * p * (1 - p)
        b += (np.bincount(mi, weights=f - n * p) - (b - b.mean()) / 4) / (np.bincount(mi, weights=w) + 1 / 4)
    return pd.Series(b, index=mids.astype(str))
V = pd.read_csv(args.pass_v2, dtype={"lb_id": str}).set_index("lb_id")
def r2(b):
    b = b.reindex(V.index); ok = b.notna(); x = np.log(V.v2[ok]); y = b[ok]
    return 1 - np.var(y - np.polyval(np.polyfit(x, y, 3), x)) / np.var(y)
base = rasch(A)
print(f"\npass difficulty b, all clean attempts: pass v2 R2 {r2(base):.4f}")
for name, D in [("without suspect fails", A[~A.suspect]), ("without counterless records", A[A.rec]), ("without both", A[A.rec & ~A.suspect])]:
    b = rasch(D); j = pd.concat([base.rename("a"), b.rename("b")], axis=1).dropna(); d = (j.b - j.a) - (j.b - j.a).mean()
    print(f"{name:28s} corr {j.a.corr(j.b):.4f}, change SD {d.std():.3f} logits, maps moved > 0.5: {(d.abs() > 0.5).sum():3d}, pass v2 R2 {r2(b):.4f}")
