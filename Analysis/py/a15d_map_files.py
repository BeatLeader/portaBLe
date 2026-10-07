"""A15d: map-file features the analyzer's swing table does not carry (jump distance / reaction time, bombs, walls, arcs, chains), and
how much of the acc model's disagreement is a mapper or a song effect (predicted from the *other* maps of the same mapper / song).

Usage: python a15d_map_files.py --maps-dir maps --maps <a15_maps.parquet> --db-maps <maps.parquet from decode_dump> [--content <a15b_maps.parquet>]
"""
import argparse, json, os
import numpy as np, pandas as pd
from sklearn.linear_model import RidgeCV
from sklearn.model_selection import GroupKFold
from sklearn.preprocessing import StandardScaler
from sklearn.pipeline import make_pipeline

ap = argparse.ArgumentParser()
ap.add_argument("--maps-dir", required=True); ap.add_argument("--maps", required=True); ap.add_argument("--db-maps", required=True)
ap.add_argument("--content", default=None)
args = ap.parse_args()
cache = os.path.join(os.path.dirname(args.maps), "a15_mapfile_features.parquet")

def half_jump(njs, bpm, offset):
    """Beat Saber's half jump duration (beats) and jump distance (m), BeatmapObjectSpawnMovementData."""
    num = 60.0 / bpm; hjd = 4.0
    while njs * num * hjd > 17.999: hjd /= 2
    hjd = max(hjd + offset, 0.25)
    return hjd, njs * num * hjd * 2

def load(path):
    try: return json.load(open(path, encoding="utf-8-sig"))
    except Exception: return None

def map_features(d, bpm):
    v = str(d.get("_version") or d.get("version") or "2")
    if v.startswith("2"):
        notes = [n for n in d.get("_notes", []) if n.get("_type") in (0, 1)]; bombs = [n for n in d.get("_notes", []) if n.get("_type") == 3]
        t = np.array([n["_time"] for n in notes]) if notes else np.zeros(0)
        walls = [(o.get("_time", 0), o.get("_duration", 0), o.get("_lineIndex", 0), o.get("_width", 1), o.get("_type", 0)) for o in d.get("_obstacles", [])]
        arcs, chains = len(d.get("_sliders", [])), 0
        wall_crouch = sum(1 for w in walls if w[4] == 1)
    elif v.startswith("3"):
        notes = d.get("colorNotes", []); bombs = d.get("bombNotes", [])
        t = np.array([n.get("b", 0) for n in notes]) if notes else np.zeros(0)
        walls = [(o.get("b", 0), o.get("d", 0), o.get("x", 0), o.get("w", 1), o.get("y", 0)) for o in d.get("obstacles", [])]
        arcs, chains = len(d.get("sliders", [])), len(d.get("burstSliders", []))
        wall_crouch = sum(1 for o in d.get("obstacles", []) if o.get("y", 0) >= 2)
    else:  # v4: object arrays index into *Data arrays
        notes = d.get("colorNotes", []); bombs = d.get("bombNotes", [])
        t = np.array([n.get("b", 0) for n in notes]) if notes else np.zeros(0)
        od = d.get("obstaclesData", [])
        walls = [(o.get("b", 0), od[o.get("i", 0)].get("d", 0) if od else 0, od[o.get("i", 0)].get("x", 0) if od else 0,
                  od[o.get("i", 0)].get("w", 1) if od else 1, od[o.get("i", 0)].get("y", 0) if od else 0) for o in d.get("obstacles", [])]
        arcs, chains = len(d.get("arcs", [])), len(d.get("chains", []))
        wall_crouch = sum(1 for w in walls if w[4] >= 2)
    if len(t) < 10: return None
    span_b = max(t.max() - t.min(), 1.0); span_s = span_b * 60.0 / bpm
    wall_beats = sum(max(w[1], 0) for w in walls)
    centre = sum(1 for w in walls if w[2] <= 2 and w[2] + w[3] - 1 >= 1)          # covers lane 1 or 2
    return {"notes": len(t), "bombs_per_note": len(bombs) / len(t), "bombs_per_s": len(bombs) / span_s,
            "walls_per_s": len(walls) / span_s, "wall_cover": min(wall_beats / span_b, 5.0), "centre_walls_per_s": centre / span_s,
            "crouch_walls_per_s": wall_crouch / span_s, "arcs_per_note": arcs / len(t), "chains_per_note": chains / len(t)}

if os.path.exists(cache):
    F = pd.read_parquet(cache)
else:
    DM = pd.read_parquet(args.db_maps)[["Id", "Hash", "ModeName", "DifficultyName"]]
    rows = []
    for h, g in DM.groupby("Hash"):
        folder = os.path.join(args.maps_dir, h.upper())
        if not os.path.isdir(folder): continue
        info = load(os.path.join(folder, "Info.dat")) or load(os.path.join(folder, "info.dat"))
        if not info: continue
        bpm = info.get("_beatsPerMinute") or (info.get("audio") or {}).get("bpm") or 120
        diffs = {}
        for st in info.get("_difficultyBeatmapSets", []):
            for b in st.get("_difficultyBeatmaps", []):
                diffs[(st.get("_beatmapCharacteristicName"), b.get("_difficulty"))] = (b.get("_beatmapFilename"), b.get("_noteJumpMovementSpeed", 10), b.get("_noteJumpStartBeatOffset", 0))
        for b in info.get("difficultyBeatmaps", []):
            diffs[(b.get("characteristic"), b.get("difficulty"))] = (b.get("beatmapDataFilename"), b.get("noteJumpMovementSpeed", 10), b.get("noteJumpStartBeatOffset", 0))
        for r in g.itertuples():
            key = diffs.get((r.ModeName, r.DifficultyName))
            if not key or not key[0]: continue
            d = load(os.path.join(folder, key[0]))
            if not d: continue
            f = map_features(d, bpm)
            if not f: continue
            njs = key[1] or 10; hjd, jd = half_jump(njs, bpm, key[2] or 0)
            f.update(lb_id=r.Id, jd=jd, reaction_s=hjd * 60.0 / bpm, offset=key[2] or 0, bpm=bpm)
            rows.append(f)
    F = pd.DataFrame(rows); F.to_parquet(cache)
    print(f"parsed {len(F)} difficulties -> {cache}")

M = pd.read_parquet(args.maps)
DM = pd.read_parquet(args.db_maps)[["Id", "SongId"]].rename(columns={"Id": "lb_id"})
M = M.merge(DM, on="lb_id", how="left")
w = (M.n >= 100).values
# mapper / song effects: predict each map's disagreement by the mean of the *other* maps of the same mapper (song)
for key in ["Mapper", "SongId"]:
    g = M[w].groupby(key).e
    s, c = g.transform("sum"), g.transform("count")
    loo = ((s - M.e[w]) / (c - 1)).where(c >= 2)
    ok = loo.notna()
    r = np.corrcoef(loo[ok], M.e[w][ok])[0, 1]
    print(f"{key}: {ok.sum()} maps with another map of the same {key.lower()}; leave-one-out mean predicts e with r {r:.3f} (R2 ~ {max(r, 0) ** 2:.1%})")

F = F.rename(columns={c: f"{c}_file" for c in F.columns if c != "lb_id" and c in M.columns})   # bpm, notes exist already
M = M.merge(F, on="lb_id", how="inner")
if args.content:
    C = pd.read_parquet(args.content); M = M.merge(C.drop(columns=["e"], errors="ignore"), on="lb_id", how="left")
w = (M.n >= 100).values
new = ["jd", "reaction_s", "offset", "bombs_per_note", "bombs_per_s", "walls_per_s", "wall_cover", "centre_walls_per_s", "crouch_walls_per_s", "arcs_per_note", "chains_per_note"]
for c in new: M[c] = M[c].replace([np.inf, -np.inf], np.nan).fillna(M[c].median())
print(f"\n{len(M)} maps with map-file features; JD p5/p50/p95 {M.jd.quantile(.05):.1f} / {M.jd.median():.1f} / {M.jd.quantile(.95):.1f} m, "
      f"reaction {M.reaction_s.quantile(.05):.2f} / {M.reaction_s.median():.2f} / {M.reaction_s.quantile(.95):.2f} s")
print("corr with the disagreement e (maps with >= 100 scores):")
cc = pd.Series({c: np.corrcoef(M.loc[w, c], M.e[w])[0, 1] for c in new}).sort_values(key=np.abs, ascending=False)
print(cc.round(3).to_string())
feat = [c for c in pd.read_parquet(args.maps).select_dtypes(np.number).columns if c not in
        ("d", "n", "se", "mean_skill", "skill_slope", "d_top25", "n_top25", "d_cv", "e", "year", "log_n") and not c.startswith("grind_")]
layout = ["top_row", "bottom_row", "outer", "cross", "dot", "horizontal", "diagonal"] if args.content else []
groups = M.hash.str.upper().values
def resid(cols):
    X = M[cols].values; t = M.d.values; p = np.zeros(len(t))
    for a, b in GroupKFold(5).split(X, t, groups):
        p[b] = make_pipeline(StandardScaler(), RidgeCV(alphas=np.logspace(-2, 4, 25))).fit(X[a], t[a]).predict(X[b])
    return t - p
r0 = resid(feat)
for name, cols in [("+ layout", layout), ("+ map file", new), ("+ layout + map file", layout + new),
                   ("+ layout + map file + reading terms", layout + new + ["jd_lo", "jd_hi", "rt_lo"])]:
    M["jd_lo"] = np.maximum(0, 14 - M.jd); M["jd_hi"] = np.maximum(0, M.jd - 22); M["rt_lo"] = np.maximum(0, 0.5 - M.reaction_s)
    if not cols: continue
    r = resid(feat + cols)
    print(f"  {name:38s} disagreement SD {r[w].std():.4f} (base {r0[w].std():.4f}); explains {1 - r[w].var() / r0[w].var():.1%}")
