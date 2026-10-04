"""Convert portaBLe wwwroot/dump.zip (JSON export) into parquet tables.

Usage: python decode_dump.py <dump.zip> <out_dir>
Writes scores.parquet, maps.parquet, players.parquet.
"""
import sys, zipfile, time
import orjson
import pandas as pd

zip_path, out_dir = sys.argv[1], sys.argv[2]
t = time.time()
with zipfile.ZipFile(zip_path) as z:
    info = z.infolist()[0]
    raw = z.read(info)
print(f"read {len(raw)/1e6:.0f} MB in {time.time()-t:.0f}s", flush=True)
data = orjson.loads(raw)
del raw
print("keys:", list(data.keys()), {k: len(v) for k, v in data.items()}, flush=True)

pd.DataFrame(data["Players"]).to_parquet(f"{out_dir}/players.parquet")
maps = pd.json_normalize(data["Maps"])
maps.to_parquet(f"{out_dir}/maps.parquet")
scores = pd.DataFrame(data["Scores"])
scores.to_parquet(f"{out_dir}/scores.parquet")
print(maps.columns.tolist())
print(scores.dtypes)
print(scores.head())
print(f"done in {time.time()-t:.0f}s")
