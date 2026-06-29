# Diariz — Data Schema (Postgres) & Object Storage (MinIO)

The persistent state of Diariz lives in two stores:

- **PostgreSQL (+ pgvector)** — all relational data and the voiceprint/segment **vector** columns.
- **MinIO** (S3-compatible) — the original **audio blobs** only.

Redis holds only transient queue messages (Redis Streams) and is not a system of record. This document
details both stores. For how it all fits together see [`Overall_Synopsis_of_Platform.md`](Overall_Synopsis_of_Platform.md).

---

## 1. PostgreSQL

### How the schema is defined and applied

- The model is **EF Core code-first** in `src/Diariz.Domain` (`DiarizDbContext` + entity classes). The
  `DbContext` extends ASP.NET Identity's `IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>`, so
  the standard **`AspNet*` Identity tables** exist alongside the app tables.
- **Migrations** live in `src/Diariz.Domain/Migrations`. The API **auto-applies migrations on startup**
  (`Program.cs`) and seeds the default user, roles, the `PlatformSettings` singleton, and ensures the MinIO
  bucket — you do not run `database update` by hand for normal dev.
- **pgvector is Postgres-only.** The `vector` extension and the `vector(n)` columns are mapped **only when
  `Database.IsNpgsql()`**; under the EF in-memory provider (unit tests) those properties are `Ignore`d. Keep
  any new Postgres-only model config behind the same guard.
- **Enums are stored as `int`s** and are **append-only** — never renumber existing values.

### Migration history

| Migration | Adds |
|---|---|
| `InitialCreate` | Identity tables, `Recordings`, `Transcriptions`, `Segments` (with `vector(768)`), `Speakers`, `Summaries` |
| `AddRecordingNameSourceAndSummarizingStatus` | `Recording.Name`, `Recording.Source`, `Summarizing` status |
| `AddUserSettings` | `UserSettings` (per-user LLM config) |
| `AddSections` | `Sections` + `Recording.SectionId` |
| `AddChatSessionsAndContextWindow` | `ChatSessions`, `UserSettings.ChatContextWindow` |
| `AddRecordingPosition` | `Recording.Position` |
| `AddUserAccessFields` | `ApplicationUser.FullName/Status/IsEnabled`, Section→User cascade |
| `AddSpeakerIdentification` | `SpeakerProfiles`, `ProfileContributions`, `Speaker.Embedding/ProfileId/IdentifiedAuto` (all `vector(192)`) |
| `AddStorageQuotas` | `PlatformSettings` (seeded singleton), `ApplicationUser.QuotaBytes` |
| `AddSpeakerCountHints` | `Recording.MinSpeakers/MaxSpeakers` |
| `AddRecordingActions` | `RecordingActions`, `Recording.ActionsExtractedAt` |
| `AddSegmentOriginalRevised` | renames `Segment.Text` → `Original`, adds `Segment.Revised` (nullable) |
| `AddUserLanguagePreferences` | `UserSettings.NativeLanguage`, `UserSettings.UiLanguage` (both nullable) |
| `AddRecordingAudioDeleted` | `Recording.AudioDeletedAt` (nullable) — audio deleted while keeping the transcript |
| `AddSectionParentAndPosition` | `Section.ParentId` (self-ref, cascade) + `Section.Position` — two-level sub-grouping |

### Entity-relationship overview

```
ApplicationUser (AspNetUsers)
 ├─1:1─ UserSettings            (shared PK = UserId)
 ├─1:n─ Section                 (cascade)
 ├─1:n─ ChatSession             (cascade)
 ├─1:n─ SpeakerProfile          (cascade)
 └─1:n─ Recording               (FK UserId)
         ├─1:n─ Transcription   (cascade)         (RecordingId, Version) unique
         │       ├─1:n─ Segment (cascade)         Embedding vector(768)?
         │       └─1:1─ Summary (cascade)
         ├─1:n─ Speaker         (cascade)         Embedding vector(192)?, (RecordingId, Label) unique
         │       └─n:1─ SpeakerProfile (SetNull)  ProfileId
         └─1:n─ RecordingAction (cascade)

SpeakerProfile (Embedding vector(192), centroid)
 └─1:n─ ProfileContribution     (cascade)         Embedding vector(192) snapshot
         ├─ SpeakerId  → Speaker (cascade)
         └─ RecordingId          (loose Guid, for display; no FK)

Section ──(SetNull)── Recording.SectionId         (deleting a section ungroups its recordings)

PlatformSettings                                  single seeded row (Id = 1)
```

### Tables in detail

Primary keys are `uuid` (`Guid`) unless noted. `DateTimeOffset` maps to `timestamptz`.

#### `Recordings`
The owned audio recording.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers | owner; every query filters on this |
| `Title` | varchar(512) | auto descriptor (e.g. "Mic 6/26/2026, 1:25 PM") |
| `Name` | varchar(512) null | user-editable display name; auto-filled by the summariser when unset (UI shows `Name ?? Title`) |
| `Source` | int | `RecordingSource`: 0 Microphone, 1 System, 2 Upload |
| `BlobKey` | text | MinIO object key (see §2) |
| `ContentType` | text | MIME of the stored audio (e.g. `audio/webm`) |
| `SizeBytes` | bigint | blob size; counts toward the owner's quota (reset to 0 when the audio is deleted) |
| `AudioDeletedAt` | timestamptz null | non-null once the audio blob was deleted to reclaim storage (transcript kept); audio endpoints 404 |
| `DurationMs` | bigint | measured by the worker for uploads (no client duration) |
| `Status` | int | `RecordingStatus`: 0 Uploaded, 1 Queued, 2 Transcribing, 3 Transcribed, 4 Summarized, 5 Failed, 6 Summarizing, 7 Merging |
| `Error` | text null | last failure message |
| `MinSpeakers` / `MaxSpeakers` | int null | diarization hints (null = automatic) |
| `SectionId` | uuid FK → Sections null | null = "Ungrouped"; **SetNull** on section delete |
| `Position` | int | manual sort order within its group |
| `ActionsExtractedAt` | timestamptz null | non-null once action extraction has run (drives the by-exception Actions panel) |
| `CreatedAt` | timestamptz | |

Index: `(UserId, CreatedAt)`. Children cascade: `Transcriptions`, `Speakers`, `RecordingActions`.

#### `Transcriptions`
One transcription pass; recordings are **versioned**.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `RecordingId` | uuid FK → Recordings | cascade |
| `Model` | text | e.g. `whisperx-large-v3` |
| `Version` | int | monotonic per recording, starting at 1; highest = current |
| `Language` | text null | ISO-639-1 if detected |
| `CreatedAt` | timestamptz | |

Unique index: `(RecordingId, Version)`. Children: `Segments` (cascade), `Summary` (1:1, cascade).

#### `Segments`
A contiguous, single-speaker span of transcribed speech.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `TranscriptionId` | uuid FK → Transcriptions | cascade |
| `SpeakerLabel` | text | diarization label, e.g. `SPEAKER_00` / `UNKNOWN` |
| `StartMs` / `EndMs` | bigint | ms relative to recording start |
| `Original` | text | the model's verbatim output for this span — never overwritten after the worker writes it |
| `Revised` | text null | a user edit (later: a translation) of `Original`; null = unchanged. The effective text = `Revised ?? Original` |
| `Ordinal` | int | order within the transcription |
| `Embedding` | **vector(768)** null | for RAG retrieval (M3, sized for `nomic-embed-text`); unused/null today; Postgres-only |

Index: `(TranscriptionId, Ordinal)`.

#### `Summaries`
LLM summary of a specific transcription version (1:1 with `Transcription`).

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `TranscriptionId` | uuid FK → Transcriptions | unique (1:1), cascade |
| `Model` | text | LLM model id used |
| `Text` | text | |
| `CreatedAt` | timestamptz | |

#### `RecordingActions`
Extracted/hand-edited action items.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `RecordingId` | uuid FK → Recordings | cascade |
| `Text` | text | the action (UI column "Action"; named `Text` to avoid the `System.Action` clash) |
| `Actor` | text | free text, may be empty |
| `Deadline` | text | free text, may be empty |
| `Ordinal` | int | 0-based order within the recording |
| `CreatedAt` | timestamptz | |

Index: `(RecordingId, Ordinal)`.

#### `Speakers`
Per-recording diarization label → display name, plus its voiceprint and any identification.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `RecordingId` | uuid FK → Recordings | cascade |
| `Label` | text | raw diarization label |
| `DisplayName` | varchar(256) | user-facing name (defaults to the label) |
| `Embedding` | **vector(192)** null | ECAPA per-speaker voiceprint from the worker; Postgres-only |
| `ProfileId` | uuid FK → SpeakerProfiles null | the identified person; **SetNull** on profile delete |
| `IdentifiedAuto` | bool | true when name/profile were set by auto-ID (vs a manual rename) |

Unique index: `(RecordingId, Label)`.

#### `SpeakerProfiles`
An enrolled person's voiceprint (per user). Biometric data — GDPR-erasable.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers | cascade |
| `Name` | varchar(256) | |
| `Embedding` | **vector(192)** | centroid = L2-normalised mean of contribution snapshots; Postgres-only |
| `SampleCount` | int | number of contributing speakers averaged in |
| `CreatedAt` / `UpdatedAt` | timestamptz | |

Index: `(UserId)`. Children: `ProfileContributions` (cascade). Matching against new `Speaker.Embedding`s is a
**pgvector cosine distance** query (`Embedding.CosineDistance(vec)`), accepted when `≤ Identification:Threshold`.

#### `ProfileContributions`
Training provenance for a profile (which recording-speakers feed the centroid).

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `ProfileId` | uuid FK → SpeakerProfiles | cascade |
| `SpeakerId` | uuid FK → Speakers | cascade (deleting the source speaker drops the contribution) |
| `RecordingId` | uuid | loose Guid for display; **no FK** |
| `Embedding` | **vector(192)** | snapshot of the contributing speaker's embedding (lets the centroid be recomputed without the worker) |
| `CreatedAt` | timestamptz | |

Index: `(ProfileId)`.

#### `Sections`
User-defined group recordings are filed under.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers | cascade |
| `Name` | varchar(128) | |
| `ParentId` | uuid FK → Sections null | null = top-level; non-null = a sub-section (one level only). **Cascade** on parent delete |
| `Position` | int | manual sort order among siblings (drag-to-reorder; replaces alphabetical) |
| `CreatedAt` | timestamptz | |

Index: `(UserId, Name)`, `(ParentId)`. Sections nest **one level deep** (a sub-section can't be a parent;
enforced in `SectionsController`). Deleting a section **Cascade**-deletes its sub-sections and **SetNull**s
the recordings of itself and those sub-sections (ungroups, not deletes).

#### `ChatSessions`
Saved chat conversations; stateless server (thread + context stored as JSON).

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers | cascade |
| `Title` | varchar(256) | LLM-generated on save, falls back to the first user message |
| `MessagesJson` | **jsonb** | array of `{ role, content }` turns (`text` under the in-memory provider) |
| `ContextJson` | **jsonb** | `{ recordingIds, attachmentName?, attachmentText? }` |
| `CreatedAt` / `UpdatedAt` | timestamptz | |

Index: `(UserId, UpdatedAt)`.

#### `UserSettings`
Per-user preferences (1:1 with the user via a **shared primary key** = `UserId`).

| Column | Type | Notes |
|---|---|---|
| `UserId` | uuid PK + FK → AspNetUsers | cascade |
| `SummaryApiBase` | varchar(512) null | user's OpenAI-compatible endpoint |
| `SummaryApiKeyEncrypted` | text null | API key **encrypted at rest** (ASP.NET Data Protection); never returned to clients |
| `SummaryModel` | varchar(256) null | |
| `ChatContextWindow` | int null | per-user context-window override (tokens); null → server `Chat:ContextLength` |
| `NativeLanguage` | text null | the user's native language (BCP-47); default target when translating transcripts |
| `UiLanguage` | text null | the language the app UI is shown in (BCP-47); null → follow the browser |

Each field falls back to the server `Summarization`/`Chat` defaults when null. The display name lives on
`AspNetUsers.FullName` (editable via `PUT /api/user/profile`), not here.

#### `PlatformSettings`
Single seeded row (`Id = 1`), edited by the Platform Administrator.

| Column | Type | Notes |
|---|---|---|
| `Id` | int PK | always 1 |
| `StarterQuotaBytes` | bigint | quota granted to new users (default 5 GiB) |
| `MaxQuotaBytes` | bigint | ceiling any admin may raise a user to (default 50 GiB) |

#### Identity tables (`AspNet*`)
Standard ASP.NET Identity schema with **Guid** keys: `AspNetUsers`, `AspNetRoles`, `AspNetUserRoles`,
`AspNetUserClaims`, `AspNetRoleClaims`, `AspNetUserLogins`, `AspNetUserTokens`. **`AspNetUsers` is the
`ApplicationUser` table**, extended with:

| Added column | Type | Notes |
|---|---|---|
| `FullName` | varchar(256) null | display name (UI falls back to email) |
| `Status` | int | `UserStatus`: 0 Requested, 1 Invited, 2 Active |
| `IsEnabled` | bool | admin enable/disable (disabled users can't sign in) |
| `QuotaBytes` | bigint | audio storage quota; default = platform starter |

Roles: `Standard`, `Administrator`, `PlatformAdministrator` (rows in `AspNetRoles`).

### Vector columns summary

| Table.Column | Dim | Model | Purpose |
|---|---|---|---|
| `Speakers.Embedding` | 192 | SpeechBrain ECAPA (`spkrec-ecapa-voxceleb`) | per-recording speaker voiceprint |
| `SpeakerProfiles.Embedding` | 192 | (centroid of ECAPA) | enrolled person's voiceprint |
| `ProfileContributions.Embedding` | 192 | ECAPA snapshot | recompute centroids without the worker |
| `Segments.Embedding` | 768 | `nomic-embed-text` (planned) | RAG over transcript text (M3, unused today) |

Changing an embedding model means a migration to resize the column **and** re-enrolment/re-embedding.

---

## 2. MinIO (object storage)

### What's stored

**Only the original audio blobs.** Nothing else (no transcripts, no derived files) lives in MinIO — those are
in Postgres. Transcript downloads (TXT/MD/RTF/SRT) and the emailed HTML are rendered on demand by the API from
the database.

### Bucket & key layout

- **Bucket:** `recordings` (configurable via `Storage:Bucket`). Created on API startup
  (`AudioStorage.EnsureBucketAsync`) if absent.
- **Object key:** `{userId}/{recordingId}{ext}` — e.g. `8f3a…/c1b2…​.webm`. The extension comes from the
  uploaded file name (recordings default to `.webm`). The `userId` prefix gives a natural per-user "folder"
  and keeps keys unguessable (random Guids).
- The key is stored on `Recording.BlobKey`; `Recording.ContentType` holds the MIME type.

### Access pattern (who reads/writes)

| Actor | Operation | How |
|---|---|---|
| **API** (upload) | `PutObject` | streams the multipart body straight into MinIO (SigV4, path-style) |
| **Worker** (transcribe) | `download_file` | boto3 (`s3v4`, path addressing) → local temp file, deleted after the job |
| **API** (playback / download-audio) | `GetObject` (+ **byte range**) | streams back to the browser **same-origin**; supports `Range` so `<audio>` can seek |
| **API** (delete recording) | `DeleteObject` | idempotent; also used by quota/cleanup |
| **API** (quota) | `HeadObject` (`GetObjectMetadata`) | size lookups / backfill (`StorageUsage`, `StorageBackfill`) |

The S3 client uses **`ForcePathStyle = true`** and region `us-east-1` (MinIO requirements). Note: a prior bug
required **not** setting `DisablePayloadSigning` on `PutObject` — normal SigV4 payload signing works over plain
HTTP against MinIO; AWS SDK v4 rejects `DisablePayloadSigning` over HTTP. Be cautious changing request options
in `Services/AudioStorage.cs`.

### Security / exposure

- MinIO is **never exposed to the browser.** The API **proxies all reads** (same-origin streaming) instead of
  issuing presigned URLs, so MinIO only needs to be reachable from the API and worker on the internal
  network. (The old `STORAGE_PUBLIC_ENDPOINT` / presign path was removed.)
- Playback is authorised by a **short-lived token** minted by the API (`GET /api/recordings/{id}/audio-url`),
  so the streaming endpoint can be used by the native `<audio>` element without a bearer header.
- Credentials are `Storage:AccessKey`/`SecretKey` (worker: `S3_ACCESS_KEY`/`S3_SECRET_KEY`); change them from
  the `minioadmin` defaults in production.

### Lifecycle

- A blob is written once on upload and never mutated. **Re-transcribing reuses the same blob** (only new
  `Transcription`/`Segment` rows are created).
- **Deleting a recording** removes its blob (`DeleteObject`) and cascades all its DB rows.
- **Quota accounting** is by summing `Recording.SizeBytes` per user (the DB is the source of truth);
  `StorageBackfill` reconciles sizes from MinIO `HEAD`s where needed.

### Durability / volumes

In Docker Compose, MinIO data persists in the **`miniodata`** named volume (host MinIO ports are remapped to
**9002** S3 / **9003** console). Companion volumes: **`pgdata`** (Postgres), **`apikeys`** (the Data
Protection keyring that decrypts `UserSettings.SummaryApiKeyEncrypted`, mounted at `/keys`), and
**`workercache`** (model weights). Back up `pgdata` + `miniodata` together — a transcript row in Postgres is
meaningless without its audio blob, and vice-versa, and losing `apikeys` makes stored per-user API keys
unrecoverable.
