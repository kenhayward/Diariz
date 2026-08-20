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
if system audio isn't shared the take falls back to microphone-only. **On Linux**, Chromium only carries
audio when you share a **browser tab** - sharing a screen or window yields a silent stream (and so a
microphone-only take), and the desktop app does not help because loopback capture is Windows-only. The
Linux route that always works is to publish the speaker monitor as an input device and pick it from the
microphone dropdown. Diariz **ships the PipeWire drop-in that does this** - served at
`/linux/99-diariz-system-audio.conf` for a per-user install, and packaged as a one-file `.deb`
(`packaging/linux/build-deb.sh`) that installs it to `/etc/pipewire/pipewire.conf.d/` for every user on a
managed machine. See the **Recording audio** help article. The desktop app can also **start/stop
recording from its tray / menu-bar menu** (in the background, with notifications), including a **Record Both**
item. Or **upload existing audio files** to transcribe (WAV, MP3, FLAC, Ogg/Oga/Opus, WebM, M4A/M4B, AAC) - via the
Upload button or by **dragging several onto the recordings list**, with per-file status. You can also
drop a **video** (MP4, MOV, MKV, WebM): its audio is extracted **in your browser**, mixed to mono and
uploaded on its own, so the video is never sent to the server or stored against your quota. Long
extractions show progress and can be cancelled. Dropped files land in **the folder the list is
showing** - where you dropped them beats the placement preference, which decides for the Upload
button (and for a new recording) instead.
- **Record a meeting straight from your calendar.** Opening a calendar event gives you **Join meeting**, which
opens the meeting link and starts recording in one click. The recording is **named after the invite**, so your
library reads as the meetings you attended rather than a list of timestamps - and the name sticks, rather than
being replaced later by an AI-generated one. It is also **linked to that meeting straight away**: everywhere else
Diariz infers the link by finding the calendar entry that best overlaps the recording, and only when you first
open it, but starting from the event means there is nothing to infer - the invite's details are on the recording
the moment it appears, any **prep notes** you wrote on the event come across with it, and because you picked the
meeting yourself the automatic matcher will not later swap it for an adjacent one.
Under **Preferences → Recordings**, the switch **Let a calendar meeting end its own recording** lets
such a recording **end by itself** (off by default): it stops a chosen number of **minutes after
the meeting was scheduled to finish** - catching the wrap-up rather than cutting off on the hour - or after a
chosen run of **silence** for when the meeting breaks up early, whichever comes first. Silence only counts once something has been
heard, so joining before anyone speaks never cuts the recording short, and pausing does not count against it.
Both settings apply **only** to a recording started from a calendar event; one started with the Record button
runs until you stop it, exactly as before. And whatever those settings say, **joining a second meeting while
the first is still recording** finishes the first properly - it uploads and transcribes on its own - before the
new one begins. If people are still talking when the meeting's scheduled end arrives, Diariz asks whether to
**Extend this meeting** instead of cutting it off outright; leaving it unanswered keeps recording, and the
silence rule above ends the take once the room empties. Each **Extend this meeting** doubles the next wait (3,
6, 12, 24 minutes), so a long overrun stops nagging you, and the prompt raises a desktop notification too, since
you are usually looking at the meeting app rather than Diariz. A recording that ends on its own now always says
**why** - the scheduled auto-stop time, the meeting's end, or silence - whichever rule actually triggered it; a
Stop you press yourself stays silent.
- **Transcribe + diarize** server-side with WhisperX (large-v3, word-level timestamps) and pyannote 3.1,
producing speaker-labelled, timestamped segments you can rename, edit, and play back (per segment, per speaker,
or the whole recording). A **Speakers** panel lists each speaker with their segment count and **total talk time**,
plays or steps through just their segments, and reassigns them. Edits are kept **separately from the model's
original words** — a ✎ marks revised rows and a **Show original / Show revised** toggle flips the whole
transcript, so you can always get back to what the model said. Re-transcribe with a chosen model at any time
(with optional **min/max speaker hints** for pyannote when voices are merged, and an optional **spoken
language**), **merge** consecutive same-speaker rows (or set it to happen automatically for every
recording, from Preferences - Recordings, so a transcript arrives already in speaker-sized blocks), and
**email yourself** the formatted transcript. The transcript **embeds its audio** in a
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
the tiles is a summary of the calendar meeting the recording came from - title, time, location and attendee
count - with **Change** / **Unlink**; clicking the summary opens the full invite (organiser, description,
attendees, and, for a repeating meeting, the earlier recordings of the series) as a **Calendar Event** section,
the same way you open Notes or Actions. When it isn't linked yet, the card instead shows the meeting your
calendar **suggests** it came from, ready to accept in one click. It only appears when there is something to
show.
- **Notes & enhanced notes.** Take your own note lines for a meeting — sparse trigger phrases, questions,
observations. A **live notes panel** while recording stamps each line at the second you wrote it (crash-safe,
lands on the recording's **Notes** tab after upload); you can also jot **prep notes on an upcoming Google Calendar
meeting** from its preview page (they attach to the recording once it is linked). In the **desktop app** that live
panel can be **popped out into its own small window**, always on top, so it stays readable over a full-screen Teams
or Zoom call on a single monitor - the same panel, the same timestamps, the same screenshot buttons, and it keeps
working with the main window closed to the tray. Stopping the recording closes it and files the notes as usual; if
the main window goes away underneath it, it says so and stops accepting notes rather than dropping them. Timestamped lines jump to that
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
thumbnails that open a full-size viewer with previous/next, a position counter, a full-screen toggle,
jump-to-moment, download, and delete; **zoom and pan** (mouse wheel toward the pointer, a zoom cluster,
double-click, keyboard shortcuts, drag once zoomed in) let a dense capture be read at native resolution and
scrolled around instead of downloading it. The Notes tab also lists a recording's captures in a collapsed
Screenshots section. A note or screenshot sitting between two turns by the same speaker now stops those
turns from being merged past it.
- **Auto-capture (desktop app).** A sticky toggle beside the capture buttons: while it is on, Diariz watches
the capture area and takes a screenshot **every time the screen settles on something new**, which captures a
presentation slide by slide without anyone touching the keyboard. It watches once a second and only keeps a
frame once the content has held still, so a mouse cursor drifting over a slide, a blinking caret, a
half-drawn animation, or a video playing inside the deck do not produce captures - an embedded video simply
yields nothing until the deck settles again. Going **back** to a slide already captured does not file a
second copy of it. Each capture is timestamped with the moment its slide **appeared**, not the moment it was
confirmed, so it lands in the transcript beside the sentence that introduced it. Auto-capture pauses with
the recording, stops on its own when the meeting reaches its screenshot limit or the captured display goes
away, and always ends with the recording - it is never remembered into the next one. Manual captures, the
hotkey and the tray item all keep working while it runs.
- **User API access.** When a Platform Administrator enables it (Settings → Integration), generate a **personal
API token** (Preferences → Integrations) to call the Diariz **REST API** directly as yourself — acting with your own
permissions, over your own data — and browse a **built-in API reference**, opened as a panel from Preferences -> Integrations or Settings ->
Integration (and still routed at `/developers/api` for a bookmarked address). The
reference documents **every endpoint individually**: what the call does, who may make it, what it changes, and the
things worth knowing before calling it (which actions overwrite hand-edited text, which are permanent, which run in
the background and need polling, and which errors mean what). Each
token can be minted **read-only or read-write** and given an optional **expiry date**, so a token pasted into an
external tool can be least-privilege and time-boxed; a pre-existing token keeps its original read-write, no-expiry
behavior.
- **Automations (outbound webhooks).** When a Platform Administrator enables it (Settings → Integration), register
outbound webhooks from **Preferences → Integrations**: pick which events fire it (recording created, transcription
finished or failed, a summary / meeting minutes / action items / tags ready, formula run finished or failed), paste
your tool's webhook URL, and send a test event. The four **AI-output events each carry what they produced** - the
summary text, the minutes Markdown, the extracted actions, the tags - so a workflow acts on the result directly
instead of triggering on transcription and polling to find out whether the model had finished.
Every recording event also carries an **`attendees`** list: one entry per speaker, ordered by diarization label,
with the name shown on the transcript, the **person** they were identified as, their **job title**, **company**
and **internal/external** marker - enough to route on without calling back for it. An unidentified speaker
carries a null person and a null internal flag (nobody has said), and a "Multiple Speakers" slot carries no
person details at all, since it is overlapping audio rather than one human. Someone who has **opted out of
voice-printing still appears by name**: opting out concerns holding their voiceprint, not the fact that they
attended.
Attendees' **email addresses and phone numbers are opt-in per automation** (**Include attendee contact
details**, off by default; on an n8n-created automation the equivalent option lives on the Diariz Trigger
node, which owns the subscription and re-applies it on every publish). An automation posts to an arbitrary URL, so without that gate every event would fan
the directory's contact details out to whoever owns it; when it is off those keys are **absent** from the
payload rather than null, so a receiver cannot mistake "not permitted" for "not known". Each
delivery is a **Standard Webhooks-style signed** POST (HMAC-SHA256 over the exact payload bytes, timestamp and
delivery-id headers) so the receiver can verify authenticity; failed deliveries are **retried automatically with
backoff**, deliveries to a single automation are **rate-limited per minute** and a `429 Too Many Requests` is honored
(rescheduled after its `Retry-After`, not counted as a failure) so a busy endpoint is neither hammered nor wrongly
disabled, and a subscription is **auto-paused** after repeated genuine failures so a broken endpoint doesn't loop forever. A
per-automation view shows recent deliveries and their status. A single **Pause / Resume** button stops and restarts
deliveries without deleting the subscription - deleting one discards its signing secret and forces the receiving end to be
reconfigured, so pausing is the reversible way to go quiet; resuming also clears the failure count, which is how an
auto-paused automation recovers, and a card distinguishes "Paused - check the URL" (the server gave up) from a plain
"Paused" (the user's own choice). This personal scope (a subscription only sees its own
owner's recordings/formulas) sits alongside the platform scope described next.
- **Workflow Signals and platform automations.** A Platform Administrator (Settings → Integration → Workflow Signals)
defines named signals - a label, a description, and an immutable routing key - then wires a **platform automation**
(Settings → Integration → Platform Automations) to one or more signals: a webhook URL that fires for every user,
not just its creator. A formula author opens the formula editor and, under "When this finishes, trigger:", picks
one of the admin-defined signals - no URL, no per-user setup. When that formula runs, the publisher matches the
signal against every platform subscription whose signal filter includes it and delivers the event, **including the
formula's rendered output inline in the payload** (personal-scope deliveries never carry the output, only platform
signal-routed ones do). An empty signal filter on a platform subscription deliberately matches nothing, both at
create time and at delivery time, so a half-configured subscription can't silently fan out to every signal. A
signal's routing key can't be changed after creation; only its label, description, and active flag can be edited.
Deferred follow-ups: a per-platform-subscription delivery rate cap (the delivery worker's existing batch-and-backoff
throughput bound covers this for now), and detaching a platform subscription from the single admin who created it
(today it cascades with that admin's account).
- **n8n community node.** A published node package, **`n8n-nodes-diariz`**, installable from n8n's Settings →
Community Nodes (self-hosted n8n only). It ships two nodes. The **Diariz Trigger** is self-registering: activating a
workflow creates the matching Automation in Diariz through the REST API and stores the signing secret Diariz
returns once, deactivating deletes it again, and every delivery is verified against that secret using the
Standard Webhooks HMAC scheme - an unsigned or tampered request is answered `401` and starts no execution. It
covers all nine personal events, and a **Scope** setting switches it to **Platform (Administrator)**, where it
registers through the admin webhook endpoint instead: events across every user, routed by a Workflow Signal
picker, plus the platform-only **Feedback Received** event that fires when someone submits feedback through
Provide Feedback. Feedback is readable only by a Platform Administrator, so it is never offered on a personal
subscription; an **Include Feedback Text** option, off by default, adds what the person actually wrote. Scope
defaults to Personal, so an existing workflow is unchanged. The **Diariz** action node exposes every published REST operation (179 across 31
resources), generated from the platform's own OpenAPI document so its operation names and help text are the same
copy as the in-app API reference, plus a **Custom API Call** on every resource for anything added later. On top of
the generated surface, high-value operations are given real n8n ergonomics: **dropdowns** listing your actual
recordings, folders, rooms, formulas, speaker profiles and meeting types instead of raw IDs; **binary transfer**
for transcript exports, audio, attachments and formula documents in both directions; **Return All / Limit** on
every list; **Wait for Completion** on a formula run (Diariz answers `202` with a document still generating, so
the node polls until it settles rather than handing back a stub); and chat questions accumulated from the
server-sent event stream into a single finished answer with citations. The package is **MIT-licensed and free of
runtime dependencies** to meet n8n's verified-node requirements, and it versions independently of the platform.
It lives in the platform repository (`integrations/n8n-nodes-diariz`) so CI can regenerate its operations on every
build and fail if the API has moved underneath it. The `Auth` endpoint group is deliberately excluded: it takes an
account password and the node authenticates with a token.
- **Integration toggles.** A Platform Administrator can independently switch **API access**, **Claude/MCP**, and
**Automations** (webhooks) on or off from Settings → Integration - each surface is gated behind its own toggle, so
turning off webhooks, say, does not disable a user's API tokens or the MCP connector. API access and Automations
default off; Claude/MCP defaults on so an already-connected MCP client is not broken by the upgrade.
- **Status bar** locked to the bottom of the app: left-aligned live progress (transcribing, summarising,
merging, extracting actions, uploading, errors — in their tone colours) and right-aligned storage usage ·
transcription usage · total transcripts.
- **People and speaker identification.** The people who appear in your meetings live in one **platform-wide
directory**: one human is one record, however many people have recorded them, which is what makes an erasure
request a single deletion rather than a hunt through every user's private set. A **voiceprint is optional** -
someone can sit in the directory with no biometric held for them at all, and a person can be **opted out**,
which erases the voiceprint they have and stops them being matched from then on. Enrol a person from a
recording's speaker and Diariz recognises that voice automatically in later recordings (SpeechBrain ECAPA
voiceprints in pgvector, cosine matching), with manual reassignment. The **Voice Prints** tab (Preferences)
renames, prunes training samples, merges duplicates, and erases voiceprints (GDPR — biometric data).
**Browsing** the directory, and editing, deleting or merging anyone other than yourself, needs the
**Manage people** permission; labelling a speaker does not, and **opting yourself out never does** - under
GDPR, withdrawing consent to hold your own biometric is yours to exercise, not an administrator's to grant.
The trade-off of a shared directory is deliberate and worth stating plainly: a voiceprint enrolled by one
person will identify that human in **everyone's** recordings.
On a recording's Speakers tab, hovering an identified speaker's **Internal**/**External** marker shows their
full details, and selecting that speaker puts a **contact card** above their segments - the only place inside
a transcript where their **email address and phone number** are reachable, both as links. With **Manage
people**, a **pencil** on the row edits that person's record in place - the same editor the directory uses,
so one correction serves everywhere. Suggested duplicates
carry a **Dismiss** control that hides one for the current visit only; nothing is recorded, because a pair
dismissed today becomes a genuine duplicate as soon as a missing email address is filled in.

The **People** directory (account menu, or *Manage people* on a recording's Speakers tab) opens as a
window **over** what you are reading rather than navigating away, because the usual reason to open it is a
question about the transcript in front of you. It lists everyone one to a line - name plus a marker for
whether a voiceprint is held - with a search box matching **name, email address or company** and filters for
internal, external, and voiceprint. Selecting someone opens an editor beside the list; only the list
scrolls, so the editor never moves while you are typing in it. The Speakers tab on a recording shows an identified speaker's **title, company** and
**internal/external** marker inline, so you can see who was in the meeting without leaving the transcript.
A person carries a **job title, company, email address, phone number** and an **internal/external** marker
alongside their name, and Diariz reports entries that look like **duplicates** of each other - by email, or
by name once case and spacing are normalised - for a human to merge. It never merges on its own: a merge
deletes the source record, cannot be undone, and in a shared directory affects everyone's recordings.
The directory has its own REST surface at **`/api/people`** (replacing `/api/speaker-profiles`), including a
**search** endpoint that deliberately needs no permission, so finding someone in order to name a speaker in
your own recording is never gated.
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
table, the timestamped transcript table, your notes, …), **instructions to the model**, or a **horizontal rule**.
Each block has a **Break-after**
control (no break / line break / paragraph) so you decide exactly where content runs together or separates - the
two table-valued fields always get a blank line either side regardless, since a glued table stops rendering as
one; text
blocks are an **auto-growing Markdown** box, and a **drag handle** moves any block within a section or into
another. A section can also be **headless**, which is what a formula that is simply one instruction looks like.
The templates Diariz ships with are **plain markdown files** in the repository, so you can read and review the
exact words the model is given.
When a template substitutes the **attendees** field it names the identified people and then counts the rest
(e.g. "Alice, Bob and 11 unidentified attendees"). A Platform Administrator can also pick how minutes generate:
**one LLM call per section** (best structure) or a **single call** (fewer tokens).
- **Chat across one or more transcripts — a folder — or all your meetings at once** (an "All meetings" mode
that searches your whole library on demand instead of pre-loading transcripts) — streaming
replies, a context-usage dial, PDF/text attachments, and saved conversations — via the **platform's**
OpenAI-compatible LLM endpoint (see **AI models** below), with the API key encrypted at rest. The chat's context is
**inferred from what you're viewing** rather than picked from a list: the label reads **Current Transcript**,
**Current Folder**, or **Selected Transcripts** (2+ ticked in the list) and updates when you click into the
box. When a **folder** is open, chat is about that folder — its roll-up **summary, minutes, and aggregated
actions** are the context, and "Include attachments" pulls in every attachment across the folder and its
sub-folders.
  **Choosing a model.** Where an administrator has marked more than one model as available for chat (see
**AI models** below), a sparkle button beside the context dial opens a picker listing each one by its
display name with its context window in brackets. The choice can be changed **part-way through a
conversation** - chat is stateless per turn, so the whole conversation is resent and the new model picks up
where the previous one left off. The dial follows the choice immediately rather than waiting for the next
reply, so it always reports the window of the model that will actually answer. The choice is remembered
between visits and stored with a saved conversation; if the model is later withdrawn, the conversation falls
back to the platform's chat model rather than failing.
- **Search the panel** - a search box sits above the meetings list. Typing takes the list over with results and
clearing drops you back exactly where you were browsing. It searches the **folder you are in** by default (the
chip tells you which), and each hit shows the matching words in context, the folder it lives in, and clicking
it opens the transcript **at that moment**. Folders whose name matches appear too, and take you straight there.
**Search everywhere** (next to the result count) widens the search to **every room you can see**: the chip
switches to *Everywhere*, results are **grouped under the folder** each meeting lives in (coloured to match,
with a count), and **Folder / Date / Speaker** chips narrow them. The chip options are built from the results
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
to copy), or generate a **personal access token** in **Preferences → Integrations** and paste the URL +
token into Claude Desktop or Code. Either way Claude uses the same built-in tools (search / who-said-what /
action items / summaries / attendees / talk time / …, plus email-to-self) to answer grounded in your
transcripts. Per-user and secure: tokens are shown once and stored only as a **SHA-256 hash**, work only for
your own recordings, and both **tokens and web connections are revocable** any time in the same Preferences
section. A Platform Administrator can turn the whole surface off from Settings → Integration (the **Claude/MCP**
toggle, on by default so it never breaks an already-connected client); when it is off, the `/mcp` endpoint and
any `dz_mcp_` token stop authenticating.
- **Formulas: build a document, run it over a recording.** A **Formula** is a **template** plus a chosen
**context**. The template is built from blocks - **headings**, **literal text**, **substituted meeting details**
(date, time, title, attendees, duration, the action-items table, the **full timestamped transcript** as a
Time/Speaker/Text table, your notes), **instructions to the model**, and horizontal
rules - so it produces a properly laid-out document rather than whatever shape the model felt like. A
substituted detail is stamped in deterministically and never enters a prompt, so a transcript appendix costs no
tokens and is independent of what the formula lets the model read. The context
(any mix of transcript, notes, summary, minutes, and action items) is what the formula is allowed to see. Run it
over a recording to generate a named **Markdown Result** — open it, edit it in the same rich editor as minutes,
download it as `.md`, or email it to yourself. A formula that is simply one instruction is just a template with
one block, so nothing has to be more complicated than it needs to be. Formulas come in three scopes: Diariz-provided **starter formulas** seeded on every install
(Follow-up email, Meeting recap, Decisions & risks, Tone & sentiment read), **Platform-wide** formulas shared
with everyone, and your own **Personal** formulas — create and edit these in **Preferences → Formulas**. A
recording-level **Formulas tab** lists every formula you can use, and a matching **Formulas tab on any folder
page** runs the same formula over **every meeting in that folder and its sub-folders** (a map-reduce:
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
- **Organise** recordings into **folders nested up to 8 levels deep** (Customers > Acme Corp > Project Falcon)
  with drag-and-drop
ordering and cross-group moves; select a whole level at once to build chat context. Dragging a recording that
is part of a **multi-selection moves the whole selection**, in the order the rows are shown rather than the
order you ticked them; dragging an unticked row moves only that row. The list **drills in
one folder at a time** rather than showing every folder expanded: a folder row shows its colour, the count
of everything inside, and takes you in; the header above the list is a **breadcrumb showing the whole path**,
collapsing the middle when it does not fit, with every part clickable and a trailing menu for the full
ancestor chain, with a page button beside the menu opening the folder's own page - browsing deeper and opening the page stay separate targets, and
crumbs accept a dropped recording to move it up (appending it, the same as dropping onto a folder row).
Folder pages carry the same path. An **open recording shows where it is filed** as a row of chips under its
name - the room, then each folder down to the one it sits in - and clicking any chip takes the list straight
to that folder without closing the recording (from the Calendar, Actions or Tags tab it switches back to the
list first). A deep path collapses in the middle the same way, and a recording shared into several rooms
shows its folder in the room you are currently viewing, since it is filed independently in each. Recordings
with no folder simply sit at the top level, and show just the room chip. A **Sort by** control on the search
line orders the level you are looking at: **Manual** (the order you arranged by hand, the default),
**Date/Time**, **Name** or **Duration**, each **ascending or descending**, and your choice is remembered
between visits and applies in every folder and room. Sorting only changes what you see - the manual order is
left untouched underneath, so switching back to Manual returns the list exactly as you left it. Folder rows
always keep their manual order (a folder has no duration or date of its own), and while a sort other than
Manual is active **dragging rows to reorder them pauses**, since the list is no longer showing the order a
drag would rewrite; dragging into a folder still works throughout. Each row's right-hand column shows **when
the recording was made** - "Today 14:30" for today, "11 Aug 14:30" this year, with the year added beyond it -
and the duration moves to the row's hover tooltip. Browse them as a **list or a calendar**
(days with recordings are highlighted; click one to see that day on an hour-by-hour timeline). You can also **cut and
paste** to move several things at once: select recordings and click **Cut** in the toolbar, or **Cut** a
single folder from its own menu, then drill into wherever you want them and click **Paste**. A bar under
the toolbar names the destination and carries the Paste and Cancel buttons; cut rows grey out with a dashed
outline and stay put until you paste, so nothing moves until you say so. Paste stays visible but disabled,
with the reason shown, when the move is not allowed yet: pasting back where you cut from, into a shared room
(personal rooms only for now), past the 8-level depth cap, or into a folder's own descendant. Pasted items
land at the bottom of the destination, keeping the order you cut them in.
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
folders in it, nested up to 8 levels, file recordings into them (via the recording's Move to folder action or by
**drag-and-drop**), and **drag to reorder** recordings within a folder - each room keeps its own order, separate
from your Personal Room. **Your Google Calendar stays personal**: a Shared Room's Calendar tab shows only its
recordings (no personal-event overlay), and a recording opened inside a Shared Room hides its linked meeting and
offers no calendar linking. The **List, Calendar, Actions
and Tags** tabs all work in a Shared Room too, each scoped to the recordings shared into that room. **Recording or uploading a file while a shared room is open** files the
meeting into that room automatically, while the original stays in your Personal Room - so a shared room can only ever **unshare** a
recording, never delete it. You can also **Share to room** an existing recording (or **Remove from room**) from
its toolbar; the recording's Overview shows a **Rooms** line (home room first) and a **Recorded by** line, and
Delete only appears in the home room (its confirmation names the shared rooms it will also vanish from). A room
member who can read a shared recording sees its **notes and screenshots** too, woven into the transcript exactly
as the owner sees them - only the owner can add, edit, or delete them. **Chat and
the Claude (MCP) tools search across every room you belong to**, so a meeting shared into a room you are in turns
up in your searches. Deleting a user **keeps** their shared recordings and **orphans** their Personal Room rather
than destroying its history. Voiceprints, saved chats and meeting-type templates are room-scoped too.
- **Where new recordings land.** A **Recordings** tab in Settings chooses how a fresh recording is filed in
your Personal Room: **Ungrouped**, the **folder you currently have open** (the default), or a **specific
folder** you pick. When you press Record, the take is filed accordingly the moment it finishes uploading -
no manual move needed.
- **Folder pages.** Open any folder as a **first-class page** - the same layout as a recording
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
- **Tag cloud across your meetings**: tags are yours to add. After transcription, the LLM extracts up to 12
weighted concepts/themes a meeting was actually about (participant names and filler are excluded) and offers
them as **suggestions** on a Tags pill on the meeting - pick one to adopt it, or dismiss it if it is not
useful, or type your own tag directly. Only **adopted** tags count: the left panel's **Tags** tab shows them
as a flat weighted cloud (font size scales with how central a topic is across your library); click a tag to
list the meetings that carry it, and an **expand** button opens the cloud in a large modal (picking a tag
there filters the panel too; picking a meeting opens it). Re-transcribing refreshes a meeting's suggestions
only - tags you already adopted or dismissed are left alone. Existing libraries are **backfilled** with fresh
suggestions automatically at startup (when a server-wide LLM is configured), and a Platform Administrator can
trigger the backfill from **Settings → Maintenance** (e.g. for per-user-only LLM configs); a suggestion still
has to be adopted before it joins the cloud. You can always tag your own meeting; tagging someone else's in a
shared room needs the room's **Edit or regenerate other people's recordings** permission.
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
rooms, manage users, manage platform, manage formulas, manage people. A user's permissions are the union of
their groups', re-read on every request, so adding or removing someone from a group takes effect immediately
rather than at their next sign-in. Two groups ship: **Platform Administrators** (everything; it cannot be
deleted and its last member cannot be removed) and **Administrators** (the other four, but not
backup/restore or platform settings). Around that sits an
access-request → admin-grant → account-setup lifecycle (one-time email link, with an in-app fallback when
SMTP is unconfigured). Each user's data is isolated to them. Light/Dark/Auto theming.
- **The Users & access console**: all of the above is administered from one place (account menu → Users &
access), in the shape every admin tool uses - a list on the left, everything about the selected thing on the
right. **Users** carries a search over name and email plus status chips that show their own counts, so
"how many accounts are still awaiting setup?" is answered without a click; select someone and one panel holds
their groups, quota, storage, status and the enable/delete controls. That panel answers the question the old
table could not: a **Grants** line saying in plain language what the person's group memberships actually let
them do (`Grants: manage rooms, manage formulas, manage the People directory.`) - both halves were always on
the wire, but nothing joined them. **Groups** lists each group with its permission and member counts; its
detail pane gives every permission a **sentence** rather than a column heading, edits membership in place, and
sets a group's name, description and colour. **Requests** is its own tab carrying a badge while anyone is
waiting. The Platform Administrator and your own account state *why* they cannot be disabled or deleted
rather than silently offering no controls.
- **Sign in with Google** (optional): OAuth 2.0 sign-in that reads the user's name, email, and profile
picture (shown in the account menu). New Google sign-ups still require admin approval, and a Google email
matching an existing account links to it automatically. Enabled when the operator configures a Google
OAuth client. It works in the web app and in the **desktop app** (the desktop client runs consent in your
system browser and returns you to the app, since Google blocks sign-in inside embedded windows). A Google-linked user can opt in
(Preferences → Calendars) to let Diariz **read their Google Calendar** (read-only) so a recording is **linked to
the meeting it belongs to** (auto-saved on open, or picked by hand when the times don't line up), its Overview
shows a meeting-card summary (title, time, location, attendee count) that opens the meeting's **full details**
(organiser, attendees, description) as a **Calendar Event** section, with any **URLs in the location/description
rendered as clickable links** (so you can join a Zoom/Meet call straight from the app), and the **Calendar
tab overlays their meetings** (a linked recording and its meeting show as one block; a meeting with no recording
opens a preview you can link a recording to) - a revocable grant. They can also **choose which of their Google
calendars** to consider (Preferences → Calendars); only the selected calendars are used for matching and
shown on the Calendar tab.
- **The Calendar tab's day view is a time grid**: pick a day in the month grid and it is laid out on an hour
axis, with every meeting and recording placed and sized by when it actually ran, so the back-to-backs and the
gaps are visible at a glance. Meetings that clash sit **side by side** (up to three across, then a `+N` chip
that opens the rest); **all-day entries and meetings that began on an earlier day** sit in a strip above the
axis rather than being drawn at midnight; and on today a **red line marks the current time** and moves as the
day goes on. The grid opens on the working day (an hour behind the clock when the day is today) and stretches
past its usual 06:00-23:00 window whenever something falls outside it, so a meeting can never be off the edge.
A block does what its row did — a recording opens its transcript and keeps its full actions menu, a meeting
opens its invite — and a block too short for two lines puts its title and time on one. The calendar a meeting
came from is shown in the block's tooltip. A **Go to today** button sits on the panel toolbar whenever the
Calendar tab is open (in shared rooms too, unlike the two syncs beside it): it returns the selected day and
the month grid to today in one click, which matters most after you have paged the grid forward - the day was
already today, so only the grid had moved.
- **Two calendar syncs, in the panel toolbar**: **Sync calendar** refreshes every source you have connected in
one go — Google, subscribed `.ics` feeds and a mirrored desktop Outlook calendar — touching only the ones you
actually have, and **Sync selected day** does the same for a single day - whichever day you have picked in
the calendar, so looking at next Tuesday and pressing it refreshes next Tuesday rather than today. The status
bar names the day it is reading. The quick one exists because a full Outlook read is tens of seconds on a busy
mailbox where a single day is a couple: it is what you press when a meeting you have just accepted is not on
the calendar yet. While either runs, the **status bar counts the
seconds** and names which sync is going, so a long one cannot be mistaken for a button that did nothing. Both
work in a plain browser, where a sync is simply a re-read of Google and your feeds. Either also re-reads the
**recordings** drawn alongside the meetings, so one button refreshes everything the day view shows - which is
why the generic **Refresh** button is not offered on this tab, only on the tabs where it is the only refresh
there is. (The two syncs replace the *Sync Outlook* and *Refresh events* links that used to sit under the
month grid.)
- **Subscribe to external calendar feeds**: add any public iCalendar (`.ics`) URL — a team or shared
calendar — in **Preferences → Calendars**, give it a name and colour, and its meetings appear on the
Calendar tab in that colour (fetched behind an SSRF guard, no Google connection required). Feed meetings are
**first-class**: a recording is matched and linked to one exactly as it is to a Google meeting, and opening one
shows its full invite details. (Both used to be Google-only, so a user whose calendar was entirely feeds got a
populated Calendar tab but no matching at all, and a feed meeting's details would not open.)
- **Mirror a desktop Outlook calendar**: opt in at **Preferences → Calendars** to copy the calendar from
**classic Outlook** on a Windows PC into Diariz, so those meetings behave like any other — on the Calendar tab,
matched to recordings, carrying pre-meeting notes — and keep working in a browser and once the desktop app is
closed. Off by default, and the page states what is stored (titles, times, locations, attendees, invite text)
before it is switched on. Each machine is listed separately with its mailbox, meeting count, last sync and last
failure, so a connector broken on one PC is visible from another or from any browser; per machine you can
rename it, hide it without disconnecting, set how many days back and ahead it reads, skip private appointments
(on by default), and exclude invite text. **Disconnecting a machine deletes the meetings copied from it, and
turning the opt-in off clears every machine** — both confirm first. Managing it works from any browser; the
syncing itself runs from the Windows desktop app, which reads the calendar **on launch**, from its **tray
menu**, from the Calendar's **Sync calendar / Sync selected day** buttons, or from **Sync now** in Preferences.
Reading happens in a small bundled helper program rather than inside the app, so a slow or failing calendar
read cannot freeze recording, the tray or the screenshot hotkey; Diariz never closes an Outlook you had open,
though COM will start Outlook if it is not running. Each failure is named specifically — Outlook not installed,
Outlook busy, access blocked, or **the new Outlook, which does not expose a calendar to other apps at all** —
and recorded against that machine so it is visible from elsewhere. Requires **classic Outlook for Windows**.
On a PC that has Office but **not** classic Outlook, Diariz works out that it is missing by **reading the
registry**, never by trying to start it — starting it is what made Windows pop up an *install Outlook* dialog,
on every launch, because a sync runs at startup. That answer is **remembered**, so nothing tries again; if you
install Outlook later, **Check again** on the Outlook card in Preferences is what tells Diariz to look.
- **Recurring meetings are marked.** A calendar event that repeats - from Google, an `.ics` feed, or the
mirrored Outlook calendar - carries a **Repeats** badge, and both the event and a recording linked to it list
your **earlier recordings of the same meeting** (up to 10, newest first), so you can jump straight back to the
last one instead of hunting for it in the folder tree.
- **AI request timeout**: a platform default (Platform Administrator, Settings → Model Settings), in seconds
(default 120), applied to every AI call - summaries, minutes, actions, tags, embeddings, chat, and Formula
runs. Any user can override it for their own account (Preferences → Assistant → Change model); the resolved
value - their override if they set one, else the platform default - is the single authority for that call,
with no hidden HTTP cap underneath it. Raise it for a slow or large local model.
- **LLM usage logging** (Platform Administrator, Settings → Model Settings): a master switch that turns
capture of every outbound LLM call on or off, a retention window in days for how long the captured rows are
kept before a nightly sweep deletes them (0 keeps everything), and a toggle to request token counts on
streaming calls when the endpoint supports it. Captured rows hold only counts, sizes and identifiers - never
prompt or completion content. A Platform Administrator can browse the captured log at `/admin/llm-usage`
(also linked from the Model Settings tab): three views - **Operations** (one row per user-facing action,
with its turn count), **Calls** (every individual model call), and **Summary** (rolled up by any combination
of user, model, and call type) - filterable by date range (last 7 days by default), user, call type, model,
and outcome, with server-side sorting on every column. A totals row covers the whole filter, not just the
visible page - calls, operations, duration, and prompt/completion/reasoning/total tokens - and each token
total states how many of the calls in scope actually reported that figure, so a partial measurement is
never shown as a complete one. Every line carries its own generation rate in tokens per second, alongside
the platform-wide rate in the totals row, so a single slow operation is visible without averaging it away;
an operation's rate is measured against the time the model actually spent, not the wall-clock span, which
for a multi-call turn includes the gaps between calls. Rows matching the current filter can be deleted, with a confirmation stating
the exact count before anything is removed.
- **Truncated replies are visible.** Every call records the model's `finish_reason`, and the usage log
shows a **Cut off** badge on any row where a token cap ended the reply. This matters because such a call
does not fail: it returns success, every token is billed, and the answer comes back short or completely
empty - so without the badge it is indistinguishable from a model that had nothing to say. It is easiest to
hit on a reasoning model, where the reasoning is spent before the answer and a seemingly generous cap can be
consumed before a single word of the reply is written. The outcome stays **OK** beside the badge, because
the call genuinely succeeded; truncation is a separate signal, not a kind of failure. An operation is
flagged when any of its calls was cut off, so the default view shows it without drilling in.
- **AI models** (Platform Administrator, `/admin/llm-models`, reachable from Settings -> AI): every model the
platform calls is configured here, and nowhere else. A model carries its **name** (sent verbatim as the
`model` in each request), an optional **display name** (what users see in place of that name - blank means
the name is shown), **endpoint**, an optional **API key** (encrypted at rest, write-only - it is never
returned to the browser once saved) and its **context window**, which is what the chat context dial reports
against. Every sampling parameter is then exposed per model: **temperature**, **top P**, **top K**,
**repeat penalty**, **frequency** and **presence penalties**, **max tokens**, **max completion tokens**,
**reasoning effort**, the request **timeout**, and whether the model supports **tool calling** or **image
input**. Each parameter is one of three states - **inherited** (the layer below decides), **omitted** (the
key is left out of the request entirely, which some endpoints require) or a **value** - and omitted is
genuinely different from inherited: it suppresses what a lower layer would otherwise have supplied. The
value box IS how a parameter is set; the two controls beside it return the row to inherited or omit it, and
the row states which state it is in ("overridden here", or what it inherits and from where) rather than
leaving it to be inferred from which of three buttons is filled in.
  **Routing is a grid.** The models run down the side, the seven call types across the top, and every column
carries exactly one selection - clicking a cell moves that call type to that model. A final **No model** row
is where a call type goes to *follow* the default rather than be pointed at a model, and where the default
itself goes back to the endpoint configured in the server environment. The distinction is load-bearing: a
call type following the default moves with it when the default changes, while one assigned to the model that
happens to be the default stays where it was put.
  **Add all from an endpoint.** Beside **Add model** sits **Add all**: give it a server address and an
optional key, press Discover, and it lists the chat models that server reports. Embeddings, speech
recognition, rerankers and the like are filtered out - by the type the server declares where it declares one
(LM Studio's own listing does), otherwise by name. Models already configured are shown but cannot be picked
again, so it is visible that the server has them rather than looking as though they went missing. Nothing is
created until the confirm button, which names how many are ticked. Where a server does not report a model's
context window - the plain OpenAI-compatible listing never does - the row says so and the model is created
at **16,384**, enough to be useful and flagged as a guess rather than a measurement, since that number sizes
both the chat dial and how much of a meeting is actually sent. Imported models are **not** offered in chat
until they are ticked.
  A final **In chat** column sits beside the grid, and it is checkboxes rather than a dot: it marks which
models the chat model picker offers, and any number may be ticked. It does not change routing - the Chat
column still decides which model answers when the user has chosen nothing. The model that column points at
is ticked and locked, because it is the model in use and a picker that could exclude it would be unable to
show the current selection.
  The editor is a right-hand **drawer** with a tab per call type, each showing how many parameters it
overrides, and the thirteen parameters in two columns. Connection details (name, endpoint, key, context
window) sit behind a **Connection** button, since they are set once when a model is added. A panel beside
the parameters previews the **exact request body** that call type would send, updated as values are typed -
the resolved layer walk, with omitted parameters absent and inherited ones carrying their inherited value.
The timeout, tool-calling, image-support and send-reasoning flags are deliberately shown *outside* that body:
they govern Diariz rather than the request, and no endpoint ever receives them.
  Parameters can differ by **what the model is doing**. Alongside the model's own defaults, each of **tag
extraction**, **action extraction**, **summaries**, **minutes and formulas**, **translation** and **chat**
takes its own optional overrides, resolved most-specific-first: the call type's override, then the model's
defaults, then the application defaults. **Reasoning effort is free text**, not a fixed list, because models
disagree about what they accept (gpt-oss takes low/medium/high, qwen3 also takes xhigh). A whole parameter
set can be **copied from another model** as a starting point - all seven layers at once, parameters only,
never the name, endpoint or key - and **Reset all to inherit** clears the open tab alone. Each call type can
also be pointed at a **different model** entirely, with a **default model** serving anything unassigned; a
model still in use cannot be deleted until the call types pointing at it are moved, and **Delete model**
lives at the foot of that model's drawer.
  The application defaults themselves are readable by the editor (`GET /api/admin/llm-models/defaults`), which
is what lets an inherited row name the value it inherits even on the Defaults tab, where the layer below is
the application's baseline rather than the model's own.
  **Run test** (`POST /api/admin/llm-models/{id}/test`) sends one fixed sample call with the parameters
currently on screen - saved or not - and reports **time to the first token**, **total duration**, **tokens
per second** (completion tokens over duration, as in the usage log), the token counts, and the model's actual
reply. Each call type keeps its own result, since they ran with different parameters. First-token time is
separated from duration deliberately: together they distinguish a model that was still LOADING from one that
is simply slow, which need opposite fixes and otherwise look identical.
  On a failure the card carries the endpoint's own words plus **the single change that would address them**:
a timeout offers to raise the timeout, and an endpoint that rejects a parameter by name offers to **omit**
that parameter - for the open call type only. That is what makes the omit state discoverable: it is the least
obvious of the three, and the moment it is needed is exactly the moment an endpoint has just refused a
parameter by name.
  The grid's per-row **Test** and footer **Test all** answer the coarser question - is this endpoint, key and
model name reachable at all - running one model at a time, because several models commonly share a server and
testing them together would measure that server's queue. The request carries no endpoint, key or model name:
those come from the stored row only, so the endpoint cannot be used to reach a host that has no model row.
Test calls are logged like any other, as the **Model test** kind.
  Every result offers a way out of the drawer: **Copy as cURL** puts the exact request that ran on the
clipboard (with the key as a `$LLM_API_KEY` placeholder - the browser is never given the stored key, and
naming the placeholder rather than dropping the header makes a forgotten substitution fail for an obvious
reason), **Raw JSON** copies the whole result, **Open in usage log** deep-links to that model's test calls,
and a failure offers **Retry**.
  Both this page and the usage log open as **panels over Settings** rather than their own tab. They are
still routes (`/admin/llm-models`, `/admin/llm-usage`) so a bookmarked or pasted address works, but the
in-app path never navigates: opening a new tab is fine in a browser and wrong in the installed PWA and the
desktop shell, where it leaves Diariz for the system browser and an unauthenticated session.
  The application defaults are overridable per deployment through `LlmDefaults__*` environment variables
(e.g. `LlmDefaults__Temperature`, `LlmDefaults__Translation__Temperature`), and the shipped values reproduce
the request bodies Diariz sent before this was configurable. A server with **no models configured** keeps
using the endpoint from its environment (`SUMMARY_API_BASE` and friends) unchanged, and the page offers to
import that endpoint as the first model.
- **Preferences**: a tabbed window with the everyday entries (Profile, Recordings, Formulas, Calendars) over an
**Advanced** divider holding the exception settings (Integrations, Assistant).
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
one. Both report progress while they run: the panel shows the archive being built (the stage it is on, how
many files are in so far, and elapsed time) before the download begins, and during a restore it distinguishes
uploading the archive from the server applying it. Restore is destructive (replaces all data) and accepts a
backup from this app version or an older, forward-migratable one.
- **Provide Feedback.** Any signed-in user can open **Provide Feedback** from the account menu and describe
something that looks or behaves wrong, even when nothing raised an error. A short technical trail of recent
app activity (API calls and route changes, scrubbed of anything sensitive before it ever leaves the browser)
is attached automatically, so a maintainer has reproduction context without a screenshot. The dialog is
draggable, so it can be moved out of the way of whatever it is reporting on. Submissions are readable and
deletable only by a **Platform Administrator**, in a **Feedback** tab in Settings - not even the submitter can
read their own back. A platform-scoped `feedback.submitted` webhook event lets an automation react (route it
to a ticket system, for instance); the submitter's own words are included only when that platform subscription
is explicitly configured to include them (`IncludeFeedbackText`, off by default), so a webhook pointed at an
arbitrary URL does not fan free text out by default. The opt-in is a checkbox on the Platform Automations
screen (Settings -> Integration), shown only once Feedback Received is among the chosen events, and an
automation carrying it is badged as such in the list - there is no edit form, so the card is the only place
the setting is visible after creation. Feedback Received is offered to **platform** automations only; the
personal picker does not list it, and the server refuses it on a personal subscription. Screenshots are not
part of this release - they need a desktop-shell change and are a deferred follow-up.
- **Install it as an app.** From a Chromium browser (Chrome, Edge), Diariz can be installed as an
application in its own right - a launcher entry and icon alongside your other apps, opening in its own
window with no tabs or address bar. An **Install app** entry appears in the account menu whenever the
browser offers it, so the small install icon in the address bar is not the only way to find it. This is
what gives **Linux** an app-like Diariz: there is no Linux installer, and system audio there is already
handled by the PipeWire drop-in (see **Capture**), so the window was the last piece missing. It works on
Windows and macOS too, though the Electron desktop app remains the fuller option on those platforms - an
installed window has no tray presence, no pop-out notes window, and no screenshots, and offline it shows
the browser's own error page rather than working offline. Firefox does not install web apps.
- **Help and documentation.** A browsable help section at **`/help`**: a fixed header, a resizable
left-hand tree of articles grouped into Getting started / Working with recordings / Asking questions /
Settings and sharing, and the selected article rendered on the right. A **search box** above the tree
ranks articles by where the query matched (title, summary, heading, then body) and shows a snippet of
the surrounding text; clearing it restores the tree exactly where you were. Every article has its own
address (`/help/<slug>`), so links into the docs are stable and the browser's back button works. The
page is **public**, like the release notes, so it can be read before signing in and opened in a new tab
without losing your place in the app.
Alongside it is **contextual help**: a small **`?`** placed next to a feature or a setting. Clicking one
opens a popover with a two-or-three-sentence explanation and a **Read more** link straight to the
matching article. The popover is rendered once at the top of the app and portalled over everything else,
so a `?` inside a settings modal is never clipped by it, and only one is ever open at a time. The short
text is the article's own `summary` field, so the brief and full explanations cannot drift apart.
Articles are **plain Markdown files in the repository** (`apps/web/src/content/help/<locale>/`) with a
small `title` / `summary` / `group` / `order` front-matter block, bundled into the web build - adding one
is a file drop with no code change. Content is **ASCII only**, enforced by a test that names the file,
line, and offending character. A second test scans the source for every `?` button and fails the build if
one points at an article that does not exist. Prose is English for now; the loader already resolves a
requested locale and falls back to English, so translations are a folder away. Reach the docs from the
account menu, the About box, or the welcome panel.
An **Advanced and admin** group covers the topics an administrator or power user needs in depth:
configuring a formula field by field (the template blocks, break-after controls, merge fields, context
toggles, sharing, and what blocks a deletion); meeting types and how one names the formula that
generates its minutes; automations and Workflow Signals, including how an admin wires a destination once
so formula authors just tick a box, and why personal subscribers get a thin payload while platform ones
get the full output; users, groups and the five platform permissions, the access-request lifecycle, and
how room permissions differ; connecting Claude over MCP for claude.ai, Claude Desktop, and Claude Code,
with the reverse-proxy requirements that cause almost every connection failure; a step-by-step guide to
the `n8n-nodes-diariz` community node; the equivalent recipe for Zapier, which is **deliberately honest
that no Zapier app exists** and shows the Catch Hook plus custom-request path instead; and an overview of
the REST API linking to the in-app reference and the OpenAPI document.
Articles can carry **screenshots**, stored beside the article (`content/help/<locale>/images/`) and
referenced relatively; the loader rewrites the path to the bundled asset, falls back to the English
screenshot for an untranslated locale, and the content gate fails the build on an image that does not
exist.
