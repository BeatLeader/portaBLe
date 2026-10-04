"""Numpy port of portaBLe's PP model (Refresh/ReplayUtils.cs) plus helpers.

Everything here mirrors the C# so alternative rating sets / formulas can be evaluated over the
3M-score dump in seconds. `tests/validate_ppmodel.py` checks it against PP values computed by the C# code.
"""
import numpy as np

# --- acc curves (ReplayUtils.pointList / pointList2) -------------------------------------------------
CURVE_ACC = np.array([1.0, 0.999, 0.9975, 0.995, 0.9925, 0.99, 0.9875, 0.985, 0.9825, 0.98, 0.9775, 0.975, 0.9725,
                      0.97, 0.965, 0.96, 0.955, 0.95, 0.94, 0.93, 0.92, 0.91, 0.9, 0.875, 0.85, 0.825, 0.8, 0.75,
                      0.7, 0.65, 0.6, 0.0])
# Curve  (used to turn predicted accuracy into acc rating)
CURVE1 = np.array([7.424, 6.241, 5.158, 4.010, 3.241, 2.700, 2.303, 2.007, 1.786, 1.618, 1.490, 1.392, 1.315, 1.256,
                   1.167, 1.101, 1.047, 1.000, 0.919, 0.847, 0.786, 0.734, 0.692, 0.606, 0.537, 0.480, 0.429, 0.345,
                   0.286, 0.246, 0.217, 0.000])
# Curve2 (used to turn accuracy into acc PP)
CURVE2 = np.array([7.424, 6.241, 5.158, 4.010, 3.241, 2.700, 2.303, 2.007, 1.786, 1.618, 1.490, 1.392, 1.315, 1.256,
                   1.167, 1.094, 1.039, 1.000, 0.931, 0.867, 0.813, 0.768, 0.729, 0.650, 0.581, 0.522, 0.473, 0.404,
                   0.345, 0.296, 0.256, 0.000])


def _interp(acc, ys):
    acc = np.asarray(acc, dtype=float)
    # np.interp needs ascending xs
    return np.interp(acc, CURVE_ACC[::-1], ys[::-1])


def curve1(acc):
    return _interp(acc, CURVE1)


def curve2(acc):
    return _interp(acc, CURVE2)


# portaBLe PpCurve (CurveMode.PowerLaw): replaces both Curve and Curve2; acc rating is scaled by POWER_ACC_SCALE
POWER = {"gamma": 0.601, "eps": 0.0016, "acc_scale": 1.052}
CURVE_MODE = "classic"  # or "power"


def power_curve(acc):
    acc = np.asarray(acc, dtype=float)
    return np.power(np.maximum(1 - acc + POWER["eps"], 1e-6) / (0.05 + POWER["eps"]), -POWER["gamma"])


def acc_rating_from_predicted(pred_acc, pass_rating=0.0, tech_rating=0.0):
    """ReplayUtils.AccRating: 15.5 / Curve(predictedAcc + 0.0022); fallback formula when no prediction."""
    pred_acc = np.asarray(pred_acc, dtype=float)
    with np.errstate(divide="ignore", invalid="ignore"):
        if CURVE_MODE == "power":
            main = 15.5 / power_curve(pred_acc + 0.0022) * POWER["acc_scale"]
        else:
            main = 15.5 / curve1(pred_acc + 0.0022)
        tiny_tech = 0.0208 * tech_rating + 1.1284
        fallback = (-np.power(tiny_tech, -pass_rating) + 1) * 8 + 2 + 0.01 * tech_rating * pass_rating
        out = np.where(pred_acc > 0, main, fallback)
    return np.where(np.isfinite(out), out, 0.0)


def inflate(x):
    return 650.0 * np.power(x, 1.3) / 650.0 ** 1.3


def pp_components(acc, acc_rating, pass_rating, tech_rating):
    """ReplayUtils.GetPp -> (passPP, accPP, techPP) before inflation."""
    acc = np.asarray(acc, dtype=float)
    with np.errstate(over="ignore", invalid="ignore"):
        pass_pp = 15.2 * np.exp(np.power(pass_rating, 1 / 2.62)) - 30.0
    pass_pp = np.where(np.isfinite(pass_pp) & (pass_pp > 0), pass_pp, 0.0)
    acc_pp = (power_curve(acc) if CURVE_MODE == "power" else curve2(acc)) * acc_rating * 34.0
    tech_pp = np.exp(1.9 * acc) * 1.08 * tech_rating
    return pass_pp, acc_pp, tech_pp


def pp(acc, acc_rating, pass_rating, tech_rating, mult=1.0):
    """Full PP and its (pass, acc, tech) split after inflation (no-NF scores)."""
    p, a, t = pp_components(acc, acc_rating * mult, pass_rating * mult, tech_rating * mult)
    s = p + a + t
    full = inflate(s)
    with np.errstate(divide="ignore", invalid="ignore"):
        inc = np.where(s > 0, full / s, 0.0)
    return full, p * inc, a * inc, t * inc


def stars(acc_rating, pass_rating, tech_rating, at_acc=0.96):
    p, a, t = pp_components(np.full_like(np.asarray(acc_rating, dtype=float), at_acc), acc_rating, pass_rating, tech_rating)
    return inflate(p + a + t) / 52.0


PLAYER_WEIGHT = 0.965


def player_totals(df, pp_col="pp", player_col="player", extra_cols=()):
    """Player PP = sum of pp * 0.965^rank over their scores sorted by pp (PlayersRefresh)."""
    d = df.sort_values([player_col, pp_col], ascending=[True, False]).copy()
    d["_rank"] = d.groupby(player_col).cumcount()
    d["weight"] = PLAYER_WEIGHT ** d["_rank"]
    out = {c: (d[c] * d["weight"]).groupby(d[player_col]).sum() for c in (pp_col, *extra_cols)}
    import pandas as pd
    return pd.DataFrame(out), d
