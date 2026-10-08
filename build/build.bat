@echo off
REM This file must NOT be saved with a BOM: cmd reads the BOM as part of the first command, so
REM "@echo off" fails and the whole script runs with its commands echoed. (relic.iss is the
REM opposite: it NEEDS a BOM.) chcp 65001 keeps any UTF-8 output from publish.ps1 / ISCC readable.
chcp 65001 >nul
REM Builds Relic: self-contained single-file publish + (optional) Inno Setup installer.
REM Double-click it or run it from the command line.
setlocal
cd /d "%~dp0\.."

REM PSModulePath cleared before powershell.exe: if this window was started from a PowerShell 7
REM (or from the VS Code terminal with pwsh), the inherited variable points at 7's modules and
REM Windows PowerShell 5.1 no longer finds its own cmdlets - "Get-FileHash is not recognized" in
REM the middle of the build. Empty = 5.1 rebuilds its default paths.
set "PSModulePath="

echo === Building Relic (self-contained single-file) ===
powershell -NoProfile -ExecutionPolicy Bypass -File "build\publish.ps1"  --theme=summer
if errorlevel 1 (
  echo.
  echo BUILD FAILED.
  pause
  exit /b 1
)

echo.
echo === Installer (Inno Setup) ===
set "ISCC=C:\Program Files (x86)\Inno Setup 6\iscc.exe"
if exist "%ISCC%" (
  "%ISCC%" "installer\relic.iss"
  echo.
  echo Installer created: installer\Output\RelicSetup.exe
) else (
  echo Inno Setup 6 not found. The ready-to-run app is in: publish\win-x64\Relic.exe
  echo (Install Inno Setup 6 if you want a RelicSetup.exe.^)
)

echo.
echo Done.
pause
