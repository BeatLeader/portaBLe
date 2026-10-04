"""Compare two portaBLe databases (e.g. wwwroot/test-algo-power.db vs wwwroot/test-ml.db).

Usage: python compare_dbs.py <test.db> <baseline.db> [--md out.md]
Prints (and optionally writes as Markdown): player ranking agreement, PP level/composition, map rating changes, Megametric spread,
biggest movers.
"""
import argparse, sqlite3
import numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument("test"); ap.add_argument("base")
ap.add_argument("--md", default=None)
args = ap.parse_args()

def load(path):
    con = sqlite3.connect(path)
    pl = pd.read_sql("select Id, Name, Pp, AccPp, PassPp, TechPp, Rank from Players where Pp > 0", con).set_index("Id")
    lb = pd.read_sql("select Id, Name, DifficultyName, Stars, PassRating, AccRating, TechRating, PredictedAcc, Megametric125, Megametric, Count from Leaderboards", con).set_index("Id")
    sc = pd.read_sql("select LeaderboardId, Pp, Weight from Scores where Pp > 0", con)
    con.close()
    return pl, lb, sc

pt, lt, st = load(args.test)
pb, lbb, sb = load(args.base)
lines = []
def out(s=""):
    print(s); lines.append(s)

common = pt.index.intersection(pb.index)
sp = pt.Pp.loc[common].rank().corr(pb.Pp.loc[common].rank())
out(f"## {args.test} vs {args.base}\n")
out(f"players with PP: {len(pt)} vs {len(pb)}; Spearman of player PP {sp:.5f}")
for k in (10, 100, 1000):
    tt, bb = set(pt.Pp.nlargest(k).index), set(pb.Pp.nlargest(k).index)
    out(f"- top {k}: overlap {len(tt & bb)}/{k}; mean PP {pt.Pp.nlargest(k).mean():.0f} vs {pb.Pp.nlargest(k).mean():.0f} ({pt.Pp.nlargest(k).mean()/pb.Pp.nlargest(k).mean()-1:+.1%})")
top1000 = pb.Pp.nlargest(1000).index
rel = (pt.Pp.loc[top1000] / pb.Pp.loc[top1000])
rk = (pt.Rank.loc[top1000] - pb.Rank.loc[top1000]).abs()
out(f"- base top-1000 players: PP change p5/p50/p95 {rel.quantile(.05)-1:+.1%} / {rel.median()-1:+.1%} / {rel.quantile(.95)-1:+.1%}; |rank change| median {rk.median():.0f}, p90 {rk.quantile(.9):.0f}, max {rk.max():.0f}")
bands = [(1, 100), (101, 1000), (1001, 10000), (10001, 50000), (50001, 10**7)]
for lo, hi in bands:
    ids = pb.index[(pb.Rank >= lo) & (pb.Rank <= hi)].intersection(pt.index)
    if len(ids) == 0: continue
    r = pt.Pp.loc[ids] / pb.Pp.loc[ids]
    out(f"- base rank {lo}-{hi if hi < 10**7 else 'end'}: {len(ids)} players, PP change median {r.median()-1:+.1%} (p10 {r.quantile(.1)-1:+.1%}, p90 {r.quantile(.9)-1:+.1%})")
for name, p in (("test", pt), ("base", pb)):
    t = p.loc[p.Pp.nlargest(1000).index]
    out(f"- top-1000 PP composition ({name}): pass {t.PassPp.sum()/t.Pp.sum():.1%}, acc {t.AccPp.sum()/t.Pp.sum():.1%}, tech {t.TechPp.sum()/t.Pp.sum():.1%}")

lc = lt.join(lbb, rsuffix="_b", how="inner")
ds = lc.Stars - lc.Stars_b
out(f"\nmaps: {len(lc)}; stars change |d| p50 {ds.abs().median():.3f}, p90 {ds.abs().quantile(.9):.3f}, max {ds.abs().max():.2f}; corr {np.corrcoef(lc.Stars, lc.Stars_b)[0,1]:.4f}")
out(f"acc rating corr {np.corrcoef(lc.AccRating, lc.AccRating_b)[0,1]:.4f}; mean {lc.AccRating.mean():.3f} vs {lc.AccRating_b.mean():.3f}; predicted acc mean {lc.PredictedAcc.mean():.4f} vs {lc.PredictedAcc_b.mean():.4f}")
for col in ("Megametric125", "Megametric"):
    a, b = lc[col][lc.Count >= 100], lc[col + "_b"][lc.Count >= 100]
    out(f"{col} (maps with >=100 scores): mean {a.mean():.3f} vs {b.mean():.3f}; SD {a.std():.3f} vs {b.std():.3f}; maps >= 0.65: {(a>=0.65).mean():.1%} vs {(b>=0.65).mean():.1%}")
# total PP mass a map hands out (weighted) — share of all weighted PP, test vs base
mt = st.assign(w=st.Pp * st.Weight).groupby("LeaderboardId").w.sum(); mb = sb.assign(w=sb.Pp * sb.Weight).groupby("LeaderboardId").w.sum()
share = (mt / mt.sum()).to_frame("t").join((mb / mb.sum()).rename("b"), how="inner")
share["ratio"] = share.t / share.b
out(f"weighted-PP share per map: ratio p5/p50/p95 {share.ratio.quantile(.05):.2f}/{share.ratio.median():.2f}/{share.ratio.quantile(.95):.2f}")

lc["dstars"] = ds
cols = ["Name", "DifficultyName", "Stars_b", "Stars", "AccRating_b", "AccRating", "PredictedAcc_b", "PredictedAcc", "PassRating", "TechRating"]
out("\nlargest star increases:"); out(lc.sort_values("dstars", ascending=False)[cols].head(10).round(3).to_string())
out("\nlargest star decreases:"); out(lc.sort_values("dstars")[cols].head(10).round(3).to_string())
pc = pt[["Name", "Pp", "Rank"]].join(pb[["Pp", "Rank"]], rsuffix="_b", how="inner")
pc = pc[pc.Rank_b <= 500]
pc["d"] = pc.Pp / pc.Pp_b - 1
out("\nbase top-500 players moving most (PP change):"); out(pc.reindex(pc.d.abs().sort_values(ascending=False).index).head(10).round(3).to_string())
if args.md:
    open(args.md, "w", encoding="utf-8").write("\n".join(lines) + "\n")
