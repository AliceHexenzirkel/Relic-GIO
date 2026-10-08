#!/usr/bin/env bash
# save_point.sh [1.6|2.8] [label]
#
# Make a restore point of the game progress for the given version:
#   - hk4e_db_user (mysqldump, gzip)  - the players' progress
#   - redis/dump.rdb                  - session state
#   - sdk/data/sdk.db                 - the login accounts
# The same set of files the agent's provisioning uses ("save").
#
# The stack is stopped before copying (redis writes dump.rdb ONLY on exit, and the gameserver
# flushes its in-memory data on stop), then brought back the way it was.
# Do NOT run it at the same time as a job started from the app (Start/Stop/Prepare).
#
# Restore: ./restore_point.sh 1.6            (the latest point)
#          ./restore_point.sh 1.6 <folder>   (a specific point)
set -euo pipefail

V="${1:-1.6}"
LABEL="${2:-}"
case "$V" in 1.6|2.8) ;; *) echo "Unknown version: $V (expected 1.6 or 2.8)"; exit 1;; esac
# Stack path: the agent config first (GIO_DIR_16/28), then /home, then /root.
D=""
if [ -f /etc/gio-agent/config ]; then
  D="$(sed -n "s/^GIO_DIR_${V/./}=//p" /etc/gio-agent/config | head -1)"
fi
if [ -z "$D" ] || [ ! -d "$D" ]; then
  for c in "/home/${V}_live" "/root/${V}_live"; do if [ -d "$c" ]; then D="$c"; break; fi; done
fi
[ -n "$D" ] && [ -f "$D/docker-compose.yml" ] || { echo "No stack for $V (looked in the agent config, /home, /root)"; exit 1; }
# restore points live next to the stacks (e.g. /root/restore_points)
RP_ROOT="$(dirname "$D")/restore_points"
# the compose project name, derived like compose itself does from the folder name ("1.6_live" -> "16_live")
PROJ="$(basename "$D" | tr 'A-Z' 'a-z' | sed 's/[^a-z0-9_-]//g')"

MYSQL_PWD="$(sed -n 's/^MYSQL_ROOT_PASSWORD=//p' "$D/.env" | head -1)"
[ -n "$MYSQL_PWD" ] || { echo "MYSQL_ROOT_PASSWORD not found in $D/.env"; exit 1; }
export MYSQL_PWD

TS="$(date +%Y%m%d-%H%M%S)"
OUT="$RP_ROOT/$V/$TS${LABEL:+-$LABEL}"
mkdir -p "$OUT"

dc() { docker compose --project-directory "$D" "$@"; }
mysql_ready() { dc exec -T -e MYSQL_PWD="$MYSQL_PWD" mysql mysql -uroot -e "SELECT 1" >/dev/null 2>&1; }

# The agent's watchdog would see the stack "down" during the mysql-only phase (two sweeps at
# 120s are enough) and would start the WHOLE stack over the operation in progress. Stop the agent
# while we work; at the end restart it ONLY if the operation succeeded -- after a failure a
# restarted agent would boot the stack over an incomplete state through that same watchdog.
AGENT_WAS_ACTIVE=0
if systemctl is-active --quiet gio-agent 2>/dev/null; then
  AGENT_WAS_ACTIVE=1
  echo "== Temporarily stopping the gio-agent service (its watchdog would interfere)..."
  systemctl stop gio-agent
fi
cleanup() {
  rc=$?
  if [ "$AGENT_WAS_ACTIVE" = 1 ]; then
    if [ "$rc" -eq 0 ]; then
      systemctl start gio-agent || echo "WARNING: could not restart gio-agent -- start it by hand: systemctl start gio-agent"
    else
      echo "WARNING: the operation failed (code $rc); leaving gio-agent STOPPED so the watchdog does not"
      echo "start the stack over an incomplete state. Once repaired, start it: systemctl start gio-agent"
    fi
  fi
}
trap cleanup EXIT

WAS_UP=0
if [ -n "$(dc ps -q 2>/dev/null)" ]; then WAS_UP=1; fi

if [ "$WAS_UP" = 1 ]; then
  echo "== Stopping the $V stack so everything is flushed to disk (may take a few minutes if someone played)..."
  dc down
else
  # the dump needs mysql, and its network conflicts with the other stack
  if [ -n "$(docker compose ls -q 2>/dev/null | grep -v "^${PROJ}$" || true)" ]; then
    echo "The $V stack is stopped and the other one is running -- start $V from the app first, then retry."
    exit 1
  fi
fi

echo "== Copying redis + sdk..."
cp -a "$D/redis/dump.rdb"   "$OUT/dump.rdb"
cp -a "$D/sdk/data/sdk.db"  "$OUT/sdk.db"

echo "== Starting only mysql for the dump..."
dc up -d mysql >/dev/null
for i in $(seq 1 60); do mysql_ready && break; sleep 3; [ "$i" = 60 ] && { echo "MySQL did not start"; exit 1; }; done

echo "== Dumping hk4e_db_user..."
dc exec -T -e MYSQL_PWD="$MYSQL_PWD" mysql mysqldump -uroot --single-transaction \
  --default-character-set=utf8mb4 hk4e_db_user | gzip > "$OUT/hk4e_db_user.sql.gz"
[ -s "$OUT/hk4e_db_user.sql.gz" ] || { echo "Empty dump -- aborting"; rm -rf "$OUT"; exit 1; }

{
  echo "version: $V"
  echo "created: $(date '+%Y-%m-%d %H:%M:%S')"
  echo "label:   ${LABEL:--}"
  ls -l --block-size=K "$OUT"
} > "$OUT/info.txt"

ln -sfn "$OUT" "$RP_ROOT/$V/latest"

if [ "$WAS_UP" = 1 ]; then
  echo "== Restarting the $V stack..."
  dc up -d >/dev/null
else
  dc down >/dev/null
fi

echo ""
echo "DONE. Restore point: $OUT"
cat "$OUT/info.txt"
