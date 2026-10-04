"""Loading + joining of the replay-study outputs (ReplayStudy: notes_obs.csv.gz, replays.csv, swings.csv)."""
import os
import numpy as np
import pandas as pd

SCORING_NORMAL = 3  # ReplayDecoder.ScoringType.Normal (regular notes)


NOTE_DTYPES = {"lb_id": "category", "score_id": "int32", "spawn": "float32", "color": "int8", "x": "int8", "y": "int8", "cut_dir": "int8",
               "scoring_type": "int8", "event_type": "int8", "event_time": "float32", "swing_i": "int32", "pre": "float32", "post": "float32",
               "acc": "float32", "before_r": "float32", "after_r": "float32", "dist": "float32", "saber_speed": "float32", "time_dev": "float32",
               "cutdir_dev": "float32", "angle": "float32", "angle_z": "float32", "tip_len": "float32", "tip_peak": "float32", "tip_turn": "float32", "gap": "float32"}


def read_gz_tolerant(path):
    """Decompress concatenated gzip members; ignore a truncated trailing member (file still being written)."""
    import zlib, io
    raw = open(path, "rb").read()
    out = []
    pos = 0
    while pos < len(raw):
        dco = zlib.decompressobj(wbits=31)
        try:
            out.append(dco.decompress(raw[pos:]))
        except zlib.error:
            break
        if not dco.eof:
            break
        pos = len(raw) - len(dco.unused_data)
    data = b"".join(out)
    cut = data.rfind(b"\n")           # drop a partial last line
    return io.BytesIO(data[: cut + 1])


def cache(dirpath):
    """Parse the CSV outputs once and keep parquet copies next to them (fast reloads)."""
    rp, sw, n = _load_csv(dirpath, True)
    rp.to_parquet(os.path.join(dirpath, "replays.parquet")); sw.to_parquet(os.path.join(dirpath, "swings.parquet")); n.to_parquet(os.path.join(dirpath, "notes.parquet"))


def load(dirpath, notes=True, keep_lbs=None):
    if os.path.exists(os.path.join(dirpath, "notes.parquet")):
        rp = pd.read_parquet(os.path.join(dirpath, "replays.parquet")); sw = pd.read_parquet(os.path.join(dirpath, "swings.parquet"))
        n = pd.read_parquet(os.path.join(dirpath, "notes.parquet")) if notes else None
        if n is not None: n["lb_id"] = n["lb_id"].astype(str)
        return rp, sw, n
    return _load_csv(dirpath, notes)


def _load_csv(dirpath, notes=True):
    rp = pd.read_csv(os.path.join(dirpath, "replays.csv"), dtype={"lb_id": str, "player_id": str})
    rp["stratum_kind"] = rp.stratum.str.replace(r"\d+", "", regex=True)
    rp["pctile"] = np.where(rp.stratum_kind == "top", 0.0, pd.to_numeric(rp.stratum.str.extract(r"(\d+)")[0], errors="coerce") / 100)
    sw = pd.read_csv(os.path.join(dirpath, "swings.csv"), dtype={"lb_id": str}, on_bad_lines="skip")
    n = None
    if notes:
        n = pd.read_csv(read_gz_tolerant(os.path.join(dirpath, "notes_obs.csv.gz")), dtype=NOTE_DTYPES)
        n["lb_id"] = n["lb_id"].astype(str)
        done = set(rp.lb_id) & set(sw.lb_id) & set(n.lb_id.unique())
        rp, sw, n = rp[rp.lb_id.isin(done)], sw[sw.lb_id.isin(done)], n[n.lb_id.isin(done)]
        rp = rp.drop_duplicates("score_id")
    return rp, sw, n


def attach_ai(notes, ai):
    """Join per-note ML predictions to replay note events by (map, time, x, y, color).
    ai: ai_notes_prod with columns lb_id,time,acc,key (key digits = x y dir color)."""
    a = ai[ai.key.notna()].copy()
    k = a.key.astype(str).str.zfill(4)
    a["x"] = k.str[0].astype(int); a["y"] = k.str[1].astype(int); a["color"] = k.str[3].astype(int)
    a["tkey"] = (a.time * 100).round().astype(int)
    a = a[["lb_id", "tkey", "x", "y", "color", "acc"]].rename(columns={"acc": "ai_acc"})
    a = a.drop_duplicates(["lb_id", "tkey", "x", "y", "color"])
    n = notes.copy()
    n["tkey"] = (n.spawn * 100).round().astype(int)
    out = n.merge(a, on=["lb_id", "tkey", "x", "y", "color"], how="left")
    # tolerate 1-centisecond rounding differences
    miss = out.ai_acc.isna()
    if miss.any():
        for off in (-1, 1):
            n2 = n[miss.values].copy(); n2["tkey"] = n2.tkey + off
            j = n2.merge(a, on=["lb_id", "tkey", "x", "y", "color"], how="left")
            out.loc[miss.values, "ai_acc"] = j.ai_acc.values
            miss = out.ai_acc.isna()
    return out
