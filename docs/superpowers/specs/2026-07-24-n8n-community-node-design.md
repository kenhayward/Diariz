# Diariz n8n Community Node: Design Spec

**Date:** 2026-07-24
**Status:** Proposed design, awaiting review
**Deployment surface:** New npm package. No desktop release. No server redeploy required for the node itself; the optional API follow-ups in section 12 would need one.
**Version impact:** New user-facing capability -> Minor bump of `version.json`. The npm package carries its **own** semver (see section 11.3).

---

## 1. Goal

Publish `n8n-nodes-diariz`, a community node package that makes Diariz a first-class
citizen in n8n: react to meetings as they are transcribed, and drive the whole REST API
from a workflow without hand-building HTTP requests.

Two nodes and one credential:

| Artifact | n8n type | Purpose |
|---|---|---|
| **Diariz Trigger** | Trigger node | Self-registering webhook. Fires on the five Diariz events. |
| **Diariz** | Action node | Full REST coverage: 10 curated resources plus a generated tail plus a raw escape hatch. |
| **Diariz API** | Credential | Base URL plus personal API token, with a live credential test. |

The end-to-end story this unlocks, entirely inside n8n:

> Recording transcribed (Trigger) -> Run Formula "Client Summary", wait for completion
> (Action) -> post the Markdown to Slack.

This supersedes the "Published Zapier app / n8n community node" line item that the
integrations design spec (2026-07-23) listed as out of scope for v1.

## 2. What already exists, and what it means for the node

The integrations arc (PRs #333-340) plus the OpenAPI documentation arc (PRs #343-349) left
the platform unusually well prepared. The node builds on facts, not new API work:

| Existing capability | Why it matters here |
|---|---|
| `POST/GET/DELETE /api/user/webhooks` with a returned signing secret | The Trigger node can **self-register**, which is the n8n-native pattern. No manual copy-paste of URLs. |
| Standard Webhooks signing: `webhook-id`, `webhook-timestamp`, `webhook-signature` = `v1,base64(HMAC-SHA256(secret, "id.timestamp.body"))` | Signature verification is a documented open spec, not a bespoke scheme. |
| Personal API tokens `dz_api_...` with `Scope` (ReadOnly / ReadWrite) and `ExpiresAt` | A token pasted into n8n can be least-privilege and time-boxed. |
| `GET /api/user/profile` reports `apiAccessEnabled` and `webhooksEnabled` | One call serves as both the credential test **and** a pre-flight diagnostic for the Trigger. |
| All 191 endpoints carry `[EndpointSummary]` and `[EndpointDescription]` | The OpenAPI document is now rich enough to **generate** node properties with usable display names and help text. This is what makes "full coverage" tractable. |
| `OpenApiDocumentTests.GenerateDocumentAsync()` produces the document in-process with no DB or infrastructure | CI can regenerate and diff the node's operation surface on every build, with no containers. |

## 3. Prerequisites and failure modes (important)

The node depends on two platform toggles that a self-hosted administrator controls
(`PlatformSettings`):

| Toggle | Default | Blocks |
|---|---|---|
| `ApiAccessEnabled` | **off** | Personal `dz_api_` tokens and the whole user API surface. Without it, nothing works. |
| `WebhooksEnabled` | **off** | All webhook subscriptions. Without it, the **Trigger node cannot activate**. |

We chose a webhook-only trigger with no polling fallback, so this must be handled by
diagnostics rather than by a workaround:

- The **credential test** calls `GET /api/user/profile` and raises a distinct message when
  `apiAccessEnabled` is false, so the user learns at credential-save time rather than at
  first execution.
- The **Trigger's** `create` call maps `403` to: "Automations are turned off on this Diariz
  instance. Ask your platform administrator to enable Automations in Settings, then
  activate this workflow again." No stack trace, no raw 403.
- The `400 "Automation limit reached"` (20 subscriptions per user) is surfaced verbatim, with
  a hint that each active Diariz Trigger consumes one slot.

## 4. Package identity and layout

Location: **`integrations/n8n-nodes-diariz/`** in this monorepo.

```
integrations/n8n-nodes-diariz/
  package.json              # name n8n-nodes-diariz, n8n block, keyword n8n-community-node-package
  tsconfig.json
  gulpfile.js               # icon copy (the n8n starter convention)
  .eslintrc.js              # eslint-plugin-n8n-nodes-base, required for verification
  credentials/
    DiarizApi.credentials.ts
  nodes/
    Diariz/
      Diariz.node.ts               # router: resource -> operation -> execute
      Diariz.node.json             # codex metadata (categories, docs links)
      diariz.svg                   # node icon
      descriptions/                # HAND-AUTHORED core resources (section 6)
        RecordingDescription.ts
        FormulaDescription.ts
        ...
      generated/                   # CODEGEN output, checked in (section 7)
        index.ts
        openapi.snapshot.json
      transport/
        request.ts                 # thin wrapper over this.helpers.httpRequestWithAuthentication
        pagination.ts              # Return All / Limit slicing
        sse.ts                     # chat stream accumulation
      DiarizTrigger.node.ts        # section 5
      signature.ts                 # Standard Webhooks verification (pure, unit-tested)
  scripts/
    generate.ts               # OpenAPI document -> generated/*.ts
  test/
    signature.test.ts
    pagination.test.ts
    generate.test.ts
```

**Zero runtime dependencies.** n8n's verified-node programme requires it, and it is
achievable: HTTP goes through `this.helpers.httpRequestWithAuthentication`, and signature
verification uses Node's built-in `crypto`. Only `devDependencies` (typescript, eslint,
`n8n-workflow` types, the test runner).

**Licence.** The repository is **AGPL-3.0**, but n8n's verification programme expects an
MIT-licensed package. The node is standalone TypeScript that talks HTTP to Diariz; it is
not a derivative of the C# or React source, so licensing this directory MIT is clean if
you, as sole copyright holder, choose to. **This needs your explicit decision** - it is the
one prerequisite for verification that the design cannot settle on its own. If you prefer
to keep everything AGPL, we publish unverified to npm and users install it by package name.

## 5. The Trigger node

### 5.1 Properties

| Property | Type | Notes |
|---|---|---|
| **Events** | multiOptions, required | The nine subscribable types (five existing plus the four added in Phase 0). Display names in plain language: "Recording Created", "Recording Transcribed", "Transcription Failed", "Summary Ready", "Meeting Minutes Ready", "Action Items Ready", "Tags Ready", "Formula Result Completed", "Formula Result Failed". |
| **Simplify** | boolean, default true | On: output `data` only. Off: the full `{ id, type, created, data }` envelope plus received headers. n8n convention. |

### 5.2 Lifecycle (`webhookMethods.default`)

- **`create`** - `POST /api/user/webhooks` with `{ name: "n8n: <workflow name>", url: <the n8n
  webhook URL>, eventTypes: [...] }`. Store the returned `id` **and `secret`** in the node's
  static data. The secret is returned exactly once by design, so losing it means the
  subscription must be recreated; if static data ever lacks a secret for a live
  subscription, the node deletes and recreates rather than silently skipping verification.
- **`checkExists`** - `GET /api/user/webhooks`, match on the n8n webhook URL. Returns false
  when the stored secret is missing, so the recreate path above triggers.
- **`delete`** - `DELETE /api/user/webhooks/{id}`, clear static data. n8n calls this on
  deactivation, which keeps the 20-subscription cap from filling up with orphans.

n8n uses a **separate test webhook URL** for manual executions, so a "Listen for test event"
run registers a second, short-lived subscription and removes it afterwards. Expected, worth
one line in the README, and it is why the cap hint in section 3 matters.

### 5.3 Signature verification (mandatory, no opt-out)

On every delivery:

1. Read the **raw** request body. The webhook description sets `rawBody: true`. This is not
   optional: the signature covers the exact bytes Diariz serialised, and re-serialising the
   parsed JSON in JavaScript will not reproduce them (the payload is built with
   `DefaultIgnoreCondition: WhenWritingNull` and C# compact formatting). Byte equality is
   the requirement, so we never sign a round-tripped object.
2. Recompute `v1,base64(HMAC-SHA256(secret, "<webhook-id>.<webhook-timestamp>.<rawBody>"))`.
3. Compare with `crypto.timingSafeEqual`. The header may carry several space-delimited
   signatures per the Standard Webhooks spec; accept if any matches.
4. Reject a `webhook-timestamp` more than **5 minutes** from now (replay window).
5. On failure, respond `401` and emit nothing. Diariz will retry per its backoff schedule
   (8 attempts over ~24h), which is the correct behaviour for a genuinely misconfigured
   secret, and harmless for a forged request.

`signature.ts` is a pure function over `(secret, headers, rawBody, now)` so this is
unit-tested against vectors produced by the .NET `WebhookSigner` - the one place where a
cross-language contract could silently drift.

### 5.4 Output

One n8n item per event. With **Simplify** on, `json` is the event's `data`, which already
carries `links.api` and `links.web` so a downstream node can fetch detail or link a human
to the recording without string-building.

## 6. The Action node: curated core

Ten resources, hand-authored for real n8n ergonomics - `loadOptions` dropdowns listing your
actual recordings, formulas and folders; binary in and out; Return All with a limit.

| Resource | Operations |
|---|---|
| **Recording** | Get, Get Many, Upload (binary), Delete, Rename, Move to Folder, Re-transcribe, Summarize, Generate Minutes, Download Transcript (txt/md/rtf/srt), Download Audio, Get Audio URL, Email, Share |
| **Formula** | Get Many, Get, Create, Update, Delete, **Run on Recording**, Run on Folder |
| **Formula Result** | Get Many, Get, Update, Delete, Download, Email |
| **Folder** | Get Many, Create, Rename, Delete, Get Page (stats, summary, minutes) |
| **Search** | Search Transcripts (query, room, folder, speaker, date range, limit) |
| **Action Item** | Get Many (all), Get Many (recording), Extract from Transcript, Create, Update, Delete, Bulk Complete |
| **Attachment** | Get Many, Add File (binary), Add URL, Rename, Delete, Download |
| **Tag** | Get Many |
| **Speaker Profile** | Get Many, Create from Recording Speaker, Rename, Merge, Delete |
| **Chat** | Ask (over selected recordings), List Conversations, Get Conversation, Delete Conversation |

Three of these need behaviour the REST shape does not give for free:

### 6.1 Run Formula: "Wait for Completion"

`POST /api/recordings/{id}/formulas/{formulaId}/run` returns **202** with the result in
`Generating`. An n8n user almost always wants the document in the same node, so the
operation carries a **Wait for Completion** toggle (default **on**):

- Poll `GET /api/recordings/{recordingId}/formula-results/{id}` every 3s.
- Give up after a configurable **Timeout** (default 300s) and throw a
  `NodeOperationError` naming the result id, so the workflow can still fetch it later.
- Terminal states: `Ready` -> output the result. `Failed` -> throw with the recorded error.
- With the toggle **off**, output the 202 body immediately - the right choice when the
  workflow is instead resumed by a `formula_result.completed` Trigger.

### 6.2 Chat: Ask

`POST /api/chat/stream` is **SSE only** (`text/event-stream`). The node consumes the stream,
concatenates the deltas, and outputs the finished answer as a single item, so the SSE
transport is invisible to the workflow author. `transport/sse.ts` isolates this; it is the
only non-JSON transport in the package.

### 6.3 Upload

`POST /api/recordings` is multipart (`audio`, `title`, `durationMs`, `source`, `sectionId`,
`roomId`). The node sends `source=Upload` and `durationMs=0`, because the worker measures
the true duration for uploads and backfills it. The `audio` part comes from an n8n binary
property, so a Google Drive or Dropbox node feeds it directly.

## 7. The Action node: generated tail

The remaining resource groups are generated from the OpenAPI document into
`nodes/Diariz/generated/`, giving typed operations with proper display names.

Covered by codegen: ApiTokens, Calendar, CalendarEventNotes, CalendarFeeds, Groups,
Languages, McpTokens, MeetingNotes, MeetingTypes, RecordingTranslation, Rooms, Screenshots,
SectionAttachments, SectionFormulaResults, Storage, UserProfile, UserSettings, Webhooks,
WorkflowSignals.

**Deliberately excluded: `Auth`.** `POST /api/auth/login` takes an email and password. The
node's entire auth model is token-based, and exposing a password operation invites users to
put their account password in a workflow. Everything it produces (a session token) is
already covered by the credential. Say the word if you would rather have it for literal
completeness.

### 7.1 How generation works

`scripts/generate.ts` reads `generated/openapi.snapshot.json` and emits, per operation:

- `displayName` from `summary` ("Run a formula over a recording").
- `description` from the first paragraph of `description`, with Markdown stripped - n8n
  renders these as plain hint text under the field.
- Path parameters as required string fields; query parameters as optional collection fields;
  the request body schema flattened one level into typed fields.

The snapshot is **checked in**, so `npm install` and the published package never need
network access or a running API.

### 7.2 Drift guard (the reason for the monorepo)

A CI job:

1. Runs a small .NET step that writes the current document to `openapi.snapshot.json`, reusing
   the in-process host from `OpenApiDocumentTests.GenerateDocumentAsync()` - no database, no
   containers, seconds not minutes.
2. Runs `npm run generate`.
3. `git diff --exit-code`.

Any API change that alters the public surface fails the build until the node is regenerated
and its release notes updated. This is precisely the guarantee a separate repo cannot give,
and it is cheap because the endpoints are now fully documented.

## 8. Custom API Call

Every resource additionally offers **Custom API Call**: method, path, query, body, forwarded
through the same credential. It is the n8n convention, and it guarantees literal 100%
reachability even for an endpoint added after the installed node version was published.

## 9. Credential

```ts
DiarizApi:
  Base URL   string   e.g. https://diariz.example.com   (required, trailing slash trimmed)
  API Token  password dz_api_...                        (required)

authenticate: header  Authorization: Bearer {{$credentials.apiToken}}
test:         GET {{baseUrl}}/api/user/profile
```

Test rules beyond the status code:

- `apiAccessEnabled === false` -> "Your token is valid, but API access is turned off on this
  Diariz instance. Ask your platform administrator to enable it."
- `webhooksEnabled === false` -> a **warning**, not a failure: "Automations are off, so the
  Diariz Trigger will not be able to activate. Action nodes will work."

A `401` from an expired token is mapped to a message naming expiry explicitly, since
`ExpiresAt` is a supported and easily forgotten token feature.

## 10. Testing

| Layer | What |
|---|---|
| Unit (node test runner, matching the desktop package's `node --test` style) | Signature verification against fixtures generated by the .NET `WebhookSigner`; timestamp-window rejection; pagination slicing; SSE accumulation; codegen output snapshot. |
| Lint | `eslint-plugin-n8n-nodes-base` clean. A verification prerequisite, and it enforces n8n's naming and description conventions for us. |
| Cross-language contract | A .NET test emits signing vectors to a shared fixture file that the TypeScript test reads. If either side changes, one of the two suites fails. |
| Live | Install the packed tarball on **n8n.stocks-hayward.com**, pointed at **dev.diariz.stocks-hayward.com**. Build the canonical workflow (transcribed -> run formula -> Slack) through the n8n MCP connector, activate it, upload a recording, confirm the trigger fires and the formula output arrives. |

Per the repo's TDD rule: signature verification, pagination and codegen all get their
failing test first. The node's descriptor files are declarative configuration and are
verified by the lint pass plus the generated-output snapshot rather than by hand-written
tests per field.

## 11. Release and publishing

### 11.1 npm

`npm publish` from `integrations/n8n-nodes-diariz`. Users install via **Settings ->
Community Nodes -> Install** with the package name.

### 11.2 Verification submission

Requires (to be confirmed against n8n's current checklist at submission time): the
`n8n-nodes-` name prefix, the `n8n-community-node-package` keyword, zero runtime
dependencies, a clean node linter, an MIT licence (section 4), no network calls outside the
node's own API, and a completed submission through n8n's process. Verified nodes appear in
n8n's in-app node search, which is the whole point of doing it.

### 11.3 Versioning: a deliberate exception to the lockstep rule

`CLAUDE.md` requires `version.json` and its three mirrors to move together. The node package
is **not** a fourth mirror:

- `integrations/n8n-nodes-diariz/package.json` carries **independent semver**, because n8n
  users see and pin it, and it must be able to ship a patch without a platform release.
- The **platform** release that introduces the node is an ordinary Minor bump with the usual
  checklist: `version.json` plus three mirrors, `RELEASES[0]`, the `CAPABILITIES` row, the
  README Features row, `docs/features.md`, and `Overall_Synopsis_of_Platform.md` (a new
  deployable and a new external distribution channel). No `Data_Schema.md` change - the node
  adds no schema.
- The node's own changelog lives in `integrations/n8n-nodes-diariz/CHANGELOG.md`.

**This exception needs your sign-off**, since it deviates from a rule the repo otherwise
enforces strictly.

## 12. API gaps this work surfaces

Real findings from designing against the live surface. None block the node; each makes it
better. Recommend as separate PRs, in this order:

1. **No pagination on list endpoints.** `GET /api/recordings` returns everything for the
   room. "Return All" versus "Limit" is therefore client-side slicing after a full transfer.
   Fine at hundreds of recordings, wasteful at thousands. Adding `skip`/`take` would let the
   node page properly.
2. **Missing events for AI outputs.** The most natural n8n trigger is "the summary is ready",
   but the catalogue stops at `recording.transcribed`; a workflow must trigger there and then
   poll. The integrations spec already lists `recording.summarized`, `.minutes_ready`,
   `.action_items_ready`, `.tags_ready` as deferred, and they run through the same pipeline.
   **This is the highest-value follow-up.**
3. **No idempotency key on upload.** A re-run of a workflow duplicates the recording. An
   optional client-supplied key returning the existing recording would make uploads safely
   retryable, which matters more in an automation tool than in the app.
4. **`durationMs` is a required form field** on an endpoint that measures duration itself for
   uploads. Harmless (the node sends 0) but a small wart in the public contract.
5. **Personal subscriptions cap at 20**, and n8n consumes one per active trigger plus a
   transient one per manual test. Adequate today; worth revisiting if the node lands well.

## 13. Out of scope

- Zapier app, Make module, Pipedream component. Same REST and webhook surface; separate work.
- An n8n **AI Tool** wrapper for Diariz search. The MCP server already serves Claude directly,
  and n8n can reach an MCP server natively, so a duplicate path is not worth building.
- Room-scoped or platform-scoped subscriptions in the Trigger. Personal scope only, matching
  the token model. Platform routing stays an admin concern in the Diariz UI.
- Consuming inbound webhooks (Diariz reacting to n8n).
- Publishing the node from CI. Manual `npm publish` until the release shape settles.

## 14. Decisions (settled 2026-07-24)

1. **Licence** - the node directory is **MIT**, the rest of the repo stays AGPL-3.0. Verification
   is in scope. Section 4.
2. **Versioning** - the npm package carries **independent semver** with its own changelog. A
   documented exception to the lockstep mirror rule. Section 11.3.
3. **`Auth` resource** - **excluded**. Reachable via Custom API Call if ever needed. Section 7.
4. **AI-output events** - **bundled as Phase 0**, ahead of the node, so the Trigger ships with
   `recording.summarized`, `.minutes_ready`, `.action_items_ready` and `.tags_ready` in its
   event list rather than gaining them after publication. Section 12, finding 2.

The remaining findings in section 12 (pagination, upload idempotency, the `durationMs` wart,
the 20-subscription cap) stay out of this arc as separate follow-ups.

Implementation plan: `docs/superpowers/plans/2026-07-24-n8n-community-node.md`.
