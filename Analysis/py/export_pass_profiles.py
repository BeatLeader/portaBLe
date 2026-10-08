"""Add "where players fail" data (pass rating v2, energy bar) to the AccLossProfiles rows of a portaBLe DB.

Per map (key "pass" in the profile JSON): every swing's ln PassDiff (quantized to one byte, step 0.03 from -3) and note count
(base64, two bytes per swing, time order), swings per ~5 s section (the same sections as the accuracy tab), the map's own pass
threshold skill50, each section's top causes and the map-wide causes at that threshold, and where the attempts export covers the map
the observed fail hazard per section (clean attempts; quits and restarts count as alive until they end) and clear rate.
The '__model__' row gets the energy rules. The page runs the exact energy-bar DP in the browser for any pass level.

Usage: python export_pass_profiles.py --swings <swings_passv2.parquet> --db <portaBLe DB with AccLossProfiles>
       [--hazard a19_hazard_windows.parquet] [--pass-maps a19_pass_maps.parquet]
"""
import argparse, base64, json, os, sqlite3, sys
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import energylib as el

ap = argparse.ArgumentParser()
ap.add_argument("--swings", required=True); ap.add_argument("--db", required=True)
ap.add_argument("--hazard", default=None); ap.add_argument("--pass-maps", default=None)
args = ap.parse_args()
P = el.V2
Q0, QS = -3.0, 0.03
S = pd.read_parquet(args.swings)
S = S.sort_values(["lb_id", "seconds", "hand"], kind="stable").reset_index(drop=True)
codes, lbs = pd.factorize(S.lb_id)
ld = np.log(np.maximum(el.pass_diff(S, P), 1e-3))
notes = S.n_cubes.clip(1, 4).values
q = np.clip(np.round((ld - Q0) / QS), 0, 255).astype(np.uint8)
ldq = q * QS + Q0                                                       # what the page will see
LD, ACT = el.note_matrix(ldq, notes, codes)
t50 = el.skill50(LD, ACT, P["slope"])
one = S.groupby("lb_id", sort=False).hand.nunique().reindex(lbs).values < 2
print(f"{len(lbs)} maps; skill50 computed on the quantized difficulties")

# causes: each factor's log contribution to PassDiff above a typical swing, weighted by the swing's miss chance at the map threshold
k = P["stress_scale"]
smk = lambda st: 2.0 * k * st / (k * st + 2.0) + 1.0
cross = (((S.hand == 0) & (S.x == 3)) | ((S.hand == 1) & (S.x == 0))).values
contrib = np.column_stack([
    np.log(np.maximum((S.swing_speed * S.low_speed_falloff).values, 1e-3)) - 1.324,     # global median of the speed part
    np.log(smk(S.stress.values)) - np.log(smk(0.045)),                                     # global median stress
    np.log(1 + P["crossover"]) * cross,
    np.log(1 + P["horizontal"]) * S.cut_direction.isin([2, 3]).values,
    np.log(1 + P["diagonal"]) * S.cut_direction.isin([4, 5, 6, 7]).values,
    np.log(S.njs_buff.values),
    np.log(1.05) * (S.is_stream.values == 1),
    np.log(S.wall_buff.values)])
LABELS = ["Swing speed", "Tech (angle strain, repositioning, rotation)", "Crossovers", "Horizontal cuts", "Diagonal cuts",
          "High note jump speed", "Hand alternation (streams)", "Walls"]
pmiss = 1 / (1 + np.exp(-P["slope"] * (ldq - t50[codes])))
W = np.clip(contrib, 0, None) * (pmiss * notes)[:, None]

obs_h = {}
if args.hazard:
    H = pd.read_parquet(args.hazard, columns=["lb_id", "win", "hazard", "at_risk"])
    obs_h = {lb: g for lb, g in H.groupby("lb_id")}
clear = {}
if args.pass_maps:
    PM = pd.read_parquet(args.pass_maps, columns=["lb_id", "attempts", "fails"]).set_index("lb_id")
    clear = {lb: (1 - r.fails / r.attempts, int(r.attempts)) for lb, r in PM.iterrows()}

con = sqlite3.connect(args.db)
rows = dict(con.execute("select LeaderboardId, Json from AccLossProfiles"))
updates = []
starts = np.r_[0, np.flatnonzero(codes[1:] != codes[:-1]) + 1, len(codes)]
for m, lb in enumerate(lbs):
    if lb not in rows: continue
    a, z = starts[m], starts[m + 1]
    t = S.seconds.values[a:z]; t0 = t.min(); width = max(5.0, max(t.max() - t0, 1.0) / 60)
    win = np.floor((t - t0) / width).astype(int); nw = win.max() + 1
    prof = json.loads(rows[lb])
    if len(prof["windows"]["n"]) != nw:                                  # sections must match the accuracy tab
        print("section mismatch", lb, nw, len(prof["windows"]["n"])); continue
    Wm = W[a:z]
    per_win = np.zeros((nw, len(LABELS)))
    np.add.at(per_win, win, Wm)
    top = []
    for i in range(nw):
        tot = per_win[i].sum()
        top.append([[LABELS[j], round(float(per_win[i, j] / tot), 3)] for j in np.argsort(-per_win[i])[:3] if tot > 0 and per_win[i, j] / tot >= 0.05])
    mt = Wm.sum(axis=0); mtot = mt.sum()
    packed = base64.b64encode(np.column_stack([q[a:z], notes[a:z].astype(np.uint8)]).ravel().tobytes()).decode()
    pas = {"v": 1, "q": packed, "win": np.bincount(win, minlength=nw).tolist(), "skill50": round(float(t50[m]), 4),
           "one_saber": bool(one[m]), "top": top,
           "factors": {LABELS[j]: round(float(mt[j] / mtot), 4) for j in range(len(LABELS)) if mtot > 0 and mt[j] / mtot >= 0.005}}
    g = obs_h.get(lb)
    if g is not None:
        hz = [None] * nw
        for r in g.itertuples():
            if 0 <= r.win < nw and r.at_risk >= 100: hz[int(r.win)] = round(float(r.hazard), 5)
        if any(v is not None for v in hz):
            pas["observed"] = {"hazard": hz, "clear_rate": round(clear[lb][0], 4) if lb in clear else None, "attempts": clear[lb][1] if lb in clear else None}
    prof["pass"] = pas
    updates.append((json.dumps(prof, separators=(",", ":")), lb))
meta = json.loads(rows["__model__"])
meta["pass"] = {"slope": P["slope"], "scale": P["scale"], "one_saber": P["one_saber"], "start": 50, "hit": 1, "miss": 15, "q0": Q0, "qs": QS,
                "attempt_spread": 0.3}   # skill spread of real attempts, a25_attempt_mix.py (0.2 lowest hazard error, 0.4 no early/late bias)
updates.append((json.dumps(meta, separators=(",", ":")), "__model__"))
con.executemany("UPDATE AccLossProfiles SET Json = ? WHERE LeaderboardId = ?", updates)
con.commit(); con.close()
print(f"added pass data to {len(updates) - 1} profiles; with observed fail hazard: {sum('observed' in json.loads(u[0]).get('pass', {}) for u in updates[:-1])}")
