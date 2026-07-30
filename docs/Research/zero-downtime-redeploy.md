# Redeploying while users are recording - feasibility

> Research only, 2026-07-30. No code changed. Every claim about Diariz carries a file reference so it can
> be re-checked as the code moves.

## The short answer

**Yes - and it has now been done, in production, unnoticed.**

Two live tests on 2026-07-30 (details in section 3): an ordinary redeploy costs **about 3 seconds**, and a
deliberately held **50-second** API outage with a recording in progress produced **no error, no recovery
banner, no visible delay, and no interruption to status updates**. The recording uploaded and completed on
its own.

The original estimate in this document - a 30-60 second gap where a user pressing Stop sees an error - was
wrong twice over: the gap is ten times shorter than assumed, and the exposed instant is not when Stop is
pressed (nginx buffers the whole upload body before it needs the API at all).

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

> **Substantially narrower than modelled here, for a reason found only by testing it live. See
> "nginx absorbs the upload" below.**

The POST fails, the user sees an error, and the recording sits in the recovery banner until they retry.

- **Impact:** an unpleasant moment, no data loss.
- **Likelihood:** far lower than gap-duration-over-meeting-length, because the exposed instant is not when
  Stop is pressed - see below.
- The upload is now retried automatically on 502/503/504 (`lib/retry.ts`).

#### nginx absorbs the upload, which is most of the reason this barely happens

`proxy_request_buffering` is **on** by default and `client_max_body_size` is 1 GiB, so nginx **receives the
entire request body from the client before it opens a connection to the API**. Confirmed in the live test
below by nginx's own `a client request body is buffered to a temporary file` warning.

The consequence is worth stating plainly: **the API only has to be up at the moment the upload body
finishes transferring, not when Stop is pressed.** For a real meeting that transfer takes tens of seconds,
and every second of it is free cushion over an API restart. A user can press Stop while the API is
completely down and never learn that it was.

This narrows Window A to uploads whose body transfer *completes* inside the gap - i.e. short recordings on
a fast link. Those are exactly the cases `lib/retry.ts` covers, so the two mechanisms complement each
other rather than overlap.

It also means **the retry is harder to exercise than expected**, which is worth knowing before designing a
test for it (the first attempt at one is recorded below and failed to trigger the retry at all).

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

## 3. How long the API gap actually is - MEASURED

> **This section originally estimated 30-60 seconds, inferred from the compose `start_period`. That was
> wrong by an order of magnitude.** Measured 2026-07-30 at 0.173.0, polling both `:8080/health` directly
> and `:8081/api/auth/providers` through nginx every 250 ms while running
> `docker compose up -d --force-recreate api`.
>
> **The stack measured is the one serving `diariz.stocks-hayward.com`** - it runs on a workstation, but it
> is the live deployment, not a scratch environment. Treat these as production numbers.

| | |
|---|---|
| Last healthy sample before | `16:45:08.441` |
| First failing sample | `16:45:08.720` |
| Last failing sample | `16:45:11.350` |
| First healthy sample after | `16:45:11.613` |
| **Total user-visible gap** | **under ~3.2 seconds** (7 failed samples at 250 ms) |
| Container process start -> `Now listening on` | **~1.8 seconds** |

During the gap, nginx stayed up and answered **502** on every request - never a connection failure. That
is exactly the case `lib/retry.ts` retries, and the first retry is at 3 s, so an upload landing in this
window is very likely to succeed on its first retry without the user seeing anything.

**Startup is not dominated by migrations or seeding.** From the container's first EF log line to
*"No migrations were applied. The database is already up to date."* was **~360 ms**, and the whole
`Program.cs:482-497` block (migrate -> bucket -> roles -> user -> authority -> formulas -> meeting types)
finished inside about a second. Every step is a no-op on a warm database, and cheaply so.

### The held-outage test (2026-07-30) - upload survived 50s down, but the retry never fired

A second, deliberately harsher test: `docker compose stop api`, held **50 seconds**, with a real recording
in progress, and Stop pressed **14 seconds into the outage**.

| Event | Time (UTC) |
|---|---|
| API stopped | `16:11:08` |
| nginx begins buffering the upload body (user pressed Stop) | `16:11:22` |
| API reachable again | `16:11:57.8` |
| `POST /api/recordings` -> **`201 Created`** | `16:11:58` |

**The upload succeeded with no error and no user action**, and - confirmed by the operator watching it -
**nothing was visible in the UI**: no error, no recovery banner, no noticeably longer upload, and status
updates continued to arrive. A 50-second outage of the production API, with a recording in flight, was
invisible to the person using it.

(Transcription itself may have been slower, but that is not attributable - the GPU was busy with an
unrelated production run at the time. Worth noting so the observation is not over-read.)

But the nginx access log records **exactly one** `POST /api/recordings` for the whole window, and it
returned 201 - **no 502 was ever logged for it**. So the client made a single attempt that succeeded; the
retry ladder was never entered.

What actually happened is the buffering behaviour above: nginx spent the outage receiving the body, and by
the time it needed the API, the API was back. **The outage was absorbed by the proxy, not by the retry.**

Two honest conclusions:

1. **The system is more robust than the model predicted**, by a mechanism that was not in the model.
2. **`lib/retry.ts` remains proven only by unit tests.** To exercise it for real you need the body transfer
   to *finish* during the outage - a short recording, or an outage started immediately after Stop rather
   than before it.

#### The SignalR change, by contrast, *was* proven - and the old policy would have failed here

The hub's full trace across the outage:

| Time (UTC) | Event |
|---|---|
| `16:11:07` | Websockets drop (`GET /hubs/transcription` 101 ends); `negotiate` -> **502** |
| `16:12:09` | `negotiate` -> **504** (attempt times out) |
| `16:12:14` | `negotiate` -> **200** - **reconnected** |

The hub came back on its own **67 seconds after the outage began**, with no reload.

That is the decisive detail: **the old `withAutomaticReconnect()` default gives up at about 42 seconds**
(`[0, 2s, 10s, 30s]`), which is *before* the API became reachable again at `16:11:57.8`. Its final attempt
would have landed at roughly `16:11:49` and failed, and the hub would then have stayed dead for the rest of
the session - live status updates silently gone until the user reloaded.

So of the three Tier 1 changes, this test exercised exactly one, and confirmed it end to end against a real
outage. The upload retry was bypassed by proxy buffering (above); the token-refresh retry was not exercised
at all, since no token was near expiry during the window.

**The API stayed healthy afterwards** - 640 consecutive healthy samples over the following ~3 minutes, on
both the direct and through-nginx paths. Single clean outage, no crash loop, no delayed failure.

**So there is nothing worth trimming.** The `start_period: 60s` in the compose healthcheck is a generous
allowance for a *first* boot - when migrations really run, the MinIO bucket is created and the seed user
is written - not the steady-state restart cost. Reading it as the restart cost was the mistake.

The practical consequence: **the "make the gap smaller" work is unnecessary.** A redeploy costs about
three seconds, and the Tier 1 client-side changes already cover it.

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
> **cancelled, not deferred** - the measurement in section 3 showed the restart costs about three seconds,
> not the 30-60 estimated, and startup does no meaningful work on a warm database. There is nothing to
> trim. Tier 2 is on the roadmap as Theme 5.

### Tier 0 - accept it, and say so (no work)

Redeploy whenever. Recordings survive; the worst case is a retry click, and the audio is never lost. For a
platform this size that may simply be the right answer, and it is worth knowing it is already true rather
than assuming it is not.

### Tier 1 - make the gap small and quiet (cheap, high value)

1. **Retry the upload** once or twice on 502/503/504 before falling back to the recovery banner. Closes
   most of Window A on its own. **Done.**
2. **Fix the token refresh retry** (Window B). Worth doing regardless of deployment. **Done.**
3. **Deploy `api` and `web` as separate steps**, so the proxy is never down at the same moment as its
   upstream. **Done** (documented in the synopsis).
4. ~~**Trim API startup** so the gap is closer to 10 seconds than 60.~~ **Cancelled** - measurement showed
   the gap is already ~3 s and startup does nothing meaningful on a warm database. See section 3.
5. **Give SignalR an explicit retry policy** instead of the 4-attempt default, so a short gap does not
   permanently kill the hub. **Done.**

Result: a redeploy that almost nobody notices, with no architectural change and no new infrastructure.

### Tier 2 - actual zero-downtime (real work, only if Tier 1 is not enough)

In dependency order: claim webhook deliveries with a lease or `FOR UPDATE SKIP LOCKED`; decide what the
retention and backfill jobs do with two instances (probably a leader lock); add the Redis SignalR
backplane; move `MigrateAsync` out of startup and adopt expand/contract migrations; put a health-checked
proxy in front and drop the fixed host ports.

That is a genuine project, and none of it is about recording.

---

## 6. Worth re-checking before acting

- ~~The 30-60s API gap is inferred, not measured.~~ **Measured 2026-07-30: ~3 seconds.** See section 3.
  The lesson generalises - `start_period` is a first-boot allowance, not a restart cost, and reading it as
  one produced an estimate that was wrong by more than 10x and nearly bought a pointless optimisation.
- **The measurement was taken on the local stack, not the dev/prod server.** A remote host with slower
  disk, a cold page cache, or a larger database could differ - though since almost all the time is process
  start rather than database work, probably not by much. Worth one repeat on `dev` before treating ~3 s as
  a platform-wide number.
- **The gap has not been observed end-to-end against a live recording under load.** The restart above ran
  with a recording in progress and did not disturb it, which is consistent with the design, but Stop was
  not pressed inside the 3-second window - so the upload-retry path is proven by unit tests, not yet by a
  live catch. Deliberately holding the API down for ~30 s while a recording is in progress would test it
  properly.
- Whether anyone has ever actually hit Window A in production - the recovery banner firing would be the
  signal, and it is not currently instrumented.
- Whether the outer reverse proxy in front of the web container adds its own buffering or retry behaviour
  that changes the picture (it already rewrites tokens and strips fragments, so it is not neutral).
