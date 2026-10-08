"""A25: real attempts on a map come from a mix of players; the weaker ones fail early, so later sections only see stronger players.
Models the 'typical attempt' as skills ~ Normal(mu, sigma) (Gauss-Hermite, 9 points), mu set so the mixture clears as often as the
real clean attempts, and fits one global sigma (log PassDiff units) to the observed per-section fail hazard (attempts export, a19).

Usage: python a25_attempt_mix.py --db <DB with pass profiles> [--maps 300]
"""
import argparse, base64, json, sqlite3
import numpy as np

ap = argparse.ArgumentParser(); ap.add_argument("--db", required=True); ap.add_argument("--maps", type=int, default=300)
args = ap.parse_args()
con = sqlite3.connect(args.db)
M = json.loads(con.execute("select Json from AccLossProfiles where LeaderboardId='__model__'").fetchone()[0])["pass"]
rows = [json.loads(j) for (j,) in con.execute("select Json from AccLossProfiles where LeaderboardId != '__model__'")]
rows = [r["pass"] for r in rows if "pass" in r and "observed" in r["pass"] and r["pass"]["observed"].get("clear_rate") and 0.05 < r["pass"]["observed"]["clear_rate"] < 0.95]
rng = np.random.default_rng(0); rows = [rows[i] for i in rng.choice(len(rows), size=min(args.maps, len(rows)), replace=False)]
gh_x, gh_w = np.polynomial.hermite_e.hermegauss(9); gh_w = gh_w / gh_w.sum()

def run(PP, skills):
    """dead mass per section and alive mass at its start, per skill (vectorized over skills)"""
    raw = np.frombuffer(base64.b64decode(PP["q"]), np.uint8).reshape(-1, 2)
    ld = raw[:, 0] * M["qs"] + M["q0"]; notes = raw[:, 1]
    win = np.repeat(np.arange(len(PP["win"])), PP["win"])
    K = len(skills); E = np.zeros((K, 101)); E[:, M["start"]] = 1
    nw = len(PP["win"]); dead = np.zeros((K, nw)); alive0 = np.zeros((K, nw)); last = -1
    for i in range(len(ld)):
        w = win[i]
        if w != last: alive0[:, last + 1:w + 1] = E[:, 1:].sum(1)[:, None]; last = w
        p = 1 / (1 + np.exp(-M["slope"] * (ld[i] - skills)))[:, None]
        for _ in range(notes[i]):
            hit, miss = E * (1 - p), E * p
            N = np.zeros_like(E); N[:, 1:] = hit[:, :-1]; N[:, 100] += hit[:, 100]
            N[:, 1:86] += miss[:, 16:]; dead[:, w] += miss[:, 1:16].sum(1)
            E = N
    return E[:, 1:].sum(1), dead, alive0

def mixture(PP, sigma):
    cr = PP["observed"]["clear_rate"]; lo, hi = -3.0, 6.0
    for _ in range(12):
        mu = (lo + hi) / 2; c, _, _ = run(PP, mu + sigma * gh_x)
        if (c * gh_w).sum() >= cr: hi = mu
        else: lo = mu
    c, dead, alive0 = run(PP, (lo + hi) / 2 + sigma * gh_x)
    d, a = (dead * gh_w[:, None]).sum(0), (alive0 * gh_w[:, None]).sum(0)
    return np.where(a > 1e-9, d / np.maximum(a, 1e-12), np.nan)

res = {}
for sigma in [0.0, 0.2, 0.4, 0.6, 0.8]:
    err, rho = [], []
    for PP in rows:
        pred = mixture(PP, sigma); obs = np.array([np.nan if v is None else v for v in PP["observed"]["hazard"]])
        ok = np.isfinite(pred) & np.isfinite(obs)
        if ok.sum() < 6: continue
        err.append(np.mean((pred[ok] - obs[ok]) ** 2))
        # how much of the fails the model puts in the first third of the map, vs observed
        n3 = max(1, ok.sum() // 3); rho.append((pred[ok][:n3].sum() / max(pred[ok].sum(), 1e-9)) - (obs[ok][:n3].sum() / max(obs[ok].sum(), 1e-9)))
    res[sigma] = (np.mean(err), np.mean(rho))
    print(f"sigma {sigma:.1f}: mean squared hazard error {np.mean(err):.5f}; first-third share of hazard, model minus observed {np.mean(rho):+.3f} ({len(err)} maps)", flush=True)
best = min(res, key=lambda s: res[s][0]); print("best sigma", best)
