"""A7: acc-PP bias between maps at equal player skill = f(rating source, curve shape, player skill).

Truth model (A2: slopes ~1, ordering identical across skill bands): a player of skill a gets error 1-acc = exp(d_j - a) on map j.
Design intent of the PP formula: equal skill => equal acc PP on every map. Bias(S, curve, a) = SD over maps of log accPP.
Rating of a map: accRating_j = K / curve(predicted_acc_j)   (the production convention: same acc PP at the predicted accuracy).
Sources S for predicted accuracy: ML (production), oracle (data-implied d_j), algorithms (song-grouped CV predictions of d_j).
Curves: production Curve2 and a constant-exponent power law in error, curve(acc) = (1-acc)^-gamma.
"""
import os, sys, json
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import ppmodel as pm

OUT = os.path.join(os.path.dirname(__file__), "..", "out")
tag = sys.argv[1] if len(sys.argv) > 1 else "prod"
mp = pd.read_csv(os.path.join(OUT, f"a06_maps_{tag}.csv"), dtype={"lb_id": str})
w = (mp.n >= 100).values
print("maps in evaluation (>=100 scores):", int(w.sum()))

# Link data-implied difficulty to a predicted accuracy with the TRUE scale (unit slope), calibrated so that the
# average predicted error equals the ML's: pred_j = 1 - exp(d_hat_j - a_ref)
a_ref = mp.d[w].mean() - mp.ai_err[w].mean()
print(f"reference skill implied by the ML ratings: a_ref = {a_ref:.2f} -> avg-map acc {1-np.exp(-a_ref):.4f}; "
      f"d on ML log-error slope = {np.polyfit(mp.ai_err, mp.d, 1)[0]:.3f} (>1: ML compresses real difficulty differences)")
pred_of = lambda dh: np.clip(1 - np.exp(np.asarray(dh) - a_ref), 0.5, 0.9995)
sources = {
    "ML (production predictedAcc)": mp.predicted_acc.values,
    "algo: 8 swing features, linear": pred_of(mp.d_hat_compact_lin),
    "algo: all swing aggregates, linear": pred_of(mp.d_hat_all_lin),
    "ML + 8 swing features": pred_of(mp.d_hat_ml_plus),
    "oracle (score-implied d_j)": pred_of(mp.d),
}
GAMMA = 0.57  # best log-log fit of Curve2 between acc 0.90 and 0.995
curves = {
    "Curve2 (production)": lambda acc: pm.curve2(acc),
    f"power law gamma={GAMMA}": lambda acc: np.power(np.maximum(1 - acc, 1e-4), -GAMMA),
}
d_true = mp.d.values
d_mean = d_true[w].mean()
skills = (0.90, 0.93, 0.95, 0.97, 0.98)
rows = []
for cname, cf in curves.items():
    for sname, pred in sources.items():
        rating = 1.0 / cf(pred)
        r = {"curve": cname, "source": sname}
        for x in skills:
            a = d_mean - np.log(1 - x)
            acc = np.clip(1 - np.exp(d_true - a), 0.3, 0.9995)     # what a skill-a player really gets on map j
            r[f"{x:.2f}"] = np.std(np.log(cf(acc) * rating)[w])
        rows.append(r)
R = pd.DataFrame(rows)
pd.set_option("display.width", 200)
print("\nSD across maps of log(acc PP) for players of equal skill (0.10 ~ +-10% acc PP). Columns = player's accuracy on an average map:")
print(R.round(3).to_string(index=False))
R.to_csv(os.path.join(OUT, f"a07_accpp_bias_{tag}.csv"), index=False)

# local exponent of the production curve (power law => constant)
pts = np.array([0.6, 0.7, 0.8, 0.9, 0.95, 0.975, 0.99, 0.995])
loc = -np.diff(np.log(pm.curve2(pts))) / np.diff(np.log(1 - pts))
print("\nCurve2 local exponent over acc", [f"{a}->{b}" for a, b in zip(pts[:-1], pts[1:])], np.round(loc, 2))

# per-map slope (data) vs universal slope: extra skill-dependence a per-map curve could remove
pl = pd.read_csv(os.path.join(OUT, f"a01_player_skill_{tag}.csv"))
qa = pl[pl.n_scores >= 30].skill.quantile([0.10, 0.5, 0.90]).values
cj = pd.read_csv(os.path.join(OUT, f"a02_slopes_{tag}.csv"), dtype={"lb_id": str})[["lb_id", "c", "s"]]
mm = mp[["lb_id", "d", "n"]].merge(cj, on="lb_id", how="inner"); mm = mm[mm.n >= 100]
shape = lambda err: np.log(np.maximum(np.power(np.maximum(err, 1e-4), -GAMMA), 1e-9))   # power-law curve: log curve = -gamma log err
dd_univ = shape(np.exp(mm.d.values - qa[2])) - shape(np.exp(mm.d.values - qa[0]))
dd_slope = shape(np.exp(mm.c.values - mm.s.values * qa[2])) - shape(np.exp(mm.c.values - mm.s.values * qa[0]))
print(f"\nPer-map slopes (data), player skill p10 -> p90 (a_i {qa[0]:.2f} -> {qa[2]:.2f}):")
print(f"  with a power-law curve, universal slope gives identical log-PP gain on every map (SD {dd_univ.std():.3f});")
print(f"  using the measured per-map slopes the gain varies across maps with SD {dd_slope.std():.3f} "
      f"(= irreducible by a global curve; potential value of a per-map curve). Mean gain {dd_slope.mean():.2f} vs {dd_univ.mean():.2f}")
