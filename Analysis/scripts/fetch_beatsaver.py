"""Fetch BeatSaver metadata (upload dates, versions, ranked flags) for the maps of a portaBLe database.

Usage: python fetch_beatsaver.py <portaBLe .db> <out.csv>
Uses /maps/hash/{h1,...,h50} (comma-separated, up to 50 per request); hashes it does not return are retried one by one and
recorded as missing if BeatSaver no longer has them. Paced (~2 requests/s) with retries on 429/5xx; rows already in
out.csv are kept, so the script can be re-run to resume or to add new maps.
"""
import csv, json, os, sqlite3, sys, time, urllib.error, urllib.request

API = "https://api.beatsaver.com"
UA = "portaBLe-analysis (BeatLeader; github.com/BeatLeader/portaBLe)"
FIELDS = ["hash", "key", "name", "song", "mapper", "uploader", "uploaded", "created_at", "last_published_at",
          "version_created_at", "versions", "curated_at", "bl_ranked", "ss_ranked", "declared_ai", "bpm", "duration", "missing"]


def get(path, tries=5):
    for attempt in range(tries):
        req = urllib.request.Request(API + path, headers={"User-Agent": UA, "Accept": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                return json.load(r)
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            if e.code in (429, 500, 502, 503, 504) and attempt < tries - 1:
                time.sleep(int(e.headers.get("Retry-After") or 5 * (attempt + 1)))
                continue
            raise
        except urllib.error.URLError:
            if attempt < tries - 1:
                time.sleep(5 * (attempt + 1))
                continue
            raise


def row(h, m):
    if m is None:
        return {"hash": h, "missing": 1}
    version = next((v for v in m.get("versions", []) if v.get("hash", "").lower() == h), {})
    meta = m.get("metadata", {})
    return {"hash": h, "key": m.get("id"), "name": m.get("name"), "song": meta.get("songName"), "mapper": meta.get("levelAuthorName"),
            "uploader": (m.get("uploader") or {}).get("name"), "uploaded": m.get("uploaded"), "created_at": m.get("createdAt"),
            "last_published_at": m.get("lastPublishedAt"), "version_created_at": version.get("createdAt"),
            "versions": len(m.get("versions", [])), "curated_at": m.get("curatedAt"), "bl_ranked": int(bool(m.get("blRanked"))),
            "ss_ranked": int(bool(m.get("ranked"))), "declared_ai": m.get("declaredAi"), "bpm": meta.get("bpm"),
            "duration": meta.get("duration"), "missing": 0}


def main(db, out):
    hashes = sorted({h.lower() for (h,) in sqlite3.connect(db).execute("select distinct Hash from Leaderboards") if h})
    done = set()
    if os.path.exists(out):
        with open(out, newline="", encoding="utf-8") as f:
            done = {r["hash"] for r in csv.DictReader(f)}
    todo = [h for h in hashes if h not in done]
    print(f"{len(hashes)} map hashes, {len(done)} already fetched, {len(todo)} to go", flush=True)
    new = not os.path.exists(out)
    with open(out, "a", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=FIELDS)
        if new:
            w.writeheader()
        for i in range(0, len(todo), 50):
            batch = todo[i:i + 50]
            res = get("/maps/hash/" + ",".join(batch)) or {}
            if len(batch) == 1 and isinstance(res, dict) and "id" in res:      # single-hash responses are the map itself
                res = {batch[0]: res}
            res = {k.lower(): v for k, v in res.items() if v}
            for h in batch:
                m = res.get(h)
                if m is None:                                                   # not in the bulk answer: ask once more directly
                    time.sleep(0.5)
                    m = get("/maps/hash/" + h)
                w.writerow(row(h, m))
            f.flush()
            print(f"  {min(i + 50, len(todo))}/{len(todo)}", flush=True)
            time.sleep(0.5)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
