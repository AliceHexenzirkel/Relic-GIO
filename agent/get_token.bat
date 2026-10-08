@echo off
setlocal
REM ============================================================================
REM  get_token.bat - fetch the GIO agent token from the server (admin only)
REM
REM  Where the token lives: on the server, in /etc/gio-agent/config (the line
REM  GIO_AGENT_TOKEN=..., a chmod 600 file read by the gio-agent service).
REM  The script connects over SSH, reads the token and saves it to
REM  config\agent.token (gitignored), from where build\publish.ps1 bakes it
REM  into Relic.exe automatically at publish time.
REM
REM  FILL IN the values below. To keep the password out of a git-tracked file,
REM  copy this file to get_token.local.bat (it is in .gitignore).
REM    SSH_PASS - the SSH password (needs plink.exe from PuTTY)
REM               CAREFUL with special characters in the password (.bat context):
REM               - every % must be doubled as %% (e.g. ab%%cd for ab%cd)
REM               - double quotes " are not supported in the set "SSH_PASS=..." line
REM               ^, &, !, $, #, @ are fine inside the quotes. If the password has % or ",
REM               use an SSH key instead (SSH_KEY) - no such headaches.
REM    SSH_KEY  - OR the path of the private key (.ppk for plink,
REM               OpenSSH format for ssh.exe). Both empty = Pageant /
REM               the default keys / an interactive password prompt.
REM ============================================================================
set "SSH_HOST="
set "SSH_PORT=22"
set "SSH_USER=root"
set "SSH_PASS="
set "SSH_KEY="

set "TOKEN_FILE=%~dp0..\config\agent.token"
set "REMOTE_CMD=awk -F= '/^GIO_AGENT_TOKEN=/{print $2}' /etc/gio-agent/config"

REM -- find plink (PuTTY) --
set "PLINK="
for %%P in ("C:\Program Files\PuTTY\plink.exe" "C:\Program Files (x86)\PuTTY\plink.exe") do (
  if exist %%P if not defined PLINK set "PLINK=%%~P"
)
where plink.exe >nul 2>nul && if not defined PLINK set "PLINK=plink.exe"

if defined SSH_PASS (
  if not defined PLINK (
    echo ERROR: an SSH password needs plink.exe ^(install PuTTY^) or use SSH_KEY instead.
    exit /b 1
  )
  echo Connecting with a password through plink to %SSH_USER%@%SSH_HOST%:%SSH_PORT% ...
  REM first connection: answer "y" to the host-key question so it gets cached
  echo y | "%PLINK%" -P %SSH_PORT% -pw "%SSH_PASS%" %SSH_USER%@%SSH_HOST% "exit" >nul 2>nul
  "%PLINK%" -P %SSH_PORT% -batch -pw "%SSH_PASS%" %SSH_USER%@%SSH_HOST% "%REMOTE_CMD%" > "%TOKEN_FILE%"
) else if defined SSH_KEY (
  if defined PLINK (
    echo Connecting with key "%SSH_KEY%" through plink to %SSH_USER%@%SSH_HOST%:%SSH_PORT% ...
    echo y | "%PLINK%" -P %SSH_PORT% -i "%SSH_KEY%" %SSH_USER%@%SSH_HOST% "exit" >nul 2>nul
    "%PLINK%" -P %SSH_PORT% -batch -i "%SSH_KEY%" %SSH_USER%@%SSH_HOST% "%REMOTE_CMD%" > "%TOKEN_FILE%"
  ) else (
    echo Connecting with key "%SSH_KEY%" through ssh.exe to %SSH_USER%@%SSH_HOST%:%SSH_PORT% ...
    ssh -p %SSH_PORT% -i "%SSH_KEY%" -o StrictHostKeyChecking=accept-new %SSH_USER%@%SSH_HOST% "%REMOTE_CMD%" > "%TOKEN_FILE%"
  )
) else (
  REM Nothing filled in: go INTERACTIVE - you will be asked for the password in the console.
  REM ssh.exe prompts for the password itself; plink can too, but NOT with -batch.
  where ssh.exe >nul 2>nul
  if not errorlevel 1 (
    echo No SSH_PASS / SSH_KEY - interactive connection through ssh.exe ^(type the password when asked^)...
    ssh -p %SSH_PORT% -o StrictHostKeyChecking=accept-new %SSH_USER%@%SSH_HOST% "%REMOTE_CMD%" > "%TOKEN_FILE%"
  ) else if defined PLINK (
    echo No SSH_PASS / SSH_KEY - interactive connection through plink ^(type the password when asked^)...
    "%PLINK%" -P %SSH_PORT% %SSH_USER%@%SSH_HOST% "%REMOTE_CMD%" > "%TOKEN_FILE%"
  ) else (
    echo ERROR: neither ssh.exe nor plink.exe was found. Install PuTTY or OpenSSH.
    exit /b 1
  )
)

REM -- check the result --
set "TOKEN="
if exist "%TOKEN_FILE%" set /p TOKEN=<"%TOKEN_FILE%"
if not defined TOKEN (
  echo ERROR: could not read the token ^(connection failed or the agent is not installed^).
  echo Check the SSH details above or install the agent with deploy_from_windows.ps1.
  del "%TOKEN_FILE%" >nul 2>nul
  exit /b 1
)

echo.
echo Agent token: %TOKEN%
echo Saved to:    %TOKEN_FILE%
echo.
echo Next step - build the app with everything baked in:
echo   .\build\publish.ps1 --default-server=%SSH_HOST%
echo   ^(the token is taken automatically from config\agent.token; for DNS use
echo    --default-server=game.example.com instead of an IP^)
exit /b 0
