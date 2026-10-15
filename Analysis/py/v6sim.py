"""v5 PP formula in numpy (portaBLe ReplayUtils: per-map power curve, PassBlend, One Saber gamma, modifier ratings) plus the v6
difficulty premium: AccRating x ((1 - pred) / PREM_REF) ** delta. Weighted scores graph metrics, premium by band, top-1000 level."""
import sqlite3, numpy as np, pandas as pd

GAMMA, EPS, KREL, BLEND_P = 0.6198, 0.0016, 0.75, 1.5
MODE_GAMMA = {'OneSaber': 1.0998}
NEG = {'NA': -0.3, 'NB': -0.2, 'NO': -0.2, 'OP': -0.5}
PREM_REF = 0.02

def load(db):
    c = sqlite3.connect(db)
    L = pd.read_sql("select Id, AccRating, PassRating, TechRating, PredictedAcc, ModifiersRatingId, ModeName, Name, DifficultyName from Leaderboards", c).set_index('Id')
    MR = pd.read_sql("select * from ModifiersRating", c).set_index('Id')
    S = pd.read_sql("select Id, PlayerId, LeaderboardId lb, Accuracy acc, Modifiers mods, Pp from Scores", c)
    return L, MR, S

def prepare(L, MR, S):
    """per-score ratings after modifier selection (as PpFromScore) and the modifier multiplier"""
    mods = S.mods.fillna('').str.upper()
    acc_r, pass_r, tech_r, pred = (L.AccRating.reindex(S.lb).values.copy(), L.PassRating.reindex(S.lb).values.copy(),
                                   L.TechRating.reindex(S.lb).values.copy(), L.PredictedAcc.reindex(S.lb).values.copy())
    mrid = L.ModifiersRatingId.reindex(S.lb).values
    for mod in ['SS', 'FS', 'SF']:
        sel = mods.str.contains(mod).values & ~pd.isna(mrid)
        if mod == 'FS': sel &= ~mods.str.contains('SF').values
        ids = mrid[sel]
        acc_r[sel] = MR[f'{mod}AccRating'].reindex(ids).values; pass_r[sel] = MR[f'{mod}PassRating'].reindex(ids).values
        tech_r[sel] = MR[f'{mod}TechRating'].reindex(ids).values
        pm = MR[f'{mod}PredictedAcc'].reindex(ids).values; pred[sel] = np.where(pm > 0, pm, pred[sel])
    mp = 1 + sum(np.where(mods.str.contains(k).values, v, 0) for k, v in NEG.items())
    gam = np.where(L.ModeName.reindex(S.lb).values == 'OneSaber', GAMMA * MODE_GAMMA['OneSaber'], GAMMA)
    nf = mods.str.contains('NF').values | (S.acc.values <= 0)
    return dict(acc_r=acc_r, pass_r=pass_r, tech_r=tech_r, pred=pred, mp=mp, gam=gam, nf=nf, acc=np.clip(S.acc.values, 0, 1))

def inflate(x): return 650 * np.power(np.maximum(x, 0), 1.3) / 650 ** 1.3

def pp_parts(acc, ar, pr, tr, pred, gam):
    passpp = np.maximum(15.2 * np.exp(np.power(np.maximum(pr, 0), 1 / 2.62)) - 30, 0)
    eps = EPS + KREL * (1 - pred)
    curve = np.power(np.maximum(1 - acc + eps, 1e-6) / (0.05 + eps), -gam)
    accpp = curve * ar * 34; techpp = np.exp(1.9 * acc) * 1.08 * tr
    rest = accpp + techpp
    tot = np.where((passpp > 0) & (rest > 0), np.power(np.power(passpp, BLEND_P) + np.power(rest, BLEND_P), 1 / BLEND_P), passpp + rest)
    return passpp, accpp, techpp, tot

def premium(pred, delta): return np.power(np.clip(1 - pred, 1e-4, 1) / PREM_REF, delta)

def score_pp(P, delta=0.0, scale=1.0):
    ar = P['acc_r'] * premium(P['pred'], delta) * scale
    m = P['mp']
    *_, tot = pp_parts(P['acc'], ar * m, P['pass_r'] * m, P['tech_r'] * m, P['pred'], P['gam'])
    return np.where(P['nf'], 0, inflate(tot))

def stars(L, delta=0.0, scale=1.0):
    pred = L.PredictedAcc.values
    gam = np.where(L.ModeName.values == 'OneSaber', GAMMA * MODE_GAMMA['OneSaber'], GAMMA)
    ar = L.AccRating.values * premium(pred, delta) * scale
    *_, tot = pp_parts(np.full(len(L), 0.96), ar, L.PassRating.values, L.TechRating.values, pred, gam)
    return pd.Series(inflate(tot) / 52, index=L.index)

def weighted(S, pp):
    S = S.assign(pp=pp); S = S[S.pp > 0].copy()
    S['rank'] = S.groupby('PlayerId').pp.rank(method='first', ascending=False) - 1
    S['w'] = np.power(0.965, S['rank']); S['wpp'] = S.pp * S.w
    return S

def player_pp(W):
    return W.groupby('PlayerId').wpp.sum().sort_values(ascending=False)

def graph_metrics(W, st):
    """weighted scores graph: roughness of the p50/p90/p99 lines and maps whose top plays stand out from their star neighbours"""
    S = W.copy(); S['x'] = S.lb.map(st); S = S.dropna(subset=['x'])
    S['bin'] = pd.cut(S.x, np.arange(2, 14.01, 0.1))
    q = S.groupby('bin', observed=False).wpp.quantile([0.5, 0.9, 0.99]).unstack()
    cnt = S.groupby('bin', observed=False).size(); q = q[cnt >= 200]
    rough = {k: float(np.nanmedian(np.abs(q[k].values[1:-1] - (q[k].values[:-2] + q[k].values[2:]) / 2) / q[k].values[1:-1])) for k in [0.5, 0.9, 0.99]}
    top = S[S.w >= 0.5].groupby('lb').agg(med=('wpp', 'median'), n=('wpp', 'size'))
    top = top[top.n >= 20]; top['x'] = top.index.map(st)
    xs, ms = top.x.values, top.med.values
    nb = np.array([np.median(ms[(np.abs(xs - x0) <= 0.3) & (np.arange(len(xs)) != i)]) if ((np.abs(xs - x0) <= 0.3).sum() > 5) else np.nan for i, x0 in enumerate(xs)])
    top['ratio'] = ms / nb
    return rough, top.ratio.dropna()

def megametric75(S, pp, min_plays=75):
    """portaBLe LeaderboardsRefresh: per map, the top third (by weight) of its scores from players with > 75 ranked plays,
    mean of PP / player's top PP x weight. Weights 0.965^rank over each player's scores ordered by PP."""
    D = pd.DataFrame({'p': S.PlayerId.values, 'lb': S.lb.values, 'pp': pp})
    D['rank'] = D.groupby('p').pp.rank(method='first', ascending=False) - 1
    D['w'] = np.power(0.965, D['rank'])
    D['top'] = D.groupby('p').pp.transform('max'); D['n'] = D.groupby('p').pp.transform('size')
    D = D[D.top != 0]
    D['k'] = D.groupby('lb').pp.transform('size') * 0.33
    F = D[D.n > min_plays].sort_values(['lb', 'w'], ascending=[True, False])
    F['r'] = F.groupby('lb').cumcount()
    F = F[F.r < F.k.astype(int)]
    g = F.assign(v=F.pp / F.top * F.w).groupby('lb').agg(v=('v', 'mean'), c=('v', 'size'))
    return g.v.where(g.c > 10, 0.0)

def worth(L, m75, stars, count):
    m = pd.DataFrame({'m75': m75, 'stars': stars, 'count': count, 'mode': L.ModeName}).dropna()
    m = m[(m.m75 > 0) & (m['count'] >= 75)].copy(); m['band'] = np.floor(m.stars).clip(0, 13)
    m['worth'] = m.m75 / m.groupby('band').m75.transform('mean') - 1
    return m
