# Diariz desktop (Windows, Electron)

A system-tray Windows app that loads your Diariz server in a native window and adds
**microphone + Windows system/loopback audio** capture (a plain browser can't capture system output).

It's a **thin shell**: it loads the web app from your server's origin, so login, the API, SignalR, and
audio streaming are all same-origin — exactly like a browser — and the desktop rarely needs rebuilding
when the web app changes.

## First run

On first launch it asks for your **server address** (the same URL you'd open in a browser, e.g.
`https://diariz.example.com`). It's validated against the server's `/health` endpoint and saved. Change it
later from the tray's **Settings…**.

## Tray

The app lives in the system tray (closing the window hides it; it keeps running):

- **Open Diariz** — show/focus the window
- **Settings…** — change the server address
- **Quit**

> Recording from the tray (mic / system audio) lands in a later phase; for now record from inside the app
> window as usual. The **System audio** option appears because the shell sets `window.diariz.isElectron`.

## Develop

```bash
# 1. backend + web running (see the repo README); web dev server at http://localhost:5173
cd apps/desktop
npm install
npm run dev        # DIARIZ_DEV=1 → loads the Vite dev server, skips first-run setup
```

`npm test` runs the pure unit tests (`node --test`, no Electron needed).

## Build an installer

```bash
cd apps/desktop
npm install
npm run dist       # builds release/Diariz Setup <version>.exe (NSIS), no publish
```

## Release

Pushing a `v*` tag (matching `package.json` / `version.json`) runs `.github/workflows/desktop-release.yml`
on the self-hosted Windows runner, which builds the installer and publishes it to this repo's **GitHub
Releases** (+ `latest.yml` for auto-update in a later phase).

**Self-hosted feed** (no GitHub dependency, e.g. for a private fork): build with
`DIARIZ_PUBLISH=generic DIARIZ_UPDATE_URL=https://your-server/updates/ npm run dist` and upload
`release/*` (including `latest.yml`) to that URL.

Windows SmartScreen warns on unsigned installers — fine for internal/self-hosted use, or sign with an
OV/EV certificate in CI.

## System audio

The main process installs a `setDisplayMediaRequestHandler` returning `audio: "loopback"`, which on
Windows captures what the system is playing. macOS loopback needs ScreenCaptureKit entitlements (future).
