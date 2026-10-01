@echo off
setlocal

rem ==========================================================================
rem NewS3Keys.cmd - print a fresh S3 key pair for deploy\.env.
rem
rem SeaweedFS (the S3 store) reads its identities from the s3_identities
rem config in docker-compose.yml, so there is nothing to provision inside the
rem store: paste the two printed lines into deploy\.env. If the stack is
rem already running, also run
rem   docker compose up -d --force-recreate s3
rem because Compose does not notice a key change on its own.
rem
rem Run it once per key, once per server - never share keys between servers.
rem
rem Usage:   NewS3Keys.cmd             S3_APP_*        (the API and worker)
rem          NewS3Keys.cmd root        S3_ROOT_*       (administers the store)
rem          NewS3Keys.cmd glitchtip   GLITCHTIP_S3_*  (observability overlay)
rem
rem Nothing is written to disk.
rem ==========================================================================

set "KIND=%~1"
if "%KIND%"=="" set "KIND=app"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0s3-keys\new-keys.ps1" -Kind "%KIND%"
exit /b %errorlevel%
