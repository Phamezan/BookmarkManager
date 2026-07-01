#!/usr/bin/env bash
# Measure graceful shutdown of the API: start as child, SIGINT, time until exit.
set -u
cd "/c/Users/Pham2/source/repos/BookmarkManager/src/BookmarkManager.Api"
rm -f bookmarks.db bookmarks.db-shm bookmarks.db-wal 2>/dev/null

dotnet run --no-build --urls "http://localhost:5095" \
  --ConnectionStrings:Default="Data Source=bookmarks.db" >/tmp/srv.log 2>&1 &
SRV=$!
echo "server pid=$SRV"

READY=0
for i in $(seq 1 40); do
  if curl -s -o /dev/null -w "%{http_code}" http://localhost:5095/health/live 2>/dev/null | grep -q 200; then
    READY=1; echo "ready after ${i}s"; break
  fi
  sleep 0.5
done
if [ "$READY" != "1" ]; then echo "server never became ready"; kill -9 $SRV 2>/dev/null; exit 2; fi

echo "sending SIGINT at epoch $(date +%s.%N)"
kill -INT $SRV 2>/dev/null

START=$(date +%s)
while kill -0 $SRV 2>/dev/null; do
  NOW=$(date +%s)
  ELAPSED=$((NOW - START))
  if [ "$ELAPSED" -gt 60 ]; then
    echo "TIMEOUT after ${ELAPSED}s — process still alive, force killing"
    kill -9 $SRV 2>/dev/null
    break
  fi
  sleep 0.1
done
END=$(date +%s)
echo "shutdown took $((END - START))s"

echo "=== relevant log ==="
grep -iE "shutting down|stopping|stopped" /tmp/srv.log | tail -10
rm -f bookmarks.db bookmarks.db-shm bookmarks.db-wal 2>/dev/null
