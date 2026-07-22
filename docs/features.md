# Diariz - full feature list

This is the detailed, prose feature list. The [README](../README.md) carries an at-a-glance
two-column summary table and links here for the full detail. **Keep both in sync** (and the in-app
About-box `CAPABILITIES` summary in [`apps/web/src/lib/releases.ts`](../apps/web/src/lib/releases.ts))
when the app's scope changes - see [CLAUDE.md](../CLAUDE.md).

- **Capture** audio from the browser microphone — **choose a specific input device** (the choice is
remembered, and the list refreshes on hot-plug), **tune capture** (echo cancellation, noise suppression,
auto gain, mono) from a ⚙ popover, and watch a **live input-level meter** while recording (with a subtle
silence hint). **Pause and resume** a recording in progress (separate from Stop) for breaks or sensitive
moments — paused audio is never captured and never counts toward the recording's duration. **Schedule the
current recording to auto-stop** - after 15/30/60 minutes or at a set clock time - and it ends and starts
transcription on its own, so you can start a recording and walk away. Also capture
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
same-speaker rows, and **email yourself** the formatted transcript. The transcript **embeds its audio** in a
**conversation-flow player**: the recording is laid out left to right as speaker-coloured blocks sized by how
long each person talked, with silence left dark and a legend giving each speaker's share — so the shape of the
meeting is legible at a glance — and the bar doubles as the scrubber (click or drag anywhere on it to seek).
Its toolbar keeps a **Select mode** — tick segments (or click one) to **play, edit, translate, or delete** just
the selection, while **Merge** always acts on the whole transcript. **Play selected** turns into **Pause** while
that selection plays, so you can stop it without waiting for it to finish. The **speaker label at the start of
every row** is the same assignment dropdown as the Speakers panel, so you can name a voice (or enrol a new
person) at the moment you hear them, without leaving the transcript.
- **Recording hub** — opening a meeting lands on a hub rather than a strip of tabs. A **hero summary card**
carries the **meeting type** (a dropdown that drives the minutes template and the formulas offered), the key
facts as chips (date and time, duration, whether the audio is still available and how long it has left,
language, the speakers, who recorded it, which rooms it's in), and the **summary itself shown inline** — no
hover, no extra click. Below it is a grid of **tiles**: Transcript, Actions, Speakers, Notes, Files, and
Formulas, each showing its **real count and a preview of what's inside** (the first actions, the latest note,
the attached files, the formula runs), so you can see what a meeting holds without opening anything. Notes,
Files, and Formulas can be **added or run straight from their tile**. Clicking a tile drills into that section
with a **breadcrumb** back to the hub, and the section you were last in is remembered. A **meeting card** below
the tiles shows the calendar meeting the recording came from (time, location, organiser, description) with
**Change** / **Unlink**, or — when it isn't linked yet — the meeting your calendar **suggests** it came from,
ready to accept in one click. It only appears when there is something to show.
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
- **Meeting screenshots (desktop app).** Capture the screen while a recording is running, from a
**configurable global hotkey**, the **tray menu**, or a button in the app itself. The first capture of each
meeting opens a picker overlay so you choose **a whole monitor or a dragged rectangle**; every later capture
in that meeting reuses the same area, and a "Change capture area" action lets you redefine it mid-meeting
(the choice resets for the next recording). A live strip of this meeting's captures sits in the recorder's
notes popover, so a mis-aimed capture area is caught during the meeting instead of after it. Each capture
stores a full PNG (long edge capped at 2560 pixels) plus a JPEG thumbnail, and both count toward your
storage quota. Screenshots then appear **inline in the transcript** at the moment they were taken, as
thumbnails that open a full-size viewer with previous/next, jump-to-moment, download, and delete; the Notes
tab also lists a recording's captures in a collapsed Screenshots section. A note or screenshot sitting
between two turns by the same speaker now stops those turns from being merged past it.
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
- **Meeting types: minutes are a formula.** A **meeting type** is *presentation and selection* — a name, group,
icon, colour, and the **framing** you give the model ("this is a customer call; keep it suitable to send back to
them"). It carries **no prompts of its own**: it names the **formula** whose template generates the minutes, plus
any **additional formulas** to run at the same time (their documents land in the recording's Formulas tab). So
minutes and formulas are the **same machinery** — any formula you can use can produce your minutes, and the
formula decides both the shape of the document and what the model is shown. A standard set ships (General,
Customer, Cadence Call, Weekly, 1:1, Interview, Town Hall, Webinar), each with the built-in formula that generates
it; pick one from the Minutes toolbar to re-run the minutes in that structure. A **Manage Meeting Types** editor
creates/edits them — **Personal** (a user's own) or **Platform** (admin-owned, shared read-only) — and offers only
formulas that type is allowed to use (a shared type can't point at someone's private formula, or nobody else would
get minutes). A formula that generates some type's minutes **can't be deleted or disabled** until those types point
elsewhere. Meeting types can be **exported to a JSON file and imported** back (they reference their formulas by
name, and the import tells you if this instance hasn't got one), so you can share them between accounts.
- **Templates are built from blocks.** A formula's template — and therefore a minutes template — is H1/H2/H3
sections whose blocks are **literal text**, **substituted recording values** (date, attendees, the action-items
table, your notes, …), **instructions to the model**, or a **horizontal rule**. Each block has a **Break-after**
control (no break / line break / paragraph) so you decide exactly where content runs together or separates; text
blocks are an **auto-growing Markdown** box, and a **drag handle** moves any block within a section or into
another. A section can also be **headless**, which is what a formula that is simply one instruction looks like.
The templates Diariz ships with are **plain markdown files** in the repository, so you can read and review the
exact words the model is given.
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
- **Search the panel** - a search box sits above the meetings list. Typing takes the list over with results and
clearing drops you back exactly where you were browsing. It searches the **folder you are in** by default (the
chip tells you which), and each hit shows the matching words in context, the folder it lives in, and clicking
it opens the transcript **at that moment**. Folders whose name matches appear too, and take you straight there.
**Search everywhere** (next to the result count) widens the search to **every room you can see**: the chip
switches to *Everywhere*, results are **grouped under the folder** each meeting lives in (coloured to match,
with a count), and **Section / Date / Speaker** chips narrow them. The chip options are built from the results
you actually got, so none of them lead to an empty list. Scope and filters last only as long as the search -
clearing the box returns you to your folder.
- **Semantic (RAG) search** (opt-in): configure an embeddings endpoint and transcripts are embedded into a
pgvector index; the panel search, chat and the tools then search by **meaning as well as keywords** (hybrid
vector + trigram, fused with Reciprocal Rank Fusion), so a conceptual question finds the right moment even when
the words don't match. Without an embeddings endpoint, search stays keyword-only.
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
- **Voice dictation in chat.** The chat input has a microphone button that transcribes your speech into the
  box near-real-time (transcribing on each pause), so you can dictate a question and edit it before sending.
  In Chrome/Edge browser tabs it uses the built-in Web Speech API; in the desktop app and other browsers it
  falls back to an OpenAI-compatible speech-to-text endpoint configured on the server (Dictation settings).
- **Connect Claude to your transcripts (MCP server)** — Diariz hosts a **Model Context Protocol** server at
`/mcp`, so you can connect **Claude** directly to *your own* meetings, two ways: **sign in from the Claude
website** (add Diariz as a custom connector and approve it on a consent screen — an **OAuth 2.1** flow, nothing
to copy), or generate a **personal access token** in **Preferences → Claude / MCP access** and paste the URL +
token into Claude Desktop or Code. Either way Claude uses the same built-in tools (search / who-said-what /
action items / summaries / attendees / talk time / …, plus email-to-self) to answer grounded in your
transcripts. Per-user and secure: tokens are shown once and stored only as a **SHA-256 hash**, work only for
your own recordings, and both **tokens and web connections are revocable** any time in the same Preferences
section.
- **Formulas: build a document, run it over a recording.** A **Formula** is a **template** plus a chosen
**context**. The template is built from blocks - **headings**, **literal text**, **substituted meeting details**
(date, time, title, attendees, duration, action items, your notes), **instructions to the model**, and horizontal
rules - so it produces a properly laid-out document rather than whatever shape the model felt like. The context
(any mix of transcript, notes, summary, minutes, and action items) is what the formula is allowed to see. Run it
over a recording to generate a named **Markdown Result** — open it, edit it in the same rich editor as minutes,
download it as `.md`, or email it to yourself. A formula that is simply one instruction is just a template with
one block, so nothing has to be more complicated than it needs to be. Formulas come in three scopes: Diariz-provided **starter formulas** seeded on every install
(Follow-up email, Meeting recap, Decisions & risks, Tone & sentiment read), **Platform-wide** formulas shared
with everyone, and your own **Personal** formulas — create and edit these in **Preferences → Formulas**. A
recording-level **Formulas tab** lists every formula you can use, and a matching **Formulas tab on any folder
(section) page** runs the same formula over **every meeting in that folder and its sub-sections** (a map-reduce:
the formula runs on each transcript, then over the combined results). Runs happen **in the background** - the
result appears right away as "Generating..." and fills in when ready (or shows a clear error), so you can run
several at once without waiting. **Re-running a formula replaces its previous document** rather than piling up
near-identical copies - and if you have **edited** a document by hand, an automatic re-run leaves it alone
(running the formula yourself still regenerates it). Each recording and each folder keeps its own results. The tab is a resizable two-panel view - the
list of results you have generated on the left (each with an **origin icon**: the Diariz logo for built-in and
platform formulas, the author's avatar for your own), and the selected result's rendered document on the right,
read in place. Creating, editing, deleting, or enabling/
disabling a Platform or Diariz formula requires the new **Manage Formulas** permission (granted via a user
group), while your Personal formulas are always yours to manage. You can also run a formula without opening
the tab: type **`/formula <name>`** in the chat box to run it on the recording you have open, or ask **Claude**
to run it for you - the built-in `run_formula` chat tool is exposed over MCP, so Claude Desktop, Claude Code,
and the claude.ai web connector can trigger any formula you can see. Admins with **Manage Formulas** get a
**Manage Formulas** window from the account menu to create and edit Platform-wide formulas shared with
everyone, and to enable/disable or tune the built-in Diariz starter formulas. You can also **share a Personal
formula** with everyone on the platform: turn on "Share this formula" in its editor, and others can open **Find
shared formulas** in the run picker to see who shared it, read what it does, and **add** it to their own
collection - a **live link, not a copy**, so your later edits reach them too. Added formulas appear in a new
**Shared Formulas** group in the run picker (run with the subscriber's own LLM config); anyone can remove one
they added, and deleting the original removes it for everyone.
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
ordering and cross-group moves; select a whole level at once to build chat context. The list **drills in
one folder at a time** rather than showing every folder expanded: a folder row shows its colour, the count
of everything inside, and takes you in; a breadcrumb walks you back out (browser back pops a level too),
and **Open section page** opens the folder's own page - browsing deeper and opening the page are separate
targets. Recordings with no folder simply sit at the top level. Browse them as a **list or a calendar**
(days with recordings are highlighted; click one to see that day's recordings).
- **Rooms.** Every account has a private **Personal Room** (your existing space). Holders of the
**manage-rooms** permission can also create **Shared Rooms** - workspaces you invite **users and groups** into,
each member carrying their own **permission grid** (add recordings, manage contents, remove others' recordings,
share out, edit others' recordings, manage the room). A **room switcher** sits above the recordings list: each
room shows **how many folders and meetings** are in it (a shared room's line says *shared*, the one thing a name
cannot tell you), and a **tick** marks the room you are in. **Manage Rooms** (in the switcher) creates, renames,
restyles (icon + colour) and deletes rooms and edits their membership; deleting a room needs its name typed to
confirm. The room lives in the URL (`/rooms/:roomId`), so switching keeps a clean, linkable address - and Diariz
**remembers the room you were last in**, returning you to it when you come back (the URL still wins whenever it
names one) - and **browses that room**: picking a Shared Room shows the recordings
shared into it. A Shared Room has its **own folder structure**: members with **manage-contents** can create
sections and sub-sections in it, file recordings into them (via the recording's Move-to-folder action or by
**drag-and-drop**), and **drag to reorder** recordings within a section - each room keeps its own order, separate
from your Personal Room. **Your Google Calendar stays personal**: a Shared Room's Calendar tab shows only its
recordings (no personal-event overlay), and a recording opened inside a Shared Room hides its linked meeting and
offers no calendar linking. The **List, Calendar, Actions
and Tags** tabs all work in a Shared Room too, each scoped to the recordings shared into that room. **Recording or uploading a file while a shared room is open** files the
meeting into that room automatically, while the original stays in your Personal Room - so a shared room can only ever **unshare** a
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
shows the meeting's **full details** (time, location, organiser, attendees, description) with any **URLs in the
location/description rendered as clickable links** (so you can join a Zoom/Meet call straight from the app), and the **Calendar
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
