"""Per-map acc curve calibration on a B + pass fade DB's stored PP components (no re-rating needed).

Candidates: curve_j(acc) = ((1 - acc + e_j) / (0.05 + e_j))^-gamma with e_j = eps0 + k (1 - p_j) (PpCurve.RelativeEpsilon = k,
anchored at the predicted accuracy p_j), pass fade on h = log((1 - p_j) / (1 - acc)), no AccCap; p_j optionally score-corrected
(score_correct.py). gamma and the acc scale are solved so the median player PP of ranks 1-1000 and 10 001-50 000 match the source DB.

Usage: python permap_curve_sim.py --db wwwroot/test-algo-power-flat-fade.db --correction score_correction.csv
                                  [--beatsaver Analysis/out/beatsaver_maps.csv] [--cands 0.0016:0.75,0:0.5]
The source DB must be built with --curve PowerLaw --gamma G0 --acc-scale S0 --pass-fade 1,0.3,-2 --acc-cap 0.6 (defaults below).
"""
import argparse, sqlite3
import numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument("--db", required=True)
ap.add_argument("--correction", default=None, help="score_correct.py output (column 'correction'); omit for none")
ap.add_argument("--beatsaver", default=None, help="fetch_beatsaver.py output, for the Megametric by upload year")
ap.add_argument("--cands", default="0:0.5,0:0.75,0:1.0,0.0016:0.5,0.0016:0.75")
ap.add_argument("--g0", type=float, default=0.5923); ap.add_argument("--s0", type=float, default=1.2448)
ap.add_argument("--cap", type=float, default=0.6)
args = ap.parse_args()
G0, S0, CAP, FS, FF, FC = args.g0, args.s0, args.cap, 1.0, 0.3, -2.0

def inflate(x): return 650.0 * np.power(np.maximum(x, 0), 1.3) / 650.0 ** 1.3
def deflate(y): return np.power(np.maximum(y, 0) * 650.0 ** 1.3 / 650.0, 1 / 1.3)
def curve(a, g, e=0.0016): return np.power(np.maximum(1 - a + e, 1e-9) / (0.05 + e), -g)
def fade(h): return np.where(h <= FC, 1.0, FF + (1 - FF) * np.exp(-FS * (h - FC)))
def hadv(p, a): return np.log(np.maximum(1 - p, 1e-4) / np.maximum(1 - a, 1e-4))

con = sqlite3.connect(args.db)
s = pd.read_sql("select PlayerId, LeaderboardId lb, Accuracy acc, Modifiers mods, Pp, PassPP, AccPP, TechPP from Scores where Pp > 0", con)
L = pd.read_sql("""select l.Id lb, l.Hash, l.Stars, l.PredictedAcc p, m.SSPredictedAcc ss, m.FSPredictedAcc fs, m.SFPredictedAcc sf
                   from Leaderboards l left join ModifiersRating m on m.Id = l.ModifiersRatingId""", con).set_index('lb')
# the prediction each score was judged against (PpFromScore uses the first speed modifier's)
mods = s.mods.fillna('').str.upper()
col = np.select([mods.str.contains('SF'), mods.str.contains('FS'), mods.str.contains('SS')], [3, 2, 1], 0)
P4 = L.loc[s.lb, ['p', 'ss', 'fs', 'sf']].to_numpy()
pred = P4[np.arange(len(s)), col]; pred = np.where(np.isnan(pred) | (pred <= 0), P4[:, 0], pred)
acc = s.acc.to_numpy(); lbs = s.lb.to_numpy()
# stored components -> pre-inflation parts, then undo cap / fade / curve to get the map constants
k_ = np.where((t := (s.PassPP + s.AccPP + s.TechPP).to_numpy()) > 0, deflate(s.Pp.to_numpy()) / np.where(t > 0, t, 1), 0)
a_cap = np.minimum(acc, 1 - (1 - pred) * np.exp(-CAP))
pass_unfaded = s.PassPP.to_numpy() * k_ / fade(hadv(pred, acc))           # the source DB faded on the uncapped advantage
K = s.AccPP.to_numpy() * k_ * curve(pred + 0.0022, G0) / (curve(a_cap, G0) * S0)  # 15.5 * 34 * LowNoteNerf * modifier multiplier
T = s.TechPP.to_numpy() * k_ / np.exp(1.9 * a_cap)
c = pd.read_csv(args.correction, index_col=0).correction.reindex(lbs).fillna(0).to_numpy() if args.correction else 0
p = np.clip(1 - np.exp(np.log(1 - pred) + c), 0.5, 0.9995)
h = hadv(p, acc); pass_f = pass_unfaded * fade(h)

def candidate(eps0, k, gamma, scale):
    e = eps0 + k * (1 - p)
    return inflate(pass_f + K * scale * curve(acc, gamma, e) / curve(p, gamma, e) + T * np.exp(1.9 * acc))

player = pd.factorize(s.PlayerId)[0]; n_players = player.max() + 1; plays = np.bincount(player)
def totals(pp):
    o = np.lexsort((-pp, player)); pc = player[o]
    start = np.r_[0, np.flatnonzero(np.diff(pc)) + 1]
    rank_in = np.arange(len(pc)) - np.repeat(start, np.diff(np.r_[start, len(pc)]))
    w = np.empty(len(pp)); w[o] = 0.965 ** rank_in
    top = np.zeros(n_players); np.maximum.at(top, player, pp)
    return np.bincount(player, weights=pp * w, minlength=n_players), w, top
def megametric(pp, w, top):  # as LeaderboardsRefresh: top third by weight of players with > 75 plays, mean of pp / top pp * weight
    d = pd.DataFrame({'lb': lbs, 'w': w, 'rel': pp / top[player] * w, 'ok': plays[player] > 75})
    n = d.groupby('lb').size()
    d = d[d.ok].sort_values(['lb', 'w'], ascending=[True, False]); d['rk'] = d.groupby('lb').cumcount()
    d = d[d.rk < (n.reindex(d.lb).to_numpy() * 0.33).astype(int)]
    g = d.groupby('lb').rel.agg(['mean', 'size'])
    return g['mean'].where(g['size'] > 10, 0.0)

base = s.Pp.to_numpy(); bt = totals(base)[0]
rank = pd.Series(bt).rank(ascending=False, method='first').to_numpy()
bands = [(rank >= 1) & (rank <= 1000), (rank >= 10001) & (rank <= 50000)]
target = np.array([np.median(bt[b]) for b in bands])
def solve(eps0, k, x=(0.6, 0.0)):
    x = np.array(x)
    f = lambda x: np.log(np.array([np.median(t[b]) for t in [totals(candidate(eps0, k, x[0], np.exp(x[1])))[0]] for b in bands]) / target)
    for _ in range(8):
        r = f(x)
        if np.abs(r).max() < 2e-4: break
        J = np.column_stack([(f(x + d) - r) / 1e-3 for d in (np.array([1e-3, 0]), np.array([0, 1e-3]))])
        x = x - np.linalg.solve(J, r)
    return x[0], float(np.exp(x[1]))

pn = (1 - np.exp(np.log(1 - L.p) + (pd.read_csv(args.correction, index_col=0).correction.reindex(L.index).fillna(0) if args.correction else 0)))
L['band'] = pd.cut(1 - pn, [0, 0.01, 0.02, 0.035, 1], labels=['easy <1%', '1-2%', '2-3.5%', 'hard >3.5%'])
if args.beatsaver:
    bs = pd.read_csv(args.beatsaver, dtype={'hash': str}).set_index('hash')
    L['era'] = pd.cut(L.Hash.str.lower().map(pd.to_datetime(bs.uploaded, format='ISO8601').dt.year), [0, 2019, 2021, 2023, 2100],
                      labels=['2018-19', '2020-21', '2022-23', '2024-26'])
top1000 = bands[0][player]
def report(name, pp):
    t, w, top = totals(pp); mega = megametric(pp, w, top); mega = mega[mega > 0]
    wp = pd.Series(pp * w)[top1000]
    row = {'name': name, **{f"top-1000 PP share {b}": f"{v:.1%}" for b, v in
                            (wp.groupby(L.band.reindex(lbs).to_numpy()[top1000], observed=True).sum() / wp.sum()).items()},
           **{f"Megametric {b}": round(v, 3) for b, v in mega.groupby(L.band.reindex(mega.index), observed=True).mean().items()}}
    if 'era' in L:
        row.update({f"Megametric {b}": round(v, 3) for b, v in mega.groupby(L.era.reindex(mega.index), observed=True).mean().items()})
    o = np.argsort(-pp)[:5]
    row['top 5 plays'] = ", ".join(f"{pp[i]:.0f} ({L.Stars.get(lbs[i], np.nan):.1f}*, {acc[i] * 100:.2f}%)" for i in o)
    return row

rows = [report('source DB', base)]
for cand in args.cands.split(','):
    eps0, k = (float(x) for x in cand.split(':'))
    g, sc = solve(eps0, k)
    print(f"eps0 {eps0} k {k}: gamma {g:.4f}, acc scale {sc:.4f}", flush=True)
    rows.append(report(f"eps0 {eps0} k {k} (gamma {g:.3f}, scale {sc:.3f})", candidate(eps0, k, g, sc)))
pd.set_option('display.width', 250); pd.set_option('display.max_colwidth', 120)
R = pd.DataFrame(rows).set_index('name')
print(R.drop(columns='top 5 plays').T.to_string()); print(R['top 5 plays'].to_string())
