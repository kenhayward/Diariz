# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Diariz is a multi-user voice/meeting transcription platform: record audio → upload → transcribe
server-side with **speaker diarization** + word-level timestamps → view speaker-labeled segments.

**Scope is well past the original M1.** Shipped: capture/transcribe/view (M1); multi-user auth +
RBAC, LLM summaries, meeting minutes, action items, tags, export and re-transcribe (M2); chat across
transcripts with **semantic (RAG) search** over pgvector fused with keyword search, and **speaker
identification** from enrolled voiceprints (M3); and **integrations** in both directions - a scoped
REST API with expiring personal tokens, an **MCP server** (with its own OAuth 2.1 server for the
claude.ai web connector), outbound webhooks ("Automations") and Workflow Signals. **M4 is in
progress**: the Windows desktop app has shipped and macOS is an unsigned beta; mobile, packaging and
live streaming are not built. Treat any "milestone N is not built" claim in older notes as stale -
see `README.md`'s roadmap and `docs/Overall_Synopsis_of_Platform.md` for the current truth.

For a one-screen picture of the runtime - components, the primary request path, external
dependencies and trust boundaries - open **`docs/Runtime_Architecture.html`** (generated from
`docs/Runtime_Architecture.archify.json`; see "Architecture diagram" below).

## Architecture & data flow

Four deployables, a shared domain library compiled into the API, and one published npm package -
communicating across process/language boundaries:

| Component | Stack | Path |
|---|---|---|
| API / auth / orchestration | ASP.NET Core (**.NET 10**) + EF Core + SignalR + OpenIddict | `src/Diariz.Api` |
| Domain model + migrations (library) | EF Core + Postgres/pgvector | `src/Diariz.Domain` |
| Transcription worker | Python: WhisperX (large-v3) + pyannote 3.1 + SpeechBrain ECAPA, GPU | `src/Diariz.Worker` |
| Web UI | React 19 + TS + Vite + Tailwind v4 | `apps/web` |
| Desktop shell | Electron (mic + system audio; Windows tray + macOS beta menu-bar) | `apps/desktop` |
| n8n community node (published to npm, not deployed here) | TypeScript, zero runtime deps | `integrations/n8n-nodes-diariz` |

**End-to-end flow:** client records → `POST /api/recordings` (multipart) → API stores the blob in
MinIO and a `Recording` row in Postgres → API creates a `Transcription` row (versioned) and
**enqueues a job on a Redis Stream** → Python worker `XREADGROUP`s the job, downloads the blob,
runs WhisperX→align→pyannote, and **POSTs segments back** to `internal/transcriptions/result` →
API persists `Segment`s + seeds `Speaker` rows → notifies the browser over **SignalR**
(`RecordingStatusChanged`) → the detail page refetches.

### Cross-boundary contracts (the non-obvious glue)

- **Redis Stream job queues - there are eleven, not one.** `RedisJobQueue` (`Api/Services/JobQueue.cs`)
  enqueues onto **11** streams. **Three** are consumed by the Python worker, which reads all of them
  under consumer group `workers`: `transcription-jobs`, `audio-merge-jobs` and `voiceprint-jobs`
  (`worker/config.py`). The other **eight** are drained **in-process by the API's own
  `BackgroundService`s** - summarization, meeting minutes, section summary, section minutes, actions,
  tags, formula runs and embeddings (each `*Worker.cs` in `Api/Services` calls `StreamReadGroupAsync`).
  So "the worker" in a stack trace may mean either process: check which stream the job is on.
  `Program.cs` registers 15 `AddHostedService`s in total - the eight stream consumers plus backfills
  (tag, embedding, storage), retention (audio, LLM usage) and the LLM usage writer.
- **Transcription job payload.** The job payload is JSON with **PascalCase**
  keys (`TranscriptionId`, `BlobKey`, `Model`) — produced by .NET, consumed by Python. The worker's
  callback bodies are also PascalCase so .NET model binding works. Keep both sides in sync when
  changing `TranscriptionJob` / `TranscriptionResult` / `Segment` shapes.
- **Worker → API callback** uses route `internal/transcriptions/*` and is authenticated by a shared
  secret header `X-Worker-Secret` (= `CALLBACK_SECRET`), **not** JWT. It is not user-facing.
- **Speaker identification (voiceprints).** The worker also emits a per-speaker **ECAPA embedding**
  (SpeechBrain `spkrec-ecapa-voxceleb`, 192-d) in the callback's `Speakers: [{Speaker, Embedding}]`
  (gated by `ENABLE_SPEAKER_EMBEDDINGS`; `pipeline._speaker_embeddings` pools each speaker's segment audio
  and L2-normalises). `WorkerCallbackController` stores it on `Speaker.Embedding` (`vector(192)`) and
  **auto-identifies** via `ISpeakerIdentifier` — but never overrides a manually-named speaker.
  Enrolment/reassignment/erasure live in `PeopleController` + `RecordingsController` (the `vector(192)`
  cosine match is Postgres-only, so it's faked in unit tests and verified in integration).
- **Identification is platform-wide, and so is every voiceprint.** `ISpeakerIdentifier.RankAsync` takes an
  embedding and nothing else: it scans **every** `Person` with an embedding who has not opted out, with no
  owner filter, and `PeopleDirectory.RecomputeVoiceprintAsync` averages **every** `VoiceSample` for that
  person, again with no owner filter. One person is one record and one centroid, however many colleagues
  contributed to it — which is what makes an erasure request a single deletion.
  The consequence is load-bearing and easy to miss: **an ordinary user, with no permission at all, changes
  recognition for the whole platform** whenever they name a speaker on their own transcript or confirm one
  in Review Voice Matches (both go through `ISpeakerAssignment.AssignAsync`, which enrols a sample and
  rebuilds the shared centroid). That is deliberate — only someone who was in the meeting can answer
  who a voice is — but it means any change to enrolment, exclusion or centroid maths is a change to
  everyone's recognition, not one user's. The counterweights are that automatic matches never enrol, an
  opted-out person can never be enrolled, and dropping a sample excludes rather than deletes it.
- **The four identification settings live on `PlatformSettings`**, not config: `IdentificationThreshold`
  (accept at or below), `IdentificationConfirmBand` (ask up to), `IdentificationMargin` (clear air over the
  runner-up) and `IdentificationMinSpeechMs`. They replace the compiled `Identification:Threshold`, and the
  decision is three-way — accept / suggest / ignore, per `IdentificationRules.Decide` — not a single
  cut-off. `Identification:Enabled` remains a server-level master switch.
- **Summarisation queue (in-process).** The archetype for all eight in-process streams above:
  `summarization-jobs` (consumer group `summarizers`) is **produced and consumed entirely within the
  API** — `RedisJobQueue.EnqueueSummarizationAsync`
  enqueues, and `Services/SummarizationWorker` (a `BackgroundService`; one of the API's eight stream consumers)
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
  unset. `Recording.Source` (`Microphone`/`System`/`Upload`) is captured at upload — **append only, never
  renumber** (ints in Postgres). `RecordingStatus` gained `Summarizing = 6` — same append-only rule.
- **File uploads** (the web "Upload" button) reuse `POST /api/recordings` with `source=Upload`. Only that
  source is gated: `AudioFormats` (pure, magic-byte sniff — never trusts the client extension/MIME) +
  `UploadOptions` (`Uploads:MaxBytes` 500 MB, `Uploads:AllowAac` — M4A/AAC is patented, so toggleable;
  royalty-free formats + MP3 always pass). Decoding is ffmpeg in the worker, so no new formats need worker
  changes. Uploads carry no client duration: the worker measures it (`pipeline._duration_ms`) and returns
  `TranscriptionResult.DurationMs`, which the callback backfills onto `Recording.DurationMs`; the worker also
  rejects audio over `MAX_AUDIO_SECONDS`.
- **Speaker renames are preserved across re-transcribes.** Worker emits diarization labels
  (`SPEAKER_00`...); the callback seeds a `Speaker` row per new label with `DisplayName = label`,
  and the UI's rename updates `DisplayName` only.
- **pgvector** column is `vector(768)` on `Segment.Embedding` (sized for `nomic-embed-text`).
  Embeddings/RAG are **live**, not dormant: `EmbeddingWorker` fills the column off the `embedding-jobs`
  stream and chat/search read it (semantic results fused with keyword). Changing the embed model means
  a migration to resize the column **and** a re-embed of existing rows - the dimension is server-pinned
  (`Embedding__Dimension`) and must match the column, so a mismatch fails at query time, not at startup.
  `OnModelCreating` applies the pgvector extension + column **only when the provider is Npgsql**
  (`Database.IsNpgsql()`); under other providers (the in-memory test provider) the property is
  `Ignore`d. Keep new Postgres-only model config behind that same guard so unit tests can build the model.
- **Split queries are the app-wide default — you do not need `.AsSplitQuery()`.**
  `DiarizDbContext.OnConfiguring` sets `QuerySplittingBehavior.SplitQuery` (0.228.4), guarded on the provider
  being relational for the same reason as the `Database.IsNpgsql()` guards above — the in-memory unit provider
  would otherwise get a second provider registered and throw. Several collections hang off `Recording`
  (`Speakers`, `Actions`, `Tags`, `Transcriptions`→`Segments`), and EF's *own* default returns the **cartesian
  product** of every sibling collection an `Include` chain names. The results are identical either way — EF
  de-duplicates the product back into the right object graph — so it is invisible to behaviour and to any
  results-based test, and shows up only as row count and sort spill. Measured on prod before the fix: a
  recording-detail query returned 104,720 rows for 334 rows of real data and spilled 207 MB (0.228.2); an MCP
  read of the same recording returned 517,825 rows and spilled 1.97 GB (0.228.3). It compounds rather than
  plateauing, because the count is a *product* — each new action item multiplies the whole result again.
  It was fixed by hand twice, and both audits missed sites (nine across six files), which is why the default is
  inverted rather than left to discipline. Three things follow:
  - **Writing a query:** just write the `Include`s. The explicit `.AsSplitQuery()` calls still in
    `RecordingsController` and the MCP tools are redundant belt-and-braces, not a pattern to copy.
  - **Opting out:** `.AsSingleQuery()` where one statement is genuinely needed. The trade-off the default
    accepts is that split queries run as separate statements with no shared snapshot, so a collection could in
    principle be read either side of a concurrent write.
  - **The one real caveat:** a row-limiting operation (`Skip`/`Take`) on a query that also `Include`s a
    collection needs a deterministic `OrderBy`, or the separate statements can disagree. Nothing in the app
    does this today (every paged query projects with `Select` instead), so check it if you add one.
  `SplitQueryIntegrationTests`, `SplitQueryEverywhereIntegrationTests` and `GlobalSplitQueryIntegrationTests`
  pin all of the above; the last one asserts the real `Program.cs` host resolves a context with the default on,
  since a test proving only that *test-built* contexts get it would be a false positive.
- All user-scoped queries filter by `UserId` from the JWT `NameIdentifier` claim — preserve this
  ownership check on every recording endpoint.

### Architecture diagram

`docs/Runtime_Architecture.html` is a standalone, self-contained page (open it in a browser - no build,
no network) showing the 10 core runtime components, the primary capture → transcript path, external
dependencies and the trust boundaries. It is **generated**, not hand-edited: the source of truth is
`docs/Runtime_Architecture.archify.json`, rendered with the `archify` skill. To change it, edit the
JSON and re-render - never edit the HTML:

```bash
node <archify>/bin/archify.mjs deliver architecture \
  docs/Runtime_Architecture.archify.json docs/Runtime_Architecture.html \
  --quality showcase --repo-root . --json
```

The spec pins a **commit SHA** in `meta.repository.revision` and cites source files per component;
rendering **fails** if a cited path does not exist at that revision, so re-pin the revision when you
regenerate. The diagram deliberately carries supporting detail in its cards rather than extra edges -
add facts to a card, not a new arrow. It is a **reference doc, not a release-checklist target**: refresh
it when the component topology actually changes (a new deployable, queue, datastore or external
dependency), not for ordinary feature work.

## Test-driven development (required)

**This project uses TDD. Write the failing test first, watch it fail, then write the minimal code
to pass.** No production code without a failing test that preceded it. This applies to new features,
bug fixes, and behavior changes. When fixing a bug, first add a test that reproduces it (red), then
fix (green). Exceptions (throwaway spikes, generated code, pure config) need a human's sign-off.

Keep test output pristine — a passing run has no errors or warnings.

**The web suite enforces that, and you cannot check it locally on Windows.** `src/test-setup.ts` hooks
`console.error` and **fails any test that lets a React state update escape `act(...)`**, naming the source
line that updated. A test that provokes an error on purpose declares it with `expectsConsoleError(/.../)`
from the same file rather than spying on `console.error` - a spy would take the console away from that
guard for the length of the test.

The catch: **`vitest` prints nothing a test logs to `console.log`/`console.error` on the Windows dev box**
(all three pools, even a config-free scratch project - see issue #667). A clean local run is therefore not
evidence that the output is clean; it only ever shows the guard's own failures. To see what CI sees, run
the suite on Linux - mount the repo read-only into `node:24`, `tar` everything except `node_modules` into
the container, `npm ci`, then `npx vitest run`. The counts match CI exactly. This is how 143 `act()`
warnings survived unnoticed long enough to become issue #665.

## Versioning & release notes (required)

**`main` is branch-protected: every change lands through a Pull Request that passes CI - never commit or
push to `main` directly, and never merge locally.** So the **default way to finish any branch is to push it
and open a PR** (`git push -u origin <branch>` + `gh pr create`), not a local merge - do this without asking
unless the user says otherwise. Each PR must satisfy the release + docs rules below before it can merge.

**Every fix starts as a GitHub issue and ends with the PR closing it.** When the user asks for a bug or
problem to be fixed, **open a GitHub issue first** (`gh issue create`, before writing the fix) describing the
symptom, and then make the PR that fixes it **close that issue automatically** by putting a closing keyword in
the **PR body** - `Fixes #<n>` (or `Closes #<n>`) on its own line. Do this without asking. Notes:
- **Scope:** fixes only. A new feature, chore, refactor, or docs-only change does not need an issue unless the
  user asks for one - and if the user is *already* pointing at an existing issue, reuse that number instead of
  opening a duplicate.
- Write the issue from the **user-visible symptom** (what went wrong, how to reproduce, what was expected), not
  from the fix you are about to write - it is the record of the bug, and the PR is the record of the fix.
- **The issue consumes a number from the same sequence as PRs**, so the PR number is usually the issue number
  + 1 - but confirm it rather than assuming (it feeds the `pr:` field in `apps/web/src/lib/releases.ts`, which
  has to be written before `gh pr create` exists to report the real number).
- The closing keyword must be in the **PR body**; GitHub only auto-closes from the PR description or from a
  commit on the default branch, not from a PR title or a later comment. Verify the issue actually closed after
  the merge, and close it by hand if it did not.

**Every PR ships exactly one release: bump the version and add one release-notes entry.** The scheme
is **Major.Minor.Build** (currently `0.x`).

- **Bump rule:** a **functional enhancement** bumps **Minor +1 and resets Build to 0** (e.g. `0.1.2`
  → `0.2.0`); any other PR (fix / chore / docs / refactor) bumps **Build +1** (e.g. `0.2.0` → `0.2.1`).
  **Only bump Major when the user explicitly asks.**
- **The canonical version is `/version.json`.** Bump it in lockstep with its mirrors:
  `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`
  (`<Version>`), and `integrations/n8n-nodes-diariz/package.json` (which is what npm publishes under, so a
  stale one cannot be corrected after the fact). The web build injects it (`__APP_VERSION__` via
  `vite.config.ts`/`vitest.config.ts`) and the API reports it at `GET /health`. `RELEASES[0].version` in
  `apps/web/src/lib/releases.ts` **must equal** `version.json` (asserted by `releases.test.ts`), and
  `versionMirrors.test.ts` asserts every mirror.
- **Add a release entry** to the top of `RELEASES` in `apps/web/src/lib/releases.ts` with: `version`,
  `date`, `pr` (the GitHub PR number), `headline`, a **PR-level prose `summary`** (enough for a user to
  understand the impact), and `added`/`changed`/`fixed` bullet lists as applicable.
- **When the app's scope changes**, update the About-box `CAPABILITIES` summary in the same file (and
  the disclaimers list in `apps/web/src/components/AboutModal.tsx` if a new third-party library/model
  is introduced). `CAPABILITIES` is a **concise two-column markdown table** (`| Feature | Description |`,
  one line per feature) — add/edit a row, don't reintroduce long prose (the About box renders it via
  `renderMarkdown`; `.chat-md` table CSS in `apps/web/src/index.css` styles it).
- **Keep `README.md` current.** When a PR changes what the app does — a new user-facing feature, a stack
  change, or a shipped roadmap milestone — update the README in the same PR (it mirrors the
  `CAPABILITIES`/release-notes edits above). The README's **Features** section is a **two-column table**
  (`| Feature | Description |`, one concise line each) that links to **`docs/features.md`** — the canonical
  **full prose** feature list. On a feature change, update **all three in lockstep**: the README Features
  table row, the matching `docs/features.md` bullet, and the About-box `CAPABILITIES` table row (plus
  **Architecture**/**Roadmap** in the README when relevant). The README deliberately does **not** carry a
  version number (it would drift) — the version lives only in `version.json` / `releases.ts`.
- **User help articles are NOT a fourth sync target.** `apps/web/src/content/help/**` is task-oriented
  "how do I / what happens if" prose written for a user inside the app; the README table,
  `docs/features.md`, and `CAPABILITIES` are *inventories* of what exists. Different genres, deliberately
  not kept in lockstep line for line. Update a help article when the **behaviour a user relies on**
  changes, not merely because a feature row changed. Content is **ASCII only** and each article carries a
  `title` / `summary` / `group` / `order` front-matter block; the `summary` is what the contextual `?`
  popover shows, so keep it to two or three sentences. `content/help/helpContent.test.ts` enforces all of
  that, and fails the build if a `<HelpButton topic="...">` points at an article that does not exist.
- **Keep the architecture & schema docs current.** Two reference docs must not be allowed to drift:
  `docs/Overall_Synopsis_of_Platform.md` (components, data flow, cross-boundary contracts, deployment) and
  `docs/Data_Schema.md` (every Postgres table + column/key/index/cascade, the pgvector columns/dimensions, the
  enums, and the MinIO bucket/key layout). When a PR makes a **relevant change, update the matching doc in the
  same PR**:
  - **Schema / storage** (any new or changed entity, column, index, FK/cascade, migration, vector dimension,
    JSON blob, MinIO bucket/key/lifecycle) → update **`Data_Schema.md`** (and its migration-history table).
  - **Architecture / major feature** (a new component or deployable, a new queue/stream or cross-boundary
    contract, a new external dependency or LLM/worker flow, an auth/RBAC change, a shipped milestone, a port
    change) → update **`Overall_Synopsis_of_Platform.md`**.

  A change can touch both (e.g. a feature that adds a table). Pure cosmetic/UI tweaks and bug fixes don't
  require a doc edit. These are internal docs (no version number) — like the README, they don't gate the
  version bump, but they must be accurate.
- **State the deployment surface in every PR.** When opening a PR, say whether shipping it needs a
  **desktop release** (a new installer, cut by pushing a `v*` tag) or just a **server redeploy**. The
  desktop app is a thin shell that loads the web app from the server origin, so it only needs a new release
  when the PR touches the **desktop shell** — `apps/desktop/src/**`, `apps/desktop/build/**`,
  `electron-builder.config.js`, or desktop dependencies. A desktop release now covers both the **Windows
  installer** and the **macOS `.dmg`** (built on a Mac). **Web (`apps/web`) and API (`src/Diariz.Api`)
  changes ship by redeploying the server** and
  are picked up by installed desktop apps automatically. A lockstep version bump to `apps/desktop/package.json`
  alone does **not** require a desktop release (desktop version numbers may skip). Docs/CI-only PRs need
  neither — say so.

- **Release checklist (run this for every user-facing PR).** Update all of these **in lockstep, in the same
  PR** (details for each are in the bullets above):
  1. `version.json` **and** its four mirrors (`apps/web/package.json`, `apps/desktop/package.json`,
     `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`).
     `apps/web/src/lib/versionMirrors.test.ts` fails the build if any of them drifts - it exists because the
     n8n node silently sat at `0.1.0` for ~70 releases, and an npm version cannot be corrected once published.
  2. The `RELEASES[0]` entry in `apps/web/src/lib/releases.ts` (must equal `version.json`).
  3. The About-box **`CAPABILITIES`** table row in `releases.ts` (on a scope change) + `AboutModal.tsx`
     disclaimers (on a new third-party library/model).
  4. The **README Features** table row.
  5. The **`docs/features.md`** prose bullet - the canonical full feature list; **always** update it alongside
     the README Features row, never one without the other.
  6. **`docs/Overall_Synopsis_of_Platform.md`** whenever architecture, a cross-boundary contract, an external
     dependency, an endpoint/port, or a deployment detail changes.
  7. `docs/Data_Schema.md` whenever schema/storage changes (see the schema bullet above).

  A pure bug-fix / cosmetic PR still does 1-2; it only touches 3-7 when the corresponding thing actually
  changed. A **docs/CI-only PR** skips 1-3 (no version bump) but keeps 4-7 accurate - say so in the PR.

- **Exclusion: `omi/` is out of scope for the whole release checklist.** `omi/` is a vendored copy of the
  open-source Omi wearable firmware + hardware (BasedHardware/omi, MIT), kept here as the base for an
  **exploratory side project**: repurposing the device for offline ambient capture that is later uploaded to
  Diariz. It is not built, tested, shipped, or referenced by any Diariz deployable. So a PR that **only**
  touches `omi/**` (including `omi/firmware/docs/**`):
  - does **not** bump `version.json` or any mirror, and adds **no** `RELEASES` entry;
  - is **not** mentioned in `releases.ts` `CAPABILITIES`, `AboutModal.tsx`, `README.md`,
    `docs/features.md`, `docs/Overall_Synopsis_of_Platform.md`, `docs/Data_Schema.md`, or
    `apps/web/src/content/help/**`;
  - documents itself inside **`omi/firmware/docs/`** only.

  Say "omi/ only - excluded from the release checklist" in the PR body. This holds **until the side project
  produces a working device-to-Diariz path that a user can actually use**; the PR that first ships a
  user-facing capability (an upload/sync path, a settings surface, an API endpoint) is a normal Diariz PR and
  does the full checklist, and should also delete this exclusion. A PR that touches `omi/**` *and* Diariz code
  is a normal Diariz PR - the exclusion only covers the `omi/` half of the diff.

The About box (account menu → About) and the `/release-notes` page render from this data.

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

**Backup-restore compatibility:** platform restore accepts backups from older, forward-compatible schema
versions and migrates their data up to the current schema (see the backup/restore section in
`docs/Overall_Synopsis_of_Platform.md`). So if a new migration is **not** forward-restore-safe (a destructive
column drop/rename, a pgvector dimension change, a semantic data reshape that an older dump can't survive),
**bump `MaintenanceController.CurrentFormat` in the same PR** - that fence hard-rejects older backups instead
of silently corrupting them.

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

**ASR backend / AMD ROCm.** The Whisper step is pluggable via `ASR_BACKEND` (`config.py` → `pipeline._asr`):
`whisperx` = faster-whisper/CTranslate2 (CUDA default) or `whisper` = openai-whisper (pure PyTorch). The
PyTorch path exists so the worker can run on **AMD ROCm**, where CTranslate2 has no GPU support — see
`src/Diariz.Worker/Dockerfile.rocm` (base `rocm/pytorch`, no torch reinstall) and the standalone
`deploy/docker-compose.rocm.yml` (AMD GPU via `/dev/kfd` + `/dev/dri`, `ASR_BACKEND=whisper`). Alignment/
diarization/voiceprints are unchanged — PyTorch-ROCm keeps the **`"cuda"` device string**, so `DEVICE` stays
`cuda` on AMD. The API/web are vendor-agnostic; only the worker image differs. Initial target: Strix Halo
(gfx1151); ROCm inference is not yet hardware-validated (the ASR dispatch is unit-tested; CUDA path unchanged).

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
via `tsconfig.json`. Component testing is wired: `@testing-library/react` + the react plugin in
`vitest.config.ts`, with `src/test-setup.ts` booting i18n (pinned to `en`). The established pattern is
`vi.mock` of `../lib/api` / `../lib/signalr` / `../lib/rooms`, rendering inside `MemoryRouter` +
`QueryClientProvider` + `SelectionProvider` — see `components/RecordingsPanel.test.tsx`.

### Desktop (Electron — Windows system-tray + macOS menu-bar app)
```bash
cd apps/desktop && npm run dev    # DIARIZ_DEV=1 → loads the Vite dev server, skips first-run setup
npm test                          # pure unit tests (node --test, no Electron)
npm run dist                      # NSIS installer on Windows; unsigned .dmg on macOS (run on a Mac)
```
A **thin tray shell** (`apps/desktop/src/`): `main.js` owns the tray, single-instance
lock, close-to-tray, and a **first-run setup window** (`setup.html`) that stores the **server address**
(validated via `GET {url}/health`) in `electron-store`. The main window then **loads the web app from that
server origin** (so the SPA is same-origin — no bundled SPA, no API-base override needed; the old
`__DIARIZ_API_BASE__` is gone). Only the shell can capture **system audio**
(`setDisplayMediaRequestHandler` → `audio: "loopback"`; the **same** handler captures **macOS
ScreenCaptureKit** system audio on Electron 43 / macOS 13+, granted via the Screen Recording permission - no
mac-specific code needed); it exposes `window.diariz.isElectron` to enable the "System audio" recorder
option. Releases: `electron-builder` via `.github/workflows/desktop-release.yml` on a `v*` tag → GitHub
Releases (or a self-hosted feed when `DIARIZ_PUBLISH=generic`). The tag currently builds **Windows only**
(NSIS); the unsigned macOS `.dmg` is built by hand on a Mac until the signed CI job lands.

**Tray-driven recording (phase 2).** The tray menu can start/stop recording. Recording itself always
happens in the **web app's** `MediaRecorder` (the desktop has no recorder of its own), so the two sides
talk over IPC: the preload exposes `window.diariz.onTrayCommand(cb)` (tray → renderer `start`/`stop`) and
`reportRecorderState(state)` (renderer → tray phase). The web `Recorder` connects them via
`apps/web/src/lib/trayRecorder.ts` and drives the **same single recorder** as the on-screen button; it
reports `idle/recording/uploading/error` back. `main.js` keeps the recorder state machine, renders the
dynamic menu/tooltip from the **pure** `src/recorderState.js` model (`trayRecorderItems`/`trayTooltip`/
`notificationFor`, unit-tested with `node --test`), runs a 1 s ticker to keep the `Stop Recording (mm:ss)`
label live, and raises Windows `Notification`s on start/upload. The record items are disabled until the
renderer reports `ready` (loaded + signed in).

**Auto-update + launch-at-login (phase 3).** `setupAutoUpdater()` runs **only in a packaged build**
(`app.isPackaged`; it lazily `require`s `electron-updater`, so dev `npm start` never loads it) and uses the
**same publish feed** electron-builder ships in `app-update.yml` (GitHub Releases, or a fork's generic feed).
It auto-downloads in the background (on launch + every 6 h + a manual **Check for Updates…** tray item); on
`update-downloaded` it shows a notification and a **Restart to update (x.y.z)** tray item (→
`autoUpdater.quitAndInstall()`; `autoInstallOnAppQuit` also applies it on a normal quit). The user-facing
copy/menu item come from the **pure** `src/updateState.js` model (`updateRestartItem`/`notificationForUpdate`,
unit-tested) — automatic checks stay silent, manual checks always give feedback. An **Open at Login / Start
with Windows** tray checkbox (platform-aware label) toggles `app.setLoginItemSettings({ openAtLogin })` (off
by default). Builds are **unsigned** for now (code signing is deferred), so SmartScreen may warn on first
install.

**macOS (Electron — beta, unsigned).** The same shell runs on macOS as a **dock + menu-bar** app
(hide-to-menu-bar on window close). Platform branches in `main.js` (`process.platform === "darwin"`): a
minimal **app menu** (`appMenu`/`editMenu`/`windowMenu` roles - so Cmd-Q/C/V work), a **monochrome Template
menu-bar icon** (`build/trayTemplate.png` + `@2x`, a microphone glyph generated by `build/make-tray-icon.js`,
icon-only - a title made it too wide behind the notch), `setAppUserModelId` guarded to win32, and an **"Open
in Browser"** tray item. **Updates:** Squirrel.Mac can't update an unsigned app and there's no mac feed, so
on darwin `setupAutoUpdater`/`checkForUpdates` skip electron-updater and use a **manual GitHub-Releases
check** (`isNewerVersion` in `updateState.js` vs the latest tag; opens the Releases page when newer).
electron-builder `mac` block is `identity: null` (unsigned) → a `.dmg` that opens via right-click → Open.
**Deferred to later macOS milestones:** Developer ID signing + notarization + Squirrel.Mac auto-update (needs
an Apple Developer account), a `macos-14` CI job (one `v*` tag then builds both OSes), and **Sign in with
Apple**. Design: `docs/macOS_Desktop_App_Guide.md`.

### Full stack (Docker)
```bash
cd deploy
cp .env.example .env      # JWT_KEY, CALLBACK_SECRET, HF_TOKEN, SEED_EMAIL/PASSWORD, MinIO creds
docker compose up --build # web, api, postgres, redis, minio, GPU worker
```
The Compose project is named **`diariz`** (top-level `name:` in `docker-compose.yml`; Docker forces
lowercase, so it is `diariz` not `Diariz`) rather than defaulting to the `deploy` directory name. The
**`web`** service builds `apps/web` (`apps/web/Dockerfile`) and serves the static SPA via nginx at
**http://localhost:8081**, proxying `/api`, `/hubs`, and `/mcp` to the `api` container (same-origin, so no CORS
needed — `apps/web/nginx.conf`). `/mcp` (the MCP server, Streamable HTTP) is proxied with `proxy_buffering off`
so the SSE stream isn't stalled; any **outer** reverse proxy in front of the web container must forward `/mcp`
(buffering off) too, or Claude can't connect. The GPU worker needs the NVIDIA Container Toolkit; for CPU comment
out the `deploy.resources` GPU block and set `WORKER_DEVICE=cpu WORKER_COMPUTE_TYPE=int8`.

## Conventions & gotchas

- **No em/en dashes in user-facing text.** Use a plain hyphen `-` (not `—` or `–`) in all UI strings,
  i18n catalogs (`apps/web/src/locales/**`, `src/Diariz.Api/locales/**`), release notes, and user-visible
  copy — user feedback on fancy dashes is negative. (Code, comments, and internal docs are unaffected.)
- **Never put production data in the repo or anywhere public.** Real people's names, email addresses,
  company names, recording titles, and transcript text from the running instance must not appear in code,
  comments, **test fixtures**, docs, commit messages, GitHub issues, or pull requests. The repo is public,
  and the people in those recordings did not consent to being named in it. This holds even when the data is
  the evidence for the change - a live query is a perfectly good reason to make a fix, and the finding is
  reported as **summary calculations**: counts, distances, percentages, how many rows fell into each
  category ("six samples across six people, three of them reassigned"). Where a table genuinely helps, use
  the *shape* (a column per category with counts) or invented placeholders, never the rows themselves.
  Invent names for fixtures - `Ada`, `Grace`, `Alice` - and never copy one out of the database.
- **Tests:** harnesses exist for all three stacks — .NET (`tests/Diariz.Api.Tests` + integration),
  web (`vitest`), and the Python worker (`pytest`, see the Worker section). No CI runs them on push yet.
- **Ports:** API `8080`; web UI (Docker/nginx) `8081`; web dev server `5173`. Two infra ports are **remapped on
  the host** to avoid clashing with other local instances: **MinIO S3 API** `9002→9000` and **Postgres**
  `5433→5432` (the latter published only for external tooling — psql/pgAdmin/test harnesses — and overridable
  via `POSTGRES_PORT`/`POSTGRES_BIND` in `deploy/.env`; a published port bypasses the host firewall, so
  `POSTGRES_BIND=127.0.0.1` keeps it host-only). Redis and the MinIO console (`9001`) are **not published** —
  the app never uses them from the host. In-container, services use the compose service names
  (`minio:9000`, `redis:6379`, `postgres:5432`).
- **MinIO/S3 quirk:** `AmazonS3Config` uses `ForcePathStyle` + region `us-east-1`. A prior bug
  required removing `DisablePayloadSigning` on `PutObject` for MinIO uploads to work — be cautious
  changing S3 request options in `Services/AudioStorage.cs`.
- Config binds via the options pattern (`Configuration/AppOptions.cs`): `Jwt`, `Storage`,
  `JobQueue`, `Worker` sections, settable through `__`-delimited env vars in compose.
- Worker model load is **lazy + cached** in `pipeline.py` (Whisper/align/diarizer load once and are
  reused across jobs — loading large-v3 + pyannote is expensive).
