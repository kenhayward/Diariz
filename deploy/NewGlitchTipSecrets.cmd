@echo off
setlocal

rem ==========================================================================
rem NewGlitchTipSecrets.cmd - generate GlitchTip's secrets and assemble its
rem EMAIL_URL, ready to paste into deploy\.env.
rem
rem Run this ONCE PER SERVER. Dev and prod must NOT share secrets: the two
rem instances are deliberately independent, and a shared SECRET_KEY would let
rem a session or a signed value from one be replayed against the other.
rem
rem Generates:
rem   GLITCHTIP_SECRET_KEY          Django signing key
rem   GLITCHTIP_POSTGRES_PASSWORD   its own database, not the app's
rem   GLITCHTIP_EMAIL_URL           correctly percent-encoded (prompted)
rem   GLITCHTIP_FROM_EMAIL
rem
rem It does NOT generate the MinIO key - that one has to be created inside
rem MinIO itself, so it lives in ProvisionGlitchTipMinio.cmd.
rem
rem Nothing is written to disk. Copy the output into deploy\.env and close
rem the window.
rem
rem Usage:   NewGlitchTipSecrets.cmd
rem          NewGlitchTipSecrets.cmd /noemail     (secrets only, skip SMTP)
rem ==========================================================================

set "PSARGS="
if /i "%~1"=="/noemail" set "PSARGS=-NoEmail"
if /i "%~1"=="-noemail" set "PSARGS=-NoEmail"
if /i "%~1"=="--noemail" set "PSARGS=-NoEmail"
if not "%~1"=="" if "%PSARGS%"=="" (
  echo Unknown argument "%~1". Usage: %~nx0 [/noemail]
  exit /b 2
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0glitchtip-secrets\new-secrets.ps1" %PSARGS%
exit /b %errorlevel%
