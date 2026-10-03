@echo off

rem ==========================================================================
rem BringUpProd.cmd - pull, rebuild everything, and bring the whole stack up.
rem
rem This is the COLD-START / full-stack tool: it rebuilds and recreates every
rem service, including postgres, redis, the s3 store and the GPU worker. Use it for a
rem first bring-up, after a machine restart, or when infrastructure or the
rem worker image has changed.
rem
rem NOT the tool for an ordinary release. `BringUpWebApi.cmd` rebuilds just the
rem API and web containers, in the right order, waiting for the API to report
rem healthy before it touches nginx - which keeps the proxy to /api, /hubs and
rem /mcp alive across the deploy. This script has no such ordering or health
rem waiting, and restarting redis here drops queued jobs' consumer-group state.
rem Prefer BringUpWebApi.cmd unless you specifically need the full stack.
rem
rem It also runs `git pull` first, so it deploys whatever is on the checked-out
rem branch - confirm you are on `main` and at the commit you mean to ship.
rem
rem And it runs `docker compose pull` for the third-party images (postgres,
rem redis, s3, and GlitchTip under the overlay). Without that step they stay at
rem whatever version the host first cached FOREVER: `docker compose build` only
rem rebuilds the three services that have a build: section, and `up -d` fetches
rem an image: service only when it is missing locally. Issue #816 found
rem production on a June Postgres image - one patch release and four pgvector
rem releases behind - on a server that had been up for two days. It is invisible
rem by construction: containers are recreated on every deploy so they always
rem look fresh, and `docker ps` shows the tag, not what the tag resolved to.
rem
rem The pull is also what makes the pinning strategy mean anything. The tags are
rem pinned to the MINOR line (redis:8-alpine, postgres:16-alpine, glitchtip:6.2)
rem precisely so a pull collects patch and security fixes without taking new
rem features unannounced - and nothing used to run the pull.
rem
rem A pull FAILURE does not stop the bring-up. The other job of this script is
rem recovering after a machine restart or a power cut, and a stack that refuses
rem to come up because a registry is unreachable is worse than one running last
rem month's Postgres.
rem
rem The observability overlay (docker-compose.observability.yml, self-hosted
rem GlitchTip; see docs/GlitchTip_Deployment.md) is switched on by COMPOSE_FILE
rem in deploy\.env, not by this script - so this script, BringUpWebApi.cmd and
rem every hand-typed `docker compose` command all use the same files. It
rem refuses to run when .env configures GlitchTip but not COMPOSE_FILE: the
rem overlay also changes the s3 service, and bringing the stack up without it
rem would lock GlitchTip out of its bucket.
rem
rem Usage:   run from this directory:  BringUpProd.cmd
rem ==========================================================================

rem Run from this script's own folder, so it works from anywhere.
pushd "%~dp0"

if not exist .env (
  echo deploy\.env is missing. Copy .env.example to .env and fill it in first.
  popd & exit /b 1
)

set "OVERLAY=0"
findstr /r /b /c:"COMPOSE_FILE=.*observability" .env >nul && set "OVERLAY=1"
findstr /r /b /c:"GLITCHTIP_SECRET_KEY=." .env >nul
if not errorlevel 1 if "%OVERLAY%"=="0" (
  echo .env sets GLITCHTIP_SECRET_KEY but COMPOSE_FILE does not include the
  echo observability overlay. Uncomment COMPOSE_PATH_SEPARATOR and COMPOSE_FILE
  echo in deploy\.env ^(see .env.example^), or remove the GlitchTip settings.
  popd & exit /b 1
)
if "%OVERLAY%"=="1" (echo Bringing up the core stack WITH the GlitchTip overlay.) else (echo Bringing up the core stack only - no GlitchTip overlay.)
rem findstr leaves errorlevel 1 on no match; clear it so the exit code below is docker's.
ver >nul

git pull

rem --ignore-buildable skips api/web/worker, which are built from source below, and
rem leaves only the third-party images. It needs no service list: COMPOSE_FILE in
rem .env already puts the overlay in play, so GlitchTip's images are covered when it
rem is switched on and absent when it is not.
echo Refreshing third-party images...
docker compose pull --ignore-buildable
if errorlevel 1 (
  echo.
  echo WARNING: could not refresh one or more third-party images - carrying on with
  echo          the versions already on this host. Re-run when the registry is
  echo          reachable, or the stack stays on them indefinitely.
  echo.
)
rem Clear the errorlevel the warning above may have left set, so the exit code
rem below is docker's from `up -d` and a failed pull is not reported as a failed
rem bring-up.
ver >nul

docker compose build
docker compose up -d
set "RC=%errorlevel%"
popd
exit /b %RC%
