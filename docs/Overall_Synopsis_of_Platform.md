# Diariz — Platform Synopsis

A detailed, current view of what Diariz is, how it's built, and how the pieces fit together. For the
data model and object-storage layout see [`Data_Schema.md`](Data_Schema.md); for the macOS port see
[`macOS_Desktop_App_Guide.md`](macOS_Desktop_App_Guide.md); for speaker-ID internals see
[`Speaker_Identification_and_Verification.md`](Speaker_Identification_and_Verification.md).

## What it is

Diariz is a **self-hostable, multi-user voice/meeting transcription platform**. You **record** (microphone,
or Windows system/loopback audio via the desktop app) or **upload** an audio file; the server **transcribes
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
| **API** | ASP.NET Core (**.NET 10**), EF Core, SignalR, AWS S3 SDK, MailKit | `src/Diariz.Api` | Auth, orchestration, persistence, audio storage/streaming, summarisation + chat + action-extraction, SignalR notifications |
| **Domain** | EF Core + Npgsql + pgvector | `src/Diariz.Domain` | Entities, `DiarizDbContext`, migrations (compiled into the API) |
| **Worker** | Python: WhisperX (large-v3), pyannote 3.1, SpeechBrain ECAPA, CUDA | `src/Diariz.Worker` | GPU transcription → alignment → diarization → per-speaker voiceprints |
| **Web** | React 19 + TypeScript + Vite + Tailwind v4 | `apps/web` | SPA UI (served by nginx in Docker) |
| **Desktop** | Electron (Windows tray shell) | `apps/desktop` | Mic + Windows loopback capture, tray recording, auto-update; loads the web app from the server origin |

Infrastructure (via Docker Compose, project name **`diariz`**):

- **PostgreSQL + pgvector** (`pgvector/pgvector:pg16`) — relational data **and** voiceprint/embedding vectors.
- **Redis** (`redis:7`) — job queues (Redis **Streams**), nothing is stored long-term here.
- **MinIO** (S3-compatible) — original audio blobs and uploaded attachment files.

### Ports (Docker / local dev)

| Service | In-container | Host (Docker) | Dev |
|---|---|---|---|
| API | 8080 | 8080 | 8080 |
| Web (nginx, proxies `/api` + `/hubs`) | 80 | **8081** | Vite dev server **5173** (proxies to 8080) |
| Postgres | 5432 | 5432 | 5432 |
| Redis | 6379 | 6379 | 6379 |
| MinIO S3 API | 9000 | **9002** | — |
| MinIO console | 9001 | **9003** | — |

MinIO is remapped on the host (9002/9003) to avoid clashing with other local MinIO instances. In-container,
services address each other by Compose service name (`minio:9000`, `redis:6379`, `postgres:5432`, `api:8080`).

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

1. **Capture/upload.** The web `Recorder` (`MediaRecorder`) records mic or — in the desktop shell — Windows
   loopback audio; or the user uploads a file. For microphone capture the user can pick a **specific input
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
field is **omitted entirely** so non-reasoning endpoints aren't broken.

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
- **Meeting minutes (async).** A **third Redis stream `meeting-minutes-jobs`** (group `minute-takers`) with its
  own `MeetingMinutesWorker` (singleton `BackgroundService`) generates a formal, emailable **`MeetingMinutes`**
  (GitHub-flavoured Markdown; `MeetingMinutesClient`/`Prompt`) from the transcript. It is enqueued **alongside
  the summary** after transcription (same effective per-user config gates both), and re-runnable via
  `POST /api/recordings/{id}/meeting-minutes/generate`. The minutes **instruction prompt lives in the editable
  template** `prompts/meeting-minutes.md` — `{meeting_date}`/`{meeting_time}`/`{meeting_title}`/`{speaker_list}`/
  `{meeting_duration}` are substituted and the transcript is attached as a **separate user (data) turn** so it
  can't be read as instructions. Minutes **do not own `Recording.Status`** (so they never
  race the summary's status transitions) — the processor notifies over SignalR to trigger a refetch. Minutes can
  be **hand-edited** (`PUT .../meeting-minutes`, sets `IsUserEdited`; auto-generator then skips) and **emailed on
  their own** (`POST .../meeting-minutes/email {includeAttachments}`) — the Markdown is rendered to HTML with
  **Markdig** and, when requested, the recording's file attachments are attached (`IEmailSender` gained an
  attachments parameter). Minutes also ride along in the emailed transcript and the md/txt/rtf downloads. The web
  edits them in a **WYSIWYG editor** (TipTap) that round-trips Markdown.
- **Extract actions (pipeline + on demand).** Action items are extracted **automatically as part of the
  pipeline**: a **fourth Redis stream `actions-jobs`** (group `actions-extractors`) with its own `ActionsWorker`
  (singleton `BackgroundService`) runs `ActionsProcessor` → `ActionsClient`/`ActionsPrompt`, enqueued **alongside
  the summary + minutes** after transcription (same effective per-user config gates all three). The automatic
  pass **skips any recording whose `Recording.ActionsExtractedAt` is already set** (extraction ran, or the user
  added an action), so a re-transcribe never clobbers manual edits; like minutes it is **status-neutral** and
  notifies over SignalR. An explicit re-extract stays synchronous: `POST /api/recordings/{id}/actions/extract`
  calls the LLM inline and **replaces** the recording's **`RecordingAction`** rows. Actions also travel into
  transcript downloads, the emailed transcript, and the chat context. Its instruction prompt is the **editable**
  `prompts/extract-actions.md` (`{calendar_date}` substituted).
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
  (current / selected / none) **plus their action items** and an optional uploaded attachment
  (`ChatContextBuilder`), then streams tokens back via **Server-Sent Events** (`ChatStreamClient`).
  With **`IncludeAttachments`** the selected recordings' **attachments** are folded in too: uploaded files
  are read into text by **`AttachmentExtractor`** (PDF, text, Office `.docx/.xlsx/.pptx`, email/calendar
  `.eml/.ics` — via PdfPig / Open XML SDK / MimeKit), and **URL** attachments are fetched by
  **`UrlFetcher`** behind **SSRF guards** (`UrlFetchGuard` — blocks loopback/private/link-local IPs and
  non-http(s) schemes), with a size cap, redirect re-validation, and HTML→text reduction. Conversations
  save to **`ChatSession`** rows (thread + context stored as `jsonb`), so the server stays stateless
  between turns — each request resends the full history and context.
- **Chat tool calling (built-in transcript tools).** When a user enables tools (master switch + per-tool list,
  resolved by **`ChatToolSettingsResolver`** — user override ?? server `Chat:ToolsEnabled` / `Chat:DisabledTools`),
  the chat turn runs as a bounded **agentic loop** (`ChatToolOrchestrator`, ≤5 rounds): it offers the enabled
  tools to the model, executes any **tool calls** server-side, re-injects their results as `role:"tool"` messages,
  and repeats until the model answers in text. The stream carries new `tool_start`/`tool_end` SSE events (the web
  shows a transient grey *"Tool call: …"* line). Tools are `IChatTool`s collected by `IChatToolRegistry`. The
  search-based ones (`who_said_that`, `what_did_they_say`, `search_transcripts`, `when_was_discussed`,
  `count_mentions`, plus `list_recordings`) query **`TranscriptSearch`**, a Postgres **`pg_trgm`** GIN-trigram
  fuzzy search over the user's own current-version transcripts (a `scope` arg lets the model search the whole
  library or just the selected recordings). The EF-based ones (`list_action_items`, `get_recording_summary`,
  `who_attended`, `speaker_talk_time`, `get_segment_context`) read existing relational data directly. Each tool
  result embeds an in-app **markdown deep-link** (`/recordings/{id}?t={ms}`); the model cites it, and the web
  intercepts the click to open the transcript and **scroll/highlight the segment** at that moment
  (`lib/transcriptNav.ts`). The orchestrator also emits `ref` events (the recordings a tool referenced) so the
  web can **linkify plain mentions** the model didn't link (`lib/linkify.ts`); when an answer cites several
  moments in one recording the transcript shows a **Match k/n prev/next** control (a `?ts=` list). The chat
  **system prompt** grounds questions in the user's own meetings and (with tools) tells the model to search the
  transcripts before saying it doesn't know. With tools off, chat is the same single-pass stream as before.
  Tools run inside the API (no worker) — server-redeploy only.

## Auth, multi-tenancy, and roles

- **ASP.NET Core Identity** (Guid keys) issues **JWT** bearer tokens (`TokenService`). Browsers pass the
  token as `?access_token=` on the SignalR WebSocket handshake (picked up in `Program.cs` `OnMessageReceived`).
  The token is a **sliding session**: the web client silently calls `POST /api/auth/refresh` (re-issues a token
  for the still-authenticated user) shortly before expiry, so long sessions — e.g. a recording left running —
  don't lapse. Recordings are also written to the browser (**IndexedDB**, `lib/pendingRecording.ts`) the moment
  Stop is pressed, before upload, and offered for re-upload on return if the upload didn't complete — so a
  session lapse can never lose audio.
- **RBAC:** `Standard` / `Administrator` / `PlatformAdministrator`. The Platform Administrator is the seed
  user — undeletable, undemotable, non-disable-able.
- **Access lifecycle:** a person **requests access** (`UserStatus.Requested`) → an admin **grants** it
  (issues a one-time setup link; emailed via SMTP/MailKit, or shown to the admin as a fallback when SMTP is
  unconfigured) → the user **sets up** their name + password (`Active`). Admins can also add users directly.
- **Isolation:** every recording/section/chat/voiceprint query filters by `UserId` from the JWT
  `NameIdentifier` claim. **Storage quotas** (audio bytes) are per-user: the Platform Administrator sets the
  starter + maximum (`PlatformSettings`), any admin can raise an individual user up to the max.
- **Self-service profile:** every user has a **Preferences** screen to change their **display name**
  (`PUT /api/user/profile` → updates `ApplicationUser.FullName` and re-issues the token so the name claim
  updates without a re-login) and their **native / UI language** (stored on `UserSettings`). The supported
  languages come from a shared list at **`GET /api/languages`** (anonymous, so the signup page offers a
  language selector too). This underpins the localization & translation feature.

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

- **Redis Streams, five of them.** `transcription-jobs`/`workers` and `audio-merge-jobs` (both API → Python
  worker, sharing the `workers` group — the worker `XREADGROUP`s both streams and dispatches by stream key) and
  three API-internal streams with their own in-process consumers: `summarization-jobs`/`summarizers`,
  `meeting-minutes-jobs`/`minute-takers`, and `actions-jobs`/`actions-extractors`. Job payloads are **PascalCase JSON**
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
- **M4 — planned:** packaging/TLS hardening, macOS desktop build (see the macOS guide).
