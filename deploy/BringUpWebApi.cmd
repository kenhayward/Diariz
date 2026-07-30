@echo off
setlocal enabledelayedexpansion

rem ==========================================================================
rem BringUpWebApi.cmd - rebuild and redeploy just the API and web containers.
rem
rem These are the two that change on an ordinary release; postgres, redis,
rem minio and the GPU worker are left alone.
rem
rem The API goes first and the script waits for it to report healthy before
rem touching web. That order matters: the web container is not only the SPA,
rem it is also the reverse proxy for /api, /hubs, /mcp, /connect and
rem /.well-known. Bringing both down together breaks the path to the API at
rem the same moment the API is restarting, for no reason.
rem
rem Safe to run while people are recording. Capture is entirely client-side
rem and the browser only contacts the API when Stop is pressed - and nginx
rem buffers the whole upload before it needs the API at all. A measured
rem redeploy costs about 3 seconds. See docs/Research/zero-downtime-redeploy.md
rem
rem Usage:   BringUpWebApi.cmd
rem ==========================================================================

rem Run from this script's own folder, so it works from anywhere.
pushd "%~dp0"

set "TIMEOUT_SECONDS=300"
set "POLL_SECONDS=2"
set "HEALTH_URL=http://localhost:8080/health"

echo.
echo ======================================================================
echo  Diariz - redeploying api + web
echo ======================================================================

rem ---------------------------------------------------------------- API ---
echo.
echo [1/4] Building and starting the API ...
docker compose up -d --build api
if errorlevel 1 (
    echo.
    echo FAILED: the API container did not start. Recent output:
    docker compose logs api --tail 30
    goto :fail
)

echo.
echo [2/4] Waiting for the API to report healthy ...
set "SERVICE=api"
call :wait_healthy
if errorlevel 1 goto :fail

rem ---------------------------------------------------------------- web ---
echo.
echo [3/4] Building and starting the web container ...
docker compose up -d --build web
if errorlevel 1 (
    echo.
    echo FAILED: the web container did not start. Recent output:
    docker compose logs web --tail 30
    goto :fail
)

echo.
echo [4/4] Waiting for the web container to report healthy ...
set "SERVICE=web"
call :wait_healthy
if errorlevel 1 goto :fail

rem ------------------------------------------------------------- report ---
echo.
echo ======================================================================
echo  Done. Both containers are up and healthy.
echo ======================================================================

set "VERSION_JSON="
for /f "usebackq delims=" %%v in (`curl -fsS "%HEALTH_URL%" 2^>nul`) do set "VERSION_JSON=%%v"
if defined VERSION_JSON (
    echo  Serving: !VERSION_JSON!
) else (
    echo  ^(Could not read %HEALTH_URL% from the host to confirm the version.^)
)
echo.
docker compose ps api web
echo.

popd
endlocal
exit /b 0

rem ==========================================================================
rem  :wait_healthy - poll %SERVICE% until Docker reports it healthy.
rem  Returns 1 on timeout, or if the container disappears mid-wait.
rem ==========================================================================
:wait_healthy
set /a ELAPSED=0

:wait_loop
set "CID="
for /f "usebackq delims=" %%c in (`docker compose ps -q %SERVICE% 2^>nul`) do set "CID=%%c"

if not defined CID (
    echo.
    echo FAILED: the %SERVICE% container is not running. Recent output:
    docker compose logs %SERVICE% --tail 30
    exit /b 1
)

set "HEALTH="
for /f "usebackq delims=" %%h in (`docker inspect -f "{{.State.Health.Status}}" !CID! 2^>nul`) do set "HEALTH=%%h"
if not defined HEALTH set "HEALTH=unknown"

if /i "!HEALTH!"=="healthy" (
    echo       %SERVICE% is healthy after !ELAPSED!s.
    exit /b 0
)

if !ELAPSED! GEQ %TIMEOUT_SECONDS% (
    echo.
    echo FAILED: %SERVICE% was still '!HEALTH!' after %TIMEOUT_SECONDS%s. Recent output:
    docker compose logs %SERVICE% --tail 30
    exit /b 1
)

echo       %SERVICE%: !HEALTH! ^(!ELAPSED!s^)
timeout /t %POLL_SECONDS% /nobreak >nul
set /a ELAPSED+=%POLL_SECONDS%
goto :wait_loop

rem ==========================================================================
:fail
echo.
echo Redeploy aborted.
popd
endlocal
exit /b 1
