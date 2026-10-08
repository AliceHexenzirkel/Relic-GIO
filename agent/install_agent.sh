#!/usr/bin/env bash
# Interactive installer for the GIO agent, run ON the Linux server (as root).
# Works on RHEL-family (AlmaLinux/Rocky/RHEL/CentOS Stream/Fedora -- dnf, firewalld) and Debian-family
# (Ubuntu/Debian -- apt, ufw or no firewall). A FRESH box is fine: what the agent needs and the box
# lacks is installed here -- docker + the compose v2 plugin (distro packages when the distro ships
# them, else Docker's own repository), a python3 the agent can run on (3.9+; a box whose python3
# is older gets a versioned interpreter next to it and the service uses that one) and a 7z extractor
# (7zip / p7zip-full / libarchive-tools on apt, 7zip (EPEL) / p7zip / bsdtar on dnf; optional: without
# it the hotpatch mirror is filled file by file from the CDN and no server package can be downloaded).
# A box with NO server stack yet is fine too: answer "n" to "already extracted?" and the agent
# downloads the ready-made 1.6 / 2.8 package from the Internet Archive after the install.
# Upload this together with gio_agent.py, uninstall_agent.sh and the payloads/ folder, then:
#   bash install_agent.sh              # interactive
#   bash install_agent.sh --yes        # non-interactive: takes every default (installs missing deps);
#                                      # pre-seed defaults via env (GIO_DIR_16, GIO_BIND_IP,
#                                      # GIO_AGENT_TOKEN, GIO_SERVER_NAME, GIO_MYSQL_ROOT_PASSWORD,
#                                      # GIO_FLASK_SECRET_KEY, ...; GIO_KEEP_ENV=1 = "Keep .env
#                                      # settings": the agent leaves each server's own .env as it
#                                      # is, and no password or address of a stack is asked for;
#                                      # GIO_INSTALL_DOCKER=n / GIO_INSTALL_PYTHON=n / GIO_INSTALL_7Z=n
#                                      # refuse the dependency installs; GIO_FETCH_16=y /
#                                      # GIO_FETCH_28=y = "not on this box: download it into
#                                      # GIO_DIR_16 / GIO_DIR_28"; GIO_FETCH_FOLLOW=n only prints the
#                                      # download command instead of running it)
#   bash install_agent.sh --deps-only  # only the dependency phase (python3 / docker / 7z), then exit
#   bash install_agent.sh --no-deps    # never install anything, only warn
# GIO_FETCH_16/28, GIO_INSTALL_7Z and GIO_FETCH_FOLLOW are installer-only: never written to the config.
# UPGRADE = the same command on a box that already has the agent: every value of the existing
# /etc/gio-agent/config (token, stack paths, advertised IP, ...) is the default of the run, the
# previous agent/config/state/payloads are kept under /var/lib/gio-agent/previous/ for a rollback,
# and the run refuses while the running agent has a job in progress (GIO_UPGRADE_FORCE=y overrides a
# probe that could not tell, never a running job). The service keeps its enabled/stopped state on an
# upgrade; GIO_AGENT_START=y (the launcher's install form sets it) starts it regardless.
# Details: docs/AGENT-UPGRADE.md.
set -euo pipefail

ASSUME_YES=0; DEPS_ONLY=0; NO_DEPS=0
for arg in "$@"; do
  case "$arg" in
    --yes|-y) ASSUME_YES=1 ;;
    --deps-only) DEPS_ONLY=1 ;;
    --no-deps) NO_DEPS=1 ;;
    *) echo "unknown option: $arg  (use --yes, --deps-only, --no-deps)"; exit 2 ;;
  esac
done

echo "==== GIO agent -- install ===="
here="$(cd "$(dirname "$0")" && pwd)"

if [ "$(id -u)" != 0 ]; then echo "Run as root (sudo bash install_agent.sh)."; exit 1; fi

# ── an existing install: its config is the default of this run (= upgrade) ──────────────────
# Every KEY=VALUE the previous install (or the admin, by hand) left in /etc/gio-agent/config becomes
# the default of the matching prompt, so `install_agent.sh --yes` with no environment is a plain
# upgrade: token, stack paths, MUIP host/key, the MySQL root password and the Flask secret key
# (GIO_MYSQL_ROOT_PASSWORD, GIO_FLASK_SECRET_KEY), advertised/bind IP, listen address, server name
# and any extra key (GIO_TRUST_PROXY, ...) survive. Explicit, NON-EMPTY environment still wins (the
# launcher's install form, an admin pre-seeding values); an EMPTY environment value means "not given",
# never "clear it" -- clearing is done in the config file. Otherwise a plain re-run (or the
# launcher's form with the field left blank) would silently write GIO_ADVERTISED_IP= and the next re-render
# of the stack would advertise the LAN IP to players outside it.
CONFIG=/etc/gio-agent/config
UPGRADE=0
OLD_VERSION=""
SVC_WAS_ACTIVE=""; SVC_WAS_ENABLED=""
NEW_VERSION=$(sed -n 's/^AGENT_VERSION = "\([^"]*\)".*/\1/p' "$here/gio_agent.py" 2>/dev/null | head -1 || true)
declare -A OLDCFG=()
if [ -f "$CONFIG" ]; then
  UPGRADE=1
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line#"${line%%[![:space:]]*}"}"
    case "$line" in [A-Za-z_]*=*) ;; *) continue ;; esac
    k="${line%%=*}"; v="${line#*=}"
    k="${k%"${k##*[![:space:]]}"}"; v="${v#"${v%%[![:space:]]*}"}"   # "KEY = value" is fine for the agent's parser
    case "$k" in *[!A-Za-z0-9_]*) continue ;; esac
    # systemd's EnvironmentFile and the agent's own parser accept a trailing CR, trailing blanks and
    # a quoted value -- normalise the same way, or a hand-edited "0.0.0.0:18080" becomes a port of
    # 18080" further down (firewall skipped, probe dialling nonsense).
    v="${v%$'\r'}"; v="${v%"${v##*[![:space:]]}"}"
    case "$v" in \"*\") v="${v#\"}"; v="${v%\"}" ;; \'*\') v="${v#\'}"; v="${v%\'}" ;; esac
    OLDCFG["$k"]="$v"      # a repeated key: the LAST line wins, exactly as for systemd and the agent
  done < "$CONFIG"
  if [ "${#OLDCFG[@]}" -gt 0 ]; then
    for k in "${!OLDCFG[@]}"; do
      # only GIO_* becomes a default of this run; anything else in the EnvironmentFile (DOCKER_HOST,
      # HTTPS_PROXY -- the docker children inherit the file too) is carried into the rewrite verbatim
      # and never exported into this shell
      case "$k" in GIO_*) if [ -z "${!k:-}" ]; then export "$k=${OLDCFG[$k]}"; fi ;; esac
    done
  fi
  # The service's enabled/active state is part of what an upgrade must not change (an admin who
  # stopped or disabled the agent on purpose keeps it that way; a crashed one is restarted).
  SVC_WAS_ACTIVE=$(systemctl is-active gio-agent 2>/dev/null || true)
  SVC_WAS_ENABLED=$(systemctl is-enabled gio-agent 2>/dev/null || true)
  OLD_VERSION=$(sed -n 's/^AGENT_VERSION = "\([^"]*\)".*/\1/p' /opt/gio-agent/gio_agent.py 2>/dev/null | head -1 || true)
  echo "  existing install: agent ${OLD_VERSION:-?} -> ${NEW_VERSION:-?}; the values in $CONFIG are this run's defaults"
  if [ -n "$OLD_VERSION" ] && [ -n "$NEW_VERSION" ] && [ "$OLD_VERSION" != "$NEW_VERSION" ] \
     && [ "$(printf '%s\n%s\n' "$OLD_VERSION" "$NEW_VERSION" | sort -V | tail -1)" = "$OLD_VERSION" ]; then
    echo "  WARNING: this is a DOWNGRADE ($OLD_VERSION -> $NEW_VERSION) -- state.json may hold keys the older agent does not know"
  fi
fi

ask() {
  local prompt="$1" def="$2" ans
  if [ "$ASSUME_YES" = 1 ]; then echo "$def"; return; fi
  read -rp "$prompt [$def]: " ans; echo "${ans:-$def}"
}
# yes/no question; the default comes from the caller (env-seeded, so --yes can be told "no").
ask_yn() {
  local prompt="$1" def="$2" ans hint
  case "$def" in y|Y|yes|1|true) def=y; hint="Y/n" ;; *) def=n; hint="y/N" ;; esac
  if [ "$ASSUME_YES" = 1 ]; then ans="$def"; else read -rp "$prompt [$hint]: " ans; ans="${ans:-$def}"; fi
  case "$ans" in y|Y|yes|YES|Yes) return 0 ;; *) return 1 ;; esac
}
# A switch of the agent config, read by the agent's own rule: 1 / true / yes / on in any letter case,
# blanks around it ignored = on; anything else, empty included = off. Call it only as a CONDITION
# (if / while): as a plain statement an "off" would end the script (set -e).
is_on() {
  local v="$1"
  v="${v#"${v%%[![:space:]]*}"}"; v="${v%"${v##*[![:space:]]}"}"
  case "$v" in 1|[Tt][Rr][Uu][Ee]|[Yy][Ee][Ss]|[Oo][Nn]) return 0 ;; esac
  return 1
}
# `ask` for a value that "Keep .env settings" leaves alone: with KEEPENV=1 (set by that question, which
# comes before the first call) the prompt is not shown and its default -- the stored / pre-seeded value --
# is the answer. Handed back on stdout, like `ask`.
ask_unless_kept() {
  if [ "$KEEPENV" = 1 ]; then echo "$2"; else ask "$1" "$2"; fi
}
# What a secret given at install time may look like. secret_ok = the agent's SECRET_VALUE_RE;
# mysql_pw_ok = its MYSQL_ROOT_RE: the MySQL root password is rendered UNQUOTED into docker-compose.yml
# (MYSQL_ROOT_PASSWORD: <value>), and only a leading letter keeps YAML from reading it as a number or a
# date. LC_ALL=C: under a UTF-8 locale bash's [A-Za-z] also matches accented letters, and the agent
# (ASCII only) would drop such a value with a WARNING and use a random one. Subshell bodies, so there is
# nothing to restore. Call them only as a CONDITION (if / while): as a plain statement a "no" would end
# the script (set -e).
secret_ok() ( LC_ALL=C; [[ "$1" =~ ^[A-Za-z0-9_-]{8,128}$ ]] )
mysql_pw_ok() ( LC_ALL=C; [[ "$1" =~ ^[A-Za-z][A-Za-z0-9_-]{7,127}$ ]] )
# The MUIP sign key's prompt contract for the other install-time secrets: ask_secret LABEL KEY STORED
# CHECK RULE. STORED = the stored / pre-seeded value: Enter keeps it, '-' clears it; it is never the
# prompt default (`ask` prints "[$def]") and never echoed -- only its fingerprint, and only in the
# interactive prompt. CHECK = one of the two functions above, RULE = the same rule in words; KEY is what
# a --yes run names when it has to stop. With "Keep .env settings" on (KEEPENV=1) nothing is asked and
# STORED is the answer -- still held to CHECK: an invalid one is asked for after all (there an empty
# answer = none, $SECRET_EMPTY), or stops a --yes run. The answer is left in SECRET_ANSWER: NEVER call
# this as X=$(ask_secret ...) -- its `exit 1` would only leave the subshell and its message would become
# the value. `if` statements, not `[ ... ] && x=...`: a function whose last command is a false && list
# returns 1, which ends the script under set -e.
ask_secret() {
  local label="$1" key="$2" stored="$3" check="$4" rule="$5" hint ans=""
  if [ "$KEEPENV" != 1 ]; then
    if [ -n "$stored" ]; then
      hint="Enter = keep the stored one, fingerprint $(printf %s "$stored" | sha256sum | cut -c1-8)"
    else
      hint="empty = random at the next Prepare server"
    fi
    ans=$(ask "$label ($hint, '-' = clear)" "")
  fi
  if [ -z "$ans" ]; then ans="$stored"; fi
  if [ "$ans" = "-" ]; then ans=""; fi
  while [ -n "$ans" ] && ! "$check" "$ans"; do
    if [ "$ASSUME_YES" = 1 ]; then
      echo "$key must be $rule -- fix it in $CONFIG (or the environment) and run this again."
      exit 1
    fi
    if [ "$KEEPENV" = 1 ] && [ "$ans" = "$stored" ]; then echo "  the stored $label is not valid:"; fi
    echo "  $rule"
    ans=$(ask "$label ($SECRET_EMPTY)" "")
  done
  SECRET_ANSWER="$ans"
}

# ── OS family / package manager ─────────────────────────────────────────────────────────────
# ID / ID_LIKE from os-release: "almalinux" + "rhel centos fedora", "ubuntu" + "debian", "debian",
# "rocky" + "rhel centos fedora", "fedora", "linuxmint" + "ubuntu debian" ...
OS_ID=""; OS_LIKE=""; OS_VER=""; OS_CODENAME=""; OS_NAME="Linux"
if [ -r /etc/os-release ]; then
  # shellcheck disable=SC1091
  . /etc/os-release
  OS_ID="${ID:-}"; OS_LIKE="${ID_LIKE:-}"; OS_VER="${VERSION_ID:-}"; OS_NAME="${PRETTY_NAME:-${NAME:-Linux}}"
  OS_CODENAME="${VERSION_CODENAME:-${UBUNTU_CODENAME:-}}"
fi
FAMILY=unknown; PKG=""
case " $OS_ID $OS_LIKE " in
  *" debian "*|*" ubuntu "*) FAMILY=debian; PKG=apt-get ;;
  *" rhel "*|*" centos "*|*" fedora "*) FAMILY=rhel; PKG=$(command -v dnf >/dev/null 2>&1 && echo dnf || echo yum) ;;
esac
echo "  system  : $OS_NAME (family=$FAMILY${PKG:+, $PKG})"

APT_UPDATED=0
pkg_install() {
  case "$FAMILY" in
    debian)
      # A fresh cloud box may still hold the dpkg lock (unattended-upgrades right after boot):
      # wait for it instead of failing on the first apt call. Lists first, once.
      if [ "$APT_UPDATED" = 0 ]; then
        DEBIAN_FRONTEND=noninteractive apt-get -o DPkg::Lock::Timeout=300 update -q || true
        APT_UPDATED=1
      fi
      DEBIAN_FRONTEND=noninteractive apt-get -o DPkg::Lock::Timeout=300 install -y -q "$@" ;;
    rhel) "$PKG" install -y "$@" ;;
    *) echo "  cannot install '$*': unknown distribution family (no apt-get/dnf)"; return 1 ;;
  esac
}
# apt: does the distro ship this package at all? (docker-compose-v2 exists on Ubuntu >= 22.04, not
# on Debian 12 -- there the official Docker repository is used instead). Needs fresh lists.
apt_has() {
  if [ "$APT_UPDATED" = 0 ]; then
    DEBIAN_FRONTEND=noninteractive apt-get -o DPkg::Lock::Timeout=300 update -q || true
    APT_UPDATED=1
  fi
  apt-cache show "$1" >/dev/null 2>&1
}

# ── python3 (the agent is stdlib-only, but needs 3.9+) ───────────────────────────────────────
PY_MIN="3.9"
py_ok() { "$1" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)' 2>/dev/null; }
py_ver() { "$1" -c 'import sys; print("%d.%d.%d" % sys.version_info[:3])' 2>/dev/null || echo "?"; }
# The distro's python3 when it is new enough (that is what the unit file should run), else the
# newest versioned interpreter already on the box.
find_python() {
  local c
  for c in python3 python3.14 python3.13 python3.12 python3.11 python3.10 python3.9; do
    if command -v "$c" >/dev/null 2>&1 && py_ok "$(command -v "$c")"; then command -v "$c"; return 0; fi
  done
  return 1
}
install_python() {
  case "$FAMILY" in
    debian) pkg_install python3 ;;
    rhel)
      # Alma/Rocky/RHEL 9+: python3 IS 3.9+ (dnf itself runs on it). 8.x ships 3.6 as python3 and
      # newer interpreters as versioned packages in AppStream -- take the newest that installs.
      "$PKG" install -y python3.12 2>/dev/null || "$PKG" install -y python3.11 2>/dev/null || "$PKG" install -y python3 ;;
    *) return 1 ;;
  esac
}
PY3="$(find_python || true)"
if [ -z "$PY3" ]; then
  if command -v python3 >/dev/null 2>&1; then
    echo "  python3 : $(command -v python3) is $(py_ver "$(command -v python3)") -- the agent needs $PY_MIN or newer"
  else
    echo "  python3 : not installed -- the agent needs python $PY_MIN or newer"
  fi
  if [ "$NO_DEPS" = 1 ] || ! ask_yn "Install python3 now ($FAMILY packages)?" "${GIO_INSTALL_PYTHON:-y}"; then
    echo "python3 $PY_MIN+ is required. Install it first:  dnf install python3  (Alma/RHEL 9+; python3.11 on 8.x)  or  apt install python3  (Ubuntu/Debian)"
    exit 1
  fi
  install_python || { echo "python3 could not be installed -- install it by hand and run this again."; exit 1; }
  PY3="$(find_python || true)"
  [ -n "$PY3" ] || { echo "python3 is still older than $PY_MIN after the install ($(py_ver "$(command -v python3 || echo false)")). Install a newer interpreter by hand and run this again."; exit 1; }
fi
echo "  python  : $PY3 ($(py_ver "$PY3"))"

# ── docker + compose v2 ───────────────────────────────────────────────────────────────────────
docker_ok() { command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; }
install_docker() {
  case "$FAMILY" in
    debian)
      if apt_has docker-compose-v2; then
        # Ubuntu >= 22.04: the distro's own engine + compose v2 (no third-party repository).
        pkg_install docker.io docker-compose-v2
      else
        # Debian (and any apt distro without docker-compose-v2): Docker's official repository,
        # exactly the documented recipe (keyring + sources entry for this codename).
        local repo="debian" codename="$OS_CODENAME"
        case " $OS_ID $OS_LIKE " in *" ubuntu "*) repo="ubuntu"; codename="${UBUNTU_CODENAME:-$OS_CODENAME}" ;; esac
        [ -n "$codename" ] || { echo "  cannot tell the distribution codename (VERSION_CODENAME) -- add Docker's apt repository by hand"; return 1; }
        pkg_install ca-certificates curl
        install -m 0755 -d /etc/apt/keyrings
        curl -fsSL "https://download.docker.com/linux/$repo/gpg" -o /etc/apt/keyrings/docker.asc
        chmod a+r /etc/apt/keyrings/docker.asc
        echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/$repo $codename stable" \
          > /etc/apt/sources.list.d/docker.list
        APT_UPDATED=0
        pkg_install docker-ce docker-ce-cli containerd.io docker-compose-plugin
      fi ;;
    rhel)
      # No docker in the base repositories of Alma/Rocky/RHEL (they ship podman) -- Docker's own
      # repository: centos/ serves Alma/Rocky/CentOS Stream, rhel/ and fedora/ their own.
      local repo="centos"
      case "$OS_ID" in rhel) repo="rhel" ;; fedora) repo="fedora" ;; esac
      if command -v docker >/dev/null 2>&1 && docker --version 2>/dev/null | grep -qi podman; then
        echo "  'docker' here is podman's shim (podman-docker) -- remove it ($PKG remove podman-docker) or install docker-ce by hand"
        return 1
      fi
      command -v curl >/dev/null 2>&1 || pkg_install curl
      curl -fsSL "https://download.docker.com/linux/$repo/docker-ce.repo" -o /etc/yum.repos.d/docker-ce.repo
      "$PKG" install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin ;;
    *) echo "  unknown distribution family -- install docker + the compose v2 plugin by hand"; return 1 ;;
  esac
}
if ! docker_ok; then
  if command -v docker >/dev/null 2>&1; then
    echo "  docker  : $(docker --version 2>/dev/null || echo present) but 'docker compose' (v2 plugin) does not answer"
  else
    echo "  docker  : not installed"
  fi
  if [ "$NO_DEPS" = 1 ] || ! ask_yn "Install docker + the compose v2 plugin now?" "${GIO_INSTALL_DOCKER:-y}"; then
    echo "  warning: 'docker compose' does not answer -- the agent starts, but stack operations will fail until docker + the compose v2 plugin are installed."
  else
    install_docker || { echo "docker could not be installed -- install docker + the compose v2 plugin by hand and run this again."; exit 1; }
    # The daemon: enabled for every boot. Fails only where there is no systemd (a container) --
    # then the packages are in place and the box's own init has to start dockerd.
    if command -v systemctl >/dev/null 2>&1 && systemctl enable --now docker >/dev/null 2>&1; then
      echo "  docker  : installed and running ($(docker --version 2>/dev/null | sed 's/,.*//'); $(docker compose version 2>/dev/null | sed 's/^Docker //'))"
    else
      echo "  docker  : installed ($(docker --version 2>/dev/null | sed 's/,.*//')) -- could not enable the service via systemctl, start dockerd by hand"
    fi
    docker_ok || echo "  warning: docker is installed but 'docker compose' still does not answer -- stack operations will fail until it does"
  fi
else
  echo "  docker  : $(docker --version 2>/dev/null | sed 's/,.*//'); $(docker compose version 2>/dev/null | sed 's/^Docker //')"
fi

# ── 7z extractor (the archive.org bundles of the hotpatch mirror + the server package download) ──
# Optional, so NON-FATAL: without it the agent fills the hotpatch mirror file by file from the official
# CDN and refuses a server package download (409 "No 7z extractor") until one is installed. Ubuntu 24.04+
# and Debian 12 ship `7zip` (7zz); older Ubuntu has p7zip-full; libarchive-tools = bsdtar. Alma/Rocky 9:
# 7zip lives in EPEL (added only as the second choice); bsdtar is in AppStream (no extra repository).
extractor_ok() {
  local t
  for t in 7zz 7z 7za 7zr bsdtar; do
    if command -v "$t" >/dev/null 2>&1; then echo "  7z      : $(command -v "$t")"; return 0; fi
  done
  return 1
}
install_extractor() {
  case "$FAMILY" in
    debian) pkg_install 7zip || pkg_install p7zip-full || pkg_install libarchive-tools ;;
    rhel)
      "$PKG" install -y 7zip 2>/dev/null \
        || "$PKG" install -y bsdtar 2>/dev/null \
        || { "$PKG" install -y epel-release && "$PKG" install -y p7zip p7zip-plugins; } ;;
    *) echo "  unknown distribution family -- install 7zz/7z/p7zip or bsdtar by hand"; return 1 ;;
  esac
}
# Under --deps-only the stack prompts never run: a launcher-driven or scripted run says through
# GIO_FETCH_16/28 whether a server package download is planned, which turns the warning into a
# louder one (the download cannot work without the tool).
FETCH_PLANNED=n
if [ "${GIO_FETCH_16:-n}" = y ] || [ "${GIO_FETCH_28:-n}" = y ]; then FETCH_PLANNED=y; fi
warn_no_extractor() {
  if [ "$FETCH_PLANNED" = y ]; then
    echo "  WARNING : no 7z/bsdtar tool -- the server package download will be refused until one is installed (apt install 7zip | dnf install bsdtar), then run the download again"
  else
    echo "  warning : no 7z/bsdtar tool -- the agent fetches the hotpatch files one by one from the official CDN and cannot download a server package (apt install 7zip | dnf install bsdtar)"
  fi
}
if ! extractor_ok; then
  echo "  7z      : no 7zz/7z/7za/7zr/bsdtar on this box"
  if [ "$NO_DEPS" = 1 ] || ! ask_yn "Install a 7z extractor now? (unpacks the archive.org bundles of the hotpatch mirror and the ready-made server packages)" "${GIO_INSTALL_7Z:-y}"; then
    warn_no_extractor
  elif install_extractor && extractor_ok; then
    :
  else
    echo "  7z      : the install did not produce a usable tool"
    warn_no_extractor
  fi
fi
if [ "$DEPS_ONLY" = 1 ]; then echo "Dependencies done (--deps-only)."; exit 0; fi

# The stacks may live under /home, /root, /opt, /srv -- or, for a non-root admin, in their own home
# (/home/admin/1.6_live). $SUDO_USER's home is tried before the glob so a multi-user box is deterministic.
# Both of those stay quoted: an empty ${SUDO_USER:+...} and an unmatched glob become words that fail [ -d ].
detect_dir() {
  local d
  for d in "/home/$1" "/root/$1" "/opt/$1" "/srv/$1" "${SUDO_USER:+/home/$SUDO_USER/$1}" /home/*/"$1"; do
    if [ -d "$d" ]; then echo "$d"; return; fi
  done
  echo "/home/$1"
}

# An EMPTY stored path means "this version is not on this box" (the agent reports it unconfigured);
# on an upgrade it stays empty instead of turning into the detected default.
DEF16=$(detect_dir 1.6_live); DEF28=$(detect_dir 2.8_live)
if [ "$UPGRADE" = 1 ]; then DEF16="${OLDCFG[GIO_DIR_16]-$DEF16}"; DEF28="${OLDCFG[GIO_DIR_28]-$DEF28}"; fi
# ${VAR-default}, NOT ${VAR:-default}: the launcher exports every GIO_* key, empty ones included, so a
# field the admin deliberately left blank arrives SET AND EMPTY. With `:-` that would become the detected
# default -- a path that does not exist -- and the config would then claim the version is installed.
# Per version: "already extracted?" yes = the path prompt; no = the folder the
# ready-made package from the Internet Archive is to be installed into, and the version is MARKED for
# the download that runs after the service is up (FETCH16/FETCH28 -- installer-only, never written to
# the config; under --yes the answer comes from GIO_FETCH_16/28, so the launcher's form decides).
# A marked folder that already holds a docker-compose.yml.tmpl needs no download: unmarked, with a note.
FETCH16=n; FETCH28=n
if ask_yn "Is the 1.6 server stack already extracted on this box?" "$( [ "${GIO_FETCH_16:-n}" = y ] && echo n || echo y )"; then
  DIR16=$(ask "Path of the 1.6 server (docker compose dir; empty = not on this box)" "${GIO_DIR_16-$DEF16}")
else
  DIR16=$(ask "Where should the 1.6 server be installed (docker compose dir; empty = skip this version)" "${GIO_DIR_16-$DEF16}")
  if [ -n "$DIR16" ]; then
    if [ -f "$DIR16/docker-compose.yml.tmpl" ]; then echo "  note    : $DIR16 already holds a server stack -- the download will be skipped"
    else FETCH16=y; fi
  fi
fi
if ask_yn "Is the 2.8 server stack already extracted on this box?" "$( [ "${GIO_FETCH_28:-n}" = y ] && echo n || echo y )"; then
  DIR28=$(ask "Path of the 2.8 server (docker compose dir; empty = not on this box)" "${GIO_DIR_28-$DEF28}")
else
  DIR28=$(ask "Where should the 2.8 server be installed (docker compose dir; empty = skip this version)" "${GIO_DIR_28-$DEF28}")
  if [ -n "$DIR28" ]; then
    if [ -f "$DIR28/docker-compose.yml.tmpl" ]; then echo "  note    : $DIR28 already holds a server stack -- the download will be skipped"
    else FETCH28=y; fi
  fi
fi
# A download was just chosen interactively and the dependency phase above had no tool (and no
# GIO_FETCH_* to know one was wanted): offer the extractor once more. Still non-fatal -- the agent
# refuses the download with a clear 409 until a tool exists, and the admin can install one later.
if { [ "$FETCH16" = y ] || [ "$FETCH28" = y ]; } && ! extractor_ok >/dev/null 2>&1; then
  FETCH_PLANNED=y
  if [ "$NO_DEPS" = 1 ] || ! ask_yn "A 7z extractor is needed to unpack the downloaded server package. Install one now?" "${GIO_INSTALL_7Z:-y}"; then
    warn_no_extractor
  elif install_extractor && extractor_ok; then
    :
  else
    warn_no_extractor
  fi
fi
# "Keep .env settings" (GIO_KEEP_ENV). While it is on, the agent leaves each server's own .env as it is:
# it replaces none of the vendor's published passwords (MySQL root, Flask key -- nor the MUIP sign key or
# the stack internal password) and does not rewrite OUTER_IP. So with a yes this script asks for no
# password and no address of a stack: the MUIP host and sign key, the MySQL root password, the Flask
# secret key, the advertised IP, the DDNS host and the bind IP are each the stored / pre-seeded value,
# else empty -- and still validated, so an invalid one is asked for after all, or stops a --yes run.
# Not asked is not the same as not applied: the advertised IP and the DDNS host are no .env settings, so
# the agent keeps handing a stored / pre-seeded one to the clients while the option is on. The question
# therefore promises only what holds for a stack's own settings: its passwords and OUTER_IP stay.
# The agent's own listen address and its token are asked in either case.
# The default is the stored / pre-seeded GIO_KEEP_ENV as the agent reads it (is_on): --yes takes it, and
# an upgrade keeps what the box has. The help is plain echo lines, and only in an interactive run:
# printed inside `ask` -- or inside any $(...) -- a line would become the value.
KEEPENV_DEF=n
if is_on "${GIO_KEEP_ENV:-}"; then KEEPENV_DEF=y; fi
if [ "$ASSUME_YES" != 1 ]; then
  echo "  Keep .env settings = the agent leaves each server's own .env as it is: it replaces none of the"
  echo "  vendor's published passwords (MySQL root, Flask key -- nor the MUIP sign key or the stack internal"
  echo "  password) and does not rewrite OUTER_IP. With it on, no password or address of a stack is asked for."
fi
KEEPENV=0; SECRET_EMPTY="empty = random"
if ask_yn "Keep the .env settings of the server stacks as they are (their passwords and OUTER_IP are not replaced)?" "$KEEPENV_DEF"; then
  KEEPENV=1; SECRET_EMPTY="empty = none"
fi
# Empty = the agent derives http://<OUTER_IP>:21051 per version from the stack's .env. Do NOT put
# 127.0.0.1: muipserver publishes its port on OUTER_IP only, so loopback does not answer.
MUIP=$(ask_unless_kept "MUIP host (empty = derived from the stack's OUTER_IP)" "${GIO_MUIP_HOST:-}")
REGION=$(ask "MUIP region"                          "${GIO_AGENT_REGION:-dev_docker}")
# The sign key muipserver accepts GM commands with. Empty = the agent writes a RANDOM key into a stack
# it prepares while that stack still carries the vendor's published key (creds.txt on the box keeps
# it). '-' clears a stored value. Only a fingerprint is ever echoed -- never the key.
# The stored key is NOT passed as the prompt default: `ask` prints "[$def]", which would put the
# secret itself in the terminal, in scrollback and in any screen-share or `script` capture. Enter
# keeps it (the sentinel below), '-' clears it, and only its fingerprint is ever shown.
# With "Keep .env settings" on the prompt is not shown and the stored key is the answer (the same
# sentinel): it stays in the config, and no stack's key is replaced by it or by a random one.
MUIPKEY_STORED="${GIO_MUIP_KEY:-}"
MUIPKEY=""
if [ "$KEEPENV" != 1 ]; then
  if [ -n "$MUIPKEY_STORED" ]; then
    MUIPKEY_HINT="Enter = keep the stored key, fingerprint $(printf %s "$MUIPKEY_STORED" | sha256sum | cut -c1-8)"
  else
    MUIPKEY_HINT="empty = random at the next Prepare server"
  fi
  MUIPKEY=$(ask "MUIP sign key ($MUIPKEY_HINT, '-' = clear)" "")
fi
[ -z "$MUIPKEY" ] && MUIPKEY="$MUIPKEY_STORED"
[ "$MUIPKEY" = "-" ] && MUIPKEY=""
# Tested through secret_ok (LC_ALL=C), never a bare [[ =~ ]]: under tr_TR.UTF-8 a bare [A-Za-z] refuses
# an ASCII i / I (a --yes run then stops on a key the launcher's form and the agent accept), and under
# most UTF-8 locales it lets through accented letters the agent drops.
while [ -n "$MUIPKEY" ] && ! secret_ok "$MUIPKEY"; do
  if [ "$ASSUME_YES" = 1 ]; then
    echo "GIO_MUIP_KEY must be 8-128 characters (letters, digits, '_' or '-') -- fix it in $CONFIG (or the environment) and run this again."
    exit 1
  fi
  if [ "$KEEPENV" = 1 ] && [ "$MUIPKEY" = "$MUIPKEY_STORED" ]; then echo "  the stored MUIP sign key is not valid:"; fi
  echo "  8-128 characters: letters, digits, '_' or '-'"
  MUIPKEY=$(ask "MUIP sign key ($SECRET_EMPTY)" "")
done
# The MySQL root password and the Flask secret key of a stack. The archives ship published
# values for both in .env; the agent replaces such a value when it prepares the stack -- with the one
# given here or, empty, with a RANDOM one per stack (creds.txt on the box keeps it, the launcher's Server
# secrets card shows it). A value that is already private is NEVER touched from here (rotate it from
# that card): on a prepared box these two matter only for a stack still on the vendor's default.
# The MUIP key's contract: Enter keeps a stored value, '-' clears it, the value itself is never echoed.
# ${VAR:-}: the launcher's form OMITS a blank key (unset = keep what the box has), so under `set -u` a
# bare $GIO_MYSQL_ROOT_PASSWORD would end every blank-form install right here.
ask_secret "MySQL root password" GIO_MYSQL_ROOT_PASSWORD "${GIO_MYSQL_ROOT_PASSWORD:-}" mysql_pw_ok "8-128 characters: letters, digits, '_' or '-', starting with a letter"
MYSQLPW="$SECRET_ANSWER"
ask_secret "Flask secret key" GIO_FLASK_SECRET_KEY "${GIO_FLASK_SECRET_KEY:-}" secret_ok "8-128 characters: letters, digits, '_' or '-'"
FLASKKEY="$SECRET_ANSWER"
# The IP the server HANDS to clients (dispatch/gateserver/gacha). Decoupled from .env OUTER_IP,
# which must be a local IP of the machine because docker binds to it. On a VPS with the public IP
# directly on the interface the two coincide.
# What belongs there, told to whoever is typing (the launcher's form says the same behind its "?").
# Plain echo lines, and only in an interactive run that shows the prompt: `ask` hands its answer back on
# stdout, so a line printed inside it -- or inside any $(...) -- would become the value.
if [ "$ASSUME_YES" != 1 ] && [ "$KEEPENV" != 1 ]; then
  echo "  The advertised IP is the PUBLIC (internet) IP that players outside your network connect to."
  echo "  Behind a router (this machine only has a private address such as 192.168.x.x): put the router's"
  echo "  public IP here and forward TCP 21000, UDP 21081 and the agent's TCP port (18080 unless you change"
  echo "  it below) to this machine -- players on your own network then need NAT loopback on the router."
  echo "  Empty ('-' clears a stored one) = the bind IP is advertised: right for a server whose own address"
  echo "  is public (a VPS), LAN-only behind a router. An IP that changes? Use the DDNS host name prompt"
  echo "  below instead."
fi
ADV=$(ask_unless_kept "Public IP advertised to clients (empty = the one in .env, '-' = clear)" "${GIO_ADVERTISED_IP:-}")
[ "$ADV" = "-" ] && ADV=""
# The agent takes this value verbatim as the rewrite target of dispatch/gateserver/the URL columns: a typo
# would be handed to every player. Asked again interactively; under
# --yes there is nobody to ask, so the run stops before anything is replaced.
while [ -n "$ADV" ] && ! "$PY3" -c 'import ipaddress,sys; ipaddress.ip_address(sys.argv[1])' "$ADV" 2>/dev/null; do
  if [ "$ASSUME_YES" = 1 ]; then
    echo "GIO_ADVERTISED_IP='$ADV' is not an IP address -- fix it in $CONFIG (or pass GIO_ADVERTISED_IP=<ip>) and run this again."
    exit 1
  fi
  if [ "$KEEPENV" = 1 ]; then echo "  '$ADV' is not an IP address (a DNS name belongs in GIO_ADVERTISED_HOST)"
  else echo "  '$ADV' is not an IP address (a DNS name goes in the DDNS host prompt below)"; fi
  ADV=$(ask "Public IP advertised to clients (empty = the one in .env)" "")
  [ "$ADV" = "-" ] && ADV=""
done
# A dynamic WAN IP: a DNS name kept current by a DDNS updater (router or box). The agent resolves it at
# start and every 2 minutes and re-applies a changed address to the running stack by itself; it wins
# over the IP above when both are set. '-' clears a stored value (the prompt default on an upgrade).
ADVHOST=$(ask_unless_kept "DDNS host name to follow instead of a fixed IP (empty = none, '-' = clear)" "${GIO_ADVERTISED_HOST:-}")
[ "$ADVHOST" = "-" ] && ADVHOST=""
if [ -n "$ADVHOST" ] && [ -n "$ADV" ]; then
  # On an upgrade, one of them new in this run (typed, or sent by the launcher's form) and the other merely
  # kept from the stored config: the new one is what the admin means. Otherwise keep both -- the agent
  # follows the host (and logs a warning at start).
  OLDADV=""; OLDHOST=""
  if [ "$UPGRADE" = 1 ]; then OLDADV="${OLDCFG[GIO_ADVERTISED_IP]:-}"; OLDHOST="${OLDCFG[GIO_ADVERTISED_HOST]:-}"; fi
  if [ "$UPGRADE" = 1 ] && [ "$ADV" != "$OLDADV" ] && [ "$ADVHOST" = "$OLDHOST" ]; then
    echo "  note    : the advertised IP given now replaces the stored DDNS host ($ADVHOST)"; ADVHOST=""
  elif [ "$UPGRADE" = 1 ] && [ "$ADVHOST" != "$OLDHOST" ] && [ "$ADV" = "$OLDADV" ]; then
    echo "  note    : the DDNS host given now replaces the stored advertised IP ($ADV)"; ADV=""
  else
    echo "  WARNING : both an advertised IP and a DDNS host are set -- the agent follows the host and ignores the IP"
  fi
fi
# The LOCAL IP docker binds to (OUTER_IP in .env). The vendor archive ships 127.0.0.1 there, and a
# re-extraction over an installed stack would bind every port to loopback (the client sees "server
# busy" 502) -- the agent repairs .env with this value before any bootstrap.
# Default: the source IP of the default route -- NOT `hostname -I | awk '{print $1}'`, which on a
# docker box may list a bridge gateway (172.17.0.1 / 172.10.3.x) or a VPN IP first.
# `|| true`: under `set -euo pipefail`, an `ip route` without a default route would kill the install here.
DEFBIND=$(ip -4 route get 203.0.113.1 2>/dev/null | sed -n 's/.*src \([0-9.]*\).*/\1/p' | head -1 || true)
# On an upgrade the stored value is the default EVEN WHEN EMPTY: empty means "the agent auto-detects"
# (the NAT/DHCP setting), and pinning today's default-route IP into it would make the agent refuse
# every start once the box's address changes. Non-colon form: a config that does not CARRY the key
# (one this script did not write) was never configured either way, so it keeps the detected default
# exactly as a fresh install does -- taken for "deliberately blank" it would leave OUTER_IP=127.0.0.1
# in the stack, which comes up green and only fails at the client as a 502.
# One stored EMPTY value is not a setting either: the one of a config that has "Keep .env settings" ON
# (OLDKEEP). A run with the option on shows no bind IP prompt and writes no detected address, so on a
# box that had no bind IP it stores blank whatever the admin would have chosen. Taken as the default, a
# run that switches the option off would write blank again and the stack would stay on the vendor's
# loopback OUTER_IP -- so there the detected address stays the default, as on a fresh box. A stored
# NON-EMPTY value is the default whatever the option was, and so is an empty one stored with it off.
OLDKEEP=n
if [ "$UPGRADE" = 1 ] && is_on "${OLDCFG[GIO_KEEP_ENV]:-}"; then OLDKEEP=y; fi
if [ "$UPGRADE" = 1 ] && { [ "$OLDKEEP" != y ] || [ -n "${OLDCFG[GIO_BIND_IP]:-}" ]; }; then DEFBIND="${OLDCFG[GIO_BIND_IP]-$DEFBIND}"; fi
# ${VAR:-...} here, UNLIKE the two stack dirs: an empty bind IP is NOT "auto-detect" on a fresh box.
# With no bind IP and no advertised IP the agent's ensure_bind_ip deliberately leaves .env alone, and the
# vendor archive ships OUTER_IP=127.0.0.1 -- every published port would bind loopback and the client gets
# 502 "server busy". The upgrade case is already safe without the non-colon form: DEFBIND was pinned to
# the STORED value just above, so a deliberately blank setting stays blank there.
# With "Keep .env settings" on OUTER_IP stays the stack's own: nothing is asked and no detected address
# is written -- the bind IP is the stored / pre-seeded value, else empty.
if [ "$KEEPENV" = 1 ]; then DEFBIND=""; fi
BINDIP=$(ask_unless_kept "Local bind IP for docker (OUTER_IP in .env; empty = auto-detected by the agent)" "${GIO_BIND_IP:-$DEFBIND}")
# 0.0.0.0 = direct access from the app (normal mode); 127.0.0.1 = only through an SSH tunnel (advanced)
BIND=$(ask  "Agent bind address"                    "${GIO_AGENT_LISTEN:-0.0.0.0:18080}")
# The name players see in the launcher (/public/status). Quotes are stripped: systemd's EnvironmentFile
# parser treats ' and " as quoting that spans newlines, so one apostrophe would swallow every assignment
# written after this one. (The launcher strips them too -- this covers an interactive run.)
SRVNAME=$(ask "Server name shown to players"        "${GIO_SERVER_NAME:-$(hostname)}")
SRVNAME=${SRVNAME//[\"\']/}
# Where the hotpatch mirror is filled FROM the first time this server fills it: the ready-made
# archive.org bundles (default -- one pinned download) or the official CDN, file by file. This is
# the SEED of the agent's policy field `hotpatchSource`; once the admin picks a source in the
# launcher's hotpatch card, that stored choice wins and this value is no longer consulted.
HPSRC="${GIO_HOTPATCH_SOURCE:-archive}"
while :; do
  HPSRC=$(ask "Hotpatch mirror source (archive|cdn)" "$HPSRC")
  HPSRC=$(printf %s "$HPSRC" | tr "[:upper:]" "[:lower:]")
  case "$HPSRC" in archive|cdn) break ;; esac
  if [ "$ASSUME_YES" = 1 ]; then
    echo "GIO_HOTPATCH_SOURCE must be 'archive' or 'cdn' -- fix it in $CONFIG (or the environment) and run this again."
    exit 1
  fi
  echo "  type 'archive' (archive.org bundles) or 'cdn' (the official CDN, file by file)"
done
# On an upgrade the existing token is the default (loaded from the config above): the launchers that
# administer this server hold it -- entered on their start screen, or carried by an admin build -- and a
# repeated run must not silently disconnect them. A DIFFERENT token (typed here, or sent by the
# launcher's install form, which generates one when its Token field is left blank) is accepted but
# called out in the summary: every launcher that uses the old one has to be given the new one.
TOKEN="${GIO_AGENT_TOKEN:-}"
if [ "$ASSUME_YES" != 1 ]; then read -rp "Bearer token [${TOKEN:-generate}]: " TOKEN_IN; TOKEN="${TOKEN_IN:-$TOKEN}"; fi
if [ -z "${TOKEN:-}" ]; then TOKEN=$(head -c24 /dev/urandom | od -An -tx1 | tr -d ' \n' || true); echo "  generated token: $TOKEN"; fi
TOKEN_NOTE=""
if [ "$UPGRADE" = 1 ] && [ -n "${OLDCFG[GIO_AGENT_TOKEN]:-}" ]; then
  if [ "$TOKEN" = "${OLDCFG[GIO_AGENT_TOKEN]}" ]; then TOKEN_NOTE="kept (launchers that already have it keep working)"
  else TOKEN_NOTE="CHANGES -- every launcher that uses the old token must be given the new one"; fi
fi

echo
echo "Summary:"
if [ "$UPGRADE" = 1 ]; then echo "  mode    : UPGRADE (agent ${OLD_VERSION:-?} -> ${NEW_VERSION:-?})"; else echo "  mode    : fresh install (agent ${NEW_VERSION:-?})"; fi
echo "  1.6 dir : $DIR16$( [ "$FETCH16" = y ] && echo " (to be downloaded from archive.org)")"
echo "  2.8 dir : $DIR28$( [ "$FETCH28" = y ] && echo " (to be downloaded from archive.org)")"
# Keep .env settings: none of the MUIP key / MySQL pw / Flask / bind IP values is applied to a stack, so
# their lines promise neither a random value nor a detected address. A value the config stores is only
# said to stay there -- without a fingerprint, since nothing runs on it.
[ "$KEEPENV" = 1 ] && echo "  .env    : kept as it is -- the agent replaces none of the vendor's published passwords and does not rewrite OUTER_IP (GIO_KEEP_ENV=1)"
# A package the agent downloads arrives with the vendor's own .env -- OUTER_IP=127.0.0.1 and the
# published passwords -- and with the option on nothing replaces either. Said once, for a run that has
# both the option on and a version marked for download, and here: above "Continue?", while the admin
# can still answer "n".
if [ "$KEEPENV" = 1 ] && { [ "$FETCH16" = y ] || [ "$FETCH28" = y ]; }; then
  echo "  note    : the downloaded package comes with the vendor's .env (ports on 127.0.0.1, the published passwords) -- it is kept as it is; edit that .env before Prepare server to let other machines connect"
fi
echo "  MUIP    : $MUIP  (region=$REGION)"
if [ "$KEEPENV" = 1 ]; then
  echo "  MUIP key: the stack's own key is kept$([ -n "$MUIPKEY" ] && echo " (the one in the config is not applied)")"
  echo "  MySQL pw: the stack's own password is kept$([ -n "$MYSQLPW" ] && echo " (the one in the config is not applied)")"
  echo "  Flask   : the stack's own key is kept$([ -n "$FLASKKEY" ] && echo " (the one in the config is not applied)")"
else
  echo "  MUIP key: $([ -n "$MUIPKEY" ] && echo "chosen (fingerprint $(printf %s "$MUIPKEY" | sha256sum | cut -c1-8))" || echo "random at the next Prepare server")"
  # No fingerprint for these two: this summary reaches the launcher's install log, and an unsalted hash of
  # a password a human chose is something to test guesses against.
  echo "  MySQL pw: $([ -n "$MYSQLPW" ] && echo "set in the config -- used when a stack still on the vendor's default is prepared" || echo "random at the next Prepare server")"
  echo "  Flask   : $([ -n "$FLASKKEY" ] && echo "set in the config -- used when a stack still on the vendor's default is prepared" || echo "random at the next Prepare server")"
fi
echo "  adv. IP : ${ADV:-<the one in .env>}"
[ -n "$ADVHOST" ] && echo "  DDNS    : $ADVHOST (followed by the agent)"
if [ "$KEEPENV" = 1 ]; then echo "  bind IP : the stack's own OUTER_IP is kept${BINDIP:+ ($BINDIP in the config is not applied)}"
else echo "  bind IP : ${BINDIP:-<auto-detected by the agent>}"; fi
echo "  listen  : $BIND"
echo "  name    : $SRVNAME"
echo "  hotpatch: mirror filled from $([ "$HPSRC" = cdn ] && echo "the official CDN, file by file" || echo "the archive.org bundles")"
echo "  python  : $PY3 ($(py_ver "$PY3"))"
[ -n "$TOKEN_NOTE" ] && echo "  token   : $TOKEN_NOTE"
if [ "$ASSUME_YES" != 1 ]; then
  read -rp "Continue? [y/N]: " ok; [ "${ok:-N}" = "y" ] || { echo "cancelled"; exit 1; }
fi

# The selftest runs on the UPLOADED copy, before the existing install is touched: a broken new
# agent must not leave the old service running over half-replaced files. The payloads validated
# are the uploaded ones too, not the old ones under /opt.
# The agent warns at import about the value it is HANDED -- an invalid or vendor-default secret, an
# unknown hotpatch source -- and this shell exports only what it started with: the stored config (the
# loader at the top) or the caller's environment, i.e. after an interactive answer the value from before
# it, or none. The warning would then name a value this run does not write and say nothing about the one
# just typed, so these values go over as they are about to be written -- GIO_KEEP_ENV with them, as the
# 1 or 0 the config gets (nothing further down takes a default from any of them).
export GIO_MUIP_KEY="$MUIPKEY" GIO_MYSQL_ROOT_PASSWORD="$MYSQLPW" GIO_FLASK_SECRET_KEY="$FLASKKEY" GIO_HOTPATCH_SOURCE="$HPSRC" GIO_KEEP_ENV="$KEEPENV"
GIO_PAYLOAD_DIR="$here/payloads" "$PY3" "$here/gio_agent.py" --selftest

# Where an agent with the given GIO_AGENT_LISTEN answers from this box: the literal host when it
# is one (a bind to a specific address does not answer on loopback), 127.0.0.1 for the wildcards
# and for a bare port; IPv6 literals get their brackets for the URL. Sets PROBE_HOST / PROBE_PORT.
listen_host_port() {
  local l="$1" h p
  case "$l" in
    ::|'[::]') h=""; p="18080" ;;
    \[*\]:*) h="${l%\]:*}"; h="${h#\[}"; p="${l##*:}" ;;
    *:*)     h="${l%:*}"; p="${l##*:}" ;;
    '')      h=""; p="18080" ;;
    *)       if [ "$l" -eq "$l" ] 2>/dev/null; then h=""; p="$l"; else h="$l"; p="18080"; fi ;;
  esac
  [ "$p" -eq "$p" ] 2>/dev/null || p="18080"
  case "$h" in ''|0.0.0.0|::|'[::]') h="127.0.0.1" ;; *:*) h="[$h]" ;; esac
  PROBE_HOST="$h"; PROBE_PORT="$p"
}
# An upgrade restarts the service, and a restart under a running job (a provision, a secrets
# rotation, a staged start) leaves a stack cut in half. Ask the OLD agent, with the OLD token, on the
# address it is bound to. Answers: none (nothing listens there), idle, busy KIND VERSION, or
# "unknown: WHY" -- and unknown is NOT idle: a probe that timed out or got a 401 must stop the
# upgrade, not wave it through. /health and /jobs never touch docker (fast even mid-bootstrap);
# /status does (it is what knows about public signup jobs), so it gets a long timeout.
agent_probe() {
  GIO_OLD_TOKEN="$2" "$PY3" - "$1" <<'PYEOF' 2>/dev/null || echo "unknown: probe crashed"
import json, os, sys, urllib.error, urllib.request
base = "http://%s" % sys.argv[1]
token = os.environ.get("GIO_OLD_TOKEN", "")
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))   # never through an http_proxy of this shell
def get(path, timeout, auth):
    req = urllib.request.Request(base + path, headers={"Authorization": "Bearer " + token} if auth else {})
    return json.load(opener.open(req, timeout=timeout))
try:
    get("/health", 5, False)
except urllib.error.URLError as e:
    reason = getattr(e, "reason", e)
    if isinstance(reason, ConnectionRefusedError) or getattr(reason, "errno", None) in (111, 10061):
        print("none"); sys.exit(0)
    print("unknown: /health: %s" % reason); sys.exit(0)
except Exception as e:
    print("unknown: /health: %s" % e); sys.exit(0)
try:
    try:
        running = [j for j in (get("/jobs", 15, True).get("jobs") or []) if j.get("state") == "running"]
    except urllib.error.HTTPError as e:
        if e.code != 404: raise
        running = []          # an agent older than /jobs: /status alone decides
    if running:
        print(("busy %s %s" % (running[0].get("kind", "?"), running[0].get("version", ""))).strip()); sys.exit(0)
    b = get("/status", 60, True).get("busy")
    if isinstance(b, dict):
        print(("busy %s %s" % (b.get("kind", "?"), b.get("version", ""))).strip()); sys.exit(0)
    print("idle")
except urllib.error.HTTPError as e:
    print("unknown: HTTP %s from the agent (token changed by hand?)" % e.code)
except Exception as e:
    print("unknown: %s" % e)
PYEOF
}
PROBE_HOST=""; PROBE_PORT=""
if [ "$UPGRADE" = 1 ]; then
  listen_host_port "${OLDCFG[GIO_AGENT_LISTEN]:-}"
  PROBE=$(agent_probe "$PROBE_HOST:$PROBE_PORT" "${OLDCFG[GIO_AGENT_TOKEN]:-}")
  case "$PROBE" in
    none) echo "  agent   : not running (nothing answers on $PROBE_HOST:$PROBE_PORT) -- no job to wait for" ;;
    idle) echo "  agent   : running, no job in progress" ;;
    busy*) echo "The running agent has a job in progress (${PROBE#busy }). Wait for it to finish (the launcher shows it) and run this again."; exit 1 ;;
    *) if [ "${GIO_UPGRADE_FORCE:-}" = y ]; then
         echo "  WARNING : could not tell whether a job is running ($PROBE) -- continuing because GIO_UPGRADE_FORCE=y"
       else
         echo "Could not tell whether the running agent has a job in progress ($PROBE)."
         echo "Fix that first (is the agent healthy? is the token in $CONFIG the one it runs with?) or run again with GIO_UPGRADE_FORCE=y to upgrade regardless."
         exit 1
       fi ;;
  esac
fi

# Upgrade: keep ONE previous generation (agent, config, state, payloads) for a rollback --
# docs/AGENT-UPGRADE.md has the commands. mode 0700: the config copy carries the token.
if [ "$UPGRADE" = 1 ]; then
  PREV=/var/lib/gio-agent/previous
  rm -rf "$PREV"; install -d -m 0700 "$PREV"
  KEPT=""
  if [ -f /opt/gio-agent/gio_agent.py ]; then cp -a /opt/gio-agent/gio_agent.py "$PREV/gio_agent.py"; KEPT="$KEPT agent(${OLD_VERSION:-?})"; fi
  cp -a "$CONFIG" "$PREV/config"; KEPT="$KEPT config"
  OLD_STATE="${OLDCFG[GIO_STATE_PATH]:-/var/lib/gio-agent/state.json}"
  if [ -f "$OLD_STATE" ]; then cp -a "$OLD_STATE" "$PREV/state.json"; KEPT="$KEPT state"; fi
  if [ -f /etc/systemd/system/gio-agent.service ]; then cp -a /etc/systemd/system/gio-agent.service "$PREV/gio-agent.service"; KEPT="$KEPT unit"; fi
  # A payloads folder that is a symbolic link is kept as the files it leads to: a copy of the link would
  # be the live folder itself, which this run then takes entries out of (carry_box_payloads).
  if [ -L /opt/gio-agent/payloads ] && [ -d /opt/gio-agent/payloads ]; then
    mkdir "$PREV/payloads"; cp -a /opt/gio-agent/payloads/. "$PREV/payloads/"; KEPT="$KEPT payloads"
  elif [ -d /opt/gio-agent/payloads ]; then cp -a /opt/gio-agent/payloads "$PREV/payloads"; KEPT="$KEPT payloads"; fi
  # A version folder in it that is a symbolic link is kept the same way.
  if [ -d "$PREV/payloads" ]; then
    while IFS= read -r -d '' PLINK; do
      [ -d "$PLINK" ] || continue
      rm -f "$PREV/payloads/${PLINK##*/}"; mkdir "$PREV/payloads/${PLINK##*/}"
      cp -a "$PLINK/." "$PREV/payloads/${PLINK##*/}/"
    done < <(find /opt/gio-agent/payloads/ -mindepth 1 -maxdepth 1 -type l -print0)
  fi
  echo "  previous :${KEPT} kept in $PREV"
fi

install -d /opt/gio-agent /etc/gio-agent /var/lib/gio-agent
install -m 0644 "$here/gio_agent.py" /opt/gio-agent/gio_agent.py
if [ -f "$here/uninstall_agent.sh" ]; then
  install -m 0755 "$here/uninstall_agent.sh" /opt/gio-agent/uninstall_agent.sh
  echo "  uninstall: bash /opt/gio-agent/uninstall_agent.sh"
fi
# What a box added to its payloads folder moves into the folder that replaces it: per version -- unless
# the new payloads ship a navmesh bundle for that version -- the navmesh folder, a folder an adoption
# renamed aside, the archives and the refusal record of an adoption; and the archives of the payloads
# folder itself unless the new payloads ship a bundle for any version. These are entries the agent's
# build identity leaves out (identity_skips in gio_agent.py). $1 = the old folder, $2 = the new one;
# prints how many entries moved. An entry that will not move stays behind (one warning on stderr). The
# old folder, or a version folder in it, may be a symbolic link (payloads kept on another disk): the
# walks start INSIDE it (the trailing slash; -L for the list of version folders), or find would look at
# the link itself and see nothing to keep.
carry_box_payloads() {
  local old="$1" new="$2" moved=0 bundles d v e n
  bundles=$(find "$new" -mindepth 2 -maxdepth 2 -name navmesh | wc -l)
  if [ "$bundles" -eq 0 ]; then
    while IFS= read -r -d '' e; do
      n="${e##*/}"
      case "${n,,}" in
        *.zip)
          if [ ! -e "$new/$n" ]; then
            if mv "$e" "$new/$n" 2>/dev/null; then moved=$((moved + 1)); else echo "  warning: payloads/$n could not be kept" >&2; fi
          fi ;;
      esac
    done < <(find "$old/" -mindepth 1 -maxdepth 1 -type f -print0)
  fi
  while IFS= read -r -d '' d; do
    v="${d##*/}"
    if [ -d "$new/$v" ] && [ ! -e "$new/$v/navmesh" ]; then
      while IFS= read -r -d '' e; do
        n="${e##*/}"
        case "${n,,}" in
          navmesh*|*.zip|.relic-navmesh-zip-refused)
            if [ ! -e "$new/$v/$n" ]; then
              if mv "$e" "$new/$v/$n" 2>/dev/null; then moved=$((moved + 1)); else echo "  warning: payloads/$v/$n could not be kept" >&2; fi
            fi ;;
        esac
      done < <(find "$d/" -mindepth 1 -maxdepth 1 -print0)
    fi
  done < <(find -L "$old/" -mindepth 1 -maxdepth 1 -type d -print0)
  echo "$moved"
}
if [ -d "$here/payloads" ]; then
  # The new payloads are put together beside the old folder -- with what this box added to the old one
  # (carry_box_payloads) -- and take its place in one rename.
  NEWPAY=/opt/gio-agent/.payloads-new
  rm -rf "$NEWPAY"
  cp -r "$here/payloads" "$NEWPAY"
  CARRIED=0
  if [ -d /opt/gio-agent/payloads ]; then CARRIED=$(carry_box_payloads /opt/gio-agent/payloads "$NEWPAY"); fi
  rm -rf /opt/gio-agent/payloads
  mv "$NEWPAY" /opt/gio-agent/payloads
  echo "  payloads: $(find /opt/gio-agent/payloads -type f | wc -l) files$(if [ "${CARRIED:-0}" -gt 0 ]; then echo " ($CARRIED kept from this box: navmesh bundle / archives)"; fi)"
else
  echo "  warning: payloads/ is missing -- the agent will not be able to apply the GAA progress"
fi

# Created 0600 BEFORE its first byte: on a fresh box the redirection below would create it 0644 (umask
# 022) and leave it so until the chmod further down -- with the token and the stack secrets already in
# it. An existing file is rewritten in place and keeps its mode.
[ -e /etc/gio-agent/config ] || install -m 600 /dev/null /etc/gio-agent/config
cat > /etc/gio-agent/config <<EOF
GIO_AGENT_TOKEN=$TOKEN
GIO_AGENT_LISTEN=$BIND
GIO_DIR_16=$DIR16
GIO_DIR_28=$DIR28
GIO_KEEP_ENV=$KEEPENV
GIO_MUIP_HOST=$MUIP
GIO_AGENT_REGION=$REGION
GIO_MUIP_KEY=$MUIPKEY
GIO_MYSQL_ROOT_PASSWORD=$MYSQLPW
GIO_FLASK_SECRET_KEY=$FLASKKEY
GIO_ADVERTISED_IP=$ADV
GIO_ADVERTISED_HOST=$ADVHOST
GIO_BIND_IP=$BINDIP
GIO_SERVER_NAME=$SRVNAME
GIO_PAYLOAD_DIR=/opt/gio-agent/payloads
GIO_STATE_PATH=${GIO_STATE_PATH:-/var/lib/gio-agent/state.json}
GIO_PROVISION_MODE=${GIO_PROVISION_MODE:-once}
GIO_TXT_FIXES_MODE=${GIO_TXT_FIXES_MODE:-now}
GIO_HOTPATCH_SOURCE=$HPSRC
EOF
# Keys this script does not manage (GIO_TRUST_PROXY, GIO_PATHFINDING, anything an admin added by hand)
# survive an upgrade verbatim instead of vanishing with the rewrite. GIO_KEEP_ENV, GIO_MUIP_KEY,
# GIO_MYSQL_ROOT_PASSWORD and GIO_FLASK_SECRET_KEY ARE managed: the heredoc writes them, so listing them
# here keeps the stored value from being appended a second time. The "kept" line names the KEY only: an
# unmanaged value may be a secret (a proxy URL with its password), and this output reaches the launcher's
# install log.
MANAGED=" GIO_AGENT_TOKEN GIO_AGENT_LISTEN GIO_DIR_16 GIO_DIR_28 GIO_KEEP_ENV GIO_MUIP_HOST GIO_AGENT_REGION GIO_MUIP_KEY GIO_MYSQL_ROOT_PASSWORD GIO_FLASK_SECRET_KEY GIO_ADVERTISED_IP GIO_ADVERTISED_HOST GIO_BIND_IP GIO_SERVER_NAME GIO_PAYLOAD_DIR GIO_STATE_PATH GIO_PROVISION_MODE GIO_TXT_FIXES_MODE GIO_HOTPATCH_SOURCE "
if [ "${#OLDCFG[@]}" -gt 0 ]; then
  for k in "${!OLDCFG[@]}"; do
    case "$MANAGED" in *" $k "*) ;; *) echo "$k=${OLDCFG[$k]}" >> /etc/gio-agent/config; echo "  kept    : $k (not managed by this script; value unchanged)" ;; esac
  done
fi
chmod 600 /etc/gio-agent/config

cat > /etc/systemd/system/gio-agent.service <<EOF
[Unit]
Description=GIO agent (Relic launcher control service)
After=docker.service network-online.target
Wants=docker.service network-online.target
[Service]
Type=simple
ExecStart=$PY3 /opt/gio-agent/gio_agent.py
EnvironmentFile=/etc/gio-agent/config
Restart=always
RestartSec=2
User=root
NoNewPrivileges=true
[Install]
WantedBy=multi-user.target
EOF

# Direct access: open the port in the box's firewall -- firewalld (Alma/RHEL) OR ufw (Ubuntu).
# With no local firewall active there is nothing to open, but say so explicitly: silence here
# would look identical to a ufw that blocks the port.
# Non-fatal: a firewall that refuses the rule must not leave the install half done (service not
# installed yet) -- warn and move on. LC_ALL=C: "Status: active" is localised.
open_port() {
  local port="$1"
  if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then
    if firewall-cmd --permanent --add-port="$port"/tcp && firewall-cmd --reload; then
      echo "  firewalld: port $port/tcp opened"
    else
      echo "  WARNING: firewalld refused to open port $port/tcp -- open it by hand"
    fi
  elif command -v ufw >/dev/null 2>&1 && LC_ALL=C ufw status 2>/dev/null | grep -q '^Status: active'; then
    if ufw allow "$port"/tcp; then
      echo "  ufw: port $port/tcp opened"
    else
      echo "  WARNING: ufw refused to open port $port/tcp -- open it by hand"
    fi
  else
    echo "  no local firewall active (firewalld/ufw) -- port $port is reachable unless an external firewall filters it"
  fi
}
case "$BIND" in
  127.0.0.1:*) : ;;
  *:*) PORT="${BIND##*:}"
     if [ -n "$PORT" ] && [ "$PORT" -eq "$PORT" ] 2>/dev/null; then
       open_port "$PORT"
     else
       echo "  warning: '$PORT' in BIND='$BIND' is not a port number -- skipping the firewall"
     fi ;;
  *) echo "  warning: BIND='$BIND' is not host:port -- skipping the firewall" ;;
esac

systemctl daemon-reload
# Fresh install: enabled and started. Upgrade: the service keeps its state -- an agent the admin
# disabled stays disabled, one deliberately stopped ("inactive") stays stopped; anything else
# (active, failed, crash-looping) is restarted on the new file.
# GIO_AGENT_START=y: whoever runs this clicked "Install" (the launcher's form) -- a running agent is
# the outcome they expect, whatever state the old one was left in.
if [ "$UPGRADE" = 1 ] && [ "$SVC_WAS_ENABLED" = disabled ] && [ "${GIO_AGENT_START:-}" != y ]; then
  echo "  service : left disabled, as it was (systemctl enable gio-agent to start it at boot)"
else
  systemctl enable gio-agent >/dev/null 2>&1 || systemctl enable gio-agent
fi
if [ "$UPGRADE" = 1 ] && [ "$SVC_WAS_ACTIVE" = inactive ] && [ "${GIO_AGENT_START:-}" != y ]; then
  echo "  service : was stopped -- left stopped, as it was (systemctl start gio-agent)"
else
  systemctl restart gio-agent
  sleep 1
  systemctl --no-pager --lines=5 status gio-agent || true
fi

echo
listen_host_port "$BIND"
HEALTH_URL="http://$PROBE_HOST:$PROBE_PORT/health"
if command -v curl >/dev/null 2>&1; then HEALTH=$(curl -s -m 5 "$HEALTH_URL" 2>/dev/null || true)
else HEALTH=$("$PY3" -c 'import sys,urllib.request; print(urllib.request.urlopen(sys.argv[1], timeout=5).read().decode().strip())' "$HEALTH_URL" 2>/dev/null || true); fi
if [ -n "$HEALTH" ]; then echo "  health  : $HEALTH"
elif [ "$UPGRADE" = 1 ] && [ "$SVC_WAS_ACTIVE" = inactive ] && [ "${GIO_AGENT_START:-}" != y ]; then echo "  health  : agent not started (left stopped)"
else echo "  health  : no answer yet on $HEALTH_URL (journalctl -u gio-agent)"; fi
if [ "$UPGRADE" = 1 ]; then
  echo "Done. Upgrade ${OLD_VERSION:-?} -> ${NEW_VERSION:-?}; the previous install is in /var/lib/gio-agent/previous (rollback: docs/AGENT-UPGRADE.md)."
else
  echo "Done. Test locally on the server:  curl -s $HEALTH_URL"
fi
echo "Agent token (enter it on the launcher's start screen, Server Admin mode): $TOKEN"

# ── the server package download(s) chosen above ────────────────────────────────────────────────
# The RUNNING agent does the work (job `fetch`: resumable, verified, extracted, healed); this only
# asks for it and streams the job log. Not when the launcher drives the install (GIO_RELIC_ENV=1: the
# launcher starts and follows the jobs itself), and not when the service is not answering. The command
# is printed first so a Ctrl+C (which stops the FOLLOWING, never the job) or a later session can rerun
# it; GIO_FETCH_FOLLOW=n only prints it.
FETCH_LIST=""
[ "$FETCH16" = y ] && FETCH_LIST="1.6"
[ "$FETCH28" = y ] && FETCH_LIST="${FETCH_LIST:+$FETCH_LIST }2.8"
if [ -n "$FETCH_LIST" ]; then
  FETCH_CMD="$PY3 /opt/gio-agent/gio_agent.py --config /etc/gio-agent/config --fetch $FETCH_LIST"
  echo
  if [ "${GIO_RELIC_ENV:-}" = 1 ]; then
    echo "  download: the launcher starts the server package download ($FETCH_LIST) now and shows it on its Server page"
  elif [ -z "$HEALTH" ]; then
    echo "  download: the agent is not answering yet -- once it does, download the server package(s) with:"
    echo "            $FETCH_CMD"
  elif [ "${GIO_FETCH_FOLLOW:-y}" != y ]; then
    echo "  download: start the server package download ($FETCH_LIST) with:"
    echo "            $FETCH_CMD"
  else
    echo "  download: server package(s) $FETCH_LIST from archive.org -- running (Ctrl+C stops following, not the download):"
    echo "            $FETCH_CMD"
    echo "            (watch a job by hand: curl -s -H 'Authorization: Bearer \$TOKEN' http://$PROBE_HOST:$PROBE_PORT/jobs/<id>?since=0)"
    # never fatal for the install: the agent is installed and running whatever the download did.
    # env -u: on an UPGRADE this shell carries the OLD GIO_AGENT_TOKEN / GIO_AGENT_LISTEN, exported
    # from the stored config as the run's defaults. The CLI's apply_config_file never overrides a key
    # already in the environment, so a run that CHANGED the token or the listen address would send
    # the download request with the old ones -- 401, or a knock on a port nothing answers on. Cleared
    # here, the CLI reads both from the file this run just wrote. (The command printed above needs no
    # such treatment: a later shell has no stale exports.)
    env -u GIO_AGENT_TOKEN -u GIO_AGENT_LISTEN $FETCH_CMD \
      || echo "  download: not finished (exit $?) -- run the command above again to continue where it stopped"
  fi
fi
