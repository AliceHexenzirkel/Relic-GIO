<#
  Publishes Relic as a self-contained single-file win-x64 app and fetches the WebView2
  Evergreen bootstrapper for the installer. Run from the repo root:  .\build\publish.ps1

  Baked-in defaults (all optional — the start screen asks for whatever is not baked):
    -DefaultServer  host[:port]            one default server (shown pre-selected on the start screen)
    -DefaultServers "Label=host[:port];…"  several default servers, offered as quick picks
    -Token <agent bearer token>            admin token for Server Admin mode; default: contents of
                                           config\agent.token if it exists. NEVER bake a token into a
                                           build handed to players — it is the only security boundary.
    -DefaultMode player|admin              start screen preselects this mode
    -Theme summer|classic                  default launcher theme baked into ui/index.html
    -Drive                                 keep Google Drive ids in the shipped config\versions.json
                                           (default: they are stripped — Drive is for private builds)
    -NavmeshDir <dir>                      the OPTIONAL pathfindingserver navmesh bundle (docs\NAVMESH.md):
                                           every <dir>\<ver>\navmesh.json + NavMesh\ ships as
                                           agent\payloads\<ver>\navmesh\. Default: <repo>\_bundled_navmesh;
                                           a missing folder is normal (the server keeps its own set).
  GNU style works too:  .\build\publish.ps1 --default-server=game.example.com --token=abc --theme=classic
                        (--navmesh=<dir> is -NavmeshDir). Started through `pwsh -File`, a value that holds a
                        colon (host:port, C:\dir) is split at it by pwsh's own command line: use the -Parameter
                        form there, or call the script from inside PowerShell as build.bat and CI do.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
  [string]$Configuration = 'Release',
  [string]$Runtime = 'win-x64',
  [string]$DefaultServer = '',
  [string]$DefaultServers = '',
  [string]$Token = '',
  [string]$DefaultMode = '',
  [string]$Theme = '',
  [switch]$Drive,
  [string]$NavmeshDir = '',
  [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo

# GNU-style aliases (--default-server=x / --token=y), so the documented syntax works verbatim.
foreach ($a in @($Rest)) {
  if ($a -match '^--default-server=(.+)$') { $DefaultServer = $Matches[1] }
  elseif ($a -match '^--default-servers=(.+)$') { $DefaultServers = $Matches[1] }
  elseif ($a -match '^--token=(.+)$') { $Token = $Matches[1] }
  elseif ($a -match '^--default-mode=(.+)$') { $DefaultMode = $Matches[1] }
  elseif ($a -match '^--theme=(.+)$') { $Theme = $Matches[1] }
  elseif ($a -match '^--navmesh=(.+)$') { $NavmeshDir = $Matches[1] }
  elseif ($a -eq '--drive') { $Drive = $true }
  elseif ($a) { throw "unknown argument: $a" }
}
if (-not $NavmeshDir) { $NavmeshDir = Join-Path $repo '_bundled_navmesh' }
# Normalized BEFORE validating: -notin is case-insensitive, so 'Classic' would pass and then be
# baked verbatim — and body.className='theme-Classic' matches no CSS rule (class names are case-sensitive).
if ($Theme) { $Theme = $Theme.ToLowerInvariant() }
if ($Theme -and $Theme -notin @('summer', 'classic')) { throw "--theme accepts only 'summer' or 'classic' (got: $Theme)" }
if ($DefaultMode) { $DefaultMode = $DefaultMode.ToLowerInvariant() }
if ($DefaultMode -and $DefaultMode -notin @('player', 'admin')) { throw "--default-mode accepts only 'player' or 'admin' (got: $DefaultMode)" }
if (-not $Token) {
  $tokenFile = Join-Path $repo 'config\agent.token'
  if (Test-Path $tokenFile) {
    # Read first, THEN decide: on an EMPTY file -Raw yields no value at all, and .Trim() on it kills
    # the build with "You cannot call a method on a null-valued expression". An empty agent.token is a
    # NORMAL leftover (get_token.bat creates it by redirection before the ssh call runs).
    $raw = Get-Content $tokenFile -Raw
    $Token = if ($raw) { $raw.Trim() } else { '' }
    if (-not $Token) { Write-Warning 'config\agent.token is empty - continuing WITHOUT a baked token (player-only build).' }
  }
}

# Default server list -> base64(JSON [{label,host,port}]). MSBuild splits -p: values on ';', so the
# list cannot travel as plain text; base64 is opaque to both MSBuild and cmd.
$serverList = @()
if ($DefaultServer) {
  $serverList += @{ label = ''; host = $DefaultServer }
}
if ($DefaultServers) {
  foreach ($item in ($DefaultServers -split ';')) {
    $item = $item.Trim(); if (-not $item) { continue }
    $eq = $item.IndexOf('=')
    if ($eq -gt 0) { $serverList += @{ label = $item.Substring(0, $eq).Trim(); host = $item.Substring($eq + 1).Trim() } }
    else { $serverList += @{ label = ''; host = $item } }
  }
}
$serversB64 = ''
if ($serverList.Count -gt 0) {
  $json = ($serverList | ConvertTo-Json -Compress -Depth 3)
  if ($serverList.Count -eq 1) { $json = "[$json]" }   # ConvertTo-Json unwraps single-element arrays
  $serversB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
}

# SHA256 without Get-FileHash (a Windows PowerShell 5.1 started from PowerShell 7 inherits PS7's
# PSModulePath and cannot load Microsoft.PowerShell.Utility — the failure would surface HALFWAY
# through the build; .NET has no such problem).
function Get-Sha256([string]$path) {
  $sha = [Security.Cryptography.SHA256]::Create()
  try {
    $fs = [IO.File]::OpenRead($path)
    try { return (($sha.ComputeHash($fs) | ForEach-Object { $_.ToString('X2') }) -join '') }
    finally { $fs.Dispose() }
  }
  finally { $sha.Dispose() }
}

# i18n gate: every key referenced in code must exist in ui/lang/en.json, index.json must be sane and
# ui/lang/en.js must match en.json (the script regenerates it). Needs python 3 on the build machine.
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { throw "python 3 is required on the build machine (build\check_i18n.py validates the translations)" }
& $python.Source (Join-Path $repo 'build\check_i18n.py') --strict
if ($LASTEXITCODE -ne 0) { throw "i18n check failed (see messages above)" }

$props = @()
if ($DefaultServer) { $props += "-p:RelicDefaultServer=$DefaultServer" }
if ($serversB64) { $props += "-p:RelicDefaultServersB64=$serversB64" }
if ($Token) { $props += "-p:RelicAgentToken=$Token" }
if ($DefaultMode) { $props += "-p:RelicDefaultMode=$DefaultMode" }
$serverTxt = if ($serverList.Count -gt 0) { ($serverList | ForEach-Object { if ($_.label) { "$($_.label)=$($_.host)" } else { $_.host } }) -join ', ' } else { '(none - the start screen asks for one)' }
$tokenTxt = if ($Token) { $Token.Substring(0, [Math]::Min(6, $Token.Length)) + '... (baked in - ADMIN build)' } else { 'none (player build)' }
Write-Host "==> Default server(s): $serverTxt" -ForegroundColor Yellow
Write-Host "==> Agent token:       $tokenTxt" -ForegroundColor Yellow
if ($DefaultMode) { Write-Host "==> Default mode:      $DefaultMode" -ForegroundColor Yellow }

Write-Host "==> Publishing self-contained single-file ($Runtime)..." -ForegroundColor Cyan
$outDir = Join-Path $repo "publish\$Runtime"
if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }

dotnet publish app/Relic.App/Relic.App.csproj -c $Configuration -r $Runtime --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  @props `
  -o $outDir
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Trim debug/doc leftovers so the installer only ships runtime files.
Get-ChildItem $outDir -Include *.pdb, *.xml -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

# Archived translations (*.bak, e.g. ui/lang/ro.json.bak) must never ship: the csproj excludes them,
# this is the second net.
$baks = @(Get-ChildItem $outDir -Recurse -Filter *.bak -ErrorAction SilentlyContinue)
if ($baks.Count -gt 0) { throw "archived translation files reached the output: $($baks.FullName -join ', ')" }
foreach ($must in @('ui\lang\en.json', 'ui\lang\en.js', 'ui\lang\index.json', 'ui\i18n.js')) {
  if (-not (Test-Path (Join-Path $outDir $must))) { throw "missing from the publish output: $must" }
}

# Every read-modify-write below reads the file as UTF-8 EXPLICITLY: build.bat runs this script under
# Windows PowerShell 5.1, whose bare Get-Content reads a BOM-less UTF-8 file as ANSI, and the UTF-8
# write that follows then double-encodes every non-ASCII character (the catalogue's em dashes
# would ship as "â€”" on the version cards).

# Bake the default theme: rewrite the marked "var def = '...'" line in the PUBLISHED ui/index.html
# (the repo copy stays untouched). The theme is pure front-end, so this is the whole mechanism.
if ($Theme) {
  $uiIndex = Join-Path $outDir 'ui\index.html'
  $html = [IO.File]::ReadAllText($uiIndex, [Text.UTF8Encoding]::new($false))
  $patched = $html -replace "var def = '(summer|classic)'; /\*RELIC-DEFAULT-THEME\*/", "var def = '$Theme'; /*RELIC-DEFAULT-THEME*/"
  if ($patched -eq $html -and $html -notmatch "var def = '$Theme'; /\*RELIC-DEFAULT-THEME\*/") {
    throw "RELIC-DEFAULT-THEME marker not found in ui/index.html - the theme could not be baked"
  }
  [IO.File]::WriteAllText($uiIndex, $patched, [Text.UTF8Encoding]::new($false))
  Write-Host "==> Default theme:     $Theme (baked into ui/index.html)" -ForegroundColor Yellow
}

# Google Drive ids: the shipped catalogue carries them only for private (--drive) builds. A plain
# regex edit, not a JSON round-trip (ConvertTo-Json's default depth would mangle the mirrors arrays).
$versionsOut = Join-Path $outDir 'config\versions.json'
if (Test-Path $versionsOut) {
  if (-not $Drive) {
    $txt = [IO.File]::ReadAllText($versionsOut, [Text.UTF8Encoding]::new($false))
    $stripped = [regex]::Replace($txt, '"driveId"\s*:\s*"[^"]*"', '"driveId": ""')
    [IO.File]::WriteAllText($versionsOut, $stripped, [Text.UTF8Encoding]::new($false))
    if ($stripped -match '"driveId"\s*:\s*"[^"]+"') { throw "a Drive id survived the strip in config\versions.json" }
    # Encoding guard: outside the Drive ids the shipped catalogue must equal the repo's character for
    # character (a bare Get-Content under PowerShell 5.1 would double-encode it, see above).
    $srcCatalogue = Join-Path $repo 'config\versions.json'
    if (Test-Path $srcCatalogue) {
      $srcStripped = [regex]::Replace([IO.File]::ReadAllText($srcCatalogue, [Text.UTF8Encoding]::new($false)), '"driveId"\s*:\s*"[^"]*"', '"driveId": ""')
      if ($srcStripped -cne $stripped) { throw "config\versions.json: the shipped catalogue differs from the repo's beyond the Drive ids (encoding drift?)" }
    }
    Write-Host "==> Google Drive:      ids stripped from the shipped catalogue (use --drive to keep them)" -ForegroundColor Yellow
  } else {
    Write-Host "==> Google Drive:      ids KEPT in the shipped catalogue (--drive)" -ForegroundColor Yellow
  }
  # Sanity: a shipped version with no voice packs would install a mute game. A legacy 'audio' entry
  # still counts — the loader reads it as voices.English(US). Read-only parse: the file itself is only
  # ever edited by the regex above (ConvertTo-Json would mangle the nested arrays).
  $catalogue = Get-Content $versionsOut -Raw | ConvertFrom-Json
  foreach ($v in @($catalogue.versions)) {
    if (-not $v.voices -and -not $v.audio) {
      throw "config\versions.json: version $($v.id) carries no voice packs ('voices' or the legacy 'audio')"
    }
  }
}

# Copied here rather than left to the csproj's <None> glob: `dotnet publish` reliably drops
# payload\common\ayy\anime\build\* (the two PE files) even though a plain build copies them, so the
# ship build would silently lose the Win11 injector. A straight mirror has no such opinions.
Copy-Item (Join-Path $repo 'payload') -Destination $outDir -Recurse -Force

# The 2.8 client does not start without its patched global-metadata.dat, and on Windows 11 no client
# starts without the injector. Catch a payload that failed to publish HERE, not on a user's machine
# after a 30 GB download.
$manifestPath = Join-Path $outDir 'payload\manifest.json'
if (-not (Test-Path $manifestPath)) { throw "payload\manifest.json is missing from the publish output - the 2.8 client would not start" }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$entries = @($manifest.common) + @($manifest.versions.PSObject.Properties.Value | ForEach-Object { $_ })
foreach ($e in $entries) {
  $f = Join-Path $outDir "payload\$($e.src -replace '/', '\')"
  if (-not (Test-Path $f)) { throw "payload file missing from the publish output: $($e.src)" }
  if ($e.sha256) {
    $got = Get-Sha256 $f
    if ($got -ne $e.sha256.ToUpperInvariant()) { throw "payload altered: $($e.src) has sha256 $got, the manifest expects $($e.sha256)" }
  }
}
Write-Host "==> Client payload:    $($entries.Count) files verified (sha256)" -ForegroundColor Yellow
# No .exe / .dll of the payload may import a Visual C++ runtime DLL: such a file depends on the Visual
# C++ redistributable of the player's PC. Without one (a freshly installed Windows) launcher.exe does not
# start - on Windows 11 no client would - and a DLL handed to it is silently absent from the game. With
# an old one a mhynot2.dll linked that way faults inside msvcp140.dll at the game's first
# DeviceIoControl, after launcher.exe has reported success (its std::mutex needs a newer runtime:
# msvcp140.dll 14.31 faults, 14.38 does not). Linked with the static C runtime the files carry what they
# need (build\build_dlls.ps1).
. (Join-Path $PSScriptRoot 'pe_imports.ps1')
$needRuntime = @(Get-PayloadVcRuntimeProblems (Join-Path $outDir 'payload') $entries)
if ($needRuntime.Count -gt 0) {
  throw "payload imports a Visual C++ runtime DLL: $($needRuntime -join '; '). Build these files with the static C runtime (build\build_dlls.ps1)."
}
$nativeCount = @($entries | Where-Object { "$($_.dst)" -match '\.(exe|dll)$' }).Count
Write-Host "==> Client payload:    $nativeCount native files, none imports a Visual C++ runtime DLL" -ForegroundColor Yellow
# The in-game enhancements DLL(s) — manifest entries flagged "inject" — are the heaviest optional
# payload: report their size here so an unexpectedly fat build (debug DLL, un-dieted resources) is
# noticed at publish time, not after the players downloaded it. Informational only.
foreach ($e in $entries) {
  if (-not $e.inject) { continue }
  $f = Join-Path $outDir "payload\$($e.src -replace '/', '\')"
  $mb = [math]::Round((Get-Item $f).Length / 1MB, 1)
  Write-Host "==> Inject payload:    $($e.src) = $mb MB" -ForegroundColor Yellow
  if ($mb -gt 80) { Write-Warning "inject payload $($e.src) is $mb MB (> 80 MB) - is the Release, resource-dieted DLL the one in payload\? (see docs\ENTERTAINMENT-EXPERIENCE.md)" }
}
if (-not (Test-Path (Join-Path $outDir 'agent\payloads\1.6\manifest.json'))) {
  Write-Warning "agent\payloads is missing from the publish output - 'Install the agent' could not provision the GAA progress."
}

# The OPTIONAL navmesh bundle for the pathfindingserver (docs\NAVMESH.md): only the files that ADD to or
# REPLACE the server's own navmesh set, never the whole set (build\navmesh_tool.py pack leaves out what
# the stack already ships). Mirrored as agent\payloads\<ver>\navmesh\, where the agent takes it from at
# Prepare / Start, and gated like the client payload — every entry's size + sha256 against navmesh.json —
# so an altered or half-copied bundle fails HERE, not on the server. A missing folder is normal.
$navmeshBytes = 0
$navmeshShipped = @()
if (Test-Path $NavmeshDir) {
  foreach ($verDir in @(Get-ChildItem $NavmeshDir -Directory)) {
    $nm = Join-Path $verDir.FullName 'navmesh.json'
    if (-not (Test-Path $nm)) { continue }
    $ver = $verDir.Name
    if (-not (Test-Path (Join-Path $outDir "agent\payloads\$ver\manifest.json"))) {
      throw "navmesh bundle '$ver': agent\payloads knows no such version (a bundle folder is named after its version: 1.6, 2.8)"
    }
    $m = [IO.File]::ReadAllText($nm, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
    if ($m.format -ne 1) { throw "navmesh bundle ${ver}: navmesh.json has format '$($m.format)', this build understands format 1" }
    if ($m.version -ne $ver) { throw "navmesh bundle ${ver}: navmesh.json says version '$($m.version)'" }
    $files = @($m.files)
    if ($files.Count -eq 0) { throw "navmesh bundle ${ver}: navmesh.json lists no files" }
    $dst = Join-Path $outDir "agent\payloads\$ver\navmesh"
    if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
    New-Item -ItemType Directory -Force -Path (Join-Path $dst 'NavMesh') | Out-Null
    Copy-Item $nm -Destination (Join-Path $dst 'navmesh.json') -Force
    $bytes = 0
    foreach ($e in $files) {
      # The agent's grammar for a name, exactly (NAVMESH_NAME_RE + NAVMESH_NAME_MAX in agent\gio_agent.py): the server
      # splits the stem on '_' and loads only 'scene' / 'scenepolygon' names (case-sensitive on Linux). [0-9], not \d --
      # .NET's \d also matches the digits of other scripts --, and \z, not $, so a name ending in a newline fails here too.
      $name = [string]$e.name
      if ($name.Length -gt 128 -or $name -cnotmatch '^(?:scene_[0-9]+(?:_[0-9]+)?(?:_-?[0-9]+_-?[0-9]+)?|scenepolygon_[0-9]+_[0-9]+_[0-9]+)\.navmesh\z') { throw "navmesh bundle ${ver}: '$name' is not a file name the pathfindingserver loads" }
      $src = Join-Path $verDir.FullName "NavMesh\$($e.name)"
      if (-not (Test-Path $src)) { throw "navmesh bundle ${ver}: $($e.name) is listed in navmesh.json but missing from NavMesh\" }
      $f = Join-Path $dst "NavMesh\$($e.name)"
      Copy-Item $src -Destination $f -Force
      $len = (Get-Item $f).Length
      if ($len -ne [long]$e.size) { throw "navmesh bundle ${ver}: $($e.name) is $len bytes, navmesh.json expects $($e.size)" }
      $got = Get-Sha256 $f
      if ($got -ne ([string]$e.sha256).ToUpperInvariant()) { throw "navmesh bundle ${ver}: $($e.name) has sha256 $got, navmesh.json expects $($e.sha256)" }
      $bytes += $len
    }
    $unlisted = @(Get-ChildItem (Join-Path $verDir.FullName 'NavMesh') -Filter *.navmesh -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin @($files | ForEach-Object { $_.name }) })
    if ($unlisted.Count -gt 0) { Write-Warning "navmesh bundle ${ver}: $($unlisted.Count) file(s) in NavMesh\ are not listed in navmesh.json and do not ship: $($unlisted.Name -join ', ')" }
    $noun = if ($files.Count -eq 1) { 'file' } else { 'files' }
    Write-Host "==> Bundled navmesh: $ver = $($files.Count) $noun, $([math]::Round($bytes / 1MB, 1)) MB -> agent\payloads\$ver\navmesh" -ForegroundColor Yellow
    $navmeshBytes += $bytes
    $navmeshShipped += $ver
  }
}
if ($navmeshShipped.Count -eq 0) { Write-Host "==> Bundled navmesh: none" -ForegroundColor Yellow }
elseif ($navmeshBytes -gt 400MB) { Write-Warning "the navmesh bundles total $([math]::Round($navmeshBytes / 1MB, 1)) MB (> 400 MB) - a bundle carries only the files the stack does not ship (build\navmesh_tool.py pack --vendor-list), not the vendor's whole set" }

Write-Host "==> Fetching WebView2 Evergreen bootstrapper..." -ForegroundColor Cyan
$redist = Join-Path $repo 'installer\redist'
New-Item -ItemType Directory -Force -Path $redist | Out-Null
$bootstrapper = Join-Path $redist 'MicrosoftEdgeWebview2Setup.exe'
if (-not (Test-Path $bootstrapper)) {
  Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrapper
}

$size = [math]::Round((Get-Item (Join-Path $outDir 'Relic.exe')).Length / 1MB, 1)
Write-Host "`nDone. Relic.exe = $size MB at $outDir" -ForegroundColor Green
Write-Host "Next: compile installer\relic.iss with Inno Setup (iscc installer\relic.iss)."
