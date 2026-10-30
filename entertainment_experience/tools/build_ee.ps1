<#
.SYNOPSIS
  Builds the in-game enhancements DLL (CLibrary.dll) for one or more game versions and, optionally,
  drops each result into payload/<ver>/ee/CLibrary.dll.bin with its sha256 written into payload/manifest.json.

.EXAMPLE
  .\build_ee.ps1 -Versions 28                    # mod\bin\v28\Release-x64\CLibrary.dll
  .\build_ee.ps1 -Versions 16,28,32              # one build per game version: 1.6, 2.8 and 3.2
  .\build_ee.ps1 -Versions 16,28 -CopyToPayload  # + payload\1.6\ee\ and payload\2.8\ee\ (+ manifest sha256)
  .\build_ee.ps1 -Versions 28 -Config Release_WS # the offset-discovery build (pattern scanner)
  .\build_ee.ps1 -Versions 28 -Toolset v143      # the Visual Studio 2022 toolset (GitHub's Windows runners)
  .\build_ee.ps1 -Versions 33 -Clean
  .\build_ee.ps1 -Versions 28 -Config DevTest -StageDev D:\relic-devtest\stage   # the dev-only test bed -> <dir>\2.8\CLibrary.dll

.NOTES
  Toolchain: Visual Studio Build Tools (MSVC toolset v145 or v143) + Windows SDK; MSBuild is located with
  vswhere. The project files name v145; -Toolset hands the choice to MSBuild over them: auto (default) is
  v145 when an MSVC 14.5x toolset is installed under that Visual Studio instance, else v143.
  The DLL must import no Visual C++ runtime DLL (static CRT, see cheat-library.vcxproj) because mhynot2's
  launcher cannot report a failed LoadLibrary; -CopyToPayload refuses one that does (build\pe_imports.ps1).
  -CopyToPayload copies a version's DLL only into an entry payload\manifest.json already lists: a version
  without one is not copied (publish.ps1 ships the folder whole, and the entry is what makes the launcher
  copy, check and inject the file).
  A module whose game file Relic byte-patches (3.2's UserAssembly.dll) is recognised by the word sum of the
  patched file: its entry in res\assembly_checksum-<ver>.json names that file's sha256 as "bytepatchSha256",
  and a build is refused before anything is built when the version's bytepatch entry of payload\manifest.json
  gives another file (the DLL would not recognise the client Relic leaves on disk).
  publish.ps1 never calls this script: the blessed binaries are committed.
  DevTest is the dev-only test bed (docs\ENTERTAINMENT-EXPERIENCE.md, "DevTest"): a Release build with the
  test bed compiled in (RELIC_DEVTEST) and a mark every release gate refuses (Test-DevBuildMarker). It never
  goes to payload\: -CopyToPayload refuses it, and -StageDev copies it into a folder of its own instead -
  never one inside the repository, the installed launcher's folder or the launcher's data folder.
#>
[CmdletBinding()]
param(
    [string[]]$Versions = @('28'),   # accepts -Versions 16,28 as well as '16,28' (powershell -File passes one string)
    [ValidateSet('Release', 'Release_WS', 'Debug', 'DevTest')]
    [string]$Config = 'Release',
    [ValidateSet('auto', 'v145', 'v143')]
    [string]$Toolset = 'auto',
    [switch]$CopyToPayload,
    [string]$StageDev,               # DevTest only: <dir>\<version>\CLibrary.dll (+ its map file)
    [switch]$Clean
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$mod  = Join-Path $here '..\mod' | Resolve-Path
$repo = Join-Path $here '..\..' | Resolve-Path
$sln  = Join-Path $mod 'relic-ee.sln'
$map  = @{ 16 = '1.6'; 28 = '2.8'; 32 = '3.2'; 33 = $null }
. (Join-Path $repo 'build\pe_imports.ps1')

# Refused before anything is built: a DevTest build never reaches payload\ (that folder ships), and the stage
# folder lies outside the repository (payload\ ships, the installer packs publish\ whole, and any other file there
# is one `git add` away from a commit), outside the installed launcher (which puts its own payload back before
# every PLAY anyway) and outside the launcher's own data folder.
if ($CopyToPayload -and $Config -eq 'DevTest') {
    throw 'a DevTest build never goes to payload\ - -CopyToPayload is refused (stage it with -StageDev <dir>)'
}
$stageDir = $null
if ($StageDev) {
    if ($Config -ne 'DevTest') { throw '-StageDev stages a DevTest build only (-Config DevTest)' }
    # A relative path is taken from PowerShell's location; .NET alone would take it from the process's directory.
    $stageDir = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($StageDev)).TrimEnd('\')
    $refused = @([pscustomobject]@{ Path = "$repo"; What = 'the repository' })
    if ($env:LOCALAPPDATA) {
        $refused += [pscustomobject]@{ Path = (Join-Path $env:LOCALAPPDATA 'Programs\Relic'); What = 'the installed launcher' }
        $refused += [pscustomobject]@{ Path = (Join-Path $env:LOCALAPPDATA 'Relic'); What = "the launcher's own data" }
    }
    foreach ($r in $refused) {
        $root = [IO.Path]::GetFullPath($r.Path).TrimEnd('\')
        if ($stageDir.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
            $stageDir.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "-StageDev $stageDir is inside $root ($($r.What)) - a DevTest build is never staged there (D:\relic-devtest\stage is the usual place)"
        }
    }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found ($vswhere) - install VS Build Tools" }
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild.exe not found via vswhere' }
Write-Host "MSBuild: $msbuild"
$vsRoot = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1
$msvcDir = Join-Path $vsRoot 'VC\Tools\MSVC'
$msvcVersions = @(Get-ChildItem $msvcDir -Directory -ErrorAction SilentlyContinue | ForEach-Object Name | Sort-Object -Descending)
if ($Toolset -eq 'auto') {
    $Toolset = if (@($msvcVersions | Where-Object { $_ -match '^14\.5\d' }).Count -gt 0) { 'v145' } else { 'v143' }
    $found = if ($msvcVersions.Count -gt 0) { $msvcVersions -join ', ' } else { 'none' }
    Write-Host "Toolset: $Toolset (auto; MSVC $found under $msvcDir)"
} else {
    Write-Host "Toolset: $Toolset (requested)"
}

$results = @()
$vers = @($Versions | ForEach-Object { $_ -split ',' } | Where-Object { $_ -ne '' } | ForEach-Object { [int]$_ })  # new variable: the [string[]] param would coerce ints back to strings

# payload\manifest.json is only read here (payload_manifest.py is the tool that edits it): every entry with the
# version it is listed under ('' for the common ones).
function Get-ManifestEntries {
    $m = Get-Content -Raw -Encoding UTF8 (Join-Path $repo 'payload\manifest.json') | ConvertFrom-Json
    foreach ($e in @($m.common)) { if ($e) { [pscustomobject]@{ Version = ''; Entry = $e } } }
    foreach ($p in @($m.versions.PSObject.Properties)) {
        foreach ($e in @($p.Value)) { if ($e) { [pscustomobject]@{ Version = $p.Name; Entry = $e } } }
    }
}

# Refused before anything is built: a module the version's byte patch changes is recognised by the word sum of the
# patched file, and "bytepatchSha256" names the file that sum was taken from - the "sha256" of the bytepatch entry
# of that module in payload\manifest.json. Another byte patch gives another file, which the DLL would not recognise.
foreach ($v in $vers) {
    $checksums = Join-Path $mod "cheat-library\res\assembly_checksum-$v.json"
    if (-not $map[$v] -or -not (Test-Path $checksums)) { continue }
    $modules = (Get-Content -Raw -Encoding UTF8 $checksums | ConvertFrom-Json).global.modules
    foreach ($module in @($modules.PSObject.Properties)) {
        $want = "$($module.Value.bytepatchSha256)".ToLowerInvariant()
        if (-not $want) { continue }
        $patches = @(Get-ManifestEntries | Where-Object {
            $_.Version -eq $map[$v] -and $_.Entry.kind -eq 'bytepatch' -and ("$($_.Entry.dst)" -split '/')[-1] -eq $module.Name })
        $gives = @($patches | ForEach-Object { "$($_.Entry.sha256)".ToLowerInvariant() })
        if ($gives -notcontains $want) {
            $now = if ($gives.Count -gt 0) { "the $($map[$v]) byte patch of payload\manifest.json gives sha256 $($gives -join ', ')" }
                   else { "payload\manifest.json names no $($map[$v]) byte patch of $($module.Name)" }
            throw "res\assembly_checksum-$v.json recognises $($module.Name) by the word sum of the byte-patched file (sha256 $want), but $now - a DLL built now would not recognise the client Relic leaves on disk. Write the word sum of the patched file (tools\checksum.py <file>) and its sha256 into the JSON first; nothing was built"
        }
    }
}

foreach ($v in $vers) {
    if (-not $map.ContainsKey($v)) { throw "unknown GameVersion $v (16|28|32|33)" }
    $target = if ($Clean) { 'Rebuild' } else { 'Build' }
    Write-Host "==> GameVersion=$v Config=$Config Toolset=$Toolset ($target)"
    & $msbuild $sln "-t:$target" "-p:Configuration=$Config" '-p:Platform=x64' "-p:GameVersion=$v" "-p:PlatformToolset=$Toolset" '-m' '-nologo' '-v:minimal' '-clp:Summary;ErrorsOnly;WarningsOnly'
    if ($LASTEXITCODE -ne 0) { throw "MSBuild failed for GameVersion=$v (exit $LASTEXITCODE)" }
    $dll = Join-Path $mod "bin\v$v\$Config-x64\CLibrary.dll"
    if (-not (Test-Path $dll)) { throw "expected output missing: $dll" }
    $sha = (Get-FileHash -Algorithm SHA256 $dll).Hash.ToLowerInvariant()
    $mb  = [math]::Round((Get-Item $dll).Length / 1MB, 2)
    $dev = Test-DevBuildMarker $dll
    Write-Host ("    {0}  {1} MB  sha256 {2}{3}" -f $dll, $mb, $sha, $(if ($dev) { '  (DevTest build)' } else { '' }))
    $results += [pscustomobject]@{ Version = $v; Dll = $dll; Sha256 = $sha; MB = $mb; DevTest = $dev }
    if ($Config -eq 'DevTest' -and -not $dev) { throw "the DevTest build of GameVersion $v carries no DevTest mark - the configuration did not reach the resource compiler" }
    if ($Config -ne 'DevTest' -and $dev) { throw "the $Config build of GameVersion $v carries the DevTest mark - RELIC_DEVTEST reached a configuration that is not DevTest" }

    if ($stageDir) {
        $ver = $map[$v]
        if (-not $ver) { Write-Warning "GameVersion 33 is the upstream reference build - not staged"; continue }
        # The same import rule as for payload\: the dev build goes through mhynot2's launcher too.
        $vc = @(Get-VcRuntimeImports $dll)
        if ($vc.Count -gt 0) { throw "CLibrary.dll (GameVersion $v, DevTest) imports $($vc -join ', ') - not staged" }
        $dstDir = Join-Path $stageDir $ver
        New-Item -ItemType Directory -Force $dstDir | Out-Null
        Copy-Item $dll (Join-Path $dstDir 'CLibrary.dll') -Force
        $mapFile = [IO.Path]::ChangeExtension($dll, '.map')
        if (Test-Path $mapFile) { Copy-Item $mapFile (Join-Path $dstDir 'CLibrary.map') -Force }
        Write-Host "    -> $(Join-Path $dstDir 'CLibrary.dll')  sha256 $sha"
    }

    if ($CopyToPayload) {
        $ver = $map[$v]
        if (-not $ver) { Write-Warning "GameVersion 33 is the upstream reference build - not shipped, not copied"; continue }
        if ($Config -ne 'Release') { Write-Warning "only Release builds go to payload/ (this is $Config) - not copied"; continue }
        # launcher.exe cannot report a failed LoadLibrary: a DLL that imports a Visual C++ runtime DLL
        # would simply be absent from the game on a PC without the Visual C++ redistributable.
        $vc = @(Get-VcRuntimeImports $dll)
        if ($vc.Count -gt 0) { throw "CLibrary.dll (GameVersion $v) imports $($vc -join ', ') - not copied to payload/" }
        if (Test-DevBuildMarker $dll) { throw "CLibrary.dll (GameVersion $v) carries the DevTest mark - not copied to payload/" }
        # Nothing reaches payload\ without its manifest entry: publish.ps1 ships the folder whole, and only an entry
        # makes the launcher copy, check and inject the file (payload_manifest.py matches "src" exactly, so does this).
        $src = "$ver/ee/CLibrary.dll.bin"
        if (@(Get-ManifestEntries | Where-Object { $_.Entry.src -ceq $src }).Count -eq 0) {
            Write-Warning "payload\manifest.json has no entry with src $src - add the version's inject entry first; not copied"; continue
        }
        $dstDir = Join-Path $repo "payload\$ver\ee"
        New-Item -ItemType Directory -Force $dstDir | Out-Null
        $dst = Join-Path $dstDir 'CLibrary.dll.bin'
        Copy-Item $dll $dst -Force
        Write-Host "    -> $dst"
        & python (Join-Path $here 'payload_manifest.py') --set-sha "$ver/ee/CLibrary.dll.bin" $sha
        if ($LASTEXITCODE -ne 0) { Write-Warning 'payload/manifest.json was NOT updated (entry missing? see message above)' }
    }
}
Write-Host '==> done'
$results | Format-Table -AutoSize
