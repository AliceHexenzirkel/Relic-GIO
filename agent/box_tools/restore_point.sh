#!/usr/bin/env bash
# restore_point.sh [1.6|2.8] [folder|latest]
#
# Bring the game progress back to a point saved with save_point.sh:
#   hk4e_db_user (DROP + CREATE + import), redis/dump.rdb, sdk/data/sdk.db.
# Does NOT touch hk4e_db_config (events, advertised IP) nor the txt files -- those stay the
# way the agent keeps them. Everything played AFTER the saved point is lost -- that is the idea.
#
# The stack is stopped during the restore and restarted only if it was running before.
# Do NOT run it at the same time as a job started from the app (Start/Stop/Prepare).
set -euo pipefail

V="${1:-1.6}"
SEL="${2:-latest}"
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
RP_ROOT="$(dirname "$D")/restore_points"
PROJ="$(basename "$D" | tr 'A-Z' 'a-z' | sed 's/[^a-z0-9_-]//g')"

SRC="$RP_ROOT/$V/$SEL"
[ -d "$SRC" ] || { echo "Restore point $SRC does not exist"; echo "Available points:"; ls -1 "$RP_ROOT/$V" 2>/dev/null || echo "  (none)"; exit 1; }
SRC="$(readlink -f "$SRC")"
for f in hk4e_db_user.sql.gz dump.rdb sdk.db; do
  [ -s "$SRC/$f" ] || { echo "Incomplete point: $f is missing from $SRC"; exit 1; }
done

MYSQL_PWD="$(sed -n 's/^MYSQL_ROOT_PASSWORD=//p' "$D/.env" | head -1)"
[ -n "$MYSQL_PWD" ] || { echo "MYSQL_ROOT_PASSWORD not found in $D/.env"; exit 1; }
export MYSQL_PWD

dc() { docker compose --project-directory "$D" "$@"; }
mysql_ready() { dc exec -T -e MYSQL_PWD="$MYSQL_PWD" mysql mysql -uroot -e "SELECT 1" >/dev/null 2>&1; }

echo "== Restoring $V from: $SRC"
[ -f "$SRC/info.txt" ] && sed 's/^/   /' "$SRC/info.txt"

# The agent's watchdog would see the stack "down" during the mysql-only phase (two sweeps at
# 120s are enough) and would start the WHOLE stack over the import in progress -- players would
# log into a half-imported database. Stop the agent while we work; restart it ONLY if the
# restore succeeded.
AGENT_WAS_ACTIVE=0
if systemctl is-active --quiet gio-agent 2>/dev/null; then
  AGENT_WAS_ACTIVE=1
  echo "== Temporarily stopping the gio-agent service (its watchdog would interfere with the restore)..."
  systemctl stop gio-agent
fi
cleanup() {
  rc=$?
  if [ "$AGENT_WAS_ACTIVE" = 1 ]; then
    if [ "$rc" -eq 0 ]; then
      systemctl start gio-agent || echo "WARNING: could not restart gio-agent -- start it by hand: systemctl start gio-agent"
    else
      echo "WARNING: the restore failed (code $rc); leaving gio-agent STOPPED so the watchdog does not"
      echo "start the stack over an incomplete database. Once repaired, start it: systemctl start gio-agent"
    fi
  fi
}
trap cleanup EXIT

WAS_UP=0
if [ -n "$(dc ps -q 2>/dev/null)" ]; then WAS_UP=1; fi

if [ "$WAS_UP" = 1 ]; then
  echo "== Stopping the $V stack (may take a few minutes if someone played)..."
  dc down
else
  if [ -n "$(docker compose ls -q 2>/dev/null | grep -v "^${PROJ}$" || true)" ]; then
    echo "The $V stack is stopped and the other one is running -- start $V from the app first, then retry."
    exit 1
  fi
fi

echo "== Putting redis + sdk back..."
cp -a "$SRC/dump.rdb" "$D/redis/dump.rdb"
cp -a "$SRC/sdk.db"   "$D/sdk/data/sdk.db"

echo "== Starting only mysql..."
dc up -d mysql >/dev/null
for i in $(seq 1 60); do mysql_ready && break; sleep 3; [ "$i" = 60 ] && { echo "MySQL did not start"; exit 1; }; done

echo "== Recreating and importing hk4e_db_user..."
dc exec -T -e MYSQL_PWD="$MYSQL_PWD" mysql mysql -uroot -e \
  "DROP DATABASE IF EXISTS hk4e_db_user; CREATE DATABASE hk4e_db_user CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;"
zcat "$SRC/hk4e_db_user.sql.gz" | dc exec -T -e MYSQL_PWD="$MYSQL_PWD" mysql mysql -uroot \
  --default-character-set=utf8mb4 hk4e_db_user

if [ "$WAS_UP" = 1 ]; then
  echo "== Restarting the $V stack..."
  dc up -d >/dev/null
  echo "DONE. The $V stack is running with the progress from the saved point."
else
  dc down >/dev/null
  echo "DONE. The progress is restored; start $V from the app whenever you want."
fi
