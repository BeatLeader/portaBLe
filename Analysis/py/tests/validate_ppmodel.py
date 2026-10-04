"""Validate the numpy PP port against PP values computed by portaBLe's C# code (local SQLite DB)."""
import sqlite3, sys, os
import numpy as np, pandas as pd
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import ppmodel as pm

db = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "..", "..", "wwwroot", "Database.db")
con = sqlite3.connect(db)
d = pd.read_sql("""select s.Id, s.Accuracy acc, s.Modifiers mods, s.Pp, s.PassPP, s.AccPP, s.TechPP, s.BonusPp,
                          l.AccRating ar, l.PassRating pr, l.TechRating tr, l.PredictedAcc pa, l.Stars
                   from Scores s join Leaderboards l on l.Id = s.LeaderboardId
                   where s.Pp > 0 and (s.Modifiers is null or s.Modifiers = '')""", con)
full, p, a, t = pm.pp(d.acc.values, d.ar.values, d.pr.values, d.tr.values)
for name, mine, theirs in (("pp", full, d.Pp), ("pass", p, d.PassPP), ("acc", a, d.AccPP), ("tech", t, d.TechPP)):
    err = np.abs(mine - theirs.values)
    print(f"{name:5s} n={len(d)} max|err|={err.max():.4f} mean|err|={err.mean():.5f}  rel.max={np.max(err / np.maximum(theirs.values, 1e-6)):.2e}")
st = pm.stars(d.ar.values, d.pr.values, d.tr.values)
print(f"stars max|err|={np.abs(st - d.Stars.values).max():.5f}")
ar = pm.acc_rating_from_predicted(d.pa.values, d.pr.values, d.tr.values)
print(f"acc_rating(predicted_acc) vs stored (stored includes LowNoteNerf, so expect ratio<=1): ratio range {np.min(d.ar.values/ar):.3f}..{np.max(d.ar.values/ar):.3f}")
