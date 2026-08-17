# Installable PWA (web app manifest)

Date: 2026-08-17
Status: approved, not yet implemented

## Problem

There is no Linux build of the desktop shell, and there is not going to be one soon. A Linux user reaches
Diariz only as a browser tab: no launcher entry, no application window, no icon in the dock or overview -
it lives among their other tabs and is found by hunting for it.

Everything *else* about Linux has been closed off already. System audio - the one capability that looked
like it needed a native shell - works there through the PipeWire drop-in
(`apps/web/public/linux/99-diariz-system-audio.conf`), installed either per-user from the help article or
fleet-wide via the `diariz-system-audio` .deb, which publishes the speaker monitor as an ordinary input
device. So the microphone path in the browser already captures system audio on Linux, with no Electron
involved.

What remains missing is only the *window*. That is what this spec adds.

A web app manifest is the whole mechanism: Chromium then offers to install the site, and the installed app
gets a launcher entry, its own icon, and a chromeless window - on Linux, and incidentally on every other
platform too.

## Goal

Make the web app installable in Chromium browsers, and make that discoverable from inside the app.

## Non-goals

- **No service worker, and therefore no offline mode.** Chromium dropped the service-worker requirement for
  installability in version 108 (mobile) and 112 (desktop) - a manifest over HTTPS is sufficient. Adding one
  anyway would introduce a second caching layer in front of the SPA shell, and the `no-cache` block on
  `index.html` in `apps/web/nginx.conf` exists precisely because a stale shell pins clients to the whole of
  a previous build. Offline, the installed app will show Chromium's default error page. That is the accepted
  behaviour, not an oversight.
- **No tray or menu-bar presence, no always-on-top notes popout, no auto-update prompt.** These are Electron
  capabilities with no web equivalent. A PWA is the window, not the shell.
- **No notifications, `file_handlers`, `protocol_handlers`, or `window-controls-overlay`.** All reachable from
  a manifest, none needed to close the Linux gap. Deferred deliberately.
- No change to how system audio is captured, on any platform.
- No change to the desktop shell's runtime code. The icon generator it owns gains outputs, but nothing in
  `apps/desktop/src/**` is touched.

## Installability criteria being met

Chromium requires all of the following. Each is listed with where this design satisfies it, because a single
miss makes the install offer silently not appear - there is no error surfaced to the user.

| Requirement | Satisfied by |
|---|---|
| Served over HTTPS (or localhost) | Production and `dev.diariz` are behind TLS; `localhost:8081` (compose) and `localhost:5173` (vite) both qualify as localhost |
| `name` or `short_name` | Both set |
| `start_url` | `/` |
| `display` is `standalone`, `minimal-ui` or `fullscreen` | `standalone` |
| `icons` contains a 192px **and** a 512px icon | Both, as PNG, with real matching pixel dimensions |
| `prefer_related_applications` absent or `false` | Absent |
| The manifest is reachable and parses | `<link rel="manifest">` plus the nginx MIME fix below |

## The manifest

`apps/web/public/manifest.webmanifest`, static and hand-maintained. It deliberately carries **no version
number** - the same reasoning the README uses: a mirrored version there would drift, and nothing about the
manifest is version-dependent.

```json
{
  "name": "Diariz",
  "short_name": "Diariz",
  "description": "Record, transcribe and diarize your meetings.",
  "start_url": "/",
  "scope": "/",
  "display": "standalone",
  "theme_color": "#4f46e5",
  "background_color": "#4f46e5",
  "icons": [
    { "src": "/icons/icon-192.png", "sizes": "192x192", "type": "image/png" },
    { "src": "/icons/icon-512.png", "sizes": "512x512", "type": "image/png" },
    { "src": "/icons/icon-maskable-512.png", "sizes": "512x512", "type": "image/png", "purpose": "maskable" }
  ]
}
```

Linked from `apps/web/index.html` beside the existing favicon link.

### `display: standalone` and the missing back button

`standalone` gives no browser chrome at all, which is the point - it is what makes the window read as an
application rather than a tab. The cost is that there is no visible back button, and the folder drill-in
design banks on browser back (`apps/web/src/lib/drillRoute.ts`: "browser **back pops a level**").

Accepted, because back is a convenience there rather than the only route out: `SectionBreadcrumb` pops any
number of levels, and in an installed window Alt+Left, the mouse back button, and the right-click menu all
still navigate back. `minimal-ui` was considered and rejected - it keeps a visible back/reload strip at the
cost of looking like a browser and losing vertical space.

### `theme_color: #4f46e5`

The installed window's titlebar is tinted by `theme_color`, and a manifest value is static while the app has
light/dark/auto themes (applied pre-paint by the inline script in `index.html`). A single value cannot follow
the theme.

The brand indigo is chosen because it reads as deliberate in both themes. Setting it to the light theme's
surface colour would look seamless in light mode and produce a bright bar above a dark app in dark mode -
exactly the failure the pre-paint script exists to avoid for the page itself. Syncing a `<meta name="theme-color">`
to the active theme was considered and rejected as unreliable: whether Chromium honours the meta over the
manifest for a desktop titlebar varies by version, so it would need live re-verification on every upgrade to
stay true.

## Icons

Three new PNGs in `apps/web/public/icons/`, generated by extending the existing
`apps/desktop/build/make-app-icon.js` with a second output pass writing `../../web/public/icons/`.

Reusing that generator rather than writing a new one keeps a single definition of the glyph geometry. The
mark already exists four times - `integrations/n8n-nodes-diariz/nodes/Diariz/diariz.svg`,
`apps/web/public/favicon.svg`, `apps/desktop/build/icon.png`, and the monochrome `trayTemplate.png` - each
held in step by a comment rather than a check. Adding a fifth independent redraw would widen that surface;
adding outputs to an existing generator does not.

| File | Content |
|---|---|
| `icon-192.png` | The existing mark (white microphone on an indigo rounded square) at 192x192 |
| `icon-512.png` | The same at 512x512 |
| `icon-maskable-512.png` | **Different geometry**: full-bleed indigo with the microphone inset into the central 80% safe circle |

The maskable variant is not a resize. A maskable icon is cropped by the platform to an arbitrary shape -
circle, squircle, rounded square - so only the central 80% diameter is guaranteed visible. Feeding the
rounded-square mark in unchanged would have its corners shaved and its silhouette clipped. Full-bleed
background plus an inset glyph is what survives every mask.

The generator stays dependency-free and continues to be run by hand (`node build/make-app-icon.js` from
`apps/desktop`); the committed PNGs remain the source of truth, as its header comment already states.

## nginx

Two additions to `apps/web/nginx.conf`.

**1. A MIME type for `.webmanifest`.** Verified against the runtime image rather than assumed:

```
docker run --rm nginx:alpine sh -c "grep -n 'manifest' /etc/nginx/mime.types"
```

returns nothing on nginx 1.31.2 - the default `mime.types` has no `manifest` entry, so the extension is
undetermined and the file would be served as `application/octet-stream`. Vite's dev server resolves
`.webmanifest` correctly, so this is a divergence that appears only on a deployed box. Fixed with a
`default_type` inside the manifest's own `location`:

```
location = /manifest.webmanifest {
    default_type application/manifest+json;
    ...
}
```

**Not a `types { ... }` block.** This was tried first and is wrong in a way that passes every check short of
a real HTTP request: a `types` block **replaces** the inherited MIME map rather than extending it, so naming
this one extension turned `index.html`, the JS bundle, and every PNG into `application/octet-stream` - a
completely unusable app - while `nginx -t` still reported the configuration as valid. `default_type` governs
exactly the case at hand (an extension absent from the map) and is scoped to the one location, so nothing
else moves. `manifest.test.ts` carries a regression guard asserting no `types` block is ever reintroduced.

**2. Explicit caching for the manifest and icons.** Files in `public/` land at the web root, not under
`/assets/`, so they fall through to `location /` with no `Cache-Control` - which is the heuristic-freshness
behaviour (RFC 9111 4.2.2) the `index.html` block in that file already warns about at length. The manifest
gets `no-cache` for the same reason `index.html` does: it is the document that *names* the icons, so a
stale copy pins the installed app's identity. `/icons/` gets a long immutable cache, since the mark is
stable and a change to it can change the filename.

An outer reverse proxy needs no new configuration: unlike `/mcp`, nothing here streams.

## The install affordance

Chromium's own install entry point is an easily-missed icon in the omnibox, which is not adequate
discoverability for the platform this exists to serve. The app offers its own.

### `apps/web/src/lib/installPrompt.ts`

Self-contained and testable without React:

- A **module-level** `beforeinstallprompt` listener that calls `preventDefault()` (suppressing Chromium's own
  mini-infobar) and stashes the event. Module-level is load-bearing: the event fires shortly after page load,
  potentially before React has mounted, so registering it in an effect would miss it and the row would never
  appear.
- An `appinstalled` listener that clears the stashed event.
- `useInstallPrompt(): { canInstall: boolean; install: () => void }`. `install()` calls `prompt()` on the
  stashed event; a stashed event is single-use, so it is cleared after prompting.

`canInstall` is false in any of these cases:

| Case | Why | Detection |
|---|---|---|
| No event arrived | Not installable (wrong browser, http, already installed, criteria unmet) | Nothing stashed |
| Already running installed | Offering to install the app you are in is nonsense | `matchMedia("(display-mode: standalone)").matches` |
| Inside the Electron shell | It *is* the desktop app | `isElectron` from `apps/web/src/lib/audioSource.ts` |

No user-agent sniffing anywhere. The row appears wherever the browser says installing is possible - Linux
users get it, and a Windows or macOS user who would rather not install Electron can use it too. The About
box and help article are where the trade-off is explained, not a platform-conditional label.

### The menu row

One conditional row in `apps/web/src/components/UserMenu.tsx`, in the existing `role="menu"` block and
matching the shape of the rows already gated there (`{isAdmin && <MenuRow ... />}`). It reuses `MenuRow`, so
it inherits the hover treatment, padding, and `role="menuitem"` accessible name for free - no new UI is
designed.

Label lives in the `account` namespace across all four locales (`en`, `de`, `es`, `fr`). Plain hyphens only,
never em or en dashes.

## Testing

TDD throughout: each test written and seen to fail before the code that satisfies it.

Three files, all in `apps/web/src`: `lib/manifest.test.ts` (the manifest asset, its icons, and the icon
generator drift guard - all of it static-asset assertion, so it sits beside `lib/linuxSystemAudio.test.ts`
which does the same job for the PipeWire drop-in), `lib/installPrompt.test.ts`, and additions to the existing
`components/UserMenu` test coverage.

| Test | What it pins |
|---|---|
| `manifest.test.ts` - required fields | `name`/`short_name`, `start_url`, `display: standalone`, both required icon sizes declared, `prefer_related_applications` absent. Follows `linuxSystemAudio.test.ts`, which treats a `public/` asset as a deliverable and pins the properties it must have to work at all |
| `manifest.test.ts` - icons are real | Each declared PNG exists, and its true pixel dimensions (parsed from the PNG IHDR header, no dependency) match the `sizes` string. A manifest declaring a size the file does not have fails installability with nothing surfaced to the user |
| `manifest.test.ts` - maskable present | A `purpose: "maskable"` entry exists, so the Linux launcher icon is not corner-shaved |
| icon drift guard | `make-app-icon.js` references the `apps/web/public/icons` output paths - the same guard shape as the `build-deb.sh` assertion at the end of `linuxSystemAudio.test.ts`, and for the same reason: if the generator is repointed, the committed icons start ageing silently |
| `index.html` link | A `rel="manifest"` link exists and names the file that exists. An unlinked manifest is inert |
| `installPrompt.test.ts` | Event stashed then `canInstall` true; `install()` calls `prompt()`; suppressed when already standalone; suppressed under Electron; false when no event ever arrived |
| `UserMenu` component test | The row renders only when `canInstall`, and clicking it calls `install()`. Existing pattern: RTL inside `MemoryRouter` + `QueryClientProvider`, `vi.mock` of `../lib/api` |

Every assertion above must be mutation-verified - break the thing, watch the specific test fail with the
real message - because a test that reads a file and asserts a field is present is exactly the shape that can
pass while proving nothing.

**What tests cannot cover.** jsdom computes no window geometry and cannot install anything, so the installed
window is verified by hand once on the dev server: install from Chromium, confirm the launcher entry and icon
appear, confirm the window opens with no browser chrome and an indigo titlebar, and confirm a recording still
starts and uploads inside it. The Chromium DevTools Application > Manifest panel reports installability
errors directly and is the fastest check that the criteria table above is actually satisfied.

## Release chores

Functional enhancement, so Minor +1: **0.219.0 -> 0.220.0**.

1. `version.json` and all four mirrors (`apps/web/package.json`, `apps/desktop/package.json`,
   `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`).
2. `RELEASES[0]` in `apps/web/src/lib/releases.ts`. The `pr` number is confirmed from the real PR at
   `gh pr create` time - not guessed as "last + 1", since Dependabot and issues share that sequence.
3. `CAPABILITIES` table row in `releases.ts` - this is a new user-facing capability. No new third-party
   library or model, so the `AboutModal.tsx` disclaimers are untouched.
4. README Features table row.
5. The matching `docs/features.md` prose bullet, updated in lockstep with the README row.
6. `docs/Overall_Synopsis_of_Platform.md` - the new deployable artefact (a manifest + icons) and the nginx
   MIME requirement, which an outer proxy or alternative web server would also need.
7. `docs/Data_Schema.md` - **not touched**. No schema, storage, or migration change.

The Linux section of `apps/web/src/content/help/en/recording-audio.md` gains a short pointer to Install,
since installing is now part of the behaviour a Linux user relies on - but the article is not rewritten to
mirror the features table, per the "help articles are not a fourth sync target" rule.

**Deployment surface: server redeploy only.** No desktop release is needed. `apps/desktop/src/**`,
`build/**` shipped assets, `electron-builder.config.js`, and desktop dependencies are all untouched -
`make-app-icon.js` is a build-time generator, not shipped code - so the lockstep bump to
`apps/desktop/package.json` does not require a new installer.

## Known gaps after this ships

Stated so they are not mistaken for bugs:

- Offline shows Chromium's default error page.
- No tray or menu-bar icon, so no start/stop recording from outside the window.
- The notes popout stays Electron-only; it needs an always-on-top window, which the web platform does not offer.
- No auto-update prompt; the installed PWA picks up new builds by the same `no-cache` shell revalidation as
  a browser tab.
- Firefox on Linux does not install PWAs. This is Chromium-only, matching where system audio works anyway.
