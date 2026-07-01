#!/usr/bin/env bash
# Measure graceful shutdown of the REAL kestrel child process.
set -u
cd "/c/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api"
rm -f bookmarks.db bookmarks.db-shm bookmarks.db-wal 2>/dev/null

dotnet run --no-build --urls "http://localhost:5094" \
  --ConnectionStrings:Default="Data Source=bookmarks.db" >/tmp/srv2.log 2>&1 &
SRV_WRAPPER=$!
echo "wrapper pid=$SRV_WRAPPER"

READY=0
for i in $(seq 1 40); do
  if curl -s -o /dev/null -w "%{http_code}" http://localhost:5094/health/live 2>/dev/null | grep -q 200; then
    READY=1; echo "ready after ${i}s"; break
  fi
  sleep 0.5
done
if [ "$READY" != "1" ]; then echo "never ready"; kill -9 $SRV_WRAPPER 2>/dev/null; exit 2; fi

# Find the actual child dotnet process running the API (the one bound to 5094).
# `dotnet run` spawns `dotnet exec BookmarkManager.Api.dll`.
CHILD=$(ps -W 2>/dev/null | grep -i dotnet | grep -iv "run" | awk '{print $1}' | head -1)
echo "child candidate: $CHILD"
# Also list all dotnet pids for visibility
echo "all dotnet pids:"; ps -W 2>/dev/null | grep -i dotnet | awk '{print "  "$1" "$NF}'

# Send SIGINT to BOTH wrapper and child to faithfully simulate Ctrl+C in the console
echo "sending SIGINT at $(date +%s.%N) to wrapper=$SRV_WRAPPER child=$CHILD"
kill -INT $SRV_WRAPPER 2>/dev/null
[ -n "$CHILD" ] && kill -INT $CHILD 2>/dev/null

START=$(date +%s)
# Wait until port 5094 is no longer accepting connections (server truly gone)
for i in $(seq 1 600); do
  code=$(curl -s -o /dev/null -w "%{http_code}" --max-time 1 http://localhost:5094/health/live 2>/dev/null)
  if [ "$code" = "000" ]; then
    END=$(date +%s)
    echo "port 5094 free after $((END - START)).$(( (i%10) ))s (curl=$code)"
    break
  fi
  sleep 0.1
done
ELAPSED=$(( $(date +%s) - START ))
echo "total shutdown wait: ${ELAPSED}s"

echo "=== shutdown log lines ==="
grep -iE "shutting down|stopping|stopped|Application" /tmp/srv2.log | tail -10
rm -f bookmarks.db bookmarks.db-shm bookmarks.db-wal 2>/dev/null
