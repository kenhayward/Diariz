# Observability: crash reporting and API timings via GlitchTip

**Date:** 2026-07-31
**Status:** Design, awaiting implementation plan

## 1. Goal

Answer two questions that Diariz currently cannot answer:

1. **What broke for a user?** - the stack trace, the release it happened on, and what the user
   was doing beforehand, across all three runtimes (Python worker, .NET API, React SPA).
2. **Where is time going?** - per-endpoint latency (p50/p95), per-stage timings inside a
   transcription job, and how long outbound LLM calls actually take.

Today there is no answer to either. The API logs to console with the default `ILogger`, the
worker logs to stdout, and browser errors reach an `ErrorBoundary` that shows the user a message
and tells nobody.

## 2. Approach and why

**GlitchTip, using the official Sentry SDKs.** GlitchTip is MIT-licensed and speaks the Sentry
wire protocol, so the real `sentry-sdk` / `Sentry.AspNetCore` / `@sentry/react` packages point at
a self-hosted endpoint.

It covers **both** goals in one container. Its performance monitoring records transactions and
spans with count, average, p50 and p95 per transaction group, and drills into a span breakdown to
show which child operation consumed the time. Sentry SDKs propagate `sentry-trace` and `baggage`
automatically over HTTP, so a browser XHR and the ASP.NET Core request it triggered land in a
single trace.

### Rejected alternatives

**OpenTelemetry Collector plus a trace backend (Jaeger / Tempo / SigNoz).** Rejected for now.
The collector stores nothing, so it obliges a storage backend, and the realistic options mean
running ClickHouse or the four-service Grafana stack on a box already carrying a ~12 GB worker
image and GPU models. Since GlitchTip already provides p50/p95 spans, this buys very little
today. OTel browser instrumentation is also still described by its own project as experimental
and subject to breaking change, so it is the weaker option for the SPA specifically.

This is not a dead end: GlitchTip 6.2 (June 2026) added native OTLP log ingest at `/v1/logs`
with no SDK required, and announced span and trace ingest as forthcoming. If OTel is wanted
later, GlitchTip is on a path to receive it rather than be replaced by it.

**SigNoz alone.** OTel-native and genuinely one product, but ClickHouse is a substantial tenant
and its error-tracking experience is weaker than the Sentry-style flow that is the primary goal.

### Known limitations, accepted

- **No session tracking.** GlitchTip does not support sessions; `autoSessionTracking` must be
  `false`. No release-health or crash-free-rate metrics.
- **No session replay.** No DOM playback of a user's session.
- **Co-located GlitchTip cannot report that the box is down.** It reports application failures,
  not infrastructure death. Uptime monitoring is a separate concern and out of scope.
- **Spans require DuckDB enabled** (`GLITCHTIP_ENABLE_DUCKDB`). DuckDB runs inside the existing
  Python process writing Parquet, so this costs about 128 MB of RAM and no extra container.

## 3. Non-goals

- Infrastructure metrics (CPU, GPU, VRAM, queue depth). Prometheus can be added later without
  conflicting; it is not part of this work.
- Uptime / external availability monitoring.
- Session replay.
- Linking API traces to worker traces across the Redis stream (see 5.3).
- Any change to what end users see. This is operational tooling; the app's feature set is
  unchanged.

## 4. Architecture

### 4.1 Two fully independent instances

Dev and prod each get their own GlitchTip, own database, own retention, own subdomain. Nothing is
shared, and events are not distinguished by an `environment` tag on a shared instance. Dev is the
proving ground: every sub-phase lands on dev and is observed there before prod is touched.

### 4.2 Per-box composition

| Component | Choice | Rationale |
| --- | --- | --- |
| GlitchTip | Single all-in-one container (web + worker) | ~256 MB minimum, 512 MB recommended |
| Database | **Its own Postgres container**, own volume | See below |
| Cache / queue | **Reuse the existing Redis**, DB index 1 (`redis://redis:6379/1`) | `VALKEY_URL` is optional and only affects cache and task speed; small blast radius, saves a container |
| Cold storage | **Reuse MinIO**, dedicated `glitchtip` bucket | See 4.4 |
| Delivery | Optional overlay `deploy/docker-compose.observability.yml` | Mirrors the existing standalone `docker-compose.rocm.yml` pattern; core stack unchanged for anyone who does not want this |

**Why a separate Postgres rather than a second database in `pgvector/pgvector:pg16`:**

1. The platform backup/restore feature (`MaintenanceController`, with its `CurrentFormat` fence)
   operates on the app database. Co-locating an observability tool's tables entangles two things
   with completely different retention and recovery semantics.
2. The `postgres` image only auto-creates `POSTGRES_DB`. A second database needs an init script in
   `/docker-entrypoint-initdb.d`, which **only runs on a fresh volume** - so on the existing dev
   and prod boxes it would silently not run, forcing a hand-run `CREATE DATABASE` on live systems.
3. GlitchTip requires Postgres 14+; Diariz is pinned to 16 for pgvector. Decoupling means
   GlitchTip's upgrade cycle never blocks Diariz's.

Cost is roughly 50-100 MB of RAM. Accepted.

### 4.3 Instrumentation points

| Where | Package | Captures |
| --- | --- | --- |
| Python worker | `sentry-sdk` | Unhandled exceptions; one transaction per job with a span per stage |
| API | `Sentry.AspNetCore` | Unhandled exceptions; request transactions with p50/p95 per endpoint; outbound HttpClient calls as spans |
| SPA | `@sentry/react` | Unhandled exceptions and `ErrorBoundary` captures; browser tracing linking XHR to the API request |

### 4.4 MinIO cold storage: a verified constraint

DuckDB cold storage writes Parquet to either a local directory or an S3 bucket
(`GLITCHTIP_COLD_STORAGE_BUCKET`). Reusing the existing MinIO keeps telemetry in the same place
as everything else durable.

**Verified against the code:** the platform backup enumerates blobs through
`_storage.ListKeysAsync` (`MaintenanceController.cs`), and `AudioStorage` scopes every S3 call to
the single configured `Storage:Bucket`. Restore wipes using that same bucket-scoped listing. A
separate bucket is therefore invisible to both backup and restore.

**Two rules follow, and both are load-bearing:**

- **GlitchTip must use its own bucket, never a prefix inside `recordings`.** Restore *wipes* the
  recordings bucket before repopulating it. Cold storage placed under a prefix there would be
  silently destroyed by any restore.
- **GlitchTip must have its own MinIO access key**, scoped by policy to its own bucket. Handing it
  `MINIO_ROOT_USER` would give an observability tool full read/write access to every user's audio.

## 5. Phase 1: worker and API

### 5.1 Sub-phase 1a - infrastructure, dev only

Bring up GlitchTip on the dev box. Create the organisation, two projects (`diariz-worker`,
`diariz-api`), and issue their DSNs. Configure the outer proxy subdomain and TLS. Nothing is
instrumented yet.

This exists as its own step so that the container, the SMTP wiring, the CSRF/proxy configuration
and the TLS certificate are all proven before any application code changes.

**Done when:** the GlitchTip UI is reachable over HTTPS on its subdomain, login works, a password
reset email arrives, and a manually-sent test event appears.

### 5.2 Sub-phase 1b - Python worker

Highest value in the whole plan. The worker is where the genuinely nasty failures live (model
load, CUDA, pyannote), and per-stage timings are information that does not exist today in any
form.

- Initialise `sentry-sdk` when `SENTRY_DSN` is set; complete no-op when it is empty.
- Unhandled exceptions in `worker.handle` reported with job context (transcription id, model,
  blob key) - **never** transcript content.
- One transaction per job, with a span per stage: blob download, ASR, alignment, diarization,
  speaker embeddings, callback POST.
- Release tag from the platform version.

**Done when:** a deliberately failed job appears in GlitchTip with a usable traceback, and a
successful job shows a stage breakdown that accounts for the wall-clock time.

### 5.3 Sub-phase 1c - API

- `Sentry.AspNetCore` initialised when the DSN is set; no-op when empty.
- Unhandled exceptions, plus request transactions giving p50/p95 per endpoint.
- `SentryHttpMessageHandler` on the outbound `HttpClient`s so summarisation, embedding and
  dictation calls to the LLM endpoint each become a span. Given the history of LM Studio
  slowness and runtime faults, "is this us or the model" becomes directly answerable.
- Release tag from `version.json`, which the API already reports at `GET /health`.

**Deliberately excluded: API-to-worker trace linking.** That hop is a Redis stream job, not an
HTTP call, so propagation would mean adding trace headers to the `TranscriptionJob` payload - a
cross-boundary contract change requiring both the .NET producer and the Python consumer to move
in lockstep. Each side gets its own traces for now. Revisit only if the disconnect proves
painful in practice.

**Done when:** a forced 500 appears with a stack trace, the endpoint list shows p50/p95, and a
summarisation request shows the outbound LLM call as a timed child span.

### 5.4 Sub-phase 1d - promote to prod

Stand up the prod instance and enable the worker and API DSNs there. Gated on dev having run
long enough to trust the scrubbing rules, with a deliberate review of what actually arrived.

## 6. Phase 2: SPA

Phase 2 inherits the scrubbing rules proven server-side in phase 1. That is the reason for this
ordering: browser payloads start flowing only after the redaction approach has been observed
working on real traffic.

### 6.1 Sub-phase 2a - errors, and the DSN delivery problem

**The DSN cannot be a build-time variable.** The SPA today uses no `import.meta.env` values at
all, and `apps/web/Dockerfile` takes only `BUILD_COMMIT`. Everything environment-specific is
same-origin by design. A `VITE_SENTRY_DSN` baked at build time would produce one image carrying
one DSN, so dev and prod would either share a GlitchTip instance - which this design explicitly
rejects - or require two separate image builds of identical source.

**Resolution: the API serves the browser DSN at runtime.** Browser DSNs are public by design
(they are embedded in shipped JavaScript), so this leaks nothing. The API already serves
`GET /health` with the version; the browser DSN is served the same way, from
`Sentry__BrowserDsn`. One image, two environments, correct DSN in each, and the existing
"empty value disables the feature" convention is preserved.

**Accepted trade-off:** the SDK cannot initialise until that config request returns, so errors
thrown during the very earliest boot are missed. If those turn out to matter, the mitigation is a
minimal inline `window.onerror` buffer replayed once the SDK is live. Start without it.

`@sentry/react` wired into the existing `ErrorBoundary`, with `autoSessionTracking: false`
(GlitchTip has no sessions). Errors only; tracing off.

Note this also covers the **Electron desktop app**, which loads the SPA from the server origin -
desktop users' renderer errors arrive for free. Main-process errors would need `@sentry/electron`
and are out of scope.

### 6.2 Sub-phase 2b - browser tracing

Enable browser tracing so XHR calls link to their API request in one trace. axios uses XHR, so
instrumentation is automatic.

**Prerequisite:** confirm the outer proxy does not strip `sentry-trace` and `baggage` headers on
the app's own hostname. If it does, tracing degrades into disconnected halves and **fails
silently** - no error, just permanently unlinked traces.

### 6.3 Sub-phase 2c - source maps

The web build currently ships `sourcemap: true` to production, meaning the application source is
publicly readable. Switch to uploading source maps to GlitchTip per release
(`glitchtip-cli sourcemaps inject` / `upload`) and stop serving them. This yields readable stack
traces **and** closes the source exposure - a security improvement independent of observability.

Requires a CI step keyed to the release version.

## 7. Configuration

### 7.1 Off by default

Every integration is disabled unless its DSN is set, exactly as `Summarization__ApiBase` and
`Dictation__ApiBase` already work. Nobody who clones the repo gets telemetry they did not ask
for, and the test suites are unaffected.

| Variable | Applies to | Empty means |
| --- | --- | --- |
| `SENTRY_DSN` | Worker | No SDK init, no-op |
| `Sentry__Dsn` | API | No SDK init, no-op |
| `Sentry__BrowserDsn` | API, served to the SPA at runtime (see 6.1) | No SDK init, no-op |
| `Sentry__TracesSampleRate` / `SENTRY_TRACES_SAMPLE_RATE` | API, worker | Defaults per below |

### 7.2 Sampling

GlitchTip's documentation recommends `tracesSampleRate: 0.01` because every HTTP request becomes
a transaction. **That advice targets high-traffic sites and does not apply here.** Diariz volume
is small enough to capture everything: errors at 100%, traces at 100% initially, with the knob
exposed so it can be turned down if event volume ever becomes a problem. Disk cost is roughly
30 GB per *million* events, which this deployment will not approach.

### 7.3 Email

GlitchTip requires `EMAIL_URL` (or a provider API key) for invitations and password resets.

**Its own credentials in `.env`**, distinct from the app's MailKit settings, under a technical
contact address. In practice the same mailbox today, but the two are allowed to diverge without
one change breaking the other.

### 7.4 Registration

`ENABLE_USER_REGISTRATION=false` and `ENABLE_ORGANIZATION_CREATION=false` on both instances.
These are internal tools on public subdomains; open registration on them is not wanted.

## 8. Outer nginx proxy

Each environment gets its own subdomain, proxied by the existing outer TLS-terminating proxy to
the GlitchTip container's port 8000. This is a **separate server block** from the app's, pointing
at a different upstream - GlitchTip does not sit behind the `web` container's nginx.

### 8.1 Prerequisites

- DNS A record per environment.
- A TLS certificate covering each new hostname (or a wildcard).
- The GlitchTip port reachable from the proxy: published on the host, or on a shared Docker
  network if the proxy runs in Docker.

### 8.2 The failure that will happen if this is got wrong

GlitchTip is Django. **Django rejects logins with a CSRF failure when the proxy does not tell it
the original scheme was HTTPS.** This is the single most common self-hosting problem, and it
presents as "my password is wrong" rather than anything mentioning proxies.

Two things must agree:

- `GLITCHTIP_DOMAIN` must be the **full external URL including scheme** (`https://errors.example`),
  because Django derives its trusted CSRF origins from it.
- The proxy must send `X-Forwarded-Proto: https`.

The existing `apps/web/nginx.conf` already solves the identical problem for OpenIddict with its
`$client_proto` map, and the comment there explains the same trap. The new server block needs the
same discipline.

### 8.3 Server block shape

```nginx
# At http level, NOT inside server. Required by the Connection header below - nginx will refuse
# to start with "unknown variable connection_upgrade" if this is omitted. Many configs already
# have it; do not declare it twice.
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 443 ssl;
    server_name errors.dev.example.com;

    # TLS config as per the existing app server blocks.

    location / {
        proxy_pass http://127.0.0.1:8000;   # or the container name on a shared Docker network

        proxy_set_header Host              $host;
        proxy_set_header X-Real-IP         $remote_addr;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        # Load-bearing: without this Django 403s every login with a CSRF failure.
        proxy_set_header X-Forwarded-Proto $scheme;

        # Source map bundles and minidumps are far larger than nginx's 1 MB default.
        client_max_body_size 100m;

        # Source map uploads can be slow on a large bundle.
        proxy_read_timeout 300s;
        proxy_send_timeout 300s;

        proxy_http_version 1.1;
        proxy_set_header Upgrade    $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
    }
}
```

### 8.4 A separate change to the *app's* server block

Distinct from the above, and only needed for phase 2b: the **existing** app hostname's proxy
configuration must not strip `sentry-trace` and `baggage` request headers. nginx does not strip
arbitrary headers by default, but this deployment's outer proxy is already known to rewrite
things, so it needs verifying rather than assuming. The failure is silent.

### 8.5 Ad blockers

Browser ad blockers pattern-match Sentry ingest paths and hostnames. Some proportion of browser
events will never leave the client, with no signal that they did not. If this proves material,
the mitigation is a same-origin tunnel through the app's own nginx. Phase 2 only - the worker and
API are unaffected. Start without a tunnel and measure.

## 9. PII policy

**The highest-severity item in this plan.** Transcripts, meeting content, attendee names and email
addresses are the application's payload. Sentry SDKs capture request bodies, breadcrumbs, headers
and XHR URLs by default. Once sent, an event cannot be un-sent.

Rules:

- `SendDefaultPii = false` on every SDK.
- An explicit `before_send` / `SetBeforeSend` scrubber on each runtime, denying by default:
  redact request and response bodies, `Authorization`, `X-Worker-Secret`, cookies, and any field
  carrying transcript, summary, minutes or note text.
- Identify users by **GUID only**, never email or display name.
- Job context may carry ids (transcription id, recording id, blob key) but never content.
- The GlitchTip database and its MinIO bucket are in scope for the platform's data handling, and
  retention must be bounded rather than left at default.

## 10. Testing

Per the project's TDD requirement, with tests written first. What genuinely earns tests:

| Test | Where | Why |
| --- | --- | --- |
| Scrubber redacts transcript text, auth headers, cookies, emails | `Diariz.Api.Tests` and worker `pytest` | This is the code whose bugs leak customer data |
| Scrubber preserves the ids needed for diagnosis | Both | A scrubber that redacts everything is useless |
| Empty DSN means no init and no network call | Both | The off-by-default guarantee |
| `worker.handle` orchestration and temp cleanup still pass with span wrapping added | Worker `pytest` | Regression guard on existing behaviour |
| `ErrorBoundary` still renders its fallback with capture enabled | `vitest` + RTL | Phase 2; a reporting hook must not break the user-facing fallback |

The SDKs themselves are not tested.

## 11. Release and documentation obligations

Per `CLAUDE.md`, for **every** PR in both phases:

- **Version:** Build +1 (infrastructure and operational tooling, not a user-facing functional
  enhancement). `version.json` plus all four mirrors.
- **Release notes:** one `RELEASES[0]` entry per PR.
- **`docs/Overall_Synopsis_of_Platform.md`:** yes - new deployable component, new external
  dependencies, new deployment surface.
- **`docs/Data_Schema.md`:** no - GlitchTip owns its own database and its own MinIO bucket;
  the Diariz schema is unchanged.
- **README Features / `docs/features.md` / About-box `CAPABILITIES`:** no - nothing user-visible
  changes. (`AboutModal.tsx` disclaimers: to be decided, see 13.)
- **Deployment surface:** server redeploy only. **No desktop release** in either phase - nothing
  under `apps/desktop/src/**` or the builder config is touched.

## 12. Risks

| Risk | Severity | Mitigation |
| --- | --- | --- |
| PII leaked in event payloads | **High** - silent and irreversible | Deny-by-default scrubber, tested; dev-first rollout with a deliberate review of what arrived |
| Outer proxy strips `sentry-trace` / `baggage` | Medium - fails silently | Verify before 2b; check for linked traces rather than assuming |
| Django CSRF failure behind the proxy | Medium - blocks 1a, misleading symptom | `GLITCHTIP_DOMAIN` with scheme + `X-Forwarded-Proto`; see 8.2 |
| Cold storage placed inside `recordings` | **High** - destroyed by any restore | Separate bucket, mandated in 4.4 |
| GlitchTip holding MinIO root credentials | Medium | Dedicated scoped access key |
| Ad blockers drop browser events | Low | Phase 2 only; measure, tunnel if material |
| Disk growth from spans and Parquet | Low at this volume | Bounded retention set explicitly |
| SPA bundle growth (~30-40 kB gzipped) | Low | Accepted; errors-only in 2a is smaller than with tracing |
| Co-located GlitchTip cannot report a dead box | Accepted | Out of scope; see 2 |

## 13. Open questions

- **`AboutModal.tsx` disclaimers.** The project convention is to list new third-party libraries
  there. `@sentry/react` ships in the SPA bundle in phase 2, which arguably qualifies even though
  no user-facing capability changes. Decide before the 2a PR.
- **Retention periods.** Not yet chosen for either environment. Dev can be aggressive; prod
  should be deliberate rather than defaulted.
- **SDK versions.** Pin at implementation time rather than in this document, so the plan does not
  ship a stale version number.
