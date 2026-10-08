<#
  Reads the import table of a PE file without loading it and answers the one question the builds ask of
  it: does this file import a Visual C++ runtime DLL? Dot-sourced by build_dlls.ps1 (its result table and
  -CopyToPayload), entertainment_experience\tools\build_ee.ps1 (-CopyToPayload), publish.ps1 (the check of
  payload\) and the GitHub workflows:

    . .\build\pe_imports.ps1
    Get-PeImports <file>                           # names of the DLLs the file imports
    Test-VcRuntimeDllName <name>                   # is this the name of a Visual C++ runtime DLL?
    Get-VcRuntimeImports <file>                    # of the file's imports, the Visual C++ runtime DLLs
    Get-PayloadVcRuntimeProblems <dir> <entries>   # payload\manifest.json entries that import one

  build\make_release_bundles.py runs on Linux too and carries the same name rule in Python
  (VC_RUNTIME_RE): the two expressions change together.

  Those DLLs are not a part of Windows. A file that imports one loads only where the Microsoft Visual C++
  redistributable is installed, which a freshly installed Windows does not have and Relic neither
  installs nor asks for: such a launcher.exe does not start there, and a DLL handed to launcher.exe is
  silently absent from the game, because launcher.exe never checks LoadLibrary. Where one is installed
  the file runs on that PC's version of it, which can be too old for the compiler that built the file
  (injector\README.md, "Static C runtime").
#>

# Names of the DLLs a PE file imports, read from its import directory.
function Get-PeImports([string]$path) {
    # .NET resolves a relative path against the process directory, not against PowerShell's location.
    $path = (Resolve-Path -LiteralPath $path -ErrorAction Stop).ProviderPath
    $b = [IO.File]::ReadAllBytes($path)
    if ($b.Length -lt 0x40 -or $b[0] -ne 0x4D -or $b[1] -ne 0x5A) { throw "not a PE file: $path" }
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    if ($pe -lt 0 -or $pe + 24 -gt $b.Length -or [BitConverter]::ToUInt32($b, $pe) -ne 0x4550) { throw "not a PE file: $path" }
    $coff = $pe + 4
    $nSections = [BitConverter]::ToUInt16($b, $coff + 2)
    $optSize = [BitConverter]::ToUInt16($b, $coff + 16)
    $opt = $coff + 20
    $dirs = if ([BitConverter]::ToUInt16($b, $opt) -eq 0x20B) { $opt + 112 } else { $opt + 96 }   # PE32+ / PE32
    $importRva = [BitConverter]::ToUInt32($b, $dirs + 8)                                          # directory 1 = imports
    if ($importRva -eq 0) { return @() }
    $sections = @()
    for ($i = 0; $i -lt $nSections; $i++) {
        $s = $opt + $optSize + 40 * $i
        $virt = [BitConverter]::ToUInt32($b, $s + 8); $rawSize = [BitConverter]::ToUInt32($b, $s + 16)
        $sections += [pscustomobject]@{ Va = [BitConverter]::ToUInt32($b, $s + 12); Size = [Math]::Max($virt, $rawSize); Raw = [BitConverter]::ToUInt32($b, $s + 20) }
    }
    $toOffset = {
        param($rva)
        foreach ($s in $sections) { if ($rva -ge $s.Va -and $rva -lt $s.Va + $s.Size) { return [int]($rva - $s.Va + $s.Raw) } }
        throw "rva 0x$($rva.ToString('x')) is outside every section of $path"
    }
    $names = @()
    $d = & $toOffset $importRva
    while (($nameRva = [BitConverter]::ToUInt32($b, $d + 12)) -ne 0) {
        $o = & $toOffset $nameRva
        # An index past the end of a byte[] reads as $null, which never equals 0: the end of the name is
        # searched with a bound, so a file cut short inside a name fails instead of looping.
        $e = if ($o -lt $b.Length) { [Array]::IndexOf($b, [byte]0, $o) } else { -1 }
        if ($e -lt 0) { throw "an import name runs past the end of $path" }
        $names += [Text.Encoding]::ASCII.GetString($b, $o, $e - $o)
        $d += 20
    }
    $names
}

# Is this the name of a Visual C++ runtime DLL: the C, C++, OpenMP, MFC or ATL runtime of MSVC
# (VCRUNTIME140, MSVCP140, CONCRT140, VCCORLIB140, VCOMP140, LIBOMP140, MFC140 and the same families of
# the older compilers, numbers 70 to 120)? The release ones come with the Microsoft Visual C++
# redistributable, the debug ones (...D.dll, ucrtbased.dll) only with Visual Studio. Windows' own DLLs
# with similar names do not count: api-ms-win-crt-* and ucrtbase.dll are the Universal CRT of Windows 10
# and 11, and msvcrt.dll, mfc42.dll, msvcp60.dll and the *_win / *_clr0400 files ship with Windows.
# The match ignores case by the invariant culture, not the session's: under a Turkish or Azerbaijani
# regional format 'I' does not fold to 'i', and the linker writes VCRUNTIME140.dll in capitals.
function Test-VcRuntimeDllName([string]$name) {
    [regex]::IsMatch($name,
        '^(vcruntime|msvcp|msvcr|concrt|vccorlib|vcomp|vcamp|mfcm?|atl|libomp)(70|71|80|90|100|110|120|140)(?!_win|_clr|_1_clr)|^ucrtbased\.dll$',
        'IgnoreCase, CultureInvariant')
}

# The Visual C++ runtime DLLs among a file's imports.
function Get-VcRuntimeImports([string]$path) {
    @(Get-PeImports $path | Where-Object { Test-VcRuntimeDllName $_ })
}

# Of the manifest entries (objects with src and dst) whose destination is an .exe or a .dll, the ones
# whose file under $payloadDir imports a Visual C++ runtime DLL: one "<src> imports <names>" line each.
# Nothing comes back for a payload that loads on a PC without the redistributable.
function Get-PayloadVcRuntimeProblems([string]$payloadDir, $entries) {
    $problems = @()
    foreach ($e in @($entries)) {
        if ("$($e.dst)" -notmatch '\.(exe|dll)$') { continue }
        $file = Join-Path $payloadDir ("$($e.src)" -replace '/', '\')
        $crt = @(Get-VcRuntimeImports $file)
        if ($crt.Count -gt 0) { $problems += "$($e.src) imports $($crt -join ', ')" }
    }
    $problems
}
