# Diariz Integrations: Outbound Webhooks + Inbound Hardening - Design Spec

**Date:** 2026-07-23
**Status:** Approved design, ready for implementation planning
**Deployment surface:** Server redeploy only (web + API). No desktop release. Additive, forward-restore-safe migrations (no `MaintenanceController.CurrentFormat` bump).
**Version impact:** Functional enhancement -> Minor bump.

---

## 1. Goal

Let external orchestration tools (Zapier, n8n, Make, Pipedream, and any HTTP-capable
platform) integrate with Diariz in both directions:

- **Outbound (triggers):** Diariz emits signed HTTP webhook events when recordings and
  formula results change state, so a workflow can react.
- **Inbound (actions):** a workflow calls Diariz's REST API to fire a formula and retrieve
  its output. Personal API tokens gain coarse scopes and optional expiry so a token pasted
  into a third-party cloud tool can be least-privilege and time-boxed.
- **Workflow Signals:** a formula author attaches a plain-language named signal ("Send to
  Slack") to a formula; the signal rides outward on the completion event; a platform admin
  wires that named signal to a workflow once, for everyone.

The canonical end-to-end flow this enables:

> A workflow fires a formula (REST action) -> Diariz runs it -> `formula_result.completed`
> / `formula_result.failed` webhook fires (optionally carrying a named signal) -> the
> workflow retrieves the detailed output (REST GET), or receives it inline when routed via
> a platform signal.

## 2. Architecture summary

Two loosely-coupled subsystems, one platform-admin gating layer, shipped together.

- **Outbound webhooks** use a **Postgres-backed delivery queue** polled by a
  `BackgroundService` delivery worker (not a new Redis stream), because webhooks need
  scheduled retries with backoff and a durable, user-visible delivery history. This keeps
  self-hosted deployments free of any external delivery provider (no Svix).
- **Signing** follows the **Standard Webhooks** open spec (HMAC-SHA256, `webhook-id`,
  `webhook-timestamp`, `webhook-signature`), which Zapier and n8n users can verify.
- **Inbound hardening** adds two columns to the existing `ApiAccessToken` and enforces them
  in the existing ApiKey auth path, plus gives "run a formula / fetch a result" first-class
  REST parity (today running a formula is MCP-only).
- **Platform gating** splits the current single `PlatformSettings.ApiAccessEnabled` toggle
  into three independent admin switches: **API**, **MCP**, **Webhooks**.

## 3. Tech stack / reuse

- ASP.NET Core (.NET 10) + EF Core + Postgres. New entities + one additive migration.
- Delivery worker follows the existing `SummarizationWorker` polling `BackgroundService`
  pattern (ensure-loop, DI scope per item, resilient to failure).
- Signing secret encrypted at rest via the existing `IApiKeyProtector` Data Protection
  pattern (must be recoverable to sign, so encrypted, not hashed like tokens).
- Outbound URL validated with the existing SSRF guards (`UrlFetcher` / `SafeRedirect`).
- React 19 + TS + Tailwind v4 web UI; four locale catalogues (en/de/es/fr); no em/en dashes
  in user-facing copy.

## 4. Scope

### In scope (v1)

1. Outbound webhook subscriptions (personal + platform-scoped), delivery, signing, retry,
   auto-disable, delivery log.
2. Event catalog: `recording.created`, `recording.transcribed`,
   `recording.transcription_failed`, `formula_result.completed`, `formula_result.failed`.
3. Workflow Signals: admin-defined named signals, formula attachment, signal-on-event,
   platform-scoped subscriptions that route by signal, inline formula output on
   signal-routed formula deliveries.
4. Inbound hardening: `ApiAccessToken` gains `Scope (ReadOnly|ReadWrite)` and `ExpiresAt`;
   REST parity for running a formula and fetching a formula result; OpenAPI completeness.
5. Three granular platform toggles (API / MCP / Webhooks).
6. Approachable end-user "Automations" UI, admin management UI, formula-author signal picker.
7. Bundled fix: the two in-app API-reference links open in a new tab; the Preferences one no
   longer lands on a blank page.

### Out of scope (noted for future)

- Published Zapier app / n8n community node (generic webhooks + documented REST cover them).
- Room-scoped personal subscriptions (only personal + platform scopes in v1).
- The "AI outputs ready" events (`recording.summarized`, `.minutes_ready`,
  `.action_items_ready`, `.tags_ready`) and `speaker.identified` - same pipeline, easy to
  add later.
- Fine-grained per-resource token scopes (only coarse read/write in v1).
- MCP-based triggers, inbound webhooks (Diariz consuming external webhooks).
- Fat/opt-in payloads for personal subscriptions (personal deliveries stay thin).

## 5. Terminology

| Term | Meaning |
|---|---|
| **Automation** (user-facing) | A user's own outbound webhook subscription to their external tool. Surfaced in Preferences as "Automations". |
| **Webhook subscription** (internal) | The `WebhookSubscription` row backing an Automation (personal) or an admin routing rule (platform). |
| **Workflow Signal** | An admin-defined, named routing key (e.g. `post-to-slack`) that a formula author can attach to a formula. |
| **Event** | An outbound occurrence (e.g. `recording.transcribed`) delivered to matching subscriptions. |
| **Delivery** | One attempt-tracked send of one event to one subscription (`WebhookDelivery`). |

## 6. Platform gating (three granular toggles)

Extend the existing `PlatformSettings` singleton (which today carries `ApiAccessEnabled`,
default off):

| Field | Default | Governs |
|---|---|---|
| `ApiAccessEnabled` (exists) | **off** (unchanged) | Personal `dz_api_` REST tokens + the user API-access surface. |
| `McpAccessEnabled` (new) | **on** (seeded true for the existing row) | The `/mcp` server + `dz_mcp_` token issuance + the MCP user surface. |
| `WebhooksEnabled` (new) | **off** | The Automations surface + all webhook subscriptions/delivery. |

Rules:

- Each platform toggle is **bounded by the deployment env kill-switch**. Effective MCP
  availability = env `Mcp:Enabled` **AND** `McpAccessEnabled`. Env can hard-disable; the
  admin toggle governs runtime when env permits.
- `McpAccessEnabled` **must be seeded `true`** for the existing singleton row in the
  migration, so shipping this does not silently disable the live claude.ai MCP connector.
  New installs also default it true (MCP is a shipped capability).
- When a capability is **off**, its user endpoints return **404** and its UI is **hidden**
  entirely - the user sees nothing about it.
- Admin UI: the Settings modal's Integration section (Platform-Administrator-only) shows the
  three toggles with help text.

## 7. Data model

All new tables live in `DiarizDbContext`; one additive EF migration.

### 7.1 `WebhookSubscription`

| Column | Type | Notes |
|---|---|---|
| `Id` | guid PK | |
| `OwnerUserId` | FK -> ApplicationUser, **cascade delete** | The user (personal) or the admin who created it (platform). |
| `Scope` | enum `Personal` \| `Platform` | Platform requires `ManagePlatform` to create/edit. |
| `Name` | string | Friendly label. |
| `Url` | string | Delivery target; https required (http allowed only for localhost in dev); SSRF-validated. |
| `SecretEncrypted` | string | Signing secret, encrypted at rest (Data Protection). Shown to user once at creation. |
| `EventTypes` | string (delimited or json array) | Which event types this subscription wants. |
| `SignalFilter` | string? (json array of signal keys) | Optional. Platform subscriptions route on this; personal may also narrow by it. |
| `IsActive` | bool | User/admin on-off. |
| `ConsecutiveFailures` | int | Reset on any success. |
| `DisabledReason` | string? | Set when auto-disabled. |
| `LastDeliveryAt` | timestamptz? | For the status pill. |
| `LastStatus` | string? | Last delivery outcome summary. |
| `CreatedAt` | timestamptz | |

### 7.2 `WebhookDelivery` (queue + audit log)

| Column | Type | Notes |
|---|---|---|
| `Id` | guid PK | |
| `SubscriptionId` | FK -> WebhookSubscription, cascade delete | |
| `EventId` | string | Stable idempotency key; the `webhook-id` header; constant across retries. |
| `EventType` | string | |
| `PayloadJson` | jsonb | The exact signed body. |
| `Status` | enum `Pending` \| `Delivered` \| `Failed` | |
| `AttemptCount` | int | |
| `NextAttemptAt` | timestamptz | Due-time the worker polls on. |
| `ResponseStatus` | int? | Last HTTP status from the target. |
| `LastError` | string? | |
| `CreatedAt` | timestamptz | |

Index on `(Status, NextAttemptAt)` for the worker poll. Retain delivered/failed rows for the
UI history; a later pruning job (out of scope) can trim them.

### 7.3 `WorkflowSignal` (admin-defined vocabulary)

| Column | Type | Notes |
|---|---|---|
| `Id` | guid PK | |
| `Key` | string, unique | Routing slug (e.g. `post-to-slack`); stable, machine-facing. |
| `Label` | string | Friendly, author-facing (e.g. "Send to Slack"). |
| `Description` | string? | Shown to authors in the picker. |
| `IsActive` | bool | Inactive signals are hidden from the picker but keep existing links. |
| `CreatedAt` | timestamptz | |

### 7.4 `FormulaWorkflowSignal` (join)

Many-to-many between `Formula` and `WorkflowSignal` (`FormulaId`, `WorkflowSignalId`,
composite PK, FK cascade). A formula may carry zero or more signals. Deleting a signal
removes its links (does not delete formulas).

### 7.5 `ApiAccessToken` (extend existing)

| New column | Type | Notes |
|---|---|---|
| `Scope` | enum `ReadOnly` \| `ReadWrite` | Default `ReadWrite` for existing rows (backward compatible). |
| `ExpiresAt` | timestamptz? | Null = never (existing rows). |

### 7.6 `PlatformSettings` (extend existing)

Add `McpAccessEnabled` (seed true) and `WebhooksEnabled` (default false). See section 6.

## 8. Outbound webhook pipeline

### 8.1 Publishing

Introduce `IWebhookPublisher.PublishAsync(eventType, ownerUserId, data, signals?)`. Call it
at the **existing notify call-sites** that already push SignalR - the same places, so no new
transition logic:

- `WorkerCallbackController` result/failure -> `recording.transcribed` /
  `recording.transcription_failed`.
- Recording creation path -> `recording.created`.
- `FormulaRunProcessor` completion/failure -> `formula_result.completed` /
  `formula_result.failed`, passing the formula's attached signal keys.

The publisher:

1. Builds the thin event envelope once, with a fresh stable `EventId`.
2. Resolves **matching subscriptions** (section 10) for `(eventType, ownerUserId, signals)`.
3. Inserts one `WebhookDelivery` row per match (`Pending`, `NextAttemptAt = now`). For a
   platform, signal-routed `formula_result.*` delivery, the payload embeds the formula
   output inline (section 9.3); all other payloads are thin.

Publishing is best-effort and must never block or fail the originating request/worker (wrap
in try/catch, log on failure).

### 8.2 Delivery worker

`WebhookDeliveryWorker : BackgroundService`, singleton, following `SummarizationWorker`:

- Polls due rows (`Status = Pending AND NextAttemptAt <= now`), a small batch per tick,
  ~1s idle delay. Opens a DI scope per delivery.
- POSTs `PayloadJson` to the subscription URL with the Standard Webhooks headers (8.3) and a
  short timeout.
- **Success** (2xx): mark `Delivered`, set `ResponseStatus`, reset the subscription's
  `ConsecutiveFailures`, update `LastDeliveryAt`/`LastStatus`.
- **Failure** (non-2xx, timeout, DNS, connection): increment `AttemptCount`, set
  `NextAttemptAt` per the backoff schedule, record `LastError`/`ResponseStatus`. When
  `AttemptCount` exhausts the schedule, mark the delivery `Failed` and increment the
  subscription's `ConsecutiveFailures`.
- **Auto-disable:** when `ConsecutiveFailures` crosses a threshold (**15**), set
  `IsActive = false` and `DisabledReason`, so a dead URL stops retrying forever. The user
  re-enables from the UI.

**Backoff schedule:** ~8 attempts over ~24h (Standard-Webhooks-style), e.g. 5s, 30s, 2m,
10m, 30m, 2h, 5h, 10h. Encoded as a pure function `nextAttempt(attemptCount)` for unit
testing.

### 8.3 Signing (Standard Webhooks)

Per delivery, set headers:

- `webhook-id`: the `EventId` (idempotency key, constant across retries).
- `webhook-timestamp`: unix seconds at send (replay-protection input).
- `webhook-signature`: `v1,<base64(HMAC-SHA256(secret, "{webhook-id}.{webhook-timestamp}.{body}"))>`.

The secret is the subscription's decrypted `SecretEncrypted`. Signing is a pure function
(secret, id, timestamp, body) -> header, unit-tested against a known vector.

## 9. Event catalog and payloads

### 9.1 Thin envelope (default)

```json
{
  "id": "evt_9f3c...",
  "type": "recording.transcribed",
  "created": "2026-07-23T10:15:00Z",
  "data": {
    "recordingId": "…",
    "recordingName": "…",
    "status": "Transcribed",
    "links": {
      "api": "https://host/api/recordings/{id}",
      "web": "https://host/recordings/{id}"
    }
  }
}
```

### 9.2 Event types (v1)

| Type | `data` payload (thin) |
|---|---|
| `recording.created` | `recordingId, recordingName, source, status, links` |
| `recording.transcribed` | `recordingId, recordingName, status, durationMs, links` |
| `recording.transcription_failed` | `recordingId, recordingName, status, error?, links` |
| `formula_result.completed` | `recordingId, sectionId?, formulaId, formulaResultId, signals[], status, links.result` |
| `formula_result.failed` | `recordingId, sectionId?, formulaId, formulaResultId, signals[], status, error?, links.result` |

`signals[]` is the list of Workflow Signal keys attached to the formula (empty if none).

### 9.3 Inline output on signal-routed formula deliveries

When a `formula_result.completed` / `.failed` event is delivered to a **platform**
subscription **because a signal matched**, that delivery's `data` additionally embeds the
formula output inline (`data.output`, plus `recordingName`, `formulaName`). Rationale: the
author explicitly opted in by attaching "Send this output to X", and it avoids a cross-user
fetch-authorization problem (an admin-wired workflow otherwise could not read another user's
result under the user-scoped API). **Personal-subscription deliveries never embed output -
they stay thin** and fetch via `links.result`.

## 10. Subscription matching

At publish time, for an event `(type, ownerUserId, signals[])`:

- **Personal subscriptions:** match if `Scope = Personal` AND `IsActive` AND
  `OwnerUserId = ownerUserId` AND `type in EventTypes` AND (`SignalFilter` empty OR
  intersects `signals`). Deliver thin.
- **Platform subscriptions:** match if `Scope = Platform` AND `IsActive` AND
  `type in EventTypes` AND `SignalFilter` intersects `signals` (platform subs are
  signal-routed; a platform sub with an empty `SignalFilter` matches nothing, to avoid an
  admin accidentally firing on every user's every event). For `formula_result.*` matched
  here, deliver with inline output (9.3).

Personal subscriptions are the only ones scoped to the owner; this preserves the "personal
only" decision for user-created Automations, while platform subscriptions are the deliberate
admin-owned, signal-routed exception.

## 11. Inbound hardening

### 11.1 Token scope + expiry

- Enforced centrally in the existing ApiKey auth path:
  - `ExpiresAt` in the past -> authentication fails (401), same as an unknown token.
  - `Scope = ReadOnly` -> an authorization requirement blocks unsafe verbs (POST/PUT/PATCH/
    DELETE); GET/HEAD allowed. `ReadWrite` -> unrestricted (current behavior).
- Mint UI gains a read-only/read-write choice and an optional expiry date. Existing tokens
  are `ReadWrite`, never-expire - unchanged.

### 11.2 Formula REST parity

Today running a formula is MCP-only (`run_formula` tool). Add first-class REST so an HTTP
node can drive the fire-then-fetch loop:

- **Run:** `POST /api/recordings/{id}/formula-runs` (body: `formulaId`) -> returns
  `{ formulaResultId, status }`. Requires `ReadWrite` scope. Reuses the existing formula-run
  enqueue path (`FormulaRunProcessor`).
- **Fetch:** `GET /api/formula-results/{id}` -> returns the result detail (status + output
  when complete). Owner-scoped as usual. Confirm/extend `FormulaResultsController` as needed.

Both appear in the curated OpenAPI document.

### 11.3 OpenAPI completeness

Ensure `/api/openapi/v1.json` cleanly covers recordings, formula-runs, formula-results, and
the new webhook-subscription management endpoints, so it can drive a codegen'd node later.

## 12. Security

- **SSRF:** validate `Url` on create/update with the existing `UrlFetcher` / `SafeRedirect`
  guards - reject private, loopback (except localhost in dev), link-local, and
  cloud-metadata addresses; require `https` in production.
- **Secret at rest:** `SecretEncrypted` via Data Protection (`IApiKeyProtector` pattern),
  recoverable for signing. Shown to the user once.
- **Payload minimisation:** thin by default keeps transcript/summary text out of webhook
  logs; inline output is confined to author-opted, admin-routed platform signal deliveries.
- **Authorization:** platform subscriptions and Workflow Signal CRUD require `ManagePlatform`;
  personal Automations require only the authenticated user and `WebhooksEnabled`.
- **Rate limiting:** a per-subscription delivery cap prevents a runaway from hammering a
  target.
- **Token scope/expiry** enforced centrally (11.1).

## 13. UI

### 13.1 Admin (Settings modal, Platform-Administrator-only)

- **Integration toggles:** the existing API-access toggle plus the two new ones (MCP,
  Webhooks), each with help text.
- **Workflow Signals:** a CRUD list (Key, Label, Description, Active).
- **Platform subscriptions:** create/edit/delete admin-owned subscriptions that route by
  signal to a workflow URL (same form as a user Automation, plus a signal selector). Only
  visible when Webhooks is enabled.

### 13.2 End user - "Automations" (Preferences), approachable by design

Only present when `WebhooksEnabled`. The surface is called **Automations** (not "webhooks").

**Create flow - a 3-step guided panel:**

1. **"What should trigger it?"** - plain-language checkboxes, not event slugs
   ("A recording finishes transcribing", "A recording fails", "A recording is created",
   "A formula finishes", "A formula fails"), each with a one-line description.
2. **"Where should it go?"** - one URL field with an inline tabbed hint
   ([Zapier] [n8n] [Make] [Other]), each two lines (e.g. "In Zapier: add a 'Webhooks by
   Zapier -> Catch Hook' trigger and paste its Custom Webhook URL here"). URL validated +
   SSRF-checked on save with a friendly error.
3. **"Test & save"** - a prominent "Send a test event" button fires a `ping` so the user
   sees it land in their tool before trusting it; then save.

**List view** - each Automation is a card: friendly name, its triggers as chips, the
destination host, and a status pill ("Active - delivered 2 min ago" / "Paused - 15 failures,
check the URL") with "Resend last" and "Re-enable" affordances. "Send test event" per card.

**Signing secret** - tucked under an "Advanced / Verify requests" disclosure with a copy
button and one line ("Optional: use this to verify requests really came from Diariz"). Most
users never open it.

**Token issuance folded into the flow** - if the user selects a formula trigger (implying the
flow reads results back), the panel offers inline: "This automation may need to read results
into your workflow. Create a read-only access token?" One click mints a scoped, optionally
expiring token, shown once with a copy button and a "paste this as the Authorization header"
hint. The user never reasons about auth as a separate concept.

**Empty state** - a single "Connect an automation" button with the 3-step promise and a
"Learn more" docs link.

### 13.3 Formula author - signal picker

In the formula editor, a "When this finishes, trigger" multi-select showing the admin's
active Workflow Signals (Label + Description). Selecting signals writes `FormulaWorkflowSignal`
links. Hidden when Webhooks is disabled or no active signals exist.

## 14. Bundled fix: API-reference links open in a new tab

Two in-app links to `/developers/api`:

- **Admin Settings modal** (`SettingsModal.tsx`, Integration section): currently a
  same-tab in-app `Link` (with `onClose`). Change to open `/developers/api` in a **new tab**
  (`target="_blank"`, `rel="noopener noreferrer"`).
- **Preferences -> Developers** (`DeveloperAccessSection.tsx`): currently an in-app `Link`
  that navigates from within the modal and lands on a blank page. Repoint to the same
  `/developers/api` and open in a **new tab**; the full SPA load at that route resolves the
  blank-page issue.

Existing tests assert `href === "/developers/api"`; update them (TDD) to also assert the new
tab (`target="_blank"`), keeping the href unchanged.

## 15. Testing (TDD)

**Pure / unit:**
- Event envelope builder (shape, stable `EventId`).
- HMAC signature against a known Standard-Webhooks vector.
- Backoff schedule `nextAttempt(attemptCount)`.
- Subscription matching (personal vs platform; signal intersection; empty-signal platform
  matches nothing).
- Token scope-verb enforcement; expiry check.
- SSRF URL classifier (accept/reject cases).
- Web: Automations panel steps, status-pill rendering, token-inline offer; signal picker;
  the two API-reference links open in a new tab.

**Integration (Testcontainers - real Postgres/Redis/MinIO):**
- Publish -> `WebhookDelivery` rows created for matching subscriptions only.
- Worker delivers to an in-test HTTP sink; signature verifies; retry/backoff advances
  `NextAttemptAt`; auto-disable after the failure threshold.
- Personal thin vs platform-signal inline-output payloads.
- Read-only token blocks a write verb; expired token rejected; formula run + fetch
  round-trips.
- Each platform toggle off -> user endpoints 404 and UI hidden; MCP seeded on.

## 16. Docs, versioning, deployment (release checklist)

- **Version:** functional enhancement -> Minor bump. `version.json` + three mirrors
  (`apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`).
- **Release notes:** new `RELEASES[0]` entry (must equal `version.json`), headline + prose
  summary + added/changed/fixed bullets (including the API-reference link fix).
- **About `CAPABILITIES`** table row (new scope: integrations/webhooks). No new third-party
  library/model, so no `AboutModal.tsx` disclaimer change.
- **README Features** table row + **`docs/features.md`** prose bullet (lockstep).
- **`docs/Overall_Synopsis_of_Platform.md`:** new outbound-webhook contract, the delivery
  worker + Postgres delivery queue, Workflow Signals routing, the three platform toggles,
  token scope/expiry, formula REST endpoints.
- **`docs/Data_Schema.md`:** `WebhookSubscription`, `WebhookDelivery`, `WorkflowSignal`,
  `FormulaWorkflowSignal`, the two `ApiAccessToken` columns, the two `PlatformSettings`
  columns, and the migration-history row.
- **Deployment surface:** server redeploy only (no desktop). Migration is additive and
  forward-restore-safe -> no `CurrentFormat` bump. The migration seeds
  `McpAccessEnabled = true` on the existing singleton row.

## 17. Defaults to confirm at spec review

1. `McpAccessEnabled` seeded **on** (preserve the live connector), `ApiAccessEnabled`
   stays **off**, `WebhooksEnabled` **off**.
2. Auto-disable threshold **15** consecutive failures; backoff **~8 attempts over ~24h**.
3. Inline formula output only on **platform, signal-routed** `formula_result.*` deliveries;
   everything else thin.
4. User-facing name **"Automations"** (fallback "Connections") for personal webhooks;
   admin concept stays "Workflow Signals".
5. API-reference link fix rides in **this** PR (could be a standalone fix if you want it
   sooner).
