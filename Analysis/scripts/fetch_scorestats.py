"""Fetch per-map attempt statistics (/leaderboard/scorestats/{id}) for every leaderboard in lbs.csv.
Run on the server against the local origin (low-rate, resumable):
  systemd-run --unit=bl-scorestats --collect -p MemoryMax=300M -p CPUQuota=50% -p Nice=19 python3 fetch_scorestats.py
Output: /root/analysis/out/scorestats.jsonl  (one JSON object per leaderboard; ~6 KB each)
"""
import csv, json, time, urllib.request

OUT = "/root/analysis/out/scorestats.jsonl"
done = set()
try:
    for l in open(OUT):
        done.add(json.loads(l)["lb_id"])
except Exception:
    pass
out = open(OUT, "a")
rows = list(csv.DictReader(open("/root/analysis/lbs.csv")))
n = 0
for r in rows:
    lb = r["lb_id"]
    if lb in done:
        continue
    for attempt in range(3):
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:5000/leaderboard/scorestats/{lb}", timeout=30) as resp:
                d = json.loads(resp.read())
            d["lb_id"] = lb
            out.write(json.dumps(d) + "\n"); out.flush()
            break
        except Exception:
            time.sleep(2 * (attempt + 1))
    n += 1
    time.sleep(0.08)
    if n % 500 == 0:
        print(n, flush=True)
print("finished", n)
