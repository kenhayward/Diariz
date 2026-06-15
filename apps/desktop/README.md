# Diariz desktop (Electron)

Thin Electron shell that loads the React web app and adds **microphone + Windows
system/loopback audio** capture (a plain browser cannot capture system output).

## Run (dev)

In separate terminals:

```bash
# 1. backend stack
cd deploy && docker compose up        # api, postgres, redis, minio, worker

# 2. web dev server
cd apps/web && npm install && npm run dev

# 3. desktop shell (loads http://localhost:5173 via the Vite proxy)
cd apps/desktop && npm install && npm run dev
```

`npm run dev` sets `DIARIZ_DEV=1`, which loads the Vite dev server (so `/api` and
`/hubs` are proxied to the backend) and exposes `window.diariz.isElectron = true`,
enabling the "System audio" option in the recorder.

## System audio

`main.js` installs a `setDisplayMediaRequestHandler` returning `audio: "loopback"`,
which on Windows captures what the system is playing. macOS loopback needs
ScreenCaptureKit entitlements (Milestone 4).

## Packaging

Production packaging (electron-builder) and loading the built `apps/web/dist` with a
configured API base + CORS origin is deferred to Milestone 4.
