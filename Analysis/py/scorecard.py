"""Scorecard of whole rating + PP systems on the same scores (portaBLe DBs): acc prediction vs score-implied difficulty, pass rating vs
pass difficulty from attempts (whole list and top of the list), Loloppe's balance (Megametric75 worth by star band), One Saber worth,
PP-vs-skill spread, stars, speed-mod share, easy-map farming, the weighted scores graph (v6sim.graph_metrics) and the difficulty premium.

Usage: python scorecard.py --b <a30_b_top.parquet> --skill <player_skill.parquet> --yardstick <portaBLe DB whose PredictedAcc places maps
       for the premium bands (v5)> --out <csv> name=<db> [name=<db> ...]
"""
import argparse, os, sqlite3, sys
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import v6sim as V
OUT = os.path.join(os.path.dirname(__file__), "..", "out")
ap = argparse.ArgumentParser()
ap.add_argument("--b", required=True); ap.add_argument("--skill", required=True); ap.add_argument("--yardstick", required=True)
ap.add_argument("--out", required=True); ap.add_argument("dbs", nargs="+")
args = ap.parse_args()
DBS = dict(a.split("=", 1) for a in args.dbs)
v5L = pd.read_sql("select Id, PredictedAcc pred, ModeName m from Leaderboards", sqlite3.connect(args.yardstick)).set_index('Id')
d = pd.read_csv(os.path.join(OUT, 'fit_acc_model_maps.csv'), dtype={'lb_id': str}).set_index('lb_id').d
B = pd.read_parquet(args.b).set_index('lb_id')
B = B[B.index.isin(pd.read_csv(os.path.join(OUT, 'pass_v2_maps.csv'), dtype={'lb_id': str}).lb_id)]
skill = pd.read_parquet(args.skill).set_index('player').skill
def spear(a, b): return float(pd.Series(np.asarray(a)).rank().corr(pd.Series(np.asarray(b)).rank()))
def r2cubic(x, y): c = np.polyfit(x, y, 3); return 1 - np.var(y - np.polyval(c, x)) / np.var(y)
rows, tops = {}, {}
for name, path in DBS.items():
    c = sqlite3.connect(path)
    L = pd.read_sql("select Id, Name, DifficultyName dd, ModeName m, Stars, PassRating pass, AccRating acc, TechRating tech, PredictedAcc pred, Megametric75 m75, Count from Leaderboards", c).set_index('Id')
    r = {}
    # acc: predicted error rate vs the scores' map difficulty d (latent model, log(1-acc) = d - skill)
    j = pd.concat([np.log(np.clip(1 - L.pred, 1e-4, 1)).rename('x'), d.rename('d')], axis=1).dropna()
    r['acc: R2 ln(1-pred) vs d'] = round(float(np.corrcoef(j.x, j.d)[0, 1] ** 2), 4)
    # pass vs attempts
    j = L[['pass']].join(B, how='inner'); j = j[j['pass'] > 0]
    x = np.log(j['pass'].values); e = j.b - np.polyval(np.polyfit(x, j.b, 3), x)
    r['pass: R2 vs attempts'] = round(r2cubic(x, j.b.values), 4); r['pass: Spearman'] = round(spear(j['pass'], j.b), 4)
    r['pass: off >1 / >2 logits'] = f"{int((np.abs(e) > 1).sum())} / {int((np.abs(e) > 2).sum())}"
    for nn in [50, 200]: r[f'pass: top{nn} overlap'] = len(set(j['pass'].nlargest(nn).index) & set(j.b.nlargest(nn).index)) / nn
    t = j['pass'].nlargest(50).index; r['pass: order in top50'] = round(spear(j['pass'][t], j.b[t]), 2)
    k = j[j.top_attempts >= 100]; t = k['pass'].nlargest(50).index
    r['pass: strong players top50 overlap / order'] = f"{len(set(t) & set(k.b_top.nlargest(50).index)) / 50:.2f} / {spear(k['pass'][t], k.b_top[t]):.2f}"
    jb = j.assign(br=j.b.rank(ascending=False).astype(int))
    tops[name + ' (pass)'] = '; '.join(f"{L.Name[i][:15]} {L.dd[i][:2]} #{jb.br[i]}" for i in j['pass'].nlargest(8).index)
    # balance (Loloppe's criterion): Megametric75 / mean of its integer star band
    m = L[(L.m75 > 0) & (L.Count >= 75)].copy(); m['band'] = np.floor(m.Stars).clip(0, 13)
    m['worth'] = m.m75 / m.groupby('band').m75.transform('mean') - 1
    r['balance: SD of worth'] = round(float(m.worth.std()), 3); r['balance: maps worth > +50%'] = int((m.worth > 0.5).sum())
    r['balance: corr(worth, pred acc)'] = round(float(m.worth.corr(m.pred)), 2)
    r['balance: One Saber worth'] = round(float(m.worth[m.m == 'OneSaber'].mean()), 2)
    # PP consistency: per-map slope of ln PP on player skill (unmodified scores), spread across maps
    S = pd.read_sql("select PlayerId, LeaderboardId lb, Pp, Modifiers mods from Scores where Pp > 0", c)
    S = S[S.mods.fillna('') == '']; S['skill'] = S.PlayerId.map(skill); S = S.dropna(subset=['skill'])
    sl = S.groupby('lb').apply(lambda g: np.polyfit(g.skill, np.log(g.Pp), 1)[0] if len(g) >= 30 else np.nan).dropna()
    r['PP vs skill slope: median / IQR'] = f"{sl.median():.3f} / {sl.quantile(.75) - sl.quantile(.25):.3f}"
    r['stars p50 / p90 / max'] = f"{L.Stars.median():.2f} / {L.Stars.quantile(.9):.2f} / {L.Stars.max():.2f}"
    P = pd.read_sql("select Pp from Players where Pp > 0 order by Pp desc limit 1", c)
    S2 = pd.read_sql("select s.Pp, s.Modifiers mods, l.Stars from Scores s join Leaderboards l on l.Id = s.LeaderboardId where s.Pp > 0 order by s.Pp desc limit 1000", c)
    mm = S2.mods.fillna('').str.upper()
    r['top player PP / FS-SF share of top 1000 scores'] = f"{P.Pp[0]:.0f} / {(mm.str.contains('FS') | mm.str.contains('SF')).mean():.0%}"
    r['best score on a <4* map'] = round(float(pd.read_sql("select max(s.Pp) p from Scores s join Leaderboards l on l.Id = s.LeaderboardId where l.Stars < 4", c).p[0]), 0)
    # weighted scores graph (stars vs PP x weight): roughness of the percentile lines, maps standing out from their star neighbours
    Sw = pd.read_sql("select PlayerId, LeaderboardId lb, Pp, Weight w, Accuracy acc, Modifiers mods from Scores where Pp > 0", c); Sw['wpp'] = Sw.Pp * Sw.w
    rough, ratio = V.graph_metrics(Sw, L.Stars)
    r['weighted graph: roughness p50 / p90 / p99'] = f"{rough[0.5]:.3f} / {rough[0.9]:.3f} / {rough[0.99]:.3f}"
    r['weighted graph: maps > 1.5x / > 2x star neighbours'] = f"{(ratio > 1.5).sum()} / {(ratio > 2).sum()}"
    r['weighted graph: top stand-outs'] = '; '.join(f"{L.Name[i][:12]} {L.dd[i][:2]} x{ratio[i]:.1f}" for i in ratio.nlargest(3).index)
    # difficulty premium: total PP of hard (<97 %) vs easy (99-99.5 %) maps at the same skill (v5's predicted acc as the common yardstick)
    u = Sw[(Sw.mods.fillna('') == '')].join(v5L, on='lb'); u = u[(u.m == 'Standard') & (u.acc > 0.8)]
    rel = np.log(np.clip(1 - u.acc, 1e-5, 1) / (1 - u.pred))
    prem = []
    for lo, hi in [(0.3, 0.7), (-0.2, 0.2)]:
        x = u[(rel > lo) & (rel < hi)]
        prem.append(x[x.pred < 0.97].Pp.median() / x[(x.pred >= 0.99) & (x.pred < 0.995)].Pp.median())
    r['difficulty premium: hard / easy PP at the same skill (below ref / at ref)'] = f"{prem[0]:.2f} / {prem[1]:.2f}"
    tops[name + ' (stars)'] = '; '.join(f"{L.Name[i][:15]} {L.dd[i][:2]}" for i in L.Stars.nlargest(8).index)
    rows[name] = r
    print('done', name, flush=True)
T = pd.DataFrame(rows)
pd.set_option('display.width', 260); pd.set_option('display.max_colwidth', 40)
print(T.to_string())
print()
for k, v in tops.items(): print(f"{k:32s} {v}")
T.to_csv(args.out)
