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
| **Web** | React 19 + TypeScript + Vite + Tailwind v4 | `apps/web` | SPA UI (served by nginx in Docker) |
| **Desktop** | Electron thin shell - Windows tray + **macOS (beta) menu-bar** | `apps/desktop` | Mic + system audio (Windows loopback / macOS ScreenCaptureKit), tray recording; auto-update on Windows, manual update check on macOS; loads the web app from the server origin |

Infrastructure (via Docker Compose, project name **`diariz`**):

- **PostgreSQL + pgvector** (`pgvector/pgvector:pg16`) — relational data **and** voiceprint/embedding vectors.
- **Redis** (`redis:7`) — job queues (Redis **Streams**), nothing is stored long-term here.
- **MinIO** (S3-compatible) — original audio blobs and uploaded attachment files.

### Ports (Docker / local dev)

| Service | In-container | Host (Docker) | Dev |
|---|---|---|---|
| API | 8080 | 8080 | 8080 |
| Web (nginx, proxies `/api` + `/hubs` + `/mcp`) | 80 | **8081** | Vite dev server **5173** (proxies to 8080) |
| Postgres | 5432 | 5432 | 5432 |
| Redis | 6379 | 6379 | 6379 |
| MinIO S3 API | 9000 | **9002** | — |
| MinIO console | 9001 | not published | — |

The MinIO S3 API is remapped on the host (9002) to avoid clashing with other local MinIO instances; the web
console (container 9001) is **not published** - the app never uses it (the API reaches MinIO in-network at
`minio:9000`), so port-forward or `docker exec` if you need it. In-container, services address each other by
Compose service name (`minio:9000`, `redis:6379`, `postgres:5432`, `api:8080`).

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
   noise suppression / auto gain / mono) applied to `getUserMedia`. While recording, a **Web Audio
   `AnalyserNode`** taps the same stream to drive a live **input-level meter** (`lib/audioLevel.ts` +
   `InputLevelMeter.tsx`; a passive read, not connected to output) with a subtle sustained-silence hint. The
   client `POST`s multipart to `POST /api/recordings` (`source = Microphone | System | Upload`).
2. **Store + enqueue.** The API streams the blob into **MinIO** (`recordings` bucket, key `{userId}/{recordingId}{ext}`),
   writes a **`Recording`** row, creates a **`Transcription`** row (version 1) and **enqueues a job** on the
   Redis stream **`transcription-jobs`** (consumer group **`workers`**). Uploads are gated by magic-byte
   format sniffing (`AudioFormats`) + size cap (`Uploads:MaxBytes`) + the owner's storage quota.
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
field is **omitted entirely** so non-reasoning endpoints aren't broken. The **per-request timeout** is a single
platform-wide value (`PlatformSettings.LlmTimeoutSeconds`, default 120, Platform-Admin-editable on Settings → Model
Settings), applied by the resolvers and enforced via a linked `CancellationTokenSource` in each client; the typed
`HttpClient`s themselves are registered with `Timeout = InfiniteTimeSpan` so that per-request value is the **only**
cap (otherwise `HttpClient`'s default 100 s silently capped anything longer). The streaming chat client keeps the
default timeout for its header phase and relies on client-disconnect for cancellation.

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
  **meeting type** (`MeetingType`) — a reusable template of H1/H2 **sections** whose blocks are **boilerplate
  text**, **substituted recording values** (`date`/`time`/`title`/`attendees`/`duration`, and `action_items`
  which renders the deterministic actions table), or **model prompts**. A recording's `MeetingTypeId` (null → the
  seeded **General Meeting** default) selects it. Types are **Platform** (admin-owned, shared) or **Personal**
  (a user's own); the app seeds a standard set on startup (`MeetingTypeSeeder`, insert-if-missing by `Key`). The
  `MeetingTypeMinutesGenerator` resolves the type, reads the platform-wide **generation mode**
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
- **Tag cloud (pipeline + backfill).** Every transcription also gets **weighted topic tags** for the web's
  cross-transcript tag cloud: a Redis stream **`tag-cloud-jobs`** (group `tag-extractors`) with its own
  `TagsWorker` (singleton `BackgroundService`) runs `TagsProcessor` → `TagsClient`/`TagsPrompt` (editable
  prompt `prompts/tagcloud.md`; strict JSON array of `{tag, weight}` hardened for reasoning models via
  `ActionsPrompt.ExtractJsonArray`), enqueued alongside the summary/actions after transcription. Unlike
  actions, tags are **machine-only**, so the processor **replaces the recording's `RecordingTag` set
  wholesale** on every (re)transcription — guarded instead against **stale jobs** (only the recording's
  latest transcription version may write) — and sets `Recording.TagsExtractedAt` even on a zero-tag result.
  Status-neutral; no LLM configured → silent no-op with the marker left null. **Backfill:** a one-shot
  `TagBackfillService` enqueues jobs for every never-tagged recording at startup (gated on a server-wide
  summarisation endpoint), and a Platform Admin can trigger the same via
  `POST /api/platform/settings/run-tag-backfill` (Settings → Maintenance; returns the enqueued count —
  per-user-only LLM configs are covered this way). The web reads **`GET /api/tags`**: owner-scoped,
  case-insensitive aggregation (count + summed weight + carrying recording ids) that the left panel's
  **Tags tab** renders as a flat weighted cloud (log-scaled font sizes, single-select filter, an expanded
  80% modal sharing the same selection state).
- **Folder (section) pages (async roll-ups).** A **folder page** (`GET /api/sections/{id}` +
  `SectionPageController`, web route `/sections/:id`) aggregates everything across a section **and its child
  sections**: stats, an LLM **folder summary**, consolidated **folder minutes**, and the actions/notes/
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

## Meeting notes (the user's own notes)

Users can jot their **own note lines** for a meeting - sparse trigger phrases, questions, observations - as
a first-class entity (**`MeetingNote`**, row per line). A line is anchored to **either a recording or an
upcoming Google Calendar event**: prep notes are taken on the calendar-event preview page
(`/calendar-event/:id`, CRUD at `/api/calendar/events/{calendarId}/{eventId}/notes`) and are **adopted onto
the recording automatically** when its calendar link forms (`MeetingNoteAdoption`, called inside the
`LinkCalendar` chokepoint that both the auto-match save and manual linking use - one-way and additive).
Recording-anchored lines live on the detail page's **Notes tab** (CRUD at `/api/recordings/{id}/notes`);
lines can carry a **`CapturedAtMs`** recording-clock timestamp (immutable; stamped lines deep-link to that
moment in the transcript via the existing `?t=` navigation). **Transcript weave:** a stamped note is rendered
inline in the **Transcript tab** right after the segment being spoken when it was written (pure
`lib/transcriptNotes.ts` `weaveTranscript`; anchor = greatest `StartMs ≤ CapturedAtMs`) as its own green row
with the current user as the "speaker"; the same anchor rule (server-side `TranscriptNoteAnchor`) makes the
**merge-segments** action treat a note as a boundary, so `SegmentMerger` won't collapse same-speaker text from
either side of a note (a `BreakBefore` flag on the segment after each note's anchor). **Live capture:** while recording, a
`LiveNotesPanel` auto-opens beside the recorder (dismissable; preference in localStorage) - each
Enter-committed line is stamped with the current *recorded* time (`recorderTiming`, pause-aware), mirrored
to IndexedDB (`lib/pendingNotes.ts`, its own `diariz-notes` DB, keyed by user) so a crash never loses lines,
and bulk-attached to the recording right after upload; an attach failure keeps the lines durable (with the
recording id) behind a retry banner, and recovered pending recordings adopt their stashed lines on
re-upload. **The minutes weave:** notes feed minutes generation two ways (`MeetingMinutesProcessor` loads
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
> is served for `/mcp` (or a POST returns 405) and clients report "cannot load the MCP server". The OAuth server
> adds the same requirement for **`/connect/`** (authorize/token/register) and **`/.well-known/`** (discovery +
> protected-resource metadata): both nginx and any outer proxy must forward them to the API, or an OAuth client
> gets the SPA index.html instead of the metadata and the claude.ai connection never starts. (`/oauth/consent`
> is deliberately a **SPA** route and must NOT be proxied.) **The `X-Forwarded-Proto` header must carry `https`**
> all the way to the API - OpenIddict rejects its own endpoints as non-HTTPS otherwise (`ID2083`). The web
> nginx forwards the outer proxy's incoming `X-Forwarded-Proto` (falling back to its own `$scheme`) rather than
> clobbering it, so **the outer proxy must set `X-Forwarded-Proto: https`** (most do by default).

- **Per-user token auth.** The endpoint is guarded by a dedicated auth scheme (`McpBearerAuthenticationHandler`,
  scheme `"Mcp"`), separate from the browser JWT. A user generates a personal access token in **Preferences →
  Claude / MCP access** (`McpTokensController`, `/api/user/mcp-tokens` GET/POST/DELETE, JWT-authed). Tokens are
  `dz_mcp_` + base64url(32 random bytes); **only their SHA-256 hash is stored** (`McpAccessToken`), shown to the
  user **once** at generation. On each `/mcp` request the presented bearer is hashed and looked up
  (`McpTokenAuthenticator`), the owner's `NameIdentifier` claim is set (so every query stays owner-scoped like
  the rest of the API), and `LastUsedAt` is recorded. Multiple named tokens per user; revoke = delete the row.
- **Tools = the chat tool registry.** Low-level handlers (`DiarizMcpHandlers`) project the same
  `IChatTool`/`IChatToolRegistry` used by chat onto MCP `tools/list` + `tools/call` — no duplicate logic, and a
  new `IChatTool` lights up in both chat and MCP. The catalog is the user's **per-tool-enabled** tools
  (respecting Settings → Chat Tools per-tool choices, but **not** the chat *master* switch — the MCP opt-in is holding a
  token), **minus `add_as_attachment`** (which needs an in-chat selection). `send_email` is included (it can only
  ever email the user's own address). Each tool carries an MCP **`readOnlyHint` annotation** (from
  `IChatTool.ReadOnly` — true for every read/search tool, **false only for `send_email`**) plus
  `destructiveHint=false`, so clients can group read-only vs write tools (`McpToolProjection.Annotations`). Tool
  results' in-app deep-links are rewritten to **absolute** URLs (`McpLinkRewriter`, against `App:PublicUrl` or the
  request origin) so they're clickable in Claude.
- **Config.** `Mcp:Enabled` (default true) mounts the endpoint. `IHttpContextAccessor` provides the request's
  user + scoped services inside the handlers. **Claude Code:** `claude mcp add --transport http diariz {origin}/mcp
  --header "Authorization: Bearer dz_mcp_…"` (or the `headers` block in `.mcp.json`). **Claude Desktop** only
  accepts stdio servers in `claude_desktop_config.json`, so it connects via the **`mcp-remote`** bridge
  (`command: npx -y mcp-remote {origin}/mcp --header "Authorization:${AUTH}"`, token in `env.AUTH="Bearer …"` — the
  env indirection avoids mcp-remote splitting the header on its space). Preferences → *Claude / MCP access* shows
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
  **claude.ai can connect end-to-end**. Users **manage connections** in Preferences → *Claude / MCP access*:
  `OAuthConnectionsController` (`/api/oauth/connections`, JWT-authed, owner-scoped by subject) lists the granted
  OpenIddict authorizations (client name + connected date) alongside the personal tokens, and **revoke** deletes
  the authorization + its tokens so the client can no longer connect (refresh dies immediately; any issued access
  token lapses at its short lifetime). Config lives under the `McpOAuth` options block; the whole server is gated
  by `McpOAuth:Enabled` (on by default). **The OAuth-for-MCP arc is complete.**

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
  the owner's id **and role claims** — **full session parity**, so ownership checks and admin authorization work
  exactly as for a JWT. To make it satisfy every `[Authorize]` variant (including `[Authorize(Roles=…)]`, which
  authenticate with the default scheme), the **default authenticate scheme is a forwarding policy scheme**:
  `Bearer dz_api_…` routes to the ApiKey handler, everything else (JWT, or the query-string SignalR/audio/backup
  flows) to JWT. Isolation: a `dz_mcp_` token is rejected on `/api/*` and a `dz_api_` token on `/mcp` (each scheme
  accepts only its own prefix). The feature is **gated by `PlatformSettings.ApiAccessEnabled` (default off)** —
  the authenticator fails while it's off — and a Platform Admin toggles it in **Settings → Integration**; users
  manage tokens in **Preferences → Developers** (shown only when enabled). A **curated OpenAPI document** is
  published at **`/api/openapi/v1.json`** (`Microsoft.AspNetCore.OpenApi`, authenticated; the user-facing REST
  surface only — `api/*` minus the admin/OAuth prefixes `api/oauth`/`api/platform`/`api/admin`/`api/maintenance`,
  and the non-`api/` `internal/*`, `connect/*`, `.well-known/*`, `/mcp` — with a bearer security scheme declared,
  see `OpenApiCuration`), and a signed-in user can browse it via an in-app **Scalar** reference at
  **`/developers/api`** (lazy-loaded route, `@scalar/api-reference-react`), linked from both the Developers and
  Integration tabs.
- **RBAC:** `Standard` / `Administrator` / `PlatformAdministrator`. The Platform Administrator is the seed
  user — undeletable, undemotable, non-disable-able.
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
  (`IDesktopAuthCodeStore`, GETDEL) and **302s to `diariz://auth/callback?code=…`** (a raw redirect, reached
  only from the encrypted-state desktop flow — never an open redirect). The desktop app redeems the code at
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
  selected calendars (primary first) for an event by id. Users manage the selection in **Preferences → Google
  Account** (`GET`/`PUT /api/calendar/calendars`, backed by the public `ListAllCalendarsAsync` which returns the
  unfiltered list for the picker); the single `ListCalendarsAsync` chokepoint means the selection restricts
  matching, linking, and the Calendar overlay alike. Still `calendar.readonly` - the existing grant already
  covers team/shared/subscribed calendars, so no new scope.
- **Match a recording to its calendar meeting (Phase 2 feature):** with the Calendar grant, the recording's
  Overview calls **`GET /api/recordings/{id}/calendar-match`**, which asks `ListEventsAsync` for events across the
  user's selected calendars around the recording's wall-clock span (`CreatedAt` .. `+DurationMs`, padded ±30 min),
  and returns the **best time-overlapping** event (`GoogleCalendarClient.PickBest`) as `{ match }` (or `null`).
  Read-only (`calendar.readonly`); 400s without the grant. The Overview shows the matched meeting with a link to
  the Google Calendar event.
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
  appear with no clicks); the recording Overview renders the meeting's full details (`CalendarEventDetails`, fetched
  live, falling back to the snapshot) with **Change meeting** (a browse-events modal - date-range + title filter,
  `CalendarLinkModal`) and **Unlink** actions. A manually-linked event is never overwritten by the auto-match.
- **Calendar-tab event overlay (Phase 2 feature):** with the Calendar grant, the recordings **Calendar tab**
  overlays the month's meetings. The web app fetches **`GET /api/calendar/events?timeMin&timeMax`**
  (`CalendarController`, range-capped ≤62 days, reusing `IGoogleCalendarClient.ListEventsAsync`; empty list
  when not connected) **once per viewed month** (React-Query keyed by month, short `staleTime`, Refresh link).
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
  stored. **Ical.Net** (MIT) is a new API dependency. Recording↔meeting **linking stays Google-only** (ICS
  events are display-only in the Calendar tab). Users manage their feeds in **Preferences → Calendar feeds**
  (`CalendarFeedsSection`: add/rename/recolour/enable/remove, with the last-fetch error surfaced per feed); the
  Calendar tab renders feed events **coloured but non-clickable** (a row whose `calendarId` starts `ics:`).
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

## Speaker identification (voiceprints)

Enrol a person once (from a recording's speaker) and Diariz recognises that voice in future recordings. A
**`SpeakerProfile`** holds a centroid voiceprint (L2-normalised mean of its **`ProfileContribution`**
snapshots); matching is pgvector cosine distance. The **People** screen manages voiceprints — rename, view
training contributions, add/remove a contribution (recomputes the centroid), merge duplicates, and **erase**
one or all (GDPR): erasing reverts auto-applied labels to the anonymous label but keeps names typed by hand.
Voiceprints are **per-user** (a user's voiceprints only match their own recordings). See
[`Speaker_Identification_and_Verification.md`](Speaker_Identification_and_Verification.md).

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

## GPU / worker notes

The worker pins a **CUDA 12.8 (cu128)** torch stack so it runs on Blackwell / RTX 50-series (sm_120). Three
non-obvious pins make whisperx 3.3.1 work (`ctranslate2==4.6.3`, `transformers==4.48.0` +
`huggingface_hub==0.27.1`, and a `torch.load(weights_only=False)` shim for pyannote checkpoints). Diarization
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

## Platform backup & restore

`MaintenanceController` (Platform-Administrator only) exports/imports the **whole platform** as one `.zip`:
a `manifest.json` (app version, the last-applied EF migration id, createdAt), a `database.dump`
(`pg_dump --format=custom`), and one `objects/<key>` entry per object-store blob (audio + attachments). The
API image therefore ships the **PostgreSQL client tools** (`pg_dump`/`pg_restore`). `GET /api/maintenance/backup`
streams the zip straight to the response (token via `access_token`, like the audio endpoint); `POST
/api/maintenance/restore` takes the raw zip body, **refuses a manifest whose migration id ≠ the running
schema** (a `pg_restore --clean` brings the dump's schema, which must match the code), runs the restore, then
wipes and re-uploads the bucket. Restore is **destructive** (replaces all data; the admin is signed out).
The Data-Protection **keyring is not included** — after restoring on a different instance, encrypted per-user
LLM API keys can't be decrypted (users re-enter them); everything else is faithful. The `pg_dump`/`pg_restore`
shell-out is behind `IDatabaseBackup` so the archive/object orchestration is unit-tested; the real round-trip
is an integration test that skips when the client tools aren't on the host PATH.

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
deploy/                     # docker-compose.yml (+ .env.example)
docs/                       # this folder
branding/                   # GitHub social card + source
tests/                      # Diariz.Api.Tests (unit), .IntegrationTests (Testcontainers), .TestSupport
```

## Testing & CI

Three .NET test projects (fast **unit** with the EF in-memory provider + hand-rolled fakes; **integration**
via Testcontainers spinning up real Postgres/Redis/MinIO; shared **TestSupport** fakes), plus **vitest** for
the web and **pytest** for the worker (whisperx stubbed). **TDD is required** — write the failing test first.
CI runs all four suites on a self-hosted Windows runner.

## Roadmap (milestones)

- **M1 — done:** capture → transcribe (WhisperX + pyannote) → view speaker-labelled segments.
- **M2 — done:** multi-user auth + RBAC, LLM summaries, action extraction, transcript export/email,
  re-transcribe with model choice, sections (**two-level nesting**: `Section.ParentId`, drag-to-reorder),
  speaker identification, delete-audio (keep transcript, free quota), **supporting-document attachments**
  (files or URLs on a recording — `Attachments` table + `AttachmentsController`, files in MinIO under
  `{userId}/attachments/…` and counted toward the quota).
- **M3 — partial:** chat across transcripts (shipped); full embedding-backed RAG over `Segment.Embedding`
  (`vector(768)`, sized for `nomic-embed-text`) is scaffolded but not yet populated.
- **M4 — in progress:** packaging/TLS hardening; **macOS desktop app** shipped as an unsigned **beta**
  (mic + ScreenCaptureKit system audio, menu-bar shell, manual update check) - signing/notarization +
  auto-update + Sign in with Apple are the next macOS milestones (see the macOS guide).
