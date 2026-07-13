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
| `AddSpeakerMultiSpeaker` | `Speaker.IsMultiSpeaker` (bool) — "Multiple Speakers" slots excluded from voiceprints |
| `AddActionCompletion` | `RecordingActions.Completed` (bool, default false) + `RecordingActions.CompletedAt` (nullable) — action done-tracking |
| `AddSummaryUserEdited` | `Summary.IsUserEdited` (bool) + `Summary.UpdatedAt` (nullable) — manual/protected summary edits |
| `AddTranscriptionProcessingMs` | `Transcription.ProcessingMs` (nullable) — full-pipeline wall-clock time the worker spent |
| `AddAttachments` | `Attachments` (file/URL supporting documents on a recording, cascade) |
| `AddChatToolsSupport` | `UserSettings.ChatToolsEnabled`/`ChatToolOverridesJson`; enables `pg_trgm` + a GIN trigram index `IX_Segments_Text_Trgm` on `coalesce("Revised","Original")` (chat tool fuzzy search) |
| `AddReasoningToUserSettings` | `UserSettings.ReasoningEnabled` (bool null) + `UserSettings.ReasoningEffort` (text null) — per-user `reasoning_effort` on LLM requests |
| `AddMeetingMinutes` | `MeetingMinutes` (1:1 with `Transcription`, cascade, unique on `TranscriptionId`) — LLM-generated emailable meeting minutes (Markdown) |
| `AddGoogleIdentity` | `ApplicationUser.GoogleSubject` (varchar(256) null, **unique index**) + `ApplicationUser.PictureUrl` (varchar(1024) null) — Google sign-in linkage + profile picture |
| `AddGoogleConnection` | `UserSettings.GoogleRefreshTokenEncrypted` (text null, encrypted) + `UserSettings.GoogleCalendarGranted`/`GoogleGmailGranted` (bool, default false) — opt-in Google Calendar/Gmail data access |
| `RemoveGoogleGmailGranted` | drops `UserSettings.GoogleGmailGranted` — the Gmail-draft feature was removed (Gmail scopes are restricted; not worth the security assessment). Calendar access unchanged |
| `AddMcpAccessTokens` | `McpAccessTokens` (per-user MCP personal access tokens; SHA-256 hash only, **unique** on `TokenHash`, cascade on user delete) — connect Claude to transcripts over `/mcp` |
| `AddOpenIddict` | `OpenIddictApplications`, `OpenIddictAuthorizations`, `OpenIddictScopes`, `OpenIddictTokens` (OpenIddict EF Core stores, string keys) — the OAuth 2.1 authorization server for the MCP web connector. Registered by `ModelBuilder.UseOpenIddict()`; not owned by an entity class |
| `AddTranscriptChunks` | `TranscriptChunks` (windowed retrieval chunks for RAG/M3; `vector(768)`, denormalized `RecordingId`/`UserId`, cascade on `Transcription`, index `(UserId, RecordingId)`) — semantic-search index; supersedes the unused `Segment.Embedding` |
| `AddRecordingCalendarLink` | `RecordingCalendarLinks` (1:1 with `Recording`, shared PK, cascade) — persisted link from a recording to its Google Calendar event (lightweight snapshot; rich invite details fetched live) |
| `AddCalendarLinkCalendarIdAndColor` | `RecordingCalendarLinks.CalendarId` (varchar(1024), NOT NULL, existing rows backfilled to `primary`) + `RecordingCalendarLinks.Color` (varchar(32) null) — which calendar the linked event is on + its Google colour |
| `AddIcsCalendarSource` | `IcsCalendarSources` (per-user external `.ics` feed subscriptions; indexed on `UserId`, cascade on user delete) — events fetched live and merged into the Calendar views |
| `AddMeetingType` | `MeetingTypes` (minutes templates; nullable `UserId` — null = shared Platform type, non-null = a user's Personal type; unique `Key` for seeded standards; `ContentJson` **jsonb**; cascade on user delete) + `Recordings.MeetingTypeId` (FK, `ON DELETE SET NULL`) — the chosen template driving a recording's minutes |
| `AddMinutesGenerationMode` | `PlatformSettings.MinutesGenerationMode` (int, NOT NULL, default 0 = SingleCall) — platform-wide switch for how template-driven minutes generate (per-section calls vs one call) |
| `AddAudioRetention` | `PlatformSettings.AutoDeleteAudioEnabled` (bool, default false) + `AudioRetentionDays` (int, default 30) + `AudioDeletionTimeOfDay` (time, default 03:00) — the opt-in nightly audio-retention policy; and `Recording.AudioProtectedAt` (timestamptz null) — per-recording exemption from audio deletion |
| `AddUserProfileAndCalendarSelection` | `UserSettings` gains `JobTitle`/`CompanyName`/`LinkedIn` (varchar(256) null), `JobDescription`/`CompanyDescription` (varchar(2048) null), `Theme` (int, default 0 = Auto), and `GoogleSelectedCalendarIdsJson` (jsonb null) — richer profile + per-user theme + the Google calendar selection |
| `AddApiAccessTokens` | `ApiAccessTokens` (per-user personal REST-API tokens; SHA-256 hash only, **unique** on `TokenHash`, cascade on user delete) + `PlatformSettings.ApiAccessEnabled` (bool, default false) — user API access, off until a Platform Admin enables it |
| `AddMeetingNotes` | `MeetingNotes` (the user's own note lines; anchored to a recording **or** a calendar event, adopted onto the recording when the calendar link forms; cascades from both user and recording) |
| `AddRecordingTags` | `RecordingTags` (LLM-extracted weighted tag-cloud tags, machine-only; cascade on `Recording`, index `(RecordingId, Ordinal)`) + `Recordings.TagsExtractedAt` (timestamptz null) — the tag-backfill "done" marker |
| `AddLlmTimeout` | `PlatformSettings.LlmTimeoutSeconds` (int, NOT NULL, default 120) — the platform-wide per-request timeout applied to every LLM call (the single authority; the HTTP clients have no cap) |
| `AddSectionSummaryAndMinutes` | `SectionSummaries` + `SectionMinutes` (1:1 with `Section`, cascade) — the folder-level roll-up LLM summary/minutes; `SectionMinutes.MeetingTypeId` (FK, `ON DELETE SET NULL`) is the folder's chosen template |
| `AddSectionAttachments` | `SectionAttachments` (file/URL supporting documents filed directly on a `Section`, cascade, index `(SectionId, Ordinal)`) — folder-direct attachments, independent of any recording |
| `AddUserGroups` | `UserGroups` (named permission holders; unique `Name`; `Permissions` int **[Flags]**; `IsSystem`) + `UserGroupMembers` (composite PK `(GroupId, UserId)`, cascade from both) — platform authority via group membership. The migration also **seeds** the two groups and performs a **one-time** move of Identity role holders into them (`RoleToGroupBackfill`); it is deliberately not repeated on boot |
| `AddRooms` | `Rooms` (a workspace; `Kind` int 0=Personal/1=Shared; `OwnerUserId` FK **`ON DELETE SET NULL`** — a deleted user's personal room is **orphaned**, not destroyed; **filtered** unique index on `OwnerUserId WHERE NOT NULL`, **filtered** unique index on `Name WHERE "Kind" = 1`) + `RoomMembers` (composite PK `(RoomId, PrincipalType, PrincipalId)`; the principal is a user **or** a group; `Permissions` int **[Flags]**; cascade from `Rooms`) — the room model. The migration also **backfills**, once, one Personal room per existing user (`PersonalRoomBackfill`) |
| `AddRoomRecordings` | `RoomRecordings` (the placement of a recording in a room; composite PK `(RoomId, RecordingId)`; `IsMainRoom` with a **filtered** unique index on `RecordingId WHERE "IsMainRoom"` — exactly one main room per recording; `SectionId` = the folder **within that room**, FK `ON DELETE SET NULL`; `SharedByUserId`/`SharedAt` null on the main row, enforced by `CK_RoomRecordings_MainRoomHasNoSharer`; cascade from `Rooms` and `Recordings`; index `(RoomId, SectionId)`). The migration also **backfills**, once, one main placement per recording in its recorder's personal room — carrying the folder it was filed under — minting any missing personal room first (`RecordingPlacementBackfill`) |
| `DropRecordingSectionId` | Drops `Recordings.SectionId` (and its FK/index). The folder is now a property of the **placement** (`RoomRecordings.SectionId`), not of the recording, so the same recording can sit in different folders in different rooms |
| `AddSectionRoomId` | `Sections.RoomId` (uuid, indexed `(RoomId, Name)`; a **plain column**, no FK yet - the Rooms FK + the `UserId` drop land with Phase 4). The migration **backfills** each section into its owner's personal room, minting a missing one first (`SectionRoomBackfill`). Folders are now room-scoped; `Section.UserId` is retained as owner identity for now |
| `AddRoomScopedEntities` | `SpeakerProfiles.RoomId` + `ChatSessions.RoomId` (uuid, not-null) and `MeetingTypes.RoomId` (uuid, **nullable** - null mirrors the platform type's null `UserId`); all **plain columns**, no FK yet (the Rooms FK + the `UserId` drop land with Phase 4). The migration **backfills** each voiceprint, saved chat and personal meeting type into its owner's personal room, minting a missing one first (`RoomScopedEntitiesBackfill`); platform meeting types keep `RoomId` null. These are populated on create but still **queried by `UserId`** for now |
| `AddRecordingPlacementPreference` | `UserSettings.RecordingPlacementMode` (int, not-null, **default 1** = `SelectedFolder`) + `UserSettings.RecordingPlacementSectionId` (uuid, nullable). Where a new recording is filed in the recorder's personal room; no data backfill (the column default covers existing rows) |
| `AddRoomRecordingPosition` | `RoomRecordings.Position` (int, not-null, **default 0**). Per-room sort order of a recording within its room, so a recording can be ordered differently in two rooms; supersedes the now-dead global `Recording.Position`. **Backfills** once, copying `Recording.Position` onto each **main** placement (`RoomRecordingPositionBackfill`); shared placements keep 0 |
| `AddFormulas` | `Formulas` (a saved prompt + chosen context; `Scope` int 0=Personal/1=Platform/2=Diariz; `OwnerUserId` FK `ON DELETE CASCADE`, set only for Personal - a user's personal formulas die with the account; `Context` int **[Flags]**; `Enabled` bool default true; `IsBuiltIn` blocks delete) + `FormulaResults` (the generated Markdown per recording; cascade on `Recording`, `ON DELETE SET NULL` on `Formula`, nullable `CreatedByUserId` `ON DELETE SET NULL` - the document survives its author's account deletion with attribution dropped, index `(RecordingId, Ordinal)`) — additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddFormulaSharing` | `Formulas.Shared` (bool, not-null, **default false** - only meaningful for Personal scope: when true the formula is discoverable platform-wide) + `FormulaSubscriptions` (a subscriber's live link to a shared Personal formula; `FormulaId` FK `ON DELETE CASCADE`, `UserId` FK `ON DELETE CASCADE`, unique index `(FormulaId, UserId)`) — additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddFormulaResultStatus` | `FormulaResults.Status` (int enum `Generating/Ready/Failed`, not-null, default 0) + `FormulaResults.Error` (text, null), for the async run lifecycle; existing rows backfilled to `Ready` — additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |

### Entity-relationship overview

```
ApplicationUser (AspNetUsers)
 ├─1:1─ UserSettings            (shared PK = UserId)
 ├─1:n─ Section                 (cascade)
 ├─1:n─ ChatSession             (cascade)
 ├─1:n─ SpeakerProfile          (cascade)
 └─1:n─ Recording               (FK UserId)
         ├─1:n─ Transcription   (cascade)         (RecordingId, Version) unique
         │       ├─1:n─ Segment (cascade)         Embedding vector(768)? (unused; superseded by TranscriptChunk)
         │       ├─1:n─ TranscriptChunk (cascade)  Embedding vector(768)?, denormalized RecordingId/UserId
         │       ├─1:1─ Summary (cascade)
         │       └─1:1─ MeetingMinutes (cascade)
         ├─1:n─ Speaker         (cascade)         Embedding vector(192)?, (RecordingId, Label) unique
         │       └─n:1─ SpeakerProfile (SetNull)  ProfileId
         ├─1:n─ RecordingAction (cascade)
         └─1:n─ RecordingTag    (cascade)         LLM tag-cloud tags (machine-only)

SpeakerProfile (Embedding vector(192), centroid)
 └─1:n─ ProfileContribution     (cascade)         Embedding vector(192) snapshot
         ├─ SpeakerId  → Speaker (cascade)
         └─ RecordingId          (loose Guid, for display; no FK)

Section ──(SetNull)── RoomRecording.SectionId     (deleting a section ungroups the placement)

PlatformSettings                                  single seeded row (Id = 1)

ApplicationUser
 └─1:n─ Formula (cascade)                          OwnerUserId (Personal scope only; null for Platform/Diariz)
Formula
 └─1:n─ FormulaResult (via FormulaId, SetNull)     survives its Formula being deleted
 └─1:n─ FormulaSubscription (cascade)              a shared Personal formula's subscriber links
Recording
 └─1:n─ FormulaResult (cascade)                    RecordingId
ApplicationUser
 └─1:n─ FormulaResult (SetNull)                    CreatedByUserId (nullable; doc survives author deletion)
 └─1:n─ FormulaSubscription (cascade)              UserId (a subscriber's links die with the account)
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
| `Source` | int | `RecordingSource`: 0 Microphone, 1 System, 2 Upload, 3 Combined (mic + system mixed) |
| `BlobKey` | text | MinIO object key (see §2) |
| `ContentType` | text | MIME of the stored audio (e.g. `audio/webm`) |
| `SizeBytes` | bigint | blob size; counts toward the owner's quota (reset to 0 when the audio is deleted) |
| `AudioDeletedAt` | timestamptz null | non-null once the audio blob was deleted to reclaim storage (transcript kept); audio endpoints 404 |
| `AudioProtectedAt` | timestamptz null | non-null once the owner protected the audio from deletion; skips the nightly retention job and refuses the manual delete-audio action |
| `DurationMs` | bigint | measured by the worker for uploads (no client duration) |
| `Status` | int | `RecordingStatus`: 0 Uploaded, 1 Queued, 2 Transcribing, 3 Transcribed, 4 Summarized, 5 Failed, 6 Summarizing, 7 Merging |
| `Error` | text null | last failure message |
| `MinSpeakers` / `MaxSpeakers` | int null | diarization hints (null = automatic) |
| `MeetingTypeId` | uuid FK → MeetingTypes null | chosen minutes template; null = the seeded General default; **SetNull** on type delete |
| `Position` | int | manual sort order within its group |
| `ActionsExtractedAt` | timestamptz null | non-null once action extraction has run (drives the by-exception Actions panel) |
| `TagsExtractedAt` | timestamptz null | non-null once tag extraction has run (even a zero-tag result); null rows are the tag backfill's work list. Left null when the owner has no LLM so a later backfill retries |
| `CreatedAt` | timestamptz | |

Index: `(UserId, CreatedAt)`. Children cascade: `Transcriptions`, `Speakers`, `RecordingActions`, `RecordingTags`.

#### `Transcriptions`
One transcription pass; recordings are **versioned**.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `RecordingId` | uuid FK → Recordings | cascade |
| `Model` | text | e.g. `whisperx-large-v3` |
| `Version` | int | monotonic per recording, starting at 1; highest = current |
| `Language` | text null | ISO-639-1 if detected |
| `ProcessingMs` | bigint null | full-pipeline wall-clock time the worker spent (download+transcribe+diarize+embed) |
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
| `Embedding` | **vector(768)** null | legacy per-segment RAG slot - **unused/null**, superseded by `TranscriptChunks` (a segment is too small a retrieval unit); kept to avoid a drop migration; Postgres-only |

Indexes: `(TranscriptionId, Ordinal)`; GIN trigram index `IX_Segments_Text_Trgm` on
`coalesce("Revised","Original")` (Postgres `pg_trgm`) backing the chat tools' fuzzy transcript search.

#### `TranscriptChunks`
Windowed retrieval chunks for semantic search (RAG / M3). Each row is a window of consecutive segments
(`TranscriptChunker`, ~1200 chars with a 1-segment overlap), embedded as a single vector. Built/replaced
wholesale by the `EmbeddingWorker` on each (re)transcription; a no-op when no embeddings endpoint is
configured.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `TranscriptionId` | uuid FK → Transcriptions | cascade (chunks die with the transcription) |
| `RecordingId` | uuid | denormalized owning recording (citation deep-links + fast scoping; no FK) |
| `UserId` | uuid | denormalized owner, for the owner-scoped vector pre-filter (no FK) |
| `Ordinal` | int | chunk order within the transcription |
| `StartMs` / `EndMs` | bigint | span of the covered segments (min start / max end) |
| `SpeakerLabels` | varchar(1024) | comma-separated distinct speaker display names in the chunk |
| `Text` | text | the flattened "Speaker: Text" body that was embedded |
| `Embedding` | **vector(768)** null | chunk embedding (dimension-pinned to the server embed model; `nomic-embed-text` = 768); Postgres-only |
| `CreatedAt` | timestamptz | |

Indexes: `(UserId, RecordingId)` (owner-scoped pre-filter) and `TranscriptionId`. No ANN index yet - a flat
scan is fine per-user; HNSW is a later optimization. Chunks are always the latest transcription's (replaced on
re-transcribe), so retrieval needs no version filtering.

#### `Summaries`
LLM summary of a specific transcription version (1:1 with `Transcription`).

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `TranscriptionId` | uuid FK → Transcriptions | unique (1:1), cascade |
| `Model` | text | LLM model id used (or `user` for a hand-written/edited summary) |
| `Text` | text | |
| `CreatedAt` | timestamptz | |
| `IsUserEdited` | bool | user hand-wrote/edited it — the auto-summariser won't overwrite it |
| `UpdatedAt` | timestamptz null | when the user last edited it |

#### `MeetingMinutes`
LLM-generated (or hand-edited) meeting minutes for a transcription version (1:1 with `Transcription`),
stored as GitHub-flavoured Markdown. Mirrors `Summaries`; generated in-pipeline on its own Redis stream.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `TranscriptionId` | uuid FK → Transcriptions | unique (1:1), cascade |
| `Model` | text | LLM model id used (or `user` for hand-edited minutes) |
| `Text` | text | Markdown (headings, lists, tables, bold) |
| `CreatedAt` | timestamptz | |
| `IsUserEdited` | bool | user hand-edited it — the auto-generator won't overwrite it |
| `UpdatedAt` | timestamptz null | when the user last edited it |

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
| `Completed` | bool | user-set done flag (default false; reversible) |
| `CompletedAt` | timestamptz null | when marked done; null = not done |

Index: `(RecordingId, Ordinal)`. The cross-meeting Actions list (`GET /api/actions`) joins to `Recordings`
for ownership + display name; bulk complete/un-complete via `POST /api/actions/complete`.

#### `RecordingTags`
LLM-extracted weighted tag-cloud tags. Machine-only (never user-edited): the tags worker **replaces the
whole set** on every (re)transcription, guarded against stale jobs (only the recording's latest
transcription version may write). `GET /api/tags` aggregates them case-insensitively per owner (count +
summed weight + carrying recording ids) for the web's Tags tab cloud.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `RecordingId` | uuid FK → Recordings | cascade |
| `Tag` | varchar(64) | canonical tag text (Title Case, 1-2 words per the prompt) |
| `Weight` | double precision | per-recording salience 0-1 (clamped on ingest) |
| `Ordinal` | int | 0-based, the LLM's weight-descending order |
| `CreatedAt` | timestamptz | |

Index: `(RecordingId, Ordinal)`.

#### `MeetingNotes`
The user's own note lines for a meeting - sparse trigger phrases that (from a later PR) steer minutes
generation. A row is anchored to EITHER a recording (`RecordingId` set) OR an upcoming calendar event
(`CalendarId`+`EventId` set, `RecordingId` null). When a recording's calendar link forms (the `LinkCalendar`
chokepoint - auto-match or manual), the owner's event-anchored lines are **adopted**: `RecordingId` set,
event keys cleared, ordinals appended after existing lines (one-way, additive; unlinking never detaches).

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers | owner; **cascade** (event-anchored notes have no recording) |
| `RecordingId` | uuid FK → Recordings, null | **cascade**; set once anchored/adopted |
| `CalendarId` | varchar(256) null | pre-meeting anchor; cleared on adoption |
| `EventId` | varchar(256) null | pre-meeting anchor; cleared on adoption |
| `Text` | varchar(2048) | the note line (trimmed; blank lines skipped on create) |
| `CapturedAtMs` | bigint null | offset into the recording clock (pause-aware); null = pre-meeting/post-hoc; immutable |
| `Ordinal` | int | 0-based order within the anchor |
| `CreatedAt` / `UpdatedAt` | timestamptz | |

Indexes: `(RecordingId, Ordinal)`, `(UserId, CalendarId, EventId)`. CRUD at
`/api/recordings/{id}/notes` and `/api/calendar/events/{calendarId}/{eventId}/notes`.

#### `Formulas`
A saved prompt + a chosen context, run over a recording to produce a Markdown `FormulaResult`. `Scope`
determines visibility/ownership: `Personal` (owned by one user, `OwnerUserId` set), `Platform` (shared,
admin-managed, no owner), or `Diariz` (seeded, `IsBuiltIn = true`, cannot be deleted).

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `Scope` | int | `Personal`=0, `Platform`=1, `Diariz`=2 (append-only) |
| `OwnerUserId` | uuid FK → AspNetUsers, null | set only for `Personal`; **`ON DELETE CASCADE`** (a user's personal formulas die with the account); null for Platform/Diariz |
| `Name` | varchar(256) | |
| `Description` | varchar(1024) null | |
| `Prompt` | text | |
| `Context` | int **[Flags]** | which parts of the recording the run may see: `Transcript`=1, `Notes`=2, `Attachments`=4, `Summary`=8, `Minutes`=16, `Actions`=32 (append-only) |
| `Enabled` | bool, **DB default true** | Platform/Diariz availability toggle |
| `Shared` | bool, **DB default false** | only meaningful for `Personal` scope: when true, other users can discover this formula and subscribe to it (a live link - see `FormulaSubscriptions`) |
| `IsBuiltIn` | bool, default false | Diariz-seeded; blocks delete |
| `CreatedAt` / `UpdatedAt` | timestamptz | |

Index: `OwnerUserId`.

#### `FormulaResults`
The Markdown document produced by running a `Formula` over a recording. Many per recording (one per run).

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `RecordingId` | uuid FK → Recordings | **cascade** |
| `CreatedByUserId` | uuid FK → AspNetUsers, null | **`ON DELETE SET NULL`** — a result can live on another user's shared recording, so the document survives its author's account deletion with attribution dropped |
| `FormulaId` | uuid FK → Formulas, null | **`ON DELETE SET NULL`** — the result survives its source formula being deleted |
| `Name` | varchar(256) | formula name snapshot, so a later formula rename/delete doesn't relabel past results |
| `Text` | text | generated Markdown body (empty until the run completes) |
| `Ordinal` | int | 0-based order within the recording |
| `Status` | int enum | run lifecycle: `Generating = 0`, `Ready = 1`, `Failed = 2`. The row is created `Generating` when the run is enqueued and flipped by the `FormulaRunWorker`; existing rows were backfilled to `Ready` |
| `Error` | text, null | failure message when `Status = Failed` |
| `CreatedAt` / `UpdatedAt` | timestamptz | |

Indexes: `(RecordingId, Ordinal)`, `FormulaId`, `CreatedByUserId`.

Formula runs are **asynchronous**: `POST .../formulas/{id}/run` creates a `Generating` row, enqueues a
`FormulaRunJob` on the `formula-run-jobs` Redis stream (consumer group `formula-runners`), and returns 202;
the in-process `FormulaRunWorker` runs the LLM and flips the row to `Ready`/`Failed` (SignalR
`FormulaResultStatusChanged`, plus the client polls). The MCP/chat `run_formula` tool stays synchronous.

#### `FormulaSubscriptions`
A subscriber's live link to another user's shared Personal formula (a pointer, not a copy): it lets the
subscriber run the formula and see it under "Shared Formulas" in the run picker, and the owner's edits
propagate. Deleting the formula OR the subscriber cascade-removes the link.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `FormulaId` | uuid FK → Formulas | **`ON DELETE CASCADE`** — deleting the shared formula removes every subscriber's link |
| `UserId` | uuid FK → AspNetUsers | **`ON DELETE CASCADE`** — a subscriber's links die with the account |
| `CreatedAt` | timestamptz | |

Indexes: unique `(FormulaId, UserId)` (a user can't add the same formula twice; the controller is also
idempotent), `UserId`.

#### `Attachments`
Supporting documents on a recording — an uploaded file (blob) or a URL.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `RecordingId` | uuid FK → Recordings | cascade |
| `Kind` | int | `File`=0, `Url`=1 (append-only) |
| `Name` | varchar(512) | display name / link text |
| `BlobKey` | text null | object-storage key (File kind) |
| `ContentType` | text null | MIME of the uploaded file |
| `SizeBytes` | bigint | file size — counts toward the quota (0 for a URL) |
| `Url` | text null | the linked address (Url kind) |
| `Ordinal` | int | 0-based order within the recording |
| `CreatedAt` | timestamptz | |

Index: `(RecordingId, Ordinal)`. Attachment blobs live under MinIO key `{userId}/attachments/{attachmentId}{ext}`.
Markdown attachments (`text/markdown`) are editable in place via `PUT .../attachments/{id}/content`, which
overwrites the same blob key and recomputes `SizeBytes` (quota re-checked on the delta).

#### `SectionAttachments`
Supporting documents filed **directly** on a folder (`Section`) rather than a recording — an uploaded file
(blob) or a URL. Same shape as `Attachments`, keyed on the section; independent of any transcript.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `SectionId` | uuid FK → Sections | cascade (deleting the folder, or a parent folder, removes these) |
| `Kind` | int | `File`=0, `Url`=1 (reuses `AttachmentKind`) |
| `Name` | varchar(512) | display name / link text |
| `BlobKey` | text null | object-storage key (File kind) |
| `ContentType` | text null | MIME of the uploaded file |
| `SizeBytes` | bigint | file size — counts toward the quota (0 for a URL) |
| `Url` | text null | the linked address (Url kind) |
| `Ordinal` | int | 0-based order within the folder |
| `CreatedAt` | timestamptz | |

Index: `(SectionId, Ordinal)`. Blobs live under MinIO key `{userId}/section-attachments/{attachmentId}{ext}`.
Counts toward the owner's storage quota (`StorageUsage` sums recording + section attachment bytes). CRUD +
in-place Markdown edit live in `SectionAttachmentsController` at route `api/sections/{id}/folder-attachments`.

#### `RecordingCalendarLinks`
The Google Calendar event a recording belongs to (1:1 with `Recording`, shared primary key). A lightweight
**snapshot** for cheap list/Calendar-tab rendering; the rich invite details (attendees, description, location,
organiser) are fetched live from Google by `EventId`, never stored.

| Column | Type | Notes |
|---|---|---|
| `RecordingId` | uuid PK / FK → Recordings | shared PK; **cascade** delete with the recording |
| `EventId` | varchar(1024) | Google Calendar event id |
| `CalendarId` | varchar(1024) | which calendar the event is on (`primary` or a secondary/shared/subscribed id); existing rows backfilled to `primary` |
| `Color` | varchar(32) null | the calendar's Google background colour (hex) snapshot, for tinting the linked icon |
| `Summary` | varchar(1024) null | event title snapshot |
| `StartsAt` / `EndsAt` | timestamptz | event span snapshot |
| `HtmlLink` | varchar(2048) null | Google Calendar deep link |
| `LinkedManually` | bool | user picked it by hand (vs. auto-saved best time-overlap match) |
| `SyncedAt` | timestamptz | when the snapshot was last written |

#### `IcsCalendarSources`
Per-user external iCalendar (`.ics`) feed subscriptions - public team/shared calendars or any ICS URL not
reachable through the user's Google account. Events are fetched **live** at read time (SSRF-guarded, https-only)
and merged into the Calendar views tagged `ics:{Id}`; nothing from the feed is stored.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers | indexed; **cascade** delete with the user |
| `Name` | varchar(128) | user label shown in the Calendar views |
| `Url` | varchar(2048) | feed URL (validated https only; re-checked against private IPs on every fetch) |
| `Color` | varchar(32) null | hex colour used to tint this feed's events |
| `Enabled` | bool | off = kept but excluded from reads |
| `CreatedAt` | timestamptz | |
| `LastFetchedAt` | timestamptz null | last successful fetch; null until first read |
| `LastError` | text null | last fetch error (unreachable, non-200, too large, parse failure); null when healthy |

#### `MeetingTypes`
Reusable minutes templates. A **Platform** type (`UserId` null) is created by a Platform Administrator and is
shared read-only to everyone (the app seeds a standard set on startup, insert-if-missing by `Key`); a **Personal**
type (`UserId` set) is a user's own, with full CRUD. `ContentJson` holds the structured template (an ordered list
of H1/H2/H3 sections whose blocks are boilerplate text, substituted recording values, model prompts, or a
horizontal rule - `hr` - that emits a Markdown divider). Each block
also carries an optional **`breakAfter`** (`"none" | "line" | "paragraph"`) controlling the whitespace emitted
after it when the minutes are composed; a null/absent value uses the legacy rule (a `field` glues to the preceding
block, otherwise a paragraph break). It lives inside the existing `ContentJson` blob, so it needs **no migration**.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers null | **null = Platform** (shared); non-null = a Personal type. Indexed; **cascade** delete with the user |
| `RoomId` | uuid null | the owning room (a Personal type's owner personal room; **null for Platform types**, mirroring `UserId`). Plain column, no FK yet (Phase 4); populated on create, still queried by `UserId` for now |
| `Key` | varchar(64) null | stable slug for the seeded standards (**unique**; multiple NULLs for user-created types); null for user types |
| `GroupName` | varchar(128) | grouping label in the picker |
| `Title` | varchar(256) | |
| `Overview` | text | context prepended to model prompts |
| `Icon` | varchar(64) | icon key from the app's fixed set |
| `Color` | varchar(32) | icon background colour (hex) |
| `ContentJson` | **jsonb** | the structured template (sections/blocks); Postgres jsonb, plain text under the in-memory provider |
| `CreatedAt` | timestamptz | |
| `UpdatedAt` | timestamptz null | |

`Recordings.MeetingTypeId` (uuid FK → MeetingTypes, null) points at the chosen type; **`ON DELETE SET NULL`** so
deleting a type drops its recordings back to the General default. Null = the seeded General Meeting default.

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
| `IsMultiSpeaker` | bool | user marked this slot as overlapping speech ("Multiple Speakers"); never auto-identified or enrolled into a voiceprint |

Unique index: `(RecordingId, Label)`.

#### `SpeakerProfiles`
An enrolled person's voiceprint (per user). Biometric data — GDPR-erasable.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers | cascade |
| `RoomId` | uuid not-null | the owner's personal room. Plain column, no FK yet (Phase 4); populated on create, still queried by `UserId` for now |
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

#### `SectionSummaries` / `SectionMinutes`
The folder-level LLM roll-ups shown on the section (folder) page - a summary combining the included
recordings' summaries, and minutes reshaping their minutes through a template. Each is **1:1 with `Section`**
(cascade), mirroring `Summary`/`MeetingMinutes` (which are per-`Transcription`). Generated asynchronously by
the `SectionSummaryWorker`/`SectionMinutesWorker`; "included" = recordings whose **placement** (`RoomRecordings.SectionId`) is the section or
one of its child sections.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `SectionId` | uuid FK → Sections | **unique** (1:1); cascade |
| `MeetingTypeId` | uuid FK → MeetingTypes null | **`SectionMinutes` only** - the folder's chosen template; `ON DELETE SET NULL` |
| `Model` | text | LLM model, or `"user"` for a hand-edit |
| `Text` | text | the summary (plain) / minutes (Markdown) |
| `CreatedAt` | timestamptz | |
| `IsUserEdited` | bool | protects a hand-edit from the next regenerate |
| `UpdatedAt` | timestamptz null | last user edit |
| `Status` | int | `SectionGenerationStatus`: 0 Idle, 1 Generating, 2 Ready, 3 Failed |
| `Error` | text null | last generation error (when Failed) |

#### `ChatSessions`
Saved chat conversations; stateless server (thread + context stored as JSON).

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers | cascade |
| `RoomId` | uuid not-null | the owner's personal room. Plain column, no FK yet (Phase 4); populated on create, still queried by `UserId` for now |
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
| `ChatToolsEnabled` | bool null | chat tool-calling master override; null → server `Chat:ToolsEnabled` |
| `ChatToolOverridesJson` | jsonb null | explicit per-tool on/off map `{ "tool_name": bool }`; a tool absent follows the server default |
| `ReasoningEnabled` | bool null | send `reasoning_effort` on LLM requests; null → server `Summarization:ReasoningEnabled` |
| `ReasoningEffort` | text null | reasoning level (`low`/`medium`/`high`) when enabled; null → server `Summarization:ReasoningEffort` |
| `NativeLanguage` | text null | the user's native language (BCP-47); default target when translating transcripts |
| `UiLanguage` | text null | the language the app UI is shown in (BCP-47); null → follow the browser |
| `GoogleRefreshTokenEncrypted` | text null | Google OAuth refresh token (offline Calendar access), **encrypted at rest** (Data Protection); never returned to clients |
| `GoogleCalendarGranted` | bool | user granted Google Calendar read access |
| `GoogleSelectedCalendarIdsJson` | jsonb null | JSON array of the Google calendar ids to consider for attribution + the overlay; null → not chosen (fall back to the Google-visible calendars + primary) |
| `JobTitle` / `CompanyName` / `LinkedIn` | varchar(256) null | free-text profile fields |
| `JobDescription` / `CompanyDescription` | varchar(2048) null | free-text profile fields |
| `Theme` | int | UI colour theme (`ThemePreference`): `0` = Auto (default), `1` = Light, `2` = Dark. Append-only enum |
| `RecordingPlacementMode` | int | where a new recording is filed in the user's personal room (`RecordingPlacementMode`): `0` = Ungrouped, `1` = SelectedFolder (default - the folder they had open), `2` = SpecificFolder. Append-only enum |
| `RecordingPlacementSectionId` | uuid null | the fixed folder for `SpecificFolder` mode; null in the other modes |

Each field falls back to the server `Summarization`/`Chat` defaults when null. The display name lives on
`AspNetUsers.FullName` (editable via `PUT /api/user/profile`), not here.

#### `PlatformSettings`
Single seeded row (`Id = 1`), edited by the Platform Administrator.

| Column | Type | Notes |
|---|---|---|
| `Id` | int PK | always 1 |
| `StarterQuotaBytes` | bigint | quota granted to new users (default 5 GiB) |
| `MaxQuotaBytes` | bigint | ceiling any admin may raise a user to (default 50 GiB) |
| `MinutesGenerationMode` | int | how template-driven minutes generate: `0` = SingleCall (default), `1` = PerSection. Append-only enum |
| `AutoDeleteAudioEnabled` | bool | master switch for the nightly audio-retention job (default false = off) |
| `AudioRetentionDays` | int | audio older than this many days (by `Recording.CreatedAt`) is eligible for auto-deletion (default 30) |
| `AudioDeletionTimeOfDay` | time | server-local time of day the nightly retention job runs (default 03:00) |
| `ApiAccessEnabled` | bool | master switch for user API access (personal `dz_api_` tokens); default false = off |
| `LlmTimeoutSeconds` | int | platform-wide per-request timeout (seconds) for every LLM call - the single authority (the HTTP clients have no cap); default 120 |

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
| `GoogleSubject` | varchar(256) null | linked Google account `sub` (**unique index**; nullable → many password-only NULLs allowed) |
| `PictureUrl` | varchar(1024) null | Google profile picture URL (avatar; falls back to initials) |

Roles: `Standard`, `Administrator`, `PlatformAdministrator` (rows in `AspNetRoles`).

#### `McpAccessTokens`
Per-user MCP personal access tokens (used by Claude to connect to `/mcp`). Only the hash is stored — the
plaintext is shown to the user once at generation and is never recoverable.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers | owner; **cascade** on user delete |
| `Name` | varchar(128) | user label (e.g. "Claude Desktop") |
| `TokenHash` | varchar(64) | lowercase-hex SHA-256 of the full token; **unique index** (incoming tokens are hashed and looked up) |
| `Prefix` | varchar(32) | short non-secret display prefix (e.g. `dz_mcp_ab12cd`) |
| `CreatedAt` | timestamptz | |
| `LastUsedAt` | timestamptz null | last time the token was presented on an MCP request |

Indexes: unique `(TokenHash)`, `(UserId)`.

#### `ApiAccessTokens`
Per-user personal REST-API tokens (`dz_api_…`), used to call the Diariz API as the owning user. Same shape and
storage discipline as `McpAccessTokens` (hash-only, shown once), but a **separate** credential: gated by
`PlatformSettings.ApiAccessEnabled` and accepted on the general `/api/*` surface (not `/mcp`).

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers | owner; **cascade** on user delete |
| `Name` | varchar(128) | user label (e.g. "CI pipeline") |
| `TokenHash` | varchar(64) | lowercase-hex SHA-256 of the full token; **unique index** |
| `Prefix` | varchar(32) | short non-secret display prefix (e.g. `dz_api_ab12cd`) |
| `CreatedAt` | timestamptz | |
| `LastUsedAt` | timestamptz null | last time the token was presented on an API request |

Indexes: unique `(TokenHash)`, `(UserId)`.

#### `UserGroups`

Platform authority. A user's effective permissions are the **union** of the flags on every group they belong to,
resolved from the database on each request (never from a token claim).

| Column | Type | Notes |
|---|---|---|
| `Id` | `uuid` | PK |
| `Name` | `varchar(128)` NOT NULL | **Unique index** (`IX_UserGroups_Name`) |
| `Description` | `text` null | |
| `Icon` | `text` null | Icon key from the shared set (unused until Rooms) |
| `Color` | `text` null | Hex swatch (unused until Rooms) |
| `Permissions` | `int` NOT NULL | `[Flags] PlatformPermission`: `ManageRooms = 1`, `ManageUsers = 2`, `ManagePlatform = 4`. **Append-only** |
| `IsSystem` | `bool` NOT NULL | True for the seeded `Platform Administrators`: undeletable, name/permissions immutable, last member cannot be removed |

Seeded: `Platform Administrators` (`IsSystem`, flags `7`) and `Administrators` (flags `3` — **no**
`ManagePlatform`, which confers backup/restore and platform-settings writes).

#### `UserGroupMembers`

| Column | Type | Notes |
|---|---|---|
| `GroupId` | `uuid` | PK part 1. FK → `UserGroups`, **cascade** |
| `UserId` | `uuid` | PK part 2. FK → `AspNetUsers`, **cascade**; index `IX_UserGroupMembers_UserId` |

Deleting a group removes its memberships and leaves the users; deleting a user removes their memberships and
leaves the groups.

#### `RoomRecordings`

The placement of a recording in a room. A recording has exactly one **main** placement — always in its
recorder's Personal room — plus one row per room it has been shared into. The **folder is a property of the
placement**, so the same recording can sit in different folders in different rooms; that is why
`Recordings.SectionId` no longer exists.

| Column | Type | Notes |
|---|---|---|
| `RoomId` | `uuid` | PK part 1. FK → `Rooms`, **cascade** |
| `RecordingId` | `uuid` | PK part 2. FK → `Recordings`, **cascade** |
| `IsMainRoom` | `bool` | True on exactly one row per recording (**filtered** unique index `WHERE "IsMainRoom"`), and that row's room is the personal room of `Recording.UserId`. Because the main room is always personal, deleting a shared room can only ever unshare — never destroy |
| `SectionId` | `uuid` null | The folder **within this room**. Null = ungrouped. FK → `Sections` **`ON DELETE SET NULL`** (deleting a folder ungroups the placement, never removes it from the room). Index `(RoomId, SectionId)` |
| `SharedByUserId` | `uuid` null | Null on the main-room row: nobody shared a recording into its own home |
| `SharedAt` | `timestamptz` null | As above. `CK_RoomRecordings_MainRoomHasNoSharer` enforces that a main placement carries neither |
| `Position` | `int` | Manual sort order of the recording **within this room** (lower = higher; ties → newest-first by `Recording.CreatedAt`). Per-placement, so a recording can be ordered differently in two rooms. Default 0; supersedes the now-dead global `Recording.Position`. Written by `PUT /api/recordings/reorder`; read by `GET /api/recordings` ordering |

Backfilled once by the `AddRoomRecordings` migration: one main placement per existing recording, in its
recorder's personal room, carrying the folder it was filed under. Minting a personal room first for any user
who lacks one (`RecordingPlacementBackfill`).

#### `Rooms`

A workspace: folders, recordings, voiceprints, chats and meeting types all live in one. Every user has exactly
one **Personal** room; a recording's main room is always its recorder's Personal room.

| Column | Type | Notes |
|---|---|---|
| `Id` | `uuid` | PK |
| `Name` | `varchar(128)` NOT NULL | **Filtered** unique index `WHERE "Kind" = 1` — shared-room names are identifiers, personal-room names are display labels (the owner's name) and two users may share one |
| `Description` | `text` null | |
| `Icon` | `text` null | Icon key. Null for personal rooms (the owner's avatar is shown) |
| `Color` | `text` null | Hex. Null for personal rooms |
| `Kind` | `int` NOT NULL | `Personal = 0`, `Shared = 1`. **Append-only** |
| `OwnerUserId` | `uuid` null | Personal rooms only. FK → `AspNetUsers` **`ON DELETE SET NULL`**. **Filtered** unique index `WHERE "OwnerUserId" IS NOT NULL` — one personal room per user, any number of orphans |
| `CreatedAt` | `timestamptz` NOT NULL | |

An **orphaned** room is `Kind = 0` with `OwnerUserId IS NULL`: what a deleted user leaves behind. Its recordings
survive in the shared rooms they were shared into, and it appears in no switcher. Cascading the delete instead
would destroy recordings that live in other people's rooms.

`RoomMembers.PrincipalId` carries no FK (it points at either `AspNetUsers` or `UserGroups`), so the database
cannot cascade. Deleting a user (`AdminUsersController.Delete`) therefore **sweeps** their `RoomMembers` rows
explicitly, and deleting a group (`GroupsController.Delete`) sweeps its own, before the principal is removed.
Without the sweep a stale row would survive: inert on an orphaned personal room, but a live grant in a shared
room once those have members.

Backfilled once by the `AddRooms` migration: one Personal room per existing user, named after them
(`FullName` → `Email` → `"Personal"`), with the owner holding every permission (`63`).

#### `RoomMembers`

| Column | Type | Notes |
|---|---|---|
| `RoomId` | `uuid` | PK part 1. FK → `Rooms`, **cascade** |
| `PrincipalType` | `int` | PK part 2. `User = 0`, `Group = 1`. **Append-only** |
| `PrincipalId` | `uuid` | PK part 3. An `AspNetUsers.Id` or a `UserGroups.Id`, per `PrincipalType`. **No FK** — it points at one of two tables, so the database cannot cascade. Index `IX_RoomMembers_PrincipalType_PrincipalId` |
| `Permissions` | `int` NOT NULL | `[Flags] RoomPermission`: `ManageRoom = 1`, `CreateRecording = 2`, `RemoveOthersRecordings = 4`, `ShareOut = 8`, `ManageContents = 16`, `EditOthersRecordings = 32`. **Append-only** |

A caller's effective permissions in a room are the **union** of their own row and the rows of every group they
belong to, resolved by `RoomScope`. The **owner of a personal room implicitly holds everything** and needs no
row; a personal room ignores member rows entirely, which is what makes it structurally private.

`RemoveOthersRecordings` cannot destroy a recording. Because a recording's main room is always its recorder's
Personal room, the permission can only ever unshare it from this room.

#### `AspNetUserRoles` (legacy)

Still present and still written by the seeder for the seed user, but **no longer read for authorization**.
Superseded by `UserGroupMembers`. Dropping the Identity role tables is a later chore.

#### `OpenIddict*` (library-managed)
`OpenIddictApplications`, `OpenIddictAuthorizations`, `OpenIddictScopes`, `OpenIddictTokens` are created by
`ModelBuilder.UseOpenIddict()` (string primary keys) and back the OAuth 2.1 authorization server for the MCP
web connector. Their columns are defined by the OpenIddict EF Core stores (not a Diariz entity class), so they
are not enumerated here - a registered `Application` is a dynamically-registered OAuth client (client id, public
type, redirect URIs, permitted scopes/grant types, PKCE requirement); an `Authorization` + its `Tokens`
represent a user's granted, revocable connection. Revoking a connection deletes the authorization and its
tokens. See `Overall_Synopsis_of_Platform.md` for the auth flow.

### Vector columns summary

| Table.Column | Dim | Model | Purpose |
|---|---|---|---|
| `Speakers.Embedding` | 192 | SpeechBrain ECAPA (`spkrec-ecapa-voxceleb`) | per-recording speaker voiceprint |
| `SpeakerProfiles.Embedding` | 192 | (centroid of ECAPA) | enrolled person's voiceprint |
| `ProfileContributions.Embedding` | 192 | ECAPA snapshot | recompute centroids without the worker |
| `TranscriptChunks.Embedding` | 768 | `nomic-embed-text` (server-pinned) | RAG semantic search over windowed transcript passages (M3) |
| `Segments.Embedding` | 768 | - | legacy, **unused** (superseded by `TranscriptChunks`) |

Changing an embedding model means a migration to resize the column **and** re-enrolment/re-embedding.

---

## 2. MinIO (object storage)

### What's stored

**The original audio blobs and uploaded attachment files.** Nothing else (no transcripts, no derived files)
lives in MinIO — those are in Postgres. Transcript downloads (TXT/MD/RTF/SRT) and the emailed HTML are
rendered on demand by the API from the database.

### Bucket & key layout

- **Bucket:** `recordings` (configurable via `Storage:Bucket`). Created on API startup
  (`AudioStorage.EnsureBucketAsync`) if absent.
- **Object key:** `{userId}/{recordingId}{ext}` — e.g. `8f3a…/c1b2…​.webm`. The extension comes from the
  uploaded file name (recordings default to `.webm`). The `userId` prefix gives a natural per-user "folder"
  and keeps keys unguessable (random Guids).
- The key is stored on `Recording.BlobKey`; `Recording.ContentType` holds the MIME type.
- **Attachment files** use key `{userId}/attachments/{attachmentId}{ext}` (stored on `Attachment.BlobKey`);
  the API streams them back same-origin (inline) and counts their bytes toward the user's quota.
- **Folder-direct attachment files** use key `{userId}/section-attachments/{attachmentId}{ext}` (stored on
  `SectionAttachment.BlobKey`); same streaming + quota behaviour, keyed on the folder instead of a recording.
- **Blob lifecycle on delete/merge:** deleting a recording also deletes its attachment-file blobs (the DB
  cascade only removes the rows). Merging **moves** the merged-away recordings' attachments onto the survivor
  (rows reparented, blobs kept), so nothing is orphaned; the audio-merge worker callback also defensively
  frees any attachment blob still on a source it removes.

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
