# Design: User API access (personal tokens) + browsable API reference

**Date:** 2026-07-07 · **Status:** approved design, pending spec review

## Goal

Let platform users call the Diariz REST API programmatically and understand its calls: a durable
per-user credential plus a curated, browsable API reference. Gated by a platform-wide switch that is
**off by default**.

## Decisions (locked)

- **Scope:** credential + docs, one design, **two PRs**.
- **Key permissions:** full parity with the user's session - the key authenticates as the user with
  all their claims (including admin roles). Owner-scoped either way.
- **Docs access:** signed-in platform users only (served as an in-app SPA route, behind the app login).
- **Platform gate:** a Platform-Admin enable/disable switch, **default disabled**. Ships "dark".
- **Admin home:** a new **Integration** tab in the platform admin Settings panel (extensible later).

## Architecture

### Credential (PR A)

- **Token:** `dz_api_` + base64url(32), stored **SHA-256-hashed only**, shown once, named, revocable -
  the exact `dz_mcp_` recipe. New `ApiAccessTokens` table + migration. Thin `ApiTokenService` /
  `ApiTokenAuthenticator` mirroring the MCP ones, kept **separate** from MCP (different scope/lifetime -
  clean separation, not duplication).
- **CRUD:** `ApiTokensController` at `/api/user/api-tokens` (JWT-authed, mirrors `McpTokensController`).
  Multiple named tokens per user; revoke = delete.
- **Auth scoping (the crux):** a new `"ApiKey"` authentication scheme. Its handler recognises a
  `dz_api_` bearer, validates the hash, bumps `LastUsedAt`, and builds a principal with the user's id
  **and roles** (full parity). To make it satisfy **every** `[Authorize]` variant - including
  `[Authorize(Roles=…)]` endpoints, which authenticate with the default *authenticate* scheme - the
  **default authenticate scheme becomes a forwarding policy scheme**: `Bearer dz_api_…` routes to the
  `ApiKey` handler, everything else (JWT bearer, or no header for the SignalR/audio/backup query-string
  flows) routes to JWT. Existing flows are unchanged; `/mcp` names its own scheme and is unaffected.
  (Refined from the initial "add both schemes to the default *authorization* policy" idea, which does
  not cover `[Authorize(Roles=…)]`.)
- **Isolation (test-enforced):** a `dz_mcp_` token is rejected on `/api/*`, a `dz_api_` token is
  rejected on `/mcp` (each scheme accepts only its own prefix). `internal/*` (X-Worker-Secret) is
  unaffected.

### Platform gate (PR A)

- **`PlatformSettings.ApiAccessEnabled`** (bool, default **false**), same pattern as the audio-retention
  flags; folded into PR A's migration.
- **Enforcement:** the `ApiKey` handler resolves `ApiAccessEnabled` (from `PlatformSettings`) and fails
  authentication when the feature is off - so no key works until a Platform Admin enables it.
- **Flag exposure:** expose `apiAccessEnabled` to authenticated users (read-only, e.g. on the user
  profile/settings response) so the SPA can show the Developers tab only when enabled.

### Docs (PR B)

- **Curated OpenAPI:** enable `MapOpenApi()` in production but **filter** the document - exclude
  `internal/*` (worker callbacks), the OAuth/OpenIddict + `.well-known` plumbing, and non-REST
  surfaces. Declare both security schemes (JWT + `dz_api_`) so Scalar's Authorize / try-it works. Serve
  the JSON under `/api/openapi/v1.json` (existing nginx `/api` proxy covers it) and require auth on it.
- **In-app Scalar reference:** a React route (`/developers/api`) rendering Scalar
  (`@scalar/api-reference-react`) pointed at the authed OpenAPI JSON with the user's JWT. Being an SPA
  route, it's already behind login -> "signed-in platform users only", no separate gate.

### UI

- **Developers / API access tab** (Preferences): new `DeveloperAccessSection` mirroring
  `McpAccessSection` - list / create-once / revoke keys, show the base URL and a copyable example. Shown
  only when `apiAccessEnabled`. Gets a "View API reference" button (-> `/developers/api`) in PR B. Added
  to `PreferencesModal` (`PreferencesTab` + left-nav entry).
- **Integration tab** (platform admin `SettingsModal`, Platform-Admin only, beside Quotas/Maintenance):
  the enable/disable switch (PR A) + the "View API reference" link (PR B). Extensible for future
  platform-level integration settings.

## Decomposition

**PR A - Credential + platform gate (feature ships disabled).**
`ApiAccessToken` entity + migration (incl. `PlatformSettings.ApiAccessEnabled`), `ApiTokenService` /
`ApiTokenAuthenticator`, the `ApiKey` scheme + default-policy change, `ApiTokensController`, expose
`apiAccessEnabled`, admin **Integration** tab with the toggle, Preferences **Developers / API access**
tab (token management + base URL), doc/schema updates. Functional enhancement -> `0.101.0`.

**PR B - Browsable reference.**
Curated production OpenAPI (filtered + security schemes), in-app Scalar route (`/developers/api`), the
"View API reference" links in the Developers tab and the Integration tab. Functional enhancement ->
`0.102.0`.

## Testing (TDD)

- **Unit:** `ApiTokenService` (generate/hash), `ApiTokenAuthenticator` (hash lookup, `LastUsedAt`,
  disabled-feature rejection), OpenAPI curation (excluded paths absent, security schemes present).
- **Integration:** the auth matrix - JWT OK; `dz_api_` OK on `/api` when enabled; `dz_api_` rejected
  when the platform switch is off; `dz_mcp_` rejected on `/api`; `dz_api_` rejected on `/mcp`; admin key
  OK on an admin endpoint.
- **Web:** `DeveloperAccessSection` (create/revoke, hidden when disabled), Integration tab toggle,
  Scalar route renders.

## Docs / versioning / deploy

- Update `Data_Schema.md` (new `ApiAccessTokens` table + `PlatformSettings.ApiAccessEnabled`),
  `Overall_Synopsis_of_Platform.md` (new auth scheme, platform gate, docs endpoint), and the About-box
  `CAPABILITIES` (new user-facing feature).
- Both PRs: **server redeploy (web + API)**, no desktop release. PR A adds a migration; PR B adds the
  `Scalar.AspNetCore` NuGet dep (+ `@scalar/api-reference-react` web dep) and possibly one nginx line.

## Optional / deferred (YAGNI for now)

- Scoped tokens (read-only vs read-write) - deferred; v1 keys are full parity.
- Per-token rate limiting - revisit if abuse appears.
- The Integration tab is intentionally minimal now; future integration settings extend it.
