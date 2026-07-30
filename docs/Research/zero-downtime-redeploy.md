# Redeploying while users are recording - feasibility

> Research only, 2026-07-30. No code changed. Every claim about Diariz carries a file reference so it can
> be re-checked as the code moves.

## The short answer

**Not impossible. In fact redeploying during a recording is already non-destructive today** - no audio is
lost, and the recording keeps running. What it is not today is *invisible*: there is a gap of roughly
30-60 seconds where the API is unreachable, and a user who presses Stop inside that gap sees an error.

Three findings drive everything below:

1. **A recording needs the server for nothing.** Capture is entirely client-side; the API is contacted
   once, at Stop.
2. **The web tier is unusually safe to swap** because the SPA is a single bundle with exactly one lazy
   route - the usual "stale chunk 404" that makes SPA redeploys dangerous barely applies here.
3. **True zero-downtime is blocked, but not by anything in the recording path.** It is blocked by the
   API's in-process background workers, one of which would double-send if a second replica existed.

---

## 1. What actually happens during a recording

| Thing | Where it lives while recording | Server involved? |
|---|---|---|
| Audio | `chunksRef` - an in-memory `Blob[]` in the page (`Recorder.tsx:281`) | **No** |
| Live notes | IndexedDB, written as they are typed | **No** |
| Screenshots | IndexedDB, one capture at a time | **No** |
| Elapsed time / auto-stop | `setInterval` in the page | **No** |
| Tray state (desktop) | IPC to the Electron main process | **No** |

The upload is a **single multipart POST at Stop** (`api.ts:326`, `FormData` with the whole blob) - there
is no chunked or resumable upload, and nothing is streamed to the server as you speak.

So during the recording itself the only server traffic is the SignalR heartbeat, the token-refresh timer,
and background react-query refetches belonging to other panels. **None of them can end a recording.**

### The audio is already protected

`pendingRecording.ts` writes the finished blob to IndexedDB **before** the upload is attempted, keyed by
user, and clears it only on success. Its own header says it exists for exactly this class of problem:

> *"so the audio is never lost if the upload fails (e.g. the session expired during a long meeting and
> Stop lands on a 401)"*

A failed upload therefore leaves a recovery banner offering the recording back, rather than losing it.
**That single design decision is what turns "impossible" into "already survivable".**

---

## 2. The four exposure windows

### Window A - Stop lands during the API gap

The realistic failure. The POST fails, the user sees an error, and the recording sits in the recovery
banner until they retry.

- **Impact:** an unpleasant moment, no data loss.
- **Likelihood:** gap duration divided by meeting length, per concurrent recorder. With a 45s gap and
  hour-long meetings, roughly a 1-in-80 chance per in-flight recording.
- **Worth noting:** the upload is *not* retried automatically. `Recorder` catches, stashes, and shows the
  banner. A single automatic retry on 502/503 after a few seconds would close most of this window without
  any deployment change.

### Window B - the token refresh, which is the one bug-shaped finding

Access tokens last **120 minutes** (`appsettings.json:16`) and the SPA silently refreshes **60 seconds
before expiry** (`tokenRefresh.ts`, `auth.tsx:83-108`) precisely so a long recording never lapses.

But `doRefresh` swallows a failure and **does not retry or reschedule** - rescheduling only happens as a
side effect of `setSession()` on success (`auth.tsx:88-101`). The only recovery is the `focus` listener.

So: **if an API restart lands inside that 60-second window, the one refresh attempt is spent.** If the tab
is not refocused afterwards, the token expires; Stop then returns 401, which triggers
`window.location.assign("/login")` (`api.ts:113`) and unmounts the recorder.

The blob is still stashed first, so nothing is lost - but the user is thrown to the login page mid-flow.

- **Likelihood:** narrow. A 60-second window per 2-hour session.
- **This is worth fixing regardless of deployment**, because a transient network blip does the same thing.
  A retry with backoff inside `doRefresh` is a few lines.

### Window C - the web container swap

Normally the dangerous one. Here it is nearly free:

- The app has **exactly one** `lazy()` import - the API Reference page (`App.tsx:17`). Every
  `import.meta.glob` (locales, help content, help images) is `eager: true`, so it is all bundled.
- The build produces one ~2.4 MB `index-*.js`; the other chunks (Scalar, Vue runtime, icon SVGs) belong
  to that one lazy route.
- Public assets (`favicon.svg`, `logo.png`, `background.webp`) have **unhashed** names and survive a
  rebuild.

**A tab that has already loaded needs nothing further from the web container.** A user recording cannot
reach the one lazy route by accident.

The catch is indirect: **`/api`, `/hubs`, `/mcp` and `/connect` are proxied through the web container**
(`nginx.conf`). Replacing `web` therefore breaks the path to the API even when the API is healthy. That
argues for replacing `api` and `web` as separate steps rather than one `docker compose up -d`.

### Window D - SignalR

`createHub` uses `withAutomaticReconnect()` with no arguments (`signalr.ts:16`), which is the library
default of `[0, 2s, 10s, 30s]` and then **gives up permanently**. An API gap longer than about 40 seconds
leaves the hub dead until the page is reloaded.

Consequence: live status updates (transcribing -> summarising -> done) stop arriving. Some views poll
anyway (`RecordingDetail.tsx:111,132`), so this is staleness rather than breakage - but it is a reason to
keep the API gap short, or to pass an explicit retry policy.

---

## 3. Why the API gap is 30-60 seconds, not milliseconds

`Program.cs:482-497` runs, **before the app starts listening**:

`MigrateAsync` -> `EnsureBucketAsync` -> `SeedRolesAsync` -> `SeedDefaultUserAsync` ->
`SeedPlatformAuthorityAsync` -> `SeedFormulasAsync` -> `MeetingTypeSeeder.SeedAsync`

The compose healthcheck allows `start_period: 60s` for the API, which is a fair estimate of real startup.
Most of that work is a no-op on a normal boot (migrations already applied, seeders idempotent) but it is
still serialised ahead of the first request.

**Reducing this is the single highest-value change** for the "already works, just visible" tier - it
shrinks Windows A, B and D at once.

---

## 4. Can we have real zero-downtime?

Not by configuration. The blocker is **not** the recording path, the web tier, or the database. It is
that the API hosts **13 `BackgroundService` singletons** in-process, and it currently assumes there is
exactly one of it.

### The hard blocker: duplicate webhook deliveries

`WebhookDeliveryProcessor.ProcessDueAsync` (`Services/WebhookDeliveryProcessor.cs:28-34`) claims work with
a plain read:

```
WHERE Status == Pending AND NextAttemptAt <= now
ORDER BY NextAttemptAt
LIMIT BatchSize
```

There is **no row lock, no lease, no `FOR UPDATE SKIP LOCKED`**, and the status is only written at
`SaveChangesAsync` **after the whole batch has been POSTed** (`:141`). Two API replicas polling this table
2 seconds apart would both select the same rows and **both deliver them**.

That is a correctness bug the moment a second replica exists - and a silent one, since the receiving end
just sees duplicate events. (The `webhook-id` header is a stable idempotency key, so a well-built receiver
would dedupe; but "we send everything twice and hope the customer deduped it" is not a deployment
strategy.)

### The rest of the fleet, graded

| Worker(s) | Two replicas? | Why |
|---|---|---|
| `SummarizationWorker`, `MeetingMinutesWorker`, `ActionsWorker`, `TagsWorker`, `FormulaRunWorker`, `SectionMinutesWorker`, `SectionSummaryWorker`, `EmbeddingWorker` | **Fine** | All consume Redis streams via `XREADGROUP` on a consumer group, which distributes rather than duplicates. This part was built for it. |
| `WebhookDeliveryWorker` | **Blocker** | See above. |
| `AudioRetention` | **Risky** | A *deletion* job. Two instances running the nightly sweep concurrently deserves scrutiny before it is duplicated. |
| `EmbeddingBackfill`, `StorageBackfill`, `TagBackfill` | **Risky** | Boot-time backfills; two instances would race and duplicate work. |

### The other two costs

- **SignalR needs a backplane.** Clients are joined to a per-user group and events are published from
  whichever instance handled the callback. With two replicas and no backplane, a user connected to A never
  receives an event published from B. Redis is already in the stack, so `AddStackExchangeRedis` is the
  fix - but it is a real change, not a flag.
- **Migrations must become N-1 safe.** Today one container means one schema version at a time, which is
  quietly why destructive migrations have been tolerable. Two replicas means old and new code hit the same
  schema simultaneously, so every migration becomes expand/contract and `MigrateAsync` should move out of
  app startup into a one-shot job.

Also mechanical: `api` binds `8080:8080` and `web` binds `8081:80` (`docker-compose.yml:146,167`). A fixed
host port cannot have two containers. Replicas need a real proxy (Traefik, Caddy, or nginx upstreams) in
front.

---

## 5. What I would actually do

> **Status (2026-07-30):** Tier 1 items 1, 2, 3 and 5 are **done**. Item 4 (trim API startup) is
> **outstanding on purpose** - it needs the measurement in section 6 first, since the 30-60s figure is
> inferred rather than observed and optimising against a guess is how you make things slower. Tier 2 is on
> the roadmap as Theme 5.

### Tier 0 - accept it, and say so (no work)

Redeploy whenever. Recordings survive; the worst case is a retry click, and the audio is never lost. For a
platform this size that may simply be the right answer, and it is worth knowing it is already true rather
than assuming it is not.

### Tier 1 - make the gap small and quiet (cheap, high value)

1. **Retry the upload** once or twice on 502/503/504 before falling back to the recovery banner. Closes
   most of Window A on its own.
2. **Fix the token refresh retry** (Window B). Worth doing regardless of deployment.
3. **Deploy `api` and `web` as separate steps**, so the proxy is never down at the same moment as its
   upstream.
4. **Trim API startup** so the gap is closer to 10 seconds than 60.
5. **Give SignalR an explicit retry policy** instead of the 4-attempt default, so a short gap does not
   permanently kill the hub.

Result: a redeploy that almost nobody notices, with no architectural change and no new infrastructure.

### Tier 2 - actual zero-downtime (real work, only if Tier 1 is not enough)

In dependency order: claim webhook deliveries with a lease or `FOR UPDATE SKIP LOCKED`; decide what the
retention and backfill jobs do with two instances (probably a leader lock); add the Redis SignalR
backplane; move `MigrateAsync` out of startup and adopt expand/contract migrations; put a health-checked
proxy in front and drop the fixed host ports.

That is a genuine project, and none of it is about recording.

---

## 6. Worth re-checking before acting

- The 30-60s API gap is **inferred** from the compose `start_period` and what `Program.cs` does at boot,
  not measured. Time an actual `docker compose up -d api` on the dev server before optimising it.
- Whether anyone has ever actually hit Window A in production - the recovery banner firing would be the
  signal, and it is not currently instrumented.
- Whether the outer reverse proxy in front of the web container adds its own buffering or retry behaviour
  that changes the picture (it already rewrites tokens and strips fragments, so it is not neutral).
