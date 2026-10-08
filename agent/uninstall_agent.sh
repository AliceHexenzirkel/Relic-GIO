#!/usr/bin/env bash
# Uninstaller for the GIO agent -- removes ONLY the agent (service, files, firewall rule).
# The game stacks (1.6_live/2.8_live), docker, MySQL data and player progress are NOT touched;
# the .orig backups and payload files the agent placed INSIDE the stacks stay where they are,
# because removing those would change the running game server, not the agent.
#   bash uninstall_agent.sh          # interactive (asks once)
#   bash uninstall_agent.sh --yes    # non-interactive
# Works on RHEL-family (firewalld) and Debian-family (ufw or no firewall).
set -euo pipefail

ASSUME_YES=0
if [ "${1:-}" = "--yes" ] || [ "${1:-}" = "-y" ]; then ASSUME_YES=1; fi

echo "==== GIO agent -- uninstall ===="
if [ "$(id -u)" != 0 ]; then echo "Run as root (sudo bash uninstall_agent.sh)."; exit 1; fi

# The port must be read BEFORE the config is deleted, or the firewall rule stays behind forever.
PORT=""
if [ -f /etc/gio-agent/config ]; then
  LISTEN=$(sed -n 's/^GIO_AGENT_LISTEN=//p' /etc/gio-agent/config | head -1)
  PORT="${LISTEN##*:}"
fi
case "$PORT" in ''|*[!0-9]*) PORT=18080 ;; esac

echo "This removes: the gio-agent systemd service, /opt/gio-agent (agent + payloads),"
echo "/etc/gio-agent (the token!), /var/lib/gio-agent (state + account-copy backups)"
echo "and the firewall rule for port $PORT/tcp (if any)."
echo "The game stacks, docker and the database are NOT touched."
if [ "$ASSUME_YES" != 1 ]; then
  read -rp "Continue? [y/N]: " ok; [ "${ok:-N}" = "y" ] || { echo "cancelled"; exit 1; }
fi

# 1) service: stop + disable (removes the multi-user.target.wants symlink), then the unit file
if systemctl list-unit-files gio-agent.service >/dev/null 2>&1; then
  systemctl disable --now gio-agent 2>/dev/null || true
fi
rm -f /etc/systemd/system/gio-agent.service
systemctl daemon-reload
systemctl reset-failed gio-agent 2>/dev/null || true
echo "  gio-agent service: stopped and removed"

# 2) firewall rule -- mirror of install_agent.sh's open_port
if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
  if firewall-cmd --permanent --remove-port="$PORT"/tcp >/dev/null 2>&1; then
    firewall-cmd --reload >/dev/null 2>&1 || true
    echo "  firewalld: port $PORT/tcp closed"
  fi
elif command -v ufw >/dev/null 2>&1 && LC_ALL=C ufw status 2>/dev/null | grep -q '^Status: active'; then
  ufw delete allow "$PORT"/tcp >/dev/null 2>&1 || true
  echo "  ufw: rule for port $PORT/tcp removed"
else
  echo "  no local firewall active -- no rule to remove"
fi

# 3) files -- state/backups last, so a failed run above leaves the evidence in place
rm -rf /opt/gio-agent
rm -rf /etc/gio-agent
rm -rf /var/lib/gio-agent
echo "  files removed: /opt/gio-agent, /etc/gio-agent, /var/lib/gio-agent"

echo
echo "Done. The agent has been uninstalled. The old token is no longer valid -- an app build that"
echo "had it baked in can no longer connect. The game stacks were left untouched; if they are"
echo "running, they keep running (stop them with: docker compose --project-directory <dir> down)."
echo "Their passwords did not go with the agent: each stack keeps its own (MySQL root, Flask secret"
echo "key, MUIP sign key, the stack's internal password) in <stack dir>/creds.txt; the agent's note of"
echo "the MySQL root password the database last accepted, <stack dir>/.relic-creds, stays there too."
