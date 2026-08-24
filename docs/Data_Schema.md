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
| `AddLlmTimeout` | `PlatformSettings.LlmTimeoutSeconds` (int, NOT NULL, default 120) — the platform-wide default per-request timeout applied to every LLM call; the HTTP clients have no cap of their own. Superseded as the sole authority by `AddUserLlmTimeout` below, which adds a per-user override on top |
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
| `AddFormulaResultIsUserEdited` | `FormulaResults.IsUserEdited` + `SectionFormulaResults.IsUserEdited` (bool, default false) - set when the document's Markdown is hand-edited. A run now **replaces** that formula's previous result rather than appending, so this is what stops an **automatic** re-run (a meeting type's additional formulas, re-firing whenever the minutes regenerate) from destroying the user's own words; an **explicit** run still replaces it and clears the flag. Mirrors `MeetingMinutes.IsUserEdited`. Additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `MeetingTypesPointAtFormulas` | `MeetingTypes.PrimaryFormulaId` (uuid FK → Formulas, **RESTRICT**) + `MeetingTypeFormulas` (the additional formulas run alongside the minutes; `MeetingTypeId`/`FormulaId` FK **Cascade**, `Ordinal`, unique on the pair) replace `MeetingTypes.ContentJson`: the column and table are added, **every meeting type's template is converted into a Formula** (preserving scope - a seeded standard becomes a built-in `Diariz` formula, an admin Platform type a `Platform` one, a user's Personal type a `Personal` one they own) and linked, and only then is `ContentJson` dropped. `Down` copies the primary formula's template back before unlinking (break-glass, not an inverse). The data is carried forward rather than discarded, so an older backup still restores and is rolled up by this migration - **no `MaintenanceController.CurrentFormat` bump** |
| `AddFormulaContent` | `Formulas.ContentJson` (**jsonb**, default `{"sections":[]}`) replaces `Formulas.Prompt`: the column is added, **backfilled from `Prompt`** (each prompt wrapped as one headless level-0 section holding one prompt block, via `jsonb_build_object` so any quotes/newlines stay escaped), and only then is `Prompt` dropped. A formula becomes a structured template, the same shape a meeting type uses. `Down` recovers the first prompt block (lossy for a genuinely structured formula - break-glass, not an inverse). Destructive column drop, but the data is preserved by the backfill and older backups restore fine onto it, so **no `MaintenanceController.CurrentFormat` bump** |
| `AddSectionFormulaResults` | `SectionFormulaResults` table (a formula run over a folder + its sub-sections; `SectionId` FK `ON DELETE CASCADE`, `FormulaId`/`CreatedByUserId` FK `ON DELETE SET NULL`, `Status`/`Error` like `FormulaResults`, index `(SectionId, Ordinal)`) — additive new table, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddMeetingScreenshots` | `MeetingScreenshots` (screen captures taken during a recording from the desktop app; cascade FKs from both `AspNetUsers` and `Recordings`, index `(RecordingId, CapturedAtMs)`) — additive new table, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddSectionAttachmentUploader` | `SectionAttachments.UploadedByUserId` (uuid, not-null, plain column - no FK, mirrors `Sections.RoomId`'s "not yet" pattern; indexed). Storage quota (`StorageUsage`) now sums a folder's file attachments by whoever **uploaded** them instead of `Section.UserId` (the folder's creator) - the two can differ once a shared-room member with `ManageContents` can add to a folder they didn't create. **Backfills** every existing row from its `Section.UserId` (that's who it was charged to before this column existed, so the backfill is a no-op in effect) — additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddApiTokenScopeExpiry` | `ApiAccessTokens.Scope` (int, not-null, **default 1** = ReadWrite, so every pre-existing token keeps full access) + `ApiAccessTokens.ExpiresAt` (timestamptz null, default never-expires) — least-privilege, time-boxed personal API tokens; additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddPlatformIntegrationToggles` | `PlatformSettings.McpAccessEnabled` (bool, not-null, **default true**, existing row updated to true so an already-connected MCP client is not broken) + `PlatformSettings.WebhooksEnabled` (bool, not-null, default false) — split the single implicit "integrations" surface into three independent admin toggles (API access already existed; MCP and Webhooks join it) — additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddWebhooks` | `Webhooks` (a user's outbound webhook subscription, backing the `WebhookSubscription` entity - table name predates a later rename; `Scope` int 0=Personal/1=Platform, default Personal, Phase 2 only supports Personal; `OwnerUserId` FK `ON DELETE CASCADE`; `SecretEncrypted` **text**, Data-Protection-encrypted HMAC secret; `EventTypes` **text**, comma-separated event keys; index `OwnerUserId`) + `WebhookDeliveries` (one queued/sent event per matching subscription; `SubscriptionId` FK `ON DELETE CASCADE`; `PayloadJson` **text, not jsonb** - preserved byte-for-byte so the HMAC signature computed over it stays valid across retries; `Status` int enum `Pending/Delivered/Failed`; composite index `(Status, NextAttemptAt)` - the delivery worker's due-poll query; index `SubscriptionId`) — additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddWorkflowSignals` | `Webhooks.SignalFilter` (varchar(1024) null, comma-separated Workflow Signal keys this subscription routes on; empty/null deliberately matches nothing, so a Platform subscription must pick at least one signal to ever fire) + `WorkflowSignals` (the admin-defined named routing vocabulary; `Key` varchar(64) **unique**, immutable after creation; `Label` varchar(200); `Description` text null; `IsActive` bool) + `FormulaWorkflowSignals` (join table, composite PK `(FormulaId, WorkflowSignalId)`, both FKs `ON DELETE CASCADE`, index `WorkflowSignalId`) - Phase 3 of the Integrations roadmap: lets a formula author attach one or more admin-defined signals to a formula and a Platform Administrator route a `Webhooks` row of `Scope = Platform` to those same signals, so the formula's completion/failure event fans out across every user through that one wired automation. Additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddWebhookAttendeeContacts` | `Webhooks.IncludeAttendeeContacts` (boolean NOT NULL DEFAULT false) - opt-in, per subscription, to include attendees' email addresses and phone numbers in outbound payloads. Additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddPersonDirectory` | Turns `SpeakerProfiles` into the people directory (CLR type `Person`). `Embedding`, `UserId` and `RoomId` become **nullable** (`DROP NOT NULL`), so a person can exist with no voiceprint; adds `Title`, `CompanyName`, `Email`, `Phone`, `IsInternal`, `VoiceprintOptOut`, `LinkedUserId`; adds an `(Email)` index and a **filtered unique** `(LinkedUserId) WHERE NOT NULL`; `COMMENT ON COLUMN` documents the `UserId`/`LinkedUserId` split. **Backfills** one person per Active user (`PersonForUserBackfill`; Requested/Invited accounts get theirs at CompleteSetup). The tables and columns are **not** renamed - only the CLR types are - so this is additive plus three `DROP NOT NULL`s: forward-restore-safe, **no `MaintenanceController.CurrentFormat` bump** |
| `AddWebhookDeliveryLastAttemptAt` | `WebhookDeliveries.LastAttemptAt` (timestamptz null) - records when the worker last contacted the target, so the delivery worker can enforce a per-subscription rolling-minute rate cap (`WebhookOptions.MaxPerSubscriptionPerMinute`, default 120) that paces bursts to a single fan-out automation. Additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddFeedback` | `Feedback` (a user's "something looks or behaves wrong" report; `UserId` FK → `AspNetUsers`, **cascade** on user delete; `Description`/`Route`/`Release`/`TrailJson` text, not null; `ScreenshotBlobKey` text null - reserved for a deferred screenshot phase, always null today; index `(UserId)`) - readable and deletable only by a Platform Administrator, including a submitter's own. Additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddIncludeFeedbackText` | `Webhooks.IncludeFeedbackText` (boolean NOT NULL DEFAULT false) - opt-in, per **Platform** subscription, to include the submitter's own words in a `feedback.submitted` payload. Additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddRecordingStartedAt` | `Recordings.StartedAt` / `Recordings.EndedAt` (timestamptz null) plus index `(UserId, StartedAt)` - the recording's true wall-clock span, so calendar matching stops spanning from upload time (which made a recorded meeting's window a full recording-length late, so it overlapped nothing). **Backfills** `StartedAt = CreatedAt - DurationMs` for existing rows where `Source <> 2` (not an upload, whose `CreatedAt` says nothing about when the audio was recorded) and `DurationMs > 0`; `EndedAt` is left null on backfilled rows so `recEnd` falls back to exactly the pre-migration value rather than a guess. Additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddOutlookCalendarSync` | `OutlookCalendarSources` (one per user+device, unique `(UserId, DeviceId)`, cascade on user delete) and `OutlookCalendarEvents` (flattened occurrences; deterministic uuid PK, unique `(SourceId, Uid)`, indexes `(UserId, StartsAt)` and `(SourceId, StartsAt)`, `AttendeesJson` as **jsonb**, cascade from both the source and the user), plus `UserSettings.OutlookSyncEnabled` (boolean NOT NULL DEFAULT false - the privacy opt-in). Two new tables and one defaulted column: additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddCalendarRecordingPreferences` | `UserSettings.CalendarAutoStopEnabled` (boolean NOT NULL DEFAULT false) + `CalendarAutoStopAfterMinutes` (int NOT NULL **DEFAULT 3**) + `CalendarSilenceStopSeconds` (int NOT NULL **DEFAULT 30**) - how a recording started by joining a meeting from the calendar should end. The two int defaults are set in the **column**, not just the C# initialiser: these columns land on a table that already has rows, and EF's usual `defaultValue: 0` would have meant "stop the moment recording starts" / "end on zero seconds of silence" for every existing user. Additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddCalendarSeriesId` | `RecordingCalendarLinks.SeriesId` (varchar(1024) null) - which recurring series a linked event belongs to, so a recording's earlier occurrences of the same meeting can be listed. **Backfills** existing Google/`.ics` links by stripping the `_{yyyyMMddTHHmmssZ}` occurrence suffix off `EventId`, and existing Outlook links (`EventId` = `outlook:{id}`) by joining `OutlookCalendarEvents` on that id and taking the `#`-prefix of its `Uid`; links the backfill cannot resolve are left null. Additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `OutlookNarrowSyncStamp` | `OutlookCalendarSources.LastNarrowSyncedAt` (timestamptz null) - when this device last completed a narrow (<= 2 day) push, so the desktop's "Sync today" has its own 10s cooldown instead of sharing the full run's 60s one and being refused in the moment it exists for. Additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddRecordingTagStatus` | `RecordingTags.Status` (int, NOT NULL, **default 0** = `Suggested`) + `RecordingTags.AdoptedAt` (timestamptz null) - tags become manual: the default demotes every existing tag to a suggestion, so the tag cloud and tag search start empty and rebuild only as users adopt tags. Also creates the Postgres-only unique index `IX_RecordingTags_RecordingId_TagLower` on `(RecordingId, lower("Tag"))`, first deleting legacy case-variant duplicates so the index can be created. Additive and forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) - an older backup's tags simply arrive as suggestions |
| `AddUserLlmTimeout` | `UserSettings.LlmTimeoutSeconds` (int, nullable, no default = null) - a per-user override of the platform LLM timeout; null means inherit `PlatformSettings.LlmTimeoutSeconds`, which in turn falls back to the server option. Additive and nullable, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddLlmCalls` | `LlmCalls` (one row per outbound model call - kind, attribution, model/endpoint, timing, token counts, prompt size, success/error, streamed; `UserId`/`RecordingId`/`SectionId` FKs **`ON DELETE SET NULL`**, each paired with a denormalized snapshot column so a row stays readable after its subject is deleted; five indexes) - the LLM usage log's storage. Never stores prompt or completion content. New table, additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddLlmUsageSettings` | `PlatformSettings.LlmUsageLoggingEnabled` (bool, default **true**) + `LlmUsageRetentionDays` (int, default 90; 0 = keep forever) + `LlmStreamUsageEnabled` (bool, default true) - the three admin controls for the usage log, edited on Model Settings. Additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `PlatformLlmModels` | `LlmModels`, `LlmModelParameters` (`jsonb`), `LlmCallAssignments` + `PlatformSettings.DefaultLlmModelId` - platform-wide model configuration. Purely additive, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddLlmCallFinishReason` | `LlmCalls.FinishReason` (text, nullable) - the model's reason for stopping, so a reply cut off by a token cap is distinguishable from one that had nothing to say. Additive and nullable, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddTranscriptionLanguage` | `Recording.TranscriptionLanguage` + `UserSettings.TranscriptionLanguage` (both text, nullable) - the spoken language to transcribe in, per recording and as a per-user default; null on both means Whisper detects it. Additive and nullable, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `DropPerUserLlmSettings` | **Drops** `UserSettings.SummaryApiBase`, `SummaryApiKeyEncrypted`, `SummaryModel`, `ChatContextWindow`, `LlmTimeoutSeconds`, `ReasoningEnabled`, `ReasoningEffort` - LLM configuration moved to the platform. Destructive, but **deliberately no `CurrentFormat` bump**: restore does `pg_restore --clean` then migrates forward, so an older backup restores its own columns and this migration drops them - the restore succeeds and the platform is left correct, and the only loss is per-user values this release discards by design |
| `ChatModelSelection` | `LlmModels.DisplayName` (varchar(128) null - the user-facing name; null or blank falls back to `Name`) + `LlmModels.ChatEnabled` (boolean NOT NULL DEFAULT false - offered in the chat model picker) + `UserSettings.ChatModelId` (uuid null, FK -> `LlmModels` **`ON DELETE SET NULL`**). **No backfill of `ChatEnabled`**: `ChatModelCatalog` offers the chat-assigned model implicitly, so a platform upgraded with zero ticked rows behaves exactly as before, and writing rows to state what the code infers would be a one-way data move for nothing. Additive and defaulted, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddAutoMergeSpeakerSegments` | `UserSettings.AutoMergeSpeakerSegments` (boolean NOT NULL DEFAULT false) - whether consecutive same-speaker segments are collapsed automatically once a recording finishes transcribing. Additive and defaulted, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddLlmTestRecording` | `UserSettings.LlmTestRecordingId` (uuid, nullable, **no FK**) - the recording an administrator last chose to test an AI model against in the model editor's test rail. Deliberately unconstrained: it is resolved on read and nulled out when the recording is gone, so an admin's convenience setting can never be a reason a user's recording will not delete (and a nullable tracked FK would not enforce anything anyway - EF nulls it first). Additive and nullable, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddLlmModelDescription` | `LlmModels.Description` (varchar(200) null) - the administrator's short phrase for a model, shown beside its name in the chat model picker. Additive and nullable, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `AddScreenshotOcr` | `MeetingScreenshots.OcrText` (text null), `OcrModel` (text null), `OcrGeneratedAt` (timestamptz null) - text read off a capture by an OCR model, plus which model read it and when. Additive and nullable, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
| `SyncPersonalRoomNames` | **No schema change.** A one-time data correction (`PersonalRoomNameBackfill`): points every Personal room's `Name` at its owner's display name, using the same `COALESCE(NULLIF(TRIM("FullName"), ''), "Email", 'Personal')` expression `RoomScope.Display` applies. Personal rooms are immutable to the user, so the name is purely derived and this cannot clobber anything hand-typed. Not destructive - `MaintenanceController.CurrentFormat` is unchanged and older backups still restore |

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
 └─1:n─ SectionFormulaResult (via FormulaId, SetNull)  folder run; survives its Formula being deleted
 └─1:n─ FormulaSubscription (cascade)              a shared Personal formula's subscriber links
Recording
 └─1:n─ FormulaResult (cascade)                    RecordingId
Section
 └─1:n─ SectionFormulaResult (cascade)             SectionId (a folder's formula run results)
ApplicationUser
 └─1:n─ FormulaResult (SetNull)                    CreatedByUserId (nullable; doc survives author deletion)
 └─1:n─ SectionFormulaResult (SetNull)             CreatedByUserId (nullable)
 └─1:n─ FormulaSubscription (cascade)              UserId (a subscriber's links die with the account)
 └─1:n─ Webhooks (cascade)                         OwnerUserId (a subscription's owner)
 │       └─1:n─ WebhookDeliveries (cascade)        SubscriptionId
 └─1:n─ Feedback (cascade)                         UserId (readable/deletable only by a Platform Administrator)
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
| `TranscriptionLanguage` | text null | the spoken language to transcribe in (BCP-47, from the supported-language list). Null = fall back to `UserSettings.TranscriptionLanguage`, then to Whisper's own detection |
| `MeetingTypeId` | uuid FK → MeetingTypes null | chosen minutes template; null = the seeded General default; **SetNull** on type delete |
| `Position` | int | manual sort order within its group |
| `ActionsExtractedAt` | timestamptz null | non-null once action extraction has run (drives the by-exception Actions panel) |
| `TagsExtractedAt` | timestamptz null | non-null once tag extraction has run (even a zero-tag result); null rows are the tag backfill's work list. Left null when the owner has no LLM so a later backfill retries |
| `CreatedAt` | timestamptz | when the row was created, i.e. when the **upload landed** - for a recorded take, roughly when it stopped. Keep for retention/ordering, not for "when the meeting happened" |
| `StartedAt` | timestamptz null | wall clock capture began, reported by the client and sanity-checked server-side (rejects >24 h future / >366 d past, normalised to UTC). Null for `Source=Upload` and pre-`AddRecordingStartedAt` rows; callers fall back to `CreatedAt`. **This is what calendar matching spans from** |
| `EndedAt` | timestamptz null | wall clock capture stopped. Null when unknown; callers fall back to `StartedAt + DurationMs`. Stored separately because `DurationMs` is captured-audio length - it excludes paused time, and after a merge it is the concatenated length - so it cannot describe a wall-clock span |

Index: `(UserId, CreatedAt)`, `(UserId, StartedAt)`. Children cascade: `Transcriptions`, `Speakers`, `RecordingActions`, `RecordingTags`.

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
A tag on a recording, either typed by hand or extracted by the LLM as a suggestion. `Status` is what makes
the two kinds meaningful: only `Adopted` rows are the user's own and reach the cross-transcript tag cloud;
`Suggested` rows are candidates nobody has acted on yet; `Dismissed` rows are tombstones so a rejected
suggestion is not offered again on that recording. The tags worker **replaces only the `Suggested` rows** on
every (re)transcription - adopted tags and dismissals survive - guarded against stale jobs (only the
recording's latest transcription version may write). `GET /api/tags` aggregates **`Adopted`** rows only,
case-insensitively per owner (count + summed weight + carrying recording ids), for the web's Tags tab cloud.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `RecordingId` | uuid FK → Recordings | cascade |
| `Tag` | varchar(64) | tag text, normalised: internal whitespace collapsed to hyphens, case preserved, never contains a space. Stored normalised whether it arrived as an LLM suggestion or was typed by hand; promoting a suggestion rewrites its text to the normalised form |
| `Weight` | double precision | for a `Suggested` row, the LLM's per-recording salience 0-1 (clamped on ingest); for an `Adopted` row, always `1.0` so summed weight across recordings equals a plain count |
| `Ordinal` | int | 0-based, the LLM's weight-descending order at extraction time |
| `Status` | `RecordingTagStatus` (int) | `Suggested = 0`, `Adopted = 1`, `Dismissed = 2`. Append-only ints, same rule as `RecordingStatus` / `Source` |
| `AdoptedAt` | timestamptz null | when the tag became the user's (typed, or a suggestion promoted); null for suggestions and dismissals. Orders the chips in the tag popover, since `CreatedAt` would shuffle hand-typed and promoted tags by whenever extraction happened to run |
| `CreatedAt` | timestamptz | |

Indexes: `(RecordingId, Ordinal)`, and a **Postgres-only** unique index `IX_RecordingTags_RecordingId_TagLower`
on `(RecordingId, lower("Tag"))` - one tag per recording regardless of case. Created via raw SQL in the
`AddRecordingTagStatus` migration (not an EF `HasIndex`, so it can express the lowercase expression), which
first deletes any legacy case-variant duplicates (keeping the lowest `Ordinal`) so the index can be created
at all.

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

#### `MeetingScreenshots`
A screen capture taken during a recording from the **desktop client** (Windows only, for now). Two blobs
per row: the full PNG and a small JPEG thumbnail. `CapturedAtMs` is the offset into the *recorded* (pause-
aware) clock stamped by the recorder - an immutable capture fact, so unlike `MeetingNote.CapturedAtMs` it is
non-nullable here.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers | owner (the recording's owner at capture time); **cascade** |
| `RecordingId` | uuid FK → Recordings | **cascade** |
| `CapturedAtMs` | bigint | offset into the recording clock (pause-aware); not user-editable |
| `BlobKey` | varchar(512) | object-storage key of the full PNG |
| `ThumbBlobKey` | varchar(512) | object-storage key of the JPEG thumbnail |
| `Width` / `Height` | int | pixel dimensions of the full image (long edge capped at 2560) |
| `SizeBytes` | bigint | full plus thumbnail bytes combined; counts toward the owner's storage quota |
| `Ordinal` | int | 0-based sort order within the recording |
| `OcrText` | text null | text an OCR model read off the capture; null until OCR has been run. **Machine-extracted and unverified** - see the note below |
| `OcrModel` | text null | the model that produced `OcrText`. Stored rather than derived, because the routed OCR model changes over time and the provenance line must name the one that actually ran |
| `OcrGeneratedAt` | timestamptz null | when `OcrText` was produced; null exactly when no OCR has run |
| `CreatedAt` | timestamptz | |

Indexes: `(RecordingId, CapturedAtMs)` (transcript-weave lookups and list ordering), `UserId`. CRUD +
streaming at `/api/recordings/{id}/screenshots` (`GET`/`POST`, `GET {id}/content`, `GET {id}/thumb`,
`DELETE {id}`); the content/thumb routes accept the bearer as an `access_token` query parameter so an
`<img>` tag can load them directly. Deleting a recording deletes both blobs of every screenshot explicitly
(the cascade above only removes the rows); merging a recording away does the same rather than reassigning
its screenshots to the survivor (unlike attachments), since a capture's clock offset has no meaning once its
source recording's audio has been spliced into a different timeline.

**OCR.** `POST {id}/ocr` (optionally `?force=true`) reads the text off a capture using the model routed to the
`Ocr` call group, and caches it in the three `Ocr*` columns so the second read is free. The cache is the only
reason those columns exist on the row rather than being computed per request: the feature offers two
destinations for one extraction (the chat context and a Markdown attachment), and paying for a second model
call to send the same text somewhere else would be indefensible. An empty model response is **never** written
over a stored result - a model that cannot see images returns nothing, and letting that erase a good
extraction would lose real work to a misconfiguration - so the endpoint answers 422 instead.

Treat `OcrText` as **machine-extracted and unverified** wherever it is surfaced, and always alongside
`OcrModel`. Four OCR models measured against one dense desktop capture each produced silent errors: a
reproducible glyph misread (`DSP` read as `OSP`), whole tables dropped, and at one image size an entirely
invented column of plausible scores. The columns are additive and nullable, so an older backup still restores
and no `MaintenanceController.CurrentFormat` bump was needed.

#### `Formulas`
A saved **template** + a chosen context, run over a recording to produce a Markdown `FormulaResult`. `Scope`
determines visibility/ownership: `Personal` (owned by one user, `OwnerUserId` set), `Platform` (shared,
admin-managed, no owner), or `Diariz` (seeded, `IsBuiltIn = true`, cannot be deleted).

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `Scope` | int | `Personal`=0, `Platform`=1, `Diariz`=2 (append-only) |
| `OwnerUserId` | uuid FK → AspNetUsers, null | set only for `Personal`; **`ON DELETE CASCADE`** (a user's personal formulas die with the account); null for Platform/Diariz |
| `Name` | varchar(256) | |
| `Description` | varchar(1024) null | |
| `ContentJson` | **jsonb** | the structured template (sections/blocks - the same `TemplateContent` shape `MeetingTypes.ContentJson` uses). A formula that is just a prompt is stored as **one headless (`level: 0`) section holding one prompt block**, which composes to exactly that prompt's output - no heading added. Postgres jsonb; plain text under the in-memory provider |
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
`FormulaRunJob(RecordingId?, SectionId?, ResultId, FormulaId, UserId)` on the `formula-run-jobs` Redis stream
(consumer group `formula-runners`), and returns 202; the in-process `FormulaRunWorker` runs the LLM and flips
the row to `Ready`/`Failed` (SignalR `FormulaResultStatusChanged`, plus the client polls). The MCP/chat
`run_formula` tool stays synchronous.

#### `SectionFormulaResults`
The Markdown document produced by running a `Formula` over a **folder (section) and its sub-sections**. Many
per section (one per run). Mirrors `FormulaResults` but section-scoped; the run is a **map-reduce** (the
formula runs on each included recording, then over the combined per-meeting outputs). The same async job
pipeline is used - the `FormulaRunJob` carries `SectionId` (with `RecordingId` null) and the worker flips this
row.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `SectionId` | uuid FK → Sections | **cascade** — deleting a folder removes its formula results |
| `CreatedByUserId` | uuid FK → AspNetUsers, null | **`ON DELETE SET NULL`** |
| `FormulaId` | uuid FK → Formulas, null | **`ON DELETE SET NULL`** |
| `Name` | varchar(256) | formula name snapshot |
| `Text` | text | generated Markdown body (empty until the run completes) |
| `Ordinal` | int | 0-based order within the folder |
| `Status` | int enum | `Generating = 0`, `Ready = 1`, `Failed = 2` |
| `Error` | text, null | failure message when `Status = Failed` |
| `CreatedAt` / `UpdatedAt` | timestamptz | |

Indexes: `(SectionId, Ordinal)`, `FormulaId`, `CreatedByUserId`. Run access = room membership; edit/delete a
result = its creator or a member with `ManageContents`.

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
| `UploadedByUserId` | uuid | plain column, no FK (mirrors `Sections.RoomId`'s "not yet" pattern); who to charge for `SizeBytes` — the caller who created this row, which can differ from the folder's creator (`Section.UserId`) in a shared room. Indexed |
| `Kind` | int | `File`=0, `Url`=1 (reuses `AttachmentKind`) |
| `Name` | varchar(512) | display name / link text |
| `BlobKey` | text null | object-storage key (File kind) |
| `ContentType` | text null | MIME of the uploaded file |
| `SizeBytes` | bigint | file size — counts toward the quota (0 for a URL) |
| `Url` | text null | the linked address (Url kind) |
| `Ordinal` | int | 0-based order within the folder |
| `CreatedAt` | timestamptz | |

Indexes: `(SectionId, Ordinal)`, `UploadedByUserId`. Blobs live under MinIO key
`{uploaderUserId}/section-attachments/{attachmentId}{ext}`. Counts toward the **uploader's** storage quota
(`StorageUsage` sums recording + section-attachment bytes by `UploadedByUserId`, not by the folder's creator).
CRUD + in-place Markdown edit live in `SectionAttachmentsController` at route
`api/sections/{id}/folder-attachments`.

#### `RecordingCalendarLinks`
The calendar event a recording belongs to (1:1 with `Recording`, shared primary key) - Google, an `.ics` feed,
or the mirrored Outlook calendar. A lightweight **snapshot** for cheap list/Calendar-tab rendering; the rich
invite details (attendees, description, location, organiser) are fetched live by `EventId`, never stored.

| Column | Type | Notes |
|---|---|---|
| `RecordingId` | uuid PK / FK → Recordings | shared PK; **cascade** delete with the recording |
| `EventId` | varchar(1024) | calendar event id (Google id, `.ics` UID, or `outlook:{OutlookCalendarEvents.Id}`) |
| `CalendarId` | varchar(1024) | which calendar the event is on (`primary` or a secondary/shared/subscribed id); existing rows backfilled to `primary` |
| `Color` | varchar(32) null | the calendar's Google background colour (hex) snapshot, for tinting the linked icon |
| `Summary` | varchar(1024) null | event title snapshot |
| `StartsAt` / `EndsAt` | timestamptz | event span snapshot |
| `HtmlLink` | varchar(2048) null | Google Calendar deep link |
| `LinkedManually` | bool | user picked it by hand (vs. auto-saved best time-overlap match) |
| `SyncedAt` | timestamptz | when the snapshot was last written |
| `SeriesId` | varchar(1024) null | the recurring series this event belongs to, or null for a one-off event. **Stored, not derived**: for Google/`.ics` it is the master event id (the shared prefix of an expanded occurrence's id); for Outlook it is the mirrored event's `Uid` prefix, but the Outlook mirror is a **rolling window** (see `OutlookCalendarEvents` below) that only ever holds the current sync range - deriving the series by joining to it would lose exactly the older-meeting history this column exists to show. Backfilled by `AddCalendarSeriesId` for existing links; written on every new link thereafter |

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

#### `OutlookCalendarSources`
One machine's connection to a **classic desktop Outlook** calendar. Keyed **per (user, device)**, not per user:
two PCs against two mailboxes are independent mirrors, or each machine's orphan sweep would delete the other's
events on every launch. Unlike Google and `.ics` - both fetched live and never stored - Outlook is only
reachable from the user's own PC, so its events are **pushed** by the desktop app and persisted
(`OutlookCalendarEvents`), which is what lets them keep working in a browser and after the app is closed.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers | indexed; **cascade** delete with the user |
| `DeviceId` | varchar(64) | opaque per-installation id minted by the desktop app; **unique with `UserId`** so a repeat push updates its own source rather than creating another |
| `DeviceName` | varchar(128) null | hostname, for telling two devices apart. Display only |
| `MailboxName` | varchar(256) null | the Outlook default account's address. Display only; never logged |
| `DisplayName` | varchar(128) | user-editable label, defaulting to "Outlook ({DeviceName})" |
| `Color` | varchar(32) null | hex colour used to tint this device's events |
| `Enabled` | bool | off = kept but excluded from reads (as `IcsCalendarSources.Enabled`) |
| `PastDays` / `FutureDays` | int | rolling read window (defaults 30 / 180; clamped 0-365 and 1-730). The sweep is bounded by the window a run covered, so narrowing it never deletes history outside it |
| `SkipPrivate` | bool | default **true**; private/confidential appointments are dropped **on the machine**, so they never leave it |
| `IncludeBody` | bool | default true; a private appointment's body is stripped regardless |
| `CreatedAt` | timestamptz | |
| `LastSyncedAt` | timestamptz null | last completed **full** push; also gates the 60s per-device run cooldown |
| `LastNarrowSyncedAt` | timestamptz null | last completed **narrow** push (a window of <= 2 days - the desktop's "Sync today"); gates that run's own 10s cooldown. Kept apart from `LastSyncedAt` so the two cannot block each other: a quick sync is what a user reaches for seconds after a full one ran, and one shared stamp would refuse it exactly then. Preferences shows the later of the two |
| `LastError` | varchar(512) null | last sync failure (Outlook not installed, the new Outlook, blocked COM), surfaced in Preferences from any device |
| `LastEventCount` | int | events held after the last completed run |

#### `OutlookCalendarEvents`
One **flattened occurrence** of a desktop Outlook appointment. Outlook expands recurring series itself, so there
is no master row and no recurrence rule. Cancelled appointments are never stored - the desktop stops reporting
them and the sweep removes any existing copy.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | **deterministic**: first 16 bytes of `SHA-256(SourceId ‖ Uid)` with RFC-4122 bits set (`OutlookEventId.For`). Public ids are `outlook:{Id}` = **44 chars**, which is what keeps them inside `MeetingNotes.EventId`/`CalendarId` (varchar 256) - `CalendarEventNotesController` clamps writes at 256 but reads raw, so a longer id would save a note under a truncated key and never read it back. Deterministic rather than random so an occurrence that leaves the window and returns keeps its links |
| `SourceId` | uuid FK → OutlookCalendarSources | **cascade**; indexed with `StartsAt` |
| `UserId` | uuid FK → AspNetUsers | **cascade**; denormalised for the hot window read, indexed with `StartsAt`. Double cascade path as `MeetingNotes` / `MeetingScreenshots` |
| `Uid` | varchar(512) | Outlook `GlobalAppointmentID` (the per-occurrence Global Object ID, = EWS `calendar:UID`), or `entry:{sha1(EntryID)}` for local items without one. **Unique with `SourceId`**. Chosen over `EntryID` (unstable across moves/stores) and `CleanGlobalObjectId` (identical for a whole series) |
| `Subject` | varchar(512) null | |
| `StartsAt` / `EndsAt` | timestamptz | always populated, including for all-day (local midnight as UTC), so the window query, ordering and sweep have one sortable column |
| `AllDay` | bool | date-only entry; never auto-matched to a recording |
| `StartDate` / `EndDate` | varchar(10) null | for all-day, the **local** `yyyy-MM-dd` dates - the display truth, stored verbatim and never re-derived from the UTC instant (that is the classic off-by-one: an all-day 2026-03-15 in Europe/London is `2026-03-14T23:00:00Z`). `EndDate` is the exclusive next day, as Google and iCalendar |
| `TimeZoneId` | varchar(128) null | IANA, converted server-side via `TimeZoneInfo.TryConvertWindowsIdToIanaId`; falls back to the device's zone rather than being left null |
| `WindowsTimeZoneId` | varchar(128) null | the raw Windows id, kept for diagnosing an unmapped zone |
| `Location` | varchar(1024) null | |
| `OnlineMeetingUrl` | varchar(2048) null | Teams/Zoom join link; doubles as the event's clickable target, since a local appointment has no web permalink |
| `BodyText` | text null | plain text only - HTML is never transmitted - capped at 8000 chars on write. Null when `IncludeBody` is off or the item is private |
| `Categories` | varchar(512) null | comma-joined as Outlook gives them |
| `Sensitivity` | int | 0 Normal, 1 Personal, 2 Private, 3 Confidential - **append only** |
| `BusyStatus` | int | 0 Free, 1 Tentative, 2 Busy, 3 OOF, 4 Working Elsewhere - **append only** |
| `IsRecurring` | bool | informational; occurrences are stored flat either way |
| `OrganizerName` / `OrganizerEmail` | varchar(256) null | |
| `AttendeesJson` | **jsonb** (Npgsql only) | `[{name,email,response,optional}]`; `response` uses Google's vocabulary (accepted/declined/tentative/needsAction) so the shared `CalendarAttendee` projection needs no translation |
| `SourceLastModified` | timestamptz | Outlook's `LastModificationTime` - the change-detection fingerprint (no content hashing) |
| `SyncId` | uuid | stamped by every upsert; the sweep deletes in-window rows carrying a stale one. Scoping is **structural** - this table holds only rows the sync created for this source - so there is nothing else it could hit |
| `SyncedAt` | timestamptz | |

#### `MeetingTypes`
Reusable minutes templates. A type is **presentation + selection**: it carries no prompts of its own, and names the **formula** whose template generates the minutes (`PrimaryFormulaId`) plus any run alongside it (`MeetingTypeFormulas`). A **Platform** type (`UserId` null) is created by a Platform Administrator and is
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
| `PrimaryFormulaId` | uuid FK → Formulas, null | the formula whose template generates the minutes. **`ON DELETE RESTRICT`** - a formula in use as a primary can't be deleted or disabled out from under its templates (SET NULL would silently degrade everyone's minutes). Null = fall back to the seeded General type's formula |
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
| `ProfileId` | uuid FK → SpeakerProfiles null | = CLR `PersonId`; the identified person; **SetNull** on person delete |
| `IdentifiedAuto` | bool | true when name/profile were set by auto-ID (vs a manual rename) |
| `IsMultiSpeaker` | bool | user marked this slot as overlapping speech ("Multiple Speakers"); never auto-identified or enrolled into a voiceprint |

Unique index: `(RecordingId, Label)`.

#### `SpeakerProfiles` — the people directory (CLR type `Person`)

> **The table name does not match the CLR type, deliberately.** The entity is `Person` (and
> `ProfileContribution` is `VoiceSample`), but the tables keep their original names because renaming them
> is a destructive rename: it would force a `MaintenanceController.CurrentFormat` bump, which hard-rejects
> every backup archive taken before that point with no conversion path. The mapping is pinned with
> `ToTable`/`HasColumnName` in `DiarizDbContext.OnModelCreating`.

Someone who appears in meetings. The **voiceprint is optional** — a person added by hand, or one who has
opted out, has a null `Embedding`. Biometric data — GDPR-erasable.

**Two columns mean different things and are easy to confuse in raw SQL** (both carry a `COMMENT ON COLUMN`
so `\d+` shows it):

| C# property | Column | Meaning |
|---|---|---|
| `CreatedByUserId` | `"UserId"` | who **enrolled** this person. Provenance only, never filtered on |
| `LinkedUserId` | `"LinkedUserId"` | which account this person **is** |
| `Speaker.PersonId` | `"ProfileId"` | (on `Speakers`) the identified person |

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers **null** | = `CreatedByUserId`; who enrolled them. Cascade. Nullable so the backfill can provision a person nobody enrolled. **Provenance only - nothing filters on it** since the directory went platform-wide |
| `RoomId` | uuid **null** | the room they were first enrolled from. **Provenance only**, no FK; still written on create, never read |
| `LinkedUserId` | uuid FK → AspNetUsers null | the account this person is. Cascade — deleting the account removes the directory entry, rather than orphaning a row that still holds their name and email |
| `Name` | varchar(256) | for a linked person this follows the account (`IPeopleDirectory.SyncFromUserAsync`) |
| `Title` | varchar(128) null | |
| `CompanyName` | varchar(256) null | |
| `Email` | varchar(256) null | for a linked person this follows the account |
| `Phone` | varchar(64) null | |
| `IsInternal` | bool | colleague vs external party; drives routing decisions downstream |
| `VoiceprintOptOut` | bool | they asked not to be voice-printed; excluded from automatic identification |
| `Embedding` | **vector(192) null** | centroid = L2-normalised mean of voice-sample snapshots; **null when they have no voiceprint**. Postgres-only |
| `SampleCount` | int | number of voice samples averaged in; kept in step by `RecomputeVoiceprintAsync` |
| `CreatedAt` / `UpdatedAt` | timestamptz | |

Indexes: `(UserId)`, `(Email)`, and a **filtered unique** `(LinkedUserId) WHERE "LinkedUserId" IS NOT NULL` —
one account is one person, while the many people with no account do not collide on null. Children:
`ProfileContributions` (cascade). Matching against new `Speaker.Embedding`s is a **pgvector cosine distance**
query (`Embedding.CosineDistance(vec)`), restricted to people who have an embedding and have not opted out,
and accepted when `≤ Identification:Threshold`.

#### `ProfileContributions` — voice samples (CLR type `VoiceSample`)
Training provenance for a person's voiceprint (which recording-speakers feed the centroid). Table name kept
for the same backup-compatibility reason as `SpeakerProfiles` above.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `ProfileId` | uuid FK → SpeakerProfiles | = CLR `PersonId`; cascade |
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
| `ParentId` | uuid FK → Sections null | null = top-level; non-null = a sub-section, nesting up to 8 levels. **Cascade** on parent delete |
| `Position` | int | manual sort order among siblings (drag-to-reorder; replaces alphabetical) |
| `CreatedAt` | timestamptz | |

Index: `(UserId, Name)`, `(ParentId)`. Sections nest up to **8 levels deep** (`SectionTree.MaxDepth`, enforced
in `SectionsController`: create checks the parent's depth; reparent rejects a folder becoming its own parent,
rejects a move into the folder's own descendant, and checks the target's depth plus the moved branch's height).
Deleting a section **Cascade**-deletes
its whole subtree - Postgres cascades the self-referencing FK recursively - and **SetNull**s the recordings of
every folder in it (ungroups, not deletes). No migration was needed for the deeper tree: the self-referencing
`ParentId` already supported it, so older backups remain restorable.

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
| `ContextJson` | **jsonb** | `{ recordingIds, attachmentName?, attachmentText?, includeAttachments?, searchAllMeetings?, sectionId?, modelId?, screenshots? }`. `screenshots` is `[{ recordingId, screenshotId }]` - the captures attached to the conversation (0.238.0). Additive keys in an existing blob, so no DDL and no migration: an older row reads a missing key as null |
| `CreatedAt` / `UpdatedAt` | timestamptz | |

Index: `(UserId, UpdatedAt)`.

#### `UserSettings`
Per-user preferences (1:1 with the user via a **shared primary key** = `UserId`).

| Column | Type | Notes |
|---|---|---|
| `UserId` | uuid PK + FK → AspNetUsers | cascade |
| `ChatToolsEnabled` | bool null | chat tool-calling master override; null → server `Chat:ToolsEnabled` |
| `ChatToolOverridesJson` | jsonb null | explicit per-tool on/off map `{ "tool_name": bool }`; a tool absent follows the server default |
| `NativeLanguage` | text null | the user's native language (BCP-47); default target when translating transcripts |
| `UiLanguage` | text null | the language the app UI is shown in (BCP-47); null → follow the browser |
| `TranscriptionLanguage` | text null | the default spoken language for this user's recordings (BCP-47); null → let Whisper detect it per recording. Deliberately **not** `NativeLanguage`: that is the translation target, and people record in languages that are not their own. A recording's own `TranscriptionLanguage` overrides it |
| `GoogleRefreshTokenEncrypted` | text null | Google OAuth refresh token (offline Calendar access), **encrypted at rest** (Data Protection); never returned to clients |
| `GoogleCalendarGranted` | bool | user granted Google Calendar read access |
| `OutlookSyncEnabled` | bool | opt-in to mirroring a desktop Outlook calendar; **default false**. Gates storing meeting bodies and attendee addresses server-side, so an installed desktop app changes nothing until it is set. Deliberately separate from the per-device `OutlookCalendarSources.Enabled` plumbing flag; turning it off purges every source and, by cascade, every stored event |
| `GoogleSelectedCalendarIdsJson` | jsonb null | JSON array of the Google calendar ids to consider for attribution + the overlay; null → not chosen (fall back to the Google-visible calendars + primary) |
| `JobTitle` / `CompanyName` / `LinkedIn` | varchar(256) null | free-text profile fields |
| `JobDescription` / `CompanyDescription` | varchar(2048) null | free-text profile fields |
| `Theme` | int | UI colour theme (`ThemePreference`): `0` = Auto (default), `1` = Light, `2` = Dark. Append-only enum |
| `RecordingPlacementMode` | int | where a new recording is filed in the user's personal room (`RecordingPlacementMode`): `0` = Ungrouped, `1` = SelectedFolder (default - the folder they had open), `2` = SpecificFolder. Append-only enum |
| `RecordingPlacementSectionId` | uuid null | the fixed folder for `SpecificFolder` mode; null in the other modes |
| `CalendarAutoStopEnabled` | bool | whether a recording started from a **calendar event** ends by itself; **default false**. The two columns below are its conditions and are inert while it is false |
| `CalendarAutoStopAfterMinutes` | int | minutes to keep recording past the invite's end time; **default 3**. Clamped to the default on write if non-positive (a zero would stop the take the instant it began) |
| `CalendarSilenceStopSeconds` | int | seconds of continuous near-silence that also ends such a recording; **default 30**. Clamped like the above. Only counted once something has been heard, so a take started before anyone speaks is never cut short |
| `ChatModelId` | uuid null | FK -> `LlmModels` **`ON DELETE SET NULL`** - the model this user last chose in the chat picker; null follows the platform's chat routing. `SET NULL` deliberately, unlike the `RESTRICT` on `LlmCallAssignments` and `PlatformSettings.DefaultLlmModelId`: those mean "the platform is using this model, refuse the delete", while a user's pick is a preference that must never block an administrator. `LlmModelsController.Delete` therefore does **not** check this column. Un-ticking a model's `ChatEnabled` does not clear it either - it is left pointing at the model and ignored while not offered, so re-ticking restores everyone's choice |
| `AutoMergeSpeakerSegments` | bool | whether consecutive same-speaker segments are collapsed into single blocks automatically after transcription and speaker identification, the same collapse `POST /api/recordings/{id}/merge-segments` performs on demand; **default false**. Permanent for that transcription version |
| `LlmTestRecordingId` | uuid null | the recording this administrator tests AI models against in the model editor's test rail (`GET`/`PUT /api/admin/llm-models/test-recording`); null = never chosen. **No foreign key** - see the migration note above. Per user, not per platform, because the test runs against the caller's own recordings only |

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
| `McpAccessEnabled` | bool | master switch for the `/mcp` server and personal `dz_mcp_` tokens; default **true** (seeded true in its migration so shipping this toggle never disables an already-connected MCP client) |
| `WebhooksEnabled` | bool | master switch for outbound webhooks / user Automations; default false = off (enforced starting with the Phase 2 webhooks core) |
| `DefaultLlmModelId` | uuid null | FK -> `LlmModels` **`ON DELETE RESTRICT`** - the model used by any call group with no explicit assignment. Null falls through to the model synthesized from `Summarization:ApiBase`, so an upgrade with no rows keeps working unchanged |
| `LlmTimeoutSeconds` | int | The platform-wide request timeout, applied to **every** LLM call. Declared obsolete in 0.221.0 (the timeout became a per-model parameter) and read only on the environment-fallback path, which made it inert as soon as a deployment configured a model while the Settings control went on promising platform-wide behaviour - so 0.235.1 made it authoritative again. A **floor, not an override**: `LlmPlatformLayers` puts it below a model own layers, so per-model tuning still wins, and it stays silent at its default so `LlmDefaults__TimeoutSeconds` is not outranked by a row that merely exists |
| `LlmUsageLoggingEnabled` | bool | master switch for the LLM usage log; default **true** (the log is the feature). Enforced by `LlmUsageWriter`, not the capture handler, so the LLM call path never pays for a settings lookup |
| `LlmUsageRetentionDays` | int | usage log rows older than this many days are deleted by the nightly `LlmUsageRetentionWorker` sweep; default 90. `0` = keep forever |
| `LlmStreamUsageEnabled` | bool | whether a streaming request asks the model for token counts via `stream_options.include_usage`; default true. A toggle rather than a constant so an endpoint that rejects the field is recoverable without a redeploy |

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
| `Scope` | int | `ApiTokenScope`: `0` = ReadOnly, `1` = ReadWrite (default, so pre-existing tokens keep full access). Set only at creation; a ReadOnly token gets 403 on any unsafe verb (POST/PUT/PATCH/DELETE). Append-only enum |
| `ExpiresAt` | timestamptz null | optional hard expiry, set only at creation; null = never expires (all pre-existing tokens) |

Indexes: unique `(TokenHash)`, `(UserId)`.

#### `Webhooks`
A webhook subscription ("Automation" in the UI), backing the `WebhookSubscription` entity - the physical table is
named `Webhooks` (the `DbSet` name), not `WebhookSubscriptions`. Gated end-to-end by
`PlatformSettings.WebhooksEnabled`. Two scopes: **Personal** (Phase 2 - owned by and fires only for its creator)
and **Platform** (Phase 3 - owned by the admin who created it, but routes by Workflow Signal across every user;
see `WorkflowSignals` below).

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `OwnerUserId` | uuid FK → AspNetUsers | owner; **cascade** on user delete (a Platform subscription cascades with the admin who created it - a documented follow-up is to detach it from a single owner) |
| `Scope` | int | `WebhookScope`: `0` = Personal, `1` = Platform |
| `Name` | varchar(200) | user-chosen label |
| `Url` | varchar(2048) | delivery target; SSRF-validated (`IWebhookUrlValidator`) on every create/update |
| `SecretEncrypted` | text | the HMAC signing secret (`dz_whsec_…`), encrypted at rest via Data Protection; shown to the user once, at creation |
| `EventTypes` | text | comma-separated `WebhookEventTypes` keys this subscription wants |
| `SignalFilter` | varchar(1024) null | comma-separated `WorkflowSignals.Key` values this subscription routes on. Personal subscriptions may use it to narrow (empty = no narrowing, matches any signal on a subscribed event); a **Platform** subscription requires a non-empty filter - empty deliberately matches nothing, both at create/update time (`PlatformWebhooksController.Validate`) and at publish time (`WebhookPublisher`/`WebhookSignals.Intersects`), so a half-configured platform automation can't silently fire on everything |
| `IsActive` | bool | default true; flipped false by auto-disable or by the user |
| `IncludeAttendeeContacts` | bool | default **false**; opt-in to include attendees' email addresses and phone numbers in the payload. An automation posts to an arbitrary URL, so this is per-subscription rather than global |
| `IncludeFeedbackText` | bool | default **false**; opt-in, **Platform subscriptions only**, to include the submitter's own words in a `feedback.submitted` payload. Same reasoning as `IncludeAttendeeContacts` - the payload otherwise carries only ids and context, and an automation that needs the words fetches them through the API |
| `ConsecutiveFailures` | int | consecutive failed deliveries; reset to 0 on any success |
| `DisabledReason` | text null | set when auto-disabled, so the UI can explain why |
| `LastDeliveryAt` | timestamptz null | |
| `LastStatus` | text null | `"Delivered"` or the last error string |
| `CreatedAt` | timestamptz | |

Indexes: `(OwnerUserId)`.

#### `WebhookDeliveries`
One queued or sent event for one `Webhooks` subscription - doubles as the retry queue (`WebhookDeliveryWorker`
polls it) and the durable delivery-history log shown in the UI.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `SubscriptionId` | uuid FK → Webhooks | **cascade** on subscription delete |
| `EventId` | varchar(64) | the stable `evt_…` idempotency key (the `webhook-id` header); constant across retries of this delivery |
| `EventType` | varchar(64) | a `WebhookEventTypes` key, or `webhook.ping` for a manual test |
| `PayloadJson` | **text, not jsonb** | the exact signed request body; never re-serialized after creation, since the HMAC signature is computed over these literal bytes |
| `Status` | int | `WebhookDeliveryStatus`: `Pending` / `Delivered` / `Failed` |
| `AttemptCount` | int | incremented on every delivery attempt; capped by `WebhookBackoff.MaxAttempts` (8) |
| `NextAttemptAt` | timestamptz | earliest time the worker may attempt this delivery; the poll key |
| `ResponseStatus` | int null | HTTP status of the last attempt, if any response was received |
| `LastError` | text null | last error message (non-2xx status or exception), if any |
| `LastAttemptAt` | timestamptz null | when the worker last actually contacted the target for this delivery; null until first attempt. Drives the per-subscription rolling-minute rate cap (`WebhookOptions.MaxPerSubscriptionPerMinute`) |
| `CreatedAt` | timestamptz | |

Indexes: composite `(Status, NextAttemptAt)` (the delivery worker's due-poll query), `(SubscriptionId)`.

#### `WorkflowSignals`
The admin-defined named vocabulary a formula author picks from ("Send to Slack") and a Platform Administrator
wires a `Webhooks` (Platform-scope) subscription's `SignalFilter` to. Managed via `api/workflow-signals` (list is
open to any authenticated user, for the formula-editor picker; create/update/delete require `ManagePlatform`).

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `Key` | varchar(64) | stable machine-facing routing slug (e.g. `post-to-slack`); **unique index**; immutable after creation - the admin edit endpoint updates `Label`/`Description`/`IsActive` only |
| `Label` | varchar(200) | friendly, author-facing name shown in the formula editor's signal picker |
| `Description` | text null | |
| `IsActive` | bool | default true; an inactive signal is hidden from the picker but existing `FormulaWorkflowSignals` links and `Webhooks.SignalFilter` entries referencing its `Key` are kept as-is |
| `CreatedAt` | timestamptz | |

Indexes: unique `(Key)`. Deleting a signal cascades to its `FormulaWorkflowSignals` rows (it does not touch any
`Webhooks.SignalFilter` text, which is why an inactive/deleted signal's key can linger harmlessly in a filter).

#### `FormulaWorkflowSignals`
Join table: which admin-defined Workflow Signals a formula carries. When that formula's run completes or fails,
`FormulaRunProcessor` loads the formula's **active** signal keys and passes them to `IWebhookPublisher.PublishAsync`,
which matches them against every Platform subscription's `SignalFilter` and delivers the formula's output inline
to each match (a Personal subscriber on the same event never receives the output, only the thin event body).

| Column | Type | Notes |
|---|---|---|
| `FormulaId` | uuid | PK part 1. FK → `Formulas`, **cascade** |
| `WorkflowSignalId` | uuid | PK part 2. FK → `WorkflowSignals`, **cascade**; index `IX_FormulaWorkflowSignals_WorkflowSignalId` |

#### `Feedback`
A user's "something looks or behaves wrong" report, captured with the client-side technical trail leading up
to it (`apps/web/src/lib/trail.ts` - recent API calls and route changes, scrubbed browser-side before it is
ever sent). Distinct from the optional GlitchTip error tracker: nothing threw, so the exception path never
saw it, and it works even on a deployment with no error tracker configured. Submitted by any signed-in user
via `POST /api/feedback`; reading (`GET /api/feedback`) and deleting (`DELETE /api/feedback/{id}`) are
`ManagePlatform`-gated - a Platform Administrator only, deliberately including a user's own submissions,
since a per-user view would imply a support conversation this feature does not have.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `UserId` | uuid FK → AspNetUsers | who submitted it; **cascade** on user delete - user-authored content, disappears with the account like everything else they own |
| `CreatedAt` | timestamptz | |
| `Description` | text | the user's own words; free text, so it may quote meeting content - which is why it lives here, under the same retention, backup and deletion rules as the rest of their data. Trimmed server-side, rejected if empty, truncated to `FeedbackController.MaxDescription` (4000 chars) if very long |
| `Route` | text | the SPA route at submission |
| `Release` | text | the app version the browser was running |
| `TrailJson` | text | the client trail, already scrubbed browser-side, stored verbatim as a JSON array |
| `ScreenshotBlobKey` | text null | reserved for a deferred screenshot phase (needs an Electron shell change, and so a desktop release). Always **null** today - added now so that phase needs no further migration |

Indexes: `(UserId)`. Submission also raises a `feedback.submitted` webhook event (Platform subscriptions only -
see `Webhooks.IncludeFeedbackText` above) carrying `{ id, route, release, hasScreenshot: false }`, plus
`description` only for a subscription that has opted in.

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
| `Permissions` | `int` NOT NULL | `[Flags] PlatformPermission`: `ManageRooms = 1`, `ManageUsers = 2`, `ManagePlatform = 4`, `ManageFormulas = 8`, `ManagePeople = 16`. **Append-only** |
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

#### `LlmCalls.FinishReason`

| Column | Type | Notes |
|---|---|---|
| `FinishReason` | text null | the response's `finish_reason` - `stop`, `length`, `tool_calls`, `content_filter` - or null when the server reported none (or the call never got a response). Stored as the raw string rather than a boolean: it costs the same and the other values are worth having. Read from `choices[].finish_reason` on a buffered body, and from the SSE chunks by `SseUsageScanner` on a streamed one |

`length` is the one that matters. A reply cut off by a token cap is otherwise **invisible**: a 200, no
error, and empty content because reasoning consumed the whole budget before an answer was written. The API
derives `Truncated` from it (case-insensitively) rather than storing a second column, so the two can never
disagree, and rolls it up per operation as "any call was cut off".

#### `LlmModels`

Every model the platform can call. Self-contained: pointing a call group at a model brings its connection
with it, which is what lets a local LM Studio model and a cloud model coexist on one platform.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid | PK |
| `Name` | text | sent verbatim as `model` in each request, e.g. `openai/gpt-oss-20b`. **Unique** |
| `DisplayName` | varchar(128) null | the user-facing name, e.g. `QWEN 3.8`. Null or blank means "use `Name`" - the entity's computed `Label` does that fallback, so a slug rename cannot strand a stale label. Never a stored copy of `Name` |
| `Description` | varchar(200) null | a short phrase shown beside the name in the chat model picker, e.g. "Use this for most chats". Null means the model has none - never a generated one, since a sentence nobody wrote would read as advice the platform is giving |
| `ChatEnabled` | bool | whether this model appears in the chat model picker; **default false**. It does **not** affect routing: `LlmCallAssignments[Chat]` still decides which model answers when the user has chosen nothing, and that model is offered implicitly whatever this column says |
| `ApiBase` | text | OpenAI-compatible endpoint, e.g. `http://localhost:1234/v1` |
| `ApiKeyEncrypted` | text null | encrypted via `IApiKeyProtector` (same Data Protection keyring as the old per-user key); never returned to clients. Null = no key needed, normal for a local endpoint |
| `ContextLength` | int | the model's context window in tokens - a fact about the model, which is why it lives here rather than in per-user settings where it used to be |
| `CreatedAt` / `UpdatedAt` | timestamptz | |

Indexes: unique on `Name`.

#### `LlmModelParameters`

One parameter layer per (model, group). `Group = ModelBase` (0) holds the model's own defaults; the other
members hold that call group's overrides.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid | PK |
| `LlmModelId` | uuid | FK -> `LlmModels` **`ON DELETE CASCADE`** - a model's parameters are meaningless without it |
| `Group` | int | `LlmCallGroup` (see below) |
| `ParametersJson` | **`jsonb`** | the layer. Three states per key, and the last two differ: **absent** = inherit from the next layer down, **present and null** = omit the parameter from the request entirely, **present with a value** = use it. A sentinel could not express both - `-1` is legal for `max_tokens` (unlimited) and `top_k` (disabled) on some servers |

Indexes: **unique on `(LlmModelId, Group)`**. `Group` is non-nullable with `ModelBase = 0` precisely because
Postgres treats NULLs as distinct in a unique index, so a nullable "this is the base" marker would let two
base rows through.

#### `LlmCallAssignments`

Which model serves which call group. At most six rows; a group with no row falls back to
`PlatformSettings.DefaultLlmModelId`, and then to the environment endpoint.

| Column | Type | Notes |
|---|---|---|
| `Group` | int | **PK** - `LlmCallGroup`, never `ModelBase` (the API rejects it: it is a parameter scope, not a call type) |
| `LlmModelId` | uuid | FK -> `LlmModels` **`ON DELETE RESTRICT`** |

**The RESTRICT is a backstop, not the guard.** EF's change tracking gets there first and behaves differently
per FK: `LlmCallAssignment.LlmModelId` is required, so `DbSet.Remove` throws client-side before any statement
is sent; `PlatformSettings.DefaultLlmModelId` is **nullable**, so with that row tracked EF issues
`UPDATE PlatformSettings SET DefaultLlmModelId = NULL` ahead of the DELETE and the constraint never fires -
the model really is deleted. `LlmModelsController.Delete` therefore checks both itself and returns 409.

#### `LlmCallGroup`

| Value | Member | Covers (`LlmCallKind`) |
|---|---|---|
| 0 | `ModelBase` | not a call type - the model's own default parameters |
| 1 | `Tags` | `Tags` |
| 2 | `Actions` | `ExtractActions` |
| 3 | `Summaries` | `Summarize`, `SectionSummary` |
| 4 | `MinutesAndFormulas` | `MeetingMinutes`, `SectionMinutes`, `MeetingTypeMinutes`, `FormulaRun` |
| 5 | `Translation` | `Translation` |
| 6 | `Chat` | `ChatMessage`, `ChatTitle` |

`Embedding`, `SearchQuery` and `Dictation` map to **no group**: they send no sampling parameters (embeddings
post `{model, input}`, dictation posts multipart audio), so there is nothing for a temperature to mean.
`AdminTest` also maps to **no group**, for the opposite reason: it sends a full set, but the administrator
chooses which group's while editing them, so the resolver never decides it.

#### `LlmCalls`
One row per outbound call to a model endpoint, written by `LlmTelemetryHandler` off the request path via a
bounded in-memory channel and a background writer (`LlmUsageWriter`) - see
`Overall_Synopsis_of_Platform.md` for the full capture contract. **Never stores prompt or completion
content** - counts and sizes only, the same rule `SentryScrubber` enforces elsewhere. A Platform
Administrator browses, filters, and deletes rows in this table via the admin usage viewer at
`/admin/llm-usage` (`LlmUsageController`; see `Overall_Synopsis_of_Platform.md` for its endpoints).

The `UserId`/`RecordingId`/`SectionId` links are each `ON DELETE SET NULL` and each paired with a
**denormalized snapshot column** (`UserEmail`/`RecordingTitle`/`SectionName`) captured at write time, so a
row stays readable - "who this was for", "which recording" - after the user, recording, or folder it
pointed at is deleted. That is deliberate for an audit trail: erasure of this data is instead a filtered
bulk delete on the admin usage viewer, not a cascade off the subject's own deletion.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | |
| `OperationId` | uuid | groups every call made by one user-facing operation (e.g. all section calls of one folder-minutes run); a "turn" = `MAX(Sequence)` per operation |
| `Sequence` | int | 1-based index of this call within its operation |
| `Kind` | int | `LlmCallKind`: what the call was for - `Unknown`(0, no active scope), `Summarize`, `SectionSummary`, `MeetingMinutes`, `SectionMinutes`, `MeetingTypeMinutes` (reserved; never written - the generator's calls belong to the enclosing MeetingMinutes/SectionMinutes operation), `ExtractActions`, `Tags`, `Translation`, `Dictation`, `Embedding`, `SearchQuery`, `ChatMessage`, `FormulaRun`, `ChatTitle`, `AdminTest`(15, an administrator's connection test from
/admin/llm-models). Append-only enum |
| `UserId` | uuid null FK → AspNetUsers | who the call was for; **`ON DELETE SET NULL`** |
| `UserEmail` | text | denormalized snapshot of the user's email at write time; empty string when there was no active scope |
| `RecordingId` | uuid null FK → Recordings | the recording the call was for, if any; **`ON DELETE SET NULL`** |
| `RecordingTitle` | text null | denormalized snapshot of the recording's title at write time |
| `SectionId` | uuid null FK → Sections | the folder the call was for, if any; **`ON DELETE SET NULL`** |
| `SectionName` | text null | denormalized snapshot of the folder's name at write time |
| `Model` | text | the `model` field read out of the outbound request body; empty string if absent/unparseable |
| `Endpoint` | text | scheme + host + path only - the query string is dropped outright rather than scrubbed (the same rule already applied to span descriptions, after a SignalR JWT once reached a transaction name that way) |
| `StartedAt` | timestamptz | |
| `CompletedAt` | timestamptz | |
| `DurationMs` | int | stored rather than derived from the two timestamps above, so ordering and `SUM` are trivial |
| `TimeToFirstTokenMs` | int null | populated for streamed calls (chat replies, formula runs) with the elapsed time from request to the first response byte; null for non-streaming calls, which have no meaningful first-token moment |
| `PromptTokens` | int null | from the response's `usage` block; null (not 0) when the endpoint reports none |
| `CompletionTokens` | int null | ditto |
| `ReasoningTokens` | int null | from `usage.completion_tokens_details.reasoning_tokens`, reported by reasoning models; almost always null |
| `TotalTokens` | int null | reported directly, or derived as prompt + completion when the endpoint reports the two halves but not the sum |
| `PromptChars` | int null | length of the serialized outbound request body - a proxy for prompt size, showing when context-budget truncation is biting |
| `Streamed` | bool | whether the response was `text/event-stream` (SSE) |
| `Success` | bool | false whenever `ErrorKind` is set |
| `StatusCode` | int null | HTTP status of the response, when one was received |
| `ErrorKind` | text null | a class, never a message: `Timeout` (covers both a genuine per-call timeout and an ordinary caller cancellation - the handler cannot tell them apart), `Transport` (`HttpRequestException`), `Http{status}` (a non-2xx response), or the raw exception type name as a fallback |

Indexes: `(OperationId)`, `(RecordingId)`, `(SectionId)`, `(StartedAt)` descending, `(UserId, StartedAt)`
(`UserId` ascending, `StartedAt` descending) - the last two support the admin viewer's default "my/everyone's
usage, most recent first" queries.

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
- **Meeting screenshots** use **two** keys per capture: `{userId}/screenshots/{id}.png` (the full image,
  stored on `MeetingScreenshot.BlobKey`) and `{userId}/screenshots/{id}.thumb.jpg` (the thumbnail, on
  `MeetingScreenshot.ThumbBlobKey`). Both are streamed back the same way audio/attachments are (same-origin,
  never a presigned MinIO URL) and count toward the owner's quota.
- **Blob lifecycle on delete/merge:** deleting a recording also deletes its attachment-file and
  screenshot blobs (the DB cascade only removes the rows). Merging **moves** the merged-away recordings'
  attachments onto the survivor (rows reparented, blobs kept), so nothing is orphaned; screenshots are
  **not** reassigned - their blobs are freed instead, both from the synchronous no-audio merge path and from
  the audio-merge worker callback, which also defensively frees any attachment blob still on a source it
  removes.

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

In Docker Compose, MinIO data persists in the **`miniodata`** named volume (the S3 API is remapped to host
**9002**; the console is not published). Companion volumes: **`pgdata`** (Postgres, reachable from the host
on **5433** for external tooling), **`apikeys`** (the Data
Protection keyring that decrypts `LlmModels.ApiKeyEncrypted`, mounted at `/keys`), and
**`workercache`** (model weights). Back up `pgdata` + `miniodata` together — a transcript row in Postgres is
meaningless without its audio blob, and vice-versa, and losing `apikeys` makes stored model API keys
unrecoverable.
