@echo off
setlocal enabledelayedexpansion

rem ==========================================================================
rem ProvisionDiarizMinio.cmd - give the Diariz API and worker a MinIO access
rem key scoped to the recordings bucket, instead of the MinIO root credentials.
rem
rem Run this ONCE PER SERVER. It is safe to re-run: an existing key is left
rem alone unless you ask for a rotation.
rem
rem WHY: without it the API and the worker both authenticate to MinIO as root,
rem which can administer MinIO and read every bucket on it. The scoped key can
rem only work inside the recordings bucket. The script proves the key cannot
rem read another bucket or administer MinIO before it exits.
rem
rem HOW: the real work is deploy\diariz-minio\provision.sh, piped into the
rem running `minio` container. The host needs no `mc`, does not depend on
rem MinIO's host port, and never sees the MinIO root credentials.
rem
rem AFTERWARDS: add the two printed lines to deploy\.env and recreate the api,
rem worker (and live-worker, if you run it):  docker compose up -d
rem
rem Usage:   ProvisionDiarizMinio.cmd
rem          ProvisionDiarizMinio.cmd /rotate
rem
rem Requires: the stack's `minio` service running (docker compose up -d minio)
rem ==========================================================================

pushd "%~dp0"

set "MODE="
if /i "%~1"=="/rotate" set "MODE=rotate"
if /i "%~1"=="-rotate" set "MODE=rotate"
if /i "%~1"=="--rotate" set "MODE=rotate"
if not "%~1"=="" if "!MODE!"=="" (
  echo Unknown argument "%~1". Usage: %~nx0 [/rotate]
  popd & exit /b 2
)

set "BUCKET=recordings"
set "OTHER=glitchtip"

echo.
echo === Diariz app MinIO provisioning ===
echo.

rem --- Is the minio container actually up? A clear message beats a docker stack trace.
rem
rem Ask for the container ID rather than matching service names in text.
rem `docker compose ps --services` emits UNIX line endings even on Windows, and
rem findstr /x then sees the whole output as one line and never matches - so the
rem obvious `| findstr /x "minio"` check reports "not running" on a running
rem stack. Asking for -q sidesteps text parsing entirely.
set "MINIO_CID="
for /f "delims=" %%i in ('docker compose ps -q --status running minio 2^>nul') do set "MINIO_CID=%%i"
if not defined MINIO_CID (
  echo ERROR: the `minio` service is not running.
  echo        Start it first:  docker compose up -d minio
  popd ^& exit /b 1
)

rem --- Generate the secret.
rem
rem HEX, deliberately. A .env value containing $ would be interpolated by
rem docker compose, a # would start a comment, and quotes or spaces would
rem need escaping in three different places. Hex is [0-9a-f] only, so it is
rem safe unquoted in .env, in a shell, and in a docker command line.
rem 32 bytes = 64 hex characters = 256 bits.
rem The PowerShell below deliberately contains NO pipeline. A `|` inside a
rem for /f backtick block has to be escaped for cmd, and the escape character
rem survives into PowerShell and breaks it. BitConverter avoids the pipe.
set "SECRET="
for /f "usebackq delims=" %%s in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$b=[byte[]]::new(32);[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b);[System.BitConverter]::ToString($b).Replace('-','').ToLower()"`) do set "SECRET=%%s"

if "!SECRET!"=="" (
  echo ERROR: could not generate a secret with PowerShell.
  popd & exit /b 1
)

rem --- Run the provisioning script inside the container.
rem The secret is passed as an argument rather than piped because stdin is
rem already carrying the script itself. It is hex, so it needs no quoting.
type "diariz-minio\provision.sh" | docker compose exec -T minio sh -s -- "!SECRET!" "!BUCKET!" "!OTHER!" "!MODE!"

if errorlevel 3 goto :left_alone
if errorlevel 1 goto :failed

echo.
echo ==========================================================================
echo  Add these two lines to deploy\.env on THIS server:
echo.
echo MINIO_APP_ACCESS_KEY=diariz-app
echo MINIO_APP_SECRET_KEY=!SECRET!
echo.
echo  Then recreate the api and worker:  docker compose up -d
echo.
echo  This secret is not stored anywhere else. If you lose it, re-run with
echo  /rotate to issue a new one.
echo ==========================================================================
echo.
popd & exit /b 0

:left_alone
echo.
echo Nothing changed - `diariz-app` already exists on this server.
echo The MINIO_APP_SECRET_KEY already in deploy\.env is still the right one.
echo If you have lost it, re-run:  %~nx0 /rotate
echo.
popd & exit /b 0

:failed
echo.
echo Provisioning FAILED. Nothing above should be treated as usable.
echo.
popd & exit /b 1
