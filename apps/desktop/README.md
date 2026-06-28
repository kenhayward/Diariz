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
- **Record Microphone** / **Record System Audio** — start a recording in the background (no need to open
  the window); while recording these collapse to **Stop Recording (mm:ss)** with a live timer. Windows
  notifications confirm when recording starts and when the clip has uploaded.
- **Start with Windows** — launch Diariz automatically at login (off by default)
- **Check for Updates…** — check the release feed now (see Auto-update below)
- **Settings…** — change the server address
- **Quit**

The record items are disabled until the app is loaded and you're signed in (the recorder lives in the web
app). Recording is driven over IPC — the tray sends start/stop to the web app's `MediaRecorder` and it
reports its phase back so the tray label, tooltip, and notifications stay in sync; it's the **same** single
recorder as the on-screen **Record** button. The **System audio** option appears because the shell sets
`window.diariz.isElectron`.

## Auto-update

In a **packaged** build the app checks the release feed (the same one it was published to — GitHub Releases
by default, or a fork's self-hosted feed) on launch and every few hours, and downloads new versions in the
background. When one is ready, a notification appears and the tray gains a **Restart to update (x.y.z)** item;
clicking it (or the notification) relaunches into the new version. It also installs on the next normal quit.
Updates are a no-op in `npm start` / `npm run dev` (electron-updater only runs in packaged builds).

> Builds are currently **unsigned**, so Windows SmartScreen may warn on first install/update. Code signing
> is a later addition.

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

> **📌 Release reminder — temporary, remove once it's muscle memory.**
> A version bump alone does **not** publish an installer — you must push a matching **tag**. Whenever the
> desktop app changes and you want a downloadable build:
>
> 1. Land the change on `main` first, **including the version bump** (`version.json` + the web/desktop
>    `package.json` + the API `<Version>`), exactly like any other PR — then merge it.
> 2. From an up-to-date `main`, tag that commit and push the tag. **The tag must equal `version.json`,
>    prefixed with `v`:**
>    ```bash
>    git checkout main && git pull
>    git tag v0.12.3            # ← use the version from version.json
>    git push origin v0.12.3    # this push is what triggers the release build
>    ```
> 3. The push runs `desktop-release.yml` → builds and publishes `Diariz-Setup-<version>.exe` (+ `latest.yml`)
>    straight to GitHub Releases (no draft step; `releaseType: "release"`).
>
> If a tagged build fails: fix it, **bump to a new version**, and tag *that* — never reuse a tag. Delete a
> bad tag with `git push origin :refs/tags/v0.12.3 && git tag -d v0.12.3`.

Pushing a `v*` tag (matching `package.json` / `version.json`) runs `.github/workflows/desktop-release.yml`
on the self-hosted Windows runner, which builds the installer and publishes it to this repo's **GitHub
Releases** (+ `latest.yml`, which the app's auto-updater reads).

**Self-hosted feed** (no GitHub dependency, e.g. for a private fork): build with
`DIARIZ_PUBLISH=generic DIARIZ_UPDATE_URL=https://your-server/updates/ npm run dist` and upload
`release/*` (including `latest.yml`) to that URL.

Windows SmartScreen warns on unsigned installers — fine for internal/self-hosted use, or sign with an
OV/EV certificate in CI.

## System audio

The main process installs a `setDisplayMediaRequestHandler` returning `audio: "loopback"`, which on
Windows captures what the system is playing. macOS loopback needs ScreenCaptureKit entitlements (future).
