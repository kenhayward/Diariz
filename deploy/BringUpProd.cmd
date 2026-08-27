@echo off

rem ==========================================================================
rem BringUpProd.cmd - pull, rebuild everything, and bring the whole stack up
rem with the observability overlay.
rem
rem This is the COLD-START / full-stack tool: it rebuilds and recreates every
rem service, including postgres, redis, minio and the GPU worker. Use it for a
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
rem The observability overlay (docker-compose.observability.yml) adds the
rem self-hosted GlitchTip services; see docs/GlitchTip_Deployment.md.
rem
rem Usage:   run from this directory:  BringUpProd.cmd
rem ==========================================================================

git pull
docker compose build
docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d
