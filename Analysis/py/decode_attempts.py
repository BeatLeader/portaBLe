"""Decode /admin/attemptsexport pages (attempts-<page>-of-<n>.bin.gz) into parquet.

Usage: python decode_attempts.py <out_dir> <attempts-*.bin.gz ...>

Each page is gzip-compressed protobuf: length-prefixed ExportAttempt messages with field number 1 (protobuf-net
SerializeWithLengthPrefix, PrefixStyle.Base128). Field numbers follow ExportAttempt in beatleader-server
Controllers/Admin/StatsAdminController.cs; protobuf-net omits zero / false / null values, so missing fields take those
defaults. Pages are decoded in parallel and written as <out_dir>/attempts-<page>.parquet in row groups.
"""
import gzip, os, re, struct, sys
from concurrent.futures import ProcessPoolExecutor
import pyarrow as pa, pyarrow.parquet as pq

# field number -> (column, kind); kind: i = int (varint), f = float (fixed32), s = string, b = bool, n = nullable int
FIELDS = {1: ("Id", "i"), 2: ("PlayerId", "s"), 3: ("LeaderboardId", "s"), 4: ("Type", "i"), 5: ("Timeset", "i"),
          6: ("Time", "f"), 7: ("StartTime", "f"), 8: ("Speed", "f"), 9: ("Accuracy", "f"), 10: ("BaseScore", "i"),
          11: ("ModifiedScore", "i"), 12: ("Modifiers", "s"), 13: ("AttemptsCount", "i"), 14: ("MissedNotes", "i"),
          15: ("BadCuts", "i"), 16: ("BombCuts", "i"), 17: ("WallsHit", "i"), 18: ("Pauses", "i"), 19: ("FullCombo", "b"),
          20: ("MaxCombo", "i"), 21: ("FcAccuracy", "f"), 22: ("Pp", "f"), 23: ("ScoreId", "n"), 24: ("Hmd", "i"),
          25: ("Controller", "i"), 26: ("Platform", "s"), 27: ("AccLeft", "f"), 28: ("AccRight", "f"), 29: ("LeftTiming", "f"),
          30: ("RightTiming", "f"), 31: ("MaxStreak", "n"), 32: ("Replay", "s"), 33: ("Timepost", "i")}
DEFAULTS = {"i": 0, "f": 0.0, "s": "", "b": False, "n": None}
TYPES = {"i": pa.int64(), "f": pa.float32(), "s": pa.string(), "b": pa.bool_(), "n": pa.int64()}
SCHEMA = pa.schema([(name, TYPES[kind]) for name, kind in FIELDS.values()])
ENDTYPE = {0: "unknown", 1: "clear", 2: "fail", 3: "restart", 4: "quit", 5: "practice"}
BATCH = 1_000_000


def _varint(b, i):
    r = s = 0
    while True:
        c = b[i]; i += 1
        r |= (c & 0x7F) << s
        if c < 0x80:
            return r, i
        s += 7


def _signed(v):  # protobuf-net writes negative int32 as 10-byte two's complement varints
    return v - (1 << 64) if v >= (1 << 63) else v


def decode_page(path, out_dir):
    data = gzip.open(path, "rb").read()
    page = re.search(r"attempts-(\d+)", os.path.basename(path))
    out = os.path.join(out_dir, f"attempts-{page.group(1) if page else os.path.basename(path)}.parquet")
    writer = pq.ParquetWriter(out, SCHEMA, compression="zstd")
    cols = {name: [] for name, _ in FIELDS.values()}
    n = i = 0
    end = len(data)
    while i < end:
        key, i = _varint(data, i)
        if key != (1 << 3 | 2):
            raise ValueError(f"{path}: unexpected record key {key} at byte {i}")
        length, i = _varint(data, i)
        stop = i + length
        row = {}
        while i < stop:
            k, i = _varint(data, i)
            f, wt = k >> 3, k & 7
            if wt == 0:
                v, i = _varint(data, i)
            elif wt == 5:
                v = struct.unpack_from("<f", data, i)[0]; i += 4
            elif wt == 2:
                ln, i = _varint(data, i)
                v = data[i:i + ln].decode("utf-8", "replace"); i += ln
            elif wt == 1:
                v = struct.unpack_from("<d", data, i)[0]; i += 8
            else:
                raise ValueError(f"{path}: unsupported wire type {wt}")
            row[f] = v
        for f, (name, kind) in FIELDS.items():
            v = row.get(f, DEFAULTS[kind])
            if v is not None and kind in "in":
                v = _signed(v)
            elif kind == "b":
                v = bool(v)
            cols[name].append(v)
        n += 1
        if n % BATCH == 0:
            writer.write_table(pa.table(cols, schema=SCHEMA))
            cols = {name: [] for name in cols}
    if cols["Id"]:
        writer.write_table(pa.table(cols, schema=SCHEMA))
    writer.close()
    return path, out, n


if __name__ == "__main__":
    out_dir, paths = sys.argv[1], sys.argv[2:]
    os.makedirs(out_dir, exist_ok=True)
    total = 0
    with ProcessPoolExecutor(max_workers=min(len(paths), max(1, (os.cpu_count() or 2) // 2))) as pool:
        for path, out, n in pool.map(decode_page, paths, [out_dir] * len(paths)):
            total += n
            print(f"{os.path.basename(path)}: {n:,} attempts -> {out}", flush=True)
    print(f"total {total:,} attempts")
