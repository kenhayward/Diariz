# Diariz — Platform Synopsis

A detailed, current view of what Diariz is, how it's built, and how the pieces fit together. For the
data model and object-storage layout see [`Data_Schema.md`](Data_Schema.md); for the macOS port see
[`macOS_Desktop_App_Guide.md`](macOS_Desktop_App_Guide.md); for speaker-ID internals see
[`Speaker_Identification_and_Verification.md`](Speaker_Identification_and_Verification.md).

## What it is

Diariz is a **self-hostable, multi-user voice/meeting transcription platform**. You **record** (microphone,
system audio, or **both mixed together on one device** - system audio via `getDisplayMedia` "Share audio" in
Chromium browsers, or the desktop app's Windows loopback / macOS ScreenCaptureKit) or **upload** an audio file; the server **transcribes
it with speaker diarization and word-level timestamps**; and you get speaker-labelled, timestamped segments
you can rename, edit, play back, and re-transcribe. On top of the transcript it can **identify known speakers
across recordings** (voiceprints), **summarise**, **extract action items**, **email/download** the
transcript, and **chat across one or more transcripts** — all using an **OpenAI-compatible LLM endpoint you
configure** (per user or server-wide), so audio, transcripts, and the model stay on infrastructure you
control.

The canonical app version lives in `/version.json` (mirrored to the web/desktop `package.json`s and the API
`<Version>`); the API reports it at `GET /health`, and user-facing release notes live in
`apps/web/src/lib/releases.ts`.

## Components

Diariz is **four code components** that communicate across process and language boundaries, plus three
infrastructure services.

| Component | Stack | Path | Role |
|---|---|---|---|
| **API** | ASP.NET Core (**.NET 10**), EF Core, SignalR, AWS S3 SDK, MailKit, OpenIddict (OAuth AS) | `src/Diariz.Api` | Auth, orchestration, persistence, audio storage/streaming, summarisation + chat + action-extraction, SignalR notifications, OAuth 2.1 server for the MCP web connector |
| **Domain** | EF Core + Npgsql + pgvector | `src/Diariz.Domain` | Entities, `DiarizDbContext`, migrations (compiled into the API) |
| **Worker** | Python: WhisperX (large-v3), pyannote 3.1, SpeechBrain ECAPA, CUDA | `src/Diariz.Worker` | GPU transcription → alignment → diarization → per-speaker voiceprints |
| **Web** | React 19 + TypeScript + Vite + Tailwind v4 | `apps/web` | SPA UI (served by nginx in Docker); **installable as a PWA** in Chromium - the only app-like client on Linux, which has no desktop build |
| **Desktop** | Electron thin shell - Windows tray + **macOS (beta) menu-bar** | `apps/desktop` | Mic + system audio (Windows loopback / macOS ScreenCaptureKit), tray recording; auto-update on Windows, manual update check on macOS; loads the web app from the server origin |
| **n8n node** | TypeScript, zero runtime dependencies, MIT | `integrations/n8n-nodes-diariz` | Published npm package (`n8n-nodes-diariz`) installed into a **user's own n8n**, not deployed with Diariz: a self-registering webhook trigger and a full REST action node. **Mirrors `version.json`** so the node's number names the Diariz version it wraps. |

Infrastructure (via Docker Compose, project name **`diariz`**):

- **PostgreSQL + pgvector** (`pgvector/pgvector:pg16`) — relational data **and** voiceprint/embedding vectors.
- **Redis** (`redis:7`) — job queues (Redis **Streams**), nothing is stored long-term here.
- **MinIO** (S3-compatible) — original audio blobs and uploaded attachment files.

### Ports (Docker / local dev)

| Service | In-container | Host (Docker) | Dev |
|---|---|---|---|
| API | 8080 | 8080 | 8080 |
| Web (nginx, proxies `/api` + `/hubs` + `/mcp`) | 80 | **8081** | Vite dev server **5173** (proxies to 8080) |
| Postgres | 5432 | **5433** | 5432 |
| Redis | 6379 | not published | 6379 |
| MinIO S3 API | 9000 | **9002** | — |
| MinIO console | 9001 | not published | — |

Two host ports are deliberately remapped so a Compose stack can sit alongside other local instances of the
same service: the **MinIO S3 API** (9002) and **Postgres** (**5433**, so it does not clash with a Postgres
already on the host's 5432). Postgres is published purely for external tooling - psql, pgAdmin, a test
harness on another machine - and is overridable per host via `POSTGRES_PORT` / `POSTGRES_BIND` in `.env`.
Note that a published port binds `0.0.0.0` by default **and bypasses the host firewall** (Docker writes its
own DNAT rules), so an exposed database needs a strong `POSTGRES_PASSWORD`; `POSTGRES_BIND=127.0.0.1`
restricts it to the host. The MinIO web console (container 9001) is **not published** - the app never uses
it (the API reaches MinIO in-network at `minio:9000`), so port-forward or `docker exec` if you need it.
In-container, services address each other by Compose service name (`minio:9000`, `redis:6379`,
`postgres:5432`, `api:8080`) - publishing a port changes nothing about that private path.

### Redeploying while the platform is in use

**Recordings survive a redeploy.** Capture is entirely client-side (an in-memory `Blob[]` in the page,
with notes and screenshots stashed in IndexedDB as they are taken) and the API is contacted exactly once,
at Stop. Nothing streams to the server during a meeting, so restarting the API cannot end one. The client
also stashes the finished audio to IndexedDB **before** attempting the upload, so even an upload that
fails outright is recoverable from the banner rather than lost. See
`docs/Research/zero-downtime-redeploy.md` for the full analysis.

**Redeploy `api` and `web` as separate steps, API first:**

```
docker compose up -d --build api     # wait for it to report healthy
docker compose up -d --build web
```

`web` is not merely the SPA - it is also the reverse proxy for `/api`, `/hubs`, `/mcp`, `/connect` and
`/.well-known` (`apps/web/nginx.conf`). A single `docker compose up -d` takes both down at once, so the
path to the API is broken at the same moment the API itself is restarting, widening the outage for no
reason. Bringing the API up first means the proxy is only ever briefly absent in front of a *healthy*
upstream.

Replacing the `web` container is close to free for anyone already in the app: the SPA builds to a single
bundle with exactly one lazy route (the API reference), and every `import.meta.glob` is `eager`, so a
loaded tab needs nothing further from that container. The usual stale-chunk hazard of an SPA redeploy does
not apply here - **but it would the moment a second `lazy()` import is added**, so keep that in mind.

The *opposite* hazard is real, and it hid two releases before anyone noticed: **`index.html` must never be
cached.** nginx sends no `Cache-Control` of its own - only `ETag`/`Last-Modified` - and a cache is then free
to apply heuristic freshness (RFC 9111 4.2.2), reusing the shell without revalidating. That shell names the
previous build's content-hashed bundles, so a redeploy reaches nobody until each client's cache happens to
expire: the fix is live on the server and the user still sees the old app, with a hard reload the only way
through. It bites the **desktop shell** hardest, since that loads the SPA from this origin and people leave
it open for days. `apps/web/nginx.conf` therefore sets `no-cache` on `/index.html` (revalidation costs a 304
on about a kilobyte) and `immutable, max-age=1y` on `/assets/`, which is safe precisely because those names
are content-hashed. `/assets/` also stops falling through to the SPA fallback, so a missing bundle is a clean
404 rather than HTML served as JavaScript.

**The web app manifest is served under the same two rules, plus a third that is easy to miss.**
`/manifest.webmanifest` gets `no-cache` for exactly the reason `index.html` does - it is the document that
names the icons and the `start_url`, so a heuristically-cached copy pins an *installed* app's identity to a
previous build - while `/icons/` gets `immutable, max-age=1y` like `/assets/`. The third rule is the MIME
type: nginx's bundled `mime.types` has no `manifest` entry at all (verified on nginx 1.31.2), so without an
explicit `types { application/manifest+json webmanifest; }` the file goes out as `application/octet-stream`.
Vite's dev server resolves the extension on its own, which makes this a **deploy-only** failure, and any
replacement front end or alternative web server in this position needs the same mapping. There is
deliberately **no service worker**: it would put a second caching layer in front of the shell, which is the
hazard this whole section is about, and Chromium has not required one for installability since version 112
on desktop.

The shell was given the matching half, because a header only helps a client that asks: it loads the document
with `pragma: no-cache` (`apps/desktop/src/documentLoad.js`) so every launch revalidates, and its tray gained
a **Reload** item that calls `reloadIgnoringCache()`. Both exist because the Windows build had *no* way to
force a refresh - it runs menu-less (`Menu.setApplicationMenu(null)`), so Electron's Ctrl-R / Ctrl-Shift-R
accelerators are never registered, and closing the window only hides it to the tray, so the process a user
believes they restarted has usually been up for days. Diagnosing this from the outside starts with
`Get-Process Diariz | Select StartTime`.

Three client behaviours cover the API's own restart window: uploads retry past a gateway error
(`lib/retry.ts`), the sliding-session token refresh retries on failure rather than lapsing
(`lib/tokenRefresh.ts`), and the SignalR hub reconnects indefinitely instead of giving up after ~42s
(`lib/signalrRetry.ts`).

**Measured cost of an API redeploy: about 3 seconds** (2026-07-30, local stack at 0.173.0, polling every
250 ms across a `--force-recreate`). nginx stays up throughout and answers **502** - never a connection
failure - which is precisely what the upload retry handles. Container start to `Now listening on` is ~1.8 s;
the migrate-and-seed block costs well under a second on a warm database. Note that the `start_period: 60s`
on the API healthcheck is a **first-boot** allowance (real migrations, bucket creation, seed user), not the
restart cost - reading it as the latter previously produced an estimate ten times too high.

#### Which containers are safe to redeploy, and when

"A recording is running" hides three different in-flight states, and they carry very different risk. The
capture itself is the safest of the three, because it is entirely client-side.

| Container | Capture in progress | Upload in flight (the ~30s body transfer after Stop) | Processing in flight (transcribe/summarise/minutes/actions/tags) |
|---|---|---|---|
| **api** | Safe (measured) | Safe (measured - nginx buffers the body) | **Delays the job** - it is reclaimed, not lost |
| **web** | Safe | **Kills the upload** | Safe |
| **worker** | Safe | Safe | **Delays the job** - reclaimed after 10 min idle |
| **redis** | Safe | Risky | Risky (queue survives a restart now, but reconnects churn) |
| **postgres** | Safe | **Upload fails** | Fails |
| **minio** | Safe | **Upload fails** | Fails |

Three things are worth knowing because they are the opposite of what you would guess:

- **`web` is more dangerous than `api` during an upload.** An API restart is survivable *because* nginx
  buffers the request body; nothing protects against nginx itself going away mid-transfer. Worse, a dead
  nginx gives the browser a connection error rather than a 502, and `lib/retry.ts` deliberately does not
  retry those (they are ambiguous - the request may have been processed). That upload falls to the
  recovery banner. An outer proxy may convert it to a 502, in which case the retry does fire; untested.
- **Killing `api` or `worker` mid-job no longer strands it.** It used to: `XACK` sits in a `finally` that
  never runs when a process is killed, and every consumer reads only `">"`, so the message stayed pending
  forever and the recording sat in `Transcribing` with nothing to move it on. `StreamReclaimer` (API) and
  `worker.reclaim_stale` (Python) now take over messages idle past a threshold. The job is delayed, not
  lost. See the orphaned-job recovery note below.
- **Redis is now persistent** (`appendonly yes` + a `redisdata` volume). Before that a Redis restart
  silently discarded every queued job.

Practical rule: `api` and `web` - the two you actually ship - are fine to redeploy whenever.
**`deploy/BringUpWebApi.cmd` does the whole sequence**, including the in-flight check below.

If you want to avoid even a delay, check the streams the API itself consumes. Note that
`transcription-jobs` and `audio-merge-jobs` are **not** among them - those belong to the worker
container, which an `api`/`web` redeploy never touches, so checking them before one answers the wrong
question:

```
docker compose exec redis redis-cli XPENDING summarization-jobs summarizers
docker compose exec redis redis-cli XPENDING meeting-minutes-jobs minute-takers
docker compose exec redis redis-cli XPENDING actions-jobs actions-extractors
docker compose exec redis redis-cli XPENDING tag-cloud-jobs tag-extractors
docker compose exec redis redis-cli XPENDING formula-run-jobs formula-runners
docker compose exec redis redis-cli XPENDING embedding-jobs embedders
```

A non-zero count is a job in flight. Since 0.174.0 that is a **delay, not a loss** - it is reclaimed once
its message has been idle half an hour - so it is a reason to pause, not a reason not to deploy.

Treat `postgres`, `redis` and `minio` as maintenance-window work.

#### Orphaned-job recovery

Every stream consumer acks in a `finally`, which handles a job that *throws* but not one whose process is
*killed*. The reclaim closes that, and the interesting part is the threshold:

- **API workers** use `StreamReclaimer` with a **30-minute** idle threshold, checked at most once a
  minute per stream. It has to clear the longest legitimate run or one instance would steal a job another
  was still working on and run it twice (a duplicated LLM call and charge). That length is set by the LLM
  timeout, which is resolved per call as `UserSettings.LlmTimeoutSeconds` ?? `PlatformSettings.LlmTimeoutSeconds`
  ?? the server option - so it is **not** capped by the platform default, and any user can raise their own.
  Half an hour clears the slow-local-model values that setting exists for (900s and the like) with margin;
  a user who sets a timeout beyond it can still see a duplicate delivery, bounded by the redelivery cap
  below. The threshold is a compile-time constant (the constructor's overrides are for tests only).
- **The Python worker cannot rely on a margin** - a long transcription legitimately runs for tens of
  minutes, during which its message looks idle. It therefore **refreshes its own claim** every 60s while
  working (`worker.claim_keepalive`), so "idle" genuinely means "nobody is on it", and a 10-minute
  threshold stays safe.
- **Both cap redeliveries** (`RECLAIM_MAX_DELIVERIES` / `StreamReclaimer.MaxDeliveries`, default 3).
  Reclaiming otherwise reintroduces the poison-message loop that acking-in-finally exists to prevent: a
  job that kills the worker would be handed straight to its replacement, forever. Past the cap the
  message is acked and abandoned with a loud log, on the reasoning that it is likelier the cause of the
  deaths than a casualty of them.

**This is not zero-downtime**, and the stack cannot currently provide it: the API binds a fixed host port
and hosts 13 in-process `BackgroundService` singletons, one of which (`WebhookDeliveryProcessor`) claims
work without a row lock and would double-send from a second replica. See the roadmap - though at a
three-second window the case for that work is weak.

## Architecture at a glance

```
                         Browser / Desktop shell (SPA)
                                   │  HTTPS (same-origin)
                                   ▼
   ┌─────────────────────────────────────────────────────────────┐
   │  ASP.NET Core API  (src/Diariz.Api  +  Diariz.Domain)        │
   │   • JWT auth + RBAC          • SignalR hub (/hubs/transcription)│
   │   • Recordings / Sections / Speakers / Actions / Chat / Admin │
   │   • In-process Summarization + MeetingMinutes workers (BGSvc) │
   └───┬───────────────┬──────────────────┬──────────────┬────────┘
       │ EF Core        │ S3 SDK            │ Redis Streams │ HTTP /chat/completions
       ▼                ▼                   ▼              ▼
   ┌────────┐      ┌──────────┐      ┌──────────────┐   ┌──────────────────┐
   │Postgres│      │  MinIO    │      │    Redis      │   │ OpenAI-compatible │
   │+pgvector│     │ (audio)   │      │ transcription-│   │   LLM endpoint    │
   └────────┘      └────┬──────┘      │ jobs / workers│   │ (per-user/server) │
                        │ download    └──────┬───────┘   └──────────────────┘
                        ▼                    │ XREADGROUP
                 ┌──────────────────────────▼──────────────┐
                 │  Python GPU Worker (src/Diariz.Worker)   │
                 │  WhisperX → align → pyannote → ECAPA     │
                 └──────────────────────────┬───────────────┘
                            POST internal/transcriptions/result
                            (X-Worker-Secret)  ▲
                                               └──── back to the API
```

## Core data flow: capture → transcript

1. **Capture/upload.** The web `Recorder` (`MediaRecorder`) records the mic, **system audio**
   (`getDisplayMedia` - available in Chromium browsers via "Share audio", and seamlessly in the desktop shell
   via Windows loopback), or **both mixed** into one track (a Web Audio `MediaStreamAudioDestinationNode`
   sums the mic + system streams; `RecordingSource.Combined`); or the user uploads a file. A "System audio"
   checkbox adds system audio to the capture, and a "No microphone" source option records system audio alone
   (both hidden where `getDisplayMedia` is unsupported); if system audio isn't shared, capture falls back to
   mic-only. For microphone capture the user can pick a **specific input
   device** (`enumerateDevices()`; the choice is persisted in `localStorage` and re-resolved against the live
   device list on hot-plug via `lib/audioDevices.ts`) and toggle **capture constraints** (echo cancellation /
   noise suppression / auto gain / mono) applied to `getUserMedia`. The three DSP constraints **default to
   off** (`DEFAULT_CONSTRAINTS`): capturing system audio means capturing a loopback of the speakers, which is
   exactly what an echo canceller removes — measured, it converged over a few seconds and nulled a real take
   from -28 dB to -84 dB, producing an empty transcript with a healthy-looking level meter. They remain
   available for a genuine echo/room problem, and previously persisted choices are unaffected.

   **Linux system audio** is a special case: Chromium there implements `getDisplayMedia` audio only for
   **tab** sharing, not screen/window, so the documented "Share audio" route yields a silent mic-only take.
   The supported route is to record the speaker monitor as an ordinary input. PipeWire does not publish a
   sink's monitor as a node, so Diariz ships the drop-in that does —
   `apps/web/public/linux/99-diariz-system-audio.conf`, served at `/linux/...` for a per-user install and
   packaged by `packaging/linux/build-deb.sh` into a one-file `.deb` that installs it to
   `/etc/pipewire/pipewire.conf.d/` for every user on a managed machine (config only, depends on `pipewire`,
   registered as a conffile). One canonical file feeds both routes, pinned by `lib/linuxSystemAudio.test.ts`. While recording, a **Web Audio
   `AnalyserNode`** taps the same stream to drive a live **input-level meter** (`lib/audioLevel.ts` +
   `InputLevelMeter.tsx`; a passive read, not connected to output) with a subtle sustained-silence hint. The
   client `POST`s multipart to `POST /api/recordings` (`source = Microphone | System | Upload`), including
   **`startedAt`/`endedAt`** - the wall clock capture began and stopped. The recorder holds the start in its own
   ref rather than in `Timing`, because `recorderTiming.pause()` folds `runningSince` into an accumulator and
   nulls it, destroying the original start on the first pause; both are also stashed with the offline pending
   recording so a recovered upload replays the real time instead of the recovery moment. The server drops an
   implausible value (>24 h future, >366 d past, end before start) rather than failing the upload.
2. **Store + enqueue.** The API streams the blob into **MinIO** (`recordings` bucket, key `{userId}/{recordingId}{ext}`),
   writes a **`Recording`** row, creates a **`Transcription`** row (version 1) and **enqueues a job** on the
   Redis stream **`transcription-jobs`** (consumer group **`workers`**). Uploads are gated by magic-byte
   format sniffing (`AudioFormats`) + size cap (`Uploads:MaxBytes`) + the owner's storage quota.
   - **Video is converted client-side, before any of this.** Dropping an `.mp4`/`.m4v`/`.mov`/`.mkv`/`.webm`
     on the web or desktop UI runs a **mediabunny** (MPL-2.0, WebCodecs) conversion in a **Web Worker**
     (`apps/web/src/lib/videoAudio.worker.ts`): the video track is discarded and the audio is re-encoded to
     **Opus, 1 channel, 48 kHz, 32 kbps in WebM**, which is byte-for-byte the same *kind* of artifact the
     browser recorder produces - so nothing server-side changes. A container holding no video (an audio-only
     WebM, or an MP4 whose only video track is cover art) is passed through **untouched**, never re-encoded.
     Measured: a 60 s 1080p MP4 went from 7.2 MB to 273 kB, stereo AAC to mono Opus, video track gone.
     This is why the 500 MB cap is judged on the *extracted* file rather than the dropped one
     (`apps/web/src/lib/mediaKinds.ts` splits the two guards; the source ceiling is 8 GB).
   - **This is a UI guarantee, not a server invariant.** A direct API, n8n, or MCP caller posting an MP4 to
     `POST /api/recordings` still has the whole video stored, because `AudioFormats.Detect` sniffs any
     ISO-BMFF `ftyp` box as `m4a` and cannot see whether a video track is present without parsing the box
     tree. Closing that would need an ISO-BMFF box-tree parse plus an EBML walk for Matroska; deliberately
     deferred (0.209.0 / see `docs/superpowers/specs/2026-08-12-video-audio-extraction-design.md`).
   - **Four size limits sit in front of this endpoint and must agree**, largest first: nginx
     `client_max_body_size` (1024m, `apps/web/nginx.conf`), Kestrel's per-request `[RequestSizeLimit]` and
     `FormOptions.MultipartBodyLengthLimit` (both `UploadOptions.MaxRequestBytes`, 1 GiB), then the action's
     own `Uploads:MaxBytes` (500 MB) which is the one that produces a readable 413. The multipart one is set
     explicitly in `Program.cs` because it defaults to **128 MB** and is *not* raised by `[RequestSizeLimit]`;
     left at the default it rejected anything larger while binding the form, before the action could explain
     why (a 400 reading `Failed to read the request form. Multipart body length limit 134217728 exceeded`,
     which reads as a framework fault rather than a size refusal). Fixed in 0.186.12 / PR #472; covered by
     `UploadSizeLimitIntegrationTests`, which posts a real 132 MB body over HTTP.
   - **What these limits mean in minutes.** Measured against the recording that surfaced the bug: a
     **3 h 17 m** all-hands from the desktop recorder was **178 MB**, i.e. **~0.9 MB/min** for the WebM/Opus
     the browser produces. At that rate the 128 MB default was cutting in at about **2 h 20 m** - so the bug
     was not an edge case, it made any long meeting unuploadable - and `Uploads:MaxBytes` (500 MB) allows
     roughly **9 hours**. Uploaded *files* are a different story and the reason the cap is not expressed in
     hours anywhere: a 256 kbps MP3 runs ~2 MB/min (500 MB ≈ 4 h) and uncompressed 44.1 kHz stereo WAV runs
     ~10 MB/min, which reaches the cap in under an hour.
   - **The outer reverse proxy is a fifth limit, and it is not in this repo.** Anything in front of the web
     container (an nginx-proxy-manager host, a cloud load balancer) applies its own body cap and timeouts.
     Deployed settings, in use since 2026-08-07 and confirmed by a 178 MB upload that survived a server
     redeploy and a client re-login mid-flight (so the range above 128 MB is exercised; the 500 MB ceiling
     itself is not):

     ```nginx
     client_max_body_size 1024m;      # match apps/web/nginx.conf; see below for why not 500m
     proxy_read_timeout 600s;
     proxy_send_timeout 600s;
     proxy_request_buffering off;
     ```

     Three things worth keeping straight:
     - **Size the proxy above the app, never at it.** `Uploads:MaxBytes` should stay the narrowest limit,
       because the action is the only layer that can answer with "the maximum upload size is 500 MB". A proxy
       set to `500m` both pre-empts that message with a bare nginx 413 *and* rejects a genuine 500 MB file,
       since the multipart envelope adds a few hundred bytes on top of it.
     - **Timeouts, not size, are the next failure.** A 500 MB upload takes minutes on a domestic connection,
       and the API then streams it into MinIO before it answers - well past a typical 60s default, which
       surfaces as a **504** rather than anything about size. Diagnose by the status code: 413 is a limit,
       504 is a timeout.
     - **`proxy_request_buffering off` matters most.** Left on, the proxy spools the whole upload to its own
       disk before forwarding a byte - doubling the wall-clock wait and needing scratch space equal to the
       file. (Note the distinction from the `/mcp` requirement further down: that one disables **response**
       buffering, `proxy_buffering`, for a different reason.)
   - Job payload is JSON with **PascalCase** keys: `{ RecordingId, TranscriptionId, BlobKey, Model, MinSpeakers?, MaxSpeakers? }` —
     produced by .NET, consumed by Python.
3. **Transcribe.** The worker `XREADGROUP`s a job, downloads the blob from MinIO to a temp file, then runs
   **WhisperX (large-v3)** → **word-alignment** → **pyannote 3.1 diarization** (honouring optional
   min/max speaker hints) → optional **ECAPA per-speaker voiceprints** (SpeechBrain, 192-d, L2-normalised).
   It measures duration and rejects audio over `MAX_AUDIO_SECONDS`.
4. **Callback.** The worker `POST`s to **`internal/transcriptions/result`**, authenticated by the shared
   header **`X-Worker-Secret`** (= `CALLBACK_SECRET`), not JWT. Body (PascalCase) carries
   `{ TranscriptionId, Language, DurationMs, ProcessingMs, Segments[], Speakers[] }`, where each `Speaker`
   may include a 192-d `Embedding`. `ProcessingMs` is the worker's full-pipeline wall-clock time.
5. **Persist + identify.** The API writes the **`Segment`** rows (the worker's text lands in `Segment.Original`;
   a later user edit or translation goes in `Segment.Revised`, and the effective text shown/exported is
   `Revised ?? Original`), seeds a **`Speaker`** row per new
   diarization label (`DisplayName = label`), stores each speaker's embedding, backfills the recording's
   duration, records the transcription's **`ProcessingMs`** (surfaced in the detail subtitle and summed into
   the account-menu total), and runs **auto-identification**: for any speaker not manually named, it matches the embedding
   against the owner's enrolled **`SpeakerProfile`** voiceprints by **pgvector cosine distance** (≤
   `Identification:Threshold`) and, on a hit, sets `ProfileId` + `DisplayName` + `IdentifiedAuto = true` —
   never overriding a manual name, and **skipping any speaker the user flagged `IsMultiSpeaker`**
   ("Multiple Speakers" — overlapping/simultaneous speech, which is also never enrolled into a voiceprint).
   Individual segments can be deleted from a transcript (the survivors renumber); re-transcribe regenerates them.
   Deleting segments also **prunes any `Speaker` row whose label no longer appears** in a surviving segment, so
   deleting all of one speaker's segments (the per-speaker Delete in the Speakers panel) removes that speaker —
   and its stored voiceprint — from the recording.
   The web transcript panel adds a **Select mode** with bulk operations on the picked segments:
   `POST .../segments/delete { ids }` (delete the set, renumber once) and `POST .../segments/translate
   { ids, language? }` (translate just those, one batched LLM call); the panel itself pins to the top on scroll
   and scrolls its segments internally.
6. **Notify.** The API pushes **`RecordingStatusChanged`** over **SignalR** (`/hubs/transcription`) to the
   owner's per-user group; the browser refetches and the detail page shows the transcript.

**Re-transcribe** bumps the `Transcription.Version`; `GET /api/recordings/{id}` returns only the
highest-version transcription (plus its summary and the recording's actions). Speaker renames are preserved
across re-transcribes (the callback only seeds new labels). Embeddings refresh and auto-ID re-runs without
clobbering manual names.

## LLM-powered features (all via an OpenAI-compatible endpoint)

The same per-user-or-server LLM config (`UserSettings` ?? server `Summarization` defaults, resolved by
`SummarizationSettingsResolver`) powers four features. The API key is **encrypted at rest** (ASP.NET Data
Protection, keyring on the `DataProtection:KeysPath` volume) and is **write-only** over the API (`GET` returns
only `hasApiKey`). The resolved config also carries an optional **`reasoning_effort`** (`UserSettings.ReasoningEnabled`/
`ReasoningEffort` ?? server `Summarization:ReasoningEnabled`/`ReasoningEffort`); when reasoning is on, every LLM
client (summarise / actions / translation / chat) adds the field to its `/chat/completions` body, and when off the
field is **omitted entirely** so non-reasoning endpoints aren't broken. The **per-request timeout** resolves in three
steps - **`UserSettings.LlmTimeoutSeconds` ?? `PlatformSettings.LlmTimeoutSeconds` (default 120,
Platform-Admin-editable on Settings → Model Settings) ?? the server option** - via the same resolver
(`SummarizationSettingsResolver`; `EmbeddingSettingsResolver` mirrors the chain for the separate embeddings
config), and is enforced via a linked `CancellationTokenSource` in each client. Every LLM `HttpClient`
(`AddLlmClient` in `Program.cs`) is registered with `Timeout = InfiniteTimeSpan`, so the resolved value is the
**single authority** for every LLM call - chat replies, chat tool rounds, chat title generation, and Formula
runs included; without that, `HttpClient`'s own 100 s default silently capped them regardless of the configured
timeout, which is what happened before `IChatStreamClient` was added to `AddLlmClient` in `Program.cs`. The
streaming chat client (`ChatStreamClient`) now applies the resolved timeout itself, but as an **inactivity**
allowance rather than a cap on the whole call: a linked `CancellationTokenSource` is reset with
`CancelAfter(TimeoutSeconds)` after every line read from the SSE stream, so a reply that keeps producing
output can run indefinitely, and only a gap with no output at all trips it - throwing `ChatStreamException` so
`ChatController` surfaces a visible error frame instead of the stream just dying silently. The **request/header
phase** (DNS, connect, TLS, sending the request, waiting for the first response header) gets the same allowance
from its own linked CTS in `SendRawAsync`, because nothing else bounds it once `HttpClient.Timeout` is infinite
(`SocketsHttpHandler.ConnectTimeout` defaults to `Infinite`): an upstream that accepts the connection and never
answers would otherwise hang until the caller's token trips, which for browser chat and title generation is only
`RequestAborted`. So the allowance also covers **time-to-first-token**: a cold model that has to load first
produces nothing until it's ready, so the configured value must exceed the model's worst-case load time or a
cold start reads as a timeout. **A proxy in front still caps it**: the bundled nginx allows `1h` on `/api/`
(`proxy_read_timeout`), so a resolved timeout above 3600s is cut there, and an outer proxy needs the same
treatment.

**One context budget for every LLM call.** The resolved config also carries **`ContextCharBudget`** - the single
upper bound on injected context shared by *every* call site (recording summary, actions, tags, minutes, the folder
summary/minutes roll-ups, formula map **and** reduce, and the chat system prompt). `LlmContextBudget.CharsFor`
derives it from the effective **model context window** in tokens (`UserSettings.ChatContextWindow` ?? server
`Chat:ContextLength`, default 131,072) as `window x 60% x 4 chars/token`, with a 24,000-char floor. The 60% share
leaves the window's remainder for the instruction template, chat history and - since it is charged against the same
window - the model's own completion; the 4:1 char/token ratio matches `ChatContextMeter`, so the chat context dial
and the real truncation finally agree. Each site previously carried its own unrelated constant (24,000 chars for a
folder summary, 32,000 for folder minutes and formula reduces, 16,000 for the whole minutes context, 48,000 for
chat), none of which tracked the configured model: on the default window that fed a 131k-token endpoint roughly 6k
tokens of context, and because `FolderSummaryPrompt.JoinItems` drops **whole items** once its budget is spent,
large folders silently rolled up only their first ~18 meetings. The old per-worker options
(`SectionSummary`/`SectionMinutes`/`FormulaRun:CombineCharBudget`, `MeetingMinutes:TranscriptCharBudget`) are gone -
`Chat:ContextLength` (env `CHAT_CONTEXT_LENGTH`) is now the only knob, and it must be set to the model's real window.

- **Summarise (async).** `POST /api/recordings/{id}/summarize` sets status `Summarizing` and enqueues on a
  **second Redis stream `summarization-jobs`** (group `summarizers`). The API's **only stream consumer**,
  `SummarizationWorker` (a singleton `BackgroundService`), reads it, calls `/chat/completions`
  (`SummarizationClient`), writes a **`Summary`** (and an auto-generated `Recording.Name` when unset), and
  notifies over SignalR. It XACKs even on failure to avoid poison-message loops. A summary can also be
  **written/edited by hand** — `PUT /api/recordings/{id}/summary` (works with no LLM configured) sets
  `Summary.IsUserEdited`; the automatic summariser then **skips** that summary, and a user-initiated
  re-summarise clears the flag first (the UI warns before overwriting). Its instruction prompt is the
  **editable** `prompts/summarise.md` (see the editable-prompts note below; `{output_shape}` is substituted
  with the JSON contract, which stays machine-controlled).
- **Meeting minutes (async, template-driven).** A **third Redis stream `meeting-minutes-jobs`** (group
  `minute-takers`) with its own `MeetingMinutesWorker` (singleton `BackgroundService`) generates a formal,
  emailable **`MeetingMinutes`** (GitHub-flavoured Markdown) from the transcript, **chained after action
  extraction** so the minutes carry the **canonical extracted action set**. Minutes are driven by a
  **meeting type** (`MeetingType`), which is **presentation + selection only**: a title, group, icon, colour and
  a free-text `Overview` that frames the model. It carries **no prompts of its own** — it names the
  **`PrimaryFormulaId`**, the formula whose `TemplateContent` generates the minutes (H1/H2 **sections** whose
  blocks are **boilerplate text**, **substituted recording values** —
  `date`/`time`/`title`/`attendees`/`duration`, plus the two **table-valued** ones: `action_items` (the
  deterministic actions table) and `transcript` (the full Time/Speaker/Text table, `TranscriptFormatter.MarkdownTable`,
  shared with the Markdown export). Both render **bare** — the template supplies the heading — and the composer
  gives them a paragraph gap on both sides regardless of `BreakAfter`, since a glued table stops being one
  — or **model prompts**), plus any **`MeetingTypeFormulas`** run alongside in the same pipeline (their results
  land in the recording's Formulas tab). So minutes and formulas are the same thing, authored the same way; any
  accessible formula can be a primary. A recording's `MeetingTypeId` (null → the seeded **General Meeting**
  default) selects the type. Types are **Platform** (admin-owned, shared) or **Personal** (a user's own); the app
  seeds a standard set on startup (`MeetingTypeSeeder`, insert-if-missing by `Key`), each with the built-in
  `Diariz` formula that generates its minutes. **The shipped templates are markdown files** -
  `src/Diariz.Api/meeting-types/*.md`, loaded at boot by `MeetingTypeCatalog` and parsed by `TemplateMarkdown`
  (`#` = a section, `{{field}}` = a substituted value, `[[WRITE: …]]` = a model prompt, `---` = a rule) - the
  same authoring format as the built-in formulas (`formulas/*.md`, `BuiltInFormulaCatalog`). The words the model
  is given are content, not code: editable, mountable, and reviewable in a diff. If the directory is missing,
  `MeetingTypeSeeder.EmergencyGeneral` keeps the app producing usable minutes. A **Platform** type may reference only Platform/Diariz formulas —
  minutes generate as the *recording owner*, and a Personal formula can only be run by its owner, so pointing a
  shared type at one would produce no minutes for everyone else (refused at save). The
  `MeetingTypeMinutesGenerator` resolves the type and its primary formula, **assembles the context from that
  formula's `FormulaContext` flags** (`FormulaContextBuilder` - the same assembly a Formulas-tab run of the same
  formula uses, so the transcript arrives as `[mm:ss] Speaker: Text` and a minutes template can ask for the
  summary/actions too; the `Minutes` bit is ignored for a primary, which would ask the document to read itself),
  reads the platform-wide **generation mode**
  (`PlatformSettings.MinutesGenerationMode`, a Platform-Admin switch), and runs one of two
  `IMeetingTypeMinutesStrategy` implementations: **PerSection** (one LLM call per model-prompt block,
  bounded-parallel) or **SingleCall** (the whole template as one prompt/one call). The pure
  `MeetingTypeMinutesComposer` assembles the deterministic parts (headings, boilerplate, fields) with the model
  output; a shared guardrail preamble (`prompts/minutes-section-preamble.md`) prefixes every model prompt, and the
  transcript is attached as a **separate user (data) turn**. Applied + re-run via
  `POST /api/recordings/{id}/meeting-type {meetingTypeId}` (and the legacy re-run
  `POST /api/recordings/{id}/meeting-minutes/generate`); types are managed at `/api/meeting-types` (GET =
  Platform ∪ own; POST/PUT/DELETE gated so a Platform type needs a Platform Admin, a Personal type needs
  ownership). Minutes **do not own `Recording.Status`** (so they never
  race the summary's status transitions) — the processor notifies over SignalR to trigger a refetch. Minutes can
  be **hand-edited** (`PUT .../meeting-minutes`, sets `IsUserEdited`; auto-generator then skips) and **emailed on
  their own** (`POST .../meeting-minutes/email {includeAttachments}`) — the Markdown is rendered to HTML with
  **Markdig** and, when requested, the recording's file attachments are attached (`IEmailSender` gained an
  attachments parameter). Minutes also ride along in the emailed transcript and the md/txt/rtf downloads. The web
  edits them in a **WYSIWYG editor** (TipTap) that round-trips Markdown.
- **Extract actions (pipeline + on demand).** Action items are extracted **automatically as part of the
  pipeline**: a **fourth Redis stream `actions-jobs`** (group `actions-extractors`) with its own `ActionsWorker`
  (singleton `BackgroundService`) runs `ActionsProcessor` → `ActionsClient`/`ActionsPrompt`, enqueued **alongside
  the summary** after transcription (same effective per-user config gates both) and, when it finishes, **chains
  the minutes job** (so the minutes render the same set). The automatic
  pass **skips any recording whose `Recording.ActionsExtractedAt` is already set** (extraction ran, or the user
  added an action), so a re-transcribe never clobbers manual edits; like minutes it is **status-neutral** and
  notifies over SignalR. An explicit re-extract stays synchronous: `POST /api/recordings/{id}/actions/extract`
  calls the LLM inline and **replaces** the recording's **`RecordingAction`** rows. Actions also travel into
  transcript downloads, the emailed transcript, and the chat context. Its instruction prompt is the **editable**
  `prompts/extract-actions.md` (`{calendar_date}` substituted).
- **Tag cloud (pipeline + backfill) - tags are adopted, not auto-applied.** Every transcription still gets
  **weighted topic candidates** via a Redis stream **`tag-cloud-jobs`** (group `tag-extractors`) with its own
  `TagsWorker` (singleton `BackgroundService`) running `TagsProcessor` → `TagsClient`/`TagsPrompt` (editable
  prompt `prompts/tagcloud.md`; strict JSON array of `{tag, weight}` hardened for reasoning models via
  `ActionsPrompt.ExtractJsonArray`), enqueued alongside the summary/actions after transcription - but the LLM
  only ever **suggests**. `RecordingTag.Status` (`Suggested`/`Adopted`/`Dismissed`) separates a machine
  candidate from the user's own tag: `TagsProcessor` writes new candidates as `Suggested` and **replaces only
  the `Suggested` rows** on every (re)transcription, so an adopted tag or a dismissal survives a
  re-transcribe untouched - guarded, as before, against **stale jobs** (only the recording's latest
  transcription version may write) - and sets `Recording.TagsExtractedAt` even on a zero-tag result.
  Status-neutral; no LLM configured → silent no-op with the marker left null. Suggestion text is stored
  **normalised** (`TagText.Normalize`: internal whitespace collapsed to hyphens, case preserved, deduplicated
  by normalised form) rather than verbatim, so a suggestion and a hand-typed tag for the same concept compare
  equal. **Backfill:** a one-shot `TagBackfillService` enqueues jobs for every never-tagged recording at
  startup (gated on a server-wide summarisation endpoint), and a Platform Admin can trigger the same via
  `POST /api/platform/settings/run-tag-backfill` (Settings → Maintenance; returns the enqueued count —
  per-user-only LLM configs are covered this way) - backfilled recordings get suggestions, not adopted tags.
  Three endpoints on `RecordingsController` turn a suggestion into the user's own tag or dispose of it:
  `POST /api/recordings/{id}/tags` (adopt - typed by hand or promoted from a suggestion; idempotent; a
  promoted suggestion's stored text is rewritten to the normalised form so an adopted tag never keeps a
  space), `DELETE /api/recordings/{id}/tags?tag=x` (remove an adopted tag by deleting the row outright, so it
  does not reappear as a suggestion - only a fresh extraction can offer it again), and
  `POST /api/recordings/{id}/tags/dismiss` (reject a suggestion; the row becomes a `Dismissed` tombstone so
  the same word is not re-suggested on that recording; 404 when there is no such suggestion). An adopted tag
  always carries `Weight = 1.0`, so the cloud's summed weight equals a plain count of recordings carrying it.
  **These three are gated by `IRoomScope.AuthorizeRecordingPermissionAsync(..., RoomPermission.EditOthersRecordings)`**
  - the recording's owner may always tag it (ownership is its own grant, so a recording with no placement yet
  is still taggable by its recorder), and a non-owner needs `EditOthersRecordings` in a room the recording is
  placed in. Bare membership is deliberately NOT enough: these are writes on someone else's recording, and a
  member granted `RoomPermission.None` could otherwise delete every adopted tag on a colleague's meeting.
  Status codes follow the folder gates' NotFound-before-Forbidden order - 404 for someone who cannot see the
  recording at all, 403 for a member who can see it but lacks the flag. These are the first endpoints to
  enforce `EditOthersRecordings`, which until now was granted by default to new shared-room members and read
  by nothing else - `RecordingsController.Get` now also reads it: `RecordingDetailDto.CanEditTags` is computed
  with the same `AuthorizeRecordingPermissionAsync(..., RoomPermission.EditOthersRecordings)` call that gates
  the three write endpoints, so the flag and the gate cannot drift, and it defaults to `false` (fail closed).
  The web's `TagsPopover` reads `rec.canEditTags ?? false` and, when false, renders the recording's adopted
  tags as a read-only chip list - no entry field, remove buttons, or suggestions section - instead of letting
  a viewer who cannot write reach the same controls and hit a 403. Note that a member's tag does not land in
  THEIR cloud: `GET /api/tags` with no `roomId` scopes by
  `Recording.UserId`, so tagging a shared recording feeds the owner's personal cloud plus the room-scoped
  cloud (`?roomId=`) of any room it sits in. The web reads **`GET /api/tags`**: owner-scoped,
  case-insensitive aggregation over **`Adopted`** rows only (count + summed weight + carrying recording ids) -
  suggestions and dismissals never reach the cloud or search - that the left panel's **Tags tab** renders as a
  flat weighted cloud (log-scaled font sizes, single-select filter, an expanded 80% modal sharing the same
  selection state). `GET /api/recordings/{id}` returns both `tags` (adopted, in adoption order) and
  `suggestedTags` (suggestions, heaviest first); dismissed tags are never returned. A migration
  (`AddRecordingTagStatus`) demotes every pre-existing tag to a suggestion on upgrade, so the cloud and tag
  search start empty on an existing library and rebuild only as tags are adopted.
- **Folder (section) pages (async roll-ups).** A **folder page** (`GET /api/sections/{id}` +
  `SectionPageController`, web route `/sections/:id`) aggregates everything across a section **and every folder
  beneath it** (`SectionTree.SubtreeIdsAsync`), resolving placements by the folder's **own room**: stats, an LLM
  **folder summary**, consolidated **folder minutes**, and the actions/notes/
  attachments (read aggregations that carry each item's source-recording name; edit/delete reuse the
  per-item controllers). The two roll-ups generate asynchronously on **their own Redis streams** -
  **`section-summary-jobs`** (group `section-summarizers`, `SectionSummaryWorker`) and
  **`section-minutes-jobs`** (group `section-minute-takers`, `SectionMinutesWorker`), each a singleton
  `BackgroundService` running a static `SectionSummaryProcessor`/`SectionMinutesProcessor`. Each processor
  first **(re)generates and persists any missing per-recording** summary/minutes (reusing `ISummarizationClient`
  / `IMeetingTypeMinutesGenerator`), then combines them via the arbitrary-prompt `IMeetingMinutesClient`
  (editable prompts `prompts/folder-summary.md` / `prompts/folder-minutes.md`; minutes reshape through a chosen
  `MeetingType`). Results persist on the section (`SectionSummary`/`SectionMinutes`, 1:1, mirroring
  `Summary`/`MeetingMinutes`) with their own `Status/Error`, and a **`SectionStatusChanged`** SignalR event
  (distinct from `RecordingStatusChanged`) tells the folder page to refetch; hand-edits set `IsUserEdited` and
  survive the next regenerate.
- **Folder-direct attachments.** Besides the read-only aggregation of its transcripts' attachments, a folder
  can hold **its own** attachments (files or URLs) filed directly against the `Section` (`SectionAttachments`
  table + `SectionAttachmentsController` at `api/sections/{id}/folder-attachments`, blobs under
  `{uploaderUserId}/section-attachments/…`). Files open in a new tab via `/content` (the `access_token`
  query-auth allowance in `Program.cs` was extended to `/api/sections/…/content`). Access is gated by room
  permission, not by the folder owner: **read** (list, content) only requires the caller to be a **member** of
  the section's room; **write** (add/rename/edit-content/delete) additionally requires **`ManageContents`** -
  the same gate `SectionsController` uses for folder create/rename/delete, and the personal room's owner holds
  every permission so that path is unaffected. Each row is stamped with `UploadedByUserId` (the caller who
  created it) and `StorageUsage` counts a folder's file attachments toward **that** person's quota, not the
  folder's creator (`Section.UserId`) - in a shared room a `ManageContents` member can add to a folder someone
  else made, so the two can differ. The folder page's Attachments tab shows these as a second, **addable** list
  above the transcript-aggregated one, gated on `canManage` resolved from the folder's **actual** room
  (`SectionDetailDto.RoomId` looked up against the caller's room list) rather than the URL's room - the
  room-less legacy `/sections/{id}` deep-link would otherwise resolve permissions against the caller's personal
  room instead of the folder's real one. Open stays available to any member regardless.
- **Semantic search index (RAG, M3 - backend).** A **fifth Redis stream `embedding-jobs`** (group `embedders`)
  with its own `EmbeddingWorker` (singleton `BackgroundService`) builds the semantic-search index. Per job,
  `EmbeddingProcessor` windows the transcription's segments into overlapping passages (`TranscriptChunker`,
  ~1200 chars, 1-segment overlap), embeds them via `IEmbeddingClient` (OpenAI-compatible `/embeddings`, batched),
  and **replaces** the recording's `TranscriptChunk` rows (`vector(768)`) - so a re-transcribe never leaves stale
  chunks and retrieval needs no version filtering. Enqueued from the worker callback right after segments are
  saved (independent of summarisation), and an `EmbeddingBackfillService` indexes the existing library once on
  startup. Unlike the free per-user chat/summary endpoint, the embedding **model + dimension are server-pinned**
  (every chunk and query must match the `vector(768)` column); the **endpoint/key** are resolved per recording
  owner by `EmbeddingSettingsResolver` - a dedicated `Embedding` config block, else the owner's summarisation
  endpoint, else the server summarisation default. Chunks and queries carry the model's **task prefixes**
  (`search_document: ` / `search_query: `, config-driven; the nomic default retrieves better with them, and
  they're empty-able for models like OpenAI that don't use them). **Ships inert:** with no embeddings endpoint
  configured, nothing is enqueued and retrieval stays lexical (`pg_trgm`).
- **Hybrid retrieval (RAG, M3).** `TranscriptSearch.SearchAsync` runs two arms and fuses them: the **lexical**
  arm (pg_trgm word-similarity over segments, as before) and a **semantic** arm that embeds the query
  (`IEmbeddingClient`) and runs a pgvector cosine KNN (`<=>`) over the owner's `TranscriptChunk` embeddings,
  owner-scoped and optionally restricted to a recording scope. The two ranked lists are merged in C# by
  **Reciprocal Rank Fusion** (`SearchFusion`, keyed by `(RecordingId, StartMs)`) - so a passage matched by
  meaning but not by keywords still surfaces. Every existing search tool (and the MCP projection) gets this
  transparently. **Graceful-off:** no embeddings endpoint (or a speaker filter, or a failed query embedding) →
  the semantic arm is skipped → identical to the pre-M3 lexical behaviour. The chat system prompt is also given
  **today's date** so the model can resolve relative-date scoping ("last quarter"). Chat exposes this as an
  **"All meetings" context mode** (alongside Current / Selected / None): the request sets `SearchAllMeetings`,
  no transcripts are pre-loaded, and the system prompt tells the model to answer by searching the whole library
  and citing the meetings it draws from. Finer filters (dates, people, folders) are typed in plain language and
  resolved by the model - there are no filter widgets. **Milestone 3 (RAG) is shipped.**
- **Search as a REST endpoint (`GET /api/search`).** `SearchController` is the **second consumer** of
  `ITranscriptSearch`, alongside `Tools/*`. The distinction matters: the tools render **markdown** for a model to
  read (`ToolFormat`), whereas this returns **structured JSON** for the web left-nav's search bar -
  `SearchResponse(Query, Scope, Folders[], Recordings[])`, one hit per recording (best passage), each carrying the
  snippet, its `SnippetStartMs` (so the client deep-links to the moment via `?ts=`), and the folder breadcrumb.
  Snippets are **plain text** - highlighting is the client's job, so no markup is ever shipped.
  Params: `q` (required; empty → 400), `roomId`, `sectionId` (that folder **and its sub-folders**), `everywhere`
  (spans every room the caller can see and **wins over** the other two), `from`/`to`, `speaker`, `limit`
  (default 20, clamped to `TranscriptSearch.MaxLimit`).
  Two non-obvious rules: (1) a **folder scope resolves to recording ids** and an *empty* result **short-circuits** -
  `TranscriptSearch` reads an empty `recordingScope` as *unscoped*, so passing it through would search the whole
  library instead of an empty folder; (2) `SearchAsync` gained an optional `roomId` that **intersects**
  `RoomScope.RoomIdsForUserAsync` rather than replacing it, so a room the caller isn't in yields an empty array
  (`= ANY('{}')`) and the gate **fails closed** with no extra authorization check. Folder-name matching is plain
  EF in the controller (not the raw-SQL engine) so it translates on every provider and stays unit-testable.
- **Editable prompt templates.** The summarise, action-extraction, and meeting-minutes instruction prompts each
  live as a Markdown file under `prompts/` (`summarise.md` / `extract-actions.md` / `meeting-minutes.md`), read
  via a single `IPromptTemplateProvider` (`prompts/<name>.md`) **on each use** so edits (or a volume mount) apply
  without an API restart; each falls back to a built-in default (`*Prompt.DefaultTemplate`) if the file is
  missing/unreadable. The files ship in the published image (`Diariz.Api.csproj` copies `prompts/**`).
- **Action management (cross-meeting).** `ActionsController` exposes a library-wide view: `GET /api/actions`
  lists every action on the caller's recordings (joined to `Recordings` for ownership + display name, newest
  recording first), and `POST /api/actions/complete { ids, completed }` bulk-marks actions done/undone (sets/
  clears `RecordingAction.CompletedAt`; ignores ids the caller doesn't own). Both the new **Actions tab** in
  the left **Meetings** panel (filter by person, Select-mode multi-complete, Hide-completed, edit, link back to
  the source transcript) and the per-transcript Actions table's inline **Done** toggle drive this endpoint.
- **Translate (sync).** `POST /api/recordings/{id}/translate { language? }` translates the current
  transcript into a target language (the request's, else the caller's `NativeLanguage`; 400 if neither, or no
  endpoint) via `TranslationClient` → `TranslationPrompt`. It batches segment **Originals** by a char budget,
  writes each translation to the segment's **`Revised`** column (Original preserved), and translates the
  **Summary** + **Actions** text in place; speaker/actor names are kept. `POST .../segments/{segId}/translate`
  does one segment, and `POST .../segments/translate { ids, language? }` does a selected set in one batched call.
  The English language name is resolved from `SupportedLanguages`.
- **Chat (streaming).** `POST /api/chat/stream` builds a system prompt from the selected transcripts
  **plus their action items** and an optional uploaded attachment (`ChatContextBuilder`), then streams tokens
  back via **Server-Sent Events** (`ChatStreamClient`). The web infers the context from what's open rather
  than a manual pick (`lib/chatContext.ts`): the open recording, the open **folder**, the 2+ ticked
  recordings, or all/none — the pill label (Current Transcript / Current Folder / Selected Transcripts) is
  snapshotted on input focus. When **`SectionId`** is set the request is **folder chat**: `ChatController`
  builds the context from the folder's **roll-up summary + minutes + aggregated actions** (`ChatFolderContext`,
  across the section and its child sections) and scopes attachments + `scope:"current"` tools to the folder's
  recordings. With **`IncludeAttachments`** the in-context recordings' **attachments** are folded in too (for a
  folder, every attachment across it and its sub-folders): uploaded files
  are read into text by **`AttachmentExtractor`** (PDF, text, Office `.docx/.xlsx/.pptx`, email/calendar
  `.eml/.ics` — via PdfPig / Open XML SDK / MimeKit), and **URL** attachments are fetched by
  **`UrlFetcher`** behind **SSRF guards** (`UrlFetchGuard` — blocks loopback/private/link-local IPs and
  non-http(s) schemes), with a size cap, redirect re-validation, and HTML→text reduction. Conversations
  save to **`ChatSession`** rows (thread + context stored as `jsonb`, including a folder chat's `SectionId`
  so reopening it resumes the folder context), so the server stays stateless between turns — each request
  resends the full history and context.
- **Voice dictation (chat input).** The chat box's microphone button dictates a chat question by voice. In
  Chrome/Edge browser tabs it uses the browser's built-in **Web Speech API** entirely client-side (no server
  call). Elsewhere (the desktop app, Safari, Firefox) it falls back to `POST /api/chat/transcribe`: a
  **JWT-authenticated** endpoint that forwards one recorded audio utterance to an OpenAI-compatible
  `/audio/transcriptions` endpoint and returns the transcribed text - it **persists nothing** (no recording,
  no transcript row). This is a **server-level-only** config, separate from the per-user summarisation
  settings: a new `Dictation` options section (`ApiBase`/`ApiKey`/`Model`/`TimeoutSeconds`) is optional, and
  an empty `ApiBase` disables the server-fallback path (the browser Web Speech path still works in
  Chrome/Edge regardless). The endpoint returns **400** when no `Dictation:ApiBase` is configured and **502**
  when the configured speech-to-text service is unreachable.
- **Chat tool calling (built-in transcript tools).** When a user enables tools (master switch + per-tool list,
  resolved by **`ChatToolSettingsResolver`** — user override ?? server `Chat:ToolsEnabled` / `Chat:DisabledTools`),
  the chat turn runs as a bounded **agentic loop** (`ChatToolOrchestrator`, ≤5 rounds): it offers the enabled
  tools to the model, executes any **tool calls** server-side, re-injects their results as `role:"tool"` messages,
  and repeats until the model answers in text. The stream carries new `tool_start`/`tool_end` SSE events (the web
  shows a transient grey *"Tool call: …"* line). Tools are `IChatTool`s collected by `IChatToolRegistry`. The
  search-based ones (`who_said_that`, `what_did_they_say`, `search_transcripts`, `when_was_discussed`,
  `count_mentions`, plus `list_recordings`) query **`TranscriptSearch`**, a Postgres **`pg_trgm`** GIN-trigram
  fuzzy search over the user's own current-version transcripts (a `scope` arg lets the model search the whole
  library or just the selected recordings). **Passage-retrieval** tools cap results at `TranscriptSearch.MaxLimit`
  (**50**) and accept an optional `limit` arg (`ToolFormat.ReadLimit`/`LimitProperty`) so the model can ask for
  fewer/more up to the ceiling. **Counting/aggregation** tools are **exact and uncapped**: `count_mentions` uses
  `TranscriptSearch.CountMentionsAsync` (a grouped `COUNT(*)` over the same fuzzy match - a true total, not a
  capped "at least N"), and `speaker_talk_time` uses `SpeakerTalkTimeAsync` (a grouped `SUM` of segment durations
  over **all** in-scope recordings). `who_attended` likewise computes its distinct-people set over **all** matching
  recordings (the per-recording listing stays capped, with an honest "showing N of M" note) - previously these
  three silently used only the 20 most-recent recordings, skewing the answer. The EF-based ones (`list_action_items`,
  `get_recording_summary`, `who_attended`, `speaker_talk_time`, `get_segment_context`, and the single-recording read tools
  `get_transcript` / `get_meeting_minutes` / `get_recording_details`) read existing relational data directly. Two
  **write** tools act rather than read: `send_email` (`SendEmailTool`) emails the user a composed subject+body —
  it **always** sends to the owner's registered `ApplicationUser.Email` (no recipient parameter; any address in
  the args is ignored) via `IEmailSender`, and on a successful send it also **files a copy of the email onto the
  transcript** as a Markdown attachment (named `Email: <subject>`, body as content); and `add_as_attachment`
  (`AddAsAttachmentTool`) saves prepared content to a transcript as a Markdown attachment. Neither write tool
  touches storage directly — each queues an
  `AttachmentDraft` on a per-turn `ChatToolEffects` sink that the orchestrator drains into a **`ChatAttachmentDraftEvent`**
  (SSE `attachment` event carrying the name, Markdown, and candidate recordings). The web resolves the
  destination — one in-context transcript → POST it straight to `POST /api/recordings/{id}/attachments/markdown`;
  several → a picker modal, then the same endpoint (which stores a `.md`/`text/markdown` file blob, quota-enforced).
  Separately, a **client-side `/attach` slash command** (not an LLM tool) saves the **whole current conversation**
  as a Markdown attachment straight from the browser — onto the current transcript, the first selected transcript,
  or the current folder (folder-direct attachment), depending on chat context. Any **Markdown attachment is editable
  in place**: clicking Open on a `text/markdown` attachment opens the TipTap editor and Save overwrites the blob via
  `PUT …/attachments/{id}/content` (recording or folder route).
  Both write tools are on by default (safe: email only ever reaches the user; the note only their own transcripts);
  either can be disabled in Settings / `Chat:DisabledTools`. The chat **system prompt** also now names the current
  user (`FullName` +
  `Email`, via `ChatContextBuilder`) so the model knows who it is helping and writes emails as being from them.
  Each read tool's result embeds an in-app **markdown deep-link** (`/recordings/{id}?t={ms}`); the model cites it, and the web
  intercepts the click to open the transcript and **scroll/highlight the segment** at that moment
  (`lib/transcriptNav.ts`). The orchestrator also emits `ref` events (the recordings a tool referenced) so the
  web can **linkify plain mentions** the model didn't link (`lib/linkify.ts`); when an answer cites several
  moments in one recording the transcript shows a **Match k/n prev/next** control (a `?ts=` list). The chat
  **system prompt** grounds questions in the user's own meetings and (with tools) tells the model to search the
  transcripts before saying it doesn't know. With tools off, chat is the same single-pass stream as before.
  Tools run inside the API (no worker) — server-redeploy only.
- **Minutes are a formula run.** Once the minutes are saved, `MeetingMinutesProcessor` queues one `FormulaRunJob`
  per **additional formula** on the recording's meeting type (`MeetingTypeFormulas`, in `Ordinal` order) - *after*
  the minutes, so an additional formula may legitimately declare the `Minutes` context and read them. Each lands as
  an ordinary `FormulaResult` in the recording's Formulas tab. A disabled Platform/Diariz formula is skipped (only
  the **primary** is protected from being disabled), and one failing to queue never touches the minutes or the
  others.
- **A run replaces that formula's previous result** (`FormulaResultUpsert`), matched on `(RecordingId, FormulaId)` -
  reusing the row, keeping its `Id` and `Ordinal` so the list doesn't reshuffle and an open deep-link stays valid.
  Without this, a re-transcribed recording would accumulate a duplicate per regeneration. Because results are
  **hand-editable**, an **automatic** run (the pipeline) **skips** a result with `IsUserEdited` - exactly as the
  minutes refuse to overwrite hand-edited minutes - while an **explicit** run replaces it and clears the flag, as
  `ApplyMeetingType` does. There is deliberately **no unique index** on `(RecordingId, FormulaId)`: enforcing one
  would mean de-duplicating existing rows on upgrade, and those are real user documents.
- **Formulas (async run pipeline).** A **`Formula`** is a saved **template** (`ContentJson`, the same
  `TemplateContent` shape a meeting type's minutes template uses - sections of heading / boilerplate / field /
  prompt / hr blocks) + a chosen context (`FormulaContext`, a `[Flags]` combination of
  `Transcript`/`Notes`/`Attachments`/`Summary`/`Minutes`/`Actions`), run over a recording to produce a
  **`FormulaResult`** — a persisted Markdown document (`Name`, `Text`, `Ordinal` for display order). A run
  **composes** the template (`MeetingTypeMinutesComposer`, shared with the minutes pipeline, as is the field
  substitution in `TemplateFields`): headings and boilerplate are emitted verbatim, `field` blocks are
  substituted from the recording, and each `prompt` block is one LLM call (that prompt as the system message,
  the assembled context as the user message). A `field` is stamped into the **output only** and never enters a
  prompt, so the unbounded `transcript` field costs no tokens and is independent of `FormulaContext` (which
  governs only what the model reads); its segments are loaded lazily, only when the template asks for it. A formula that is *just* a prompt is stored as one **headless
  (`level: 0`) section holding one prompt block**, so it composes to exactly one call with that prompt and no
  heading around it - which is why making formulas structured changed no existing formula's output. A `Formula` has a **`FormulaScope`**: `Personal` (owned by one user, `OwnerUserId` set,
  always usable by its owner, cascade-deleted with the user), `Platform` (shared, admin-managed), or `Diariz`
  (shared, seeded built-ins, `IsBuiltIn = true`, can never be deleted). `Platform`/`Diariz` formulas carry an
  `Enabled` flag gating their availability. `FormulaResult.FormulaId` is **`ON DELETE SET NULL`** (a result
  survives its source formula being deleted) and `CreatedByUserId` is likewise nullable/`SET NULL` (a result
  survives its author's account being deleted); `FormulaResult` itself cascades with its `Recording`.
  **`Services/Seeder.SeedFormulasAsync`** insert-if-missing-by-name seeds four `Diariz`-scope starter formulas
  on startup: *Follow-up email*, *Meeting recap*, *Decisions & risks*, and *Tone & sentiment read* - loaded at
  boot from git-editable `src/Diariz.Api/formulas/*.md` (markdown + `name`/`description`/`context` frontmatter,
  parsed by `BuiltInFormulaCatalog`; still create-only by name, so admin edits survive), mirroring the editable
  `prompts/*.md` templates.
  Formula runs are **asynchronous**, mirroring the summarise/minutes pipeline: the run endpoint validates
  access + LLM config (via the shared `IFormulaRunner.ValidateRecordingRunAsync`), creates a `FormulaResult`
  in **`Status = Generating`**, enqueues a **`FormulaRunJob`** on the **`formula-run-jobs`** Redis stream
  (consumer group `formula-runners`), and returns **202** immediately. The in-process **`FormulaRunWorker`**
  (`BackgroundService`, one stream per kind) `XREADGROUP`s the job, opens a DI scope, and dispatches to the
  static **`FormulaRunProcessor`**, which resolves the caller's per-user-or-server LLM config via
  `ISummarizationSettingsResolver`, loads only the recording data the formula's context flags require (so a
  Summary-only formula never pulls the full segment list), builds a single context blob
  (`FormulaContextBuilder`, a pure formatter), streams one completion through **`IChatStreamClient`** inside a
  timeout derived from the resolved config, and flips the row to **`Ready`** (with `Text`) or **`Failed`**
  (with `Error`) - notifying the browser over SignalR (**`FormulaResultStatusChanged`**; the client also polls
  while any result is `Generating`). The **MCP/chat `run_formula` tool stays synchronous** - it targets a
  single recording and must return the result inside the tool call, so it shares the same
  context/LLM helpers (`FormulaRunProcessor.RunOverRecordingAsync`) but persists a `Ready` result inline via
  `IFormulaRunner.RunAsync`.
  Endpoints: **`FormulasController`** at `api/formulas` is Formula CRUD (`GET` = the caller's own Personal
  formulas ∪ every enabled Platform/Diariz formula; `POST`/`PUT`/`DELETE`/`PUT .../enabled`) plus
  **`POST api/recordings/{id}/formulas/{formulaId}/run`** (validates, creates a `Generating` `FormulaResult`,
  enqueues a `FormulaRunJob`, and returns **202** with the pending result); **`FormulaResultsController`** at
  `api/recordings/{id}/formula-results` covers listing (with `Status`/`Error`),
  reading, hand-editing, deleting, emailing (to the caller's own registered address, mirroring the meeting-minutes
  email), and downloading a result as a `.md` file. Write access to a `Personal` formula requires ownership (a
  non-owned Personal formula 404s rather than 403ing, so its existence isn't leaked); write access to a
  `Platform`/`Diariz` formula requires the new **`ManageFormulas`** platform permission (granted through a user
  group, alongside `ManageRooms`/`ManageUsers`); running any formula a user can see is always allowed. A web
  **Formulas tab** on the recording page runs formulas and lists/opens/edits/downloads/emails their results; a
  **Preferences → Formulas** panel manages Personal formulas. A **`run_formula`** chat tool
  (`src/Diariz.Api/Tools/RunFormulaTool.cs`) resolves a formula by name plus a recording (by id, name, or the
  in-chat selection, via the shared `RecordingArg` resolver), calls `IFormulaRunner`, and returns the Markdown
  result; it's a regular chat tool, so it's exposed over MCP automatically (it is not listed in
  `McpToolProjection.ExcludedToolNames`), letting Claude (Desktop/Code/the claude.ai web connector) run a
  user's formulas. There's also a deterministic **`/formula <name>`** client slash command in the chat panel
  that runs a formula on the open recording directly, without going through the LLM. Formulas is now complete:
  an admin **Manage Formulas** popup, opened from the account menu and gated on the `ManageFormulas`
  permission, lets an admin create/edit Platform-wide formulas and enable/disable or edit the Diariz built-ins
  without leaving the popup. It's backed by **`GET api/formulas/managed`** (every Platform/Diariz formula,
  enabled or not, ordered by scope then name - so the popup can see and toggle disabled shared formulas too)
  plus the existing CRUD/enable endpoints above; the profile now also exposes `ManageFormulas` via
  `PermissionsDto` so the web app can gate the account-menu entry point. The Formulas tab is a **two-panel
  view** (`FormulasPanel`): a resizable left runs-list (`FormulasManager`) beside a right panel that renders
  the selected result's Markdown read-only. Each `FormulaResultDto` now carries an **`Origin`**
  (`FormulaResultOriginDto`: `Kind` = `diariz`/`platform`/`personal` + optional person name/picture), resolved
  server-side and batched (no N+1) by **`Services/FormulaResultOrigins`** across the List/Update/Run endpoints;
  the web list shows the Diariz logo for Diariz/Platform formulas and the author's avatar for Personal ones (a
  result whose formula was deleted falls back to its creator).

  **Shared formulas.** A `Personal` formula can be marked **`Shared`** (a checkbox in its editor), making it
  discoverable platform-wide. Other users **subscribe** to it - a **`FormulaSubscription`** link row (a live
  pointer, not a copy), unique per `(FormulaId, UserId)`, that cascade-deletes with either the formula or the
  subscriber. A subscriber can **run** the shared formula (with their own LLM config) and sees it under a new
  **"Shared Formulas"** group in the run picker; the owner's edits propagate live, and un-sharing hides it from
  discovery/the run list and blocks running (existing links go inert, re-sharing restores them). Run access for
  a `Personal` formula is **owner OR (Shared AND the caller has a subscription)** - a shared but un-subscribed
  formula stays 404 on run (leak-avoidance; it's added via the browser, not run directly). Endpoints on
  `FormulasController`: **`GET api/formulas/shared`** (formulas shared by others, with the owner's name/avatar
  and whether the caller already added it), **`POST/DELETE api/formulas/{id}/subscribe`** (idempotent
  add/remove; 404 for a missing/non-shared/own formula); `GET api/formulas` also returns the caller's
  subscribed shared formulas. The web **`SharedFormulasBrowser`** modal (opened from a "Find shared formulas"
  button in the run picker) lists them with the sharer's avatar, a read-only prompt preview, and Add/Remove.

  **Folder (section) formulas.** The same formulas also run over a **folder and its sub-folders** to produce a
  **`SectionFormulaResult`** (mirrors `FormulaResult`, section-scoped). The run is a **map-reduce**:
  `FormulaRunProcessor.RunOverSectionAsync` resolves the folder's recording set **room-aware, across the whole
  subtree** (the section + every folder beneath it, via `SectionTree.SubtreeIdsAsync` and the `RoomRecordings`
  placement scoped to `section.RoomId` - the same
  resolution the folder read pages use), runs the formula on each included transcript's context (the "map",
  reusing `RunOverRecordingAsync`; empty meetings skipped, and the per-meeting outputs are ephemeral - never
  persisted), then composes the **same** template over the concatenated `## {meeting}` outputs (the
  "reduce", within the shared `ContextCharBudget`, mirroring `FolderSummaryPrompt`); a single included meeting short-circuits the
  reduce. It shares the Phase-1 async pipeline - the `FormulaRunJob` carries `SectionId` (with `RecordingId`
  null) and the `FormulaRunWorker`/`FormulaRunProcessor` flip the `SectionFormulaResult` row. Endpoints on
  **`SectionFormulaResultsController`** at `api/sections/{id}/...`: **`POST .../formulas/{formulaId}/run`**
  (validates section **membership** + formula run-access + LLM config, 400s a folder with no meetings, creates
  a `Generating` row + enqueues + returns 202) and **`.../formula-results`** listing/reading/editing/deleting/
  emailing/downloading. Run access = room **membership**; editing or deleting a folder result requires being its
  **creator** or a member with **`ManageContents`**. The web **Formulas tab on the folder page** reuses the same
  `FormulasToolbar`/`FormulasManager`/`FormulasPanel`/`FormulaRunModal` components with a section target.

## LLM usage logging

Every outbound call the platform makes to a model endpoint - summaries, minutes, tags, actions, embeddings,
chat, formula runs, translation, dictation, search - is captured to a new `LlmCalls` table, browsable by a
Platform Administrator at `/admin/llm-usage` (see "Admin usage viewer" below). **No prompt or completion
content is ever stored** - the table holds only counts, sizes, and identifiers, the same
content-out-of-telemetry rule `SentryScrubber` already enforces for Sentry/GlitchTip spans.

**The capture contract, end to end:**

1. **`LlmCallScope`** (`Services/LlmCallScope.cs`) is an `AsyncLocal<LlmCallScope?>` pushed once at the top
   of each user-facing operation (a summarize job, a chat turn, a formula run, and so on - seventeen call
   sites push a scope, spanning thirteen distinct `LlmCallKind` values; two of the seventeen both push
   `FormulaRun` - once in `FormulaRunner` for the synchronous chat/MCP tool path, once in
   `FormulaRunProcessor` for the enqueued job path - so an MCP-invoked `run_formula` call, which has no
   enclosing operation to inherit a scope from, is still attributed rather than falling through to
   `Unknown`). It carries the `LlmCallKind`, an `OperationId` (groups every call the operation
   makes), and the attributed user/recording/section. Everything called beneath the push - however many
   layers of client/service code deep - is attributed for free, without threading a context parameter
   through every LLM client interface. A call made with **no active scope** is still recorded, as
   `Kind = Unknown`, rather than silently dropped - an unattributed row is visible and fixable, a missing
   one is not.
2. **`LlmTelemetryHandler`** (`Services/LlmTelemetry.cs`), the `DelegatingHandler` already attached to every
   LLM client's typed `HttpClient` for Sentry span timing, reads `LlmCallScope.Active` on each call, times
   it, and builds an `LlmCall` row. A non-streaming (buffered JSON) response is measured and recorded the
   moment `SendAsync` returns, with `usage` parsed straight out of the buffered body. A **streaming** (SSE)
   response is different: the client reads it with `ResponseHeadersRead`, so the call is nowhere near over
   when `SendAsync` returns - it has barely started. The handler wraps the response's content stream in an
   **`ObservingStream`** (`Services/ObservingStream.cs`), a read-only pass-through that forwards every byte
   to the real caller (`ChatStreamClient` et al.) unbuffered - it never delays or reorders a chunk, so the
   reply keeps streaming to the browser exactly as before - while feeding a copy of each chunk to an
   **`SseUsageScanner`** (`Services/SseUsageScanner.cs`) looking for the trailing `usage` event. The
   `LlmCall` row is completed only when the stream actually ends: cleanly at end-of-stream, or when a read
   faults - including a cancelled read - mid-stream, which **is** recorded as a failure even though the
   response started with a 200. A client that stops reading at `[DONE]` and disposes cleanly, with no fault,
   is not counted as a failure - but that is the normally-completed case, not an abandoned one: a closed tab
   or a Stop button cancels the in-flight read, which surfaces as an `OperationCanceledException` and is
   attributed as a fault. Cancellation and a per-call inactivity timeout share the same `CancellationToken`
   and are not distinguishable at this layer, so both classify as `ErrorKind = "Timeout"`. The wrapper also
   stamps **time to first token** - the elapsed time from
   `SendAsync` to the first byte observed on the stream - alongside the true end-to-end duration, both far
   more meaningful for a streamed call than the old time-to-headers figure. Because the handler already
   wrapped every client, a new LLM client added later is logged automatically with no call-site change. A
   telemetry failure here can never break the call it measures - every read is best-effort, every observer
   callback is individually guarded, and the final hand-off is wrapped in a swallowed try/catch.
3. **`ILlmUsageSink`** / **`ChannelLlmUsageSink`** (`Services/LlmUsageSink.cs`) is where the handler hands the
   row off - a bounded (`Capacity = 10_000`) in-memory `Channel<LlmCall>`, `TryWrite` (never blocks the call
   path). Full is `DropOldest`; a sustained burst past capacity drops the oldest buffered rows rather than
   stalling an LLM call, and records still buffered during a hard crash are lost. Both trade-offs are
   accepted deliberately: a monitoring feature must never become an availability risk for transcription or
   chat.
4. **`LlmUsageWriter`** (`Services/LlmUsageWriter.cs`), a singleton `BackgroundService`, drains the channel -
   coalescing on a real flush timer (up to ~2s or 200 rows, whichever comes first) so steady traffic batches
   instead of paying a DI-scope + settings-query + `SaveChanges` round trip per record - and persists each
   batch. It opens its **own** DI scope per batch (the handler cannot hold a `DbContext`: it is transient but
   `HttpClientFactory` pools handler instances for ~2 minutes, so an injected scoped dependency would be
   captive and used after disposal). The **`LlmUsageLoggingEnabled`** master switch is enforced here, in
   `LlmUsageBatch.PersistAsync`, not in the handler - deliberately, so the LLM call path never pays for a
   settings lookup even when logging is off; a batch drained while the switch is off is simply discarded. A
   failed persist backs off 5s before the next iteration (so a database outage cannot spin the loop) and
   never takes the writer down.
5. **`LlmCalls`** (Postgres) is the resting place - see `Data_Schema.md` for every column, its five indexes,
   and its three `ON DELETE SET NULL` foreign keys (each paired with a denormalized snapshot column so a row
   stays readable after its subject is deleted).

**Retention.** A nightly `LlmUsageRetentionWorker` (a singleton `BackgroundService`, mirroring
`AudioRetentionWorker`'s schedule helper and server-local run time) deletes `LlmCalls` rows older than
`PlatformSettings.LlmUsageRetentionDays` via a set-based `ExecuteDeleteAsync` (`LlmUsageRetentionSweep`) -
`0` means keep forever, guarded explicitly so an admin typing `0` cannot be misread as "delete everything
older than now" on the first sweep.

**Admin settings (Model Settings tab).** Three new `PlatformSettings` fields, all admin-editable: a master
on/off switch for the log (`LlmUsageLoggingEnabled`, default **true** - the log is the feature), the
retention window in days (`LlmUsageRetentionDays`, default 90, `0` = forever), and whether a streaming
request should ask the endpoint for token counts via `stream_options.include_usage`
(`LlmStreamUsageEnabled`, default true). `ChatStreamClient` adds that field to the request body only when
the setting is on. It stays a toggle rather than a constant specifically so a platform administrator whose
endpoint rejects the unrecognised field can turn it off from Model Settings and recover immediately, with
no redeploy required.

**Streaming token counts and duration.** Chat replies and formula runs (both driven by `ChatStreamClient`)
now report real token counts, a true end-to-end duration, and a time to first token, closing the gap the
previous release left open. A streamed call used to be recorded at `SendAsync`, which only ever measured
time to the first response header - a few tens of milliseconds - not the time the model actually spent
generating. It is now recorded when the stream ends (see the capture contract above), with `PromptTokens`/
`CompletionTokens`/`ReasoningTokens`/`TotalTokens` parsed from the trailing `usage` chunk `stream_options`
asked the endpoint to send, and `TimeToFirstTokenMs` populated for the first time.

**Admin usage viewer (`/admin/llm-usage`).** The first user-visible surface over `LlmCalls` - a Platform
Administrator page, linked from the Model Settings tab, over four endpoints on **`LlmUsageController`**
(`api/admin/llm-usage`), every one gated by **`[Authorize(Policy = "ManagePlatform")]`** (not the weaker
`ReadAdminSettings` Administrators also hold, because this log carries every user's activity across the
whole platform, not just platform configuration):
- **`GET api/admin/llm-usage`** - `mode=operations` (default) collapses every call belonging to one
  operation (same `OperationId`, `Kind`, user, recording/section, and - conventionally, not enforced -
  `Model`) into a single row with a turn count; `mode=calls` returns one row per individual `LlmCall`.
  Both accept the shared filter (date range, `userIds`, `kinds`, `models`, `outcome`, `recordingId`,
  `sectionId`), a whitelisted `sort`/`desc`, and `page`/`pageSize` (capped at 200), and both return a
  `total` and a `totals` block computed over the *whole filtered set* - never just the returned page -
  from the same `LlmUsageQuery.TotalsAsync` the other three endpoints reuse, so no two views of the data
  can disagree about what is in scope. Each token total is paired with how many of the calls in scope
  actually reported that figure (a nullable `SUM`/measured-count pair, not a bare sum), so a partial
  measurement is never presented as a complete one. A request whose filter matches more than 25,000
  operations is rejected with 400 rather than silently truncated or risking a large in-memory
  materialization.
- **`GET api/admin/llm-usage/summary`** - rolls the same filtered set up by a required, comma-separated
  `groupBy` of `user`, `model`, and/or `kind`; each group's tokens-per-second is that group's own
  `SUM(completion)/SUM(duration)`, never an average of its rows or the overall rate, and `turns` is
  reported per operation as an average and a maximum, never summed across operations.
- **`DELETE api/admin/llm-usage`** - the most destructive endpoint in the feature: permanently removes
  every `LlmCalls` row matching the same shared filter (defaulting to the same 30-day window, so an
  unfiltered request can never silently mean "delete everything") via a set-based `ExecuteDeleteAsync`
  (rows are never materialized into API process memory), capped at 25,000 rows per request, and returns
  the count actually removed. The web page confirms with that exact count before calling it.
- **`GET api/admin/llm-usage/filters`** - lists the distinct users/models/kinds actually present in the
  scoped set (same date range, no other filter - populating a dropdown from an already-filtered dropdown
  would be circular), to drive the filter bar's dropdowns.

The web page (`apps/web/src/pages/LlmUsage.tsx`) presents this as three views - Operations, Calls, and
Summary - sharing one filter bar (default window: the last 7 days - a UI-chosen default, distinct from
`LlmUsageQuery.Apply`'s own 30-day fallback for a caller that sends no `from` at all, since the page always
sends an explicit preset), a totals row pinned to the bottom of the table, sortable column headers issuing a
fresh server-side request per click, and a filtered-delete button whose native confirm dialog states the
exact row count before anything is removed.

## Meeting notes (the user's own notes)

Users can jot their **own note lines** for a meeting - sparse trigger phrases, questions, observations - as
a first-class entity (**`MeetingNote`**, row per line). A line is anchored to **either a recording or an
upcoming Google Calendar event**: prep notes are taken on the calendar-event preview page
(`/calendar-event/:id`, CRUD at `/api/calendar/events/{calendarId}/{eventId}/notes`) and are **adopted onto
the recording automatically** when its calendar link forms (`MeetingNoteAdoption`, called inside the
`LinkCalendar` chokepoint that both the auto-match save and manual linking use - one-way and additive).
Recording-anchored lines live on the detail page's **Notes section** (CRUD at `/api/recordings/{id}/notes`;
`GET` is gated on `IRoomScope.CanReadRecordingAsync` - the owner, or a member of a room the recording is placed
in - while create/update/delete stay owner-only, so a room co-viewer reads a recording's notes but cannot
change them);
lines can carry a **`CapturedAtMs`** recording-clock timestamp (immutable; stamped lines deep-link to that
moment in the transcript via the existing `?t=` navigation). **Transcript weave:** a stamped note is rendered
inline in the **Transcript tab** right after the segment being spoken when it was written (pure
`lib/transcriptNotes.ts` `weaveTranscript`; anchor = greatest `StartMs ≤ CapturedAtMs`) as its own green row
with the current user as the "speaker"; the same anchor rule (server-side `TranscriptNoteAnchor`) makes the
**merge-segments** action treat a note as a boundary, so `SegmentMerger` won't collapse same-speaker text from
either side of a note (a `BreakBefore` flag on the segment after each note's anchor). **Live capture:** while recording, the **notes popover** in the command hub
auto-opens (dismissable; preference in localStorage) - each
Enter-committed line is stamped with the current *recorded* time (`recorderTiming`, pause-aware), mirrored
to IndexedDB (`lib/pendingNotes.ts`, its own `diariz-notes` DB, keyed by user) so a crash never loses lines,
and bulk-attached to the recording right after upload; an attach failure keeps the lines durable (with the
recording id) behind a retry banner, and recovered pending recordings adopt their stashed lines on
re-upload. **Pop-out window (desktop shell only):** that popover can be detached into a small always-on-top
`BrowserWindow` at `{origin}/notes-popout`, so notes stay readable over a full-screen call on a single
monitor - see the pop-out contract under *Cross-boundary contracts*. **The minutes weave:** notes feed minutes generation two ways (`MeetingMinutesProcessor` loads
them; `IMeetingTypeMinutesGenerator` takes them alongside actions). (1) **Steering** - when notes exist, a
"NOTE-TAKER'S EMPHASIS" block listing the lines rides the shared section preamble, so **every**
prompt-driven template section weights them (both SingleCall and PerSection inherit it; no notes → prompts
byte-identical). (2) **Enhanced notes section** - templates may use a **`notes` field** (peer of
`action_items`; in the editor's field picker; the seeded General template gains an "Enhanced notes" section
via a conservative upgrade that only applies when the admin never edited it). When present, a **pre-pass**
runs: `NotesEnhancer` (one `IMeetingMinutesClient` call, strict JSON with parser repair) expands each note
line from the transcript, then `NotesComposer` renders **deterministically with provenance** - the user's
literal words bold (never paraphrased), capture stamps italic, `[mm:ss](/recordings/{id}?t=ms)` deep-links
per supporting moment, and lines the transcript doesn't support kept and marked *not discussed in the
recording*. Failure posture: an enhancer failure renders the raw stamped lines and the minutes still
generate; a `notes` field with no notes renders "No notes were taken for this meeting." Design:
`docs/superpowers/specs/2026-07-07-enhanced-notes-design.md`.

## Meeting screenshots (desktop capture)

The **Windows desktop app** can capture the screen while a recording is running (**`MeetingScreenshot`**, a
row per capture; entity in `src/Diariz.Domain/Entities/MeetingScreenshot.cs`, migration
`AddMeetingScreenshots`). A capture is triggered from a **configurable global hotkey**, the tray menu, or a
button in the web app itself - all three funnel through the same main-process `captureScreenshot()`, which
is a no-op unless a recording is actually running (`canCapture(recorder)`).

**Capture-area contract: main owns the capture and the area, the renderer owns the pause-aware clock.**
`apps/desktop/src/main.js` picks the target and grabs the pixels - it has no idea what time it is in the
meeting. On the **first** capture of a recording it opens a full-screen overlay per display
(`picker.html`/`picker-preload.js`) so the user chooses **a whole monitor or a dragged rectangle**; that
target (`{ displayId, selection }`) is cached in memory and reused for every later capture in that
recording, and is cleared on every transition into "recording" so a stale rectangle from a previous monitor
layout can never silently capture the wrong thing. A tray/recorder "Change capture area" action
(`screenshot:change-area`) forgets the cached target and reopens the picker mid-meeting.

**The overlays are pre-warmed, not built per pick.** Building them on demand measured at **400-750ms** on a
three-display machine (each overlay is its own sandboxed renderer and must paint before `ready-to-show`
allows showing it) - half a second in which nothing is on screen while the app window still takes input, so
the button read as dead, and an impatient second click was either swallowed by the re-surface path or landed
on the overlay as it appeared, where a sub-6px press-release means "whole screen". So main keeps a **pool**
of hidden, already-painted overlays for exactly as long as `canCapture(recorder)` holds (built when a
recording starts, destroyed when it ends), and a pick is just `show()` - **~40ms**. `pickerPool.js` holds the
pure reconciliation (which displays need an overlay built, dropped, or re-fitted); main re-runs it on
`display-added`/`display-removed`/`display-metrics-changed` so a monitor plugged in or re-resolutioned
mid-meeting can't leave a screen unpickable or an overlay over stale geometry. Because an overlay now
outlives a single pick, main sends it **`picker:reset`** when putting it away - without that, its one-shot
choose/cancel guard would make every pick after the first inert.
**Every capture affordance hangs off `canCapture(recorder)`, and that needs the renderer to be *ready* -
which only ends with the document.** `recorder.ready` means "a document is loaded whose `Recorder` has
mounted and reported in"; it gates tray-driven recording and, through `canCapture`, the hotkey, the tray
items, the overlay pool and both capture buttons. Main clears it only on the events listed in
`apps/desktop/src/rendererReadiness.js` - **`did-navigate`** (a new document committed) and
**`render-process-gone`**. It used to clear it on `did-start-loading`, which Chromium raises for
**same-document** navigation too (react-router's `pushState`), as well as for subframe loads and for
off-origin navigations main itself aborts in `will-navigate`. So one in-app navigation mid-recording marked
the still-mounted recorder as gone, and since the web app reports readiness only when it mounts, nothing
ever set it back: for the rest of that take the hotkey was unregistered, the overlay pool torn down, the
tray items absent and "Change capture area" a silent no-op that still highlighted on hover.

**Whether an area is set is mirrored to the renderer** (`screenshot:has-area` for the starting value,
`screenshot:area-changed` for every later change - every write goes through main's `setCaptureTarget`), and
the web app **disables its capture buttons until there is one**: capturing with no area opens the picker,
and while that picker is waiting both capture controls no-op behind the in-flight guard, which reads as the
notes popover having frozen. Setting the area is therefore the visible first step. Defence in depth for the
same trap: a UI capture or change-area click while a picker is *already* open now **re-surfaces that
overlay** (`show` + focus on the cursor's display) instead of silently returning its pending promise, so an
overlay that slipped behind another window can always be reached. The hotkey path deliberately does not
re-surface - it auto-repeats at ~30Hz while held and would fight the user's drag. An **older shell** without
the bridge reports "area set" so it keeps its original pick-on-first-capture behaviour against a newer web
build. The grabbed image
is resized so its **long edge is capped at 2560px** (`MAX_LONG_EDGE`) and a **320px-long-edge JPEG
thumbnail** is derived from it, then both are pushed to the renderer as raw bytes - main never touches the
recording clock or uploads anything. The **renderer** (`Recorder.tsx`) is the only side that knows about
pauses: it stamps each arriving capture with the current *recorded* time via the same pause-aware
`recorderTiming` helper `MeetingNote` lines use, mirrors it to IndexedDB (`lib/pendingScreenshots.ts`) so a
crash never loses an unattached capture, and uploads it to `POST /api/recordings/{id}/screenshots` once the
recording row exists (an attach failure keeps the capture durable behind a retry banner, exactly like the
notes stash).

**IPC channels** (main ⇄ renderer/picker/hotkey windows, `contextBridge`-exposed, never raw `ipcRenderer` in
the web app):

| Channel | Direction | Payload |
|---|---|---|
| `screenshot:capture` | renderer → main (invoke) | none - captures now; opens the picker if this recording has no target yet, or re-surfaces one already waiting |
| `screenshot:change-area` | renderer → main (invoke) | none - forgets the cached target and reopens the picker |
| `screenshot:has-area` | renderer → main (invoke) | none → `boolean`: whether this recording has a capture area yet (the renderer's starting value) |
| `screenshot:area-changed` | main → renderer (event) | `boolean` - the area was chosen (`true`) or cleared (`false`: new recording, re-pick, or a display that went away) |
| `screenshot:captured` | main → renderer (event) | `{ full, thumb, width, height }` (PNG/JPEG bytes as `Uint8Array`) |
| `picker:choose` | picker window → main (send) | the user's selection (`{ displayId, selection }` or a whole-monitor pick) |
| `picker:cancel` | picker window → main (send) | none - the overlay was dismissed (Escape) without a choice |
| `picker:reset` | main → picker window (event) | none - the overlay has been put away; clear its one-shot choose/cancel guard so the pooled window can serve the next pick |
| `hotkey:load` | hotkey window → main (invoke) | none → returns the stored accelerator (or the default) |
| `hotkey:save` | hotkey window → main (invoke) | a candidate accelerator → `{ ok, error? }`; only saves one that is both well-formed and provably registrable |

**Endpoints** (`ScreenshotsController`, `api/recordings/{recordingId}/screenshots`). Read routes (list/content/
thumb) are gated on `IRoomScope.CanReadRecordingAsync` - the owner, or a member of a room the recording is
placed in; create/delete stay owner-only (`Recording.UserId == caller`):

- `GET /` - list a recording's captures, ordered by `CapturedAtMs`. **Read gate.**
- `POST /` - store one capture: multipart `full` (PNG) + `thumb` (JPEG) + `capturedAtMs`/`width`/`height`;
  enforces a combined-size cap (`Screenshots:MaxBytes`, default 20 MB) and the owner's storage quota before
  writing either blob. **Owner-only.**
- `GET /{screenshotId}/content` / `GET /{screenshotId}/thumb` - stream the full image / thumbnail. **Read gate.**
- `DELETE /{screenshotId}` - deletes both blobs before the row, then the row. **Owner-only.**

**`?access_token=` image URLs.** An `<img>` tag cannot set an Authorization header, so
`api.screenshotContentUrl`/`screenshotThumbUrl` append the bearer as an `access_token` query parameter,
exactly the same mechanism already used for the audio stream, attachment content, and SignalR's WS
handshake (`Program.cs`'s `OnMessageReceived` allow-lists the `/content`/`/thumb` suffixes under
`/api/recordings` alongside `/audio`).

Screenshot images count toward the owner's storage quota (`StorageUsage` sums `MeetingScreenshot.SizeBytes`
alongside recordings and attachments). Deleting a recording deletes its screenshot blobs explicitly (the DB
cascade only clears the rows). **Merge is asymmetric with attachments:** attachments are reassigned onto the
survivor, but screenshots are not - a merged-away source's screenshot blobs are freed (both the synchronous
no-audio path in `RecordingsController` and the async `WorkerMergeCallbackController` path) rather than
carried forward, since a screenshot's captured-time offset is meaningless once its source recording's audio
has been spliced into a different timeline.

**Transcript weave.** A screenshot anchors into the transcript the same way a stamped `MeetingNote` does
(`weaveTranscript`, greatest `StartMs ≤ CapturedAtMs`) and participates in the same same-speaker merge-break
rule: `RecordingsController` unions note and screenshot capture times before calling
`TranscriptNoteAnchor.BreakBeforeIndices`, so a screenshot between two same-speaker turns stops `SegmentMerger`
from collapsing them past it, exactly like a note does.

## MCP server (connect Claude to transcripts)

Diariz hosts a **Model Context Protocol server in-process** (the official `ModelContextProtocol.AspNetCore`
SDK) at **`/mcp`** (Streamable HTTP, **stateless** — no server-initiated messages), so a user can connect
**Claude** (Desktop or Code) directly to *their own* transcripts. It is **not a new deployable** — it runs in
the API and ships with a **server redeploy**. The server advertises its identity in the `initialize` handshake
(`ServerInfo.Name`/`Title`/`Description`/`WebsiteUrl`/`Icons` + `ServerInstructions`), so connector clients show
Diariz's logo, name, description and website - and the model gets usage guidance - not just the URL. The icon
is the web app's `/logo.png` (built from `App:PublicUrl`; omitted when that origin isn't set).

> **Reverse-proxy requirement.** `/mcp` must be forwarded to the API like `/api` and `/hubs`. The web image's
> nginx (`apps/web/nginx.conf`) proxies it with **`proxy_buffering off`** (Streamable HTTP streams responses as
> `text/event-stream`, so buffering would stall the stream) and a long read timeout. **Any outer reverse proxy
> in front of the web container must also forward `/mcp` with response buffering disabled** — otherwise the SPA
> is served for `/mcp` (or a POST returns 405) and clients report "cannot load the MCP server". **It must also
> allow a read timeout at least as long as the configured LLM timeout** (`PlatformSettings.LlmTimeoutSeconds`,
> now overridable per user) - a run_formula/chat-shaped tool call that outlives the outer proxy's own read
> timeout dies there regardless of what the app is configured to allow. The OAuth server
> adds the same requirement for **`/connect/`** (authorize/token/register) and **`/.well-known/`** (discovery +
> protected-resource metadata): both nginx and any outer proxy must forward them to the API, or an OAuth client
> gets the SPA index.html instead of the metadata and the claude.ai connection never starts. (`/oauth/consent`
> is deliberately a **SPA** route and must NOT be proxied.) **The `X-Forwarded-Proto` header must carry `https`**
> all the way to the API - OpenIddict rejects its own endpoints as non-HTTPS otherwise (`ID2083`). The web
> nginx forwards the outer proxy's incoming `X-Forwarded-Proto` (falling back to its own `$scheme`) rather than
> clobbering it, so **the outer proxy must set `X-Forwarded-Proto: https`** (most do by default).

- **Per-user token auth, gated by a platform toggle.** The endpoint is guarded by a dedicated auth scheme
  (`McpBearerAuthenticationHandler`, scheme `"Mcp"`), separate from the browser JWT, and fails closed while
  `PlatformSettings.McpAccessEnabled` is off (a Platform Admin toggle on **Settings → Integration**, **on by
  default** so shipping the toggle never disables an already-connected client). A user generates a personal
  access token in **Preferences →
  Claude / MCP access** (`McpTokensController`, `/api/user/mcp-tokens` GET/POST/DELETE, JWT-authed). Tokens are
  `dz_mcp_` + base64url(32 random bytes); **only their SHA-256 hash is stored** (`McpAccessToken`), shown to the
  user **once** at generation. On each `/mcp` request the presented bearer is hashed and looked up
  (`McpTokenAuthenticator`), the owner's `NameIdentifier` claim is set (so every query stays owner-scoped like
  the rest of the API), and `LastUsedAt` is recorded. Multiple named tokens per user; revoke = delete the row.
- **Tools = the chat tool registry.** Low-level handlers (`DiarizMcpHandlers`) project the same
  `IChatTool`/`IChatToolRegistry` used by chat onto MCP `tools/list` + `tools/call` — no duplicate logic, and a
  new `IChatTool` lights up in both chat and MCP. The catalog is the user's **per-tool-enabled** tools
  (respecting the per-tool choices on Preferences → Assistant, but **not** the chat *master* switch — the MCP opt-in is holding a
  token), **minus `add_as_attachment`** (which needs an in-chat selection). `send_email` is included (it can only
  ever email the user's own address). Each tool carries an MCP **`readOnlyHint` annotation** (from
  `IChatTool.ReadOnly` — true for every read/search tool, **false for the two write tools, `send_email` and
  `run_formula`**) plus
  `destructiveHint=false`, so clients can group read-only vs write tools (`McpToolProjection.Annotations`). Tool
  results' in-app deep-links are rewritten to **absolute** URLs (`McpLinkRewriter`, against `App:PublicUrl` or the
  request origin) so they're clickable in Claude.
- **Config.** `Mcp:Enabled` (default true) mounts the endpoint. `IHttpContextAccessor` provides the request's
  user + scoped services inside the handlers. **Claude Code:** `claude mcp add --transport http diariz {origin}/mcp
  --header "Authorization: Bearer dz_mcp_…"` (or the `headers` block in `.mcp.json`). **Claude Desktop** only
  accepts stdio servers in `claude_desktop_config.json`, so it connects via the **`mcp-remote`** bridge
  (`command: npx -y mcp-remote {origin}/mcp --header "Authorization:${AUTH}"`, token in `env.AUTH="Bearer …"` — the
  env indirection avoids mcp-remote splitting the header on its space). Preferences → *Integrations* (the MCP access card) shows
  this ready-to-paste. (Older/newer Desktop builds may also accept a `type:http` entry, but mcp-remote is the
  portable path.)
- **Resources.** `ListResourcesHandler`/`ReadResourceHandler` expose each of the user's recordings as MCP
  resources — `diariz://recording/{id}/transcript` (and `.../minutes` when minutes exist) — so a user can
  **@-mention a specific meeting** in Claude. Backed by `IMcpResourceService` (owner-scoped, current-version
  only, newest-first capped list); transcripts render as plain Markdown via `McpResources.TranscriptText`, minutes
  are the stored Markdown.
- **Prompts.** `ListPromptsHandler`/`GetPromptHandler` expose slash-command-style starters (the pure
  `McpPrompts` catalog): `summarise_last_meeting`, `open_action_items`, and `find_discussion(topic)`. Each
  expands into a ready-made user message that instructs the model to answer from the user's meetings via the
  built-in tools (no server LLM call — prompts are just message templates). This completes the MCP surface:
  **tools + resources + prompts** are all live.
- **OAuth 2.1 sign-in (for the claude.ai web connector) — foundation.** The static `dz_mcp_` token works for
  Desktop/Code (which can set a bearer header) but **not** for the claude.ai **web** "Custom Connector", which
  can only connect via an OAuth handshake. So the API is being made a spec-compliant **OAuth 2.1 authorization
  server** (built on **OpenIddict 7.x**, EF Core stores on `DiarizDbContext` — see `Data_Schema.md`
  `OpenIddict*` tables). Wired so far (`OpenIddictSetup.AddDiarizMcpOAuth`): authorization + token endpoints,
  **authorization-code flow with mandatory PKCE (S256)** + refresh tokens, an `mcp` scope bound to the
  `diariz-mcp` resource/audience, discovery metadata at `/.well-known/openid-configuration`, and **persistent
  signing/encryption certificates** on the `/keys` volume (`OpenIddictKeys`, so tokens survive a redeploy;
  ephemeral in dev). **Dynamic Client Registration** (RFC 7591) is hand-rolled at **`POST /connect/register`**
  (`OAuthRegistrationController`) because OpenIddict 7.x has no native DCR endpoint — a client is registered as a
  **public, PKCE-only** authorization-code client, gated by `RedirectUriPolicy`: every `redirect_uri` host must
  be on the `McpOAuth:AllowedRedirectHosts` allowlist (default `claude.ai`/`claude.com` + loopback for
  Desktop/Code), so a client can never be registered to redirect an authorization code to an attacker's site.
  The interactive **authorize + consent** flow is built: `GET/POST /connect/authorize`
  (`OAuthAuthorizeController`, passthrough) reads a short-lived, Data-Protection-encrypted **consent cookie**
  (`OAuthConsentTicketProtector`) that bridges the SPA's JWT session to the cookie-less browser redirect - no
  cookie yet → redirect to the SPA **`/oauth/consent`** route (carrying the original authorize query); an
  *allow* cookie for that client + an `Active`/`IsEnabled` user → issue the code (`SignIn` with `sub`, the
  requested scopes, and the `diariz-mcp` audience); a *deny* cookie → `access_denied`. The SPA consent screen
  (`OAuthConsent.tsx`, reusing the normal login with a `returnTo`) names the client and its access, and records
  the decision via `POST /api/oauth/consent` (`OAuthConsentController`, JWT-authed + gated). The **resource
  server** is wired: OpenIddict `AddValidation().UseLocalServer()` validates the API's own access tokens
  in-process, requiring the **canonical MCP resource** (`{issuer}/mcp`, `OAuthResource`) as the audience; the
  `/mcp` bearer handler (`McpBearerAuthenticationHandler`) now accepts **either** a `dz_mcp_` static token **or**
  an OAuth token (routing by the `dz_mcp_` prefix, bridging OpenIddict's `sub` claim to `ClaimTypes.NameIdentifier`
  so owner-scoping is unchanged), and its 401 emits `WWW-Authenticate: Bearer resource_metadata="…"`. Discovery is
  served: RFC 9728 protected-resource metadata at `/.well-known/oauth-protected-resource` (`WellKnownController`),
  the AS metadata at both `/.well-known/openid-configuration` and `/.well-known/oauth-authorization-server`, with
  the hand-rolled `registration_endpoint` advertised via an OpenIddict config event. This is the point at which
  **claude.ai can connect end-to-end**. Users **manage connections** in Preferences → *Integrations* (the MCP access card):
  `OAuthConnectionsController` (`/api/oauth/connections`, JWT-authed, owner-scoped by subject) lists the granted
  OpenIddict authorizations (client name + connected date) alongside the personal tokens, and **revoke** deletes
  the authorization + its tokens so the client can no longer connect (refresh dies immediately; any issued access
  token lapses at its short lifetime). Config lives under the `McpOAuth` options block; the whole server is gated
  by `McpOAuth:Enabled` (on by default). **The OAuth-for-MCP arc is complete.**

## Outbound webhooks (Automations)

Phase 2 of the Integrations roadmap: a user can register outbound webhooks ("Automations", Preferences →
Automations) that fire signed HTTP events when their own recordings/formulas change state. Gated end-to-end
behind the `PlatformSettings.WebhooksEnabled` platform toggle (off by default; independent of the API-access and
Claude/MCP toggles) — `WebhooksController` (`/api/user/webhooks`, JWT-authed) `Forbid`s every action when the
toggle is off.

- **Event catalog (`WebhookEventTypes`), nine subscribable types:** `recording.created`, `recording.transcribed`,
  `recording.transcription_failed`, `recording.summarized`, `recording.minutes_ready`,
  `recording.action_items_ready`, `recording.tags_ready`, `formula_result.completed`, `formula_result.failed` —
  plus a tenth, **Platform-only** type, `feedback.submitted` (see the Feedback section below), and an internal
  `webhook.ping` used only by the manual "Send test event" button (neither subscribable by a Personal
  subscription). The keys
  are **append-only**: they are stored as a comma-separated string in `WebhookSubscription.EventTypes`, so a
  rename would orphan every existing subscription. Events are emitted at the recording-create site and the
  worker-callback success/failure sites (`RecordingsController`, `WorkerCallbackController`), at the formula-run
  completion/failure sites (`FormulaRunProcessor`, invoked from `FormulaRunWorker`), and — for the four AI-output
  events — at each AI processor's success path immediately after its existing `NotifyStatusAsync` hub call
  (`SummarizationProcessor`, `MeetingMinutesProcessor`, `ActionsProcessor`, `TagsProcessor`, each invoked from its
  matching worker), all via `IWebhookPublisher.PublishAsync(eventType, ownerUserId, data)`.
- **The four AI-output events carry their output inline** (the summary text, the minutes Markdown, the extracted
  action items, the tags), so a subscriber can act on the result without a second REST call — that is the whole
  reason they exist, since `recording.transcribed` fires before the model has run. Each processor takes
  `IWebhookPublisher webhooks, string publicUrl` (resolved from `AppPublicOptions` by its worker) and wraps the
  publish in its own try/catch: the output is already persisted, and a broken publisher must never flip a
  succeeded job to `Failed`. `SummarizationProcessor` emits on **both** success paths, including the short-circuit
  that preserves a hand-edited summary — the recording still reached `Summarized`, and a subscriber waiting on
  "summary ready" must not hang.
- **Personal-scope matching in Phase 2; platform-scope matching joins it in Phase 3** (see the Workflow
  Signals section below). `WebhookSubscription.Scope` defaults to `WebhookScope.Personal`. `WebhookPublisher`
  looks up the event owner's own **active** personal subscriptions plus every active platform subscription,
  filters each by its comma-separated `EventTypes` against the firing event type (and, for platform subs, a
  mandatory signal match), builds the envelope **once** per event, and inserts one `WebhookDelivery` row per
  matching subscription — it never throws (a publish failure is logged, not surfaced to the triggering
  request/worker).
- **Envelope + Standard Webhooks-style signing.** `WebhookPayload.Build` serializes a thin
  `{ id, type, created, data }` JSON envelope once, **camelCase throughout** — the publish sites write anonymous
  objects whose members are already camelCase, but the serializer's `PropertyNamingPolicy` is what keeps a nested
  record in line (`WebhookLinks` shipped as `data.links.Api`/`.Web` beside its camelCase siblings until 0.163.1,
  and any nested type a future event adds would repeat that without the policy); that exact string is stored as `WebhookDelivery.PayloadJson`
  (a `text` column, deliberately **not** `jsonb`) and is **never re-serialized** between store and send, because
  the HMAC signature is computed over the literal stored bytes. `WebhookDeliveryProcessor` signs and POSTs it with
  three headers: `webhook-id` (the stable `evt_…` idempotency key, constant across retries), `webhook-timestamp`
  (Unix seconds), and `webhook-signature` (`WebhookSigner`: `v1,base64(HMAC-SHA256(secret, "id.timestamp.body"))`)
  — the same header names/format used by the Standard Webhooks spec, so existing verification libraries work
  unmodified. The per-subscription signing secret (`dz_whsec_…`, shown once at creation) is encrypted at rest via
  `IWebhookSecretProtector` (ASP.NET Data Protection, same keyring as the summarisation API-key protector).
- **`attendees[]` on every `recording.*` event.** `Services/AttendeePayload.ForRecordingAsync` returns one
  entry per speaker ordered by diarization label (stable across events for the same recording):
  `label, name, personId, isMultiSpeaker, identifiedAuto, isInternal`, plus `title`, `companyName`, `email`
  and `phone` when contacts are permitted. `isInternal` and `personId` are **absent** (not null) for an
  unidentified speaker - the envelope serialises with `WhenWritingNull`, so nothing unknown is stated at all -
  and a `IsMultiSpeaker` slot carries no person details either, since it is overlapping audio rather than one
  human. `AttendeePayloadTests` builds its JSON through `WebhookPayload` for exactly this reason: it
  previously used plain serialisation, kept the nulls, and so asserted a shape production never emits. An **opted-out** person still appears by name: opting out concerns holding the voiceprint,
  not the fact that they attended. `recording.created` and `recording.transcription_failed` carry `[]` (no
  speakers exist yet), deliberately rather than omitting the key - a uniform shape is kinder to a workflow.
- **The contacts gate.** `WebhookSubscription.IncludeAttendeeContacts` (bool, NOT NULL, default false) is
  opt-in **per subscription**, because an automation posts to an arbitrary URL and would otherwise fan the
  directory's contact details out to whoever owns it. The publish sites build the body twice through a local
  `Body(attendees)` function and pass the second as `dataWithContacts`; `WebhookPublisher` picks per
  subscription, **reusing its existing thin-vs-full split** rather than adding a second mechanism, and only
  serialises the contact-bearing body if something actually asked for it. Both share one `eventId`, so a
  subscriber deduplicating on it does not see two meetings. When contacts are off the keys are **absent**,
  not null, so a receiver cannot read "not permitted" as "not known".
- **Postgres-backed delivery queue, not Redis.** `WebhookDelivery` rows *are* the queue: `Status`
  (`Pending`/`Delivered`/`Failed`) + `NextAttemptAt` double as both the retry schedule and a durable audit log
  (surfaced to the user via `GET /api/user/webhooks/{id}/deliveries`). `WebhookDeliveryWorker` (a `BackgroundService`,
  the API's only webhook consumer) polls every 2 seconds, and `WebhookDeliveryProcessor.ProcessDueAsync` takes up
  to `Webhooks:BatchSize` (default 20) due rows per tick (`Status == Pending && NextAttemptAt <= now`, oldest
  first). On failure it schedules a backoff retry (`WebhookBackoff`: ~8 attempts spread from 5 seconds out to
  ~10 hours, ≈24h total); on exhausting all attempts it marks the delivery `Failed` and increments
  `WebhookSubscription.ConsecutiveFailures`, **auto-disabling** the subscription (`IsActive = false`, with a
  human-readable `DisabledReason`) once that counter reaches `Webhooks:AutoDisableThreshold` (default 15) — any
  single success resets the counter to 0. A **`429 Too Many Requests`** response is handled specially: it is
  **not** a failure (no `ConsecutiveFailures`, no consumed retry attempt) — the delivery is rescheduled after the
  endpoint's `Retry-After` (or `Webhooks:RetryAfterFallbackSeconds`, default 60, when the header is absent), so a
  throttled-but-healthy automation can't disable itself. Delivery to any one subscription is also **rate-capped**:
  each attempt records `WebhookDelivery.LastAttemptAt`, and the processor keeps a subscription under
  `Webhooks:MaxPerSubscriptionPerMinute` (default 120) over a rolling minute — excess deliveries are **paced**
  (deferred, never dropped), which matters most for a Phase 3 platform automation whose signal is attached to many
  users' formulas and would otherwise burst against one endpoint. This Postgres-backed design (rather than a Redis stream, unlike the
  summarisation/minutes/actions/embedding/tag-cloud queues) is deliberate: scheduled retries and a queryable
  delivery history come for free from the relational row instead of needing a second store.
- **SSRF validation on every write.** `IWebhookUrlValidator` (`WebhookUrlValidator`) rejects a subscription's
  `Url` unless it parses as an absolute `http(s)://` URL and **every** DNS-resolved IP passes the shared
  `UrlFetchGuard.IsBlocked` check (rejects loopback/private/link-local/CGNAT/cloud-metadata ranges — the same
  guard used elsewhere for user-supplied fetch targets). Both `Create` and `Update` on `WebhooksController`
  re-validate, so a subscription can't be repointed at an internal address after creation either.
- **`App:PublicUrl` requirement.** The delivery worker and the formula-event site run with no `HttpContext`, so
  the absolute recording links included in a payload's `data.links` (`WebhookPayload.For`) depend entirely on
  `App:PublicUrl` (`AppPublicOptions`) being configured — **any deployment that enables webhooks must set
  `App:PublicUrl`** to its externally-reachable origin. The two controller-triggered sites
  (`RecordingsController`, `WorkerCallbackController`) run inside a request and fall back to
  `{Request.Scheme}://{Request.Host}` when `App:PublicUrl` is empty, but the formula-event site has no such
  fallback.
- **`Webhooks` options section:** `AutoDisableThreshold` (default 15) and `BatchSize` (default 20) — see
  `WebhookOptions` in `Configuration/AppOptions.cs`.
- **Phase 3 (Workflow Signals) builds directly on this core** — see the next section. Still deferred: a
  per-platform-subscription delivery rate cap (the delivery worker's batch-and-backoff throughput bound
  already covers this; a per-subscription token bucket is a hardening follow-up now that platform subs fan
  out across users), and detaching a platform subscription from the single admin who owns it (see below).

## Feedback (Provide Feedback)

Any signed-in user can report "something looks or behaves wrong" from the account menu, even when nothing
threw an exception. `POST /api/feedback` (`FeedbackController`, JWT-authed, no permission gate) stores a
`Feedback` row: the description, the SPA route and app release at submission, and a client-captured trail.
Reading (`GET /api/feedback`) and deleting (`DELETE /api/feedback/{id}`) are `ManagePlatform`-gated — a
Platform Administrator surface only, deliberately including a user's own submissions (a per-user view would
imply a support conversation this feature does not have) — surfaced as a Feedback tab in Settings
(`FeedbackPanel.tsx`), which lists newest-first and can expand a row to show its parsed trail.

- **The client trail is independent of the optional error-tracking stack.** `apps/web/src/lib/trail.ts` keeps
  an in-memory ring buffer (`TRAIL_CAPACITY = 30`) of recent `{ at, kind: "api" | "nav" | "mark", label, detail? }`
  entries, fed from the app's own seams — the axios interceptors and the router — rather than from the
  GlitchTip/Sentry SDK's `beforeBreadcrumb` hook. That hook only fires once the SDK has initialised, and
  Provide Feedback has to work on a deployment with no DSN configured at all (see Observability above, which
  is entirely optional). Entries are **scrubbed on the way in**, not on the way out: `record()` runs every
  label through `scrubUrlsIn` (stripping query strings, which is where the SignalR access token lands — a
  browser cannot set a header on a WS handshake, so `@microsoft/signalr` puts the JWT in
  `?access_token=`) and every `detail` value through the shared `lib/scrub.ts` rules used by the LLM
  telemetry work, redacting a key that looks sensitive outright. The buffer therefore never holds a value
  that would be unsafe to send, closing off the disclosure path that scrubbing-on-the-way-out left open in
  the telemetry feature. `FeedbackModal.tsx` snapshots the trail and posts it as `TrailJson` alongside the
  description and `window.location.pathname`.
- **The outbound event, `feedback.submitted`, is Platform-subscription only.** It is deliberately absent
  from `WebhookEventTypes.Subscribable` (the Personal list) and only present in `PlatformSubscribable`: a
  Personal subscription receives events about its own owner's data, and feedback is readable only by a
  Platform Administrator, so a Personal subscription on this type would deliver another user's words to
  them. `FeedbackController.Create` publishes it best-effort, after the row is saved, via the same
  thin-vs-full split `IncludeAttendeeContacts` uses: the default body carries `{ id, route, release,
  hasScreenshot: false }`; a Platform subscription with `IncludeFeedbackText = true` (default false) also
  receives `description` — the submitter's own free-text words. Without that opt-in, an automation that
  needs the words fetches them through the API instead. The web admin UI (Settings → Integration → Platform
  Automations) exposes it as a checkbox from 0.178.0, revealed only once `feedback.submitted` is among the
  selected events and sent as `false` whenever it is not - so a value set and then abandoned cannot ride
  along on an unrelated automation. The same screen's event picker is the only one in the app that offers
  `feedback.submitted`: `platformWebhookEvents()` in `apps/web/src/lib/webhookEvents.ts` appends
  `PLATFORM_ONLY_EVENT_KEYS` to the shared personal list, which is left untouched. Its client-side
  "choose at least one signal" guard is skipped when every selected type is in `SIGNAL_EXEMPT_EVENT_KEYS`,
  mirroring `PlatformWebhooksController.Validate` - otherwise the form would refuse what the server allows.
- **Screenshots are deliberately deferred.** `Feedback.ScreenshotBlobKey` (text, nullable) exists in the
  schema and the webhook payload already carries `hasScreenshot: false`, so that phase needs no further
  migration or payload-shape change — but the column is always null today. Capturing a screenshot needs an
  Electron shell change (same capture surface as meeting screenshots) and so a desktop release, which this
  release does not ship.

## n8n community node (`integrations/n8n-nodes-diariz`)

A published npm package installed into a **user's own n8n** (Settings → Community Nodes). It is not deployed
with Diariz and needs no server-side support beyond the REST API and the webhook endpoints already described.

- **Versioning and publishing.** The package version is a **fifth mirror of `/version.json`**
  (`versionMirrors.test.ts` enforces it), so a node's number names the Diariz version it was generated
  against. `.github/workflows/n8n-publish.yml` publishes to npm on a push to `main` touching
  `integrations/n8n-nodes-diariz/**` **except `package.json`**, and **skips when that version is already
  published** — npm forbids overwriting, so "already there" is success rather than a failed build. This
  exists because the node was published once at `0.1.0` and then sat there through roughly seventy releases
  while the API moved under it; because npm versions are immutable, that could not be corrected
  retrospectively.

  **The exclusion is the whole filter.** Mirroring the version means `package.json` changes on *every* merge,
  so including it made the path filter match everything — four npm versions of byte-identical node code went
  out (0.170.0 through 0.171.2) before it was spotted. The two decisions were made in the same PR and defeat
  each other: whenever a file is both "part of the package" and "touched by every release", it cannot also be
  a useful trigger.

- **Two nodes plus a credential.** `DiarizTrigger` (webhook trigger), `Diariz` (action), `DiarizApi`
  (bearer credential: base URL + `dz_api_` token). The credential test calls `GET /api/user/profile` and uses
  the `apiAccessEnabled` / `webhooksEnabled` flags it returns to warn at save time when a capability is off,
  rather than surfacing an unexplained 403 at first execution.
- **The trigger is self-registering** — the n8n-native pattern, and the reason the node needs no manual setup.
  `webhookMethods.default.create` POSTs `/api/user/webhooks` with n8n's own webhook URL and stores the
  **once-returned signing secret** in the node's static data; `delete` removes the subscription on
  deactivation (so the 20-per-user cap does not silt up); `checkExists` treats a subscription whose secret is
  missing as absent, so it is recreated rather than left unverifiable.
- **Signature verification is mandatory and byte-exact.** The webhook declares `rawBody: true` and
  `nodes/Diariz/signature.ts` recomputes `v1,base64(HMAC-SHA256(secret, "<id>.<timestamp>.<body>"))` over the
  original bytes — a re-serialised body would never match, since the envelope is C#-compact JSON written with
  `DefaultIgnoreCondition: WhenWritingNull` and `PropertyNamingPolicy: CamelCase`. Comparison is `timingSafeEqual`, several space-delimited
  signatures are accepted (secret rotation), and a timestamp more than 5 minutes out is rejected as a replay.
  A failed check answers `401` and emits nothing, so Diariz retries on its normal backoff.
- **Cross-language contract test.** `tests/Diariz.Api.Tests/WebhookSignerFixtureTests.cs` writes signing
  vectors (including a non-ASCII case, where UTF-8 handling usually diverges) to
  `integrations/n8n-nodes-diariz/test/fixtures/signing-vectors.json`, and the TypeScript suite verifies every
  one. Without this, .NET signing and TypeScript verification could drift apart and silently reject every live
  delivery with nothing failing in either suite.
- **The action node's operations are generated from the published OpenAPI document.**
  `OpenApiSnapshotTests` writes `nodes/Diariz/generated/openapi.snapshot.json` using the same in-process host
  as `OpenApiDocumentTests` (no database, no containers); `scripts/generate.ts` turns it into 179 operations
  across 31 resources, taking each operation's display name from its `[EndpointSummary]` and its help text
  from the first paragraph of its `[EndpointDescription]`. The `Auth` tag is excluded (it takes an account
  password; the node is token-authenticated). A **Custom API Call** on every resource keeps anything added
  later reachable.
- **A curated layer decorates specific generated operations** (`nodes/Diariz/enhancements.ts`) rather than
  introducing a parallel set of hand-written resources, which would show two entries for the same thing:
  `loadOptions` dropdowns for parameters that name a listable entity, binary download/upload for the file
  endpoints, Return All / Limit wherever the response schema is an array (derived from the document, so a new
  list endpoint gets it automatically), completion polling for the `202`-returning formula run, and SSE
  accumulation for `POST /api/chat/stream`.
- **CI drift guard.** The `n8n-node` job in `.github/workflows/ci.yml` re-runs the snapshot test and
  `npm run generate`, then `git diff --exit-code`s the package — so an endpoint cannot change under the node
  unnoticed. This lockstep guarantee is the reason the package lives in this repository rather than its own.
- **Constraints that are requirements, not preferences.** Zero runtime dependencies and an MIT `LICENSE`
  inside the package directory (the repository root stays AGPL-3.0) are both n8n verified-node requirements.
  The package version is deliberately **not** a `version.json` mirror: n8n users pin it, and a node fix must
  be able to ship without a platform release.

## Workflow Signals and platform automations

Phase 3 of the Integrations roadmap - **completes the integrations feature**. Where Phase 2 automations are
personal (a subscription only ever sees its own owner's events), Workflow Signals let a **Platform
Administrator** wire one automation to fire **for every user**, and let a **formula author** opt a formula
into it with no URL or per-user setup at all.

- **`WorkflowSignal` — the admin-defined vocabulary.** A named routing key (`Key`/`Label`/`Description`/
  `IsActive`) a Platform Administrator manages at Settings → Integration → Workflow Signals, e.g. `Key:
  post-to-slack`, `Label: "Send to Slack"`. `WorkflowSignalsController` (`api/workflow-signals`): `GET` (any
  authenticated user — feeds the formula editor's picker, active signals only) is open; `GET manage`/`POST`/
  `PUT`/`DELETE` require the `ManagePlatform` policy. **`Key` is immutable after creation** — it is the value
  formulas and subscriptions reference, so the edit endpoint updates only `Label`/`Description`/`IsActive`.
  Deleting a signal cascades its `FormulaWorkflowSignals` links but leaves any `Webhooks.SignalFilter` text
  referencing the dead key alone (harmless — it just never matches again).
- **`FormulaWorkflowSignals` — a formula's attached signals.** A formula author, in the formula editor, picks
  zero or more active signals under "When this finishes, trigger: ...". `FormulaWorkflowSignal` is a plain
  join (`FormulaId`, `WorkflowSignalId`, both cascade).
- **`Webhooks.SignalFilter` and the `Platform` scope.** `WebhookScope.Platform` (already reserved on the
  `Scope` column in Phase 2) is now live: a Platform subscription is managed by `PlatformWebhooksController`
  (`api/admin/webhooks`, `ManagePlatform`-gated, CRUD only — no test-ping or delivery log like the personal
  controller) and is **not owner-scoped for reads/writes** (any admin can manage any platform subscription),
  though it still carries an `OwnerUserId` (the creating admin) for the cascade. Its new `SignalFilter`
  column (comma-separated `WorkflowSignal.Key`s) is the routing filter. **A platform subscription's filter
  cannot be empty** — enforced both at create/update (`PlatformWebhooksController.Validate` rejects an empty
  list) and at publish/match time (see below) — a deliberate footgun guard, since an empty filter reading as
  "match everything" would make a half-configured automation fire on every signal.
- **Platform-scope matching rule (`WebhookPublisher`).** `IWebhookPublisher.PublishAsync` gained `signals`
  (the firing formula's active signal keys) and `platformData` (the inline-output body) parameters. It loads
  every active subscription (personal, owned by the event's user, **or** platform, any owner), then splits
  matching in two: a **personal** subscription matches on its subscribed event type and (if it has a filter)
  a signal intersection; a **platform** subscription matches only when its (mandatory) `SignalFilter`
  intersects the firing signals — `WebhookSignals.Intersects` on an empty filter always returns `false`, which
  is what enforces the footgun guard above. Matching subscriptions of either scope get one `WebhookDelivery`
  row each, same delivery/retry/auto-disable machinery as Phase 2.
  **One exception, keyed on the event type:** `WebhookEventTypes.IsSignalRouted` returns `false` for
  `feedback.submitted`, and the platform branch skips the signal gate for it. Nothing in the product attaches
  a signal to a feedback submission, so the gate could never open and the type would be subscribable but
  permanently undeliverable. The exemption is deliberately per-type rather than a relaxation of the gate
  (e.g. letting an empty filter match), which would widen every existing signal-routed type. A new
  platform-only event type carrying no signals must be added there too, or it silently delivers to nobody.
  **`PlatformWebhooksController.Validate` mirrors that exemption at creation time** (0.177.0): it demands a
  non-empty `SignalFilter` only when at least one chosen event type is signal-routed, so a subscription made
  purely of exempt types (today, `feedback.submitted`) may leave the filter empty. Before that, the two rules
  disagreed - the publisher delivered feedback regardless of the filter while the controller refused to
  create the subscription without one, so an admin had to invent a meaningless signal to subscribe at all.
- **The n8n community node reaches platform scope (0.177.0).** `DiarizTrigger` gained a **Scope** parameter:
  `personal` (the default, and the only behaviour before this) registers through `/api/user/webhooks` as it
  always did; `platform` registers through `/api/admin/webhooks`, exposes the platform event list including
  `feedback.submitted`, a Workflow Signal picker backed by `GET /api/workflow-signals`, and the
  `IncludeFeedbackText` opt-in. The node records the scope it created under in its static data and deletes
  from **that** endpoint rather than the currently selected one - after a scope change the subscription still
  lives where it was made, so deleting from the new endpoint would 404 and strand it delivering forever.
  `IncludeAttendeeContacts` stays Personal-only because `CreatePlatformWebhookRequest` does not accept it.
- **Inline output only on platform signal-routed deliveries.** `FormulaRunProcessor` builds two payload
  bodies for a completion/failure event — a thin `data` body (ids, status, links; what every Phase 2 personal
  subscriber has always received) and a richer `platformData` body that additionally carries the rendered
  formula `output` text, the recording name, and the formula name. It passes **both** to `PublishAsync`
  alongside the formula's active `signals`; the publisher — not the processor — decides who gets which body:
  every matching **personal** subscription gets the thin body regardless of signals, every matching
  **platform** subscription gets `platformData` (falling back to the thin body only if `platformData` is
  null). This split is deliberate: a personal automation must never leak a formula's generated content to a
  webhook receiver the recording's owner didn't intend to see it, while a platform automation exists
  specifically to route that output somewhere (e.g. post the generated minutes to a team Slack channel).
- **New admin endpoints:** `api/workflow-signals` (signal CRUD + the open list for the picker, above) and
  `api/admin/webhooks` (platform subscription CRUD, above) — both surfaced in Settings → Integration
  alongside the personal Automations card on Preferences → Integrations.
- **Deferred follow-ups (documented, not built in Phase 3):** a per-platform-subscription delivery rate cap
  (see the Phase 2 section above); detaching a platform subscription from the single admin who created it
  (today, deleting that admin cascades and deletes their platform subscriptions too — a future improvement
  is to let a platform subscription survive its creator's departure); and the Phase 2 carryover minors
  (`Z` vs `+00:00` timestamp formatting, an N+1 query in the delivery worker, delivery-time IP re-pinning)
  remain open.

## Auth, multi-tenancy, and roles

- **ASP.NET Core Identity** (Guid keys) issues **JWT** bearer tokens (`TokenService`). Browsers pass the
  token as `?access_token=` on the SignalR WebSocket handshake (picked up in `Program.cs` `OnMessageReceived`).
  The token is a **sliding session**: the web client silently calls `POST /api/auth/refresh` (re-issues a token
  for the still-authenticated user) shortly before expiry, so long sessions — e.g. a recording left running —
  don't lapse. Recordings are also written to the browser (**IndexedDB**, `lib/pendingRecording.ts`) the moment
  Stop is pressed, before upload, and offered for re-upload on return if the upload didn't complete — so a
  session lapse can never lose audio.
- **Personal API tokens (user API access).** A user can call the general REST API programmatically with a
  personal token (`dz_api_` + base64url(32), **SHA-256 hash only** stored on `ApiAccessToken`, shown once;
  `ApiTokensController`, `/api/user/api-tokens`, JWT-authed). Auth is a dedicated `"ApiKey"` scheme
  (`ApiKeyAuthenticationHandler`) that resolves the token (`ApiTokenAuthenticator`) and builds a principal with
  the owner's id — **full session parity**, so ownership checks and admin authorization work exactly as for a
  JWT (the permission policies resolve group membership from the `NameIdentifier` claim that both schemes emit).
  To make it satisfy every `[Authorize]` variant, the **default authenticate scheme is a forwarding policy scheme**:
  `Bearer dz_api_…` routes to the ApiKey handler, everything else (JWT, or the query-string SignalR/audio/backup
  flows) to JWT. Isolation: a `dz_mcp_` token is rejected on `/api/*` and a `dz_api_` token on `/mcp` (each scheme
  accepts only its own prefix). The feature is **gated by `PlatformSettings.ApiAccessEnabled` (default off)** —
  the authenticator fails while it's off — and a Platform Admin toggles it in **Settings → Integration**; users
  manage tokens in **Preferences → Integrations** (the card explains itself when disabled). A **curated OpenAPI document** is
  published at **`/api/openapi/v1.json`** (`Microsoft.AspNetCore.OpenApi`, authenticated; the user-facing REST
  surface only — `api/*` minus the admin/OAuth prefixes `api/oauth`/`api/platform`/`api/admin`/`api/maintenance`,
  and the non-`api/` `internal/*`, `connect/*`, `.well-known/*`, `/mcp` — with a bearer security scheme declared,
  see `OpenApiCuration`), and a signed-in user can browse it via an in-app **Scalar** reference at
  **`/developers/api`** (lazy-loaded route, `@scalar/api-reference-react`), linked from both the Developers and
  Integration tabs.
  - **Reference copy.** Each section carries a one-line explanation from `OpenApiCuration.TagDescriptions` (keyed
    by controller name minus `Controller`, which is the tag the generator assigns), and each operation carries an
    `[EndpointSummary]` + `[EndpointDescription]` on the action - these work on MVC controller actions, not just
    minimal-API handlers. Descriptions are Markdown (Scalar renders it) and follow the user-facing-text
    convention (plain hyphens, no em/en dashes). Two tests enforce the copy against the **really generated**
    document: every tag group must have a description, and **every published operation** must have both a summary
    and a description. The whole surface is decorated, so both assertions are unconditional - a new endpoint fails
    `OpenApiDocumentTests` until it is documented, which is the mechanism that keeps the reference complete.
  - **Scope and expiry.** Each token carries an `ApiTokenScope` (`ReadOnly` = 0, `ReadWrite` = 1, column default
    1 so pre-existing tokens keep full access) and an optional `ExpiresAt` (null = never expires), both settable
    only at creation. `ApiTokenAuthenticator` fails a presented token once `ExpiresAt` has passed; a read-only
    token's scope travels onto the principal as a claim and `ApiTokenScopeMiddleware` (a pure `ApiTokenScopePolicy`
    plus a thin ASP.NET middleware, runs after authentication) rejects any unsafe verb (POST/PUT/PATCH/DELETE)
    from it with 403 — JWT/browser sessions carry no scope claim and are unaffected. The create-token UI
    (Preferences → Developers) offers a **"Read-only (cannot change anything)" checkbox** and an optional expiry date picker.
- **Three independent platform integration toggles.** `PlatformSettings` carries three master switches, each a
  Platform Admin setting on **Settings → Integration** and independent of the others: `ApiAccessEnabled` (personal
  API tokens / the REST API, default **off**), `McpAccessEnabled` (the `/mcp` server and `dz_mcp_` tokens, default
  **on** — seeded `true` in its migration so shipping the toggle never disables an already-connected MCP client),
  and `WebhooksEnabled` (outbound webhooks / user Automations, default **off**, enforced from the Phase 2 webhooks
  core). Turning one off does not affect the others — e.g. disabling Automations leaves existing API tokens and
  the MCP connector working.
- **Inbound automation surface (fire a formula / fetch its output).** External tools that want to trigger Diariz
  work and read the result — the shape Phase 2/3 webhooks and Workflow Signals build on — already have a surface
  via the existing Formulas endpoints, authenticated the same way as any other REST call (a `dz_api_` token, scope
  and expiry enforced as above): **`POST /api/recordings/{recordingId}/formulas/{formulaId}/run`** validates,
  creates a `Generating` `FormulaResult`, enqueues the run, and returns **202** with the new result's id (a
  read-only token is rejected here, since it is a state-changing POST); **`GET
  /api/recordings/{recordingId}/formula-results/{id}`** then polls/fetches that result, including its `Status`
  (`Generating`/`Ready`/`Failed`) and, once ready, the generated Markdown text. Both routes are `api/*` and not
  under an excluded prefix, so they are also published in the curated OpenAPI document (`OpenApiCuration`) and
  browsable in the in-app reference — no new endpoints were added for this.
- **RBAC (user groups + platform permissions).** Authority comes from **group membership**, not from a role.
  A `UserGroup` carries a `[Flags] PlatformPermission` (`ManageRooms = 1`, `ManageUsers = 2`,
  `ManagePlatform = 4`, `ManageFormulas = 8`, `ManagePeople = 16`; append-only), users join via `UserGroupMember`, and a caller's effective permissions
  are the **union** of the flags on every group they belong to. `IUserPermissions` resolves that **from the
  database on each request** — never from a token claim, which would keep granting authority until it expired,
  long after the user left the group. `PermissionAuthorizationHandler` backs the policies `ManageRooms`,
  `ManageUsers`, `ManagePlatform`, and `ReadAdminSettings` (= `ManageUsers` **or** `ManagePlatform`, so an
  administrator can still read platform settings for the default-quota field). Each policy requires **any** of
  the flags it names; an anonymous or malformed principal fails closed.
  - Two groups are seeded: **`Platform Administrators`** (`IsSystem`, all three flags) and **`Administrators`**
    (`ManageRooms | ManageUsers`, deliberately **not** `ManagePlatform` — that flag confers backup/restore and
    platform-settings writes). The system group cannot be deleted, renamed, or have its permissions changed,
    and its **last member cannot be removed**, so a deployment can never be left unadministrable. The seed user
    is placed in it on every boot.
  - The old Identity roles (`Standard` / `Administrator` / `PlatformAdministrator`) are **no longer read for
    authorization**. Existing role holders were moved into the two groups **once**, by the `AddUserGroups`
    migration (`RoleToGroupBackfill`) — a one-way move, deliberately not a boot-time reconciliation, which
    would silently re-promote anyone demoted since from their stale `AspNetUserRoles` row. The role tables
    remain, unused, pending a later chore.
  - Groups are administered at **`/api/groups`** (`GroupsController`, `ManageUsers`) and in the web
    **Manage Users → Groups** tab. The web reads the caller's permissions from `GET /api/user/profile`.
- **A new permission has two obligations, and missing either makes it ungrantable.** It must be added to
  `Seeder.SeedGroupsAsync` (which ORs the flags onto the existing rows, so a deployed platform picks it up on
  its next boot with no migration) **and** to `PERMISSION_BITS` in `apps/web/src/components/GroupsTab.tsx`,
  which is the only UI that can set one. `ManagePeople` shipped with neither in 0.164.0 and left the People
  page unreachable for everyone including platform administrators, because every test granted it explicitly
  through `Perms.Grant` and so exercised the mechanism while saying nothing about whether it was obtainable.
  Two guards now exist: `SeederPeoplePermissionTests.Every_platform_permission_is_granted_to_a_seeded_group`
  and `groupsPermissionBits.test.ts`.
- **Rooms (foundation; not yet wired into any controller).** A **room** is a workspace: folders, recordings,
  voiceprints, chats and meeting types live in one. Every user has exactly one **Personal room** — immutable and
  private, named after them, rendering their avatar rather than a stored icon. A recording's **main room is
  always its recorder's Personal room**, which is what stops a shared room ever holding a recording hostage
  (deleting a shared room can then only unshare, never destroy). Deleting a user **orphans** their Personal room
  (`OwnerUserId` → null) rather than cascading, so recordings they shared into team rooms survive their
  departure. (`RoomMember.PrincipalId` has no FK to cascade — it points at either `AspNetUsers` or `UserGroups`
  — so deleting a user or a group **sweeps** their `RoomMembers` rows explicitly, in `AdminUsersController.Delete`
  and `GroupsController.Delete` respectively, before the principal is removed.)
  - `RoomMember` carries a `[Flags] RoomPermission` (`ManageRoom = 1`, `CreateRecording = 2`,
    `RemoveOthersRecordings = 4`, `ShareOut = 8`, `ManageContents = 16`, `EditOthersRecordings = 32`;
    append-only) and its principal is a **user or a group**. `RoomScope` (`Services/RoomScope.cs`) resolves a
    caller's effective permissions as the **union** of their own member row and the rows of every group they
    belong to; the **owner of a personal room implicitly holds everything**, and a personal room ignores member
    rows entirely. **Membership is row existence, not "holds some permission"** — a member granted nothing can
    still see the room. Membership is also the **read gate**: a non-member gets **404, not 403**, so a stranger
    cannot learn that a room exists.
  - `RoomScope.PersonalRoomIdAsync` **finds-or-creates**, rather than each of the four user-creation sites
    remembering to. The filtered unique index on `Rooms.OwnerUserId` makes the race safe. Pre-existing users were
    given rooms **once**, by the `AddRooms` migration (`PersonalRoomBackfill`) — never on boot, or a seeder would
    recreate a room the user had since changed.
  - **Recording placement.** A recording's folder is a property of its **`RoomRecording`** placement, not of
    the recording: `Recording.SectionId` no longer exists. Each recording has exactly one **main** placement,
    always in its recorder's Personal room (`IsMainRoom`, a filtered-unique invariant), which is what stops a
    shared room ever holding a recording hostage — deleting a shared room can only unshare, never destroy. The
    folder lives on `RoomRecording.SectionId` (the folder *within that room*; `ON DELETE SET NULL`, so deleting
    a folder ungroups the placement). `RoomScope.RecordingsIn(roomId)` is the base queryable every room-scoped
    recording query starts from — the equivalent of the old `.Where(r => r.UserId == UserId)`, one level up —
    and `RecordingsController`, `SectionPageController`, `ChatController` and `SectionSummaryProcessor` all read
    the folder through it. Filing one recording is `PUT /api/recordings/{id}/section`; **`POST
    /api/recordings/section`** (`RecordingsController.MoveManyToSection`) is the bulk form, moving every listed
    id into one folder (or ungrouping them all with a null `sectionId`) in a single call — it backs the web's
    cut/paste flow, which lets a user cut several selected recordings, or a single folder, and paste them into
    wherever they have drilled into. Both endpoints are gated the same way: room membership (404 for a
    non-member) plus `ManageContents` in that room (the personal-room owner always holds it) — authorization is
    the **room's**, not the recording's `UserId`, so a member with the permission can file a colleague's
    recording. Unlike `PUT /api/recordings/reorder`, which imposes an explicit `0..n-1` order, the bulk endpoint
    **appends**: it reads the highest `Position` already occupying the target folder (excluding the ids being
    moved, so re-pasting a folder's own contents into itself is idempotent) and lays the listed ids after it in
    the order given. Ids no longer placed in the room are skipped rather than failing the whole call, so a stale
    clipboard entry (something deleted in another tab) doesn't lose the rest of the paste.
  - **Folders carry a room (2c).** `Section.RoomId` is set on create and backfilled to each folder's owner's
    personal room; `SectionsController` scopes by it, and `RoomScope.SetSectionAsync` refuses to file a
    recording under a folder from another room. `Section.UserId` is **kept** as owner identity (the SignalR
    group folder notifications target, the per-user LLM config the folder processors resolve); dropping it, and
    the Rooms FK, wait for Phase 4, when "the owner" of a shared-room folder is no longer one user.
  - **Voiceprints, chats and meeting types carry a room (2d).** `SpeakerProfile.RoomId`, `ChatSession.RoomId`
    (both not-null) and `MeetingType.RoomId` (**nullable** - null for Platform types, mirroring their null
    `UserId`) are set on create and backfilled to each owner's personal room. As with folders they are **plain
    columns still queried by `UserId`**; the query flip, the Rooms FK and the `UserId` drop wait for Phase 4.
  - **Rooms surface in the UI (Phase 3).** `GET /api/rooms` (`RoomsController` → `RoomScope.RoomsForUserAsync`)
    lists the rooms the caller belongs to (today: just their Personal room) with the caller's effective
    `RoomPermission` grid as an **int bitmask** (`RoomListItemDto.Permissions` - a `[Flags]` enum would serialise
    as `"A, B"` and break the web's bit arithmetic), plus `SectionCount`/`RecordingCount` for the switcher's
    detail line (two grouped counts over the caller's rooms, not a query per room; a recording shared into
    several rooms counts in each - the number answers "what will I find in here"). These are the **only**
    server-side counts in the nav: the drill-in list's counts derive client-side from the already-fetched
    recordings, but the switcher's are cross-room and cannot. The web `RoomProvider`
    (`apps/web/src/lib/rooms.tsx`, mounted in `WorkspaceLayout`) reads that list, derives the current room from
    a `/rooms/:roomId` URL segment (via `useMatch`, defaulting to the personal room), and exposes the room, its
    permission grid (`can(perm)`, failing closed while loading), the folder the user is viewing, and the
    **resolved placement target** for a new recording. It also **remembers the last room** (`lib/roomPersistence`,
    localStorage `diariz.rooms.currentRoomId`) and redirects a bare landing at `/` to it - the URL still wins
    whenever it names a room; this only fills the gap when it doesn't. A remembered *personal* room never
    redirects (its detail routes **are** the top-level ones, so `/rooms/<personal-id>` would be a redundant
    second URL for the same place), nor does one the caller has since left. A **room switcher** replaces the old "Meetings" panel header; `Record`/`Upload` disable without
    `CreateRecording` (always granted in a personal room, so unchanged today). A nested `rooms/:roomId` route
    group mirrors the four workspace children; the legacy top-level children stay working as the personal-room
    default. Per-room link rewrites + query-key isolation are no-ops with one room and land in Phase 4.
  - **Where a new recording lands (Phase 3).** `UserSettings.RecordingPlacementMode`
    (`Ungrouped`/`SelectedFolder` (default)/`SpecificFolder`) + `RecordingPlacementSectionId`, set in a new
    **Recordings** Settings tab, decide the folder. `RoomProvider` resolves the mode against the folder the user
    is viewing; `Recorder` snapshots that target when Record is pressed and passes it as `sectionId` to
    `POST /api/recordings`, which files the main placement there **only if the folder belongs to the uploader's
    personal room** (an alien/stale id is ignored, not misfiled).
  - **Shared rooms are real (Phase 4).** `RoomsController` (gated by the `ManageRooms` platform policy) creates,
    renames/restyles and deletes shared rooms and edits their membership (`RoomScope.CreateSharedRoomAsync` /
    `UpdateRoomAsync` / `DeleteRoomAsync` / `SetMemberAsync` / `RemoveMemberAsync`); the Personal room is immutable
    and memberless (every write refuses it), and shared-room names are unique. The web **Manage Rooms** modal
    (reached from the switcher, `ManageRooms` holders only) drives all of this and reuses the shared
    `IconColorPicker`; a member's grid is the six `RoomPermission` checkboxes, and delete needs the room name typed.
    **Recording into a shared room** writes a **two-placement transaction**: the always-main placement in the
    recorder's Personal room (Ungrouped) plus a non-main `RoomRecording` in the shared room
    (`RoomScope.ShareIntoRoomAsync`, `SharedBy`/`SharedAt`); the upload 403s if the caller can't `CreateRecording`
    there. **Deleting a user** sweeps their `RoomMember` rows (no FK to cascade them) and orphans their Personal
    room via the `OwnerUserId` SetNull FK - their shared recordings survive. It also cleans up object storage:
    before the cascade runs, `AdminUsersController.Delete` re-points any `SectionAttachment` the departing user
    uploaded into a folder they don't own (`UploadedByUserId` -> the folder owner - the row and its blob survive,
    and the bytes move onto the folder owner's storage usage) and then collects and deletes every MinIO blob the
    user does own outright - their recordings' audio, recording attachments, meeting screenshots (full image +
    thumbnail), and own-folder section attachments - so nothing is left orphaned in object storage. Blob deletes
    are best-effort (logged and skipped on failure) so one bad object-storage call can't abort the whole account
    deletion. The **folder / voiceprint / chat /
    meeting-type queries are all scoped by `RoomId`** now (owner-identity for LLM config + SignalR still resolves
    to the room's owner). The now-dead `UserId` columns on `Section` / `SpeakerProfile` / `ChatSession` /
    `MeetingType` are **retained pending a follow-up drop** (harmless; nothing reads them).
  - **Cross-room sharing + room-scoped search (Phase 5).** `POST /api/recordings/{id}/share` adds a non-main
    placement (needs `ShareOut` in the source room + `CreateRecording` in the target);
    `DELETE /api/rooms/{roomId}/recordings/{id}` unshares (the recorder or a holder of `RemoveOthersRecordings`,
    never the main room). `RecordingsController.Get` is visible to the recorder **or** a member of any room the
    recording is placed in, and returns its **recorded-by** + the **rooms** the caller can see (home first); the
    web Overview renders those two lines, and the toolbar/kebab gain **Share to room** / **Remove from room**
    (Delete hidden outside the home room; its confirm names the shared rooms). That same "recorder-or-room-member"
    rule is now extracted onto `IRoomScope.CanReadRecordingAsync(userId, recordingId)` - a single, testable "can
    read this recording" predicate every per-recording sub-resource shares rather than re-deriving its own.
    **`ScreenshotsController`** (list/content/thumb) and **`MeetingNotesController`** (list) call it for their read
    routes, so a room co-viewer sees a shared recording's screenshots and notes woven into the transcript exactly
    as the owner does; every mutating route on both controllers (create/update/delete) stays gated on plain
    ownership (`Recording.UserId == caller`), unaffected by room membership. The web hides the add/edit/delete
    controls for a non-owner (`RecordingDetail.tsx` compares `useAuth().id` against the detail's
    `recordedByUserId`) rather than rendering a control the API would reject. **Search now spans rooms:**
    `TranscriptSearch`'s five arms gate on a `RoomRecordings` semi-join over `RoomScope.RoomIdsForUserAsync`
    (a non-minting read), so chat + MCP tools find recordings shared into any room the caller belongs to. The
    filtered vector scan is **not yet benchmarked** on a large corpus; the denormalise-`RoomId`-onto-chunk
    fallback stays open if it regresses.
  - **Room-scoped recording collections (Phase 6).** `GET /api/recordings` and `/api/sections` take an optional
    `roomId` (default = the caller's personal room), membership-gated (404 for non-members); the recordings come
    from `RoomScope.RecordingsIn(room)` with folders from that room's placements (`PlaceInMainRoomAsync` is now
    idempotent). The web `RecordingsPanel` reads `useRoom()` and fetches `["recordings", roomId]` /
    `["sections", roomId]`, so switching the switcher to a shared room browses the recordings shared into it - a
    flat list with folders, drag-reorder, the personal Google-calendar overlay and the Actions/Tags aggregation
    tabs hidden (they belong to the Personal room). The Personal room is unchanged.
  - **Still ahead:** the deferred `UserId` column drop on the room-scoped entities (incl.
    `TranscriptChunk.UserId`) - non-breaking, left in place by decision. See
    `docs/superpowers/specs/2026-07-10-rooms-design.md`.
- **Access lifecycle:** a person **requests access** (`UserStatus.Requested`) → an admin **grants** it
  (issues a one-time setup link; emailed via SMTP/MailKit, or shown to the admin as a fallback when SMTP is
  unconfigured) → the user **sets up** their name + password (`Active`). Admins can also add users directly.
- **Google sign-in (optional):** a server-side **OAuth 2.0 authorization-code + PKCE** flow
  (`AuthController` `google/start` → `google/callback`; `GoogleAuthService` validates the ID token via
  `Google.Apis.Auth`; `GoogleSignInHandler` links/creates the account). Enabled only when `GoogleAuth:ClientId`
  + `ClientSecret` are configured (`GET /api/auth/providers` tells the SPA whether to show the button). On
  success the API leaves the token in a one-time **HttpOnly** handoff cookie and the SPA swaps it for the
  token via **`POST /api/auth/google/exchange`** (a JSON body — robust against reverse proxies that strip URL
  fragments or force HttpOnly on cookies; the token never touches a URL). Requests only `openid email profile` (no
  Gmail/Calendar yet); stores the Google `sub` on `ApplicationUser.GoogleSubject` (unique) + the profile
  picture on `PictureUrl` (a `picture` JWT claim → account-menu avatar). New Google users land as `Requested`
  (same admin-approval gate; an admin granting a Google account activates it directly, no setup link); a
  verified Google email matching an existing account **auto-links**.
- **Google sign-in on the desktop (system browser + `diariz://` handoff):** Google blocks OAuth in embedded
  webviews, so the Electron shell runs consent in the **system browser** and gets the result back via a custom
  protocol. `google/start` carries a **`desktopChallenge`** (the S256 of a PKCE verifier the app holds) in the
  encrypted state cookie; on success `google/callback` mints a **single-use, 2-minute code** in Redis
  (`IDesktopAuthCodeStore`, GETDEL) and returns a **small "you can close this tab" HTML page** that launches
  `diariz://auth/callback?code=…` (script navigation, plus a manual **Open Diariz** link for browsers that block
  a scripted external-protocol launch). It is deliberately a page, not a 302 to the custom scheme: a browser
  cannot commit a non-HTTP navigation as a document, so a redirect launches the app but aborts the navigation,
  leaving the tab parked on Google's consent page with its loading animation still running. The desktop app redeems the code at
  **`POST /api/auth/desktop/exchange`** by sending its `verifier`; the server checks `S256(verifier)` against the
  bound challenge (constant-time) and returns the JWT. The token never travels in a URL. The Electron shell owns the
  `diariz://` scheme (`electron-builder` `protocols` + `setAsDefaultProtocolClient`), opens the start URL with
  `shell.openExternal`, receives the deep link (cold-start argv / `second-instance` / macOS `open-url`), redeems it,
  and pushes the token to the renderer over an `auth:token` IPC channel; the web `AuthProvider` adopts it via
  `window.diariz.onAuthToken` through the same path as a password login, and the **login page redirects on
  `isAuthed`** so an out-of-band token (the desktop hand-off) leaves the login screen. The login page shows the
  Google button in the shell too (it calls `window.diariz.startGoogleSignIn()` instead of a full-page redirect).
  A **failed** exchange is no longer silent: the shell pops a native notification and pushes an `auth:error`
  (reason `network`/`expired`/`rejected`) which the login page surfaces (`window.diariz.onAuthError`).
- **Google data access (opt-in, Phase 2):** a Google-linked user can grant **Calendar (read)** from
  Preferences via an **incremental-consent, offline** flow (`AuthController`
  `POST google/connect` → the shared `google/callback` branches on a `mode` in the state cookie →
  `google/disconnect` revokes). The **refresh token is encrypted at rest** on `UserSettings`
  (`IGoogleTokenProtector`, dedicated Data-Protection purpose); `IGoogleTokenProvider` mints short-lived
  access tokens on demand (cached in-memory, never persisted or sent to the browser) and clears a
  revoked/expired token. `calendar.readonly` is a Google **sensitive** scope (operator enables it on the OAuth
  app; unverified apps work for the owner + test users). *Gmail draft creation was removed in 0.67.1 — Gmail
  scopes are **restricted** and would require a recurring third-party security assessment to verify; the
  minutes-email-to-self feature covers that need.*
- **Multiple calendars (Phase 2 feature):** `IGoogleCalendarClient` reads **all the user's selected calendars**,
  not just primary. `ListCalendarsAsync` (private) fetches **`users/me/calendarList`** and narrows it via the
  pure `ApplySelection` helper to the user's **stored Diariz selection** (`UserSettings.GoogleSelectedCalendarIdsJson`,
  read via `IGoogleCalendarSelectionStore`); when unchosen (null) it falls back to the entries the user has ticked
  visible in Google (`selected`) plus their `primary`. `ListEventsAsync` then fetches each calendar's events
  **in parallel** (a single flaky/shared calendar is skipped, not fatal), tagging every event with its
  **`CalendarId`/`CalendarName`/`Color`** (the calendar's Google background hex). `GetEventAsync` searches the
  selected calendars (primary first) for an event by id. Users manage the selection in **Preferences → Calendars
  Account** (`GET`/`PUT /api/calendar/calendars`, backed by the public `ListAllCalendarsAsync` which returns the
  unfiltered list for the picker); the single `ListCalendarsAsync` chokepoint means the selection restricts
  matching, linking, and the Calendar overlay alike. Still `calendar.readonly` - the existing grant already
  covers team/shared/subscribed calendars, so no new scope.
- **Match a recording to its calendar meeting (Phase 2 feature):** with the Calendar grant, the recording's
  Overview calls **`GET /api/recordings/{id}/calendar-match`**, which asks `ListEventsAsync` for events across the
  user's selected calendars around the recording's wall-clock span (padded ±30 min), and returns the **best
  time-overlapping** event (`GoogleCalendarClient.PickBest`) as `{ match }` (or `null`).
  - **The span is `StartedAt` .. `EndedAt`**, falling back to `CreatedAt` .. `+DurationMs` when the client did not
    report them (uploaded files, pre-`AddRecordingStartedAt` rows). This distinction is the whole feature:
    `CreatedAt` is when the **upload** landed, so for a recorded take it is roughly when it *stopped* - spanning
    from it put the window a full recording-length late and it overlapped nothing. Note the ±30 min pad only
    widens the **fetch**; `PickBest` scores the unpadded span and requires `overlap > 0`, so the pad never
    rescued a mis-anchored span. `EndedAt` is tracked separately from `DurationMs` because `DurationMs` is
    captured-audio length: it excludes paused time, and after a merge it is the concatenated length.
  **All-day entries are excluded from matching**: a date-only event (holiday, birthday, out-of-office day) blankets
  the whole day, so it would out-overlap every real meeting and would be auto-linked to anything recorded that day.
  `CalendarEvent.AllDay` carries the flag (Google: a `date` rather than `dateTime` start; ICS: a date-only `DTSTART`)
  and `PickBest` skips those events - they remain linkable **by hand**. Read-only (`calendar.readonly`); 400s without
  the grant. The Overview shows the matched meeting with a link to the Google Calendar event.
- **Persisted calendar links (Phase 2 feature):** the match above is a *suggestion* - a recording can also be
  **persistently linked** to an event via **`PUT /api/recordings/{id}/calendar-link`** `{ eventId, manual }`
  (owner-scoped, requires the grant), stored as a 1:1 **`RecordingCalendarLink`** (shared PK, cascade) holding a
  lightweight snapshot (event id, **calendar id**, title, start/end, link, **colour**, manual flag) - the calendar
  id lets a link target an event on **any** of the user's calendars (team/shared/subscribed), not just primary.
  **`DELETE`** unlinks. The link's presence flows onto the recording's **detail** (`CalendarLink`, incl. `calendarId`/
  `color`) and **list** (`CalendarEventId` + `CalendarColor`) projections, so the UI can show a calendar icon
  (tinted the calendar's colour) and dedupe the Calendar tab. The **rich invite details** (attendees, description, location,
  organiser) are fetched **live by id** via **`GET /api/calendar/events/{eventId}`** (`GoogleCalendarClient.GetEventAsync`;
  404 when the event is gone or Calendar isn't connected) - never stored, so they can't go stale. Linking works in
  both directions (recording → event, and event → recording) and regardless of time overlap (a manual link handles
  meetings that ran late/over). **Web behaviour:** opening an unlinked recording that has a good time-overlap match
  **auto-saves** the link once (client-driven `PUT` with `manual:false`, so GETs stay pure and the icon/details
  appear with no clicks); the recording hub shows a summary card (`MeetingCard`) - title, time, location and
  attendee count - and clicking it opens a **Calendar Event** section (drilled into like Notes or Actions) that
  renders the meeting's full details (`CalendarEventDetails`, fetched live, falling back to the snapshot) with
  **Change meeting** (a browse-events modal - date-range + title filter, `CalendarLinkModal`) and **Unlink**
  actions. A manually-linked event is never overwritten by the auto-match.
- **Recording started from a calendar event (Join and record).** The Join button on `CalendarEventDetail` opens the
  meeting URL and asks the recorder to start, over the one-line `lib/recordRequest.ts` channel (the recorder is
  mounted once in the capture bar; a plain subscription keeps the page ignorant of where it is). The request now carries
  a **`CalendarEventContext`** - `{ id, summary, endsAt }` - which is what makes the take *about* the meeting:
  - **Naming.** The invite's subject becomes the upload's `Title` **and** is pinned as `Recording.Name` (a follow-up
    `PUT /api/recordings/{id}/name`). Setting `Name` is the load-bearing half: `SummarizationProcessor` auto-names
    any recording whose `Name` is blank, so leaving it unset would have the model rename the meeting away from what
    the invite called it.
  - **Linking, at record time.** The upload is followed by `PUT /api/recordings/{id}/calendar-link` with the event's
    id and `calendarId` and **`manual: true`**. Everywhere else the link is *inferred* - `PickBest` scores the
    time-overlapping candidates, and the web app auto-saves the winner (`manual: false`) when the recording is first
    opened. Here the event id is known for certain, so the link is exact, lands immediately rather than on first
    view, and pulls the event's prep notes onto the recording through `LinkCalendar`'s `MeetingNoteAdoption` call.
    `manual: true` is deliberate: it is the user's own choice of meeting, and it is what stops the auto-matcher
    replacing it later - a take started on Join very often overlaps the meetings either side of it. The call is
    guarded like the rename (the audio is already uploaded; no calendar connected, or a since-deleted event, must
    never read as a lost recording - `LinkCalendar` 400s in both cases, and the meeting stays linkable by hand).
  - **Ending by itself**, when the user has opted in on Preferences → Recordings (`UserSettings.CalendarAutoStop*`;
    off by default, and applying **only** to a calendar-started take - the Record button is untouched). Two
    independent conditions, whichever comes first: an absolute stop at **invite end + N minutes** (folded into the
    recorder's existing schedule watcher via `earlierStop`, so the user's own auto-stop choice still wins if it is
    sooner, and `resolveCalendarStopAt` never returns a time already past - joining a meeting after its scheduled
    end would otherwise stop the take on the watcher's first tick), and **N seconds of continuous silence**
    (`lib/silenceWatcher.ts`). The silence rule only arms **after sound has been heard** (`calendarRecording.ts`
    keeps `heardSound`), so a take started before anyone speaks is never killed at the outset, and it is suspended
    while the recording is paused - pausing disables the capture track, which reads as pure silence. The watcher
    owns its own `AudioContext` rather than reusing `HubLevelMeter`'s, whose lifetime follows what popover the user
    has open.
  - **Extending past the meeting's end, if people are still talking.** At the calendar stop time,
    `shouldPromptExtend` (`lib/calendarRecording.ts`) asks whether to prompt rather than just ending: it is the
    exact complement of the silence rule above, over the **same window** (`CalendarSilenceStopSeconds`), so the
    prompt and the silence rule share one definition of "the meeting is over" instead of two numbers that could
    disagree. When it fires, the recorder shows **Extend this meeting / Stop now** and raises an OS notification
    (the user is normally looking at Teams or Zoom, not Diariz). Left unanswered, the take simply keeps
    recording and the ordinary silence rule ends it once the room actually empties. Each **Extend this meeting**
    calls `extendedStopAt`, which **doubles** the next wait off the user's own `CalendarAutoStopAfterMinutes` (3, 6, 12,
    24 minutes by default) so a meeting that overruns by a long way stops re-prompting every few minutes. Whatever
    ends a take on its own - the schedule, the calendar's end, or silence - `stop(reason?: StopReason)` now
    surfaces **which rule did it** as a toast, so a self-ended recording never reads as a mystery; a user-pressed
    Stop passes no reason and stays silent.
  - **Replacing a running take.** Joining a second meeting while the first is still recording stops the first,
    which uploads and transcribes on its own, and only then starts the second - this happens **regardless** of the
    settings above. `start()` awaits the outgoing upload before touching any shared state, because `upload()` reads
    `pendingRoomRef`/`pendingSectionRef` and the live notes/screenshots **after** its first `await`; without the
    wait the finished recording would be filed into the new take's folder and lose its notes. The promise is
    published in `stop()`, not in `onstop`, since `MediaRecorder.onstop` lands on a later task.
- **Calendar-tab event overlay (Phase 2 feature):** the recordings **Calendar tab** (`nav/CalendarTab.tsx`)
  overlays the month's meetings from **every** source. The web app fetches
  **`GET /api/calendar/events?timeMin&timeMax`** (`CalendarController` → `ICalendarAggregator`, range-capped
  ≤62 days; empty list when nothing is connected) **once per viewed month** (React-Query keyed by month, short
  `staleTime`, Refresh link).
  - **Gated on the personal room only** - deliberately *not* on a Google connection any more. It used to be
    `calendarConnected && isPersonalRoom`, which gave anyone whose calendar was entirely `.ics` feeds (or, once
    it shipped, a desktop Outlook mirror) a permanently empty Calendar tab. The endpoint already degrades to
    `[]`, so the gate bought nothing and cost those users the feature outright.
  - **The sync controls live in the panel toolbar** (`ListToolbar`, shown when the Calendar tab is up in a
    personal room), not in the tab: **Sync calendar** and **Sync selected day**, two icons. The quick one
    reads the day selected in the Calendar tab, so the selection is owned by `RecordingsPanel` - the toolbar
    is the tab's sibling and has to see it. The day travels to the shell as `{ scope: "today", date }`
    alongside the scope rather than replacing it: web and desktop ship separately, and an older shell then
    ignores the date and reads today (what it always did) instead of falling through to a full-window sync. They used to be *Sync
    Outlook* + *Refresh events* links under the month grid, which read as the calendar's own chrome and
    scrolled with it. `lib/calendarSync.ts` owns one run for every source - and there is deliberately **no
    per-provider fan-out**, because Google and `.ics` are read **live** by `/api/calendar/events` (which skips
    whichever is not connected), so invalidating `["calendar-events"]` *is* their refresh. Desktop Outlook is
    the exception: it must be harvested and pushed first, so the run asks the shell (only when opted in and
    reachable), **waits for it to return to `idle`** - the first moment the server holds the new meetings - and
    only then refetches. `scope: "today"` narrows the shell's read to the local day: seconds against the tens a
    full mailbox read costs, which is what makes "the meeting I just accepted is missing" a two-second fix.
    Progress goes to the **app status bar** (`useStatus`), counting up each second and naming the scope, and
    clears when the run ends.
  - **`busy` is joined, not reported.** The shell refuses a second run while one is in flight, and the launch
    sync holds that for tens of seconds every time the app opens - so a `busy` refusal means "the calendar you
    asked for is already being fetched", not a failure. The run marks itself as under way and keeps waiting for
    `idle`, then refetches as if it had started the sync itself. Marking it is load-bearing: attaching to a run
    already in progress means the `idle` that ends it may be the **only** event that arrives, so a waiter that
    demanded a working phase first would sit out the whole 150s timeout. The buttons also disable on the
    **shell's** phase, not just their own run (the affordance the old *Sync Outlook* link had, whose loss is what
    made `busy` reachable at all), and that phase drives the status bar too, so a launch or tray sync is
    visible. A reader-level `busy` (Outlook itself mid-dialog) is distinguishable by ordering - it arrives after
    the shell has already been through `reading -> idle`, so the waiter has settled - and the shell raises its
    own notification for those. The Calendar tab keeps its own `onOutlookState` listener for syncs it did not
    start (the tray's, and the one on launch).
  Pure client helpers (`eventDayKeys`/`dayItems` in `lib/calendar.ts`) colour the grid (event-only days a
  darker green, an events dot on recording days) and build a **merged, time-ordered day list** of meetings +
  recordings - **deduped**, so a linked recording and its meeting show as one row (both icons). Each event is
  **tinted its Google calendar colour** with the calendar's name shown, and a linked recording's calendar icon
  (list + tab) is tinted the same; the web threads `calendarId` through the link calls so linking targets the
  exact calendar. Clicking a
  meeting **that has no recording** opens an **event preview** (route `calendar-event/:eventId`,
  `pages/CalendarEventDetail`): a single Overview tab reusing `CalendarEventDetails`, plus **Link a recording**
  (`LinkRecordingModal`) - the inverse link that attaches an existing recording to the meeting and navigates to
  it. Read-only against the calendar; the only write is the calendar-link `PUT` above.
- **External `.ics` calendar feeds (Phase 3 feature):** users can subscribe to external iCalendar feeds (public
  team/shared calendars, or any ICS URL not reachable through Google) so their events show up alongside the
  Google calendars, tinted with a per-feed colour. Storage is a per-user **`IcsCalendarSource`** entity/table
  (name, https URL, colour, enabled flag, last-fetch status; cascade with the user). Two pure, unit-tested
  helpers do the work: **`IcsCalendar`** parses+maps an ICS document into `CalendarEvent`s via **Ical.Net**,
  expanding recurrences within the window and tagging each event `ics:{sourceId}`; **`IcsUrlGuard`** is the SSRF
  gate (https-only; blocks loopback/private/link-local/CGNAT/multicast literals). **`IcsCalendarClient`**
  (`IIcsCalendarClient`) fetches each of a user's **enabled** feeds behind that guard - a named http client with
  auto-redirect **off** so every hop is re-checked against the **resolved IPs**, plus size (5 MB) and time (12 s)
  caps - parses them, and merges the events (ids prefixed `ics:{sourceId}:` so they stay unique across feeds),
  recording each feed's `LastFetchedAt`/`LastError`. A single broken/unsafe feed is skipped, never fatal. The
  **`CalendarController.Events`** read now returns **Google events merged with ICS-feed events** (either source
  may be empty - a user with only `.ics` feeds and no Google still gets a populated tab). Feeds are managed via
  **`CalendarFeedsController`** (`/api/calendar/feeds`, JWT, owner-scoped): `GET` list, `POST`/`PUT`
  (name/url/colour/enabled - the URL is validated and **test-fetched** via `ProbeAsync` before it's stored, so a
  broken/unsafe URL is rejected up front; ≤20 feeds/user), `DELETE`. Events are always fetched **live** and never
  stored. **Ical.Net** (MIT) is a new API dependency. Users manage their feeds in **Preferences → Calendars**
  (`CalendarFeedsSection`: add/rename/recolour/enable/remove, with the last-fetch error surfaced per feed).
  Feed events are **first-class since `ICalendarAggregator`**: they participate in recording↔meeting matching
  and linking, and resolve by id for the detail view. (Both were previously Google-only, so a feeds-only user
  got a populated Calendar tab and nothing else - see the aggregator bullet below.)
- **`ICalendarAggregator` - one calendar, three sources:** `CalendarAggregator` composes
  `IGoogleCalendarClient` + `IIcsCalendarClient` + `IOutlookCalendarStore` and is the **single** place they are
  combined. Before it, the merge lived inline in `CalendarController.Events`, so only the Calendar tab ever saw
  all of them: `RecordingsController` called the Google client directly and gated on `GoogleCalendarGranted`,
  which meant a feeds-only user **could not have a recording matched at all**, and
  `CalendarController.Event` asked only Google, so an `.ics` event's detail fetch **404'd**. Both are fixed by
  construction here.
  - `ListEventsAsync` is the old inline merge, lifted unchanged (Google null = not connected, the others still
    contribute). `GetEventAsync` **routes by the id's scheme** - `outlook:` to the store, `ics:` resolved out of
    a feed listing (a feed is parsed wholesale, so there is no by-id fetch), anything else to Google.
    `HasAnySourceAsync` answers "do you have a calendar at all" **from the database** (a Google grant, an enabled
    feed, or an enabled Outlook device) rather than probing the clients, since it only gates whether to offer a
    match and must not cost a Google round trip plus every feed fetch.
  - **`PickBest` moved** to a pure `CalendarMatching`. It was never Google-specific, but living on the Google
    client meant only Google events could ever be scored by it. `CalendarEvent` carries no provider marker, so
    every source is matched identically - and all-day entries are still skipped whatever their origin.
  - **One failure is still surfaced rather than degraded.** `FetchAsync` returns a `CalendarFetch` carrying
    `GoogleUnavailable` (granted, but no token - a revoked/expired refresh token). `CalendarMatch` reports
    "reconnect Calendar in Preferences" **only when Google is the user's sole calendar**; with a feed or an
    Outlook device also connected, those legitimately answer and an error would be wrong. Everything else
    degrades quietly, because a revoked Google token is the one calendar failure a user can actually fix.
  - **Recurring events (`Recurring`/`SeriesId`).** `CalendarEvent` also carries whether an occurrence belongs to
    a repeating series and which one - computed per source, keyed differently, and never compared across
    sources: Google's master event id (the shared prefix an expanded occurrence's own id gets), an `.ics` UID
    (the same convention), and for Outlook the mirrored event's `Uid` up to its `#{start}` occurrence suffix.
    `RecordingCalendarLink.SeriesId` **copies** whichever value was current when the link was made rather than
    being re-derived from the live calendar on each read: the Outlook mirror is a **rolling window** (see below),
    so an occurrence linked last month has already been swept out of it, and a live join would silently return no
    series for exactly the history the endpoint below exists to show. The UI shows a **Repeats** badge on a
    recurring event; `SeriesId` **is** returned to the browser like any other `CalendarEvent` field (no
    `[JsonIgnore]`, no custom serializer - it is the user's own calendar data, and it is what the n8n node and
    MCP legitimately need). The **web client** just does not model it: `lib/types.ts`'s `CalendarEvent` carries
    only `recurring?: boolean`, because the sibling lookup below is resolved server-side from the event id, so
    the browser never needs to hold the opaque series key itself.
- **`GET /api/calendar/events/{eventId}/recordings` - a recurring meeting's history (`CalendarController`).** For
  an event that carries a `SeriesId`, the caller's **other recordings of that same series**, newest occurrence
  first, capped at 10, never including the occurrence asked about - matched on `RecordingCalendarLink.SeriesId`
  (owner-scoped by `Recording.UserId`) rather than re-derived from the calendar, for the rolling-window reason
  above. Returns `[]` for a non-recurring event or a series never recorded before (not an error); 404 when the
  event itself is gone. Returns `SeriesRecordingDto(Id, Title, Name, StartsAt, EndsAt)`. Powers the **"Earlier
  recordings of this meeting"** list on both the calendar event page and the recording's **Calendar Event**
  section (`components/SeriesRecordings.tsx`, rendered by `pages/RecordingDetail.tsx` alongside
  `CalendarEventDetails`).
- **Desktop Outlook mirror (Phase 4 feature - storage layer):** a third calendar source, and the only one that
  **inverts the fetch model**. Google and `.ics` live on the internet, so the API pulls them live at read time
  and stores nothing (an invariant `RecordingCalendarLink`'s doc comment calls out explicitly). A classic
  desktop Outlook calendar lives on the user's PC and is reachable **only from the Windows desktop shell**, so
  its events are **pushed** to the API and **persisted** - which is what makes them work in a plain browser and
  after the desktop app is closed.
  - **Storage.** `OutlookCalendarSource` is keyed **per (user, device)** (unique `(UserId, DeviceId)`), not per
    user: two PCs against two mailboxes are independent mirrors, or each machine's sweep would delete the
    other's events every launch. `OutlookCalendarEvent` holds **flattened occurrences** (Outlook expands series
    itself - no master row, no recurrence rule); cancelled appointments are never stored.
  - **Identity.** `OutlookEventId.For(sourceId, uid)` = first 16 bytes of `SHA-256(sourceId ‖ uid)` with
    RFC-4122 bits set, exposed as `outlook:{guid}` = **44 chars**. Deterministic so an occurrence that leaves
    the rolling window and returns keeps its recording link and pre-meeting notes; **short** because
    `MeetingNote.CalendarId`/`EventId` are `varchar(256)` and `CalendarEventNotesController` clamps writes at
    256 while reading raw - a raw `GlobalAppointmentID` (routinely >256 chars) would save a note under a
    truncated key and never read it back. The uid itself is `GlobalAppointmentID`, chosen over `EntryID`
    (unstable across moves/stores) and `CleanGlobalObjectId` (identical for a whole series).
  - **The push contract.** `POST /api/calendar/outlook/sync`, **`[Authorize]` with the user's own JWT** -
    deliberately *not* the `internal/` + `X-Worker-Secret` pattern, which is a server-to-server credential that
    would leak to every installer if embedded in a desktop binary. This mirrors the **screenshot** precedent:
    the shell harvests, the renderer POSTs, `main.js` never holds a token. The desktop sends the **whole
    window** and the **server reconciles** (upsert present, delete absent), because the desktop has no
    trustworthy view of server state - a restore from backup, a second machine, a reinstall or a
    disable/re-enable all silently stale a local mirror, and every one produces *missing events*.
  - **The sweep, and its guards.** Rows are stamped with the run's `SyncId`; on the page marked `final`, and
    **only when `complete == true`**, in-window rows carrying a stale `SyncId` are deleted. Scoping is
    **structural, not marker-based** - the table holds only rows this sync created for this source, so unlike a
    mirror written into someone's real calendar there is nothing else it could hit. A partial read
    (`complete: false`) never sweeps, a run that never reaches `final` degrades to upsert-only, and the sweep is
    bounded by the window the run covered so a narrower run never deletes history outside it. Limits: 500
    events/page (413), uid ≤400 chars, window ≤730 days, body capped at 8000 chars, and a per-device run
    cooldown (409) that exempts later pages of a run already in flight. The cooldown is **scoped by window
    width**: **60s** for a full run, **10s** for a narrow one (≤ 2 days - the desktop's "Sync today"), each
    against its own stamp (`LastSyncedAt` / `LastNarrowSyncedAt`). One shared stamp would have refused the quick
    sync in the one moment it exists for - seconds after a full sync finished without the meeting the user is
    looking for. Preferences shows the later of the two as "last synced".
  - **Timezone and all-day.** The desktop sends all-day dates as **local `yyyy-MM-dd` strings**, stored verbatim
    and never re-derived from the UTC instant (re-deriving is the classic off-by-one - an all-day 2026-03-15 in
    Europe/London is `2026-03-14T23:00:00Z`). Windows zone ids convert server-side via
    `TimeZoneInfo.TryConvertWindowsIdToIanaId` (ICU-backed, no CLDR table to ship), falling back to the device's
    own zone rather than leaving the field null.
  - **Reading back.** `IOutlookCalendarStore` is shaped like `IIcsCalendarClient` and projects rows into the
    **existing** `CalendarEvent` record, so nothing downstream knows the source; the join URL stands in for
    `HtmlLink` (a local appointment has no web permalink) and attendee response statuses use **Google's**
    vocabulary so every existing renderer works unchanged. *(Wiring it into `CalendarController` and
    recording↔meeting matching is the following change.)*
  - **Privacy.** `UserSettings.OutlookSyncEnabled` is the opt-in, **default false**: the push 403s until it is
    set, so an installed desktop app stores nothing on its own. `SkipPrivate` (default on) drops private items
    **on the machine**; a private appointment's body is stripped server-side regardless of `IncludeBody`.
    Disconnecting a device deletes its stored events by cascade, and setting `OutlookSyncEnabled` to **false**
    through `PUT /api/user/settings` **purges every device** (and, by cascade, every event) - the one field on
    that endpoint with a side effect, because a privacy switch that left what it had gathered on the server
    would not be one.
  - **Web surface.** `OutlookSyncSection` is a Preferences tab (`PreferencesTab` gains `"outlook"`), shown to
    **everyone** - a browser user must be able to read what it does, see which machines are syncing, and
    revoke - with only the "Sync now" button gated on `canSyncOutlook() && outlookAvailable()`. The bridge is
    `lib/outlookSync.ts` (structural typing of `window.diariz` with no-op fallbacks, the `trayScreenshots.ts`
    pattern, so nothing branches on `isElectron`) plus `lib/outlookPush.ts` (`pushWindow` pages at 250, marks
    `final` on the last page **only**, and **aborts on first failure** - carrying on after a gap would let the
    server reach a final page having never seen the missing events and sweep them as cancellations).
    `OutlookSyncBridge` renders null and is mounted in `WorkspaceLayout`, not in Preferences, because a sync
    fires on launch and from the tray - neither of which opens the settings window. Its `reportOutlookReady`
    doubles as "a signed-in renderer is ready to POST", which is what licenses the shell's launch sync: the
    shell cannot use app-ready for that, since the user may not be signed in yet.
  - **The reader (desktop shell).** A **self-contained .NET console exe**,
    `apps/desktop/native/Diariz.OutlookReader`, published to `native/publish` and shipped via electron-builder
    `extraResources` to `resources/outlook/`. It takes `--start/--end/--max/--no-body/--include-private`, plus
    `--probe`, and writes **one JSON document to stdout**; `outlookHost.js` spawns it with a 120 s timeout
    (15 s for a probe) and parses that.
    - **Why a separate process, not a native Node module.** An in-process COM binding (`winax`) is broken from
      Electron 41 up - it compiles against raw V8 headers with no N-API, so it breaks every Electron major - and
      would have made this the shell's first native dependency. Beyond that, COM is synchronous: a
      1,200-appointment read takes tens of seconds and would freeze the tray, the recorder IPC and the
      screenshot hotkey, possibly mid-meeting. A process boundary also means a hung or crashing COM call kills
      the helper, not Diariz. The shell keeps **zero native dependencies** (guarded by
      `outlookPackaging.test.js`).
    - **Presence is answered from the registry, never by activating Outlook** (`OutlookPresence.Detect()`,
      and `--probe` exposes it on its own). `Outlook.Application` is registered by **Office as a whole**, so it
      is present on a PC with Word and Excel but no Outlook, and on one migrated to the new Outlook - and
      activating it there does not fail, it hands the request to Windows Installer, which pops up an *install
      Outlook* dialog. Diariz syncs on launch, so those users met that dialog **every launch**. The probe
      resolves `Outlook.Application` → CLSID → `LocalServer32` (and the `App Paths\OUTLOOK.EXE` entry) in
      **both registry views** - a 32-bit Office on 64-bit Windows registers under `Wow6432Node`, and the reader
      is x64 - and requires the executable to **exist on disk**; `Read` consults it before it ever creates the
      COM object (guarded by `outlookPackaging.test.js`). `main.js` **remembers** a definitive "no"
      (`outlookUnavailableReason` in `electron-store`, for the sticky reasons `not-installed` / `new-outlook` /
      `unavailable` only - never a transient `busy`/`timeout`/`denied`), so even the probe stops running; the
      **Check again** button on the Outlook card in Preferences (`outlook:recheck`) is the only thing that
      clears it. Availability resolves through one shared promise, because the probe is a subprocess and the
      renderer's `outlook:ready` - which licenses the launch sync - routinely arrives while it is still running.
    - **How it reads.** Late binding only (`Type.GetTypeFromProgID` + `InvokeMember`), so no interop assembly
      and no Outlook version coupling. `IncludeRecurrences = true` then `Sort("[Start]")` then `Restrict(...)`
      - the order matters, as Outlook only expands a series once the collection is sorted by start - so the
      helper never interprets a recurrence pattern itself. It **never calls `Application.Quit()`**: releasing
      the references leaves an Outlook the user had open exactly as it was. COM *will* start Outlook if it is
      closed.
    - **Three bugs only a real calendar exposed**, all of which would have failed solely on a user's machine:
      `InvariantGlobalization` makes `GetCultureInfo("en-US")` throw, so the `Restrict` date format is built
      explicitly; `TimeZoneInfo.Local.Id` is a **Windows** id on Windows, so the server converts the
      device-zone fallback rather than storing it verbatim in an IANA column; and the bracketed
      `[Start] < '...'` filter parses its dates in **Outlook's** locale while this process formats them in its
      own, so a US-ordered date handed to an en-GB Outlook silently stopped constraining anything and
      `Restrict` returned the whole folder (asking for two months yielded hundreds of appointments from six
      months earlier, and none of the recent ones). The filter is now **DASL with ISO-8601 dates**, which is
      locale-independent. `Sort("[Start]")` must also come **before** `IncludeRecurrences`, not after.
    - **`GlobalAppointmentID` is NOT per-occurrence**, contrary to the assumption inherited from the reference
      implementation: a real calendar returned 87 recurring occurrences sharing 24 ids - one per series. Since
      the server keys a row on `(source, uid)`, occurrences would collapse into a single row and overwrite each
      other every sync. `normalizeAppointment` therefore qualifies a recurring occurrence's uid with its start.
      Done there rather than in `dedupeUids` for stability: dedupe keeps the *first* sighting bare, so an
      occurrence's id would depend on where the rolling window began and would change as it rolled, orphaning
      its recording link and pre-meeting notes.
    - **Size.** Self-contained is required (a consumer installer cannot demand a .NET runtime) and trimming must
      stay **off** (late-bound COM is entirely reflection), so the payload is compressed instead: 73 MB → 37 MB,
      taking the Windows installer to ~131 MB.
    - **Errors** are reported as machine-readable reasons - `not-installed`, `new-outlook`, `busy` (retried once
      on `RPC_E_CALL_REJECTED` / `RPC_E_SERVERCALL_RETRYLATER`), `denied`, `timeout`, `error` - which
      `describeComError` turns into copy. `new-outlook` is deliberately distinct: the new Outlook exposes no COM
      at all, and it is the case a growing share of Windows users will hit.
    - **Triggers:** the once-per-launch sync (on the renderer's `outlook:ready`), the tray item
      (`trayOutlookItems`, hidden unless the reader is present *and* the user opted in), and `outlook:sync-now`
      from the web app. A 60 s per-machine cooldown (`shouldStartSync`) sits in front of the server's own.
    - **Packaging/CI:** `npm run build:outlook` runs `dotnet publish`, and `dist`/`publish` depend on it so a
      stale reader can never be packaged; `desktop-release.yml` gains a `setup-dotnet` step plus a verify step
      that fails legibly rather than shipping an installer whose sync could never find a reader. The Windows
      target is pinned **x64**, matching the reader's RID.
- **Isolation:** every recording/section/chat/voiceprint query filters by `UserId` from the JWT
  `NameIdentifier` claim. **Storage quotas** (audio bytes) are per-user: the Platform Administrator sets the
  starter + maximum (`PlatformSettings`), any admin can raise an individual user up to the max.
- **Audio retention (auto-delete).** An opt-in `PlatformSettings` policy (`AutoDeleteAudioEnabled` + `AudioRetentionDays`
  + `AudioDeletionTimeOfDay`, edited on Settings → Storage Quotas) drives a nightly `AudioRetentionWorker` (a singleton
  `BackgroundService`). At the configured **server-local** time it opens a DI scope and, when enabled, runs the pure
  `AudioRetentionSweep` over recordings older than the window that are **fully transcribed** (status Transcribed/Summarized/
  Summarizing) and **not protected** - deleting each audio blob, stamping `Recording.AudioDeletedAt`, and zeroing `SizeBytes`
  (the transcript and all metadata are kept). It reuses the same delete-audio recipe as the manual `DELETE /api/recordings/{id}/audio`.
  **Off by default** - no audio is removed until an admin turns it on. The Platform Administrator can also trigger the same sweep
  on demand via `POST /api/platform/settings/run-audio-retention` (a "Run now" button on the Storage Quotas tab), which runs it
  immediately using the saved window regardless of the toggle. A recording is exempted via `PUT /api/recordings/{id}/audio-protection`
  (stamps `Recording.AudioProtectedAt`); while protected, both the nightly job and the manual delete-audio action skip/refuse it.
  The recording detail (`GET /api/recordings/{id}`) surfaces a **computed** `AudioScheduledDeletionAt` (`CreatedAt` + retention days,
  non-null only when auto-delete is on and the recording is a live, unprotected, eligible candidate) so the Overview can show
  "Protected from audio deletion" or "Audio will be deleted on {date}".
- **Self-service profile:** every user has a **Preferences** modal - a **vertical-tabbed** window (Profile,
  Google Account, Calendar Feeds, Claude Access, Voice Prints; the former standalone "People" voiceprints modal
  is folded in as **Voice Prints**), headed by the user's avatar + name. The **Profile** tab edits the
  **display name** (`PUT /api/user/profile` → updates `ApplicationUser.FullName` and re-issues the token so the
  name claim updates without a re-login), the **native / UI language**, free-text profile fields (job title,
  company, job/company descriptions, LinkedIn), and the **colour theme** - all stored on `UserSettings` (theme as
  the `ThemePreference` int enum, surfaced as `"auto"|"light"|"dark"`). The theme is **server-persisted** so it
  follows the user across devices: `<ThemeSync>` adopts `profile.theme` on load, with localStorage as the pre-auth
  cache (the theme picker moved out of the account menu). The supported languages come from a shared list at
  **`GET /api/languages`** (anonymous, so the signup page offers a language selector too). This underpins the
  localization & translation feature.

## People and speaker identification (voiceprints)

A **`Person`** is someone who appears in meetings: a name plus optional contact details (`Title`,
`CompanyName`, `Email`, `Phone`) and an `IsInternal` marker, with the **voiceprint as an optional
attribute**. A person added by hand, or one who set `VoiceprintOptOut`, has a null `Embedding` and is simply
skipped by identification. Enrol a person once (from a recording's speaker) and Diariz recognises that voice
in future recordings: the centroid is the L2-normalised mean of its **`VoiceSample`** snapshots, and matching
is a pgvector cosine distance restricted to people who have an embedding and have not opted out.

**Every user account is also a person.** `IPeopleDirectory` (`Services/PeopleDirectory.cs`, modelled on
`RoomScope.PersonalRoomIdAsync` down to the find-or-create race handling) provisions the linked `Person` on
demand and keeps its name and email following the account — including fanning a rename out onto every
`Speaker` already identified as them, because `Speaker.DisplayName` is denormalised rather than joined. It
also owns `RecomputeVoiceprintAsync`, which rebuilds the centroid and sample count from the remaining voice
samples and is called after a recording delete, since that cascade silently removes training data.

> **The `Person`/`VoiceSample` types map to the `SpeakerProfiles`/`ProfileContributions` tables**, and
> `Person.CreatedByUserId` to the `"UserId"` column. Renaming those would be a destructive rename, forcing a
> `MaintenanceController.CurrentFormat` bump that hard-rejects every older backup archive. Beware in raw SQL:
> `"UserId"` is who *enrolled* the person; `"LinkedUserId"` is the account the person *is*.

The **People** screen manages voiceprints — rename, view training samples, add/remove one (recomputes the
centroid), merge duplicates, and **erase** one or all (GDPR): erasing reverts auto-applied labels to the
anonymous label but keeps names typed by hand. See
[`Speaker_Identification_and_Verification.md`](Speaker_Identification_and_Verification.md).

### Platform scope, and what it costs

The directory and its voiceprints are **platform-wide**, not per-user. One human is one row, which is what
makes an erasure request a single delete rather than a hunt through every user's private set.

**State the consequence plainly:** a voiceprint enrolled by one user identifies that person in *every* user's
recordings, including private ones, and the directory lists every external contact the organisation has ever
recorded. `ManagePeople` mitigates the second half by gating **browsing** — but nothing gates the first half,
which is inherent to a shared directory rather than a permission problem.

Gates (`SpeakerProfilesController`):

| Operation | Requires |
|---|---|
| Browse / list the directory | `ManagePeople` |
| Search by name to label a speaker; assign; create | nothing (any authenticated user) |
| Rename, delete, merge | `ManagePeople` |
| **Set `VoiceprintOptOut`, erase a voiceprint** | `ManagePeople` **or the person is you** |
| Erase every voiceprint | `ManagePlatform` |

That self exception is `CanManageBiometricsAsync`, kept as a **single predicate** used by both endpoints (and,
from PR 3, projected onto the DTO the UI renders from) so the two cannot drift. Under GDPR, withdrawing
consent to process your own biometric data is the data subject's right, so routing it through an
administrator would be a weak posture.

**Opt-out semantics** (`IPeopleDirectory.EraseVoiceprintAsync`) are deliberately narrower than deleting a
person: the centroid and every `VoiceSample` go, labels that automatic identification applied revert to the
anonymous label, but **names typed by hand are kept, still linked to the person** — those are the user's own
assertion about who was in the room, not something derived from the biometric. Turning opt-out back off
restores nothing. `SpeakerIdentifier` filters `Embedding != null && !VoiceprintOptOut` before the distance
query, and `AssignSpeaker` still *names* an opted-out person but skips the training block.

**Performance note — measured, and deliberately left alone.** The cosine query is a **sequential scan** over
every enrolled person, and always was: there is no HNSW/IVFFlat index on `SpeakerProfiles.Embedding`. It
scales linearly at roughly **0.35 ms per 1,000 people** (measured on `pgvector/pgvector:pg16`, 192-d, the
exact filter + order + limit the identifier issues):

| People | Plan | Execution |
|---|---|---|
| 250 | Seq Scan | 0.12 ms |
| 5,000 | Seq Scan | 1.9 ms |
| 25,000 | Seq Scan | 8.8 ms |
| 100,000 | Seq Scan | 35.0 ms |
| 100,000 + HNSW | Index Scan | 0.42 ms |

Identification runs **once per speaker per transcription**, so at a realistic directory size (hundreds to low
thousands) even a 24-speaker meeting costs tens of milliseconds in total. An index is not warranted, and the
reason is not only the 102 MB it would add against an 87 MB table: **HNSW is approximate**, so it can miss the
true nearest neighbour, and for threshold-based biometric identification a miss means a speaker silently stays
unidentified. That is a correctness trade-off, not just a performance one.

**Revisit past roughly 25,000 enrolled people**, where the scan crosses ~10 ms. It is an additive migration
whenever it is wanted, so deferring costs nothing.

### The People screen

`apps/web/src/components/PeopleModal.tsx` - a **modal**, not a route. The directory is nearly always
consulted *while reading a transcript* ("who is this speaker?"), so a route would throw that context away;
it opens from the account menu and from **Manage people** on the Speakers tab, and matches the shape of the
other account-menu modals. It is also not a Preferences tab, because a platform-wide directory is not a
personal setting.

Master-detail: search + filter chips over `GET /api/people`, `components/PersonEditor.tsx` beside it, and a
duplicates banner over `GET /api/people/duplicates` offering a one-click merge (never automatic).
`PreferencesModal` lost its `voiceprints` tab and `VoicePrintsSection` is deleted.

**Only the list scrolls.** The editor is a fixed panel, so a field never moves out from under the cursor as
the list beside it scrolls, and a long directory cannot push the form off screen. List rows are one line -
name plus a voiceprint marker - so a large directory stays scannable.

The `q` parameter on `GET /api/people` matches **name, email or company**. `GET /api/people/search`
deliberately does **not** match company: it feeds the speaker-assign typeahead, where a company match would
offer up every colleague at the same employer when you typed a firm's name.

The editor renders the two permission rules **differently on purpose**, and the difference is load-bearing:

- **Opt-out is always shown**, with the person's real value, merely `disabled` when the viewer may not
  change it - a viewer needs to see that someone opted out even when they cannot alter it.
- **Erase voiceprint is hidden entirely** - an action you cannot perform should not advertise itself.

Both read `PersonDto.CanManageBiometrics` (the server's answer) rather than recomputing `ManagePeople ||
IsSelf`. `toggleOptOut` **also guards in the handler**: the `disabled` attribute is presentation, and a
programmatic click still reaches the handler, so relying on it alone would make the permission a styling
detail. `PersonEditor.test.tsx` pins all of this, because the asymmetry is exactly the kind of thing a
later tidy-up collapses into one branch.

### The People API

`PeopleController` at **`/api/people`** replaces `api/speaker-profiles`, which is deleted. Replacement rather
than deprecation: this is 0.x with no external consumers, and two surfaces would double the n8n node's
operation list and drift.

| Route | Gate |
|---|---|
| `GET /api/people` (`?q=&internal=&hasVoiceprint=&take=&skip=`) | `ManagePeople` |
| `GET /api/people/search?q=` | **none** |
| `GET /api/people/{id}`, `GET /api/people/duplicates` | `ManagePeople` for duplicates |
| `POST /api/people` (optionally enrols in the same call), `PUT /api/people/{id}` | edit needs `ManagePeople` unless it is you |
| `DELETE /api/people/{id}`, `POST /api/people/{id}/merge` | `ManagePeople` |
| `POST`/`DELETE /api/people/{id}/voiceprint`, `DELETE .../voiceprint/samples/{sampleId}` | `ManagePeople` or it is you |
| `DELETE /api/people/voiceprints` | `ManagePlatform` |

**`search` is ungated on purpose, and the reason is a bug this fixed.** The recording page used to prefetch
the whole directory to fill the speaker-assignment picker. The moment listing the directory required
`ManagePeople` (0.164.0), a user without it opened a recording to an empty picker and could not name a
speaker at all. Naming a speaker in your own meeting is not an administrative act, so it has its own open
endpoint, and `SpeakerAssign` now queries it as you type. `RecordingDetail.test.tsx` carries a guard
asserting the page never calls the gated listing.

`PersonDto` carries **`CanManageBiometrics`** - the server's own answer to `ManagePeople || IsSelf`. Clients
render the opt-out and erase controls from that flag rather than recomputing the rule, so the two cannot
disagree.

`PersonDuplicates` (pure, static) reports likely duplicates by email and normalised name — two users who each
enrolled the same colleague privately now both appear. It **never merges automatically**: merge destroys the
source row, has no undo, and under a shared directory a bad one damages everyone's recordings.

**Suppression is by the reported set, not by the people in it.** The original implementation removed people
from the name pass once they had appeared in an email group, so a weak email coincidence silently destroyed a
strong name match: in production one person had been given an email address belonging to someone else's
account, which grouped those two and thereby made a genuine same-name pair **impossible to report at all** —
found only by editing the email to break the coincidence. One person may therefore appear in several
suggestions; each is a separate claim about a separate pair, which is also why the UI can dismiss one.

**Merge salvages the source before deleting it**, which is not optional bookkeeping. `LinkedUserId` moves to
the survivor if the survivor has none: losing it detaches a real account from the directory, and because the
biometric self-exception resolves through `LinkedUserId`, that user silently loses the ability to opt
themselves out of voice-printing — a GDPR right, failing closed with nothing on screen. Contact fields move
the same way but only into gaps (the survivor's own values win), so a merge never destroys a detail that
existed a moment earlier. **Two linked people cannot be merged at all** (400): two accounts are two humans,
and there is no correct answer to which link survives. Both rows briefly hold the same `LinkedUserId`, which
the filtered unique index would reject if the UPDATE preceded the DELETE — EF orders the delete first, and
`PeopleSchemaTests` proves that against real Postgres, since the in-memory provider enforces no index.

**The n8n trigger owns the contacts toggle.** `WebhookSubscription.IncludeAttendeeContacts` is a server-side
per-subscription flag, but an n8n-created subscription is owned by the **Diariz Trigger node**, which deletes
and re-creates it on every publish. A value set through the Diariz UI was therefore wiped whenever the
workflow was edited - observed live: contacts stopped arriving with nothing in Diariz having changed. The
node now carries an **Include Attendee Contacts** option (default off), sends it on every registration, and
**compares it in `checkExists`** - without that comparison n8n would never call `create()` on an existing
subscription, so toggling the option would do nothing.

**Duplicate suggestions can be dismissed for the sitting** - `PeopleModal` holds a `Set` of group keys in
component state, so closing the modal restores everything. Keyed on the **people in the group**, never its
index: a merge reorders the list, and an index-keyed dismissal would then hide the wrong pair. Deliberately
not persisted - a pair dismissed today becomes a genuine duplicate the moment someone fills in the missing
email address, and a permanent hide is not a decision to take from a banner with one click and no undo.

**`EditPersonModal`** opens one person's details from the speaker they were identified as — the same
`PersonEditor` the directory uses, so there is no second set of validation rules. Gated on `ManagePeople` at
the control that opens it. Unlike every other modal here it **does not close on a backdrop click or on
Escape**: half-typed edits are lost by a stray click and cannot be recovered, so the only ways out are Save
and Cancel.

`PersonEditor` takes two opt-in props for this caller. **`onSaved`** fires after a successful save: the modal
closes on it (one person is a whole task; the directory deliberately stays open because you work down a list),
and `RecordingDetail` invalidates `["recording", id]` on it — the speaker rows and contact card render from a
**snapshot of each person carried in the recording payload**, so without that they kept showing pre-edit
details until a different row was clicked. **`showDestructiveActions={false}`** hides erase-voiceprint and
delete-person: directory-scale acts with no undo do not belong beside a job title, in front of someone who
came to correct a spelling.

**`SpeakerContactCard` renders for any identified speaker**, including one whose record holds nothing but a
name - which is most of them, since enrolling a voice creates a person with no contact details at all. It
originally required at least one detail before rendering, so exactly those people got nothing; and because
`isInternal` is never null for them, what did render was a thin box repeating the name and marker from the row
above. Both read as "no card". It now states that no details are recorded and offers to add them
(`ManagePeople`, into `EditPersonModal`), and `contactSummary` says the same rather than echoing the row.

It is also the only place inside a transcript where a person's email address and phone
number are reachable; it renders above the selected speaker's segments, with `mailto:`/`tel:` links (the
`tel:` href strips the spaces a readable number is written with). Its `contactSummary` helper also fills the
Speakers-row chip's tooltip, so the two renderings of one person cannot drift apart. Both render nothing for
an anonymous speaker, a multi-speaker slot, or a person carrying nothing but a name.

**Merging is confirmed through `MergePeopleDialog`**, not a `window.confirm`. The consequences differ per
pair (a voiceprint may move, contact fields may be filled, an account link may transfer), so the explanation
is computed from the two records rather than written as static copy; the **direction is swappable**, because
the survivor keeps its own values and only fills gaps. A pair where both records are linked is refused in the
UI with the reason, matching the server's 400 rather than discovering it after committing.

**Provisioning has three call sites, not one.** `CompleteSetup` covers the invite path, `Grant` covers a
Google-linked account (activated on the spot, so it never sees a setup link), and **`Login` self-heals** —
the only path every user takes, and therefore the only one that can repair an account the directory has
already lost. The equivalent call on `GET /api/people` is behind `ManagePeople`, which an affected user is
the least likely to hold.

## Localization (web UI)

- The interface is localized with **react-i18next**. Strings live in JSON catalogs at
  **`apps/web/src/locales/<lang>/<namespace>.json`** (namespaces: `common`, `auth`, `account`, `recordings`,
  `workspace`, `chat`, `admin`, `people`, `tour`), with **English authoritative** and **Spanish/French/German**
  shipped. Catalogs are **auto-discovered** (`lib/i18n.ts` via `import.meta.glob`), so adding a language is a
  **data-only** change.
- `LanguageProvider` (`language.tsx`) resolves the active locale by **`?lang=` → stored preference →
  `navigator.languages` → `en`** (`resolveLanguage`), sets `<html lang>`/`<html dir>` (RTL), and persists the
  choice (`diariz.language`). The picker lists the languages with a shipped catalog (`uiLanguages`); the
  full ~50 (`languages.json`, mirroring the API's `GET /api/languages`) remain available as *content*
  translation targets. Missing keys fall back to English.
- **Merge gate:** `src/locales.test.ts` asserts every catalog mirrors `en`'s keys exactly (no missing/empty),
  and `scripts/check-single-locale.mjs` (a CI job) limits a *translation-only* PR to one non-`en` language.
  See `apps/web/src/locales/README.md`.
- **Server-side exports.** The headings in **downloaded** transcripts (`TranscriptFormatter` — txt/md/rtf) and
  the **emailed** transcript (`TranscriptEmail`) are localized too, from runtime JSON at
  **`src/Diariz.Api/locales/<lang>/exports.json`** read by a tiny **`JsonExportLocalizer`** (`IExportLocalizer`,
  not compiled `.resx`; the files are copied next to the app). The endpoints resolve the recording owner's
  **`UserSettings.UiLanguage`** and pass an `ExportStrings` to the (pure) formatters, which default to English.
  Transcript *content* already uses `EffectiveText` (translated when the user translated).

## Help system (user documentation)

- **Two surfaces, one content set.** A browsable **`/help`** page (grouped article tree + client-side
  search, the same page shape as `/release-notes`) and **contextual help**: a `?` button beside a feature
  that opens a short popover with a deep link to the full article. Both read the same Markdown files, and
  the popover renders the article's own `summary` field, so the brief and full explanations cannot drift.
- **Content is bundled, not served.** Articles are Markdown files at
  **`apps/web/src/content/help/<locale>/<slug>.md`**, loaded by `lib/help/content.ts` via
  `import.meta.glob(..., { query: "?raw", eager: true })` - the same auto-discovery idiom as the i18n
  catalogs, so adding an article is a file drop with **no code change** and no API/DB surface. They live
  under `apps/web/` because the web image's Docker build context is that directory (the repo-root `docs/`
  folder is **not** visible to it).
- **Front matter** is a deliberately non-YAML `key: value` block (`title`, `summary`, `group`, `order`)
  parsed by `lib/help/parseArticle.ts`, so there is no parser dependency and content cannot grow structure
  the loader does not understand.
- **English-only prose, translatable structure.** `findArticle` resolves the requested locale and falls
  back to `en`, so a `de/` folder can be added later without code changes. Only *chrome* (nav labels,
  search box, buttons) lives in the i18n `help` namespace - long-form prose in JSON catalogs would be
  unreviewable, and `locales.test.ts` key parity would force four-file edits per doc change.
- **Routing.** `/help` and `/help/:slug` are **public** top-level routes (siblings of `/release-notes`),
  outside `WorkspaceLayout`. `HelpProvider` is therefore mounted in **`main.tsx`**, not `WorkspaceLayout`,
  so `?` buttons work on the standalone pages too; it renders one popover into `document.body` via a
  portal at `z-[70]`, above modals (`z-50`) and the tour overlay (`z-[60]`). nginx's SPA fallback already
  covers the deep links, so no infra change was needed.
- **Screenshots are co-located with the article** (`content/help/<locale>/images/*.png`) and referenced
  relatively (`![Alt](./images/x.png)`). Vite fingerprints assets, so the source path is not the build
  path: `lib/help/images.ts` globs the images with `query: "?url"` and rewrites each relative `src` to the
  emitted URL before rendering. A localised article uses its own screenshot when one exists and otherwise
  falls back to the English one. Absolute (`public/`) and external URLs are left alone. Files under ~4 KB
  are inlined as data URIs by Vite; larger ones are emitted separately.
- **Merge gates.** `content/help/helpContent.test.ts` asserts every article is **ASCII only** (naming the
  file, line, and offending character - this also enforces the no-em-dash rule mechanically), that each
  declares a title, a popover-sized summary, and a known group, that **every referenced screenshot exists**
  (a renamed or uncommitted image fails the build rather than shipping a broken picture), and that **every
  `<HelpButton topic="...">` in the source resolves to a real article**, so a dangling `?` cannot merge.
- **Not a fourth sync target.** The README Features table, `docs/features.md`, and the About-box
  `CAPABILITIES` are *inventories*; help articles are task-oriented "how do I / what happens if" prose for
  users in the app. They are different genres and are deliberately not kept in lockstep line for line.

## Audio storage & playback

- Original blobs live in **MinIO**; the **API streams them back itself** (same-origin) rather than handing
  out presigned URLs, so MinIO never needs to be browser-reachable. Playback uses HTTP **range requests**
  (`AudioStorage.OpenAsync` with a byte range) authorised by a short-lived token, so the `<audio>` element
  can seek. See [`Data_Schema.md`](Data_Schema.md) for the bucket/key layout.

## Cross-boundary contracts (the non-obvious glue)

- **Redis Streams, seven of them.** `transcription-jobs`/`workers` and `audio-merge-jobs` (both API → Python
  worker, sharing the `workers` group — the worker `XREADGROUP`s both streams and dispatches by stream key) and
  five API-internal streams with their own in-process consumers: `summarization-jobs`/`summarizers`,
  `meeting-minutes-jobs`/`minute-takers`, `actions-jobs`/`actions-extractors`, `embedding-jobs`/`embedders`
  (the RAG index), and `tag-cloud-jobs`/`tag-extractors` (the tag cloud). Job payloads are **PascalCase JSON**
  so .NET produces and Python/.NET consume without renaming. Keep `TranscriptionJob` / `TranscriptionResult` /
  `AudioMergeJob` / `Segment` shapes in sync across both languages.
- **Merge recordings.** `POST /api/recordings/merge` folds 2+ recordings into the earliest one: it builds a new
  transcription version on the survivor (`TranscriptMerger` lays the source transcripts end-to-end, offsetting
  timestamps and namespacing speakers) and **appends every source's action items** to the survivor. The summary
  is **not** merged (re-generate it). Recordings may have had their **audio deleted** — those contribute only
  transcript + actions. When **at least one** source still has audio, the survivor is set `Merging` and an
  `AudioMergeJob` is enqueued with only the **audio-present** blobs; the worker concatenates them with **ffmpeg**
  (libopus/WebM), uploads the combined blob, and calls back to `internal/recordings/merge-result`, which swaps
  the audio onto the survivor and deletes the now-merged source recordings (rows + blobs). When **no** source has
  audio, the merge **finishes synchronously** (no job) — the merged transcript/actions are already on the
  survivor and the sources are deleted. `merge-failure` flags the survivor and keeps the sources.
- **Worker → API callback** uses routes `internal/transcriptions/*` and `internal/recordings/merge-*`, both with
  the **`X-Worker-Secret`** shared header (not JWT). Not user-facing.
- **SignalR** hub `/hubs/transcription` requires JWT; clients auto-join a per-user group (group name = user
  GUID) so `RecordingStatusChanged` events are scoped per user.
- **pgvector is Postgres-only.** All vector matching sits behind `ISpeakerIdentifier`; unit tests fake it,
  integration tests exercise the real cosine query. Vector columns are mapped only when
  `Database.IsNpgsql()`; under the in-memory test provider they're `Ignore`d.
- **Pop-out notes window (window ↔ window, no server).** The desktop shell can open a second `BrowserWindow`
  at `{origin}/notes-popout` — a top-level React route deliberately **outside** `WorkspaceLayout` and
  `RequireAuth`, so it mounts no sidebar, no recorder and no SignalR, and holds no server data of its own.
  The two windows talk over a **`BroadcastChannel("diariz.live-notes")`**, which is origin-scoped, so being
  same-origin is the whole of the auth story. The **main window is the host**: it owns the recorder, the note
  lines, the capture stash and the recorded clock. The pop-out is a remote control — it sends
  `add`/`edit`/`delete`/`deleteShot`/`capture`/`changeArea` and receives a whole-state snapshot back. It
  **never stamps a timestamp**: `capturedAtMs` is pause-aware and produced only by the host. Screenshot
  thumbnails cross as `Blob`s; the full-resolution PNG never leaves the host. Two counter-intuitive rules,
  both measured (see `docs/superpowers/specs/2026-08-13-notes-popout-window-design.md`): the window is
  always-on-top at Electron's **default** level (verified to survive another app going full screen;
  `"screen-saver"` would also float it over the lock screen), and the **liveness poll runs from the pop-out**,
  because a main window hidden to the tray has its timers throttled to roughly 1 Hz — a host heartbeat would
  stall exactly when the feature is in use. Message *delivery* to a hidden host is not throttled. The pop-out
  has its own narrow preload (`notes-preload.js`); reusing `preload.js` would register a second
  `onTrayCommand` listener and a tray "Stop" would drive two recorders.

## GPU / worker notes

The worker pins a **CUDA 12.8 (cu128)** torch stack so it runs on Blackwell / RTX 50-series (sm_120). Three
non-obvious pins make whisperx 3.3.1 work (`ctranslate2==4.6.3`, `transformers==4.53.3` +
`huggingface_hub==0.35.3`, and a `torch.load(weights_only=False)` shim for pyannote checkpoints). The Hugging
Face pair is capped below `huggingface_hub` 1.0 / `transformers` 5.x: hub 1.0 dropped the shim that maps the
`use_auth_token` kwarg pyannote.audio 3.3.2 still passes to `hf_hub_download`. Diarization
is **gated on Hugging Face**: you must set `HF_TOKEN` and accept the pyannote 3.1 + segmentation-3.0 terms, or
jobs fail. CPU-only is possible (`DEVICE=cpu COMPUTE_TYPE=int8`, slow). Models load **lazily and are cached**
across jobs. Real working-set VRAM is ~9 GB during transcription (large-v3 + align + pyannote). See the README
for measured numbers and tuning (`WHISPER_MODEL`, `COMPUTE_TYPE`, `BATCH_SIZE`).

**Pluggable ASR backend (NVIDIA + AMD).** The Whisper transcription step is selectable via `ASR_BACKEND`:
`whisperx` (faster-whisper / CTranslate2 — the CUDA default) or `whisper` (openai-whisper, pure PyTorch).
This exists so the worker can also run on **AMD ROCm**, where CTranslate2 has no GPU support: a parallel
image (`src/Diariz.Worker/Dockerfile.rocm`) and a standalone stack (`deploy/docker-compose.rocm.yml`, AMD GPU
via `/dev/kfd` + `/dev/dri`) run the same pipeline with `ASR_BACKEND=whisper`. Alignment, diarization and
voiceprints are PyTorch and run on ROCm unchanged (PyTorch-ROCm keeps the `"cuda"` device string). Initial
target: Strix Halo (gfx1151). The API/web are vendor-agnostic — only the worker image differs. The
openai-whisper backend is slower than faster-whisper but accuracy is unchanged (the aligner re-times words).

The ROCm stack is **native-Linux only**: `/dev/kfd` does not exist under WSL2 (which bridges compute via
`/dev/dxg`), and AMD's ROCm-on-WSL covers only a short list of discrete cards, not gfx1151 — so Docker
Desktop on Windows fails in the *daemon*, before the container is created, with `error gathering device
information while adding custom device "/dev/kfd"`. Confirmed on a Strix Halo box, 2026-08-12.

**GPU access does not come from `group_add`.** `/dev/kfd` is conventionally `root:render 0660`, but the
worker container runs as **root** (the `rocm/pytorch` base sets no `USER`), so it opens the device through
`CAP_DAC_OVERRIDE` regardless of its groups — verified by running the worker image with no `group_add` at
all and getting a clean GPU matmul. The entry must also be **numeric**: `group_add` resolves names inside
the *container*, and the `rocm/pytorch` images ship no `render` group, so `- render` aborts container
creation with `unable to find group render: no matching entries in group file`. Even where the name does
exist it is the wrong lever, since access is decided by the *host's* GID (990 on the reference box, while
the ROCm 7.0.2 image calls `render` 991).

The base image tag is **pinned** (`rocm7.2.4_ubuntu24.04_py3.12_pytorch_release_2.8.0`) between two
opposing constraints, both measured on a Ryzen AI Max+ 395: ROCm must be ≥ **7.2.4** or every GPU
allocation segfaults on gfx1151 (7.0.2 detects the card, then dies on `torch.randn(..., device="cuda")`,
and `HSA_OVERRIDE_GFX_VERSION` does not help), while torch must stay ≤ **2.8** because torchaudio 2.9
dropped the `AudioMetaData`/`info` symbols pyannote.audio 3.3.2 imports. The previous floating `:latest`
drifted to ROCm 7.14 / torch 2.13 and crash-looped every ROCm worker; `tests/test_dockerfile_pins.py`
guards against a floating tag returning. For gfx1151 also prefer kernel 6.15+.

**An empty `HSA_OVERRIDE_GFX_VERSION` disables the GPU.** The compose file interpolates
`${HSA_OVERRIDE_GFX_VERSION:-}` and `.env.example` ships the key blank, so the default ROCm deployment
hands the container the variable *set but empty* — which the ROCm runtime treats differently from unset
and then finds no GPU at all, silently degrading to CPU. Measured on a Radeon 8060S with one image: no
variable → `torch.cuda.is_available()` `True`; `HSA_OVERRIDE_GFX_VERSION=` → `False`. `worker.py` calls
`rocm_env.clean_gfx_override()` **before** any torch-importing module to delete an empty value (logging a
warning, since it runs before `logging.basicConfig` where only WARNING+ reaches stderr) and to trim a
padded one. That import ordering is load-bearing.

**ROCm inference is now hardware-validated.** On a Ryzen AI Max+ 395 / Radeon 8060S (gfx1151), a 269 s
recording produced 106 segments across 2 speakers with a 192-d ECAPA voiceprint each — ASR, alignment,
diarization and embeddings all on the GPU, at 95-98% GPU utilisation. Indicative throughput is
~1.3-1.7x realtime for `large-v3`, untuned. Note that Strix Halo is an APU with a *dynamic* unified-memory
carve-out, so a point-in-time "VRAM %" reading is not a headroom figure — the allocation grows on demand.

**Both obvious tuning levers measured as dead ends**, and the noise floor is the reason to be sceptical of
any further micro-tuning: repeated runs of the same 269 s file span **123-200 s (±25%)**. Against that,
`HSA_OVERRIDE_GFX_VERSION=11.0.0` was within noise on warm runs and **2.3x slower cold** (MIOpen rebuilding
its kernel cache), so the commonly-cited "2-6x faster on gfx1151" did not reproduce; and TF32 is simply
absent on gfx1151 (`torch.backends.cuda.matmul.allow_tf32` reads back `False` after being set), making
pyannote's `ReproducibilityWarning` a red herring on AMD consumer hardware. Transcript output also varies
run to run (65/116/105 segments on identical audio) because the ROCm ASR path uses openai-whisper's
default **temperature fallback**, which resamples any segment failing the logprob/compression thresholds.

For scale, `DEVICE=cpu` on the *same* machine (16-core Zen 5) took 451.9 s / 481.4 s on the same file —
so the GPU is worth ~**2.8x**, or an hour of audio in ~37 minutes rather than ~104. The CPU path runs at
**0.56-0.60x realtime**, slower than the meeting itself, which is why it is documented as a fallback for
getting the stack working rather than a way to run it. The gap is narrower than a discrete GPU would give
because an APU's CPU and GPU share one package power budget.

## Observability (optional): GlitchTip

An **optional** self-hosted error-tracking and performance-monitoring service, [GlitchTip](https://glitchtip.com/)
(a self-hostable Sentry-protocol server). It is not part of the core stack: it ships as a separate overlay
compose file, `deploy/docker-compose.observability.yml`, layered on top of the main one -
`docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d` - so anyone who does not
want it runs the platform exactly as before with zero extra containers.

- **Its own Postgres.** `glitchtip-postgres` (`postgres:16-alpine`) is a separate container and volume from
  the app's `pgvector/pgvector:pg16` database — platform backup/restore (`MaintenanceController`, see below)
  operates on the app database only, so co-locating GlitchTip's tables would entangle two things with
  different retention and recovery semantics. It also decouples GlitchTip's own Postgres version from the
  app's (which is pinned to 16 for pgvector).
- **Its own MinIO bucket.** GlitchTip's DuckDB/Parquet cold storage for spans lives in a bucket of its own
  (`glitchtip` by default, `GLITCHTIP_COLD_STORAGE_BUCKET`), reached with a scoped MinIO access key - never
  the root credentials, and never a prefix inside the `recordings` bucket (a platform restore wipes and
  re-seeds `recordings`, which would silently destroy the telemetry archive alongside it).
- **Shares the app's Redis**, on **DB index 1** (`VALKEY_URL=redis://redis:6379/1`) - the app's job queues
  live on index 0 and are untouched. Redis is optional for GlitchTip (it only speeds up caching and its task
  queue), so the blast radius of sharing it is small, and it saves standing up a second Redis container.
- **A single all-in-one container**, via `GLITCHTIP_EMBED_WORKER: "true"`. That variable is required, not a
  tuning knob: the image's entrypoint only self-migrates when the Heroku `DYNO` variable is set, so the plain
  `web` role starts against an **unmigrated** database and runs **no task worker**. Setting it selects the
  `all_in_one` role, which runs `migrate` + `maintain_partitions` (issue events are stored in partitioned
  tables) + `createcachetable` on every boot and then embeds the task worker in the web process - one
  container instead of upstream's separate `migrate` and `worker` services. The failure it prevents is
  indirect: an unmigrated instance still serves the static SPA, so the only visible symptom is a login page
  whose register link never appears.
- **First user and first organisation.** The overlay ships `ENABLE_USER_REGISTRATION=false` and
  `ENABLE_ORGANIZATION_CREATION=false` (it is an internal tool on a publicly reachable subdomain), which is
  safe from the very first boot because both gates fall open while their table is empty -
  `ENABLE_USER_REGISTRATION or not User.objects.exists()`, and likewise for `Organization`, with superusers
  permanently exempt from the org gate. Both the API and the Angular frontend apply that rule, so the first
  operator signs up and creates the org through the normal UI and both doors then shut behind them.
  Everyone after that is invited.
- **Deployed per-environment.** Each environment (dev, prod) that opts in runs its own GlitchTip instance
  with its own Postgres, bucket, DSNs, and users - nothing is shared between dev and prod, the same way the
  rest of the stack is deployed per-environment.
- **Blast radius.** Because GlitchTip is co-located on the same box as the platform it is monitoring, it
  **cannot report that the box itself is down** - a host outage, an out-of-disk condition, or a Docker daemon
  crash takes GlitchTip down along with everything else. It only tells you about errors the *running*
  worker/API processes have reported to it, not that they stopped running.

- **Every LLM call is timed, and its token usage recorded.** `LlmTelemetryHandler` (a `DelegatingHandler`) is
  attached to all eight OpenAI-compatible typed clients - summarisation, minutes, tags, actions, translation,
  embeddings, dictation and chat streaming - via the `AddLlmClient` helper in `Program.cs`, so instrumentation
  is registered once rather than per client and anything added later is measured for free. Each call becomes a
  `gen_ai.request` span carrying scheme/host/path (never the query string), the HTTP status, and the
  `usage` token counts parsed out of the response by `LlmUsageParser`. **Only sizes and durations - never a
  prompt or a completion**, which are meeting content.
  - **Token counts ride in the span description, bucketed.** GlitchTip persists no span-level attributes: its
    parquet schema is `(organization_id, project_id, transaction_name, span_id, transaction_id, op,
    description, duration, timestamp)` and nothing else, so anything set via `SetExtra` is transmitted by the
    SDK and dropped on ingest. The description is the only free-text field that survives, so
    `LlmSpanDescription` appends one of six size bands (`(<500 tokens)` ... `(50k+ tokens)`). The bucketing is
    not cosmetic: the breakdown query is `GROUP BY op, description`, so exact per-call counts would give every
    call its own group and destroy the averages the view exists for. The structured `SetExtra` values are kept
    anyway, so a deployment pointed at real Sentry gets exact counts.
  - **Streaming responses are exempt from the body read.** `usage` needs a buffered JSON body, and buffering an
    SSE stream would hold every token until the model finished, so `text/event-stream` responses are timed but
    not parsed.
  - **`JobTelemetry.Begin` gives background jobs a transaction.** Sentry's ASP.NET integration creates a
    transaction per incoming HTTP request and nothing else, and a span with no parent transaction is dropped -
    so before this, every LLM call made from a `BackgroundService` was invisible no matter how it was
    instrumented. Each LLM-using worker (`Summarization`, `MeetingMinutes`, `Tags`, `Actions`, `Embedding`,
    `SectionSummary`, `SectionMinutes`, `FormulaRun`) now opens a `queue.task` transaction per job. This is
    also what gives a formula run an end-to-end duration: its HTTP endpoint only ever measured the enqueue
    (~132 ms), because the model call happens on the worker afterwards.
- **The worker, the API, and the SPA all report.** The transcription worker (`src/Diariz.Worker`) reports to GlitchTip
  when `SENTRY_DSN` is set (`SENTRY_ENVIRONMENT`, `SENTRY_TRACES_SAMPLE_RATE` tune the environment tag and
  trace sampling; see `deploy/docker-compose.yml`'s `worker` service and `src/Diariz.Worker/telemetry.py`).
  The API (`src/Diariz.Api`) reports the same way when `Sentry__Dsn` is set - `Program.cs` calls
  `WebHost.UseSentry` behind an `if (telemetry.Enabled)` guard (`TelemetryOptions`, section `Sentry`), so an
  unset DSN means the SDK is never initialised and there is zero overhead or network traffic.
  - **One transaction per job.** `worker.py` wraps each job in a `transcribe` transaction, with a `span` per
    stage - `download` (fetching the blob from MinIO), `decode` (the ffmpeg decode to a 16 kHz waveform, the
    slowest single stage on a long upload), `asr`, `align`, `diarize`, `shape` (reshaping into the callback's
    segment contract), `embeddings` (the ECAPA voiceprint step, gated by `ENABLE_SPEAKER_EMBEDDINGS`), and
    `callback` (posting the result back to the API). The stages are deliberately gap-free, so the spans add up
    to the job's wall-clock time and a slow job can be traced to the stage responsible.
  - **A failed job files one issue, not two.** `worker.handle()` both logs the failure (`log.exception`) and
    calls `telemetry.capture_exception` - the explicit capture is what carries the job context. `sentry-sdk`'s
    `LoggingIntegration` is enabled by default and promotes ERROR-level records to events, which made every
    failure arrive twice, so `init()` configures it with `event_level=None` (no promotion) and `level=INFO`
    (log records still become breadcrumbs).
  - **API: unhandled exceptions and per-endpoint timings.** `Sentry.AspNetCore`'s middleware captures
    unhandled exceptions with a stack trace and opens one transaction per request, named after the matched
    route, so GlitchTip's performance view lists every endpoint with a request count and p50/p95 latency.
  - **Outbound LLM calls appear as child spans.** With tracing on, Sentry's automatic `IHttpClientFactory`
    instrumentation wraps every outgoing `HttpClient` call in a span parented to the current request
    transaction - including the summarisation/chat/embedding/dictation calls to the configured LLM endpoint -
    so a slow request can be attributed to time spent waiting on the model rather than on the API itself, with
    its own duration visible independent of the parent.
  - **Release tag from the assembly version, shared with the worker.** `Program.cs` reads
    `Assembly.GetExecutingAssembly().GetName().Version` once at startup and uses it both as the API's Sentry
    `Release` and as the `version` field `GET /health` reports - the same value `telemetry.release()` fetches
    from `/health` for the worker's own release tag, so events from both runtimes land under one release in
    GlitchTip. The worker already waits for the API to be healthy before it starts, so `/health` is always
    reachable at that point.
  - **Every event is scrubbed.** The worker's outgoing events and transactions pass through `telemetry.scrub`
    (wired as the SDK's `before_send`/`before_send_transaction` hooks), which recursively redacts any field
    whose key matches a deny-list - transcript text, summaries, minutes, authorization/cookie headers,
    credentials, and the ECAPA voiceprint embedding vectors - before the payload leaves the process. The API's
    events pass through `SentryScrubber` (`src/Diariz.Api/Services/SentryScrubber.cs`), wired as **three**
    hooks in the same `UseSentry` block: it redacts any `Extra`/`Tag`/request-header field matching the same
    kind of deny-list, but goes further for the fields a key-based list cannot reach - it unconditionally nulls
    the request body (`Request.Data`), drops the query string and `Cookies` outright (the SignalR hub takes its
    JWT as `?access_token=<JWT>`, so the query string alone can carry a bearer token), and strips any leftover
    query portion from `Request.Url`.
    - `SetBeforeSend` -> `Scrub`, for the error events.
    - `SetBeforeSendTransaction` -> `ScrubTransaction`, for the **performance** envelopes. `BeforeSend` does
      not run for transactions in the .NET SDK, and the transaction path fires on *every* request at the
      configured `TracesSampleRate` - so this hook, not the event one, is what keeps the hub's
      `?access_token=<JWT>` off the wire in the common case. `SentryEvent` and `SentryTransaction` both
      implement `IEventLike` (settable `Request`, plus `Tags`/`Extra` through `IHasTags`/`IHasExtra`), which is
      the seam the shared redaction runs through. It additionally strips query strings from **span
      descriptions**: Sentry's automatic `IHttpClientFactory` instrumentation names an outbound span
      `<METHOD> <url>`, and `WebhookDeliveryWorker` posts to user-supplied URLs, so a webhook secret in a query
      string would otherwise land in GlitchTip as a span name.
    - `SetBeforeBreadcrumb` -> `ScrubBreadcrumb`, separately, because a `Breadcrumb`'s `Data` has no setter
      once it is attached to an event (the replacement is rebuilt, which resets that breadcrumb's timestamp -
      sub-millisecond, and unavoidable through the public API).

    **The three deny-lists are duplicated, and must be kept in step by hand.** `_DENY_EXACT`/`_DENY_SUBSTRING`
    in `src/Diariz.Worker/telemetry.py`, `DenyExact`/`DenySubstring` in `SentryScrubber.cs`, and
    `DENY_EXACT`/`DENY_SUBSTRING` in `apps/web/src/lib/telemetry.ts` cover the same names, and each runtime has
    a test pinning the shared set. That catches a *removal* from any copy; it cannot catch an *addition* made
    to only one, which is exactly how `embedding`/`embeddings` (the ECAPA voiceprint vectors) once existed only
    in Python while the API is the runtime that actually stores them. With the lists in step, no transcript
    content, credential, or biometric voiceprint vector is transmitted to GlitchTip from any of the three
    runtimes.

- **The SPA reports browser crashes too.** `apps/web/src/lib/telemetry.ts` initialises `@sentry/react` on
  boot (`initTelemetry`, called from `main.tsx`) and wires `components/ErrorBoundary.tsx` to
  `captureException`, so an uncaught render crash is reported with a stack trace instead of just leaving the
  user on a blank page. Unlike the worker/API, whose DSNs come from env vars read once at process start, the
  browser DSN is fetched at runtime from **`GET /api/config`** (which reflects `Sentry__BrowserDsn` /
  `Sentry__Environment`) - the SPA is a single static bundle served identically to every environment (dev,
  staging, prod), so the DSN cannot be baked in at build time the way it can for a server process; an empty
  DSN leaves the SPA silently unreporting, matching the worker/API's "empty DSN = inert" behaviour. The
  browser DSN is deliberately a **separate GlitchTip project** from `Sentry__Dsn` (`TelemetryOptions.BrowserDsn`,
  same `Sentry` config section) - browser noise (ad blockers, extensions, stale tabs, flaky client networks) is
  far higher-volume than server errors and would otherwise bury them in the same project. Because the
  **desktop app loads the SPA from the server origin** rather than bundling its own copy, it inherits this
  reporting automatically with no desktop-specific code or release.
  - **Scrubbing is by field name and by URL, not by content inspection.** `beforeSend`/`beforeBreadcrumb`
    recursively redact any object key matching the shared deny-list (the same categories as the worker/API:
    transcript text, summaries, minutes, credentials, the ECAPA embedding vectors) and separately strip the
    query string off every URL they touch - load-bearing because `@microsoft/signalr` appends
    `?access_token=<JWT>` to the hub's negotiate/WebSocket URLs (a browser cannot set an Authorization header
    on a WS handshake), which a key-name deny-list cannot reach since a query string is one opaque value, not
    a named field. Console breadcrumbs below `warn` are dropped entirely, since their message is free text a
    deny-list cannot vet; `warn`/`error`/`fatal` console breadcrumbs are kept and still pass through the same
    key/URL scrub. This is narrower than the API's `SentryScrubber`, which also nulls the request body and
    drops cookies outright - the browser SDK does not capture either by default, so there is nothing there to
    strip.
  - **URL stripping is a rule over every string, never a list of key names.** Three of the SDK's **default**
    integrations put credential-bearing URLs under key names this app does not choose, so `beforeSend`/
    `beforeBreadcrumb` apply the strip to whole bags rather than to named fields:
    - `breadcrumbsIntegration`'s history handler emits `{ category: "navigation", data: { from, to } }`,
      where each value is `parseUrl(...).relative` - **path plus query plus hash**. Neither key is named
      `url` and neither is deny-listed, so an earlier `data.url`-only rule passed
      `/setup?token=<account-activation credential>`, the OAuth authorize query, and
      `/login?returnTo=<encoded authorize URL>` through untouched. Every string value in a breadcrumb's
      `data` is now stripped, **at every depth** - the same handler's console branch sets
      `data: { arguments: handlerData.args, logger: "console" }` (the raw console call arguments) and
      `warn`/`error` console breadcrumbs are deliberately kept, so a URL passed to `console.error` sat
      one array level down and survived a top-level-only pass. Both scrubbers walk objects and arrays
      recursively and are **cycle-guarded** (the current path is tracked in a `WeakSet`, and a repeat
      becomes a `[circular]` marker, under a `MAX_DEPTH` backstop): breadcrumb data is arbitrary
      app-supplied structure, and before the guard a self-referencing object threw
      `RangeError: Maximum call stack size exceeded` out of a hook that runs inside the SDK's own
      breadcrumb handler - a hang or throw there takes the page with it, which is worse than the leak.
    - `httpContextIntegration` copies the browser's `Referer` header onto `event.request.headers` - the
      **full previous URL**, which is the same credential whenever the user has just come from `/setup` or
      `/connect/authorize`. Every string header value is stripped, on error events and transactions alike.
    - Free text quotes URLs too, so `exception.values[].value` and `event.message` get the same
      word-by-word `scrubUrlsIn` treatment the breadcrumb message and span descriptions already had.
  - **Sessions are off by removing the integration, not by an option.** GlitchTip does not support session
    envelopes. `autoSessionTracking: false` does **not** exist in `@sentry/react` 10.69.0 (zero occurrences
    anywhere in the installed `@sentry/*` packages), so passing it did nothing and the SPA sent a session on
    load and on every route change. `browserSessionIntegration` is a **default** integration and
    `getIntegrationsToSetup` **merges** a supplied array with the defaults, so the only way to drop it is the
    **function** form: `integrations: (defaults) => [...defaults.filter(i => i.name !== "BrowserSession"),
    browserTracingIntegration()]`. The option object is typed `Pick<BrowserOptions, ...>` against the SDK's
    own type precisely so the next non-existent option is a compile error rather than a silent no-op.
  - **Telemetry never delays first paint by more than `CONFIG_TIMEOUT_MS` (2 s).** `main.tsx` gates the first
    render on the `GET /api/config` read, so `initTelemetry` bounds it with an `AbortController` **and** a
    race: an API that accepts the connection and then hangs costs 2 s, not the proxy's timeout (60 s on
    nginx), after which the app renders with telemetry off.

- **Browser tracing joins the same trace as the API request it triggered - and depends on an outer-proxy
  header passthrough that is easy to miss.** `initTelemetry` also enables `@sentry/react`'s
  `browserTracingIntegration()`, sampled at the same `TracesSampleRate` the API/worker use (surfaced to the
  browser via `GET /api/config`'s `sentryTracesSampleRate`). It times page loads and every outgoing
  `fetch`/`XHR` call as a browser-side transaction, and by default the SDK attaches `sentry-trace` and
  `baggage` headers to same-origin requests so the browser's transaction and the API's request transaction
  are recorded as **one continuous trace** rather than two unrelated ones - which is how a slow page load
  gets traced end to end into whichever server-side stage actually took the time.
  - **Operational caveat: the outer reverse proxy must forward those two headers.** Anything sitting in
    front of the stack - the app's own nginx (`apps/web/nginx.conf`) as well as any further reverse proxy an
    operator puts in front of that (see the `/mcp` header-forwarding note above) - has to pass `sentry-trace`
    and `baggage` through untouched on requests to `/api`. If a proxy strips or rewrites them, nothing errors
    and nothing shows up as broken: tracing keeps working on both sides, it just silently degrades into two
    disconnected halves (a browser transaction with no matching API transaction, and vice versa) that look
    exactly like a deployment nobody misconfigured. This is the first thing to check if browser and API spans
    stop lining up after a proxy change.
  - **Scrubbing covers the transaction, not just error events.** `beforeSendTransaction` runs the same
    field-name/URL scrub as `beforeSend`, applied to the transaction's `request.url` and every span's
    `description` - and, beyond that, to each span's attribute bag (`spans[].data`) and the root span's
    (`contexts.trace.data`). That extra reach is load-bearing: Sentry's automatic fetch/XHR instrumentation
    puts the **full, unsanitized request URL** on span attributes such as `url`, `http.url`, and `url.full`,
    and the raw query string alone on `http.query` - none of which the SDK's own sanitizer touches, since
    that only cleans the span's name. An early cut of this hook scrubbed only descriptions and missed the
    attribute bag, which was a real access-token leak (found in review, not by the SDK) since the SignalR hub
    URL carries `?access_token=<JWT>`. `scrubAttributes` (`apps/web/src/lib/telemetry.ts`) redacts any
    `*.query`-named attribute outright and strips the query string off every other URL-shaped string value, so
    the fix generalizes to whatever attribute name the SDK adds next rather than hardcoding today's set.
    **Known residual gap:** URL fragments (`http.fragment`) are not scrubbed by any of this - not exploitable
    today only because this app's JWT travels as a query parameter, never a fragment; a future attribute or
    integration that surfaces fragment data would need its own scrub.

- **Source maps are generated but never served, and uploaded to GlitchTip at image build time instead.**
  `apps/web/vite.config.ts` sets `sourcemap: "hidden"` - the `.map` files are still built (so a stack trace
  can be un-minified), but the `//# sourceMappingURL=` comment is omitted from the shipped bundles, so no
  browser ever fetches them. `apps/web/Dockerfile`'s build stage then deletes any `.map` file that survives
  into `/usr/share/nginx/html` (`RUN find ... -name '*.map' -delete`) as defence in depth - without that, the
  files would stay fetchable by guessing the filename even though nothing links to them. Before that deletion,
  a build stage step optionally runs `npx @sentry/cli@3.6.2 sourcemaps inject ./dist` and then
  `npx @sentry/cli@3.6.2 sourcemaps upload ./dist`,
  gated on four build inputs all being present: `GLITCHTIP_URL`/`GLITCHTIP_ORG`/`GLITCHTIP_PROJECT` (build
  ARGs) and a `glitchtip_token` **BuildKit secret** (never a build ARG - an ARG is recorded in the image
  history and readable by anyone who can pull the image; it reaches the CLI as `SENTRY_AUTH_TOKEN` rather
  than `--auth-token`, so it never appears in a process command line). Missing any of the four just skips the
  upload with a log line, so a developer build and a CI build both still succeed with zero GlitchTip
  credentials; a reachable-but-failing upload **fails the build**, unless `GLITCHTIP_SOURCEMAPS_OPTIONAL` is
  set - shipping an image whose traces cannot be read is a decision, not a warning to be skimmed.
  **Sentry's CLI, not GlitchTip's, and pinned.** GlitchTip speaks the Sentry protocol, and `@glitchtip/cli`
  1.0.0 (its only published version) is broken for this: it uploads each file individually and then calls
  `releases/{version}/assemble/` once per file, an endpoint that expects a single zip of all artifacts plus a
  `manifest.json` - so the server raises `BadZipFile` per artifact and registers nothing. `--release` is
  mandatory and selects that path, so no flag avoids it. Verified against 6.2.3: `@glitchtip/cli` produced
  185 blobs, 0 files and 0 bundles; `sentry-cli` on the same server produced an artifact bundle that
  assembled. **Assembly is asynchronous**, so the build's pass/fail check can only prove the upload
  succeeded - a green build with still-minified traces means checking `File`/`DebugSymbolBundle` rows and the
  container log for `assemble_artifacts`. The upload is tagged with the same release value `apps/web/src/lib/telemetry.ts`'s
  `initTelemetry` sends as `release` (`__APP_VERSION__`, from `vite.config.ts`'s `appVersion()`) - the
  Dockerfile re-derives the same fallback (`APP_VERSION` build ARG, else this package's `package.json`
  version) so the two agree whether or not the ARG is passed, since a mismatch means GlitchTip cannot attach
  the uploaded maps to incoming browser events. `--org`/`--project` must name the **same browser project** as
  `SENTRY_BROWSER_DSN` above. **The `inject` step is what makes the upload mean anything:** because
  `sourcemap: "hidden"` strips the `//# sourceMappingURL=` comment, an uploaded map has nothing tying it back
  to the minified frame it explains, and GlitchTip silently leaves the trace minified. `inject` writes a
  matching **debug ID** into each bundle and its map (`//# debugId=...` plus a `_sentryDebugIds` global, which
  `@sentry/core`'s `prepareEvent` turns into the event's `debug_meta.images`), which is the link the upload
  registers - so GlitchTip's documented flow is inject-then-upload. It rewrites `./dist` in place and contacts
  no server, but stays inside the same credential gate: with no upload to pair them with, injected debug IDs
  are dead weight in the shipped bundles. **`GLITCHTIP_URL` must be reachable from inside the build
  container**, so it has to be the public domain: with the default `GLITCHTIP_BIND=127.0.0.1`, a
  `http://127.0.0.1:8000` value reaches the *host's* loopback, not the builder's, and the upload warns
  "connection refused" while the build still succeeds - an easy failure to miss. A compose service name
  (`http://glitchtip:8000`) does not work either, since the build runs before and outside the compose network.
  Because the SPA is built inside `apps/web/Dockerfile` on the deploying server
  rather than in GitHub CI, this upload step has to live there too - and it is wired straight into
  `deploy/docker-compose.yml`'s `web` service so the normal `docker compose up --build` picks it up with no
  extra flags: `GLITCHTIP_URL`/`GLITCHTIP_ORG`/`GLITCHTIP_PROJECT`/`APP_VERSION` are service `build.args`
  (each `${VAR:-}`, so unset stays blank) and the token is a service `build.secrets` entry backed by a
  top-level `secrets: glitchtip_token: { environment: GLITCHTIP_TOKEN }` - Compose reads `GLITCHTIP_TOKEN`
  straight out of `deploy/.env` and mounts it as a BuildKit secret, never a build ARG. That `environment:`
  form always mounts the secret file, empty when the env var is unset, which is why the Dockerfile's gate
  tests `-s` (non-empty) rather than `-f` (exists) - a bare `-f` would misread an empty file as "token
  provided" under this wiring. See `deploy/.env.example` for the variables.

## Platform backup & restore

`MaintenanceController` (Platform-Administrator only) exports/imports the **whole platform** as one `.zip`:
a `manifest.json` (a `Format` compatibility epoch, app version, the last-applied EF migration id, createdAt), a
`database.dump` (`pg_dump --format=custom`), and one `objects/<key>` entry per object-store blob (audio +
attachments). The API image therefore ships the **PostgreSQL client tools** (`pg_dump`/`pg_restore`). `GET
/api/maintenance/backup` builds the zip to a **temp file first** and then returns it (`ZipArchive` writes its
headers synchronously, which Kestrel's response body forbids), with the token passed via `access_token` like the
audio endpoint; `POST /api/maintenance/restore` takes the raw zip body and gates on compatibility: the backup's
`Format` must equal the running instance's `CurrentFormat`, and its migration id must be the current one **or
an earlier ancestor** (newer/unknown schemas are refused - there are no down-migrations). It then runs
`pg_restore --clean`, and if the backup was an **older ancestor**, calls `MigrateToCurrentAsync` to roll the
restored schema up to the running code (the response reports `migratedFrom`/`migratedTo`/`restartRecommended`);
finally it wipes and re-uploads the bucket. `Format` is the human-controlled breaking-change fence - bump it in
the same PR as any migration that is not forward-restore-safe. Restore is **destructive** (replaces all data;
on a same-version restore the admin is signed out, on a forward-migrated restore they are kept on the page with
a restart hint).
The Data-Protection **keyring is not included** — after restoring on a different instance, encrypted per-user
LLM API keys can't be decrypted (users re-enter them); everything else is faithful. The `pg_dump`/`pg_restore`
shell-out is behind `IDatabaseBackup` so the archive/object orchestration is unit-tested; the real round-trip
is an integration test that skips when the client tools aren't on the host PATH.

**Proxy limits are the restore's real ceiling.** The API applies **no** size limit of its own here - the action
is `[DisableRequestSizeLimit]` and reads the raw body - so every refusal comes from a proxy in front of it, and
a restore body is not comparable to a recording upload: it carries the dump *plus every stored blob,
uncompressed*, so it is always larger than the sum of all audio and cannot be given a sensible fixed cap. The
recording-upload chain further up sizes each layer above `Uploads:MaxBytes`; there is no equivalent app-level
number to size against, so both proxies must simply not cap this path.
- **In this repo:** `apps/web/nginx.conf` has a `location /api/maintenance/` block (longer prefix, so it wins
  over `/api/`) with `client_max_body_size 0`, `proxy_request_buffering off`, `proxy_http_version 1.1`, and 3 h
  read/send timeouts. The server-wide `1024m` still applies to every other endpoint.
- **On the outer reverse proxy (not in this repo):** the same settings must be applied to the app's host, and
  **per host** - a staging host added later does not inherit the production host's Advanced config, which is
  exactly how a first restore on staging met a bare nginx **413** that never reached the API. In
  nginx-proxy-manager this goes in the host's **Advanced** tab, which is injected at `server` scope; all four
  are valid there and inherit into NPM's generated `location /`, which sets none of them itself:

  ```nginx
  client_max_body_size 0;
  proxy_request_buffering off;
  proxy_read_timeout 3h;
  proxy_send_timeout 3h;
  ```

  **Do NOT add `proxy_http_version 1.1` here, even though the inner nginx needs it and the streaming above
  depends on it.** NPM already emits that directive at `server` scope when the host has **Websockets Support**
  enabled, and nginx rejects a second one in the same block outright (`"proxy_http_version" directive is
  duplicate`). NPM's failure mode makes this expensive to diagnose: rather than keeping the last good config, it
  sets the host's file aside as `.err` and reloads without it, so the host stops existing as far as nginx is
  concerned, the request falls through to the default server and its dummy certificate, and the browser reports
  a **TLS name error** - which looks like a DNS or certificate problem and points nowhere near the line that
  caused it. Confirm with `docker exec <npm-container> nginx -t` and by looking for a `.err` file in
  `/data/nginx/proxy_host/`.

  The corollary is a **coupling worth knowing about**: on this host, request streaming is supplied by the
  Websockets Support toggle, whose stated purpose is unrelated. Turning it off would break SignalR (so it must
  stay on regardless) and *silently* restore request buffering - a multi-GB archive spooled to the proxy's disk
  again, with no error to explain the slowdown. If NPM ever stops emitting it, the line belongs in Advanced.

  This **widens the whole host**, not just `/api/maintenance/` - which is safe only because the web container's
  own nginx still enforces `1024m` on every other path, so the backstop moves one hop in rather than
  disappearing, and `Uploads:MaxBytes` remains the layer that answers a too-large recording readably. It also
  raises the host's read timeout from the 600s recorded for uploads above; the cost is that a genuinely hung
  request now occupies a connection for 3 h instead of 10 min. **Both hops must be changed** - the inner
  location ships inside the web image, so the web container has to be rebuilt, not just restarted.
- **Why the body is streamed rather than buffered**, when the recording-upload advice above says the same for a
  different reason: buffering costs scratch space equal to the archive *and* doubles the transfer, because the
  API writes its own temp copy either way. Two arguments that normally favour buffering invert here - with an
  unlimited body size it would make nginx absorb an **unauthenticated** flood to disk before the API can answer
  401 (the action doesn't read `Request.Body` until after authorization), and buffering is what enables an
  upstream retry, which a destructive non-idempotent restore must never get. Truncation is safe: the API's zip
  fails to open before `pg_restore` or the bucket wipe. Note `proxy_http_version 1.1` is **load-bearing** -
  nginx buffers a chunked request body regardless of `proxy_request_buffering` unless HTTP/1.1 proxying is on
  (the default is 1.0), so an outer proxy streaming into this one would otherwise be spooled to disk silently.
- **Diagnose by status code**, as with uploads: **413** is a body cap, **504** is a timeout. Both endpoints are
  silent for minutes (backup builds the whole zip before the first byte; restore reloads the database and
  re-uploads every blob before answering), so a 60s default timeout reads as a crash rather than as work in
  progress.
- The browser is shown a readable message for either: `apiErrorMessage` (`apps/web/src/lib/api.ts`) discards a
  body that is markup rather than a message, so a proxy's HTML error page no longer lands in the panel verbatim.

**Progress reporting.** Because the archive is fully assembled before the first response byte, a download can
sit silent for minutes on a large platform - the browser shows no download entry until the headers arrive. The
backup action therefore reports into `IBackupProgress` (a **singleton**, in-memory, per-instance: the build is
one request on one node and the admin polling it is on that same node), which tracks a running flag, the phase
(`Database` then `Objects`), a count of blobs archived and the start time. `GET /api/maintenance/backup/status`
returns that snapshot, and the web Maintenance panel polls it while the download is in flight. The scope is
opened with `Begin()` and disposed when the zip is built - the transfer that follows is the browser's own
visible download. Concurrent builds are reference-counted, so one admin finishing doesn't clear another's
progress. Restore needs no server-side counterpart: the browser owns that upload, so the panel switches from
upload percentage to an "applying" message once the bytes are sent and the server-side work begins.

## Repository layout

```
Diariz.slnx                 # API + Domain solution (worker is Python, web/desktop are npm)
version.json                # canonical version (mirrored to package.jsons + API <Version>)
src/Diariz.Api/             # ASP.NET Core API
  Controllers/ Services/ Contracts/ Configuration/ Hubs/
src/Diariz.Domain/          # entities, DiarizDbContext, Migrations
src/Diariz.Worker/          # Python GPU worker (worker.py, pipeline.py, callback.py, storage.py, config.py)
apps/web/                   # React SPA (lib/, components/, pages/)
apps/desktop/               # Electron tray shell
integrations/n8n-nodes-diariz/  # published n8n community node (independent semver, MIT)
deploy/                     # docker-compose.yml (+ .env.example)
docs/                       # this folder
branding/                   # GitHub social card + source
tests/                      # Diariz.Api.Tests (unit), .IntegrationTests (Testcontainers), .TestSupport
```

## Testing & CI

Three .NET test projects (fast **unit** with the EF in-memory provider + hand-rolled fakes; **integration**
via Testcontainers spinning up real Postgres/Redis/MinIO; shared **TestSupport** fakes), plus **vitest** for
the web, **pytest** for the worker (whisperx stubbed), and **`node --test`** for the desktop shell and the n8n
node. **TDD is required** — write the failing test first. CI (`.github/workflows/ci.yml`) runs the suites on
`ubuntu-latest`; the n8n-node job additionally regenerates the node from the API's OpenAPI document and fails
the build if the committed output has drifted.

## Roadmap (milestones)

- **M1 — done:** capture → transcribe (WhisperX + pyannote) → view speaker-labelled segments.
- **M2 — done:** multi-user auth + RBAC, LLM summaries, action extraction, transcript export/email,
  re-transcribe with model choice, sections (**nested folders up to 8 levels deep**: `Section.ParentId`, drag-to-reorder),
  speaker identification, delete-audio (keep transcript, free quota), **supporting-document attachments**
  (files or URLs on a recording — `Attachments` table + `AttachmentsController` — or **directly on a folder** —
  `SectionAttachments` + `SectionAttachmentsController`; files in MinIO under `{userId}/attachments/…` /
  `{userId}/section-attachments/…` and counted toward the quota; Markdown attachments are editable in place).
- **M3 — partial:** chat across transcripts (shipped); full embedding-backed RAG over `Segment.Embedding`
  (`vector(768)`, sized for `nomic-embed-text`) is scaffolded but not yet populated.
- **M4 — in progress:** packaging/TLS hardening; **macOS desktop app** shipped as an unsigned **beta**
  (mic + ScreenCaptureKit system audio, menu-bar shell, manual update check) - signing/notarization +
  auto-update + Sign in with Apple are the next macOS milestones (see the macOS guide).
