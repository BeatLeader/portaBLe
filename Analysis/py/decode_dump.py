"""Convert a portaBLe dump into parquet tables (scores / maps / players).

Usage: python decode_dump.py <dump.zip | export.bin> <out_dir>

Accepts both formats the server has produced:
  * zip with a JSON entry (old portaBLe dumps)
  * protobuf-net `BigExportResponse` (StatsAdminController export; raw `export.bin` or zipped)
Field numbers follow portaBLe/DB/ExportModels.cs.
"""
import sys, zipfile, time, struct
import pandas as pd

PLAYER_FIELDS = {1: "Name", 2: "Country", 3: "Id", 4: "Avatar"}
SCORE_FIELDS = {1: "Id", 2: "LeaderboardId", 3: "Accuracy", 4: "Modifiers", 5: "PlayerId", 6: "Timepost", 7: "FC", 8: "FCAcc"}
MAP_FIELDS = {1: "Hash", 2: "Name", 3: "CoverImage", 4: "Mapper", 5: "Id", 6: "SongId", 7: "ModeName", 8: "DifficultyName",
              9: "AccRating", 10: "PassRating", 11: "TechRating", 12: "PredictedAcc", 13: "MultiPercentage", 14: "LinearPercent",
              15: "ParityErrors", 16: "BombAvoidances"}
MOD_FIELDS = {i + 1: f"ModifiersRating.{m}{k}" for i, (m, k) in enumerate(
    (m, k) for m in ("SS", "FS", "SF", "BFS", "BSF") for k in ("PredictedAcc", "PassRating", "AccRating", "TechRating", "Stars"))}


def _varint(b, i):
    r = 0; s = 0
    while True:
        c = b[i]; i += 1
        r |= (c & 0x7F) << s
        if c < 0x80:
            return r, i
        s += 7


def _message(b, start, end, names, nested=None):
    """Decode one protobuf message into a dict using a field-number -> name map."""
    out = {}
    i = start
    while i < end:
        key, i = _varint(b, i)
        f, wt = key >> 3, key & 7
        if wt == 0:
            v, i = _varint(b, i)
        elif wt == 5:
            v = struct.unpack_from("<f", b, i)[0]; i += 4
        elif wt == 1:
            v = struct.unpack_from("<d", b, i)[0]; i += 8
        elif wt == 2:
            n, i = _varint(b, i)
            if nested and f in nested:
                out.update(_message(b, i, i + n, nested[f]))
                i += n
                continue
            v = bytes(b[i:i + n]).decode("utf-8", "replace"); i += n
        else:
            raise ValueError(f"unsupported wire type {wt}")
        if f in names:
            out[names[f]] = v
    return out


def decode_protobuf(b):
    players, scores, maps = [], [], []
    i, end = 0, len(b)
    t = time.time()
    while i < end:
        key, i = _varint(b, i)
        f, wt = key >> 3, key & 7
        n, i = _varint(b, i)
        if f == 1:
            players.append(_message(b, i, i + n, PLAYER_FIELDS))
        elif f == 2:
            scores.append(_message(b, i, i + n, SCORE_FIELDS))
            if len(scores) % 1_000_000 == 0:
                print(f"  {len(scores)} scores ({time.time()-t:.0f}s)", flush=True)
        elif f == 3:
            maps.append(_message(b, i, i + n, MAP_FIELDS, nested={17: MOD_FIELDS}))
        i += n
    return players, scores, maps


def main(path, out_dir):
    t = time.time()
    if path.endswith(".zip"):
        with zipfile.ZipFile(path) as z:
            raw = z.read(z.infolist()[0])
    else:
        raw = open(path, "rb").read()
    print(f"read {len(raw)/1e6:.0f} MB in {time.time()-t:.0f}s", flush=True)
    if raw[:1] == b"{":
        import orjson
        data = orjson.loads(raw)
        players, scores, maps = data["Players"], data["Scores"], data["Maps"]
        maps = pd.json_normalize(maps)
    else:
        players, scores, maps = decode_protobuf(memoryview(raw))
        maps = pd.DataFrame(maps)
    del raw
    scores = pd.DataFrame(scores)
    for c, default in (("Modifiers", ""), ("FC", 0), ("FCAcc", 0.0), ("Timepost", 0)):
        if c in scores:
            scores[c] = scores[c].fillna(default)
    scores["FC"] = scores["FC"].astype(bool)
    print({"players": len(players), "scores": len(scores), "maps": len(maps)})
    pd.DataFrame(players).to_parquet(f"{out_dir}/players.parquet")
    maps.to_parquet(f"{out_dir}/maps.parquet")
    scores.to_parquet(f"{out_dir}/scores.parquet")
    print(f"done in {time.time()-t:.0f}s")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
