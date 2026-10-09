"""A29: why players fail where they fail: attempt replays (fails and clears) of maps pass rating v2 gets wrong.

Input: a ReplayStudy run in attempts mode (--attempts: signed R2 links to otherreplays/*.bsor), i.e. notes_obs.csv.gz (every note
event of every replay, matched to the analyzer's swings, with bad-cut flags and bombs) and replays.csv (stratum = attempt type).

1. Energy bar rebuilt from the note events (start 50 %, +1 % per good cut, -10 % per bad cut, -15 % per miss or bomb; walls are not
   in the events). The "final drain" of a fail = the mistakes after the bar last stood at >= 50 %.
2. What the fatal mistakes are: misses vs bad cuts (wrong direction / wrong saber / too slow) vs bombs.
3. What the fatal swings are: share of the final-drain mistakes vs share of the swings played in those stretches, by cut class,
   eBPM band, crossover, reach.
4. Which swings get missed: mistakes on a swing type divided by the same run's average mistake rate (skill, survivor selection and
   run length cancel), by eBPM, v2's tech factor, reach, v2's per-swing difficulty quintile, cut class, crossover, flow; for fails +
   clears and for clears alone (fail runs collapse in their fatal section, which steepens every gradient).

Getting the replays: attempt replays sit in the R2 bucket 'otherreplays' (public API: only for players with public attempts). Sign GET
links with the server's R2 keys (boto3, endpoint https://<account>.r2.cloudflarestorage.com) into a CSV lb_id,attempt_id,player_id,
type,time,url and run ReplayStudy --attempts <csv> --lbs <csv> --output <dir> (bad-cut flags and bombs are in notes_obs).

Usage: python a29_attempt_replays.py --study <ReplayStudy dir> --swings <swings_prod.csv.gz> [--out <dir>]
"""
import argparse, os, sys
import numpy as np, pandas as pd
sys.path.insert(0, os.path.dirname(__file__))
import energylib as el

ap = argparse.ArgumentParser()
ap.add_argument("--study", required=True); ap.add_argument("--swings", required=True); ap.add_argument("--out", default=None)
args = ap.parse_args()
NAMES = {"4c5e3xx71": "Godspeed Ex", "4c5e3xx91": "Godspeed E+", "3f829xxxxxx91": "Extratongue E+", "3d6abxxxxx91": "414 PER SPEED E+",
         "3e2c2xxxxx91": "Dual Doom E+", "47c48xxxx91": "SLIDE THE BPM E+", "179eb91": "Kannabis E+", "4cbd0xxxx91": "Sound Chimera 4cbd0",
         "2c00e91": "Sound Chimera 2c00e", "46f03xx91": "Calamitous Demise E+", "338afxx91": "Feral E+", "290c991": "Superluminal E+"}
GOOD, BAD, MISS, BOMB = 0, 1, 2, 3

N = pd.read_csv(os.path.join(args.study, "notes_obs.csv.gz"), dtype={"lb_id": str},
                usecols=["lb_id", "score_id", "spawn", "color", "x", "y", "cut_dir", "scoring_type", "event_type", "event_time", "swing_i", "bad"])
R = pd.read_csv(os.path.join(args.study, "replays.csv"), dtype={"lb_id": str}, usecols=["lb_id", "score_id", "player_id", "stratum", "timepost"])
R = R.drop_duplicates("score_id")
N = N.merge(R[["score_id", "stratum"]], on="score_id")
N = N.sort_values(["score_id", "event_time"], kind="stable").reset_index(drop=True)
print(f"{len(R)} replays ({(R.stratum == 'fail').sum()} fails, {(R.stratum == 'clear').sum()} clears) on {R.lb_id.nunique()} maps, {len(N):,} note events")

# ------------------------------------------------------------------ swings: features + v2 pass difficulty
S = pd.read_csv(args.swings, dtype={"lb_id": str})
S = S[S.lb_id.isin(R.lb_id.unique())].copy()
S["ld"] = np.log(np.maximum(el.pass_diff(S, el.V2), 1e-3))
S["ebpm"] = 30 * S.frequency
S["cut"] = np.select([S.cut_direction.isin([2, 3]), S.cut_direction.isin([4, 5, 6, 7]), S.cut_direction == 8], ["horizontal", "diagonal", "dot"], "vertical")
S["cross"] = ((S.hand == 0) & (S.x == 3)) | ((S.hand == 1) & (S.x == 0))
S = S.sort_values(["lb_id", "seconds", "swing_i"], kind="stable")
d = np.abs(np.mod(S.direction - S.groupby(["lb_id", "hand"]).direction.shift(1), 360)); S["turn"] = np.minimum(d, 360 - d).fillna(180)
S["flow"] = np.where(S.turn >= 157.5, "straight back", np.where(S.turn <= 22.5, "same direction", "angle change"))
S["band"] = pd.cut(S.ebpm, [0, 150, 250, 350, 1e9], labels=["<150", "150-250", "250-350", ">=350"], right=False).astype(str)
S["reach"] = np.where(S.dist_diff >= 1.3, "long reach", "normal reach")
SW = S.set_index(["lb_id", "swing_i"])

# ------------------------------------------------------------------ 1. energy bar per attempt
drain = np.select([N.event_type == GOOD, N.event_type == BAD], [0.01, -0.10], -0.15)
N["drain"] = drain
fail_rows, fatal = [], []
for sid, g in N.groupby("score_id", sort=False):
    e = 0.5; peak_i = 0; dead = None
    ev = g.event_type.values; dv = g.drain.values
    for i in range(len(g)):
        e = min(1.0, e + dv[i])
        if e >= 0.5: peak_i = i
        if e <= 1e-9: dead = i; break
    st = g.stratum.iloc[0]
    fail_rows.append((sid, g.lb_id.iloc[0], st, dead is not None, g.event_time.iloc[dead] if dead is not None else np.nan, g.event_time.iloc[-1]))
    if st == "fail" and dead is not None:
        idx = g.index[peak_i + 1: dead + 1]
        fatal.append(idx[N.event_type.values[idx] != GOOD])
F = pd.DataFrame(fail_rows, columns=["score_id", "lb_id", "stratum", "reached_zero", "zero_time", "last_event"])
Fz = F[F.stratum == "fail"]
print(f"\nenergy rebuilt from the note events: {Fz.reached_zero.mean():.0%} of fail replays reach 0 (the rest end on walls or "
      f"before their last note event); {F[F.stratum == 'clear'].reached_zero.mean():.1%} of clears would have")
FAT = N.loc[np.concatenate(fatal)] if fatal else N.iloc[:0]

def kind(r):
    if r.event_type == MISS: return "miss"
    if r.event_type == BOMB: return "bomb"
    b = int(r.bad) if not pd.isna(r.bad) else 0
    return "bad: wrong saber" if b & 2 else ("bad: wrong direction" if b & 1 else ("bad: too slow" if b & 4 else "bad: other"))
FAT = FAT.assign(kind=[kind(r) for r in FAT.itertuples()])

# ------------------------------------------------------------------ 2.-3. per map: fatal mistakes and the swings they happen on
out_rows = []
for lb, name in NAMES.items():
    fz = Fz[Fz.lb_id == lb]
    if not len(fz): continue
    fat = FAT[FAT.lb_id == lb]
    k = fat.kind.value_counts(normalize=True)
    # exposure: every note event of the final-drain stretches (good or not), and the mistakes among them
    fat_sw = fat[fat.swing_i >= 0]
    feats = SW.loc[lb].reindex(fat_sw.swing_i.values)
    allsw = SW.loc[lb]
    played = N[(N.lb_id == lb) & (N.stratum == "fail") & (N.swing_i >= 0)]
    expo = allsw.reindex(played.swing_i.values)
    row = {"map": name, "fail replays": len(fz), "reach 0": f"{fz.reached_zero.mean():.0%}",
           "median fail": f"{int(fz.zero_time.median() // 60)}:{int(fz.zero_time.median() % 60):02d}" if fz.zero_time.notna().any() else "",
           "mistakes in final drain": len(fat)}
    for kk in ["miss", "bad: wrong direction", "bad: wrong saber", "bad: too slow", "bomb"]: row[kk] = f"{k.get(kk, 0):.0%}"
    for col, lev in [("cut", "diagonal"), ("cut", "horizontal"), ("cross", True), ("band", ">=350"), ("reach", "long reach"), ("flow", "angle change")]:
        a = (feats[col] == lev).mean(); b = (expo[col] == lev).mean()
        row[f"{col}={lev}"] = f"{a:.0%} vs {b:.0%}"
    out_rows.append(row)
T = pd.DataFrame(out_rows).set_index("map")
pd.set_option("display.width", 250); pd.set_option("display.max_columns", 30)
print("\nfail replays: what the final drain (mistakes after the bar last stood at >= 50 %) is made of;")
print("swing-type columns: share of final-drain mistakes vs share of all swings played in fail runs")
print(T.to_string())

# ------------------------------------------------------------------ 4. per swing: which swings get missed, relative to the same run
# mistakes on a swing type / the run's own average mistake rate (so skill, survivor selection and run length cancel); fails + clears,
# and clears alone (fail runs collapse in their fatal section, which steepens everything)
P = N[N.swing_i >= 0].groupby(["lb_id", "swing_i", "score_id"]).event_type.agg(lambda v: int((v != GOOD).any())).rename("mistake").reset_index()
P = P.merge(R[["score_id", "stratum"]], on="score_id").join(SW, on=["lb_id", "swing_i"]).dropna(subset=["ld"])
k = el.V2["stress_scale"]; P["tech"] = 2.0 * k * P.stress / (k * P.stress + 2.0) + 1.0
P["exp"] = P.groupby("score_id").mistake.transform("mean")
P["v2q"] = P.groupby("lb_id").ld.transform(lambda v: pd.qcut(v.rank(method="first"), 5, labels=False)) + 1
oe_rows = []
for name, sub in [("fails + clears", P), ("clears only", P[P.stratum == "clear"])]:
    for col, bins, labels in [("ebpm", [0, 150, 250, 350, 450, 1e9], ["<150", "150-250", "250-350", "350-450", ">=450"]),
                              ("tech", [0, 1.2, 1.5, 2.0, 9], ["<1.2", "1.2-1.5", "1.5-2", ">=2"]),
                              ("dist_diff", [0, 1.15, 1.25, 1.35, 9], ["<1.15", "1.15-1.25", "1.25-1.35", ">=1.35"]),
                              ("v2q", [1, 2, 3, 4, 5, 6], ["q1", "q2", "q3", "q4", "q5"])]:
        g = sub.groupby(pd.cut(sub[col], bins, labels=labels, right=False), observed=True)
        for lev, v in (g.mistake.sum() / g.exp.sum()).items(): oe_rows.append((name, col, str(lev), round(float(v), 2)))
    for col in ["cut", "cross", "flow"]:
        g = sub.groupby(col)
        for lev, v in (g.mistake.sum() / g.exp.sum()).items(): oe_rows.append((name, col, str(lev), round(float(v), 2)))
OE = pd.DataFrame(oe_rows, columns=["sample", "feature", "level", "mistakes / run average"])
print("\nwhich swings get missed: mistakes on the swing type / the same run's average mistake rate (12 maps pooled)")
print(OE.pivot_table(index=["feature", "level"], columns="sample", values="mistakes / run average", sort=False).to_string())
if args.out:
    os.makedirs(args.out, exist_ok=True)
    T.to_csv(os.path.join(args.out, "a29_fatal.csv")); OE.to_csv(os.path.join(args.out, "a29_missed_vs_expected.csv"), index=False)
    F.to_csv(os.path.join(args.out, "a29_energy.csv"), index=False)
