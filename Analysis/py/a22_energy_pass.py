"""A22: pass rating from the game's energy bar, and the stamina rating (analyzer branch stamina-dev).

Energy model (Beat Saber): energy starts at 0.5, +0.01 per hit note, -0.15 per missed note, fail at 0, capped at 1. Every note of
a swing is missed with probability sigmoid(a * (ln d - theta)), d = the swing's pass difficulty (SwingDiff, optionally with the
per-swing terms from a21), theta = player skill on the same log scale. P(clear | theta) is computed exactly by dynamic programming
over the energy (1 % steps) for all maps at once; the rating is theta50 (P(clear) = 0.5), reported as exp(theta50) so it keeps
SwingDiff units like today's pass rating. One constant (a, the miss-probability slope) is fitted to the measured pass difficulty.

Stamina (stamina-dev StaminaCalculator, ported): per-swing kinetic energy cost (swing rate squared, angle strain, resets x2, holding
the arms up), smallest energy capacity that never runs out with 4-minute full regeneration, per hand, 0.9 max + 0.1 min. Ported
as-is and with swing rate taken directly from SwingFrequency (already per second in the current analyzer; the port multiplies it by
BPM / 60 again).

Usage: python a22_energy_pass.py --swings <swings_prod.csv.gz> --pass-maps <a19_pass_maps.parquet> --out <dir> [--a21 a21_results.json]
"""
import argparse, json, os
import numpy as np, pandas as pd

ap = argparse.ArgumentParser()
ap.add_argument("--swings", required=True); ap.add_argument("--pass-maps", required=True); ap.add_argument("--out", required=True)
ap.add_argument("--a21", default=None, help="use the per-swing terms of a21's 'swing' fit")
ap.add_argument("--a21-kind", default="swing", help="which a21 fit's per-swing terms to use (swing / both)")
ap.add_argument("--fit-maps", type=int, default=700)
args = ap.parse_args()
cols = ["lb_id", "seconds", "bpm_time", "hand", "x", "cut_direction", "n_cubes", "parity_error", "frequency", "angle_strain", "stress_mult",
        "stress", "swing_speed", "low_speed_falloff", "njs_buff", "wall_buff", "is_stream", "swing_diff", "entry_y", "exit_y"]
S = pd.read_csv(args.swings, usecols=cols, dtype={"lb_id": str})
PM = pd.read_parquet(args.pass_maps).set_index("lb_id")
S = S[S.lb_id.isin(PM.index)].sort_values(["lb_id", "seconds", "hand"], kind="stable").reset_index(drop=True)
lbs = S.lb_id.unique(); PM = PM.loc[lbs]; b = PM.b.values

def r2(x, y):
    c = np.polyfit(x, y, 3); return 1 - np.var(y - np.polyval(c, x)) / np.var(y)

# ------------------------------------------------------------------ stamina (stamina-dev), as-is and unit-fixed
def stamina_hand(g, fixed):
    if len(g) < 2: return 0.0
    bpm_s = (g.bpm_time / (g.seconds / 60)).replace([np.inf, -np.inf], np.nan).bfill().fillna(120).values
    sps = np.maximum(g.frequency.values * (1 if fixed else bpm_s / 60), 2)
    pt2 = 0.025 ** 2
    cost = sps ** 2 / (np.pi ** 2 * sps ** 2 * pt2 + 1)
    cost = cost * (1 + g.angle_strain.values * 0.1 * 5.0 * g.stress_mult.values)
    cost = np.where(g.parity_error.values == 1, cost * 2, cost)
    ypos = (g.entry_y.values + g.exit_y.values) / 2
    bt = g.bpm_time.values
    hold = np.minimum(np.diff(bt) * 60 / bpm_s[1:], 1.0)
    cost[:-1] = cost[:-1] + hold * (1 + ypos[:-1]) * 50.0
    bpm0 = bpm_s[0]; hi = cost.sum(); lo = 0.0
    if hi <= 0: return 0.0
    dbeat = np.diff(np.r_[0.0, bt])
    while abs(1 - lo / hi) > 1e-4:
        cur = (lo + hi) / 2; regen = cur / 240 * (60 / bpm0) * dbeat
        e = cur; ok = True
        for r_, c_ in zip(regen, cost):
            e = min(cur, e + r_) - c_
            if e < 0: ok = False; break
        if ok: hi = cur
        else: lo = cur
    return (lo + hi) / 2 / 7170.0

stam_cache = os.path.join(args.out, "a22_stamina.parquet")
if os.path.exists(stam_cache): ST = pd.read_parquet(stam_cache)
else:
    rows = []
    for lb, g in S.groupby("lb_id", sort=False):
        r = {"lb_id": lb}
        for fixed in (False, True):
            hs = [stamina_hand(g[g.hand == h], fixed) for h in (0, 1)]
            r["stamina_fixed" if fixed else "stamina_asis"] = max(hs) * 0.9 + min(hs) * 0.1
        rows.append(r)
    ST = pd.DataFrame(rows); ST.to_parquet(stam_cache)
PM = PM.join(ST.set_index("lb_id"))
print(f"{len(lbs)} maps. stamina as-is vs unit-fixed r {np.corrcoef(PM.stamina_asis, PM.stamina_fixed)[0, 1]:.3f}; "
      f"vs BPM r {np.corrcoef(PM.stamina_asis, PM.bpm)[0, 1]:+.3f} / {np.corrcoef(PM.stamina_fixed, PM.bpm)[0, 1]:+.3f}")
for c in ["stamina_asis", "stamina_fixed"]:
    x = np.log(PM[c].clip(lower=1e-6))
    X = np.column_stack([np.ones(len(PM)), np.polyval(np.polyfit(PM["pass"], b, 3), PM["pass"]), x])
    bb, *_ = np.linalg.lstsq(X, b, rcond=None); res = b - X @ bb
    print(f"  {c}: alone R2 {r2(x, b):.3f}; pass rating + log stamina R2 {1 - res.var() / b.var():.4f} (pass alone {r2(PM['pass'].values, b):.4f})")

# ------------------------------------------------------------------ energy-bar pass rating
d = S.swing_diff.values.copy()
if args.a21:
    sp = json.load(open(args.a21))[args.a21_kind]["swing_params"]; k, cc, ch, cd, cp = sp
    cross = (((S.hand == 0) & (S.x == 3)) | ((S.hand == 1) & (S.x == 0))).values
    sm = 2.0 * k * S.stress.values / (k * S.stress.values + 2.0) + 1.0
    d = (S.swing_speed * S.low_speed_falloff).values * sm * (S.njs_buff * np.where(S.is_stream == 1, 1.05, 1.0) * S.wall_buff).values \
        * (1 + cc * cross) * (1 + ch * S.cut_direction.isin([2, 3]).values) * (1 + cd * S.cut_direction.isin([4, 5, 6, 7]).values) * (1 + cp * S.parity_error.values)
    print("per-swing terms from a21:", np.round(sp, 3))
logd = np.log(np.maximum(d, 1e-3)).astype(np.float32)
notes = S.n_cubes.clip(1, 4).values
# expand to notes, pad maps into a (maps x notes) matrix
rep = np.repeat(np.arange(len(S)), notes)
mid = pd.factorize(S.lb_id.values)[0][rep]
ld = logd[rep]
cnt = np.bincount(mid); L = cnt.max()
pos = np.arange(len(rep)) - np.repeat(np.r_[0, np.cumsum(cnt)[:-1]], cnt)
LD = np.full((len(cnt), L), -20.0, np.float32); LD[mid, pos] = ld
ACT = np.zeros((len(cnt), L), bool); ACT[mid, pos] = True
print(f"{len(rep):,} notes; longest map {L} notes")

def p_clear(theta, a, rows):
    """exact P(clear) for each map in rows at skill theta[rows] (energy in 1 % steps, index 0..100)"""
    M = len(rows); E = np.zeros((M, 101), np.float32); E[:, 50] = 1.0
    th = theta.astype(np.float32)[:, None] if theta.ndim == 1 else theta
    LDr, ACTr = LD[rows], ACT[rows]
    for j in range(L):
        act = ACTr[:, j]
        if not act.any(): break
        p = 1.0 / (1.0 + np.exp(-a * (LDr[:, j] - th[:, 0])))
        p = np.where(act, p, 0.0).astype(np.float32)[:, None]
        hit = E * (1 - p); miss = E * p
        N = np.zeros_like(E)
        N[:, 1:] = hit[:, :-1]; N[:, 100] += hit[:, 100]; N[:, 0] = 0
        N[:, 1:86] += miss[:, 16:]                                     # e - 15, still alive
        E = np.where(act[:, None], N, E)
    return E.sum(axis=1)

def theta50(a, rows, iters=13):
    lo = np.full(len(rows), -2.0); hi = np.full(len(rows), 5.0)
    for _ in range(iters):
        mid_ = (lo + hi) / 2
        pc = p_clear(mid_[:, None], a, rows)
        hi = np.where(pc >= 0.5, mid_, hi); lo = np.where(pc >= 0.5, lo, mid_)
    return (lo + hi) / 2

rng = np.random.default_rng(0)
fit_rows = np.sort(rng.choice(len(cnt), size=min(args.fit_maps, len(cnt)), replace=False))
best = None
for a in [1.5, 2.5, 4.0, 6.0, 9.0]:
    t50 = theta50(a, fit_rows)
    sc = r2(t50, b[fit_rows]); print(f"  slope a {a:4.1f}: R2 on {len(fit_rows)} maps {sc:.4f} (production pass on the same maps {r2(PM['pass'].values[fit_rows], b[fit_rows]):.4f})", flush=True)
    if best is None or sc > best[1]: best = (a, sc)
a = best[0]
t50 = theta50(a, np.arange(len(cnt)))
PM["energy_rating"] = np.exp(t50)
sc = r2(t50, b)
print(f"energy-bar rating (a {a}) on all {len(cnt)} maps: R2 {sc:.4f} vs production pass {r2(PM['pass'].values, b):.4f}")
X = np.column_stack([np.ones(len(PM)), t50, t50 ** 2, t50 ** 3, np.log(PM.stamina_fixed.clip(lower=1e-6))]); bb, *_ = np.linalg.lstsq(X, b, rcond=None)
print(f"  + log stamina (unit-fixed), same cubic: R2 {1 - np.var(b - X @ bb) / np.var(b):.4f} (stamina weight {bb[-1]:+.3f})")
DM = pd.read_parquet(os.path.join(os.path.dirname(args.pass_maps), "..", "data2", "maps.parquet"), columns=["Id", "ModeName"]).set_index("Id").ModeName
mode = DM.reindex(PM.index).values
c_ = np.polyfit(t50, b, 3); res_ = b - np.polyval(c_, t50)
print("  residual by mode: " + ", ".join(f"{m} {res_[mode == m].mean():+.2f} (n {np.sum(mode == m)})" for m in pd.unique(mode)))
c = np.polyfit(t50, b, 3); PM["res_energy"] = b - np.polyval(c, t50)
c0 = np.polyfit(PM["pass"], b, 3); PM["res_pass"] = b - np.polyval(c0, PM["pass"])
print(f"maps off by > 1 logit: production {(PM.res_pass.abs() > 1).sum()}, energy {(PM.res_energy.abs() > 1).sum()}; corr of the two residuals {PM.res_pass.corr(PM.res_energy):.3f}")
PM.reset_index()[["lb_id", "b", "pass", "energy_rating", "res_energy", "res_pass", "stamina_asis", "stamina_fixed"]].to_parquet(os.path.join(args.out, "a22_energy_maps.parquet"))
