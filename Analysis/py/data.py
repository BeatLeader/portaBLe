"""Loading + joining of the analysis datasets.

Data directory layout (DATA env var, default Analysis/data or the session scratchpad):
  scores.parquet maps.parquet players.parquet     <- decode_dump.py (portaBLe wwwroot/dump.zip, scores up to Apr 2025)
  ratings/ratings_{prod,corpus}.csv               <- RatingsDump (4 rows per leaderboard: SS/none/FS/SFS)
  ratings/swings_{tag}.csv.gz  ai_notes_{tag}.csv.gz
"""
import os
import numpy as np
import pandas as pd

DATA = os.environ.get("ANALYSIS_DATA", os.path.join(os.path.dirname(__file__), "..", "data"))

# modifiers that do not change the map or the PP multiplier (fail conditions only)
CLEAN_MODS = {"", "IF", "BE", "IF,BE", "BE,IF"}


def p(*parts):
    return os.path.join(DATA, *parts)


def load_scores(clean_only=True):
    s = pd.read_parquet(p("scores.parquet"))
    s["Modifiers"] = s["Modifiers"].fillna("")
    if clean_only:
        s = s[s["Modifiers"].isin(CLEAN_MODS)]
    s = s.rename(columns={"Id": "score_id", "LeaderboardId": "lb_id", "PlayerId": "player", "Accuracy": "acc",
                          "Timepost": "time", "FCAcc": "fc_acc"})
    s = s[(s.acc > 0) & (s.acc <= 1)].copy()
    s["player"] = s["player"].astype("category")
    s["lb_id"] = s["lb_id"].astype("category")
    return s.reset_index(drop=True)


def load_maps():
    m = pd.read_parquet(p("maps.parquet"))
    m = m.rename(columns={"Id": "lb_id", "Hash": "hash", "ModeName": "mode", "DifficultyName": "difficulty", "Name": "name",
                          "PassRating": "pass_db", "AccRating": "acc_db", "TechRating": "tech_db", "PredictedAcc": "pred_db"})
    return m[["lb_id", "hash", "name", "mode", "difficulty", "pass_db", "acc_db", "tech_db", "pred_db"]]


def load_ratings(tag="prod", mod="none"):
    r = pd.read_csv(p("ratings", f"ratings_{tag}.csv"), dtype={"lb_id": str})
    r = r[r["mod"] == mod].drop(columns=["mod"]).drop_duplicates("lb_id")
    return r


def load_swings(tag="prod"):
    return pd.read_csv(p("ratings", f"swings_{tag}.csv.gz"), dtype={"lb_id": str})


def load_ai_notes(tag="prod"):
    return pd.read_csv(p("ratings", f"ai_notes_{tag}.csv.gz"), dtype={"lb_id": str})


def scores_with_ratings(tag="prod", clean_only=True, min_scores_per_map=0):
    """Scores joined to recomputed ratings of the chosen analyzer variant (+ stored DB ratings as *_db)."""
    s = load_scores(clean_only)
    s["lb_id"] = s["lb_id"].astype(str)
    r = load_ratings(tag)
    m = load_maps()
    d = s.merge(r, on="lb_id", how="inner").merge(m, on="lb_id", how="left")
    if min_scores_per_map:
        n = d.groupby("lb_id")["acc"].transform("size")
        d = d[n >= min_scores_per_map]
    return d.reset_index(drop=True)
