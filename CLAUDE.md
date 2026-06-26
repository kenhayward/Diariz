# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Diariz is a multi-user voice/meeting transcription platform: record audio → upload → transcribe
server-side with **speaker diarization** + word-level timestamps → view speaker-labeled segments.
Currently at **Milestone 1** (capture → transcribe → view). LLM summaries (M2), RAG chat over
embeddings (M3), and packaging/TLS (M4) are scaffolded but not built — see `docs/Overall_Synopsis_of_Platform.md`
and the roadmap in `README.md`.

## Architecture & data flow

Four deployables that communicate across process/language boundaries:

| Component | Stack | Path |
|---|---|---|
| API / auth / orchestration | ASP.NET Core (**.NET 10**) + EF Core + SignalR | `src/Diariz.Api` |
| Domain model + migrations | EF Core + Postgres/pgvector | `src/Diariz.Domain` |
| Transcription worker | Python: WhisperX (large-v3) + pyannote 3.1, GPU | `src/Diariz.Worker` |
| Web UI | React 19 + TS + Vite + Tailwind v4 | `apps/web` |
| Desktop shell | Electron (mic + Windows loopback capture) | `apps/desktop` |

**End-to-end flow:** client records → `POST /api/recordings` (multipart) → API stores the blob in
MinIO and a `Recording` row in Postgres → API creates a `Transcription` row (versioned) and
**enqueues a job on a Redis Stream** → Python worker `XREADGROUP`s the job, downloads the blob,
runs WhisperX→align→pyannote, and **POSTs segments back** to `internal/transcriptions/result` →
API persists `Segment`s + seeds `Speaker` rows → notifies the browser over **SignalR**
(`RecordingStatusChanged`) → the detail page refetches.

### Cross-boundary contracts (the non-obvious glue)

- **Redis Stream job queue.** Stream key `transcription-jobs`, consumer group `workers` (see
  `worker/config.py` and `Api/Services/JobQueue.cs`). The job payload is JSON with **PascalCase**
  keys (`TranscriptionId`, `BlobKey`, `Model`) — produced by .NET, consumed by Python. The worker's
  callback bodies are also PascalCase so .NET model binding works. Keep both sides in sync when
  changing `TranscriptionJob` / `TranscriptionResult` / `Segment` shapes.
- **Worker → API callback** uses route `internal/transcriptions/*` and is authenticated by a shared
  secret header `X-Worker-Secret` (= `CALLBACK_SECRET`), **not** JWT. It is not user-facing.
- **Summarisation queue (in-process).** A second Redis stream `summarization-jobs` (consumer group
  `summarizers`) is **produced and consumed entirely within the API** — `RedisJobQueue.EnqueueSummarizationAsync`
  enqueues, and `Services/SummarizationWorker` (a `BackgroundService`, the API's only stream consumer)
  reads it, calls an OpenAI-compatible `/chat/completions` endpoint (`SummarizationClient`), and writes the
  `Summary` (+ an auto-generated `Name` when the recording has none). It is a singleton, so it opens a DI
  scope per job; it XACKs even on failure (records a `Failed` status) to avoid poison-message loops.
- **Per-user summarisation config.** Config is resolved per recording-owner by
  `SummarizationSettingsResolver`: each field is the user's `UserSettings` value (table is 1:1 with the user;
  the API key is encrypted at rest via ASP.NET Data Protection — `IApiKeyProtector`, keyring persisted to the
  `DataProtection:KeysPath` volume) **?? the server `Summarization` defaults** (`SUMMARY_API_BASE`/`SUMMARY_API_KEY`/`SUMMARY_MODEL`).
  The resolved config flows into `SummarizationClient` (no longer reads `IOptions`). The Summarize endpoint
  returns 400 when neither user nor server has an endpoint; the worker **always listens** (so per-user-only
  configs work). Users manage their endpoint/model/key in the web Settings modal (`api/user/settings`); the
  key is **write-only** (GET returns only `hasApiKey`).
- **SignalR auth.** The hub (`/hubs/transcription`) requires JWT; browsers can't set Authorization
  headers on the WS handshake, so the token is passed as the `access_token` query string and picked
  up in `Program.cs` `OnMessageReceived`. Clients are auto-joined to a per-user group (group name =
  user GUID) so status events are scoped per user.

### Domain model notes

- **Transcriptions are versioned per recording** (`(RecordingId, Version)` unique). `Retranscribe`
  bumps the version; `GET /api/recordings/{id}` returns only the highest-version transcription (and its
  `Summary`, if any).
- **Recording naming.** `Recording.Title` is the auto descriptor; `Recording.Name` (nullable) is the
  user-editable display name (the UI shows `Name ?? Title`) and is also auto-filled by the summariser when
  unset. `Recording.Source` (`Microphone`/`System`) is captured at upload. `RecordingStatus` gained
  `Summarizing = 6` — **append only, never renumber** (values persist as ints in Postgres).
- **Speaker renames are preserved across re-transcribes.** Worker emits diarization labels
  (`SPEAKER_00`...); the callback seeds a `Speaker` row per new label with `DisplayName = label`,
  and the UI's rename updates `DisplayName` only.
- **pgvector** column is `vector(768)` on `Segment.Embedding` (sized for `nomic-embed-text`).
  Embeddings/RAG are unused in M1 — adjust the dimension via a migration if the embed model changes.
  `OnModelCreating` applies the pgvector extension + column **only when the provider is Npgsql**
  (`Database.IsNpgsql()`); under other providers (the in-memory test provider) the property is
  `Ignore`d. Keep new Postgres-only model config behind that same guard so unit tests can build the model.
- All user-scoped queries filter by `UserId` from the JWT `NameIdentifier` claim — preserve this
  ownership check on every recording endpoint.

## Test-driven development (required)

**This project uses TDD. Write the failing test first, watch it fail, then write the minimal code
to pass.** No production code without a failing test that preceded it. This applies to new features,
bug fixes, and behavior changes. When fixing a bug, first add a test that reproduces it (red), then
fix (green). Exceptions (throwaway spikes, generated code, pure config) need a human's sign-off.

Keep test output pristine — a passing run has no errors or warnings.

## Commands

### Backend (.NET API + Domain)
`Diariz.slnx` contains **only** the Api and Domain projects (the worker is Python, web/desktop are npm).

```bash
dotnet build Diariz.slnx
dotnet run --project src/Diariz.Api          # needs Postgres/Redis/MinIO reachable
```

### Tests (.NET)
Three test projects, all in `Diariz.slnx`:

| Project | Kind | Docker? |
|---|---|---|
| `tests/Diariz.Api.Tests` | Fast unit tests (xUnit) | No |
| `tests/Diariz.Api.IntegrationTests` | Integration tests (Testcontainers) | **Yes** |
| `tests/Diariz.Api.TestSupport` | Shared fakes/helpers (not a test project) | — |

```bash
dotnet test                                              # everything (needs Docker for integration)
dotnet test tests/Diariz.Api.Tests                       # fast unit tests only, no Docker
dotnet test tests/Diariz.Api.IntegrationTests            # integration only (needs Docker)
dotnet test --filter "FullyQualifiedName~WorkerCallback" # one class / name substring
dotnet test --filter "Name=Result_WithWrongSecret_ReturnsUnauthorized"  # one test
```

**Unit tests (`Diariz.Api.Tests`) — no Docker.** They use the **EF Core in-memory provider**
(`TestDb.Create()` gives each test an isolated database) and hand-rolled fakes for the external
boundaries. The fakes/helpers live in **`Diariz.Api.TestSupport`** (namespace
`Diariz.Api.Tests.Infrastructure`, shared with the integration project): `FakeJobQueue` (Redis),
`FakeAudioStorage` (MinIO/S3), `FakeHubContext` (SignalR — records the messages a controller pushed),
and `Http.Context(userId, headers)` (builds a `ControllerContext` with an authenticated user /
headers). **No mocking library** — add a fake to `TestSupport` rather than reaching for one.

**Integration tests (`Diariz.Api.IntegrationTests`) — needs Docker.** `ContainersFixture` (an
`ICollectionFixture`) spins up real **Postgres/pgvector, Redis, and MinIO** via Testcontainers once
per run, applies EF migrations, and exposes connection strings + `CreateDbContext()`. All classes
share the `"integration"` collection so they run sequentially against one set of containers; tests
isolate via unique ids/keys rather than per-test databases. Use this layer for anything that depends
on real relational/query behavior, FK enforcement, the pgvector column, the Redis stream wire format,
or S3/MinIO round-trips.

**In-memory provider caveat:** it does not faithfully translate relational queries (e.g. it **ignores
ordering/`Take` inside a filtered `Include`**, and does not enforce FKs). Behavior like the "current =
highest-version transcription" rule in `RecordingsController.Get` is therefore `[Fact(Skip=...)]` in
the unit project and verified for real in the integration project instead. Don't "fix" a skipped unit
test by gaming the in-memory provider — move it to the integration harness.

The API **auto-runs EF migrations, seeds the default user, and ensures the MinIO bucket on startup**
(`Program.cs`) — you do not run `database update` manually for normal dev.

EF migrations (the `DbContext` lives in `Diariz.Domain`, but it's an ASP.NET host, so use the startup project):
```bash
dotnet ef migrations add <Name> --project src/Diariz.Domain --startup-project src/Diariz.Api
```
`DiarizDbContextFactory` exists for design-time tooling.

### Worker (Python)
```bash
cd src/Diariz.Worker
pip install torch==2.7.1 torchaudio==2.7.1 --index-url https://download.pytorch.org/whl/cu128
pip install -r requirements.txt
HF_TOKEN=... REDIS_URL=redis://localhost:6379/0 API_BASE_URL=http://localhost:8080 python worker.py
```
**GPU compatibility (Blackwell / RTX 50-series, sm_120).** The worker pins the **cu128** torch
stack (CUDA 12.8 base image) because cu121/torch 2.5 only compiles kernels up to sm_90 — on a 5090
every job dies at model load with *"no kernel image is available for execution on the device"*. Three
non-obvious pins make whisperx 3.3.1 work on this stack (see `Dockerfile` / `requirements.txt`):
`ctranslate2==4.6.3` (first version with sm_120 / CUDA 12.8; whisperx caps it at <4.5.0 so the
Dockerfile force-installs it), `transformers==4.48.0` + `huggingface_hub==0.27.1` (hub 1.0 removed the
`use_auth_token` kwarg pyannote 3.3.2 still passes), and `worker.py` calls `torch_compat` to restore
`torch.load(weights_only=False)` (torch≥2.6 flipped the default and rejects the pyannote checkpoints).

Diarization is gated: you **must** set `HF_TOKEN` and accept the `pyannote/speaker-diarization-3.1`
+ `pyannote/segmentation-3.0` terms on Hugging Face, or jobs fail. CPU-only: `DEVICE=cpu COMPUTE_TYPE=int8` (slow).

Worker tests (pytest, no GPU): `pip install -r requirements-test.txt && python -m pytest`. The suite
stubs `whisperx` (`tests/conftest.py`) so `torch`/CUDA aren't needed — it covers the callback contract
(`callback.py`), job orchestration + temp cleanup (`worker.handle`), and segment shaping
(`pipeline._shape_segments`). The shaping logic is extracted into `_shape_segments` precisely so it can
be unit-tested without the models; keep new pure transforms similarly separable from the whisperx calls.

### Web
```bash
cd apps/web
npm run dev        # http://localhost:5173, proxies /api and /hubs to :8080
npm run build      # tsc typecheck + vite build
npm test           # vitest (jsdom); npm run test:watch for the watch loop
```
Vitest config is in `vitest.config.ts` (kept separate from `vite.config.ts` so the production
build doesn't depend on vitest). Tests are `src/**/*.test.ts(x)`, excluded from the build's `tsc`
via `tsconfig.json`. No DOM/component testing library is wired yet — add `@testing-library/react`
(+ the react plugin in `vitest.config.ts`) when you start testing components.

### Desktop (Electron)
```bash
cd apps/desktop && npm run dev    # sets DIARIZ_DEV=1, loads the Vite dev server
```
Only the Electron shell can capture **system/loopback** audio (`setDisplayMediaRequestHandler` →
`audio: "loopback"`, Windows only). It exposes `window.diariz.isElectron` to enable the "System
audio" recorder option, and can override the API base via `window.__DIARIZ_API_BASE__`.

### Full stack (Docker)
```bash
cd deploy
cp .env.example .env      # JWT_KEY, CALLBACK_SECRET, HF_TOKEN, SEED_EMAIL/PASSWORD, MinIO creds
docker compose up --build # web, api, postgres, redis, minio, GPU worker
```
The Compose project is named **`diariz`** (top-level `name:` in `docker-compose.yml`; Docker forces
lowercase, so it is `diariz` not `Diariz`) rather than defaulting to the `deploy` directory name. The
**`web`** service builds `apps/web` (`apps/web/Dockerfile`) and serves the static SPA via nginx at
**http://localhost:8081**, proxying `/api` and `/hubs` to the `api` container (same-origin, so no CORS
needed — `apps/web/nginx.conf`). The GPU worker needs the NVIDIA Container Toolkit; for CPU comment
out the `deploy.resources` GPU block and set `WORKER_DEVICE=cpu WORKER_COMPUTE_TYPE=int8`.

## Conventions & gotchas

- **Tests:** harnesses exist for all three stacks — .NET (`tests/Diariz.Api.Tests` + integration),
  web (`vitest`), and the Python worker (`pytest`, see the Worker section). No CI runs them on push yet.
- **Ports:** API `8080`; web UI (Docker/nginx) `8081`; web dev server `5173`; Postgres `5432`;
  Redis `6379`; **MinIO is remapped on the host**
  — S3 API `9002→9000`, console `9003→9001` (avoids clashing with other local MinIO instances).
  In-container, services use the compose service names (`minio:9000`, `redis:6379`, `postgres:5432`).
- **MinIO/S3 quirk:** `AmazonS3Config` uses `ForcePathStyle` + region `us-east-1`. A prior bug
  required removing `DisablePayloadSigning` on `PutObject` for MinIO uploads to work — be cautious
  changing S3 request options in `Services/AudioStorage.cs`.
- Config binds via the options pattern (`Configuration/AppOptions.cs`): `Jwt`, `Storage`,
  `JobQueue`, `Worker` sections, settable through `__`-delimited env vars in compose.
- Worker model load is **lazy + cached** in `pipeline.py` (Whisper/align/diarizer load once and are
  reused across jobs — loading large-v3 + pyannote is expensive).
