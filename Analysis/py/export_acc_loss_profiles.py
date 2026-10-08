"""Write per-map "where accuracy is lost" profiles (acc_loss_model.json from a17_acc_loss_model.py) into a portaBLe DB.

Table AccLossProfiles(LeaderboardId TEXT PRIMARY KEY, Json TEXT); the row '__model__' holds what the page needs for every map
(component labels, skill curves, calibration, playerbase percentiles, factor labels). Per map: ~5 s windows with the expected
loss per component at the base skill (sum over notes of the expected point-loss share), the window's top factors, the map-wide
factor breakdown per component, and the replay-observed loss per window where the replay study covered the map. Skill scales
each component by one factor (loss = exp(intercept + factors + skill term) x calibration), so the page recomputes any skill exactly.

Usage: python export_acc_loss_profiles.py --model Analysis/models/acc_loss_model.json --a17 <dir with a17_swings.parquet, a17_obs.parquet>
       --skill <player_skill.parquet> --replays <snap dir> --db wwwroot/test-algo-b-v3.db [--base-pct 0.95]
"""
import argparse, json, os, sqlite3
import numpy as np, pandas as pd

ap = argparse.ArgumentParser()
for a in ["model", "a17", "skill", "replays", "db"]: ap.add_argument(f"--{a}", required=True)
ap.add_argument("--base-pct", type=float, default=0.95)
ap.add_argument("--acc-model", default=os.path.join(os.path.dirname(__file__), "..", "..", "RatingAPI", "acc_model.json"),
                help="the acc model whose reference skill turns the leaderboard's predicted accuracy into a total per skill level")
args = ap.parse_args()
spec = json.load(open(args.model)); COMP = spec["components"]
S = pd.read_parquet(os.path.join(args.a17, "a17_swings.parquet"))
UNITS = {"hand_gap": " s", "any_gap": " s", "travel": " lanes", "njs": "", "jd": " m", "density": " swings / 4 s", "minutes": " min"}
TURN = ["same direction", "sharp turn (~45°)", "right angle", "wide turn (~135°)", "straight back"]

def level_labels(f):
    if f["edges"] is None:
        return ["yes" if lv == "1" else ("no" if lv == "0" else lv) for lv in f["levels"]]
    if f["name"] == "turn": return TURN
    e = [x if x is not None else np.inf for x in f["edges"]]; u = UNITS.get(f["name"], ""); n = len(e) - 1
    fmt = lambda v: f"{v:g}"
    return [f"< {fmt(e[1])}{u}" if i == 0 else (f"≥ {fmt(e[i])}{u}" if i == n - 1 else f"{fmt(e[i])}–{fmt(e[i + 1])}{u}") for i in range(n)]

def codes(f):
    v = S[f["column"]]
    if f["edges"] is None:
        if f["levels"] == ["0", "1"]: return v.astype(int).clip(0, 1).values
        return pd.Categorical(v, categories=f["levels"]).codes
    e = np.array([x if x is not None else np.inf for x in f["edges"]], dtype=float)
    return np.clip(np.digitize(v.values.astype(float), e[1:-1]), 0, len(e) - 2)

F = spec["factors"]
C = np.column_stack([codes(f) for f in F])
logf = {c: np.zeros(len(S)) for c in COMP}
contrib = {c: np.zeros((len(S), len(F))) for c in COMP}
for k, f in enumerate(F):
    for c in COMP:
        coef = np.array(f["coef"][c]); v = coef[C[:, k]]
        logf[c] += v; contrib[c][:, k] = v

sk_mid = np.array(spec["skill_mid"]); cal = np.array(spec["calibration"])
def skill_term(c, s): return float(np.interp(s, sk_mid, np.array(spec["skill_coef"][c])) + np.interp(s, sk_mid, cal))
P = pd.read_parquet(args.skill)
s0 = float(P.skill.quantile(args.base_pct))
notes = S.n_cubes.clip(lower=1).values.astype(float)
L = {c: np.exp(spec["intercept"][c] + logf[c] + skill_term(c, s0)) for c in COMP}
L0 = {c: np.exp(spec["intercept"][c] + skill_term(c, s0)) for c in COMP}
# excess over the plain swing, attributed to the factors that raise the loss, in proportion to their log effect
att = {}
for c in COMP:
    pos = np.clip(contrib[c], 0, None); tot = pos.sum(axis=1, keepdims=True)
    ex = np.clip(L[c] - L0[c], 0, None)[:, None]
    att[c] = np.where(tot > 0, ex * pos / np.where(tot > 0, tot, 1), 0) * notes[:, None]

lab = [level_labels(f) for f in F]
def flabel(k, lvl):
    f = F[k]
    return f["label"] if f["edges"] is None and f["levels"] in (["0", "1"], ["no", "yes"]) else f"{f['label']}: {lab[k][lvl]}"
# global ids of (factor, level) pairs
LV_OFF = np.r_[0, np.cumsum([len(lab[k]) for k in range(len(F))])]; N_FL = int(LV_OFF[-1])
FL = (C + LV_OFF[:-1]).astype(np.int64)
FL_LABEL = [flabel(k, l) for k in range(len(F)) for l in range(len(lab[k]))]

# replay-observed loss per swing and stratum (sum of point-loss shares over the replay's notes on the swing)
O = pd.read_parquet(os.path.join(args.a17, "a17_obs.parquet"))
R = pd.read_parquet(os.path.join(args.replays, "replays.parquet"), columns=["score_id", "player_id", "modifiers", "stratum_kind"])
R = R[R.modifiers.isna() | R.modifiers.fillna("").isin(["", "IF", "BE"])]
R["player"] = R.player_id.astype(str); R["stratum"] = np.where(R.stratum_kind.astype(str).str.startswith("top"), "top", "mid")
R = R.merge(P[["player", "skill"]], on="player", how="left")
O = O.merge(R[["score_id", "stratum", "skill"]], on="score_id")
O["loss"] = O[COMP].sum(axis=1)
S["row"] = np.arange(len(S))
O = O.merge(S[["lb_id", "swing_i", "row"]], on=["lb_id", "swing_i"])
O_by_lb = {lb: g for lb, g in O.groupby("lb_id", sort=False)}

rows = []
for lb, idx in S.groupby("lb_id", sort=False).indices.items():
    t = S.seconds.values[idx]; t0 = t.min(); span = max(t.max() - t0, 1.0); width = max(5.0, span / 60)
    win = np.floor((t - t0) / width).astype(int); nw = win.max() + 1
    W = {"t0": [round(t0 + i * width, 2) for i in range(nw)], "w": round(width, 3),
         "n": np.bincount(win, weights=notes[idx], minlength=nw).round(1).tolist()}
    for c in COMP: W[c] = np.bincount(win, weights=(L[c] * notes)[idx], minlength=nw).round(5).tolist()
    # excess per (window, factor level), all components at the base skill -> each window's top 3 causes
    K = len(F); fl = FL[idx]                                                  # n x K global factor-level ids
    win_k = np.repeat(win, K)
    tot_att = sum(att[c][idx] for c in COMP).ravel()
    grid = np.bincount(win_k * N_FL + fl.ravel(), weights=tot_att, minlength=nw * N_FL).reshape(nw, N_FL)
    wloss = sum(np.bincount(win, weights=(L[c] * notes)[idx], minlength=nw) for c in COMP)
    tops = []
    for i in range(nw):
        best = np.argsort(-grid[i])[:3]
        tops.append([[FL_LABEL[j], round(float(grid[i, j] / wloss[i]), 3)] for j in best if grid[i, j] > 0 and wloss[i] > 0])
    W["top"] = tops
    # map-wide excess per factor level and component (the page rescales each component with skill)
    fac = {}
    for c in COMP:
        v = np.bincount(fl.ravel(), weights=att[c][idx].ravel(), minlength=N_FL)
        fac[c] = {FL_LABEL[j]: round(float(v[j]), 4) for j in np.flatnonzero(v > 0)}
    base = {c: round(float((L0[c] * notes)[idx].sum()), 4) for c in COMP}
    obs = {}
    g = O_by_lb.get(lb)
    if g is not None and len(g):
        wi = np.floor((S.seconds.values[g.row.values] - t0) / width).astype(int)
        for st, gg in g.groupby("stratum"):
            wgi = wi[g.stratum.values == st]
            lsum = np.bincount(wgi, weights=gg.loss.values, minlength=nw); nsum = np.bincount(wgi, weights=gg.one.values, minlength=nw)
            obs[st] = {"skill": round(float(np.nanmean(gg.skill.values)), 3), "replays": int(gg.score_id.nunique()),
                       "loss": [round(float(a / b), 4) if b >= 30 else None for a, b in zip(lsum, nsum)]}
    rows.append((lb, json.dumps({"v": 1, "windows": W, "factors": fac, "base": base, "observed": obs}, separators=(",", ":"))))

pcts = spec["percentiles"]
meta = {"v": 1, "components": COMP, "labels": {"precision": "Precision (centre cut)", "swing": "Swing angles (pre/post)", "misses": "Misses / bad cuts"},
        "base_skill": round(s0, 4), "base_pct": args.base_pct, "skill_mid": spec["skill_mid"], "skill_coef": spec["skill_coef"],
        "calibration": spec["calibration"], "percentiles": pcts, "validation": spec.get("validation"),
        # total loss per skill follows the map's acc rating: log(1 - acc(s)) = log(1 - predictedAcc) + reference_skill - s
        "reference_skill": json.load(open(args.acc_model))["reference_skill"]}
con = sqlite3.connect(args.db)
con.execute("DROP TABLE IF EXISTS AccLossProfiles")
con.execute("CREATE TABLE AccLossProfiles (LeaderboardId TEXT PRIMARY KEY, Json TEXT NOT NULL)")
con.executemany("INSERT INTO AccLossProfiles VALUES (?, ?)", rows + [("__model__", json.dumps(meta, separators=(",", ":")))])
con.commit(); con.close()
size = sum(len(j) for _, j in rows)
print(f"wrote {len(rows)} profiles ({size / 1e6:.1f} MB JSON) + model row to {args.db}; base skill {s0:.3f} (playerbase p{args.base_pct * 100:g})")
