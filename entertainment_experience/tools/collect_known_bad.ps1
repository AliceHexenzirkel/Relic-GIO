# Builds a game version over and over, moving every field whose layout does not match the dump into
# field-offsets.known-bad.txt, until the build is clean. The compiler stops after a hundred errors, so one
# pass never shows the whole picture; this just keeps going until it does.
#
#   .\collect_known_bad.ps1 -Version 16 [-MaxPasses 30]
param(
    [string]$Version = "16",
    [int]$MaxPasses = 30
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$appdata = Join-Path $root "mod\cheat-library\src\appdata-$Version"
$list = Join-Path $appdata "field-offsets.known-bad.txt"
$msbuild = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"

if (-not (Test-Path $list)) {
    @(
        "# Fields whose generated C++ offset does not match the offset the game's metadata dump reports.",
        "# Features that read them are unreliable on this game version. Collected by tools/collect_known_bad.ps1;",
        "# the list may only shrink - a NEW mismatch is a build error, not a line added here."
    ) | Set-Content -Path $list -Encoding UTF8
}

for ($pass = 1; $pass -le $MaxPasses; $pass++) {
    python (Join-Path $PSScriptRoot "gen_field_asserts.py") $Version | Out-Null

    # The solution, not the project on its own: a bare .vcxproj build resolves a different intermediate
    # directory and happily reuses objects compiled for another game version.
    Push-Location (Join-Path $root "mod")
    $output = & $msbuild "relic-ee.sln" -t:Build -p:Configuration=Release `
        -p:Platform=x64 -p:GameVersion=$Version -nologo -v:minimal 2>&1 | Out-String
    Pop-Location

    $found = [regex]::Matches($output, "static assertion failed: '([A-Za-z0-9_]+__Fields)::([A-Za-z0-9_]+) ") |
        ForEach-Object { "$($_.Groups[1].Value)::$($_.Groups[2].Value)" } | Sort-Object -Unique

    if ($found.Count -eq 0) {
        Write-Host "pass $pass - clean"
        $total = (Get-Content $list | Where-Object { $_ -and -not $_.StartsWith("#") }).Count
        Write-Host "$total field(s) in $list"
        exit 0
    }

    Write-Host "pass $pass - $($found.Count) mismatched field(s)"
    Add-Content -Path $list -Value $found -Encoding UTF8
}

Write-Host "gave up after $MaxPasses passes"
exit 1
