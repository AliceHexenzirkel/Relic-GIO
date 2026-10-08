<#
  Interactive deploy of the GIO agent to a Linux server from Windows.
  Works with RHEL-family (AlmaLinux 9 -- firewalld) and Debian-family (Ubuntu -- ufw / no firewall).
  Prompts for SSH details + server paths + "Keep .env settings" (GIO_KEEP_ENV: the agent leaves each
  server's own .env as it is; with a "y" none of the stack passwords and addresses below is asked for
  and the box keeps what it stores for them) + access mode + the MUIP host/sign key (an empty key =
  the agent writes a random one into a stack it prepares while the stack still carries the vendor's
  published key; only a fingerprint is ever shown) + the MySQL root password and the Flask secret key
  (the same rule: empty = a random one replaces the vendor's published value; neither is ever shown),
  then uploads gio_agent.py (+ the uninstaller and payloads) and provisions the systemd service. Uses
  PuTTY's pscp/plink (password OR .ppk key / Pageant).

  Access mode "direct" (default): agent binds 0.0.0.0:18080 and the port is opened in the box's
  firewall (firewalld or ufw; with neither active there is nothing to open) -- the Relic app
  connects straight to it with the bearer token (no SSH on user machines).
  Access mode "tunnel": agent binds 127.0.0.1:18080, reachable only through an SSH local-forward.

  A box with NO server stack yet is fine: answer "n" to "already on the box?" and give the
  folder to install into -- the remote script installs a 7z extractor when the box has none (7zip /
  p7zip-full / libarchive-tools on apt, 7zip / bsdtar / EPEL p7zip on dnf; non-fatal) and, once the
  service answers, the agent downloads the ready-made package from the Internet Archive
  (python3 /opt/gio-agent/gio_agent.py --config /etc/gio-agent/config --fetch 1.6 2.8, streamed here
  through plink; Ctrl+C stops the following, never the download -- rerun the command on the box).
  Assumes python3 + docker on the box (install_agent.sh is the installer that adds them).

  The provisioning script travels as the SSH command itself (plink -m sends the file's text as the
  command to run), so its text -- the config values included: the token, the MUIP sign key, the MySQL
  root password, the Flask secret key -- is visible in the box's process list (ps) while it runs. On a
  box with other local users use the launcher's install form instead (Server page, "Install the agent
  on a new server..."): it hands the values over in a 0600 file, never on a command line.

  Keep this file pure ASCII, without a BOM: Windows PowerShell 5.1 reads a BOM-less script in the ANSI
  code page, where the bytes of a typographic dash or quote end a string (pwsh 7 would not notice).

  Run:  powershell -ExecutionPolicy Bypass -File agent\deploy_from_windows.ps1
#>
$ErrorActionPreference = 'Stop'
$here = Split-Path $MyInvocation.MyCommand.Path -Parent

# Enter = the default; "-" = empty (the only way to clear a stored MUIP host / advertised IP / bind IP
# on a re-deploy, where the stored value is the default). A secret never goes through this one -- its
# stored value must not be the "[$def]" on screen: see AskSecret.
function Ask($prompt, $def) { $a = Read-Host "$prompt [$def]"; if ([string]::IsNullOrWhiteSpace($a)) { $def } elseif ($a.Trim() -eq '-') { '' } else { $a } }

Write-Host "==== Deploy the GIO agent to a Linux server ====" -ForegroundColor Cyan
$sshHost = Ask "SSH host (IP or DNS name)" ""
$port    = Ask "SSH port" "22"
$user    = Ask "SSH user" "root"
$pass    = Read-Host "SSH password (empty = .ppk key / Pageant)"
$key     = ''
if ([string]::IsNullOrWhiteSpace($pass)) { $key = Ask "Path of the private .ppk key (empty = Pageant)" "" }

# locate pscp/plink (PuTTY) -- needed already for the stack-dir autodetect below
$putty = @("C:\Program Files\PuTTY", "C:\Program Files (x86)\PuTTY") | Where-Object { Test-Path (Join-Path $_ 'pscp.exe') } | Select-Object -First 1
if (-not $putty) {
  Write-Warning "PuTTY (pscp/plink) not found. Install PuTTY or use the 'Install the agent' action inside the app."
  exit 1
}
$pscp = Join-Path $putty 'pscp.exe'; $plink = Join-Path $putty 'plink.exe'
$auth = @()
if (-not [string]::IsNullOrWhiteSpace($pass)) { $auth = @('-pw', $pass) }
elseif (-not [string]::IsNullOrWhiteSpace($key)) { $auth = @('-i', $key) }

function Assert-Ok($what) { if ($LASTEXITCODE -ne 0) { Write-Error "$what failed (code $LASTEXITCODE) -- check host/password/key. No token was saved."; exit 1 } }
# plink's command argument goes through PowerShell's native-argument quoting, and Windows PowerShell
# 5.1 strips embedded double quotes from it (a bash `case "$H" in` arrives as `case $H in`). Any
# remote bash that needs quotes is therefore sent through `plink -m <file>` -- plink reads the command
# from the file verbatim. CRLF stripped: a CR inside the command is "command not found" on the box.
function Invoke-Remote($script) {
  $tmp = [System.IO.Path]::GetTempFileName()
  [System.IO.File]::WriteAllText($tmp, ($script -replace "`r", ""), (New-Object System.Text.UTF8Encoding($false)))
  try { & $plink -P $port @auth -batch -m $tmp "$user@$sshHost" } finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}

Write-Host "Checking the SSH connection..." -ForegroundColor Cyan
# first call NOT in -batch mode: "y" answers the one-time host-key prompt and caches the key
"y" | & $plink -P $port @auth "$user@$sshHost" "echo ssh-ok" | Out-Null
Assert-Ok "the SSH connection"
# The stacks may live under /home, /root, /opt or /srv -- offer what the box has.
$detect = & $plink -P $port @auth -batch "$user@$sshHost" "for d in /home/1.6_live /root/1.6_live /opt/1.6_live /srv/1.6_live; do [ -d `$d ] && { echo `$d; break; }; done; for d in /home/2.8_live /root/2.8_live /opt/2.8_live /srv/2.8_live; do [ -d `$d ] && { echo `$d; break; }; done; true" 2>$null
$def16 = '/home/1.6_live'; $def28 = '/home/2.8_live'
foreach ($line in @($detect)) {
  if ("$line" -match '1\.6_live') { $def16 = "$line".Trim() }
  elseif ("$line" -match '2\.8_live') { $def28 = "$line".Trim() }
}
# On a RE-deploy the values already set on the box become the prompt defaults -- otherwise an Enter
# on "advertised IP" would blank GIO_ADVERTISED_IP (and 4206 for everyone outside the LAN).
$cfgRaw = & $plink -P $port @auth -batch "$user@$sshHost" "cat /etc/gio-agent/config 2>/dev/null; true" 2>$null
$existing = @{}
foreach ($line in @($cfgRaw)) {
  # any variable of the EnvironmentFile (DOCKER_HOST, HTTPS_PROXY, ... reach the docker children too);
  # values normalised the way systemd and the agent read them: blanks, a trailing CR, one pair of quotes
  if ("$line" -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') {
    $k = $Matches[1]; $v = $Matches[2].Trim()
    if ($v -match '^"(.*)"$' -or $v -match "^'(.*)'$") { $v = $Matches[1] }
    $existing[$k] = $v
  }
}
function DefOr($key, $fallback) { if ($existing.ContainsKey($key) -and $existing[$key]) { $existing[$key] } else { $fallback } }
# ...and the stored value EVEN WHEN EMPTY, for keys where empty means something (a stack that is not
# on this box, a bind IP the agent auto-detects).
function DefOrKeep($key, $fallback) { if ($existing.ContainsKey($key)) { $existing[$key] } else { $fallback } }

# Per version: already on the box -> its path; not yet -> the folder the ready-made
# package from the Internet Archive is installed into, and the version is marked for the download that
# runs after the install ($fetch16/$fetch28 -- installer-only, never written to the config). A marked
# folder that already holds a docker-compose.yml.tmpl needs no download.
$have16 = (Ask "Is the 1.6 server stack already on the box? (y/n)" "y") -in @('y', 'yes')
if ($have16) { $dir16 = Ask "Path of the 1.6 server (empty = not on this box)" (DefOrKeep 'GIO_DIR_16' $def16); $fetch16 = $false }
else { $dir16 = Ask "Where should the 1.6 server be installed (docker compose dir; empty = skip this version)" $def16; $fetch16 = [bool]$dir16 }
$have28 = (Ask "Is the 2.8 server stack already on the box? (y/n)" "y") -in @('y', 'yes')
if ($have28) { $dir28 = Ask "Path of the 2.8 server (empty = not on this box)" (DefOrKeep 'GIO_DIR_28' $def28); $fetch28 = $false }
else { $dir28 = Ask "Where should the 2.8 server be installed (docker compose dir; empty = skip this version)" $def28; $fetch28 = [bool]$dir28 }
if ($fetch16 -or $fetch28) {
  # through Invoke-Remote: the quotes must reach bash verbatim (PowerShell 5.1 strips them from a
  # plain plink argument, see above)
  $present = Invoke-Remote "for d in '$dir16' '$dir28'; do [ -n `"`$d`" ] && [ -f `"`$d/docker-compose.yml.tmpl`" ] && echo `"`$d`"; done; true" 2>$null
  foreach ($line in @($present)) {
    if ($fetch16 -and "$line".Trim() -eq $dir16) { Write-Host "  note    : $dir16 already holds a server stack -- the download will be skipped"; $fetch16 = $false }
    if ($fetch28 -and "$line".Trim() -eq $dir28) { Write-Host "  note    : $dir28 already holds a server stack -- the download will be skipped"; $fetch28 = $false }
  }
}
$install7z = (Ask "Install a 7z extractor on the box when it has none? (unpacks the archive.org bundles of the hotpatch mirror and the server packages) (y/n)" "y") -in @('y', 'yes')
# "Keep .env settings" (GIO_KEEP_ENV). While it is on, the agent leaves each server's own .env as it is:
# it replaces none of the vendor's published passwords (MySQL root, Flask key -- nor the MUIP sign key or
# the stack internal password) and does not rewrite OUTER_IP. So with a "y" this script asks for no
# password and no address of a stack: the MUIP host and sign key, the MySQL root password, the Flask
# secret key, the advertised IP, the DDNS host and the bind IP are each the value the box stores, else
# empty (the bind IP is not defaulted to the SSH host then) -- and still validated, so an invalid one is
# asked for after all. Not asked is not the same as not applied: the advertised IP and the DDNS host are
# no .env settings, so the agent keeps handing a stored one to the clients while the option is on. The
# question therefore promises only what holds for a stack's own settings: its passwords and OUTER_IP
# stay. The agent's access mode, port and token are asked in either case.
# The default is the box's stored GIO_KEEP_ENV ($keepStored), read by the agent's rule (1 / true / yes /
# on in any letter case, blanks around it ignored = on; anything else = off), so a re-deploy keeps what
# the box has.
$keepStored = (DefOr 'GIO_KEEP_ENV' '').Trim() -in @('1', 'true', 'yes', 'on')
$keepDef = if ($keepStored) { 'y' } else { 'n' }
Write-Host "  Keep .env settings = the agent leaves each server's own .env as it is: it replaces none of the"
Write-Host "  vendor's published passwords (MySQL root, Flask key -- nor the MUIP sign key or the stack internal"
Write-Host "  password) and does not rewrite OUTER_IP. With it on, no password or address of a stack is asked for."
$keepEnv = (Ask "Keep the .env settings of the server stacks as they are (their passwords and OUTER_IP are not replaced)? (y/n)" $keepDef) -in @('y', 'yes')
if ($keepEnv) { Write-Host "  .env    : kept as it is -- the agent replaces none of the vendor's published passwords and does not rewrite OUTER_IP (GIO_KEEP_ENV=1)" }
# A package the agent downloads arrives with the vendor's own .env -- OUTER_IP=127.0.0.1 and the
# published passwords -- and with the option on nothing replaces either. Said once, for a run that has
# both the option on and a version marked for download.
if ($keepEnv -and ($fetch16 -or $fetch28)) { Write-Host "  note    : the downloaded package comes with the vendor's .env (ports on 127.0.0.1, the published passwords) -- it is kept as it is; edit that .env before Prepare server to let other machines connect" }
# Ask for a value that "Keep .env settings" leaves alone: with $keepEnv the prompt is not shown and its
# default -- the value the box stores -- is the answer.
function AskUnlessKept($prompt, $def) { if ($keepEnv) { $def } else { Ask $prompt $def } }
# Empty = the agent derives http://<OUTER_IP>:21051 per version from the stack's .env. Do NOT put
# 127.0.0.1: muipserver publishes its port on OUTER_IP only, so loopback does not answer.
$muip    = AskUnlessKept "MUIP host (empty = derived from the stack's OUTER_IP; '-' clears a stored value)" (DefOr 'GIO_MUIP_HOST' "")
$region  = Ask "MUIP region" (DefOr 'GIO_AGENT_REGION' "dev_docker")
# The sign key muipserver accepts GM commands with. Empty = the agent writes a RANDOM key
# into a stack it prepares while that stack still carries the vendor's published key (creds.txt on the
# box keeps it). '-' clears a stored value. Only a fingerprint is ever shown -- never the key.
# The stored key is NOT the prompt default: Ask renders "[$def]", which would print the secret into
# the console and its scrollback. Enter keeps it, '-' clears it, only the fingerprint is shown.
function Get-Fp([string]$s) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  return (($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($s)) | ForEach-Object { $_.ToString('x2') }) -join '').Substring(0, 8)
}
# One prompt for every secret, and never through Ask: Ask answers '' for an Enter (its default there)
# AND for a '-', so "keep the stored value" and "clear it" could not be told apart. Enter = keep the
# stored value, '-' = clear it, anything else = the new value; an invalid one is asked again (there an
# empty answer = random). With "Keep .env settings" on ($keepEnv) nothing is asked and the stored value
# is the answer -- still held to the pattern: an invalid one is asked for after all (there an empty
# answer = none).
# -cnotmatch: the agent's rule is case-sensitive ASCII, and a case-insensitive [a-z] also matches a
# few non-ASCII letters (the Kelvin sign).
function AskSecret([string]$label, [string]$stored, [string]$pattern, [string]$rule) {
  if ($keepEnv) { $v = $stored }
  else {
    $hint = if ($stored) { "Enter = keep the stored one, fingerprint $(Get-Fp $stored)" } else { "empty = random at the next Prepare server" }
    $a = Read-Host "$label ($hint; '-' clears a stored value)"
    if ([string]::IsNullOrWhiteSpace($a)) { $v = $stored } elseif ($a.Trim() -eq '-') { $v = '' } else { $v = $a.Trim() }
  }
  while ($v -and $v -cnotmatch $pattern) {
    if ($keepEnv -and $v -ceq $stored) { Write-Warning "The stored $label is not valid." }
    Write-Warning $rule
    $a = Read-Host "$label (empty = $(if ($keepEnv) { 'none' } else { 'random' }))"
    if ([string]::IsNullOrWhiteSpace($a)) { $v = '' } else { $v = $a.Trim() }
  }
  return $v
}
$secretRe = '^[A-Za-z0-9_-]{8,128}$'; $secretRule = "8-128 characters: letters, digits, '_' or '-'."
# The result lines under "Keep .env settings": none of these values is applied to a stack, so they
# promise no random value -- a value the box stores is only said to stay in its config ($keptNote),
# without a fingerprint, since nothing runs on it.
$keptNote = ' (the one in the config is not applied)'
$muipKey = AskSecret "MUIP sign key" (DefOr 'GIO_MUIP_KEY' "") $secretRe $secretRule
if ($keepEnv) { Write-Host "  MUIP key: the stack's own key is kept$(if ($muipKey) { $keptNote })" }
elseif ($muipKey) { Write-Host "  MUIP key: chosen (fingerprint $(Get-Fp $muipKey))" } else { Write-Host "  MUIP key: random at the next Prepare server" }
# The MySQL root password and the Flask secret key of a stack. The archives ship published
# values for both in .env; the agent replaces such a value when it prepares the stack -- with the one
# given here or, empty, with a RANDOM one per stack (creds.txt on the box keeps it, the launcher's
# Server secrets card shows it). A value that is already private is NEVER touched from here (rotate it
# from that card). The MySQL one must start with a letter: it is rendered unquoted into
# docker-compose.yml, where a leading digit or '-' can make YAML read a number or a date. Their result
# lines carry no fingerprint (neither does install_agent.sh's summary): that card shows these two
# values themselves, so there is nothing to compare a fingerprint with.
$mysqlPw = AskSecret "MySQL root password" (DefOr 'GIO_MYSQL_ROOT_PASSWORD' "") '^[A-Za-z][A-Za-z0-9_-]{7,127}$' "8-128 characters: letters, digits, '_' or '-', starting with a letter."
if ($keepEnv) { Write-Host "  MySQL pw: the stack's own password is kept$(if ($mysqlPw) { $keptNote })" }
elseif ($mysqlPw) { Write-Host "  MySQL pw: chosen" } else { Write-Host "  MySQL pw: random at the next Prepare server" }
$flaskKey = AskSecret "Flask secret key" (DefOr 'GIO_FLASK_SECRET_KEY' "") $secretRe $secretRule
if ($keepEnv) { Write-Host "  Flask   : the stack's own key is kept$(if ($flaskKey) { $keptNote })" }
elseif ($flaskKey) { Write-Host "  Flask   : chosen" } else { Write-Host "  Flask   : random at the next Prepare server" }
# The IP the server HANDS to clients; decoupled from .env OUTER_IP (docker binds to that one). On a
# VPS with its public IP directly on the interface the two coincide -- put the public IP here or
# leave it empty.
# What belongs there, told to whoever is typing (the launcher's form says the same behind its "?").
if (-not $keepEnv) {
  Write-Host "  The advertised IP is the PUBLIC (internet) IP that players outside the server's network"
  Write-Host "  connect to. Behind a router (the server only has a private address such as 192.168.x.x): put"
  Write-Host "  the router's public IP here and forward TCP 21000, UDP 21081 and the agent's TCP port (18080"
  Write-Host "  unless you change it below) to the server -- players on the server's own network then need NAT"
  Write-Host "  loopback on the router. Empty ('-' clears a stored one) = the bind IP is advertised: right for"
  Write-Host "  a server whose own address is public (a VPS), LAN-only behind a router. An IP that changes? Use"
  Write-Host "  the DDNS host name prompt below instead."
}
$advIp   = AskUnlessKept "Public IP advertised to clients (empty = the one in .env; '-' clears a stored value)" (DefOr 'GIO_ADVERTISED_IP' "")
# The agent takes it verbatim as the rewrite target of dispatch/gateserver/the URL columns -- a typo would
# be handed to every player, so it is asked again (the same rule as install_agent.sh).
# Round-tripped, NOT a bare TryParse: .NET still accepts the classic shorthand, so a mistyped port
# ("4206") parses as 0.0.16.110 and a truncated quad ("192.168.1") as 192.168.0.1 -- both would sail
# through and be advertised to every player. install_agent.sh validates with Python's strict
# ipaddress.ip_address, and these two must not disagree.
function Test-PlainIp([string]$s) {
  $ip = $null
  return [System.Net.IPAddress]::TryParse($s, [ref]$ip) -and $ip.ToString() -eq $s
}
while ($advIp -and -not (Test-PlainIp $advIp.Trim())) {
  $dnsNameGoes = if ($keepEnv) { 'a DNS name belongs in GIO_ADVERTISED_HOST' } else { 'a DNS name goes in the DDNS host prompt below' }
  Write-Warning "'$advIp' is not an IP address ($dnsNameGoes)."
  $advIp = Ask "Public IP advertised to clients (empty = the one in .env)" ""
}
if ($advIp) { $advIp = $advIp.Trim() }
# A dynamic WAN IP: a DNS name kept current by a DDNS updater; the agent follows it and wins over the
# IP above when both are set.
$advHost = AskUnlessKept "DDNS host name to follow instead of a fixed IP (empty = none; '-' clears a stored value)" (DefOr 'GIO_ADVERTISED_HOST' "")
if ($advHost -and $advIp) {
  # On a re-deploy, one of them new in this run and the other merely kept from the box's config: the new one
  # is what the admin means (the same rule as install_agent.sh, compared the same case-sensitive way).
  # Otherwise keep both -- the agent follows the host and logs a warning at start.
  $oldAdvIp = DefOr 'GIO_ADVERTISED_IP' ''; $oldAdvHost = DefOr 'GIO_ADVERTISED_HOST' ''
  if ($existing.Count -gt 0 -and $advIp -cne $oldAdvIp -and $advHost -ceq $oldAdvHost) {
    Write-Host "  note    : the advertised IP given now replaces the stored DDNS host ($advHost)"; $advHost = ''
  } elseif ($existing.Count -gt 0 -and $advHost -cne $oldAdvHost -and $advIp -ceq $oldAdvIp) {
    Write-Host "  note    : the DDNS host given now replaces the stored advertised IP ($advIp)"; $advIp = ''
  } else {
    # "Keep .env settings" shows neither prompt, so there the way to clear one is the box's config.
    $howToClear = if ($keepEnv) { "Clear one of the two in /etc/gio-agent/config on the box (or from the launcher's Agent settings card)." } else { "Press Ctrl+C and run this again answering '-' at one of the two prompts to clear it." }
    Write-Warning "Both an advertised IP and a DDNS host are set -- the agent follows '$advHost' and ignores '$advIp'. $howToClear"
  }
}
# The LOCAL IP docker binds to (what OUTER_IP in .env must be). The vendor archive ships 127.0.0.1
# there, and a re-extraction over an installed stack would bind every port to loopback (the client
# sees only "server busy" 502) -- the agent repairs .env with this value before any bootstrap.
# Default: the IP we SSH to -- right on a VPS with a public IP on the interface; on a box behind
# NAT put the LAN IP (or empty = the agent auto-detects).
# On a re-deploy the stored value is the default EVEN WHEN EMPTY (empty = the agent auto-detects, the
# NAT/DHCP setting) -- DefOr would turn it into the SSH address.
# One stored empty value is not a setting: the one of a box whose config has "Keep .env settings" ON
# ($keepStored). A deploy with the option on shows no bind IP prompt and does not write the SSH address,
# so on a box that had no bind IP it stores blank whatever the admin would have chosen. Taken as the
# default, a deploy that switches the option off would write blank again and the stack would stay on the
# vendor's loopback OUTER_IP -- so such a box gets the default of a first deploy. A stored NON-EMPTY
# value is the default whatever the option was, and so is an empty one stored with it off.
# With "Keep .env settings" on OUTER_IP stays the stack's own: nothing is asked and the SSH address is
# not written -- the bind IP is the value the box stores, else empty.
$useStoredBind = ($existing.Count -gt 0) -and -not ($keepStored -and -not (DefOr 'GIO_BIND_IP' ''))
$bindDef = if ($useStoredBind) { DefOrKeep 'GIO_BIND_IP' '' } elseif ($keepEnv) { '' } else { $sshHost }
$bindIp  = AskUnlessKept "Local bind IP for docker (OUTER_IP in .env; empty = auto-detected; '-' clears a stored value)" $bindDef
if ($bindIp -and $bindIp -notmatch '^\d{1,3}(\.\d{1,3}){3}$') {
  # a hostname would pass bind() (it resolves) and land in the compose ports, where docker refuses
  # it only at the next up -- far from the cause. Better leave it empty: the agent auto-detects.
  $autoDetects = if ($keepEnv) { '' } else { ' (the agent auto-detects the LAN IP)' }
  Write-Warning "'$bindIp' is not an IP -- leaving GIO_BIND_IP empty$autoDetects."
  $bindIp = ''
}
if ($keepEnv) {
  $bindNote = if ($bindIp) { " ($bindIp in the config is not applied)" } else { '' }
  Write-Host "  bind IP : the stack's own OUTER_IP is kept$bindNote"
}
$srvName = Ask "Server name shown to players" (DefOr 'GIO_SERVER_NAME' $sshHost)
# Where the hotpatch mirror is filled FROM the first time this server fills it: the ready-made
# archive.org bundles (default -- one pinned download) or the official CDN, file by file. This seeds
# the agent's policy field `hotpatchSource`; once the admin picks a source in the launcher's
# hotpatch card, that stored choice wins and this value is no longer consulted.
$hpSource = (DefOr 'GIO_HOTPATCH_SOURCE' 'archive')
do {
  $hpSource = (Ask 'Hotpatch mirror source (archive|cdn)' $hpSource).Trim().ToLowerInvariant()
  if ($hpSource -notin @('archive','cdn')) { Write-Warning "Type 'archive' (archive.org bundles) or 'cdn' (the official CDN, file by file)." }
} while ($hpSource -notin @('archive','cdn'))
# Re-deploy: the access mode and the port come from the box's GIO_AGENT_LISTEN, so Enter keeps a
# tunnel-only (127.0.0.1) agent tunnel-only and a custom port custom -- a silent flip to
# 0.0.0.0:18080 here would also open the firewall on it.
$exListen = DefOr 'GIO_AGENT_LISTEN' ''
# every form the agent accepts: host:port, [v6]:port, a bare port (bound on loopback), a bare host
# (port 18080), nothing (= 127.0.0.1:18080)
$exHost = ''; $exPort = '18080'
if     ($exListen -match '^\[(.*)\]:(\d+)$') { $exHost = $Matches[1]; $exPort = $Matches[2] }
elseif ($exListen -match '^(\d+)$')           { $exHost = '127.0.0.1'; $exPort = $Matches[1] }
elseif ($exListen -match '^(.*):(\d+)$')      { $exHost = $Matches[1]; $exPort = $Matches[2] }
elseif ($exListen)                             { $exHost = $exListen }
$modeDef  = if ($existing.Count -gt 0 -and ($exListen -eq '' -or $exHost -in @('127.0.0.1', 'localhost', '::1'))) { 'tunnel' } else { 'direct' }
$portDef  = $exPort
$mode    = Ask "App access mode: direct (no SSH on user machines) or tunnel" $modeDef
# Accept the obvious synonyms for tunnel; anything else is treated as direct AND confirmed, so a
# typo can't silently open the agent to the network.
$tunnelMode = $mode -in @('tunnel', 'tunel', 'ssh', 't')
if (-not $tunnelMode -and $mode -ne 'direct') {
  $c = Ask "Mode '$mode' not recognised -- using DIRECT (the agent becomes reachable on the network). Continue? (y/n)" "y"
  if ($c -notin @('y', 'yes', 'd', 'da')) { Write-Host "cancelled"; exit 1 }
}
$agPort  = Ask "Agent port" $portDef
# A listen on a specific address ([::]:18080, 192.0.2.10:18080) is kept verbatim while the admin
# changed neither the mode nor the port; otherwise the listen is rebuilt from the two answers.
$keepListen = ($existing.Count -gt 0) -and ($exListen -ne '') -and ($agPort -eq $exPort) -and ($mode -eq $modeDef) -and ($exHost -notin @('', '0.0.0.0', '127.0.0.1', 'localhost', '::1'))
$bind    = if ($keepListen) { $exListen } elseif ($tunnelMode) { "127.0.0.1:$agPort" } else { "0.0.0.0:$agPort" }
$tokDef  = DefOr 'GIO_AGENT_TOKEN' ''
$token   = Read-Host "Bearer token (empty = $(if ($tokDef) { 'keep the existing token' } else { 'generate' }))"
if ([string]::IsNullOrWhiteSpace($token)) {
  if ($tokDef) {
    $token = $tokDef
    Write-Host "  keeping the existing token (launchers that already have it keep working)"
  } else {
    $token = -join ((1..48) | ForEach-Object { '{0:x}' -f (Get-Random -Max 16) })
    Write-Host "  generated token: $token"
  }
}

# Re-deploy over a running agent: never under a job, and keep the previous generation for a
# rollback -- the same rules as install_agent.sh (docs/AGENT-UPGRADE.md). The probe asks /jobs on the
# address the agent is bound to (wildcards -> loopback), reading the token on the box, never on a
# command line.
if ($existing.Count -gt 0) {
  # The same probe install_agent.sh runs (docs/AGENT-UPGRADE.md): the agent's own bound address,
  # /health (no token) -> /jobs -> /status.busy, the token read on the box and handed over through the
  # environment. Answers: none / idle / busy KIND VERSION / unknown: WHY -- and unknown is NOT idle.
  # Everything on the remote side is silenced (a stderr line from plink would be a terminating error
  # under $ErrorActionPreference = 'Stop').
  $probe = (Invoke-Remote (@'
{ cfgval() { sed -n "s/^[[:space:]]*$1[[:space:]]*=[[:space:]]*//p" /etc/gio-agent/config | tail -1 | tr -d "\r\"'" | sed 's/[[:space:]]*$//'; }
L=$(cfgval GIO_AGENT_LISTEN); T=$(cfgval GIO_AGENT_TOKEN)
case "$L" in ::|'[::]') H=; P=18080 ;; \[*\]:*) H=${L%\]:*}; H=${H#\[}; P=${L##*:} ;; *:*) H=${L%:*}; P=${L##*:} ;; '') H=; P=18080 ;; *) if [ "$L" -eq "$L" ] 2>/dev/null; then H=; P=$L; else H=$L; P=18080; fi ;; esac
[ "$P" -eq "$P" ] 2>/dev/null || P=18080
case "$H" in ''|0.0.0.0|::|'[::]') H=127.0.0.1 ;; *:*) H="[$H]" ;; esac
GIO_OLD_TOKEN="$T" python3 - "$H:$P" <<'PYEOF'
'@ + "`n" + 'import json, os, sys, urllib.error, urllib.request
base = "http://%s" % sys.argv[1]
token = os.environ.get("GIO_OLD_TOKEN", "")
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
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
        running = []
    if running:
        print(("busy %s %s" % (running[0].get("kind", "?"), running[0].get("version", ""))).strip()); sys.exit(0)
    b = get("/status", 60, True).get("busy")
    if isinstance(b, dict):
        print(("busy %s %s" % (b.get("kind", "?"), b.get("version", ""))).strip()); sys.exit(0)
    print("idle")
except urllib.error.HTTPError as e:
    print("unknown: HTTP %s from the agent (token changed by hand?)" % e.code)
except Exception as e:
    print("unknown: %s" % e)' + "`n" + @'
PYEOF
} 2>/dev/null
'@)) -join "`n"
  $verdict = @("$probe" -split "`n" | Where-Object { $_ -match '^(none|idle|busy|unknown)' } | Select-Object -Last 1)
  $verdict = if ($verdict.Count -gt 0) { "$($verdict[0])".Trim() } else { 'unknown: no answer from the probe (python3 missing on the box?)' }
  if ($verdict -match '^busy') {
    Write-Error "The agent on $sshHost has a job in progress ($($verdict -replace '^busy\s*', '')) -- wait for it to finish (the launcher shows it) and run this again."; exit 1
  } elseif ($verdict -match '^unknown') {
    if ($env:GIO_UPGRADE_FORCE -eq 'y') { Write-Warning "could not tell whether a job is running ($verdict) -- continuing because GIO_UPGRADE_FORCE=y" }
    else { Write-Error "Could not tell whether the agent on $sshHost has a job in progress ($verdict). Fix that (is the agent healthy? is the token in /etc/gio-agent/config the one it runs with?) or set GIO_UPGRADE_FORCE=y to deploy regardless."; exit 1 }
  } elseif ($verdict -eq 'none') { Write-Host "  agent   : not running on the box -- no job to wait for" }
  else { Write-Host "  agent   : running, no job in progress" }
  # One previous generation for a rollback: agent, config, the state file the config names, unit, payloads.
  Invoke-Remote @'
{ if [ -f /opt/gio-agent/gio_agent.py ]; then
    P=/var/lib/gio-agent/previous; rm -rf "$P"; mkdir -p "$P"; chmod 700 "$P"; K=""
    cp -a /opt/gio-agent/gio_agent.py "$P/" && K="$K agent"
    cp -a /etc/gio-agent/config "$P/" && K="$K config"
    S=$(sed -n 's/^[[:space:]]*GIO_STATE_PATH[[:space:]]*=[[:space:]]*//p' /etc/gio-agent/config | tail -1 | tr -d "\r\"'"); S=${S:-/var/lib/gio-agent/state.json}
    if [ -f "$S" ]; then cp -a "$S" "$P/state.json" && K="$K state"; fi
    if [ -f /etc/systemd/system/gio-agent.service ]; then cp -a /etc/systemd/system/gio-agent.service "$P/" && K="$K unit"; fi
    if [ -d /opt/gio-agent/payloads ]; then cp -a /opt/gio-agent/payloads "$P/payloads" && K="$K payloads"; fi
    echo "  previous :$K kept in $P"
  fi; } 2>/dev/null; true
'@
}
Write-Host "Uploading gio_agent.py + uninstall_agent.sh..." -ForegroundColor Cyan
& $plink -P $port @auth -batch "$user@$sshHost" "mkdir -p /opt/gio-agent /etc/gio-agent /var/lib/gio-agent"
Assert-Ok "the SSH connection (mkdir)"
& $pscp -P $port @auth -batch (Join-Path $here 'gio_agent.py') "$user@${sshHost}:/opt/gio-agent/gio_agent.py"
Assert-Ok "uploading gio_agent.py"
if (Test-Path (Join-Path $here 'uninstall_agent.sh')) {
  & $pscp -P $port @auth -batch (Join-Path $here 'uninstall_agent.sh') "$user@${sshHost}:/opt/gio-agent/uninstall_agent.sh"
  Assert-Ok "uploading uninstall_agent.sh"
}

$payloads = Join-Path $here 'payloads'
if (Test-Path $payloads) {
  Write-Host "Uploading the payloads (GAA progress + templates)..." -ForegroundColor Cyan
  # Into a staging folder first, swapped in only once the upload succeeded: the old payloads must
  # not be gone when the upload fails half-way (and pscp -r refuses a target folder that does not
  # exist, so the parent is the target and pscp creates "payloads" under it).
  & $plink -P $port @auth -batch "$user@$sshHost" "rm -rf /opt/gio-agent/.upload && mkdir -p /opt/gio-agent/.upload"
  Assert-Ok "preparing the upload folder"
  & $pscp -P $port @auth -batch -r $payloads "$user@${sshHost}:/opt/gio-agent/.upload/"
  Assert-Ok "uploading the payloads"
  & $plink -P $port @auth -batch "$user@$sshHost" "rm -rf /opt/gio-agent/payloads && mv /opt/gio-agent/.upload/payloads /opt/gio-agent/payloads && rmdir /opt/gio-agent/.upload"
  Assert-Ok "installing the payloads"
} else {
  Write-Warning "agent\payloads is missing -- the agent will not be able to apply the GAA progress."
}
# Distro-neutral: firewalld (Alma/RHEL) or ufw (Ubuntu); with neither active there is nothing to
# open, but say so explicitly -- silence would look identical to a ufw that blocks the port.
$openPort = if ($tunnelMode) { "" } else {
  "if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then firewall-cmd --permanent --add-port=$agPort/tcp && firewall-cmd --reload && echo 'firewalld: port $agPort/tcp opened'; elif command -v ufw >/dev/null 2>&1 && LC_ALL=C ufw status 2>/dev/null | grep -q '^Status: active'; then ufw allow $agPort/tcp; else echo 'no local firewall active (firewalld/ufw) - port $agPort is reachable unless an external firewall filters it'; fi"
}
$provMode  = DefOr 'GIO_PROVISION_MODE' 'once'
$txtMode   = DefOr 'GIO_TXT_FIXES_MODE' 'now'
$statePath = DefOr 'GIO_STATE_PATH' '/var/lib/gio-agent/state.json'
# Keys this script does not manage (GIO_TRUST_PROXY, anything added by hand) survive a re-deploy verbatim.
$managed = @('GIO_AGENT_TOKEN','GIO_AGENT_LISTEN','GIO_DIR_16','GIO_DIR_28','GIO_KEEP_ENV','GIO_MUIP_HOST','GIO_AGENT_REGION','GIO_MUIP_KEY','GIO_MYSQL_ROOT_PASSWORD','GIO_FLASK_SECRET_KEY','GIO_ADVERTISED_IP','GIO_ADVERTISED_HOST','GIO_BIND_IP','GIO_SERVER_NAME','GIO_PAYLOAD_DIR','GIO_STATE_PATH','GIO_PROVISION_MODE','GIO_TXT_FIXES_MODE','GIO_HOTPATCH_SOURCE')
$extraKeys = (@($existing.Keys | Where-Object { $_ -notin $managed } | Sort-Object | ForEach-Object { "$_=$($existing[$_])" })) -join "`n"
# <<'EOF' (quoted): $remote is ALREADY PowerShell-interpolated, so the remote shell must NOT expand
# a second time -- a token/path containing $ would otherwise be mangled (or a backtick executed).
# set -e: without it, plink's exit code is only the LAST line's (systemctl status = 0 if the service
# is active), so a failed selftest would pass unnoticed and Assert-Ok would report success.
# The config is created 0600 BEFORE its first byte (the `install -m 600 /dev/null` line): on a fresh
# box the redirection would create it 0644 and leave it so until the chmod -- with the token and the
# stack secrets already in it. An existing file is rewritten in place and keeps its mode.
$upgradeFlag = if ($existing.Count -gt 0) { 1 } else { 0 }
# The 7z extractor: the archive.org bundles of the hotpatch mirror and the server package
# download need one; NON-FATAL either way (the agent works without it, file by file from the CDN, and
# refuses a package download with a clear 409 until a tool exists). Single-quoted here-string: its $t
# and $(...) are bash's, not PowerShell's, and $remote's interpolation inserts it verbatim.
$extractorCheck = @'
have_7z() { for t in 7zz 7z 7za 7zr bsdtar; do command -v $t >/dev/null 2>&1 && return 0; done; return 1; }
if ! have_7z; then
  if [ "$INSTALL_7Z" = 1 ]; then
    echo '  7z      : no 7zz/7z/7za/7zr/bsdtar on the box -- installing (non-fatal)'
    if command -v apt-get >/dev/null 2>&1; then
      export DEBIAN_FRONTEND=noninteractive
      apt-get -o DPkg::Lock::Timeout=300 update -q >/dev/null 2>&1 || true
      apt-get -o DPkg::Lock::Timeout=300 install -y -q 7zip || apt-get -o DPkg::Lock::Timeout=300 install -y -q p7zip-full || apt-get -o DPkg::Lock::Timeout=300 install -y -q libarchive-tools || true
    elif command -v dnf >/dev/null 2>&1; then
      dnf install -y 7zip 2>/dev/null || dnf install -y bsdtar 2>/dev/null || { dnf install -y epel-release && dnf install -y p7zip p7zip-plugins; } || true
    fi
  fi
  if have_7z; then for t in 7zz 7z 7za 7zr bsdtar; do command -v $t >/dev/null 2>&1 && { echo "  7z      : $(command -v $t)"; break; }; done
  else echo '  warning : no 7z/bsdtar tool -- the hotpatch mirror is filled file by file from the CDN and a server package download is refused until one is installed (apt install 7zip | dnf install bsdtar)'; fi
fi
'@
$install7zFlag = if ($install7z) { 1 } else { 0 }
$keepEnvFlag = if ($keepEnv) { 1 } else { 0 }
$remote = @"
set -e
UPGRADE=$upgradeFlag
WAS_ACTIVE=`$(systemctl is-active gio-agent 2>/dev/null || true); WAS_ENABLED=`$(systemctl is-enabled gio-agent 2>/dev/null || true)
if [ -f /opt/gio-agent/uninstall_agent.sh ]; then sed -i 's/\r`$//' /opt/gio-agent/uninstall_agent.sh; chmod 755 /opt/gio-agent/uninstall_agent.sh; fi
[ -e /etc/gio-agent/config ] || install -m 600 /dev/null /etc/gio-agent/config
cat > /etc/gio-agent/config <<'EOF'
GIO_AGENT_TOKEN=$token
GIO_AGENT_LISTEN=$bind
GIO_DIR_16=$dir16
GIO_DIR_28=$dir28
GIO_KEEP_ENV=$keepEnvFlag
GIO_MUIP_HOST=$muip
GIO_AGENT_REGION=$region
GIO_MUIP_KEY=$muipKey
GIO_MYSQL_ROOT_PASSWORD=$mysqlPw
GIO_FLASK_SECRET_KEY=$flaskKey
GIO_ADVERTISED_IP=$advIp
GIO_ADVERTISED_HOST=$advHost
GIO_BIND_IP=$bindIp
GIO_SERVER_NAME=$srvName
GIO_PAYLOAD_DIR=/opt/gio-agent/payloads
GIO_STATE_PATH=$statePath
GIO_PROVISION_MODE=$provMode
GIO_TXT_FIXES_MODE=$txtMode
GIO_HOTPATCH_SOURCE=$hpSource
$extraKeys
EOF
sed -i '/^$/d' /etc/gio-agent/config
chmod 600 /etc/gio-agent/config
PY3="`$(command -v python3 || true)"
[ -n "`$PY3" ] || { echo 'python3 is missing on the server - install it (dnf install python3 / apt install python3)'; exit 1; }
cat > /etc/systemd/system/gio-agent.service <<UNIT
[Unit]
Description=GIO agent (Relic launcher control service)
After=docker.service network-online.target
Wants=docker.service network-online.target
[Service]
Type=simple
ExecStart=`$PY3 /opt/gio-agent/gio_agent.py
EnvironmentFile=/etc/gio-agent/config
Restart=always
RestartSec=2
User=root
NoNewPrivileges=true
[Install]
WantedBy=multi-user.target
UNIT
$openPort
INSTALL_7Z=$install7zFlag
$extractorCheck
"`$PY3" /opt/gio-agent/gio_agent.py --selftest
systemctl daemon-reload
if [ "`$UPGRADE" = 1 ] && [ "`$WAS_ENABLED" = disabled ]; then echo "  service : left disabled, as it was (systemctl enable gio-agent to start it at boot)"; else systemctl enable gio-agent; fi
if [ "`$UPGRADE" = 1 ] && [ "`$WAS_ACTIVE" = inactive ]; then echo "  service : was stopped -- left stopped, as it was (systemctl start gio-agent)"; else systemctl restart gio-agent && sleep 1 && systemctl --no-pager --lines=4 status gio-agent; fi
"@
# A CRLF checkout would inject \r into the remote script (bash: "command not found") -- always strip them.
Invoke-Remote $remote
Assert-Ok "provisioning the agent on the server"

# The server package download(s) chosen above: the RUNNING agent does the work (job
# `fetch`: resumable, verified, extracted, healed); the --fetch CLI only asks for it and streams the
# job log, which plink relays live. Non-fatal: the agent is installed either way, and the command
# printed here continues where it stopped.
if ($fetch16 -or $fetch28) {
  $list = @(); if ($fetch16) { $list += '1.6' }; if ($fetch28) { $list += '2.8' }
  $fetchCmd = "python3 /opt/gio-agent/gio_agent.py --config /etc/gio-agent/config --fetch $($list -join ' ')"
  Write-Host ""
  Write-Host "Downloading the server package(s) $($list -join ', ') from archive.org on the box (Ctrl+C stops following, not the download; rerun on the box: $fetchCmd)" -ForegroundColor Cyan
  Invoke-Remote $fetchCmd
  if ($LASTEXITCODE -ne 0) { Write-Warning "the server package download did not finish (exit $LASTEXITCODE) -- run on the box: $fetchCmd  (it continues where it stopped)" }
}

Write-Host ""
Write-Host "Done. Agent token: $token" -ForegroundColor Green
Write-Host "Uninstall (on the server): bash /opt/gio-agent/uninstall_agent.sh"
if ($tunnelMode) {
  Write-Host "Tunnel mode: the agent answers on the box's 127.0.0.1:$agPort only -- reach it through an SSH local forward of that port."
} else {
  $tokenFile = Join-Path (Split-Path $here -Parent) 'config\agent.token'
  if (Test-Path $tokenFile) {
    Copy-Item $tokenFile "$tokenFile.bak" -Force
    Write-Host "The previous token was kept in config\agent.token.bak" -ForegroundColor Yellow
  }
  Set-Content -Path $tokenFile -Value $token -NoNewline
  Write-Host "The token was saved to config\agent.token. Enter it on the launcher's start screen (Server Admin mode)." -ForegroundColor Green
  Write-Host "To bake it into your own admin build: .\build\publish.ps1 --default-server=$sshHost (it reads config\agent.token)"
}
