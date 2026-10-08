"""A24: fatigue inside the energy-bar pass model (pass rating v2), from the stamina-dev energy costs.

Each hand has a stamina reservoir of capacity C (same for every map: a reference player); every swing drains the stamina-dev
StaminaCalculator cost (unit-fixed: swing rate = SwingFrequency, already per second), and the reservoir refills fully in 4 minutes.
Depletion D = 1 - reservoir / C (0 rested, 1 empty, up to 2 when overdrawn). The swing then counts as harder:
ln PassDiff_eff = ln PassDiff + phi * D. Two constants (C in units of the stamina rating scale 7170, phi), fitted against
pass difficulty from attempts on a map sample and checked on held-out songs; then the outliers and the time trend.

Usage: python a24_fatigue.py --swings <swings_prod.csv.gz> --pass-maps <a19_pass_maps.parquet> --maps-db <maps.parquet> --out <dir>
"""
import argparse, os, sys
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import energylib as el

ap = argparse.ArgumentParser()
for a in ["swings", "pass_maps", "maps_db", "out"]: ap.add_argument(f"--{a.replace('_', '-')}", dest=a, required=True)
ap.add_argument("--fit-maps", type=int, default=900)
args = ap.parse_args()
cols = ["lb_id", "seconds", "bpm_time", "hand", "x", "cut_direction", "n_cubes", "parity_error", "frequency", "angle_strain", "stress_mult",
        "stress", "swing_speed", "low_speed_falloff", "njs_buff", "wall_buff", "is_stream", "entry_y", "exit_y"]
S = pd.read_csv(args.swings, usecols=cols, dtype={"lb_id": str})
PM = pd.read_parquet(args.pass_maps).set_index("lb_id")
S = S[S.lb_id.isin(PM.index)].sort_values(["lb_id", "seconds", "hand"], kind="stable").reset_index(drop=True)
codes, lbs = pd.factorize(S.lb_id); PM = PM.loc[lbs]; b = PM.b.values
mode = pd.read_parquet(args.maps_db, columns=["Id", "ModeName"]).set_index("Id").ModeName.reindex(lbs).values
hands = S.groupby("lb_id", sort=False).hand.nunique().reindex(lbs).values
one_saber = hands < 2
song = PM.Hash.str.upper().values; half = np.array([int(s[:6], 16) % 2 for s in song])

# ---- stamina-dev energy cost per swing (unit-fixed), hold cost added to the previous swing of the same hand
sps = np.maximum(S.frequency.values, 2.0)
cost = sps ** 2 / (np.pi ** 2 * sps ** 2 * 0.025 ** 2 + 1) * (1 + S.angle_strain.values * 0.5 * S.stress_mult.values)
cost = np.where(S.parity_error.values == 1, cost * 2, cost)
g = S.groupby(["lb_id", "hand"], sort=False)
nxt_t = g.seconds.shift(-1).values
hold = np.where(np.isfinite(nxt_t), np.minimum(nxt_t - S.seconds.values, 1.0), 0.0)
cost = cost + hold * (1 + (S.entry_y.values + S.exit_y.values) / 2) * 50.0
# order of (map, hand) runs for the sequential reservoir
perm = np.lexsort((S.seconds.values, S.hand.values, codes))
key = codes[perm] * 2 + S.hand.values[perm]
run_start = np.r_[True, key[1:] != key[:-1]]
t_p, c_p = S.seconds.values[perm], cost[perm]

def depletion(C):
    """per swing depletion D before the swing, reservoir capacity C (regenerates C / 240 per second)"""
    D = np.empty(len(perm)); e = C; last = 0.0; regen = C / 240.0
    for i in range(len(perm)):
        if run_start[i]: e = C; last = t_p[i]
        e = min(C, e + regen * (t_p[i] - last)); last = t_p[i]
        D[i] = min(max(1.0 - e / C, 0.0), 2.0)
        e -= c_p[i]
    out = np.empty(len(perm)); out[perm] = D
    return out

base_ld = np.log(np.maximum(el.pass_diff(S), 1e-3))
notes = S.n_cubes.clip(1, 4).values
rng = np.random.default_rng(0); fit_rows = np.sort(rng.choice(len(lbs), size=min(args.fit_maps, len(lbs)), replace=False))
fit_mask = np.isin(codes, fit_rows)
remap = -np.ones(len(lbs), int); remap[fit_rows] = np.arange(len(fit_rows))

def rating(ld, rows_mask=None):
    """log rating (skill50 + log One Saber factor) for all maps, or for the fit sample"""
    if rows_mask is None:
        LD, ACT = el.note_matrix(ld, notes, codes); idx = np.arange(len(lbs))
    else:
        LD, ACT = el.note_matrix(ld[rows_mask], notes[rows_mask], remap[codes[rows_mask]]); idx = fit_rows
    return el.skill50(LD, ACT, el.V2["slope"]) + np.where(one_saber[idx], np.log(el.V2["one_saber"]), 0.0), idx

t0, _ = rating(base_ld, fit_mask)
print(f"{len(lbs)} maps; v2 without fatigue on the {len(fit_rows)}-map sample: R2 {el.r2_cubic(t0, b[fit_rows]):.4f}", flush=True)
Dcache = {}
best = (None, el.r2_cubic(t0, b[fit_rows]))
for C in [2000.0, 5000.0, 10000.0, 20000.0, 40000.0]:
    Dcache[C] = depletion(C)
    print(f"  capacity {C:>7.0f}: mean depletion {Dcache[C].mean():.3f}, share of swings > 0.5 depleted {np.mean(Dcache[C] > 0.5):.1%}", flush=True)
    for phi in [0.1, 0.25, 0.5, 1.0]:
        t, _ = rating(base_ld + phi * Dcache[C], fit_mask)
        sc = el.r2_cubic(t, b[fit_rows])
        print(f"    phi {phi:4.2f}: R2 {sc:.4f}", flush=True)
        if sc > best[1]: best = ((C, phi), sc)
print(f"best on the sample: {best[0]} R2 {best[1]:.4f}")
if best[0] is None:
    print("fatigue does not improve the sample; stopping"); sys.exit(0)
C, phi = best[0]
D = Dcache[C]
tf, _ = rating(base_ld + phi * D); t0_all, _ = rating(base_ld)
for name, t in [("v2", t0_all), ("v2 + fatigue", tf)]:
    print(f"{name:14s} all maps R2 {el.r2_cubic(t, b):.4f}; held-out halves (cubic fitted on one half, scored on the other): " +
          " / ".join(f"{1 - np.var(b[half != h] - np.polyval(np.polyfit(t[half == h], b[half == h], 3), t[half != h])) / np.var(b[half != h]):.4f}" for h in (0, 1)))
    c = np.polyfit(t, b, 3); e = b - np.polyval(c, t)
    print(f"   maps off > 1 logit {(np.abs(e) > 1).sum()}, > 2 {(np.abs(e) > 2).sum()}; residual vs map length r {np.corrcoef(e, PM.duration_min)[0, 1]:+.3f}")
    PM[f"res_{name}"] = e
PM["fatigue_delta"] = tf - t0_all
pd.set_option("display.width", 200)
print("\nmaps fatigue makes harder / easier (log rating change):")
o = PM.sort_values("fatigue_delta")
print(pd.concat([o.head(6), o.tail(6)])[["Name", "DifficultyName", "duration_min", "fatigue_delta", "res_v2", "res_v2 + fatigue"]].assign(Name=lambda x: x.Name.str[:26]).round(3).to_string())
pd.DataFrame({"capacity": [C], "phi": [phi]}).to_json(os.path.join(args.out, "a24_fatigue_params.json"), orient="records")
