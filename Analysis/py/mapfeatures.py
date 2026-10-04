"""Map-level feature aggregation from the per-swing analyzer table (RatingsDump swings_{tag}.csv.gz)."""
import numpy as np
import pandas as pd


def _pct(g, col, qs=(0.5, 0.9, 0.99)):
    out = {}
    v = g[col].values
    for q in qs:
        out[f"{col}_p{int(q*100)}"] = np.quantile(v, q) if len(v) else np.nan
    out[f"{col}_mean"] = v.mean() if len(v) else np.nan
    return out


def rolling_peak(values, window):
    if len(values) < window:
        return values.mean() if len(values) else 0.0
    c = np.cumsum(np.insert(values, 0, 0.0))
    return ((c[window:] - c[:-window]) / window).max()


def aggregate(sw: pd.DataFrame) -> pd.DataFrame:
    rows = []
    for lb, g in sw.groupby("lb_id", sort=False):
        g = g.sort_values("seconds")
        r = {"lb_id": lb, "sw_n": len(g)}
        dur = max(g.seconds.max() - g.seconds.min(), 1.0)
        r["sw_density"] = len(g) / dur
        for col in ["swing_speed", "frequency", "angle_strain", "reposition", "rotation", "hit_distance", "stress", "swing_diff", "swing_tech", "njs"]:
            r.update(_pct(g, col))
        for col, v in (("swing_diff", g.swing_diff.values), ("swing_speed", g.swing_speed.values), ("swing_tech", g.swing_tech.values), ("frequency", g.frequency.values)):
            for w in (8, 32, 128):
                r[f"{col}_peak{w}"] = rolling_peak(v, w)
        r["frac_parity"] = g.parity_error.mean()
        r["frac_dot"] = (g.cut_direction == 8).mean()
        r["frac_chain"] = g.is_chain.mean()
        r["frac_multi"] = (g.n_cubes > 1).mean()
        r["frac_stream"] = g.is_stream.mean()
        r["frac_linear"] = g.is_linear.mean()
        r["frac_bomb"] = g.bomb_avoidance.mean()
        r["frac_forehand"] = g.forehand.mean()
        r["hand_balance"] = g.hand.mean()
        # low-speed / high-speed shares: swings faster than 6 Hz etc
        for thr in (4, 6, 8):
            r[f"frac_freq_gt{thr}"] = (g.frequency > thr).mean()
        rows.append(r)
    return pd.DataFrame(rows)
