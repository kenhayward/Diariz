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
docker compose build
docker compose up -d
set "RC=%errorlevel%"
popd
exit /b %RC%
