#!/usr/bin/env bash
# Connect to / run commands on the test VPS (AlmaLinux 9).
#
#   scripts/vps.sh                      -> interactive shell
#   scripts/vps.sh "docker ps -a"       -> runs one command and returns its output
#
# Credentials are NOT written here. Put them in .env (already gitignored):
#   VPS_HOST=192.0.2.10
#   VPS_PORT=2024
#   VPS_USER=root
#   VPS_PASS=...            # or, preferably:
#   VPS_KEY=path/to/key.ppk   # relative to the repo root
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[ -f "$ROOT/.env" ] && set -a && . "$ROOT/.env" && set +a

HOST="${VPS_HOST:-}"
PORT="${VPS_PORT:-2024}"
USER_="${VPS_USER:-root}"
CMD="${1:-}"

if command -v plink >/dev/null 2>&1; then
  PLINK=plink
elif [ -x "/c/Program Files/PuTTY/plink.exe" ]; then
  PLINK="/c/Program Files/PuTTY/plink.exe"
else
  PLINK=""
fi

if [ -n "$PLINK" ]; then
  ARGS=(-P "$PORT" -batch)
  if [ -n "${VPS_KEY:-}" ]; then
    ARGS+=(-i "$ROOT/${VPS_KEY#"$ROOT/"}")
  elif [ -n "${VPS_PASS:-}" ]; then
    ARGS+=(-pw "$VPS_PASS")
  fi
  exec "$PLINK" "${ARGS[@]}" "$USER_@$HOST" ${CMD:+"$CMD"}
fi

# fallback: OpenSSH (needs a key in OpenSSH format, not a .ppk)
ARGS=(-p "$PORT" -o StrictHostKeyChecking=accept-new)
[ -n "${VPS_KEY_OPENSSH:-}" ] && ARGS+=(-i "$VPS_KEY_OPENSSH")
exec ssh "${ARGS[@]}" "$USER_@$HOST" ${CMD:+"$CMD"}
