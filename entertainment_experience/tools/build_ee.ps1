<#
.SYNOPSIS
  Builds the in-game enhancements DLL (CLibrary.dll) for one or more game versions and, optionally,
  drops each result into payload/<ver>/ee/CLibrary.dll.bin with its sha256 written into payload/manifest.json.

.EXAMPLE
  .\build_ee.ps1 -Versions 28                    # mod\bin\v28\Release-x64\CLibrary.dll
  .\build_ee.ps1 -Versions 16,28 -CopyToPayload  # + payload\1.6\ee\ and payload\2.8\ee\ (+ manifest sha256)
  .\build_ee.ps1 -Versions 28 -Config Release_WS # the offset-discovery build (pattern scanner)
  .\build_ee.ps1 -Versions 28 -Toolset v143      # the Visual Studio 2022 toolset (GitHub's Windows runners)
  .\build_ee.ps1 -Versions 33 -Clean

.NOTES
  Toolchain: Visual Studio Build Tools (MSVC toolset v145 or v143) + Windows SDK; MSBuild is located with
  vswhere. The project files name v145; -Toolset hands the choice to MSBuild over them: auto (default) is
  v145 when an MSVC 14.5x toolset is installed under that Visual Studio instance, else v143.
  The DLL must import no Visual C++ runtime DLL (static CRT, see cheat-library.vcxproj) because mhynot2's
  launcher cannot report a failed LoadLibrary; -CopyToPayload refuses one that does (build\pe_imports.ps1).
  publish.ps1 never calls this script: the blessed binaries are committed.
#>
[CmdletBinding()]
param(
    [string[]]$Versions = @('28'),   # accepts -Versions 16,28 as well as '16,28' (powershell -File passes one string)
    [ValidateSet('Release', 'Release_WS', 'Debug')]
    [string]$Config = 'Release',
    [ValidateSet('auto', 'v145', 'v143')]
    [string]$Toolset = 'auto',
    [switch]$CopyToPayload,
    [switch]$Clean
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$mod  = Join-Path $here '..\mod' | Resolve-Path
$repo = Join-Path $here '..\..' | Resolve-Path
$sln  = Join-Path $mod 'relic-ee.sln'
$map  = @{ 16 = '1.6'; 28 = '2.8'; 33 = $null }

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
foreach ($v in $vers) {
    if (-not $map.ContainsKey($v)) { throw "unknown GameVersion $v (16|28|33)" }
    $target = if ($Clean) { 'Rebuild' } else { 'Build' }
    Write-Host "==> GameVersion=$v Config=$Config Toolset=$Toolset ($target)"
    & $msbuild $sln "-t:$target" "-p:Configuration=$Config" '-p:Platform=x64' "-p:GameVersion=$v" "-p:PlatformToolset=$Toolset" '-m' '-nologo' '-v:minimal' '-clp:Summary;ErrorsOnly;WarningsOnly'
    if ($LASTEXITCODE -ne 0) { throw "MSBuild failed for GameVersion=$v (exit $LASTEXITCODE)" }
    $dll = Join-Path $mod "bin\v$v\$Config-x64\CLibrary.dll"
    if (-not (Test-Path $dll)) { throw "expected output missing: $dll" }
    $sha = (Get-FileHash -Algorithm SHA256 $dll).Hash.ToLowerInvariant()
    $mb  = [math]::Round((Get-Item $dll).Length / 1MB, 2)
    Write-Host ("    {0}  {1} MB  sha256 {2}" -f $dll, $mb, $sha)
    $results += [pscustomobject]@{ Version = $v; Dll = $dll; Sha256 = $sha; MB = $mb }

    if ($CopyToPayload) {
        $ver = $map[$v]
        if (-not $ver) { Write-Warning "GameVersion 33 is the upstream reference build - not shipped, not copied"; continue }
        if ($Config -ne 'Release') { Write-Warning "only Release builds go to payload/ (this is $Config) - not copied"; continue }
        # launcher.exe cannot report a failed LoadLibrary: a DLL that imports a Visual C++ runtime DLL
        # would simply be absent from the game on a PC without the Visual C++ redistributable.
        . (Join-Path $repo 'build\pe_imports.ps1')
        $vc = @(Get-VcRuntimeImports $dll)
        if ($vc.Count -gt 0) { throw "CLibrary.dll (GameVersion $v) imports $($vc -join ', ') - not copied to payload/" }
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
