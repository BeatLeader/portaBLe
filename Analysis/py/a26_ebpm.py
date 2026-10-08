"""A26: is eBPM a better speed parameter than the analyzer's swing speed?

In the analyzer (Difficulty.cs, AnalyzeMap.cs), per swing:
  SwingFrequency = 1 / seconds since this hand's last swing, x2 on a parity break (reset), /2 again when flagged a false positive
  eBPM           = 30 * SwingFrequency                       (one swing per half beat = the song's BPM)
  SwingSpeed     = SwingFrequency * distanceDiff,  distanceDiff = 1 + hitDistance / (hitDistance + 2.668)   (1 ... 2)
so swing speed is eBPM / 30 times a hit-distance factor. "Raw" eBPM (as players count it) is 30 / seconds since the hand's last
swing, without the reset doubling.

Each model that uses swing speed is refitted with eBPM in its place, nothing else changed:
  1. map-level acc model (ridge on map features, song-grouped CV): swing-speed vs frequency (= eBPM) feature families, single
     parameters, and the peak sustained eBPM the analyzer computes (best 4-swing window per hand);
  2. "where accuracy is lost" (a17 Poisson GLMs on replays): the "Fast swings" factor binned on swing speed vs on eBPM, with and
     without the separate time-since-last-swing factor; held-out per-swing deviance, map R2, within-map "where" Spearman;
  3. pass rating v2 (energy bar): PassDiff's speed part frequency * distanceDiff^alpha (alpha 1 = today, 0 = eBPM), and raw eBPM
     with the reset term refitted; R2 of a cubic fit to the attempts' pass difficulty b (a19).

Usage: python a26_ebpm.py --swings <swings_prod.csv.gz> --features <features_feat4.csv> --acc-maps <out/fit_acc_model_maps.csv>
       --a17 <a17 cache dir> --replays <snap dir> --skill <player_skill.parquet> --map-d <map_d.parquet> --pass-maps <a19_pass_maps.parquet>
       --out <dir> [--parts 1,2,3] [--rows 3000000]
"""
import argparse, json, os, sys
import numpy as np, pandas as pd, scipy.sparse as sp
from sklearn.linear_model import RidgeCV, PoissonRegressor
from sklearn.model_selection import GroupKFold
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler

sys.path.insert(0, os.path.dirname(__file__))
import energylib as el

ap = argparse.ArgumentParser()
for a in ["swings", "features", "acc_maps", "a17", "replays", "skill", "map_d", "pass_maps", "out"]:
    ap.add_argument(f"--{a.replace('_', '-')}", dest=a, required=True)
ap.add_argument("--parts", default="1,2,3"); ap.add_argument("--rows", type=int, default=3_000_000)
args = ap.parse_args()
parts = set(args.parts.split(","))
os.makedirs(args.out, exist_ok=True)
RES = {}

SW = pd.read_csv(args.swings, usecols=["lb_id", "swing_i", "seconds", "hand", "frequency", "dist_diff", "swing_speed", "hit_distance",
                                       "parity_error", "false_positive"], dtype={"lb_id": str})
SW = SW.sort_values(["lb_id", "seconds", "swing_i"], kind="stable").reset_index(drop=True)
SW["ebpm"] = 30 * SW.frequency
gap = SW.seconds - SW.groupby(["lb_id", "hand"]).seconds.shift(1)
SW["ebpm_raw"] = (30 / gap.where(gap > 0)).fillna(0).clip(upper=30 * 64)
print(f"{len(SW):,} swings; swing speed = frequency x distanceDiff exactly: max rel. error "
      f"{np.nanmax(np.abs(SW.swing_speed / (SW.frequency * SW.dist_diff) - 1)):.1e}; distanceDiff p10/p50/p90 "
      + " / ".join(f"{v:.3f}" for v in SW.dist_diff.quantile([.1, .5, .9])) + f"; corr(log speed, log eBPM) "
      f"{np.corrcoef(np.log(SW.swing_speed.clip(1e-3)), np.log(SW.ebpm.clip(1e-3)))[0, 1]:.4f}")

# ================================================================== 1. map-level acc model
if "1" in parts:
    F = pd.read_csv(args.features, dtype={"lb_id": str}); F = F[F["mod"] == "none"].drop(columns="mod").drop_duplicates("lb_id")
    T = pd.read_csv(args.acc_maps, dtype={"lb_id": str})[["lb_id", "d"]]
    # peak sustained eBPM as in AnalyzeMap.CalculatePeakSustainedEBPM: best mean frequency over 4 consecutive swings of a hand
    def peak4(f):
        c = np.r_[0, np.cumsum(f)]
        return (c[4:] - c[:-4]).max() / 4 if len(f) >= 4 else (f.mean() if len(f) else 0)
    pk = SW.groupby(["lb_id", "hand"]).frequency.apply(lambda f: peak4(f.values)).groupby("lb_id").max() * 30
    pk_raw = SW.groupby(["lb_id", "hand"]).ebpm_raw.apply(lambda f: peak4(f.values)).groupby("lb_id").max()
    M = T.merge(F, on="lb_id").merge(pk.rename("peak_ebpm").reset_index(), on="lb_id").merge(pk_raw.rename("peak_ebpm_raw").reset_index(), on="lb_id")
    feat = [c for c in F.columns if c != "lb_id"]
    M = M.replace([np.inf, -np.inf], np.nan).dropna(subset=feat + ["d"]).reset_index(drop=True)
    groups = M.lb_id.str[:-2].values      # song
    ridge = lambda: make_pipeline(StandardScaler(), RidgeCV(alphas=np.logspace(-2, 4, 25)))
    def gcv(cols):
        X = M[cols].values; y = M.d.values; pred = np.zeros(len(y))
        for trn, tst in GroupKFold(5).split(X, y, groups):
            pred[tst] = ridge().fit(X[trn], y[trn]).predict(X[tst])
        return 1 - np.var(y - pred) / np.var(y)
    sp_ = [c for c in feat if c.startswith("swing_speed_")]
    fr_ = [c for c in feat if c.startswith("frequency_") or c.startswith("frac_freq")]
    out = {}
    out["all 91 features"] = gcv(feat)
    out["without swing-speed features (eBPM only)"] = gcv([c for c in feat if c not in sp_])
    out["without frequency / eBPM features (swing speed only)"] = gcv([c for c in feat if c not in fr_])
    out["without either"] = gcv([c for c in feat if c not in sp_ + fr_])
    out["all + peak sustained eBPM"] = gcv(feat + ["peak_ebpm", "peak_ebpm_raw"])
    base = ["pass", "tech", "log_swings"]
    for name, col in [("swing speed p90", "swing_speed_p90"), ("eBPM p90", "frequency_p90"), ("swing speed peak32", "swing_speed_peak32"),
                      ("eBPM peak32", "frequency_peak32"), ("peak sustained eBPM (analyzer)", "peak_ebpm"), ("peak sustained raw eBPM", "peak_ebpm_raw")]:
        M["_l"] = np.log(M[col].clip(lower=1e-3))
        out[f"single: ln {name}"] = gcv(["_l"])
        out[f"pass + tech + ln swings + ln {name}"] = gcv(base + ["_l"])
    RES["acc_model"] = out
    print(f"\n1. map-level acc model, song-grouped 5-fold CV R2 on {len(M)} maps:")
    for k, v in out.items(): print(f"   {k:58s} {v:.4f}")

# ================================================================== 2. "where accuracy is lost" (a17 GLMs)
if "2" in parts:
    S = pd.read_parquet(os.path.join(args.a17, "a17_swings.parquet"))
    S = S.merge(SW[["lb_id", "swing_i", "ebpm", "ebpm_raw"]], on=["lb_id", "swing_i"], how="left")
    assert S.ebpm.notna().all()
    O = pd.read_parquet(os.path.join(args.a17, "a17_obs.parquet"))
    R = pd.read_parquet(os.path.join(args.replays, "replays.parquet"), columns=["score_id", "player_id", "modifiers", "stratum_kind"])
    R = R[R.modifiers.isna() | R.modifiers.fillna("").isin(["", "IF", "BE"])]
    R["player"] = R.player_id.astype(str); R["stratum"] = np.where(R.stratum_kind.astype(str).str.startswith("top"), "top", "mid")
    R = R.merge(pd.read_parquet(args.skill)[["player", "skill"]], on="player")
    O = O.merge(R[["score_id", "skill", "stratum"]], on="score_id")
    row_of = pd.Series(np.arange(len(S)), index=pd.MultiIndex.from_arrays([S.lb_id.values, S.swing_i.values]))
    O["row"] = row_of.reindex(pd.MultiIndex.from_arrays([O.lb_id.values, O.swing_i.values])).values
    O = O.dropna(subset=["row"]); O["row"] = O.row.astype(np.int64)
    O["song"] = S.lb_id.str[:-2].values[O.row.values]
    COMP = ["precision", "swing", "misses"]
    SPEED = ("speed", "Fast swings", "swing_speed", [0, 1.5, 2.5, 3.5, 4.5, 5.5, 6.5, 7.5, 9, 11, 14, np.inf])
    # the same bins in eBPM: swing speed / median distanceDiff (1.18) x 30, rounded
    EBPM = ("speed", "eBPM", "ebpm", [0, 40, 60, 90, 115, 140, 165, 190, 230, 280, 360, np.inf])
    EBPM_RAW = ("speed", "eBPM (raw)", "ebpm_raw", [0, 40, 60, 90, 115, 140, 165, 190, 230, 280, 360, np.inf])
    HAND_GAP = ("hand_gap", "Little time between swings of a hand", "hand_gap", [0, 0.1, 0.13, 0.17, 0.21, 0.26, 0.33, 0.45, 0.7, 1.2, np.inf])
    REST = [
        ("any_gap", "", "any_gap", [0, 0.04, 0.08, 0.12, 0.17, 0.25, 0.4, 0.8, np.inf]),
        ("turn", "", "turn", [-0.1, 22.5, 67.5, 112.5, 157.5, 180.1]),
        ("travel", "", "travel", [-0.1, 0.5, 1.2, 2.0, 3.0, np.inf]),
        ("angle_strain", "", "angle_strain", [-0.1, 0.01, 0.05, 0.15, 0.3, 0.6, np.inf]),
        ("reposition", "", "reposition", [-0.1, 0.01, 0.5, 1.0, 1.5, np.inf]),
        ("hit_distance", "", "hit_distance", [-0.1, 0.3, 0.5, 0.7, 0.9, 1.2, np.inf]),
        ("cut", "", "cut_class", ["vertical", "horizontal", "diagonal", "dot"]),
        ("lane", "", "lane", ["inner", "outer"]), ("layer", "", "layer", ["bottom", "middle", "top"]), ("cross", "", "cross", ["no", "yes"]),
        ("pattern", "", "pattern", ["single", "stack", "slider", "window", "tower", "multi"]),
        ("chain", "", "is_chain", [0, 1]), ("parity", "", "parity_error", [0, 1]), ("bomb", "", "bomb_avoidance", [0, 1]), ("wall", "", "wall", [0, 1]),
        ("njs", "", "njs", [0, 10, 13, 16, 19, 22, 26, np.inf]), ("jd", "", "jd", [0, 16, 19, 22, 25, 28, np.inf]),
        ("density", "", "density", [0, 4, 7, 10, 14, 19, 25, np.inf]), ("minutes", "", "minutes", [-0.1, 1, 2, 3, 4.5, 6.5, np.inf]),
    ]
    SKILL_EDGES = [-np.inf, 1.25, 1.75, 2.25, 2.6, 2.9, 3.2, 3.45, 3.7, 3.95, 4.2, np.inf]
    def is_cat(f): return isinstance(f[3][0], str) or list(f[3]) == [0, 1]
    def nlev(f): return len(f[3]) if is_cat(f) else len(f[3]) - 1
    def codes(f):
        v = S[f[2]]
        if is_cat(f): return pd.Categorical(v, categories=list(f[3])).codes.astype(np.int32)
        return np.clip(np.digitize(v.values.astype(float), f[3][1:-1]), 0, len(f[3]) - 2).astype(np.int32)

    rng = np.random.default_rng(0)
    songs = O.song.unique(); test_songs = set(rng.choice(songs, size=len(songs) // 5, replace=False))
    O["test"] = O.song.isin(test_songs)
    TR = O[~O.test].sample(min(args.rows, (~O.test).sum()), random_state=0); TE = O[O.test].sample(min(args.rows // 3, O.test.sum()), random_state=1)
    mapd = pd.read_parquet(args.map_d).set_index("lb_id").d
    test_maps = [lb for lb in O.lb_id[O.test].unique() if lb in mapd.index]
    rows_t = np.flatnonzero(S.lb_id.isin(test_maps).values); w_notes = S.n_cubes.clip(lower=1).values[rows_t]
    OT = O[O.test].copy()
    t0 = pd.Series(S.seconds.values[OT.row.values]).groupby(OT.lb_id.values).transform("min").values
    span = pd.Series(S.seconds.values[OT.row.values]).groupby(OT.lb_id.values).transform("max").values - t0
    OT["win"] = np.floor((S.seconds.values[OT.row.values] - t0) / np.maximum(5.0, np.maximum(span, 1) / 60)).astype(int)
    OT["loss"] = OT[COMP].sum(axis=1) / OT.one

    def d2(y, mu, w):
        def dev(y, mu): return 2 * np.sum(w * (np.where(y > 0, y * np.log(np.maximum(y, 1e-12) / mu), 0) - (y - mu)))
        return 1 - dev(y, mu) / dev(y, np.full_like(y, np.average(y, weights=w)))

    def run(factors):
        C = np.column_stack([codes(f) for f in factors])
        ref = np.array([np.bincount(C[:, k], minlength=nlev(f)).argmax() for k, f in enumerate(factors)])
        off = np.r_[0, np.cumsum([nlev(f) for f in factors])]; nf = off[-1]; ns = len(SKILL_EDGES) - 1
        def design(rows, skill):
            c = C[rows]; n = len(rows); cols = (c + off[:-1]).ravel(); keep = (c != ref).ravel()
            r = np.repeat(np.arange(n), len(factors))[keep]; cols = cols[keep]
            sk = np.clip(np.digitize(skill, SKILL_EDGES[1:-1]), 0, ns - 1) + nf
            return sp.csr_matrix((np.ones(len(r) + n, np.float32), (np.r_[r, np.arange(n)], np.r_[cols, sk])), shape=(n, nf + ns))
        X = design(TR.row.values, TR.skill.values)
        Ms = {c: PoissonRegressor(alpha=1e-7, max_iter=500, tol=1e-6).fit(X, (TR[c] / TR.one).values, sample_weight=TR.one.values) for c in COMP}
        pred = lambda rows, skill: sum(Ms[c].predict(design(rows, skill)) for c in COMP)
        out = {"deviance_explained": d2((TE[COMP].sum(axis=1) / TE.one).values, pred(TE.row.values, TE.skill.values), TE.one.values)}
        for s_ in [2.5, 3.8]:
            tot = pred(rows_t, np.full(len(rows_t), s_))
            acc = 1 - pd.Series(tot * w_notes).groupby(S.lb_id.values[rows_t]).sum() / pd.Series(w_notes).groupby(S.lb_id.values[rows_t]).sum()
            D = np.log((1 - acc).clip(lower=5e-4)) + s_
            j = pd.concat([D.rename("D"), mapd.reindex(D.index).rename("d")], axis=1).dropna()
            b = np.polyfit(j.D, j.d, 1); out[f"map_r2_skill{s_}"] = 1 - np.var(j.d - np.polyval(b, j.D)) / np.var(j.d)
        OT["pred"] = pred(OT.row.values, OT.skill.values)
        sp_ = []
        for (lb, st), g in OT.groupby(["lb_id", "stratum"]):
            W = g.groupby("win").agg(obs=("loss", "mean"), pred=("pred", "mean"), n=("loss", "size")); W = W[W.n >= 20]
            if len(W) >= 8: sp_.append((st, W.obs.corr(W.pred, method="spearman")))
        sp_ = pd.DataFrame(sp_, columns=["st", "r"])
        out["where_top"] = sp_[sp_.st == "top"].r.median(); out["where_mid"] = sp_[sp_.st == "mid"].r.median()
        # the speed factor's multipliers on total loss at the reference skill (precision + swing + misses, plain swing elsewhere)
        k = [f[0] for f in factors].index("speed") if "speed" in [f[0] for f in factors] else None
        if k is not None:
            f = factors[k]
            out["speed_levels"] = {f"{f[3][i]:g}-{f[3][i + 1]:g}": round(float(sum(np.exp(Ms[c].intercept_ + (0 if i == ref[k] else Ms[c].coef_[off[k] + i])) for c in COMP)
                                                                              / sum(np.exp(Ms[c].intercept_) for c in COMP)), 3) for i in range(nlev(f))}
        return out
    variants = {
        "swing speed + time since last swing (today)": [SPEED, HAND_GAP] + REST,
        "eBPM + time since last swing": [EBPM, HAND_GAP] + REST,
        "swing speed only": [SPEED] + REST,
        "eBPM only": [EBPM] + REST,
        "raw eBPM only (no reset doubling)": [EBPM_RAW] + REST,
    }
    out = {}
    print(f"\n2. 'where accuracy is lost' GLMs: {len(TR):,} training / {len(TE):,} held-out (replay, swing) rows")
    print(f"   {'variant':46s} {'deviance':>9s} {'map R2 2.5':>11s} {'map R2 3.8':>11s} {'where top':>10s} {'where mid':>10s}")
    for name, fs in variants.items():
        r = run(fs); out[name] = r
        print(f"   {name:46s} {r['deviance_explained']:9.4f} {r['map_r2_skill2.5']:11.4f} {r['map_r2_skill3.8']:11.4f} {r['where_top']:10.3f} {r['where_mid']:10.3f}", flush=True)
    for name in ["swing speed only", "eBPM only"]:
        print(f"   {name}: loss multiplier per level " + ", ".join(f"{k} x{v}" for k, v in out[name]["speed_levels"].items()))
    RES["acc_loss"] = out

# ================================================================== 3. pass rating v2 (energy bar)
if "3" in parts:
    P = pd.read_csv(args.swings, usecols=["lb_id", "swing_i", "seconds", "hand", "x", "cut_direction", "n_cubes", "parity_error", "stress",
                                          "njs_buff", "wall_buff", "is_stream"], dtype={"lb_id": str})
    P = P.sort_values(["lb_id", "seconds", "swing_i"], kind="stable").reset_index(drop=True)
    P = P.merge(SW[["lb_id", "swing_i", "frequency", "dist_diff", "ebpm_raw", "swing_speed"]], on=["lb_id", "swing_i"])
    B = pd.read_parquet(args.pass_maps)[["lb_id", "b"]]
    P = P[P.lb_id.isin(B.lb_id)].reset_index(drop=True)
    one_hand = P.groupby("lb_id").hand.nunique() == 1                 # the One Saber rule: a hand without swings
    lbs = pd.Index(P.lb_id.unique()); code = lbs.get_indexer(P.lb_id)
    b = B.set_index("lb_id").b.reindex(lbs).values
    v2 = el.V2
    k = v2["stress_scale"]; sm = 2.0 * k * P.stress.values / (k * P.stress.values + 2.0) + 1.0
    cross = (((P.hand == 0) & (P.x == 3)) | ((P.hand == 1) & (P.x == 0))).values
    rest = sm * (P.njs_buff * np.where(P.is_stream == 1, 1.05, 1.0) * P.wall_buff).values * (1 + v2["crossover"] * cross) \
        * (1 + v2["horizontal"] * P.cut_direction.isin([2, 3]).values) * (1 + v2["diagonal"] * P.cut_direction.isin([4, 5, 6, 7]).values)
    notes = P.n_cubes.clip(1, 4).values
    os_ = one_hand.reindex(lbs).values
    def rating(speed, parity):
        d = speed * (1 - 1.4 ** -speed) * rest * (1 + parity * P.parity_error.values)
        LD, ACT = el.note_matrix(np.log(np.maximum(d, 1e-3)), notes, code)
        return el.skill50(LD, ACT, v2["slope"]) + np.where(os_, np.log(v2["one_saber"]), 0)
    def score(t):
        e = b - np.polyval(np.polyfit(t, b, 3), t)
        return {"r2": 1 - e.var() / b.var(), "off1": int((np.abs(e) > 1).sum()), "off2": int((np.abs(e) > 2).sum())}
    f = P.frequency.values; dd = P.dist_diff.values; mdd = np.median(dd)
    raw = (P.ebpm_raw.values / 30)
    variants = {
        "swing speed (today, = eBPM x distanceDiff)": (P.swing_speed.values, v2["parity"]),
        "eBPM (analyzer, with reset doubling)": (f * mdd, v2["parity"]),
        "eBPM x distanceDiff^0.5": (f * dd ** 0.5 * mdd ** 0.5, v2["parity"]),
        "eBPM x distanceDiff^2": (f * dd ** 2 / mdd, v2["parity"]),
    }
    out = {}
    print(f"\n3. pass rating v2 on {len(lbs)} maps with attempts-based pass difficulty (R2 of a cubic fit, maps off by > 1 / > 2 logits):")
    for name, (spd, par) in variants.items():
        r = score(rating(spd, par)); out[name] = r
        print(f"   {name:52s} R2 {r['r2']:.4f}   {r['off1']:4d} / {r['off2']:3d}", flush=True)
    # raw eBPM: no reset doubling, so the reset term has to carry it; refit it on a grid
    best = None
    for par in [-0.14, 0.25, 0.5, 0.75, 1.0, 1.5]:
        r = score(rating(raw * mdd, par))
        print(f"   raw eBPM (no reset doubling), reset term x{1 + par:.2f}{'':18s} R2 {r['r2']:.4f}   {r['off1']:4d} / {r['off2']:3d}", flush=True)
        if best is None or r["r2"] > best[1]["r2"]: best = (par, r)
    out[f"raw eBPM, best reset term x{1 + best[0]:.2f}"] = best[1]
    RES["pass_v2"] = out

json.dump(RES, open(os.path.join(args.out, "a26_ebpm.json"), "w"), indent=1, default=float)
print("wrote", os.path.join(args.out, "a26_ebpm.json"))
