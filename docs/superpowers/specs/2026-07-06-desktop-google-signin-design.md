# Design: Google sign-in in the Diariz desktop (Electron) client

**Date:** 2026-07-06
**Status:** Approved (brainstorming) - ready for implementation plan
**Scope:** Desktop Google sign-in only (no set-password fallback - tracked separately if wanted)

## Problem

The Diariz desktop client (an Electron tray shell that loads the web app from the server origin)
offers only username/password login. The "Sign in with Google" button is deliberately hidden in the
shell (`apps/web/src/pages/Login.tsx`, condition `googleEnabled && !isElectron`) because Google
refuses to render its consent screen inside an embedded webview (`disallowed_useragent`), and the
shell's `will-navigate` handler (`apps/desktop/src/main.js`) ejects any off-origin navigation to the
system browser, which would break the SPA flow mid-handshake.

The consequence is not a minor inconvenience: a user created via Google sign-in has **no password at
all** (`src/Diariz.Api/Services/GoogleSignInHandler.cs`: `// no password - Google is the credential`),
so Google-only users are **completely locked out** of the desktop client. This restricts who can use
the desktop app.

## Goal

Let Google users sign in from the desktop client with the same trust and UX as the web, without
changing the Google Cloud console configuration and without weakening the existing security model.

## Approach (chosen)

Reuse the **existing server-side confidential OAuth flow untouched**. Run the consent in the **system
browser** (the only user-agent Google allows), and hand the result back to the app via a **`diariz://`
custom protocol** deep link. The JWT never travels in a URL - only a **short-lived, single-use opaque
code** does, bound to a PKCE-style challenge the app holds.

Two decisions were settled during brainstorming:
- **Scope:** desktop Google sign-in only (set-password fallback is out of scope).
- **Transport:** custom protocol (`diariz://`), not a loopback HTTP server - it auto-refocuses the app
  after consent, avoids running a local web server in a long-lived tray process, and slots into the
  shell's existing single-instance / `second-instance` plumbing.

### Why this needs no Google-console change

Google still only ever redirects to the same `https://{host}/api/auth/google/callback` it already
knows. The `diariz://` hop is a purely **internal 302 emitted by our own server** *after* Google has
returned. Google never sees a custom scheme (which, as a "Web application" OAuth client, it would
reject).

## End-to-end flow

```
Desktop renderer          Electron main            System browser         Diariz API                 Google
-----------------         -------------            --------------         ----------                 ------
click "Sign in
with Google"
   | IPC: auth:start-google
   v
                    verifier = random
                    challenge = SHA256(verifier)   <- verifier stays in the app, never sent
                    shell.openExternal(
                      {server}/api/auth/google/start?mode=desktop&challenge=...)
                          |
                          v
                                            GET /start ----------------> folds {desktop, challenge}
                                                                         into the SIGNED state cookie,
                                                                         redirects to Google ---------> consent
                                            <--------------------------- /api/auth/google/callback <---- code+state
                                                                         (existing: validate state,
                                                                          exchange code, verify ID token,
                                                                          resolve/create user)
                                                                         desktop intent? ->
                                                                          mint one-time code in Redis
                                                                          {userId, challenge, TTL 2m, single-use}
                                            302 diariz://auth/callback?code=... <--
                          | OS delivers deep link
                          | (argv / second-instance)
                          v
                    POST /api/auth/desktop/exchange {code, verifier} ---> GETDEL code (atomic single-use);
                                                                         SHA256(verifier)==challenge?
                                                                         -> mint JWT
                    <--------------------------------------------------  { accessToken }
   | IPC: auth:token
   v
store token,
signed in (reuse
normal login path)
```

Key properties:
- **No Google-console change** - the redirect URI Google sees is unchanged.
- **JWT stays out of the URL** - the deep link carries only a single-use opaque code.
- **PKCE-style binding** - redeeming the code needs the `verifier`, held only by the initiating app,
  so another app hijacking the `diariz://` scheme cannot steal the session.
- **Desktop intent lives in the encrypted state cookie**, so a normal web login can never be coerced
  into emitting a `diariz://` redirect (no open-redirect gadget).

## Component changes

### Server (API) - all additive, no DB migration

- **`OAuthState`** (the record serialized into the existing Data-Protection-encrypted `diariz_g_oauth`
  state cookie) gains two fields: `Desktop` (bool) and `Challenge` (string, the S256 of the app's
  verifier). Set at `/start`, read at `/callback`. Because the cookie is encrypted, the client cannot
  forge the desktop intent.
- **`GET /api/auth/google/start`** - reads optional `mode=desktop` + `challenge` query params and folds
  them into the state it stashes. The Google PKCE leg and redirect to Google are unchanged.
- **`GET /api/auth/google/callback`** - after the existing sign-in resolves a user, branch on
  `state.Desktop`:
  - Web (existing): HttpOnly handoff cookie -> redirect to the SPA `/auth/google/callback`.
  - Desktop (new): mint a one-time code via `IDesktopAuthCodeStore`, then
    `Redirect("diariz://auth/callback?code=<code>")`. This is the **only** place a `diariz://` redirect
    is emitted, and only when the signed state says desktop; it deliberately bypasses `SafeRedirect`
    (which would reject a non-http scheme), guarded by the encrypted-state check.
- **`POST /api/auth/desktop/exchange`** (new, `[AllowAnonymous]`) - body `{ code, verifier }`. Redeems
  the code (single-use), checks `SHA256(verifier) == storedChallenge`, and on success mints the JWT
  with the existing `_tokens.CreateAccessToken(...)`, returning `{ accessToken }`. Any failure ->
  generic `401`. Mirrors the web `/exchange` response shape so SPA token handling stays uniform.
- **`IDesktopAuthCodeStore`** (new small service):
  - `Task<string> MintAsync(Guid userId, string challenge, TimeSpan ttl)`
  - `Task<DesktopAuthTicket?> RedeemAsync(string code)` (returns `{ UserId, Challenge }` or null)
  - Production impl: Redis, using `GETDEL` for atomic single-use and native key TTL (reuses the
    existing `IConnectionMultiplexer`). No migration.
  - Test impl: an in-memory fake in `Diariz.Api.TestSupport` (repo convention: add a fake, no mocking
    library), so unit tests exercise the controllers without Redis. An integration test covers the real
    Redis round-trip.

### Desktop shell (`apps/desktop`)

- **Protocol registration.** `electron-builder.config.js` gains
  `protocols: [{ name: "Diariz", schemes: ["diariz"] }]` so the NSIS installer registers `diariz://`
  at install time. In `main.js`, `app.setAsDefaultProtocolClient("diariz")` (with the explicit
  exec-path/args form when unpackaged, so `npm start` dev works too).
- **Deep-link delivery** (Windows-first, matching the app's existing scope):
  - Warm app: read the `diariz://...` URL from the existing `second-instance` handler's `argv` (today
    it only calls `showMainWindow()`).
  - Cold start: parse `process.argv` on `whenReady`.
  - A one-line `open-url` handler is included for future macOS; macOS is not a target today.
- **IPC + flow in `main.js`:**
  - `ipcMain.handle("auth:start-google")` -> generate `verifier`/`challenge`, keep `verifier` in a
    module-scoped pending var, `shell.openExternal({serverUrl}/api/auth/google/start?mode=desktop&challenge=...)`.
  - On deep link -> parse `code`, `POST {serverUrl}/api/auth/desktop/exchange {code, verifier}`, clear
    the pending verifier, `mainWindow.webContents.send("auth:token", accessToken)`, and focus the
    window. If the main window is not loaded yet (cold start / signed-out), queue the token and deliver
    once the renderer reports ready (reuse the existing recorder-ready ping as the "renderer is up"
    signal, or a small pending-token handshake).
- **`preload.js`** exposes two additions on `window.diariz`: `startGoogleSignIn()` (-> `auth:start-google`)
  and `onAuthToken(cb)` (<- `auth:token`).
- **New pure module `src/desktopAuth.js`** for the testable logic (mirrors `recorderState.js` /
  `updateState.js`): build the `/start` URL from a challenge, and parse a deep-link argv array -> the
  `code` (tolerating junk / no-code / multiple args). Crypto (verifier/challenge gen) stays in
  `main.js`; the pure string/URL parts are unit-tested with `node --test`.

### Web (SPA)

- **`Login.tsx`** - the Google block condition changes from `googleEnabled && !isElectron` to render in
  both contexts:
  - Web (existing): the `<a href="/api/auth/google/start">` anchor.
  - Electron (new): a button that calls `window.diariz.startGoogleSignIn()` (an anchor would just be
    ejected to the system browser by `will-navigate` and lose the flow). The stale "not inside the
    Electron shell" comment is rewritten.
- **`GoogleSignInButton`** - takes an optional `onClick`; when provided it renders a `<button>`
  (desktop), otherwise the existing `<a href>` (web). One component, no duplication.
- **`AuthProvider`** - in Electron only, subscribes to `window.diariz.onAuthToken(token)` on mount and
  feeds it through the **existing** sign-in-success path (persist JWT, schedule the silent refresh,
  populate the user) - identical to a password login from that point on. No new token-storage code.

## Security review

- **JWT never in a URL.** The deep link carries only a short-lived (~2 min), single-use opaque code.
- **Scheme-hijack resistance (main risk).** Another app could register `diariz://` and receive the
  code, but cannot redeem it without the `verifier`, which never left the initiating app (only its
  SHA-256 `challenge` transited the browser). PKCE applied to the internal code.
- **Single-use, atomic.** Redis `GETDEL` guarantees the code redeems exactly once; replays fail closed.
- **No open-redirect gadget.** The `diariz://` 302 is emitted only when the encrypted state cookie says
  `Desktop=true`; a client cannot set that, so a normal web login or a crafted `/start` cannot smuggle
  a session to an arbitrary scheme.
- **Strict deep-link parsing.** `main.js` acts only on `diariz://auth/callback`, extracts `code`, and
  ignores everything else - no arbitrary navigation or shell-out from scheme input.
- **Generic failures.** `/exchange` returns a bare `401` on expired / unknown / wrong-verifier - no
  oracle.
- **Transport.** `/exchange` is HTTPS to the configured server origin the app already trusts (validated
  at setup via `/health`).

Residual, accepted (documented, not solved):
- A co-installed malicious app that hijacks the scheme causes a **denial** of desktop Google sign-in (it
  swallows the deep link) - annoying, not a compromise.
- Dev + prod installs on one machine share the `diariz://` scheme (last registrant wins) - a known
  desktop-dev quirk.

## Testing (TDD, per-stack conventions)

- **API unit** (`Diariz.Api.Tests`, in-memory + a new in-memory `FakeDesktopAuthCodeStore` in
  `TestSupport`): `/start` folds `challenge` + `desktop` into the state; `/callback` emits a
  `diariz://...?code=` redirect for desktop state and the normal SPA handoff for web state; `/exchange`
  returns the JWT for a valid `{code, verifier}` and `401` for expired, unknown, already-redeemed, or
  wrong-verifier.
- **API integration** (`Diariz.Api.IntegrationTests`, real Redis): the Redis code store round-trip -
  mint -> redeem returns the ticket -> second redeem returns null (single-use + TTL).
- **Desktop unit** (`node --test`, no Electron): the pure `desktopAuth.js` - build the `/start` URL from
  a challenge; parse deep-link argv -> `code`, including junk / missing-code / multiple-args cases.
- **Web** (`vitest` + jsdom, fake `window.diariz`): `Login` renders the Google button in Electron and
  clicking it calls `startGoogleSignIn` (not an anchor navigation); `AuthProvider` signs in when a
  token arrives via `onAuthToken`. Plus i18n parity (en/es/fr/de) for any new string.

## Deploy surface & versioning

- **Desktop release required.** This touches `apps/desktop/src/**` and `electron-builder.config.js`,
  and the `diariz://` scheme only registers when the **new installer** runs - shipped by cutting a
  `v*` tag; existing desktop users must update. This is the one piece a web/API redeploy alone cannot
  deliver.
- **Server redeploy** for the API + web changes.
- **No DB migration** (Redis-backed code store) -> `Data_Schema.md` untouched;
  `Overall_Synopsis_of_Platform.md` gains the new desktop-OAuth handoff as a documented cross-boundary
  contract.
- **Versioning:** functional enhancement (new capability) -> **Minor +1**. Likely lands as a small
  series of PRs (API code store + endpoints -> desktop shell + protocol -> web button/AuthProvider),
  each shipping one release entry per the repo rule; the desktop-release note goes on whichever PR
  touches the shell.
- **i18n:** any new login strings added to all four locales (en/es/fr/de).

## Out of scope

- Set-a-password fallback for Google-only users (a separate, smaller feature).
- macOS/Linux desktop builds (the shell is Windows-only today).
- Any change to the Google Cloud OAuth client configuration.
