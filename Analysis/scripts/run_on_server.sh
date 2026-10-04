#!/usr/bin/env bash
# Deploy + run the replay crawl on the BeatLeader storage box under hard resource caps (it is the PRODUCTION host:
# live MSSQL ~50 GB RSS, BeatLeader API, RatingAPI, ~1-2 GB RAM headroom). Nothing outside /root/analysis is touched.
#
#   KEY=<ssh key> HOST=root@<storage server> Analysis/scripts/run_on_server.sh deploy|pilot|full|status|stop|pull <dir>
set -euo pipefail
KEY="${KEY:?set KEY}"; HOST="${HOST:?set HOST}"
SSH="ssh -i $KEY -o BatchMode=yes $HOST"
cd "$(dirname "$0")/../.."
BIN=Analysis/.stage/bin/ReplayStudy-prod-linux-x64
LBS="${LBS:-lbs.csv}"          # lb_id,hash,mode,difficulty

# systemd transient unit: 1.5 GB hard cap (1.2 GB soft), no swap, 2 cores max, lowest CPU/IO priority.
# --api points at the local origin (127.0.0.1:5000) so the public CDN/rate limits are not involved; replay blobs come from the R2 CDN.
CAPS='-p MemoryMax=1500M -p MemoryHigh=1200M -p MemorySwapMax=0 -p CPUQuota=200% -p CPUWeight=10 -p IOWeight=10 -p Nice=19 -p TasksMax=300'
ENVS='-E DOTNET_GCHeapHardLimit=0x40000000 -E DOTNET_TieredPGO=0 -E DOTNET_PRINT_TELEMETRY_MESSAGE=false'

run_unit() { # unit out extra-args
  $SSH "systemd-run --unit=$1 --collect $CAPS -p WorkingDirectory=/root/analysis \
    -p StandardOutput=append:/root/analysis/out/$2.log -p StandardError=append:/root/analysis/out/$2.log $ENVS \
    /usr/bin/dotnet /root/analysis/bin/ReplayStudy/ReplayStudy.dll --lbs /root/analysis/lbs.csv --maps-dir /data/maps \
    --output /root/analysis/out/$2 --api http://127.0.0.1:5000 --top 8 --mid 6 --concurrency 3 $3"
}

case "${1:-}" in
  deploy)
    $SSH 'mkdir -p /root/analysis/bin/ReplayStudy /root/analysis/out'
    (cd "$BIN" && tar czf - .) | $SSH 'cd /root/analysis/bin/ReplayStudy && tar xzf -'
    scp -i "$KEY" -o BatchMode=yes "$LBS" "$HOST:/root/analysis/lbs.csv" ;;
  pilot)  run_unit bl-replaystudy-pilot pilot "--max-maps 20" ;;
  full)   run_unit bl-replaystudy full "" ;;     # resumable (done.txt); seeded random order => any prefix is a random sample
  status) $SSH 'systemctl is-active bl-replaystudy; tail -n 2 /root/analysis/out/full.log; uptime; free -m | sed -n 2p' ;;
  stop)   $SSH 'systemctl stop bl-replaystudy' ;;
  pull)   mkdir -p "$2"; scp -i "$KEY" -o BatchMode=yes "$HOST:/root/analysis/out/full/{notes_obs.csv.gz,swings.csv,replays.csv,leaderboards.csv,done.txt}" "$2/" ;;
  *) echo "usage: $0 deploy|pilot|full|status|stop|pull <dir>"; exit 1 ;;
esac
