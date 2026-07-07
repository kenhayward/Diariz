@echo off
setlocal enabledelayedexpansion

rem ==========================================================================
rem PushVersion.cmd - cut a Diariz desktop release by pushing a v<version> tag.
rem
rem The "Desktop release" workflow (.github/workflows/desktop-release.yml)
rem triggers on any pushed tag matching v*, then builds and publishes the
rem Windows installer to GitHub Releases. By convention the tag matches the
rem app version in version.json / apps/desktop/package.json.
rem
rem Usage:   PushVersion.cmd <version>|--current
rem   e.g.   PushVersion.cmd 0.98.0     push tag v0.98.0
rem          PushVersion.cmd v0.98.0    (a leading v is accepted and stripped)
rem          PushVersion.cmd --current  push the tag matching version.json
rem
rem Run this from an up-to-date "main" checkout - the tag is placed on the
rem current HEAD, so make sure the release commit is checked out first.
rem ==========================================================================

set "ARG=%~1"
if "%ARG%"=="" (
  echo Usage: %~nx0 ^<version^>^|--current
  echo   e.g. %~nx0 0.98.0        push tag v0.98.0
  echo        %~nx0 --current      push the tag matching version.json
  exit /b 1
)

rem Repo root is the parent of this script's folder (deploy\).
set "ROOT=%~dp0.."

rem Read the canonical app version from version.json (used by --current and the match check).
set "REPOVER="
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "try { (ConvertFrom-Json (Get-Content -Raw '%ROOT%\version.json')).version } catch {}"`) do set "REPOVER=%%v"

if /i "%ARG%"=="--current" (
  if "!REPOVER!"=="" (
    echo Could not read the version from "%ROOT%\version.json".
    exit /b 1
  )
  set "VER=!REPOVER!"
) else (
  rem Accept "v0.98.0" as well as "0.98.0".
  if /i "!ARG:~0,1!"=="v" set "ARG=!ARG:~1!"
  set "VER=!ARG!"
  if not "!REPOVER!"=="" if /i not "!REPOVER!"=="!ARG!" (
    echo WARNING: version.json is !REPOVER! but you asked to tag !ARG!.
    echo The desktop-release convention is that the tag matches the app version.
    set /p "CONT=Continue anyway? [y/N] "
    if /i not "!CONT!"=="y" (
      echo Aborted.
      exit /b 1
    )
  )
)
set "TAG=v!VER!"

echo.
echo About to create and push tag %TAG% to origin ^(cuts a desktop release^).
set /p "OK=Proceed? [y/N] "
if /i not "%OK%"=="y" (
  echo Aborted.
  exit /b 1
)

git -C "%ROOT%" tag -a "%TAG%" -m "Desktop release %VER%"
if errorlevel 1 (
  echo Failed to create tag %TAG% ^(does it already exist?^).
  exit /b 1
)

git -C "%ROOT%" push origin "%TAG%"
if errorlevel 1 (
  echo Failed to push tag %TAG%. Removing the local tag so you can retry.
  git -C "%ROOT%" tag -d "%TAG%" >nul 2>&1
  exit /b 1
)

echo.
echo Pushed %TAG%. The "Desktop release" workflow will build + publish the installer.
echo Watch it: gh run list --workflow "Desktop release"
endlocal
