@echo off
setlocal enabledelayedexpansion

rem ==========================================================================
rem BringUpWebApi.cmd - rebuild and redeploy just the API and web containers.
rem
rem These are the two that change on an ordinary release; postgres, redis,
rem minio and the GPU worker are left alone.
rem
rem BOTH IMAGES ARE BUILT BEFORE ANYTHING IS RESTARTED. This does not shorten
rem the outage - `up --build` already builds before it stops the old
rem container, so the downtime is the two container recreates either way
rem (measured: ~2.6s for the API, under half a second for web). What it buys
rem is that a failed web build can no longer leave you half-deployed with a
rem new API behind an old SPA, and it shrinks the window in which those two
rem versions are live together from "web build time" to "API health check".
rem
rem The API is then started first, and the script waits for it to report
rem healthy before touching web. That order matters: the web container is not
rem only the SPA, it is also the reverse proxy for /api, /hubs, /mcp,
rem /connect and /.well-known. Bringing both down together breaks the path to
rem the API at the same moment the API is restarting.
rem
rem Safe to run while people are recording. Capture is entirely client-side
rem and the browser only contacts the API when Stop is pressed - and nginx
rem buffers the whole upload before it needs the API at all.
rem See docs/Research/zero-downtime-redeploy.md
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

rem -------------------------------------------------------------- build ---
echo.
echo [1/5] Building the api and web images ^(nothing is restarted yet^) ...
docker compose build api web
if errorlevel 1 (
    echo.
    echo FAILED: an image did not build. Nothing was restarted, so the
    echo running deployment is untouched.
    goto :fail
)

rem ------------------------------------------------------- in-flight work --
rem Informational only. Since 0.174.0 a job interrupted by an API restart is
rem reclaimed once its message has been idle ten minutes, so continuing costs
rem a delay rather than a lost job - not enough to justify blocking a deploy,
rem but worth knowing before you press on.
rem
rem These are the API's OWN streams. transcription-jobs and audio-merge-jobs
rem belong to the worker container, which this script never touches.
echo.
echo [2/5] Checking for work in flight ...
docker compose exec -T redis sh -c "for s in summarization-jobs:summarizers meeting-minutes-jobs:minute-takers actions-jobs:actions-extractors tag-cloud-jobs:tag-extractors formula-run-jobs:formula-runners embedding-jobs:embedders section-summary-jobs:section-summarizers section-minutes-jobs:section-minute-takers; do k=$(echo $s | cut -d: -f1); g=$(echo $s | cut -d: -f2); n=$(redis-cli XPENDING $k $g 2>/dev/null | head -n1 | grep -E '^[0-9]+$' || echo 0); [ $n -gt 0 ] && echo '      in flight:' $k $n; done; exit 0" 2>nul
echo       ^(nothing listed above = nothing in flight; anything listed will be
echo        picked up again within ~10 minutes if you continue^)

rem ---------------------------------------------------------------- API ---
echo.
echo [3/5] Starting the API ...
rem No --build here: the image is already built above. Dependencies are left
rem in play so this still works against a cold stack.
docker compose up -d api
if errorlevel 1 (
    echo.
    echo FAILED: the API container did not start. Recent output:
    docker compose logs api --tail 30
    goto :fail
)

echo.
echo [4/5] Waiting for the API to report healthy ...
set "SERVICE=api"
call :wait_healthy
if errorlevel 1 goto :fail

rem ---------------------------------------------------------------- web ---
echo.
echo [5/5] Starting the web container ...
rem --no-deps is essential, not tidiness. web depends_on api (service_healthy),
rem so without it compose RECREATES the API here too - a second restart,
rem landing at the exact moment nginx is also down. That is precisely the
rem both-at-once outage this script's ordering exists to avoid, and it was
rem observed doing it: a health poll caught one sample with the API and nginx
rem down together.
docker compose up -d --no-deps web
if errorlevel 1 (
    echo.
    echo FAILED: the web container did not start. Recent output:
    docker compose logs web --tail 30
    goto :fail
)
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
rem `timeout /t` needs a real console: run from anything with redirected stdin
rem it fails outright with "Input redirection is not supported", which turns
rem this into a busy-loop and makes the elapsed figures fiction. ping to
rem loopback is the portable cmd sleep - n+1 pings is n seconds.
set /a PINGS=%POLL_SECONDS%+1
ping -n !PINGS! 127.0.0.1 >nul
set /a ELAPSED+=%POLL_SECONDS%
goto :wait_loop

rem ==========================================================================
:fail
echo.
echo Redeploy aborted.
popd
endlocal
exit /b 1
