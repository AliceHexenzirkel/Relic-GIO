<#
.SYNOPSIS
  Builds Relic's two native components on this PC and ends with a table of the results: the in-game
  enhancements DLL (CLibrary.dll, one per game version, through entertainment_experience\tools\build_ee.ps1)
  and the Win11 injector (launcher.exe + mhynot2.dll, through the CMake wrapper in injector\). The
  prerequisites are checked first; a missing one is named with what to install and the script exits 2.

.EXAMPLE
  .\build\build_dlls.ps1                              # EE for 16 and 28 + the injector; nothing copied
  .\build\build_dlls.ps1 -Versions 28 -SkipInjector   # one EE DLL
  .\build\build_dlls.ps1 -SkipEe -Clean               # the injector from an empty injector\build
  .\build\build_dlls.ps1 -Toolset v143                # the EE DLLs with the Visual Studio 2022 toolset
  .\build\build_dlls.ps1 -CopyToPayload               # + payload\<ver>\ee\CLibrary.dll.bin, payload\common\ayy\anime\
                                                      #   build\*.bin and their sha256 in payload\manifest.json

.NOTES
  Found with vswhere: MSBuild and the MSVC x64 tools of one Visual Studio / Build Tools install (its
  vcvars64.bat gives the injector build its compiler environment), CMake and Ninja (the copies bundled
  with Visual Studio, else PATH). Found on their own: a Windows 10/11 SDK (root = the registry's
  KitsRoot10, else the ProgramFiles(x86) folder), the initialised injector\mhynot2 submodule, and python
  for the manifest update. -Toolset auto picks v145 when an MSVC 14.5x toolset is installed under
  that instance, else v143. The injector is always built with the static C runtime (RELIC_STATIC_CRT).

  The result table says per file whether it equals the committed payload file and whether it imports a
  Visual C++ runtime DLL (build\pe_imports.ps1). Such a file depends on the Visual C++ redistributable of
  the player's PC, so publish.ps1 refuses it in payload\ and -CopyToPayload does not copy it. -CopyToPayload
  replaces committed, in-game-tested binaries: payload\ is what every player's launcher puts into the game
  folder, so launch the game with the new files before committing them. .github\workflows\dlls.yml
  builds the same things on GitHub and never copies.
#>
[CmdletBinding()]
param(
    [string[]]$Versions = @('16', '28'),   # accepts -Versions 16,28 as well as '16,28'
    [ValidateSet('auto', 'v145', 'v143')]
    [string]$Toolset = 'auto',
    [switch]$SkipEe,
    [switch]$SkipInjector,
    [switch]$CopyToPayload,
    [switch]$Clean
)
$ErrorActionPreference = 'Stop'
$repo          = Split-Path $PSScriptRoot -Parent
$buildEe       = Join-Path $repo 'entertainment_experience\tools\build_ee.ps1'
$manifestTool  = Join-Path $repo 'entertainment_experience\tools\payload_manifest.py'
$injectorSrc   = Join-Path $repo 'injector'
$injectorBuild = Join-Path $injectorSrc 'build'
$payloadMap    = @{ 16 = '1.6'; 28 = '2.8' }
$vers = @($Versions | ForEach-Object { $_ -split ',' } | Where-Object { $_ -ne '' } | ForEach-Object { [int]$_ })
$needEe  = -not $SkipEe
$needInj = -not $SkipInjector

function Rel([string]$path) {
    if ($path.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) { $path.Substring($repo.Length).TrimStart('\') } else { $path }
}

# Get-VcRuntimeImports: the Visual C++ runtime DLLs a PE file imports, read without loading the file.
. (Join-Path $PSScriptRoot 'pe_imports.ps1')

# ---- prerequisites ----------------------------------------------------------------------------------
$prereqs = @(); $missing = @()
function Add-Prereq([string]$name, [bool]$needed, $found, [string]$fix) {
    $ok = -not [string]::IsNullOrEmpty("$found")
    $status = if ($ok) { 'ok' } elseif ($needed) { 'MISSING' } else { 'not needed' }
    if ($needed -and -not $ok) { $script:missing += $name }
    $script:prereqs += [pscustomobject]@{ Prerequisite = $name; Status = $status; Where = $(if ($ok) { "$found" } else { $fix }) }
}
$vsHelp = 'install Visual Studio Build Tools with the "Desktop development with C++" workload (https://visualstudio.microsoft.com/visual-cpp-build-tools/)'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot = $null; $msbuild = $null
if (Test-Path $vswhere) {
    $vsRoot  = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath | Select-Object -First 1
    $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
Add-Prereq 'vswhere.exe' ($needEe -or $needInj) $(if (Test-Path $vswhere) { $vswhere }) $vsHelp
Add-Prereq 'Visual Studio instance' ($needEe -or $needInj) $vsRoot $vsHelp
Add-Prereq 'MSBuild.exe' $needEe $msbuild 'add the MSBuild component to the Visual Studio install'

$msvcDir = $null; $msvcVersions = @()
if ($vsRoot) {
    $msvcDir = Join-Path $vsRoot 'VC\Tools\MSVC'
    $msvcVersions = @(Get-ChildItem $msvcDir -Directory -ErrorAction SilentlyContinue | ForEach-Object Name | Sort-Object -Descending)
}
$cl = $msvcVersions | ForEach-Object { Join-Path $msvcDir "$_\bin\Hostx64\x64\cl.exe" } | Where-Object { Test-Path $_ } | Select-Object -First 1
Add-Prereq 'MSVC x64 tools (cl.exe)' ($needEe -or $needInj) $cl 'add "MSVC v145 - C++ x64/x86 build tools" (or v143) to the Visual Studio install'
$vcvars = if ($vsRoot) { Join-Path $vsRoot 'VC\Auxiliary\Build\vcvars64.bat' } else { $null }
Add-Prereq 'vcvars64.bat' $needInj $(if ($vcvars -and (Test-Path $vcvars)) { $vcvars }) 'part of the MSVC x64 tools component'
# The SDK root comes from the installer's KitsRoot10 registry value (the 64-bit view of the key, then the
# 32-bit one), with the conventional folder under ProgramFiles(x86) as the last candidate; the first root
# whose Include\<version>\um\windows.h exists is used (newest version), and the row names that root.
$sdkRoots = @()
foreach ($key in 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots') {
    $root = "$((Get-ItemProperty $key -ErrorAction SilentlyContinue).KitsRoot10)".TrimEnd('\')
    if ($root -and -not ($sdkRoots.Root -contains $root)) { $sdkRoots += [pscustomobject]@{ Root = $root; Source = "KitsRoot10 in $key" } }
}
$pf86Root = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
if (-not ($sdkRoots.Root -contains $pf86Root)) { $sdkRoots += [pscustomobject]@{ Root = $pf86Root; Source = 'the ProgramFiles(x86) folder' } }
$sdk = $null
foreach ($r in $sdkRoots) {
    $dir = Get-ChildItem (Join-Path $r.Root 'Include') -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName 'um\windows.h') } | Sort-Object Name -Descending | Select-Object -First 1
    if ($dir) { $sdk = "$($dir.FullName) (root $($r.Root), from $($r.Source))"; break }
}
Add-Prereq 'Windows SDK' ($needEe -or $needInj) $sdk 'add a "Windows 11 SDK" component to the Visual Studio install'

$cmake = $null; $ninja = $null
if ($vsRoot) {
    $vsCMake = Join-Path $vsRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake'
    if (Test-Path (Join-Path $vsCMake 'CMake\bin\cmake.exe')) { $cmake = Join-Path $vsCMake 'CMake\bin\cmake.exe' }
    if (Test-Path (Join-Path $vsCMake 'Ninja\ninja.exe'))     { $ninja = Join-Path $vsCMake 'Ninja\ninja.exe' }
}
if (-not $cmake) { $c = Get-Command cmake -ErrorAction SilentlyContinue; if ($c) { $cmake = $c.Source } }
if (-not $ninja) { $n = Get-Command ninja -ErrorAction SilentlyContinue; if ($n) { $ninja = $n.Source } }
Add-Prereq 'CMake' $needInj $cmake 'add "C++ CMake tools for Windows" to the Visual Studio install, or install CMake and put it on PATH'
Add-Prereq 'Ninja' $needInj $ninja 'comes with "C++ CMake tools for Windows"; else install Ninja and put it on PATH'
$sub = Join-Path $injectorSrc 'mhynot2\CMakeLists.txt'; $subMinhook = Join-Path $injectorSrc 'mhynot2\minhook\CMakeLists.txt'
Add-Prereq 'injector\mhynot2 submodule' $needInj $(if ((Test-Path $sub) -and (Test-Path $subMinhook)) { Split-Path $sub }) 'run: git submodule update --init --recursive'
$python = Get-Command python -ErrorAction SilentlyContinue
Add-Prereq 'python (manifest update)' ($CopyToPayload -and ($needEe -or $needInj)) $(if ($python) { $python.Source }) 'install Python 3 and put python on PATH'

$toolsetNote = 'requested'
if ($Toolset -eq 'auto') {
    $Toolset = if (@($msvcVersions | Where-Object { $_ -match '^14\.5\d' }).Count -gt 0) { 'v145' } else { 'v143' }
    $found = if ($msvcVersions.Count -gt 0) { $msvcVersions -join ', ' } else { 'none' }
    $toolsetNote = "auto; MSVC $found under $msvcDir"
}

Write-Host "==> prerequisites (Visual Studio: $(if ($vsRoot) { $vsRoot } else { 'none found' }))"
Write-Host ($prereqs | Format-Table -AutoSize | Out-String -Width 4096).TrimEnd()
if ($needEe) { Write-Host "Toolset: $Toolset ($toolsetNote)" }
if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host "Missing: $($missing -join ', '). Install what the table names, then run the script again." -ForegroundColor Red
    exit 2
}

# ---- results ---------------------------------------------------------------------------------------
$results = @()
function Add-Result([string]$component, [string]$file, [string]$payloadFile) {
    if (-not (Test-Path $file)) { throw "expected output missing: $file" }
    $sha = (Get-FileHash -Algorithm SHA256 $file).Hash.ToLowerInvariant()
    $payload = if (-not $payloadFile) { 'no payload entry' }
               elseif (-not (Test-Path $payloadFile)) { "payload file missing ($(Rel $payloadFile))" }
               elseif ((Get-FileHash -Algorithm SHA256 $payloadFile).Hash.ToLowerInvariant() -eq $sha) { "same as $(Rel $payloadFile)" }
               else { "DIFFERS from $(Rel $payloadFile)" }
    $vc = @(Get-VcRuntimeImports $file)
    $crt = if ($vc.Count -gt 0) { "imports $($vc -join ', ')" } else { 'static (no Visual C++ runtime DLL)' }
    $script:results += [pscustomobject]@{
        Component = $component; File = (Rel $file); Bytes = ('{0:N0}' -f (Get-Item $file).Length); CRT = $crt; Payload = $payload; SHA256 = $sha
    }
    $sha
}

# ---- the enhancements DLL --------------------------------------------------------------------------
if ($needEe) {
    foreach ($v in $vers) {
        # Named parameters travel in a hashtable: an array splat would bind its elements by position.
        $eeArgs = @{ Versions = "$v"; Config = 'Release'; Toolset = $Toolset }
        if ($CopyToPayload) { $eeArgs.CopyToPayload = $true }
        if ($Clean) { $eeArgs.Clean = $true }
        Write-Host ''
        Write-Host "==> build_ee.ps1 -Versions $v -Config Release -Toolset $Toolset$(if ($CopyToPayload) { ' -CopyToPayload' })$(if ($Clean) { ' -Clean' })"
        & $buildEe @eeArgs
        $dll = Join-Path $repo "entertainment_experience\mod\bin\v$v\Release-x64\CLibrary.dll"
        $payloadFile = if ($payloadMap.ContainsKey($v)) { Join-Path $repo "payload\$($payloadMap[$v])\ee\CLibrary.dll.bin" } else { $null }
        Add-Result "CLibrary.dll (GameVersion $v, $Toolset)" $dll $payloadFile | Out-Null
    }
}

# ---- the injector ----------------------------------------------------------------------------------
# The compiler environment of vcvars64.bat is imported into this process for the CMake calls (Ninja runs
# cl.exe and link.exe with it) and taken back afterwards, so the caller's session keeps its own PATH.
function Save-Environment {
    $d = @{}
    foreach ($e in [Environment]::GetEnvironmentVariables('Process').GetEnumerator()) { $d[$e.Key] = $e.Value }
    $d
}
function Restore-Environment($saved) {
    foreach ($k in @([Environment]::GetEnvironmentVariables('Process').Keys)) {
        if (-not $saved.ContainsKey($k)) { [Environment]::SetEnvironmentVariable($k, $null, 'Process') }
    }
    foreach ($e in $saved.GetEnumerator()) { [Environment]::SetEnvironmentVariable($e.Key, $e.Value, 'Process') }
}
function Import-VcEnvironment([string]$vcvarsBat) {
    $cmd = Join-Path ([IO.Path]::GetTempPath()) ("relic-vcenv-{0}.cmd" -f [guid]::NewGuid().ToString('N'))
    @('@echo off', "call `"$vcvarsBat`" >nul 2>&1 || exit /b 1", 'set') | Set-Content -Path $cmd -Encoding ASCII
    try {
        $lines = & cmd.exe /d /c $cmd
        if ($LASTEXITCODE -ne 0) { throw "vcvars64.bat failed (exit $LASTEXITCODE): $vcvarsBat" }
    } finally { Remove-Item $cmd -ErrorAction SilentlyContinue }
    foreach ($line in $lines) {
        $i = $line.IndexOf('=')
        if ($i -gt 0) { [Environment]::SetEnvironmentVariable($line.Substring(0, $i), $line.Substring($i + 1), 'Process') }
    }
}

if ($needInj) {
    Write-Host ''
    if ($Clean -and (Test-Path $injectorBuild)) { Write-Host "==> removing $(Rel $injectorBuild)"; Remove-Item -Recurse -Force $injectorBuild }
    Write-Host "==> cmake -S injector -B injector\build -G Ninja -DCMAKE_BUILD_TYPE=Release -DRELIC_STATIC_CRT=ON   (vcvars64 of $vsRoot)"
    $saved = Save-Environment
    try {
        Import-VcEnvironment $vcvars
        & $cmake -S $injectorSrc -B $injectorBuild -G Ninja -DCMAKE_BUILD_TYPE=Release -DRELIC_STATIC_CRT=ON "-DCMAKE_MAKE_PROGRAM=$ninja"
        if ($LASTEXITCODE -ne 0) { throw "cmake configure failed (exit $LASTEXITCODE)" }
        & $cmake --build $injectorBuild
        if ($LASTEXITCODE -ne 0) { throw "cmake --build failed (exit $LASTEXITCODE)" }
    } finally { Restore-Environment $saved }

    $payloadDir = Join-Path $repo 'payload\common\ayy\anime\build'
    $pair = @(@{ Name = 'launcher.exe'; Bin = 'launcher.exe.bin' }, @{ Name = 'mhynot2.dll'; Bin = 'mhynot2.dll.bin' })
    foreach ($o in $pair) {
        $o.File = Join-Path $injectorBuild $o.Name
        $o.Sha = Add-Result "$($o.Name) (injector, static CRT)" $o.File (Join-Path $payloadDir $o.Bin)
    }
    if ($CopyToPayload) {
        # Both files are checked before either is copied: a refused file never leaves the pair half replaced.
        $refused = @()
        foreach ($o in $pair) {
            $vc = @(Get-VcRuntimeImports $o.File)
            if ($vc.Count -gt 0) { $refused += "$($o.Name) imports $($vc -join ', ')" }
        }
        if ($refused.Count -gt 0) {
            Write-Host ($results | Format-Table -AutoSize | Out-String -Width 4096).TrimEnd()
            throw "$($refused -join '; '): neither injector file was copied to payload\"
        }
        foreach ($o in $pair) {
            $dst = Join-Path $payloadDir $o.Bin
            Copy-Item $o.File $dst -Force
            Write-Host "    -> $(Rel $dst)"
            & python $manifestTool --set-sha "common/ayy/anime/build/$($o.Bin)" $o.Sha
            if ($LASTEXITCODE -ne 0) { throw "payload/manifest.json was not updated for $($o.Bin) (see the message above)" }
        }
        Write-Warning 'payload\common\ayy\anime\build\ now holds this injector build: launch the game through Relic (Windows 10 and 11) before committing it.'
    }
}

Write-Host ''
Write-Host '==> results'
Write-Host ($results | Format-Table -AutoSize | Out-String -Width 4096).TrimEnd()
