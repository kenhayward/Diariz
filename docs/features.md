# Diariz - full feature list

This is the detailed, prose feature list. The [README](../README.md) carries an at-a-glance
two-column summary table and links here for the full detail. **Keep both in sync** (and the in-app
About-box `CAPABILITIES` summary in [`apps/web/src/lib/appInfo.ts`](../apps/web/src/lib/appInfo.ts))
when the app's scope changes - see [CLAUDE.md](../CLAUDE.md).

- **Capture** audio from the browser microphone — **choose a specific input device** (the choice is
remembered, and the list refreshes on hot-plug), **tune capture** (echo cancellation, noise suppression,
auto gain, mono) from a ⚙ popover, and watch a **live input-level meter** while recording (with a subtle
silence hint). **Pause and resume** a recording in progress (separate from Stop) for breaks or sensitive
moments — paused audio is never captured and never counts toward the recording's duration. Recording is **sent to the server while the meeting runs**, in pieces, rather than all at once when you stop. The recording appears in your list the moment you press **Record** and fills in as you talk, so at any point what has already been said is safe on the server rather than only in the browser: a crashed tab, a sleeping laptop or a window closed by mistake costs at most the last few seconds instead of the entire meeting, and a capture whose window disappears is finalised on its own from everything that arrived. Nothing about the controls changes - press Record and Stop exactly as before - but stopping is quick even after a long meeting, because the audio has been arriving all along instead of going up in one push at the end, and a long recording no longer accumulates in the browser tab as you go. If the server cannot be reached when you start, or a chunk never gets through, the recording falls back to being uploaded whole at Stop exactly as it used to, with the status bar counting up ("Uploading... 4s") until it is safely up. **Schedule the
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
opens the meeting link and starts recording in one click. Diariz recognises the major conferencing services by
name - Teams (both the long meetup-join links and the short teams.microsoft.com/meet ones, on work, personal and
government tenants), Zoom, Google Meet, Webex, Whereby and GoToMeeting - which matters because services do not
agree on where the link lives. A Zoom invite puts it in the meeting's location on its own; a Teams invite leaves
the location reading "Microsoft Teams Meeting" and buries the link in the body next to a help article, a dial-in
page and the organiser's meeting options. Knowing the services apart means the button opens the one that joins,
never one of the look-alikes, and it works on links your mail system has rewritten for safety. A service Diariz
does not know still joins on the first link in the invite. The recording is **named after the invite**, so your
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
**email yourself** the formatted transcript. A segment that contains **two voices** - one dominant, someone
else's few words inside it - can be **split at an exact word boundary**, and the new part handed to another
speaker or to one the API mints for it. The cut snaps to the stored word timings and the silence between the
two words falls into neither half, which is what a voiceprint trained on either side needs; an estimated cut
would slice the wrong audio. Word timings are kept from the release that introduced splitting onward, so an
older recording shows the Split control **disabled with an explanation** rather than missing, and
re-transcribing is what unlocks it. Splitting a segment you have hand-edited discards that edit, because
there is no principled way to divide edited prose at a word index it may not contain - you are asked
first. The transcript **embeds its audio** in a
**conversation-flow player**: the recording is laid out left to right as speaker-coloured blocks sized by how
long each person talked, with silence left dark and a legend giving each speaker's share — so the shape of the
meeting is legible at a glance — and the bar doubles as the scrubber (click or drag anywhere on it to seek).
Its toolbar keeps a **Select mode** — tick segments (or click one) to **play, edit, translate, or delete** just
the selection, while **Merge** always acts on the whole transcript. **Play selected** turns into **Pause** while
that selection plays, so you can stop it without waiting for it to finish. The **speaker label at the start of
every row** is the same assignment dropdown as the Speakers panel, so you can name a voice (or enrol a new
person) at the moment you hear them, without leaving the transcript.
- **Live transcript while the meeting runs.** The transcript no longer waits for the meeting to end.
  Open the notes panel while recording and it fills in roughly every half minute with what has been
  said, so you can check what someone said twenty minutes ago while they are still in the room. It is
  not a separate tab: the lines land on **one stream** beside your own notes and your screen captures,
  in the order everything happened, and **each line carries the moment it was said** - which is what
  lets you pin a note of your own to a sentence from four minutes ago. The **assistant can be asked about a meeting that is
  still going** - it is told explicitly that the transcript is partial and ends mid-meeting, so it
  will not describe something as decided or final when the people in the room are still arguing
  about it. **Speakers are named as the meeting runs**: a voice keeps one identity from start to
  finish rather than being renumbered every half minute, and where it belongs to someone already
  enrolled they are named, using the same recognition and the same operating point as a finished
  recording. When later audio shows that two live speakers were actually one person, the earlier
  lines are **rejoined in front of you** rather than staying split; where Diariz is not certain who
  someone is, the name is shown as a **question rather than stated**, so a guess reads as a guess.
  Two limits remain. It **never presents itself as finished**: a status line says how far behind the
  meeting it is, and the ordinary full-recording transcript replaces it when you press Stop. And if
  the server falls far enough behind it **stops writing the live text and says so** rather than
  showing text that is minutes stale - capture is unaffected either way, and the full transcript
  still arrives. Recognising a voice live **never enrols it**: recognition is platform-wide, so a
  name learned from a half-finished transcript would change recognition for every colleague in every
  future meeting - training still happens only when a person confirms who somebody is.

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
lands on the recording's **Notes** tab after upload). That panel is **one stamped stream** - your notes, your
screen captures and the live transcript interleaved in the order they happened, with the box you type into
fixed at the bottom of it and filter chips to narrow it to notes or captures alone. Hovering a transcript
line reveals a **plus** that pins the composer to that moment, so a thought you have forty seconds late is
still filed forty seconds back; the pin releases itself once you press Enter, and clicking the pinned time
releases it without filing anything; you can also jot **prep notes on an upcoming Google Calendar
meeting** from its preview page (they attach to the recording once it is linked). In the **desktop app** that live
panel can be **popped out into its own small window**, floating over a full-screen Teams
or Zoom call on a single monitor - the same panel, the same timestamps, the same screenshot buttons, and it keeps
working with the main window closed to the tray. Two controls of its own sit in its title bar: **On top** can be
turned off so the window drops behind the call and back again, and **Compact** shrinks it to just the note box,
for a screen where the call needs all the room but you still want somewhere to type. Compact is temporary and is
never remembered as the window's size. Stopping the recording closes it and files the notes as usual; if
the main window goes away underneath it, it says so and stops accepting notes rather than dropping them.
  **Two global hotkeys while recording.** In the desktop app, **Ctrl+Shift+0** puts the cursor in the note box
  wherever you are, and **Ctrl+Shift+8** sends the running meeting to the chat - both without leaving the call
  (the Command equivalents on macOS). They join the existing, configurable screenshot hotkey, which is unchanged.
  Numbers rather than the obvious letters, deliberately: a global shortcut is held for the whole meeting, and
  Ctrl+Shift+N is Chrome's incognito window and File Explorer's new folder while Ctrl+Shift+C is copy in Windows
  Terminal and VS Code. The note hotkey follows the notes - it raises and focuses the separate window when the
  notes are detached, and opens the panel in the main window when they are not. All three are held **only while a
  recording is actually running**, so Diariz never sits on a global key while idle, and the panel's hint line
  prints what is really registered rather than a literal, so it stays right if you change one. Timestamped lines jump to that
moment in the transcript. Each timestamped note is also **woven inline into the Transcript tab** - it appears as
its own **green line** (with your name as the speaker) right after the point in the conversation where you wrote
it; the **Merge same-speaker rows** action treats a note as a boundary, so transcript text either side of a note
stays separate. Your notes then **shape the meeting minutes** (every section weights what you flagged),
and a template can include an **Enhanced notes** section where each line is expanded from the transcript — your
words kept verbatim in bold beside the expansion, with links to the exact transcript moments (anything the meeting
never covered is kept and marked "not discussed", never silently dropped).
- **Spelling and editing on right-click (desktop app).** Text boxes in the desktop app underline
misspelled words the way a browser does, and right-clicking one now opens the corrections: pick a
suggestion and it replaces the word, or choose **Add to dictionary** for a name or piece of jargon that is
spelled correctly but unknown, which stops it being flagged from then on. The word goes into the
machine's own dictionary on both Windows and macOS, so it is remembered across restarts and other apps
learn it too.
The same menu carries **Cut**, **Copy**, **Paste** and **Select all** for editable text, greyed out
individually when they would do nothing, and **Copy** alone when you right-click a selection you cannot
edit, such as a passage of transcript. On Windows this is the only cut/copy/paste in the app, which runs
without a menu bar; macOS also has them on its **Edit** menu. It covers the main window, the popped-out
notes window and the first-run connect window.

- **Meeting screenshots (desktop app).** Capture the screen while a recording is running, from a
**configurable global hotkey**, the **tray menu**, or a button in the app itself. The first capture of each
meeting opens a picker overlay so you choose **a whole monitor or a dragged rectangle**; every later capture
in that meeting reuses the same area, and a "Change capture area" action lets you redefine it mid-meeting.
Finishing the drag **captures straight away** - drawing a rectangle round part of the screen is how you ask
for a shot of it, so it is not a separate step - while cancelling with Escape captures nothing, and neither
does an area chosen after the recording has ended (the choice resets for the next recording). A live strip of this meeting's captures sits in the recorder's
notes popover, so a mis-aimed capture area is caught during the meeting instead of after it. Each capture
stores a full PNG (long edge capped at 2560 pixels) plus a JPEG thumbnail, and both count toward your
storage quota. Screenshots then appear **inline in the transcript** at the moment they were taken, as
thumbnails that open a full-size viewer with previous/next, a position counter, a full-screen toggle,
jump-to-moment, **add to chat context**, download, and delete - the delete leads the trailing button group
behind a divider, so a click aimed at close cannot land on it; **zoom and pan** (mouse wheel toward the
pointer, a zoom cluster, double-click, keyboard shortcuts, drag once zoomed in) let a dense capture be read
at native resolution and scrolled around instead of downloading it. The Notes tab also lists a recording's
captures in a collapsed Screenshots section, from where one can be **dragged into the chat prompt** to ask
a vision-capable model about it.
  **A capture can reach the chat during the meeting.** While a recording is streaming to the server, each
  capture is **uploaded as it is taken** rather than held in the browser until Stop, so the live notes panel
  can offer a **Chat** button on it and let its thumbnail be dragged into the chat prompt there and then.
  A capture the server refuses - a full quota, a network blip - simply stays where it was and goes up with
  the rest at the end, so the worst case is the behaviour that shipped before. Deleting an already-uploaded
  capture removes the server's copy too, and a capture that is still on its way says so rather than sitting
  there as a dead button. A note or screenshot sitting between two turns by the same speaker now stops those
turns from being merged past it.
- **Extract text from a screenshot (OCR).** Where a Platform Administrator has routed a model to the **OCR**
call type, the capture viewer gains two extract buttons: one puts the text into the **chat prompt**, the other
saves it as a **Markdown attachment** on the meeting (where it is renameable, editable, and pulled into chat
with the rest). Both share a single model call - the result is cached on the capture, so the second
destination is instant and free, and a "force" re-read overwrites it. Extract several captures and they
**accumulate** in the chat's context pill rather than replacing each other, each block headed by its capture;
an uploaded file already in that slot is never overwritten without a confirmation. The buttons are hidden
entirely when no OCR model is routed, and the existing image path (**Add to chat context**) is unchanged -
sending the picture to a vision model and reading the words off it are different jobs.
  **Structure is converted, not stripped.** The models that read a page best answer with HTML - a capture
  with a table on it comes back as `<table><tr><td>`, and one model renders a gauge as `<img alt="Green">`.
  That is more useful than flat lines and is *not* worth prompting away (asked for Markdown instead, one
  model narrowed to a single table and discarded the rest of the capture), so tables are converted to real
  **GFM tables** on the way to a note or the chat pill: a `<th>` row becomes the header (or the first row is
  promoted when there is none), ragged rows are padded, pipes inside cells are escaped, `<br>` is kept as
  the one line break a GFM cell allows, and an image is reduced to its alt text. Text with no markup in it
  is passed through **byte-identical**. The API still stores the model's answer verbatim - that is the
  record of what it actually said - so the conversion happens where the Markdown is needed.
  The conversion strips real tags, then decodes entities, then escapes any angle brackets that remain -
  that order matters in both directions. Stripping before decoding stops text a page merely *showed*
  (`&lt;non-vaccines&gt;`) from being mistaken for markup and deleted; escaping afterwards stops the decode
  turning escaped markup back into a live element, and keeps angle-bracketed text visible instead of being
  swallowed by the renderer as an unknown tag. Backslashes are escaped before pipes, so a Windows path or a
  regex in a captured table cannot un-escape the separator that follows it.

  **Every extraction is stamped with the model that produced it and marked machine-read and unverified**, in
  the chat pill and in the attachment alike. This is not boilerplate: four OCR models measured against one
  dense desktop capture each made silent errors - a misread letter (`DSP` as `OSP`, reproducibly), whole
  tables dropped, and at one image size an invented column of neat, plausible scores present nowhere in the
  image. Two settings are **per model** because the right values differ sharply: `ocr_prompt` (one model wants
  the terse `Text Recognition:`, another a full sentence) and `ocr_max_edge`, the longest edge before the
  image is rescaled. Quality is **not** monotonic in resolution - the best size measured between 1288 and
  2560 pixels depending on the model, and pushing past a model's own best size made it worse - so the cap is a
  calibration rather than a maximum. Extraction runs synchronously and the image is sent as PNG even when
  rescaled, since JPEG artefacts land on exactly the glyph edges an OCR model is reading.
- **Preview what is attached to a question.** The chat composer's attachment pill names what is attached;
clicking that name opens a **read-only preview** of the contents. Text extracted from a capture renders as
Markdown (so a converted table reads as a table); an uploaded document's extracted text is shown verbatim in
a monospace block, because it is not Markdown and rendering it would eat underscores and stray hashes out of
ordinary prose. It matters most for extracted text, which is machine-read: reading it before sending is what
separates noticing a misreading from quoting one. Removing the attachment closes the preview with it.
- **Attached text is kept in the conversation.** When a turn is sent with something attached, the text it
supplied is recorded in the thread as its own card, above the question it went with - so a saved conversation
reopened later still shows what its answers were based on. This exists for extracted text specifically: a
transcript lives on its recording and can always be pointed back to, whereas text read off a capture exists
**only** in that attachment. The card is capped to a scrollable block so a long document does not bury the
conversation, and it is labelled as an attachment rather than as either party's words (including in the
Markdown `/attach` produces). It is a **record, not a turn**: it is never sent back as history, because the
server already injects the attachment itself - labelled and trimmed to the context budget - and sending both
would spend the same tokens twice. Only *new* text is recorded, so a sticky pill is not repeated on every
turn and a second extraction adds only what it appended.
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

**One person is one voiceprint, shared by everyone on the platform - and anyone can change it.** The
directory holds a single record per human, so a colleague's enrolment names that person in your recordings
too, and an erasure request is one deletion rather than a hunt through five private copies. Recognition
matches against **every** enrolled person, and a person's voiceprint is the average of **every** sample
behind them, whoever recorded it. Neither has an owner filter.

That means a user with no permission at all changes recognition for everyone, every time they name a speaker
on their own transcript or confirm one in Review Voice Matches - both enrol a sample and rebuild the shared
centroid. It is deliberate: only someone who was in the meeting can say who a voice is, so gating it on
Manage people would ask the question of exactly the people who cannot answer it. Confirming through the
review queue is the safer of the two routes, since it puts the audio in front of you first and withholds
questions whose audio has been deleted; naming a speaker on a transcript asks for none of that and has always
been ungated.

The counterweights are real but passive. Automatic matches never enrol, so only a deliberate human act can
teach a voiceprint. Someone who has opted out can never be enrolled by anyone. Dropping a sample excludes
rather than deletes it, so a wrong enrolment is reversible and stays visible. And a directory manager sees
every sample across every owner on the Voiceprint tab, with the impostor and alone verdicts built to surface
exactly the case where somebody was enrolled under the wrong name. What none of that provides is a push
signal: nothing announces that a shared voiceprint changed, and the person enrolling cannot see the other
samples, so they cannot tell they are adding an outlier - the diagnostics that would show it sit behind a
permission they do not have.

**When Diariz is nearly sure, it asks.** Identification used to run against a single strict cut-off: a match
either cleared it and was applied silently, or fell short and vanished. Between the acceptance distance and a
second, looser **confirmation band**, a match is now offered instead - **Might be Ada Lovelace - is it?** on
the speaker in the transcript, where the words and the audio already are, and gathered under **Review Voice
Matches** in the account menu for working through a backlog. That queue is a **two-panel window** opened over
whatever you were reading, directly below Preferences in the account menu: the waiting voices on the left,
and on the right what the open voice actually said - a line per segment, each with a single play/stop button
that cuts a clip of exactly that moment on the server. The answer is a tick and a cross beside the question,
so listening and deciding happen in one place without leaving the page you were on. **Play all** runs through
the segments in turn, highlighting each and scrolling it into view, so a long list can be followed rather
than hunted through.

**Every segment can be taken out on its own, and doing so reaches the voiceprint.** A diarization label is
not always one human: two people on one microphone, or a stretch of crosstalk, arrive under a single
`SPEAKER_nn`. Answering only for the whole list forces a reviewer who can hear that part of it is somebody
else either to accept audio that is not this person, or to discard an identification they know is right.
Taking a segment out drops it from the list, and accepting then trains from **only what is left** -
carried as spans on the enrolled sample and re-embedded by the worker, the same mechanism the Voiceprint
tab's span selection uses, rather than a second way of saying the same thing. Excluding nothing sends no
spans at all, which means the whole speaker and queues no re-embed, because that is what almost every accept
wants. The removals are **provisional until the voice is confirmed** - nothing is written while the reviewer is
choosing, and declining or closing the window discards them - which is why every control that removes one
now names the voiceprint rather than the list. Saying only "take this segment out" left a reader to conclude
the control tidied the screen, which is what happened.

Exclusions are held **per voice** for the sitting: one voice's marks never reach another's voiceprint, and
switching away and back leaves them as they were. Those are two requirements rather than one, and the first
implementation conflated them - it got the isolation by discarding the marks on every switch, which live use
found within a day. Nothing is written until the voice itself is confirmed, which is also the only thing that
commits, so the button says **Confirm this voice** in words and the panel states what it will train from
while anything is excluded. A **Restore** puts back anything excluded by mistake - one click with no undo
that shapes a biometric needs a way back.

**There is deliberately no matching tick.** One shipped in 0.259.0 and was removed in 0.259.6: a segment
trains unless it is removed, so ticked and untouched were the same state and the control recorded nothing -
while sitting beside the cross, same size, rendering pressed and filled, reading as a choice of what would
be used. Two controls that look like opposites, one of them inert. What replaced it is a statement rather
than a control: the panel says what confirming will train from **at all times**, not only once something
has been excluded, which was the other half of the confusion - the commonest path, look-decide-confirm, was
the one it said nothing about.
What it deliberately does **not** do is relabel those segments in the transcript: the speaker is one row, and
splitting it into two people is a different operation. It is deliberately **not**
a panel inside the People directory: the queue shows **your own recordings only** and needs no permission -
the people who can answer whether a voice belongs to someone were in the meeting - whereas the directory is
gated on Manage people, so folding one into the other would take the queue away from everyone who cannot
browse it. A platform-wide queue would also disclose who appears in every meeting in the instance. **A voice
whose recording audio has already been deleted is not offered at all** - neither in the queue nor as a prompt
on the transcript. The only honest way to answer is to listen, and the retention sweep takes recording audio
once it passes its window (the 0.257.0 exemption covers audio behind an *enrolled* sample, and a pending
suggestion is by definition not enrolled). Such a row is not a question but a permanent occupant of the list,
so the question is withheld rather than decided - the suggestion itself is left untouched. Confirming enrols that
speaker, which is how the system learns a voice in a condition it was previously unsure about; declining is
remembered, so the same pair is never suggested again - though a later outright match still applies, because
that is new evidence rather than the same question asked twice. Every answer is stored with the distance that
was on offer: rejections are the platform's **only** source of labelled negatives, since every manual link is
a positive.

A **re-scan** applies the current settings to everything already transcribed. Identification otherwise happens
once, when a recording is transcribed, so enrolling someone never revisits earlier recordings - on the
measured instance that left 38 speakers sitting inside the acceptance distance, unnamed. It **previews first**
("this would name 38 and ask about 90") and **only ever adds a name**: the scan considers only speakers that
are already anonymous and unlinked, so there is nothing for it to take away, whatever the settings change to.

Four **identification settings** are editable by a Platform Administrator rather than fixed at deploy: the
distance at which a match is applied, the distance up to which it is merely asked about (equal to the first
means no questions at all), how far the best match must beat the **next person** - measured between people,
never between two voiceprints of the same person - and the minimum speech before a voice is compared at all.
That last one matters: a second and a half of audio produces a confident-looking number that is not worth
trusting, and if accepted it would go on to train whatever it matched.

**A recording trains someone's voiceprint only while the transcript still says that speaker is them.**
Unassigning a speaker, or handing them to a different person, used to move the label and leave the voiceprint
alone: the recording carried on teaching the original person indefinitely, and did not appear on their
Voiceprint tab, which lists the speakers currently linked to them. Six recordings on a real platform were in
that state, three of them training one person on audio since labelled as somebody else - so both people were
being taught the same voice. It was also why a recording flagged as sounding wrong could have no row anywhere
to play or untick: the scoring read the samples while the list read the linked speakers, so the two disagreed
about which recordings were even behind the voiceprint.
Recordings the rule rejects stay listed, marked **No longer linked to this person**, and voiceprints built
from them are rebuilt when the server next starts.

**A voiceprint is only as good as the recordings behind it, so each one carries a verdict on whether it
sounds like the rest.** Measured on a real platform, of the samples belonging to people enrolled more than
once, a third resemble none of their others, and the widest pair inside a single person is almost completely
dissimilar - which two recordings of one human cannot be. Two explanations need opposite responses: the same
voice somewhere new (a phone, a car, a room speaker), which is exactly the audio a voiceprint benefits from;
or **someone else enrolled under that name**, which is why recognition drifts.

Each sample gets two distances, because they answer different questions and disagree in the case that matters
most - a sample can sit right beside one companion while that pair together sits well away from everything
else. The first is the distance to its **closest neighbour** ("does this have company?"); the second is to the
centre of **the person's other samples**, a true leave-one-out ("would the rest of this voiceprint recognise
it?"). Including a sample in its own comparison would make everything resemble itself, which is precisely the
false reassurance the tab exists to prevent.

The verdict is given in words rather than a bare number, because a user cannot act on "0.62" but can act on
being told which recording does not sound like the rest. `Sounds unlike their others` deliberately does not
say *wrong*: only listening settles whether it is a new microphone or a different person, and the play button
and training tick box on the same row are how you settle it.

**The verdicts sit on the recordings themselves, in the one list that also holds the controls.** They were
briefly on a tab of their own, which listed the same recordings and none of the controls - so acting on a
flagged recording meant remembering its name, switching tabs and finding it again, and the header contradicted
the rows beneath it (it counted only the outliers while the list showed everything). The ones worth a listen
sort to the top, and a tick box narrows a long list to just those.

**Re-measuring reports whether it worked.** Whether a job is in flight is recorded rather than inferred: it used to be read off the span selection, and selecting the whole speaker - the state every row starts in - is stored as no selection, so the commonest case reported nothing at all and the button looked inert. A failure now says so too. It leaves the existing voiceprint alone, which is right, but the row previously recorded zero seconds of training audio and was indistinguishable from a success. The button is named for what it does: it re-embeds **one recording's** contribution, and the person's voiceprint is the average of every recording behind it, so this is not a rebuild of the whole print.

**Every recording is also asked whether somebody else is closer.** Comparing a recording only with its own
person's others answers "does this have company?" - it can never answer "is this the right person?", and
those need opposite responses. Measured on a real platform before this was built: of the recordings behind
people with more than one, over a quarter sat closer to a different person than to any of their own, and a
third of those were within the accept distance of that person. The sibling-only check flagged most of them
but not all, and one read as perfectly healthy. Clustering that set - giving each outlier a template of its
own - would have turned each of those from a diluted nuisance into a confident match for the wrong human,
which is why multi-template voiceprints were deferred until the set is clean.

The check is **comparative, not absolute**: two people can genuinely sound alike, so what marks a
misattribution is somebody else being closer than the person's own recordings. It **names** them, so the
finding lands on the reassign control already on the row. A person with a single recording is never flagged
this way, because there is nothing of their own to compare against - a real limitation, since most of a
directory is usually in that state.

**Audio a voiceprint was enrolled from is exempt from automatic deletion.** The retention sweep removes
recording audio past its window and keeps the transcript, which is right in general and wrong for the
handful of recordings a biometric was built from: whether one really is the right person can only be settled
by ear, so deleting it makes the question permanently unanswerable while the embedding it produced goes on
being used. Measured on a real platform before the exemption: 47% of training samples were already behind
deleted audio, growing nightly. The exemption is derived from the samples rather than a flag on the
recording, so dropping the last one lets it rejoin the policy with nothing to remember to clear, and it
covers the **automatic** sweep only - deleting audio by hand stays available, because the objection is to a
background job destroying evidence silently rather than to a person choosing to. One predicate answers both
the sweep and the recording page's "will be deleted on" hint, so the hint cannot promise a deletion that
will not happen. Where audio has already gone the row says so and offers no playback, but still lists what
was said and can still be confirmed - blocking that would strand every already-swept recording permanently
unresolvable.

**Confirming a recording** records that a human listened and vouched for it, and takes it out of the review
queue. It is deliberately a different assertion from whether the recording trains the voiceprint: one asks
who it is, the other whether the audio is worth learning from, and a recording can be genuinely them and
still be too noisy to train on. There is no bulk confirm - the value of the gate is that somebody listened,
so a button that confirmed unheard audio would reintroduce the failure it exists to prevent. A confirmed
recording keeps its verdict on the card; only the queue shrinks.

**A recording that turns out to be somebody else is fixed from the row it was found on.** Each row carries
the same speaker typeahead the transcript and the Speakers tab use, showing who it currently says this is:
reassign it to the right person, create someone not yet in the directory, mark it as **Multiple speakers**
where two people talked over each other, or unlink it. Whichever is chosen, the recording stops training that
person's voiceprint immediately, because training follows the transcript's own answer. The control appears
only on recordings the caller **owns** - `ManageVoiceprints` grants listening to a segment for assessment,
not editing somebody else's transcript, and all three endpoints behind the control enforce ownership, so
offering it without the check would produce a button that always failed.

**The figures are similarity, not distance.** They were printed as raw cosine distance under a label that
reads as a match, so the worst recording in the directory displayed the largest and most reassuring number on
the screen. `closest other: 82%` was an 18% match.

In the People directory, a person with a shaky voiceprint or a likely duplicate carries a short warning line
on their own row, and a **Needs review** filter narrows the list to them - scanning a long directory for a
colour is not a way to find anything. Both warnings were full-width panels above the list until they were
found, in use, to push the person card almost off screen: the very card they were asking you to look at.
Either can be dismissed for the sitting, keyed on the person rather than the duplicate group, so a merge
elsewhere reordering the list cannot land the dismissal on the wrong row.

None of this needs audio, a re-transcription or the GPU worker: every sample already carries its embedding, so
the whole diagnosis is arithmetic over data that exists.

Each person's card carries a **Voiceprint** tab beside their profile, answering what the biometric was
actually built from - previously invisible, which is why a drifting voiceprint had no diagnosis. It lists
**every recording the person appears in**, not only the ones enrolled by hand: automatic recognition links a
speaker without creating a training sample, so a list built from samples showed a fraction of where the
voiceprint is actually applied and read as an arbitrary subset. Each row states how that speaker came to be
attributed (recognised automatically, or named by hand), how much they speak in it, and how much of that
audio is behind the voiceprint, with a tick box to add the whole speaker to training or drop it. Adding needs
no re-transcription - the voice was measured when the recording was transcribed - and dropping **excludes
rather than deletes**, so the record that someone identified that speaker survives and re-including it is one
tick.

Every segment has a **play button**. Each one plays a short WAV cut on the server with ffmpeg, seeking into
the stored object rather than pulling the whole recording, so judging whether a voice is really the right
person does not mean opening the recording and hunting for the speaker. Because the directory is
platform-wide while recordings are ownership-filtered, a person's voice can appear in recordings you do not
own: hearing those needs the **Manage voiceprints** permission (platform administrators by default). That
grant is deliberately narrow - the requested span must fall inside a segment that speaker actually spoke, the
segment list returns only their own segments and never the rest of the transcript, and every cross-owner
access is logged. Ordinary directory work stays on **Manage people**, which grants no audio at all. Without
either, the row is still listed - it is part of what trained the voiceprint - but marked as being in a
recording you cannot access.

Expanding one lists that speaker's segments with **tick boxes**: untick the places where someone
else was talking over them and press **Re-measure this recording**, and the worker re-embeds from exactly the
audio left ticked. A run of ticks queues **one** job, not one per click. What is stored is a set of
**time spans**, not segment ids - a re-transcription replaces every segment row, and ids would dangle where
wall-clock times survive - and ticking everything stores nothing at all, which is the "whole speaker" state
every voiceprint enrolled before this existed is already in. Pooled audio is capped at **120 seconds** per
speaker; where the cap bites, the tab states what was used against what was selected rather than implying it
used all of it. Moving a segment between speakers marks both of their voiceprints as **needing recomputing**,
since the audio behind each one changed; nothing recomputes on its own, because that needs the worker and the
original audio.
**Browsing** the directory, and editing, deleting or merging anyone other than yourself, needs the
**Manage people** permission; labelling a speaker does not, and **opting yourself out never does** - under
GDPR, withdrawing consent to hold your own biometric is yours to exercise, not an administrator's to grant.
The trade-off of a shared directory is deliberate and worth stating plainly: a voiceprint enrolled by one
person will identify that human in **everyone's** recordings.

Two people of the same name are told apart by **which Diariz account each one is**. The directory list, the
possible-duplicates banner and the merge dialog all show it, mark your own record, and say plainly when there
is no account behind a person - which is also the difference between a pair you may merge and a pair the
server will refuse, since two accounts are two humans.
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

Your own entry is reachable without any of that. **Preferences -> Profile** carries a read-only **You in
transcripts** panel naming the person your account *is* and whether a voiceprint is held for you - the one
thing the gated directory hid from an ordinary user, who could not otherwise find out whether Diariz would
recognise their voice. It is read-only by design: the name follows your display name above it, and erasing a
voiceprint or opting out already live in the people UI for your own record.
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
(e.g. "Alice, Bob and 11 unidentified attendees"); a speaker marked **Multiple Speakers** is left out entirely,
being overlapping speech rather than a person. A Platform Administrator can also pick how minutes generate:
**one LLM call per section** (best structure) or a **single call** (fewer tokens).
- **Chat across one or more transcripts — a folder — or all your meetings at once** (an "All meetings" mode
that searches your whole library on demand instead of pre-loading transcripts) — streaming
replies, a context-usage dial, PDF/text attachments, and saved conversations — via the **platform's**
OpenAI-compatible LLM endpoint (see **AI models** below), with the API key encrypted at rest. The chat's context is
**inferred from what you're viewing** rather than picked from a list: the label reads **Current Transcript**,
**Current Folder**, or **Selected Transcripts** (2+ ticked in the list) and updates when you click into the
box.
  **The meeting you are in right now.** The live notes panel has a **Use in chat** button that attaches the
  running meeting to the prompt as a sticky pill. It sends the recording, not a paste of the transcript, so
  every question is answered against the transcript **as it stands at that moment** rather than a snapshot
  that went stale the second the meeting carried on - and the server tells the model the meeting is still in
  progress, so it will not report an argument still being had as settled. The pill rides every question until
  you remove it, and deliberately stays after you stop recording: the same recording is then a finished one,
  and "summarise the meeting I just had" is usually the next thing you want. When a **folder** is open, chat is about that folder — its roll-up **summary, minutes, and aggregated
actions** are the context, and "Include attachments" pulls in every attachment across the folder and its
sub-folders.
  **Choosing a model.** Where an administrator has marked more than one model as available for chat (see
**AI models** below), a sparkle button beside the context dial opens a picker listing each one by its
display name, a short description written by an administrator, icons marking whether it can use the chat
tools or read images, and its context window as binary K (128K rather than 131,072, with the exact count
on hover). A legend along the foot of the menu names both icons, and the menu is wider than the chat
panel and overhangs it so a long name and its description fit on one line. The choice can be changed **part-way through a
conversation** - chat is stateless per turn, so the whole conversation is resent and the new model picks up
where the previous one left off. The dial follows the choice immediately rather than waiting for the next
reply, so it always reports the window of the model that will actually answer. The choice is remembered
between visits and stored with a saved conversation; if the model is later withdrawn, the conversation falls
back to the platform's chat model rather than failing.
  **Asking about a screenshot.** A capture goes to the chat prompt two ways: the **Add to chat context**
button in its full-size viewer (which ticks to confirm, leaves the viewer open, and expands the chat panel
if it was collapsed), or **dragging its thumbnail** out of the Notes tab's Screenshots section into the
prompt box. Either way it appears as a thumbnail above the input and goes to the model with the question.
Several can be attached, one at a time, and each thumbnail carries an X to take it back out. They are
**sticky** - they ride every turn until removed, so a follow-up about the same image needs no second
attach -
and they are stored with a saved conversation, so reopening it restores them (a capture deleted in the
meantime is simply dropped). Reading images requires a model whose **Supports image input** parameter is set:
with captures attached to a model that cannot, **Send is refused** with "Select a vision model" rather than
answering about a picture the model never received, and the model picker marks which models can read images.
It marks tool support the same way, with a briefcase - though that parameter defaults to on, so every
model carries the briefcase until an administrator turns it off on the ones that cannot call tools.
Before sending, a capture is **rescaled to fit inside 1920x1080** (ratio preserved, never enlarged) because
the models read that size more reliably than full 4K; one already within those bounds is sent byte-for-byte
as captured. The bound is a cap rather than a target, so the smallest text in a dense 4K capture may not
survive the resize. Attached captures are counted into the context dial, which now shades **orange above 50%
and red above 75%** of the window.
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
tokens and is independent of what the formula lets the model read. One token works the other way round:
**`$USERNAME`**, written anywhere in a formula, is replaced when the formula runs with the name the person
running it appears under on their transcripts (their people-directory entry; for an automatic run, the
recording's owner). It is substituted **before** the model is asked, which is the whole point - it is what
makes "What role did $USERNAME play in this meeting?" a question one shared formula can answer correctly for
everyone, rather than a copy per person with a name typed into each. A merge field could not do that, because
a merge field never reaches the model. The context
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
- **Manage the actions you choose to track across all your meetings** in a dedicated **Actions** tab (the
left panel is now **Meetings**). The tab is **opt-in**: an action reaches it only once someone **pins** it,
via the pin control on the action's own meeting page or on the row in the tab itself. Everything else stays
on the meeting it came from, so the recording page remains the one place that shows every extracted action.
A folder's Actions tab follows the same rule. Within that pinned list you can **filter by person**, mark
items **done** with a completion date (individually or in bulk, reversible), **hide completed**, and click
an action to jump to the transcript it came from. Pinning is owner-only, so in a shared room the recording's
owner decides what the room sees pinned. Re-extracting a meeting's actions replaces the list and clears its
pins, the same way it already clears completion. The per-transcript table carries the pin alongside the Done
checkbox and Completed date. The REST API's action list is unfiltered by default (so existing automations
are unaffected) and takes `pinned=true` for the pinned subset.
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
text; URLs are fetched behind SSRF guards) Or feed in **one** of them: every attachment row carries a
**drag handle**, and dropping it on the chat box reads that document's text into the chat context pill -
the narrow version of the same thing, for when you had a particular document in mind rather than all of
them. It works from a recording's Attachments tab, a folder's own attachments, and the aggregated list
across a folder, and for **URL** attachments as well as files. Unlike the bulk toggle, which skips an
attachment it cannot read so that one bad file never fails a whole chat turn, a dropped one **reports**
the failure: you are waiting on that document, and a composer that did not change would tell you nothing.
Click the pill to read exactly what the model was handed. Documents accumulate under a heading each, the
same way extracted screen-capture text does - which now also applies to the paperclip, so attaching a
file adds to what is there rather than replacing it.
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
optional key, press Discover, and it lists the chat models that server reports. The address is **verified as
one that accepts chat requests** rather than merely reachable - for LM Studio that means it ends in `/v1` -
and corrected where it can be, with the endpoint the models will be created against shown in the dialog; an
address nothing can be called on is refused rather than turned into models that never answer. Embeddings, speech
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
overrides, and the thirteen parameters in two columns. Connection details (name, display name, description,
endpoint, key, context window) sit behind a **Connection** button, since they are set once when a model is added. A panel beside
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
  **Run test** (`POST /api/admin/llm-models/{id}/test`) sends one call with the parameters currently on
screen - saved or not - and reports **time to the first token**, **total duration**, **tokens per second**
(completion tokens over duration, as in the usage log), the token counts, and the model's actual reply. Each
call type keeps its own result, since they ran with different parameters. First-token time is separated from
duration deliberately: together they distinguish a model that was still LOADING from one that is simply slow,
which need opposite fixes and otherwise look identical.
  **What that call contains depends on the call type.** For **Tags**, **Actions** and **Summaries** it is the
real thing: the same prompt the pipeline builds, from the same editable template file under `prompts/`, over
a real recording's transcript with its speaker display names. The administrator picks that recording in the
test panel from their **own** recordings that have finished transcribing, and the choice is remembered
against their account (`GET`/`PUT /api/admin/llm-models/test-recording`) so all three tabs, and every model
tried afterwards, are compared against the same meeting. Run test stays disabled on those tabs until one is
chosen. The other four call types - **Defaults** (a parameter scope nothing is dispatched to), **Minutes and
formulas** (several calls rather than one), **Translation** (needs a target language) and **Chat** (needs a
question) - keep a fixed built-in sample transcript.
  A recording-backed reply is also run through **the pipeline's own parser**, and the card shows the result
that would really have been stored: tags as weighted chips, action items as a table of task, owner and
deadline, a summary with the title it suggests (asked for only when the recording has no name, exactly as the
summariser decides). The model's raw words sit behind a **Raw reply** toggle. Those parsers are total - they
return nothing rather than throwing - so a confident reply the platform cannot read is reported as
"the pipeline would have extracted no tags from this reply" rather than as a silent pass. That is the most
diagnostic result the panel produces.
  Two consequences worth stating. The transcript is sent to the endpoint under test, and the panel says so;
the reply is returned to the browser and, as with every test call, never persisted - the usage log records
counts only. And because the reply length now depends on the meeting, first-token times are only comparable
between models when the same recording is remembered, which is why the choice is per administrator rather
than per tab. Test calls name the recording they ran against in the usage log.
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
uploading the archive from the server applying it. In the **desktop app**, which has no browser download
shelf, the panel goes on to report the transfer itself - a percentage of the archive as it comes down, the
path it was saved to, and a failure if it did not finish - and the shell raises a notification when a download
that ran more than a few seconds completes (this covers every download in the app, not just backups). A build
that fails says so, rather than showing the same message as one that worked. Restore is destructive (replaces
all data) and accepts a backup from this app version or an older, forward-migratable one.
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
