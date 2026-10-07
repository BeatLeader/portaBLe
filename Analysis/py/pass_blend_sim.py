"""How pass PP should combine with acc/tech PP: compares, on the stored PP components of a per-map-curve DB (algo-b-permap):

  fade    pass * w(h) + R, w = 1 for h <= -2, 0.3 + 0.7 exp(-(h + 2)) above (PassFade, the built DB)
  trade   pass - min(0.7 pass, beta * max(0, R - R(h = -2))) + R: pass PP is traded for at most beta of the acc/tech PP gained
          above the fade start (piecewise; slope >= (1 - beta) of the unfaded one)
  blend   (pass^p + R^p)^(1/p): a p-norm, pass PP dominates when acc/tech PP is small and merges into it as accuracy rises
          (p = 1 is today's sum, p -> inf the maximum)

with R = acc PP + tech PP, h = log((1 - predictedAcc) / (1 - acc)). For each, the power-law gamma and acc scale are re-solved so the
median player PP of ranks 1-1000 and 10 001-50 000 match the source DB (as for every B variant). Reports the fade's goals
(burst overpay, FS/SF and pass shares), top plays, Megametric by map band, low-accuracy payout and monotonicity on every map.

Usage: python pass_blend_sim.py --db wwwroot/test-algo-b-permap.db --burst burst_maps.csv
       [--cands fade,trade:0.5,trade:0.7,blend:1.5,blend:2,blend:3] [--gamma 0.7328 --scale 1.1307 --k 0.75]
"""
import argparse, sqlite3
import numpy as np, pandas as pd, numpy.linalg as la

ap = argparse.ArgumentParser()
ap.add_argument("--db", required=True)
ap.add_argument("--burst", required=True, help="per-map burstiness (lb_id, burst), as used for ALGO_ACC_TEST's burst check")
ap.add_argument("--cands", default="fade,trade:0.5,trade:0.7,blend:1.5,blend:2,blend:3")
ap.add_argument("--gamma", type=float, default=0.7328); ap.add_argument("--scale", type=float, default=1.1307)
ap.add_argument("--k", type=float, default=0.75); ap.add_argument("--eps", type=float, default=0.0016)
args = ap.parse_args()
G1, S1 = args.gamma, args.scale

def inflate(x): return 650.0 * np.power(np.maximum(x, 0), 1.3) / 650.0 ** 1.3
def deflate(y): return np.power(np.maximum(y, 0) * 650.0 ** 1.3 / 650.0, 1 / 1.3)
def curve(a, g, e): return np.power(np.maximum(1 - a + e, 1e-9) / (0.05 + e), -g)
def fade(h): return np.where(h <= -2, 1.0, 0.3 + 0.7 * np.exp(-(h + 2)))
def hadv(p, a): return np.log(np.maximum(1 - p, 1e-4) / np.maximum(1 - a, 1e-4))

con = sqlite3.connect(args.db)
s = pd.read_sql("select PlayerId, LeaderboardId lb, Accuracy acc, Modifiers mods, Pp, PassPP, AccPP, TechPP from Scores where Pp > 0", con)
L = pd.read_sql("""select l.Id lb, l.Name, l.DifficultyName d, l.Stars, l.PassRating p0, l.AccRating a0, l.TechRating t0, l.PredictedAcc q0,
  m.SSPassRating p1, m.SSAccRating a1, m.SSTechRating t1, m.SSPredictedAcc q1, m.FSPassRating p2, m.FSAccRating a2, m.FSTechRating t2,
  m.FSPredictedAcc q2, m.SFPassRating p3, m.SFAccRating a3, m.SFTechRating t3, m.SFPredictedAcc q3
  from Leaderboards l left join ModifiersRating m on m.Id = l.ModifiersRatingId""", con).set_index('lb')
mods = s.mods.fillna('').str.upper()
col = np.select([mods.str.contains('SF'), mods.str.contains('FS'), mods.str.contains('SS')], [3, 2, 1], 0)
Q4 = L.loc[s.lb, ['q0', 'q1', 'q2', 'q3']].to_numpy()
pred = Q4[np.arange(len(s)), col]; pred = np.where(np.isnan(pred) | (pred <= 0), Q4[:, 0], pred)
acc = s.acc.to_numpy(); lbs = s.lb.to_numpy(); e = args.eps + args.k * (1 - pred); h = hadv(pred, acc)
t = (s.PassPP + s.AccPP + s.TechPP).to_numpy()
kf = np.where(t > 0, deflate(s.Pp.to_numpy()) / np.where(t > 0, t, 1), 0)
PASS = s.PassPP.to_numpy() * kf / fade(h)                                      # unfaded pass PP (modifier multiplier included)
KA = s.AccPP.to_numpy() * kf / (curve(acc, G1, e) / curve(pred, G1, e)) / S1   # 15.5 * 34 * LowNoteNerf * multiplier
TT = s.TechPP.to_numpy() * kf / np.exp(1.9 * acc)
acc0 = np.clip(1 - (1 - pred) * np.exp(2.0), 0, 1)                             # fade start, h = -2

def parts(kind, x, gamma, scale, a=acc):
    R = KA * scale * curve(a, gamma, e) / curve(pred, gamma, e) + TT * np.exp(1.9 * a)
    if kind == 'fade':
        ps = PASS * fade(hadv(pred, a)); return ps, R, ps + R
    if kind == 'trade':
        R0 = KA * scale * curve(acc0, gamma, e) / curve(pred, gamma, e) + TT * np.exp(1.9 * acc0)
        ps = PASS - np.minimum(0.7 * PASS, x * np.maximum(0, R - R0)); return ps, R, ps + R
    tot = np.power(np.power(PASS, x) + np.power(R, x), 1 / x)
    return PASS * np.power(PASS / np.maximum(tot, 1e-9), x - 1), R, tot      # Euler share of pass (homogeneous p-norm)

player = pd.factorize(s.PlayerId)[0]; n_pl = player.max() + 1; plays = np.bincount(player)
def totals(pp):
    o = np.lexsort((-pp, player)); pc = player[o]
    start = np.r_[0, np.flatnonzero(np.diff(pc)) + 1]
    rank_in = np.arange(len(pc)) - np.repeat(start, np.diff(np.r_[start, len(pc)]))
    w = np.empty(len(pp)); w[o] = 0.965 ** rank_in
    top = np.zeros(n_pl); np.maximum.at(top, player, pp)
    return np.bincount(player, weights=pp * w, minlength=n_pl), w, top
def megametric(pp, w, top):
    d = pd.DataFrame({'lb': lbs, 'w': w, 'rel': pp / top[player] * w, 'ok': plays[player] > 75})
    n = d.groupby('lb').size()
    d = d[d.ok].sort_values(['lb', 'w'], ascending=[True, False]); d['rk'] = d.groupby('lb').cumcount()
    d = d[d.rk < (n.reindex(d.lb).to_numpy() * 0.33).astype(int)]
    g = d.groupby('lb').rel.agg(['mean', 'size'])
    return g['mean'].where(g['size'] > 10, 0.0)

base = s.Pp.to_numpy(); bt = totals(base)[0]
brank = pd.Series(bt).rank(ascending=False, method='first').to_numpy()
bands = [(brank <= 1000), (brank >= 10001) & (brank <= 50000)]
target = np.array([np.median(bt[b]) for b in bands])
def solve(kind, x):
    if kind == 'fade': return G1, S1
    f = lambda v: np.log(np.array([np.median(tt[b]) for tt in [totals(inflate(parts(kind, x, v[0], np.exp(v[1]))[2]))[0]] for b in bands]) / target)
    v = np.array([G1, np.log(S1)])
    for _ in range(8):
        r = f(v)
        if np.abs(r).max() < 2e-4: break
        J = np.column_stack([(f(v + d) - r) / 1e-3 for d in (np.array([1e-3, 0]), np.array([0, 1e-3]))])
        v = v - la.solve(J, r)
    return v[0], float(np.exp(v[1]))

burst = pd.read_csv(args.burst, index_col=0).burst; burst.index = burst.index.astype(str)
many = L.index[pd.Series(lbs).value_counts().reindex(L.index).fillna(0) >= 100]
L['band'] = pd.cut(1 - L.q0, [0, 0.01, 0.02, 0.035, 1], labels=['<1%', '1-2%', '2-3.5%', '>3.5%'])
def burst_overpay(mega):
    m = pd.DataFrame({'mega': mega, 'stars': L.Stars, 'burst': burst}).dropna(); m = m[m.index.isin(many)]
    X = np.column_stack([np.ones(len(m)), m.stars, m.stars ** 2, m.burst])
    b, *_ = la.lstsq(X, m.mega.to_numpy(), rcond=None)
    return b[3] * m.burst.std()

# monotonicity / flatness on every map and speed-mod rating, from the ratings (no scores needed)
grid = np.linspace(0.3, 0.9999, 2000)[None, :]
def curve_check(kind, x, gamma, scale):
    worst_loss, min_ratio = 0.0, np.inf
    for j in range(4):
        P, A, T, Q = (L[f'{c}{j}'].fillna(0).to_numpy()[:, None] for c in 'patq')
        ok = (Q[:, 0] > 0) & (A[:, 0] > 0); P, A, T, Q = P[ok], A[ok], T[ok], Q[ok]
        ej = args.eps + args.k * (1 - Q)
        nerf = A * curve(Q, G1, ej) / (15.5 * S1)                              # LowNoteNerf from the stored acc rating
        pas = np.maximum(15.2 * np.exp(np.power(P, 1 / 2.62)) - 30, 0)
        R = 15.5 * 34 * nerf * scale * curve(grid, gamma, ej) / curve(Q, gamma, ej) + 1.08 * T * np.exp(1.9 * grid)
        if kind == 'fade': tot = pas * fade(hadv(Q, grid)) + R
        elif kind == 'trade':
            a0 = np.clip(1 - (1 - Q) * np.exp(2.0), 0, 1)
            R0 = 15.5 * 34 * nerf * scale * curve(a0, gamma, ej) / curve(Q, gamma, ej) + 1.08 * T * np.exp(1.9 * a0)
            tot = pas - np.minimum(0.7 * pas, x * np.maximum(0, R - R0)) + R
        else: tot = np.power(np.power(pas, x) + np.power(R, x), 1 / x)
        pp = inflate(tot)
        worst_loss = max(worst_loss, float((np.maximum.accumulate(pp, axis=1) - pp).max()))
        d = np.diff(pp, axis=1); dn = np.diff(inflate(pas + R), axis=1)
        min_ratio = min(min_ratio, float(np.quantile((d / dn).min(axis=1), 0.05)))
    return worst_loss, min_ratio

rows = []
for cand in args.cands.split(','):
    kind, x = (cand.split(':') + ['0'])[:2]; x = float(x)
    g, sc = solve(kind, x)
    ps, R, tot = parts(kind, x, g, sc); pp = inflate(tot)
    tt, w, top = totals(pp); mega = megametric(pp, w, top)
    infl = np.where(tot > 0, pp / np.maximum(tot, 1e-9), 0)
    t1000 = np.argsort(-pp)[:1000]; fsf = (col == 2) | (col == 3)
    top100 = np.isin(player, np.argsort(-tt)[:100])
    ww = pp * w
    lo = h < -2
    loss, ratio = curve_check(kind, x, g, sc)
    mm = mega[mega > 0]
    easy = np.isin(lbs, L.index[L.Stars < 4])
    rows.append({'candidate': cand, 'gamma / acc scale': f"{g:.3f} / {sc:.3f}",
                 'burst overpay (Megametric / SD burst)': round(burst_overpay(mega), 4),
                 'FS/SF share of top 1000 scores': f"{fsf[t1000].mean():.1%}",
                 'pass share of top-100 PP': f"{(ps * infl * w)[top100].sum() / ww[top100].sum():.1%}",
                 'FS/SF share of top-100 PP': f"{ww[top100 & fsf].sum() / ww[top100].sum():.1%}",
                 'median PP change ranks 1-100 / 50 001+': " / ".join(f"{np.median(tt[b] / bt[b]) - 1:+.1%}" for b in (brank <= 100, brank > 50000)),
                 'PP of scores below the fade start (h < -2), median vs DB': f"{np.median(pp[lo] / base[lo]) - 1:+.1%}",
                 'Megametric <1% / 1-2% / 2-3.5% / >3.5%': " / ".join(f"{v:.3f}" for v in mm.groupby(L.band.reindex(mm.index), observed=True).mean()),
                 'Megametric SD / share >= 0.65 (maps with >= 100 scores)': f"{mm[mm.index.isin(many)].std():.3f} / {(mm[mm.index.isin(many)] >= 0.65).mean():.1%}",
                 'top 3 plays': "; ".join(f"{pp[i]:.0f} {L.Name[lbs[i]][:16]} {L.d[lbs[i]]} {acc[i] * 100:.2f}%" for i in np.argsort(-pp)[:3]),
                 'highest score / best on <4* map': f"{pp.max():.0f} / {pp[easy].max():.0f}",
                 'worst PP lost as acc rises (all maps, mods)': f"{loss:.1f}",
                 'p5 over maps of min slope / unfaded slope': f"{ratio:.2f}"})
    print(f"{cand}: done (gamma {g:.4f}, scale {sc:.4f})", flush=True)
pd.set_option('display.width', 250); pd.set_option('display.max_colwidth', 60)
print(pd.DataFrame(rows).set_index('candidate').T.to_string())
