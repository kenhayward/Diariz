# Diariz - full feature list

This is the detailed, prose feature list. The [README](../README.md) carries an at-a-glance
two-column summary table and links here for the full detail. **Keep both in sync** (and the in-app
About-box `CAPABILITIES` summary in [`apps/web/src/lib/releases.ts`](../apps/web/src/lib/releases.ts))
when the app's scope changes - see [CLAUDE.md](../CLAUDE.md).

- **Capture** audio from the browser microphone — **choose a specific input device** (the choice is
remembered, and the list refreshes on hot-plug), **tune capture** (echo cancellation, noise suppression,
auto gain, mono) from a ⚙ popover, and watch a **live input-level meter** while recording (with a subtle
silence hint). **Pause and resume** a recording in progress (separate from Stop) for breaks or sensitive
moments — paused audio is never captured and never counts toward the recording's duration. Also capture
**system audio** - tick the **System audio** checkbox to mix it into the recording (both sides of a call on
one device), or pick **No microphone** to record system audio alone. System audio works in **Chromium
browsers** (tick "Share audio" in the share dialog) and seamlessly in the Electron **desktop app** (Windows
loopback / **macOS - beta** ScreenCaptureKit); the checkbox is hidden where the browser can't capture it, and
if system audio isn't shared the take falls back to microphone-only. The desktop app can also **start/stop
recording from its tray / menu-bar menu** (in the background, with notifications), including a **Record Both**
item. Or **upload existing audio files** to transcribe (WAV, MP3, FLAC, Ogg/Opus, WebM, M4A) — via the
Upload button or by **dragging several onto the recordings list**, with per-file status.
- **Transcribe + diarize** server-side with WhisperX (large-v3, word-level timestamps) and pyannote 3.1,
producing speaker-labelled, timestamped segments you can rename, edit, and play back (per segment, per speaker,
or the whole recording). A **Speakers** panel lists each speaker with their segment count and **total talk time**,
plays or steps through just their segments, and reassigns them. Edits are kept **separately from the model's
original words** — a ✎ marks revised rows and a **Show original / Show revised** toggle flips the whole
transcript, so you can always get back to what the model said. Re-transcribe with a chosen model at any time
(with optional **min/max speaker hints** for pyannote when voices are merged), **merge** consecutive
same-speaker rows, and **email yourself** the formatted transcript. The transcript panel **pins to the top** as
you scroll (its segments then scroll internally) and has a header **icon toolbar** with a play bar (which plays
the whole recording) and a **Select mode** — tick segments (or click one) to **play, edit, translate, or delete**
just the selection, while **Merge** always acts on the whole transcript.
- **Tabbed recording page** — Overview (summary), Minutes, Actions, Notes, Speakers, Transcript, and Attachments
are organised into horizontal tabs, each showing its own toolbar directly below the tab strip; the last-used tab
is remembered.
- **Notes & enhanced notes.** Take your own note lines for a meeting — sparse trigger phrases, questions,
observations. A **live notes panel** while recording stamps each line at the second you wrote it (crash-safe,
lands on the recording's **Notes** tab after upload); you can also jot **prep notes on an upcoming Google Calendar
meeting** from its preview page (they attach to the recording once it is linked). Timestamped lines jump to that
moment in the transcript. Each timestamped note is also **woven inline into the Transcript tab** - it appears as
its own **green line** (with your name as the speaker) right after the point in the conversation where you wrote
it; the **Merge same-speaker rows** action treats a note as a boundary, so transcript text either side of a note
stays separate. Your notes then **shape the meeting minutes** (every section weights what you flagged),
and a template can include an **Enhanced notes** section where each line is expanded from the transcript — your
words kept verbatim in bold beside the expansion, with links to the exact transcript moments (anything the meeting
never covered is kept and marked "not discussed", never silently dropped).
- **User API access.** When a Platform Administrator enables it (Settings → Integration), generate a **personal
API token** (Preferences → Developers) to call the Diariz **REST API** directly as yourself — acting with your own
permissions, over your own data — and browse a **built-in API reference** (Developers → View API reference).
- **Status bar** locked to the bottom of the app: left-aligned live progress (transcribing, summarising,
merging, extracting actions, uploading, errors — in their tone colours) and right-aligned storage usage ·
transcription usage · total transcripts.
- **Identify speakers** across recordings: enrol a person from a recording's speaker and Diariz recognises
that voice automatically in later recordings (SpeechBrain ECAPA voiceprints in pgvector, cosine matching),
with manual reassignment. The **Voice Prints** tab (Preferences) renames, prunes training samples, merges
duplicates, and erases voiceprints (GDPR — biometric data).
- **Summarize** recordings (with automatic naming) and generate a full set of **professional meeting minutes**
(Markdown: headings, lists, tables — no emojis) as part of the pipeline; edit them in a **WYSIWYG editor**,
re-create them, or **email them to yourself** (optionally with the recording's attachments). The minutes also
travel with the emailed transcript and the Markdown/text/RTF downloads. The Meeting Minutes panel is always
available (collapsed) with a refresh button to generate them on any recording.
- **Meeting types (minutes templates).** Minutes are driven by a **meeting type** — a reusable template of
H1/H2/H3 sections whose blocks are **boilerplate text**, **substituted recording values** (date, attendees, the
action-items table, …), **model prompts**, or a **horizontal rule** (a divider on its own line). A standard set
ships (General, Customer, Cadence Call, 1:1, Interview, Town Hall, Webinar); pick one from the Minutes toolbar to
re-run the minutes in that structure. A **Manage Meeting Types** editor creates/edits templates — **Personal** (a
user's own) or **Platform** (admin-owned, shared read-only). Each block has a **Break-after** control (no break /
line break / paragraph) so you decide exactly where content runs together or separates; text blocks are an
**auto-growing Markdown** box, and a **drag handle** moves any block within a section or into another. Templates can be **exported to a JSON file and
imported** back (naming the import, since it may duplicate one you have), so you can share them between accounts.
When a template substitutes the **attendees** field it names the identified people and then counts the rest
(e.g. "Alice, Bob and 11 unidentified attendees"). A Platform Administrator can also pick how minutes generate:
**one LLM call per section** (best structure) or a **single call** (fewer tokens).
- **Chat across one or more transcripts — a folder — or all your meetings at once** (an "All meetings" mode
that searches your whole library on demand instead of pre-loading transcripts) — streaming
replies, a context-usage dial, PDF/text attachments, and saved conversations — via a per-user (or
server-default) OpenAI-compatible LLM endpoint, with the API key encrypted at rest. The chat's context is
**inferred from what you're viewing** rather than picked from a list: the label reads **Current Transcript**,
**Current Folder**, or **Selected Transcripts** (2+ ticked in the list) and updates when you click into the
box. When a **folder** is open, chat is about that folder — its roll-up **summary, minutes, and aggregated
actions** are the context, and "Include attachments" pulls in every attachment across the folder and its
sub-folders.
- **Semantic (RAG) search** (opt-in): configure an embeddings endpoint and transcripts are embedded into a
pgvector index; chat and the tools then search by **meaning as well as keywords** (hybrid vector + trigram,
fused with Reciprocal Rank Fusion), so a conceptual question finds the right moment even when the words don't
match. Without an embeddings endpoint, search stays keyword-only.
- **Chat tools** (opt-in, per-user): the assistant can call **built-in tools** that search your **whole
transcript library** — *who said a phrase*, *what a person said about a topic*, *search transcripts*, *when a
topic was discussed*, *count mentions*, *list recordings* (by date / name / speaker / topic), *list action
items*, *get a recording's summary*, *who attended*, *speaker talk time*, *the lines around a moment*, and a
recording's *full transcript*, *meeting minutes*, or *details* —
answering as **When · Who · What**. Two **write** tools let it act: **email you** (a *send-email* tool that
composes a subject + body and always delivers to your **own** registered address — it can never email anyone
else, and it files a copy of each sent email onto the transcript as a Markdown attachment), and **add its
output to a transcript** (an *add-as-attachment* tool that saves prepared content as a
Markdown attachment — you pick the transcript when several are selected). Answers
**link back to the transcript**: click a citation to open that
recording and jump to the exact segment. Fuzzy search is backed by a Postgres `pg_trgm` trigram index; a
brief grey "Tool call: …" line shows while a tool runs. Chat also has **slash commands** — `/tools`, `/help`,
`/clear`, `/context`, `/save`, `/load`, `/copy`, `/retry` — handled in the browser and never sent to the model
(type `/` for an autocomplete popup).
- **Connect Claude to your transcripts (MCP server)** — Diariz hosts a **Model Context Protocol** server at
`/mcp`, so you can connect **Claude** directly to *your own* meetings, two ways: **sign in from the Claude
website** (add Diariz as a custom connector and approve it on a consent screen — an **OAuth 2.1** flow, nothing
to copy), or generate a **personal access token** in **Preferences → Claude / MCP access** and paste the URL +
token into Claude Desktop or Code. Either way Claude uses the same built-in tools (search / who-said-what /
action items / summaries / attendees / talk time / …, plus email-to-self) to answer grounded in your
transcripts. Per-user and secure: tokens are shown once and stored only as a **SHA-256 hash**, work only for
your own recordings, and both **tokens and web connections are revocable** any time in the same Preferences
section.
- **Extract action items** (Action / Actor / Deadline) with that same LLM — **automatically as part of the
transcription pipeline**, into an editable table in an always-available **Action items** panel (collapsed by
default, with a refresh button to re-extract). The automatic pass runs once and never overwrites actions
you've added or edited. The **meeting minutes are generated from that same action set**, so the minutes' Action
Items table and the Actions panel always match. The actions also travel with the transcript — included in the
downloads (Text/Markdown/RTF), the emailed transcript, and the chat context.
- **Manage actions across all your meetings** in a dedicated **Actions** tab (the left panel is now
**Meetings**): every action item in one list, **filter by person**, mark items **done** with a completion
date (individually or in bulk, reversible), **hide completed**, and click an action to jump to the transcript
it came from. The per-transcript table gains the same Done checkbox + Completed date.
- **Translate** a transcript into your chosen language with that same LLM — the whole recording (segments,
summary, and actions) or a single segment. Translations are stored as **revisions** over the model's
original words (so you can always flip back), and exports/email/chat use them.
- **Organise** recordings into **sections and sub-sections** (one level of nesting) with drag-and-drop
ordering and cross-group moves; select a whole group at once to build chat context. Browse them as a
**list or a calendar** (days with recordings are highlighted; click one to see that day's recordings).
- **Rooms.** Every account has a private **Personal Room** (your existing space). Holders of the
**manage-rooms** permission can also create **Shared Rooms** - workspaces you invite **users and groups** into,
each member carrying their own **permission grid** (add recordings, manage contents, remove others' recordings,
share out, edit others' recordings, manage the room). A **room switcher** sits above the recordings list, and
**Manage Rooms** (in the switcher) creates, renames, restyles (icon + colour) and deletes rooms and edits their
membership; deleting a room needs its name typed to confirm. The room lives in the URL (`/rooms/:roomId`), so
switching keeps a clean, linkable address. **Recording while a shared room is open** files the meeting into that
room automatically, while the original stays in your Personal Room - so a shared room can only ever **unshare** a
recording, never delete it. You can also **Share to room** an existing recording (or **Remove from room**) from
its toolbar; the recording's Overview shows a **Rooms** line (home room first) and a **Recorded by** line, and
Delete only appears in the home room (its confirmation names the shared rooms it will also vanish from). **Chat and
the Claude (MCP) tools search across every room you belong to**, so a meeting shared into a room you are in turns
up in your searches. Deleting a user **keeps** their shared recordings and **orphans** their Personal Room rather
than destroying its history. Voiceprints, saved chats and meeting-type templates are room-scoped too.
- **Where new recordings land.** A **Recordings** tab in Settings chooses how a fresh recording is filed in
your Personal Room: **Ungrouped**, the **folder you currently have open** (the default), or a **specific
folder** you pick. When you press Record, the take is filed accordingly the moment it finishes uploading -
no manual move needed.
- **Folder pages.** Open any folder (section) as a **first-class page** - the same layout as a recording
(heading, subheading, toolbar, tabs). The disclosure triangle (enlarged) still collapses/expands; clicking the
folder **name** opens its page and highlights the folder. **Overview** shows folder stats (transcript count,
total duration, first/last date), a **roll-up LLM summary** of all the folder's recordings (and its
sub-folders), and a read-only transcript list grouped by sub-folder. **Minutes** produces **consolidated
minutes** by reshaping the recordings' individual minutes through a **meeting-type template** you pick. Both
regenerate any missing per-recording summaries/minutes first, run in the background, are editable, and are
saved on the folder. **Actions**, **Notes**, and **Attachments** tabs aggregate every item across the folder
and its sub-folders, each tagged with the **meeting** it came from - editable and deletable in place
(attachments removable). The **Attachments** tab also has a separate, **addable** list of attachments filed
**directly on the folder** (files or URLs) that don't belong to any one transcript, shown above the aggregated
list.
- **Tag cloud across your meetings**: every meeting is **tagged automatically** after transcription — the
LLM extracts up to 12 weighted concepts/themes it was actually about (participant names and filler are
excluded). The left panel's **Tags** tab shows them as a flat weighted cloud (font size scales with how
central a topic is across your library); click a tag to list the meetings that carry it, and an **expand**
button opens the cloud in a large modal (picking a tag there filters the panel too; picking a meeting opens
it). Re-transcribing refreshes a meeting's tags. Existing libraries are **backfilled** automatically at
startup (when a server-wide LLM is configured), and a Platform Administrator can trigger the backfill from
**Settings → Maintenance** (e.g. for per-user-only LLM configs).
- **Attach supporting documents** to a transcript — or **directly to a folder** — upload files (PDFs, Office
docs, emails, calendar invites, images, …) or add URLs, then rename, open, and remove them from an
"Attachments (N)" button (or drag files onto the page). Files are stored in object storage and count toward
your quota. **Markdown attachments are editable in place**: click Open and a rich (WYSIWYG) editor opens
seeded with the document; Save overwrites it. You can also save a **whole chat conversation** as a Markdown
attachment with the **`/attach`** chat command (onto the current transcript, the first selected one, or the
current folder). Turn on **Include attachments** in chat to feed them to the LLM (documents are read into
text; URLs are fetched behind SSRF guards).
- **Manage audio & merge**: **delete a recording's audio** to reclaim its storage while keeping the
transcript, and **merge** several recordings into the earliest one — their transcripts are laid end-to-end
and their action items are folded in. Audio is concatenated server-side (ffmpeg) for the recordings that
still have it; recordings whose audio was deleted merge their transcript only (the summary is regenerated).
- **Multi-user RBAC**: authority comes from **user groups**, each carrying platform permissions - manage
rooms, manage users, manage platform. A user's permissions are the union of their groups', re-read on every
request, so adding or removing someone from a group takes effect immediately rather than at their next
sign-in. Two groups ship: **Platform Administrators** (everything; it cannot be deleted and its last member
cannot be removed) and **Administrators** (manage users and rooms, but not backup/restore or platform
settings). Administrators manage groups and membership in Manage Users → Groups. Around that sits an
access-request → admin-grant → account-setup lifecycle (one-time email link, with an in-app fallback when
SMTP is unconfigured). Each user's data is isolated to them. Light/Dark/Auto theming.
- **Sign in with Google** (optional): OAuth 2.0 sign-in that reads the user's name, email, and profile
picture (shown in the account menu). New Google sign-ups still require admin approval, and a Google email
matching an existing account links to it automatically. Enabled when the operator configures a Google
OAuth client. It works in the web app and in the **desktop app** (the desktop client runs consent in your
system browser and returns you to the app, since Google blocks sign-in inside embedded windows). A Google-linked user can opt in
(Preferences → Google) to let Diariz **read their Google Calendar** (read-only) so a recording is **linked to
the meeting it belongs to** (auto-saved on open, or picked by hand when the times don't line up), its Overview
shows the meeting's **full details** (time, location, organiser, attendees, description), and the **Calendar
tab overlays their meetings** (a linked recording and its meeting show as one row; a meeting with no recording
opens a preview you can link a recording to) — a revocable grant. They can also **choose which of their Google
calendars** to consider (Preferences → Google Account); only the selected calendars are used for matching and
shown on the Calendar tab.
- **Subscribe to external calendar feeds**: add any public iCalendar (`.ics`) URL — a team or shared
calendar — in **Preferences → Calendar feeds**, give it a name and colour, and its meetings appear on the
Calendar tab in that colour (fetched behind an SSRF guard, no Google connection required).
- **Global AI timeout** (Platform Administrator, Settings → Model Settings): one platform-wide per-request
timeout in seconds (default 120) applied to every AI call - summaries, minutes, actions, tags, and embeddings.
Raise it for slow local models; the configured value is the single authority (no hidden HTTP cap).
- **Preferences**: a tabbed window (Profile, Google Account, Calendar Feeds, Claude Access, Voice Prints).
Each user can edit their **profile** — display name, job title, company, job/company descriptions, LinkedIn
account name — pick their **native** and **app** language, and choose a **theme** (Light/Dark/Auto) that is
saved to their account and follows them across devices.
- **Storage quotas**: each user gets an audio-storage quota (starter + maximum set by the Platform
Administrator; any admin can raise a user up to the maximum). Usage shows in the account menu and
per-recording; over-quota uploads are rejected.
- **Automatic audio deletion** (Platform Administrator, Settings → Storage Quotas): an opt-in nightly job
deletes the original audio of recordings older than a chosen number of days (default 30, at a chosen
server-local time), keeping the transcript. Only fully transcribed recordings are eligible, and any
recording can be marked **Protected from audio deletion** to exempt it (from both the job and manual
deletion). A **Run now** button runs the same pass on demand. Off by default.
- **Backup & restore** (Platform Administrator, Settings → Maintenance): download the whole platform —
the Postgres database (`pg_dump`) plus every stored file — as one transferable archive, and restore from
one. Restore is destructive (replaces all data) and only accepts a backup from the same app version.
