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

  A box with NO server stack yet is fine: answer d for a version and give the folder to install
  into -- the remote script installs a 7z extractor when the box has none (7zip /
  p7zip-full / libarchive-tools on apt, 7zip / bsdtar / EPEL p7zip on dnf; non-fatal) and, once the
  service answers, the agent downloads the ready-made package from the Internet Archive
  (python3 /opt/gio-agent/gio_agent.py --config /etc/gio-agent/config --fetch <version> ..., streamed
  here through plink; Ctrl+C stops the following, never the download -- rerun the command on the box).
  The question is asked of a version whose catalogue (payloads\<version>\stack.json) lies beside this
  script; without one only h / l. A version the box already has keeps its folder (a folder changes only
  through a relocation: the launcher's Server page, or gio_agent.py --relocate on the box, named once the
  agent is installed), and a re-deploy stops when a stack folder is relocated while it runs.
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

# The server versions, one row each -- the rows of STACK_VERSIONS in install_agent.sh and of VERSIONS
# in gio_agent.py, in their order. Everything this script does per version comes from them: the config
# key GIO_DIR_<nn> (<nn> = the id without its dot), the folder looked for on the box (<id>_live), the
# prompts, the config lines, the download. implicit = the agent assumes /home/<id>_live for the version
# while its key is unset, so that folder is the default on every box. A row without it gets a folder
# only once one is named, found or stored: such a version stays unconfigured on a box that never
# asked for it.
$stackVersions = @(
  @{ id = '1.6'; implicit = $true },
  @{ id = '2.8'; implicit = $true },
  @{ id = '3.2'; implicit = $false }
)
function DirKey([string]$id) { 'GIO_DIR_' + $id.Replace('.', '') }

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
# The stacks may live under /home, /root, /opt or /srv -- offer what the box has: the box answers one
# line "<id>=<folder>" per version it holds, and "stack:<id>" when that folder holds a compose file. For
# an implicit row any folder of that name counts. For another row only one that holds a compose file
# does: an empty folder -- a cancelled download leaves one -- must not configure a version the box never
# asked for. The command carries no quote (Windows PowerShell 5.1 strips them from a plain plink
# argument, see Invoke-Remote), and no path in it needs one.
$detectCmd = ''
foreach ($sv in $stackVersions) {
  $name = $sv.id + '_live'
  $holds = if ($sv.implicit) { '[ -d $d ]' } else { '{ [ -f $d/docker-compose.yml.tmpl ] || [ -f $d/docker-compose.yml ]; }' }
  $detectCmd += "for d in /home/$name /root/$name /opt/$name /srv/$name; do $holds && { echo $($sv.id)=`$d; { [ -f `$d/docker-compose.yml.tmpl ] || [ -f `$d/docker-compose.yml ]; } && echo stack:$($sv.id); break; }; done; "
}
$detect = & $plink -P $port @auth -batch "$user@$sshHost" ($detectCmd + 'true') 2>$null
$found = @{}; $foundStack = @{}
foreach ($line in @($detect)) {
  if ("$line" -match '^\s*(\d+\.\d+)=(\S+)\s*$') { $found[$Matches[1]] = $Matches[2] }
  elseif ("$line" -match '^\s*stack:(\d+\.\d+)\s*$') { $foundStack[$Matches[1]] = $true }
}
# On a RE-deploy the values already set on the box become the prompt defaults -- otherwise an Enter
# on "advertised IP" would blank GIO_ADVERTISED_IP (and 4206 for everyone outside the LAN). The box says
# first whether it HAS a config ($hasConfig = a re-deploy): one it has and could not hand over (a user that
# may not read it, an SSH call that failed) is no fresh box -- taken for one, its folders, its token and its
# service state would be written over, with no previous generation kept.
$cfgRaw = & $plink -P $port @auth -batch "$user@$sshHost" "if [ -f /etc/gio-agent/config ]; then if [ -r /etc/gio-agent/config ]; then echo __CFG_OK__; cat /etc/gio-agent/config; else echo __CFG_UNREADABLE__; fi; else echo __CFG_NONE__; fi" 2>$null
$cfgLines = @($cfgRaw)
$cfgAt = -1
for ($i = 0; $i -lt $cfgLines.Count; $i++) { if ("$($cfgLines[$i])" -match '^__CFG_(OK|NONE|UNREADABLE)__\s*$') { $cfgAt = $i; $cfgMark = $Matches[1]; break } }
if ($cfgAt -lt 0) { Write-Error "No answer from $sshHost about /etc/gio-agent/config -- check the connection and run this again (nothing was changed)."; exit 1 }
if ($cfgMark -eq 'UNREADABLE') { Write-Error "/etc/gio-agent/config on $sshHost cannot be read by $user -- deploy as root (nothing was changed)."; exit 1 }
$hasConfig = $cfgMark -eq 'OK'
$existing = @{}
foreach ($line in @($cfgLines | Select-Object -Skip ($cfgAt + 1))) {
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

# The sizes of version $id's ready-made package for its question, from payloads\<id>\stack.json beside this
# script: "<size> from the Internet Archive", then " (<size> as a .zip on a box without a 7z tool)" when the url
# is a .7z and an alt entry is a .zip, then ", <size> once unpacked" when the catalogue names it ('' when the
# catalogue cannot be read). install_agent.sh's package_phrase.
function Get-PackagePhrase([string]$id) {
  try {
    $cat = [System.IO.File]::ReadAllText((Join-Path $here "payloads\$id\stack.json")) | ConvertFrom-Json
    $gb = { param($n) [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:0.0} GB', [double]$n / 1e9) }
    if (-not ($cat.size -is [long] -or $cat.size -is [int]) -or $cat.size -le 0) { return '' }
    $out = (& $gb $cat.size) + ' from the Internet Archive'
    if ($cat.url -is [string] -and $cat.url -cmatch '^[^?#]*\.7[zZ]([?#]|$)') {
      foreach ($alt in @($cat.alt)) {
        if ($alt -and $alt.url -is [string] -and $alt.url -cmatch '^[^?#]*\.[zZ][iI][pP]([?#]|$)' -and ($alt.size -is [long] -or $alt.size -is [int]) -and $alt.size -gt 0) {
          $out += ' (' + (& $gb $alt.size) + ' as a .zip on a box without a 7z tool)'; break
        }
      }
    }
    if (($cat.extractedSize -is [long] -or $cat.extractedSize -is [int]) -and $cat.extractedSize -gt 0) { $out += ', ' + (& $gb $cat.extractedSize) + ' once unpacked' }
    return $out
  } catch { return '' }
}
# What stands at folders of the box -- absent | empty | stack | other, the agent's relocate_target_kind --, by
# folder; 'unknown' for a folder the box gave no answer for. One SSH round trip through Invoke-Remote, the remote
# side silenced (a stderr line from plink would be a terminating error under $ErrorActionPreference = 'Stop').
function Get-RemoteKinds([string[]]$dirs) {
  $kinds = @{}
  if ($dirs.Count -eq 0) { return $kinds }
  $body = 'k() { if [ ! -e "$1" ] && [ ! -L "$1" ]; then echo absent; elif [ ! -d "$1" ]; then echo other; elif ! ls -A -- "$1" >/dev/null 2>&1; then echo other; elif [ -z "$(ls -A -- "$1")" ]; then echo empty; elif [ -f "$1/docker-compose.yml.tmpl" ] || [ -f "$1/docker-compose.yml" ]; then echo stack; else echo other; fi; }' + "`n"
  for ($i = 0; $i -lt $dirs.Count; $i++) { $body += "echo $i=`$(k '" + $dirs[$i].Replace("'", "'\''") + "')`n" }
  $out = Invoke-Remote ('{ ' + $body + '} 2>/dev/null; true')
  foreach ($line in @($out)) {
    if ("$line" -match '^\s*(\d+)=(absent|empty|stack|other)\s*$' -and [int]$Matches[1] -lt $dirs.Count) { $kinds[$dirs[[int]$Matches[1]]] = $Matches[2] }
  }
  foreach ($d in $dirs) { if (-not $kinds.ContainsKey($d)) { $kinds[$d] = 'unknown' } }
  return $kinds
}

# Per version ONE choice: d = download the ready-made package now (only a version whose catalogue,
# payloads\<id>\stack.json, lies beside this script), h = it is already extracted on the box, l = not now.
# The version is marked for the download that runs after the install ($stackFetch -- installer-only, never
# written to the config). A folder the box already uses for the version stays -- the one its config stores
# (non-empty), and on a re-deploy, for an implicit row whose key the config never held, the folder the agent
# assumes: a server's folder changes only through a relocation. Such a folder that holds no server yet is
# offered the download. A re-ask never repeats a refused answer as its default. The relocation commands
# ($relocHints: a server found in another folder than the kept one) are printed only once the new agent is
# installed: an agent file that does not know --relocate would start a second agent beside the running one.
Write-Host "  Server versions: d = download the ready-made package now, h = it is already extracted on the box,"
Write-Host "  l = not now (add it later on the launcher's Server page). A version the box already has keeps its folder."
$stackDir = @{}; $stackFetch = @{}; $keptAny = $false; $relocHints = @()
foreach ($sv in $stackVersions) {
  $id = $sv.id; $dirKey = DirKey $id
  $stackDir[$id] = ''; $stackFetch[$id] = $false
  $downloadable = Test-Path -LiteralPath (Join-Path $here "payloads\$id\stack.json")
  $kept = if ($existing.ContainsKey($dirKey)) { $existing[$dirKey] } elseif ($hasConfig -and $sv.implicit) { "/home/${id}_live" } else { '' }
  if ($kept) {
    $stackDir[$id] = $kept; $keptAny = $true
    Write-Host "  $id dir : $kept (kept -- a server's folder changes only through a relocation: the launcher's Server page, or the command named once the agent is installed)"
    $k = (Get-RemoteKinds @($kept))[$kept]
    if ($k -ne 'stack' -and $foundStack.ContainsKey($id) -and $found.ContainsKey($id) -and "$($found[$id])".TrimEnd('/') -cne $kept.TrimEnd('/')) {
      Write-Host "  note    : a $id server is in $($found[$id]) -- $id keeps its folder $kept (to use that server, re-point $id at it: the launcher's Server page, or the command named once the agent is installed)"
      $relocHints += , @($id, $found[$id])
    }
    if ($downloadable -and $k -in @('absent', 'empty')) { $stackFetch[$id] = (Ask "The $id folder $kept holds no server yet. Download the package into it now? (y/n)" 'n') -in @('y', 'yes') }
    continue
  }
  $def = if ($foundStack.ContainsKey($id) -and -not $existing.ContainsKey($dirKey)) { 'h' } else { 'l' }
  $q = if ($downloadable) { $p = Get-PackagePhrase $id; if (-not $p) { $p = 'a ready-made package' }; "$id ($p) -- d / h / l" } else { "$id (no ready-made package is known for it) -- h / l" }
  $c = ''
  while (-not $c) {
    $a = "$(Ask $q $def)".Trim().ToLowerInvariant()
    if ($a -in @('d', 'download')) {
      if ($downloadable) { $c = 'd' } else { Write-Host "  no ready-made package is known for $id -- answer h or l"; $def = 'l' }
    }
    elseif ($a -in @('h', 'have')) { $c = 'h' }
    elseif ($a -in @('l', 'later', '')) { $c = 'l' }
    else { Write-Host "  answer d, h or l"; $def = 'l' }
  }
  if ($c -eq 'd') {
    $dd = if ($found.ContainsKey($id)) { $found[$id] } else { "/home/${id}_live" }
    while ($true) {
      $d = "$(Ask "Install the $id server into (docker compose dir; '-' = not now)" $dd)".Trim()
      if (-not $d) { Write-Host "  no folder given -- $id is not set up now"; break }
      if (-not $d.StartsWith('/')) { Write-Warning "The folder must be an absolute path (it starts with /)."; $dd = ''; continue }
      $k = (Get-RemoteKinds @($d))[$d]
      if ($k -in @('absent', 'empty', 'unknown')) { $stackDir[$id] = $d; $stackFetch[$id] = $true; break }
      if ($k -eq 'stack') { Write-Host "  note    : $d already holds a server stack -- the download will be skipped"; $stackDir[$id] = $d; break }
      Write-Warning "$d holds other files and no server stack -- choose an empty or a new folder."; $dd = ''
    }
  }
  elseif ($c -eq 'h') {
    $dh = if ($foundStack.ContainsKey($id)) { $found[$id] } else { '' }
    while ($true) {
      $d = "$(Ask "Folder of the extracted $id server (docker compose dir; '-' = not now)" $dh)".Trim()
      if (-not $d) { Write-Host "  no folder given -- $id is not set up now"; break }
      if (-not $d.StartsWith('/')) { Write-Warning "The folder must be an absolute path (it starts with /)."; $dh = ''; continue }
      $k = (Get-RemoteKinds @($d))[$d]
      if ($k -in @('stack', 'unknown')) { $stackDir[$id] = $d; break }
      Write-Warning "$d holds no docker-compose.yml(.tmpl) -- not an extracted server."; $dh = ''
    }
  }
  if (-not $stackDir[$id] -and $foundStack.ContainsKey($id)) { Write-Host "  note    : a $id server is in $($found[$id]) -- not set up now" }
}
$fetchIds = @($stackVersions | ForEach-Object { $_.id } | Where-Object { $stackFetch[$_] })
# Whether unpacking the server package of a version takes a 7z tool on the box. It does not when the
# package's catalogue -- payloads\<id>\stack.json beside this script -- names a .zip archive, as its
# "url" or in an "alt" entry: the agent reads a .zip by itself. An address names a .zip when it ends in
# .zip in front of its query or fragment, in any letter case. A catalogue that cannot be read counts as
# "takes one": a file that is not UTF-8 or starts with a byte order mark, a text that is not one JSON
# object, an "alt" that is not a list of objects. The rule of needs_extractor in install_agent.sh, with
# the same address pattern -- so the file is read as bytes (Get-Content would drop a byte order mark and
# decode anything), the text must open with a brace (PowerShell 7 hands a one-entry JSON list back as
# its entry) and a member is taken by its exact name ($cat.url would also find "URL", and would hand a
# one-entry list back as its entry). The two differ on one kind of text only: ConvertFrom-Json also takes
# texts that are not strict JSON (single-quoted strings, an unquoted key), which the agent and
# install_agent.sh do not read.
function Test-NeedsExtractor([string]$id) {
  try {
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $here "payloads\$id\stack.json"))
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { return $true }
    $text = (New-Object System.Text.UTF8Encoding($false, $true)).GetString($bytes)
    if ($text -notmatch '^[ \t\r\n]*\{') { return $true }
    $cat = $text | ConvertFrom-Json
    $member = { param($object, $name) foreach ($p in $object.PSObject.Properties) { if ($p.Name -ceq $name) { return $p } } }
    $entries = @($cat)
    $alt = & $member $cat 'alt'
    if ($alt) {
      if ($alt.Value -isnot [array]) { return $true }
      foreach ($entry in $alt.Value) {
        if ($entry -isnot [System.Management.Automation.PSCustomObject]) { return $true }
        $entries += $entry
      }
    }
    foreach ($entry in $entries) {
      $url = & $member $entry 'url'
      if ($url -and $url.Value -is [string] -and $url.Value -cmatch '^[^?#]*\.[zZ][iI][pP]([?#]|$)') { return $false }
    }
  } catch { }
  return $true
}
$fetchNeedsTool = @($fetchIds | Where-Object { Test-NeedsExtractor $_ }).Count -gt 0
# Offered in every run: the archive.org bundles of the hotpatch mirror are .7z, and so is the smaller
# archive of a server package that also comes as a .zip.
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
if ($keepEnv -and $fetchIds.Count -gt 0) { Write-Host "  note    : the downloaded package comes with the vendor's .env (ports on 127.0.0.1, the published passwords) -- it is kept as it is; edit that .env before Prepare server to let other machines connect" }
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
  if ($hasConfig -and $advIp -cne $oldAdvIp -and $advHost -ceq $oldAdvHost) {
    Write-Host "  note    : the advertised IP given now replaces the stored DDNS host ($advHost)"; $advHost = ''
  } elseif ($hasConfig -and $advHost -cne $oldAdvHost -and $advIp -ceq $oldAdvIp) {
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
$useStoredBind = $hasConfig -and -not ($keepStored -and -not (DefOr 'GIO_BIND_IP' ''))
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
# archive.org bundles (default -- one pinned download) or, as `cdn`, file by file from the sources
# each version's manifest names. This seeds the agent's policy field `hotpatchSource`; once the admin
# picks a source in the launcher's hotpatch card, that stored choice wins and this value is no longer
# consulted.
$hpSource = (DefOr 'GIO_HOTPATCH_SOURCE' 'archive')
do {
  $hpSource = (Ask 'Hotpatch mirror source (archive|cdn)' $hpSource).Trim().ToLowerInvariant()
  if ($hpSource -notin @('archive','cdn')) { Write-Warning "Type 'archive' (the archive.org bundles) or 'cdn' (file by file from the sources each version's manifest names)." }
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
$modeDef  = if ($hasConfig -and ($exListen -eq '' -or $exHost -in @('127.0.0.1', 'localhost', '::1'))) { 'tunnel' } else { 'direct' }
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
$keepListen = $hasConfig -and ($exListen -ne '') -and ($agPort -eq $exPort) -and ($mode -eq $modeDef) -and ($exHost -notin @('', '0.0.0.0', '127.0.0.1', 'localhost', '::1'))
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
$dirSum = ''
if ($hasConfig) {
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
  # The configuration was read at the start of this run, before every question: a stack folder relocated
  # meanwhile (from a launcher, or --relocate on the box) stops the run here, before anything is replaced --
  # writing this run's configuration would undo the relocation. The lines are read again the way $existing
  # read them; the box also hands over a fingerprint of them ($dirSum), which the provisioning script
  # compares once more right before it writes the configuration (the upload in between takes a while).
  $dirsNow = @(Invoke-Remote @'
{ L=$(grep -E '^[[:space:]]*GIO_DIR_[0-9]+[[:space:]]*=' /etc/gio-agent/config || true)
  printf '%s\n' "$L" | sed 's/^/DIR /'
  printf 'DIRSUM %s\n' "$(printf '%s' "$L" | sha256sum | cut -c1-64)"; } 2>/dev/null; true
'@)
  $late = @{}; $sumSeen = $false
  foreach ($line in $dirsNow) {
    if ("$line" -match '^DIRSUM ([0-9a-f]{64})?\s*$') { $sumSeen = $true; $dirSum = "$($Matches[1])" }
    elseif ("$line" -match '^DIR \s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') {
      $k = $Matches[1]; $v = $Matches[2].Trim()
      if ($v -match '^"(.*)"$' -or $v -match "^'(.*)'$") { $v = $Matches[1] }
      $late[$k] = $v
    }
  }
  if (-not $sumSeen) { Write-Error "No answer from $sshHost about the stack folders in /etc/gio-agent/config -- run this again (nothing was changed)."; exit 1 }
  $dirsMoved = @($stackVersions | ForEach-Object { DirKey $_.id } | Where-Object { $existing.ContainsKey($_) -ne $late.ContainsKey($_) -or ($existing.ContainsKey($_) -and $existing[$_] -cne $late[$_]) } | ForEach-Object {
    $_ + ': ' + $(if ($existing.ContainsKey($_)) { "'" + $existing[$_] + "'" } else { '(not set)' }) + ' -> ' + $(if ($late.ContainsKey($_)) { "'" + $late[$_] + "'" } else { '(not set)' }) })
  if ($dirsMoved.Count -gt 0) { Write-Error "The stack folders in /etc/gio-agent/config on $sshHost changed while this ran ($($dirsMoved -join '; ')) -- a relocation, from a launcher or with --relocate on the box? Writing this run's configuration would undo it; nothing was changed. Run this again: a folder the box uses is kept."; exit 1 }
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
  Write-Host "Uploading the payloads (saves, fixes, templates)..." -ForegroundColor Cyan
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
  Write-Warning "agent\payloads is missing -- the agent will have no save, fixes or templates to prepare a server with."
}
# Distro-neutral: firewalld (Alma/RHEL) or ufw (Ubuntu); with neither active there is nothing to
# open, but say so explicitly -- silence would look identical to a ufw that blocks the port.
$openPort = if ($tunnelMode) { "" } else {
  "if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then firewall-cmd --permanent --add-port=$agPort/tcp && firewall-cmd --reload && echo 'firewalld: port $agPort/tcp opened'; elif command -v ufw >/dev/null 2>&1 && LC_ALL=C ufw status 2>/dev/null | grep -q '^Status: active'; then ufw allow $agPort/tcp; else echo 'no local firewall active (firewalld/ufw) - port $agPort is reachable unless an external firewall filters it'; fi"
}
$provMode  = DefOr 'GIO_PROVISION_MODE' 'once'
$txtMode   = DefOr 'GIO_TXT_FIXES_MODE' 'now'
$statePath = DefOr 'GIO_STATE_PATH' '/var/lib/gio-agent/state.json'
# The stack folders' config lines, in row order, and their keys for $managed. An implicit row is always
# written: its empty value is what says "not on this box" where the agent would assume a folder. Any
# other row is written once it has a folder, or when the box's config already stores its key, even
# empty. A key the box never held stays out of the file, so a later run still takes a stack folder that
# turns up.
$dirKeys = @($stackVersions | ForEach-Object { DirKey $_.id })
$dirLines = (@($stackVersions | Where-Object { $_.implicit -or $stackDir[$_.id] -or $existing.ContainsKey((DirKey $_.id)) } | ForEach-Object { (DirKey $_.id) + '=' + $stackDir[$_.id] })) -join "`n"
# Keys this script does not manage (GIO_TRUST_PROXY, the GIO_DIR_<nn> of a version the rows above do not
# hold, anything added by hand) survive a re-deploy verbatim.
$managed = @('GIO_AGENT_TOKEN','GIO_AGENT_LISTEN') + $dirKeys + @('GIO_KEEP_ENV','GIO_MUIP_HOST','GIO_AGENT_REGION','GIO_MUIP_KEY','GIO_MYSQL_ROOT_PASSWORD','GIO_FLASK_SECRET_KEY','GIO_ADVERTISED_IP','GIO_ADVERTISED_HOST','GIO_BIND_IP','GIO_SERVER_NAME','GIO_PAYLOAD_DIR','GIO_STATE_PATH','GIO_PROVISION_MODE','GIO_TXT_FIXES_MODE','GIO_HOTPATCH_SOURCE')
$extraKeys = (@($existing.Keys | Where-Object { $_ -notin $managed } | Sort-Object | ForEach-Object { "$_=$($existing[$_])" })) -join "`n"
# <<'EOF' (quoted): $remote is ALREADY PowerShell-interpolated, so the remote shell must NOT expand
# a second time -- a token/path containing $ would otherwise be mangled (or a backtick executed).
# set -e: without it, plink's exit code is only the LAST line's (systemctl status = 0 if the service
# is active), so a failed selftest would pass unnoticed and Assert-Ok would report success.
# The config is created 0600 BEFORE its first byte (the `install -m 600 /dev/null` line): on a fresh
# box the redirection would create it 0644 and leave it so until the chmod -- with the token and the
# stack secrets already in it. An existing file is rewritten in place and keeps its mode.
$upgradeFlag = if ($hasConfig) { 1 } else { 0 }
# The 7z extractor: the archive.org bundles of the hotpatch mirror need one, and so does a server
# package that comes only as a .7z; NON-FATAL either way (the agent works without it, file by file from
# the sources each version's manifest names, and refuses such a package download with a clear 409
# until a tool exists). NEEDS_7Z on the box = $fetchNeedsTool: a download chosen above takes one,
# which turns the warning into the louder one.
# Single-quoted here-string: its $t and $(...) are bash's, not PowerShell's, and $remote's
# interpolation inserts it verbatim.
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
  elif [ "$NEEDS_7Z" = 1 ]; then echo '  WARNING : no 7z/bsdtar tool -- the server package download will be refused until one is installed (apt install 7zip | dnf install bsdtar), then run the download again'
  else echo "  warning : no 7z/bsdtar tool -- the hotpatch mirror is filled file by file from the sources each version's manifest names and a server package is downloaded only where it comes as a .zip (apt install 7zip | dnf install bsdtar)"; fi
fi
'@
$install7zFlag = if ($install7z) { 1 } else { 0 }
$needs7zFlag = if ($fetchNeedsTool) { 1 } else { 0 }
$keepEnvFlag = if ($keepEnv) { 1 } else { 0 }
# DIRSUM_SEEN = the fingerprint of the stack folder lines the folder check above read on the box ('' on a first
# deploy): changed since (a relocation that ended while the files were uploaded), the script stops with 85 before
# it writes anything -- the configuration it carries would undo that relocation.
$remote = @"
set -e
UPGRADE=$upgradeFlag
DIRSUM_SEEN=$dirSum
if [ -n "`$DIRSUM_SEEN" ] && [ "`$(printf '%s' "`$(grep -E '^[[:space:]]*GIO_DIR_[0-9]+[[:space:]]*=' /etc/gio-agent/config 2>/dev/null || true)" | sha256sum | cut -c1-64)" != "`$DIRSUM_SEEN" ]; then echo '  the stack folders in /etc/gio-agent/config changed while the files were uploaded -- the configuration is not written'; exit 85; fi
WAS_ACTIVE=`$(systemctl is-active gio-agent 2>/dev/null || true); WAS_ENABLED=`$(systemctl is-enabled gio-agent 2>/dev/null || true)
if [ -f /opt/gio-agent/uninstall_agent.sh ]; then sed -i 's/\r`$//' /opt/gio-agent/uninstall_agent.sh; chmod 755 /opt/gio-agent/uninstall_agent.sh; fi
[ -e /etc/gio-agent/config ] || install -m 600 /dev/null /etc/gio-agent/config
cat > /etc/gio-agent/config <<'EOF'
GIO_AGENT_TOKEN=$token
GIO_AGENT_LISTEN=$bind
$dirLines
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
NEEDS_7Z=$needs7zFlag
$extractorCheck
"`$PY3" /opt/gio-agent/gio_agent.py --selftest
systemctl daemon-reload
if [ "`$UPGRADE" = 1 ] && [ "`$WAS_ENABLED" = disabled ]; then echo "  service : left disabled, as it was (systemctl enable gio-agent to start it at boot)"; else systemctl enable gio-agent; fi
if [ "`$UPGRADE" = 1 ] && [ "`$WAS_ACTIVE" = inactive ]; then echo "  service : was stopped -- left stopped, as it was (systemctl start gio-agent)"; else systemctl restart gio-agent && sleep 1 && systemctl --no-pager --lines=4 status gio-agent; fi
"@
# A CRLF checkout would inject \r into the remote script (bash: "command not found") -- always strip them.
Invoke-Remote $remote
if ($LASTEXITCODE -eq 85) { Write-Error "The stack folders in /etc/gio-agent/config on $sshHost changed while the files were uploaded -- a relocation, from a launcher or with --relocate on the box? Writing this run's configuration would undo it: the new agent files are on the box, but its configuration was not written and the agent was not restarted. Run this again: a folder the box uses is kept."; exit 1 }
Assert-Ok "provisioning the agent on the server"
# The relocation commands, now that the agent file on the box knows --relocate.
if ($keptAny) {
  Write-Host "  folders : a server's folder changes only through a relocation -- the launcher's Server page, or on the box:"
  Write-Host "            python3 /opt/gio-agent/gio_agent.py --config /etc/gio-agent/config --relocate <version> <folder>   (add --mode move to take the server's files along)"
  foreach ($h in $relocHints) { Write-Host "            python3 /opt/gio-agent/gio_agent.py --config /etc/gio-agent/config --relocate $($h[0]) $($h[1])" }
}

# The server package download(s) chosen above: the RUNNING agent does the work (job
# `fetch`: resumable, verified, extracted, healed); the --fetch CLI only asks for it and streams the
# job log, which plink relays live. Non-fatal: the agent is installed either way, and the command
# printed here continues where it stopped.
if ($fetchIds.Count -gt 0) {
  $fetchCmd = "python3 /opt/gio-agent/gio_agent.py --config /etc/gio-agent/config --fetch $($fetchIds -join ' ')"
  Write-Host ""
  Write-Host "Downloading the server package(s) $($fetchIds -join ', ') from archive.org on the box (Ctrl+C stops following, not the download; rerun on the box: $fetchCmd)" -ForegroundColor Cyan
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
