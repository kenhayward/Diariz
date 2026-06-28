// Release notes + About-box copy. This is the single source for the Release Notes page and the
// About modal. Per the CLAUDE.md versioning rule, every PR adds/updates the newest RELEASES entry
// and RELEASES[0].version must equal the app version (version.json).

export const TAGLINE = "Smart Meeting Transcription";
export const GITHUB_URL = "https://github.com/kenhayward/Diariz";
export const COPYRIGHT = "© Ken Hayward";
export const LICENSE = "Apache-2.0";

/// Short summary of what the app does today, shown in the About box (markdown). Update this whenever
/// the app's scope changes.
export const CAPABILITIES = `
Diariz records audio — from your microphone, or Windows system/loopback audio via the desktop app
(which can also **start and stop recording straight from its system-tray menu**) — or you can **upload
an existing audio file** (WAV, MP3, FLAC, Ogg/Opus, WebM, M4A), and it transcribes it server-side with
**WhisperX** (word-level timestamps) and **pyannote** speaker
diarization. You get speaker-labelled, timestamped segments you can rename, edit, and play back
(per segment or the whole recording), and can re-transcribe at any time. You can **merge** consecutive
same-speaker rows into single blocks and **email yourself the transcript**.

It can **identify speakers** across recordings: enrol a person from a recording's speaker and Diariz
recognises that voice in later recordings automatically (using **SpeechBrain ECAPA** voiceprints), with
manual reassignment. A **People** screen manages enrolled voiceprints — rename, prune training samples,
merge duplicates, and erase one or all (GDPR) of the stored biometric voiceprints.

It can **summarise** recordings, **extract action items** (with actor and deadline) into an editable
table, and let you **chat across one or more transcripts** — with file attachments, a context-usage dial,
and saved conversations — using an OpenAI-compatible LLM endpoint you configure. Recordings organise into
**sections** with drag-and-drop ordering.

Diariz is **multi-user** with role-based access: people request access (or an administrator adds them),
an administrator approves, and each user sets up their own account and keeps their own private
recordings, transcripts, and chats. Each user has a **storage quota** (audio): the Platform
Administrator sets the starter and maximum, any administrator can raise an individual user, and your
usage shows in the account menu.
`.trim();

export interface Release {
  version: string;
  date: string; // ISO yyyy-mm-dd
  pr?: number;
  headline: string;
  summary: string; // markdown; PR-level detail
  added?: string[];
  changed?: string[];
  fixed?: string[];
}

/// Newest first. RELEASES[0].version must match version.json (asserted in releases.test.ts).
export const RELEASES: Release[] = [
  {
    version: "0.20.0",
    date: "2026-06-28",
    pr: 56,
    headline: "Action items everywhere they belong",
    summary: `
Extracted **action items** now travel with the transcript. When a recording has actions, an **Actions**
section (with the same Action / Actor / Deadline layout) is inserted right after the summary in every
**downloaded transcript** (Plain Text, Markdown, RTF) and in the **emailed transcript** — and the actions are
included in the **chat context** for the current and selected transcripts, so you can ask the assistant about
them. The section is omitted entirely when a recording has no actions.

Also: the **Edit segment** dialog is wider and auto-sizes to fit longer text (capped to stay on-screen).
`.trim(),
    added: [
      "Downloaded transcripts (Text/Markdown/RTF) and the emailed transcript now include an Actions section after the summary when actions are present.",
      "Chat includes a recording's action items in its context (current and selected transcripts).",
    ],
    changed: [
      "The edit-segment dialog is wider and auto-sizes its height to the text (capped to stay on-screen).",
    ],
  },
  {
    version: "0.19.1",
    date: "2026-06-28",
    pr: 55,
    headline: "Clearer progress while extracting actions",
    summary: `
While **Extract actions** is running, the recording page now shows a prominent progress banner (like the
re-transcribe one) instead of a small label, so it's obvious the model is working.
`.trim(),
    changed: [
      "Extracting actions now shows a progress banner instead of a small header label.",
    ],
  },
  {
    version: "0.19.0",
    date: "2026-06-28",
    pr: 54,
    headline: "Extract action items from a transcript",
    summary: `
A new **Extract actions** button on the recording page asks your summarisation model to pull **action
items** out of the transcript — each with an action, an actor, and a deadline (any of which may be blank).
The results appear in an editable **Actions** table below the summary, which you can add to, edit inline,
and prune. It's shown **by exception** — only once you've run it — so meetings without actions (webinars,
town halls) stay uncluttered.

The recordings list also gets a tidy-up: the **New section** and **Select** controls are now icon buttons
with tooltips, in Select mode you can **tick a whole group at once** (handy for picking chat context), and
each group header shows its **recording count in brackets**.
`.trim(),
    added: [
      "Extract actions: pull action items (Action / Actor / Deadline) from a transcript with the configured LLM, shown as an editable table below the summary (only after you run it).",
      "Add, inline-edit, and remove action items by hand.",
      "Select an entire group at once when picking recordings for chat context.",
    ],
    changed: [
      "The recordings list's New section and Select controls are now icon buttons with hover text.",
      "Group headers show their recording count in brackets, e.g. (3).",
    ],
  },
  {
    version: "0.18.0",
    date: "2026-06-28",
    pr: 53,
    headline: "Quick-action toolbar on the recording page",
    summary: `
The recording detail page now has a small **toolbar of graphical buttons** next to the ⋮ menu, for the
actions you reach for most: **Rename**, **Re-transcribe**, **Move to section**, **Email me the transcript**,
and **Download transcript**. Each button has a hover tooltip, and every action is still available in the ⋮
menu as before.
`.trim(),
    added: [
      "Quick-action toolbar on the recording detail page (Rename, Re-transcribe, Move to section, Email me the transcript, Download transcript) with hover tooltips; all actions remain in the ⋮ menu.",
    ],
  },
  {
    version: "0.17.2",
    date: "2026-06-28",
    pr: 52,
    headline: "Cleaner Markdown transcript table",
    summary: `
The Markdown transcript no longer appends a separate \`{: col-widths }\` attribute line after the table
(which some editors rendered as a stray paragraph). The 13/16/71 column widths are now carried by the
separator row's dash counts — how pandoc and MultiMarkdown size columns — so the file stays a clean table.
`.trim(),
    fixed: [
      "Markdown transcript: removed the trailing column-width attribute line that some editors showed as an extra paragraph; column widths are now encoded in the separator-row dashes.",
    ],
  },
  {
    version: "0.17.1",
    date: "2026-06-28",
    pr: 51,
    headline: "Tidier transcript downloads",
    summary: `
Polish for the transcript download formats: the **plain-text** file no longer has a blank line between
every segment, **Markdown** carries a column-width hint for renderers that honour it, and the **RTF**
table now has a proper repeating header row, sensible column widths, and a little more space after the
summary.
`.trim(),
    fixed: [
      "Plain-text transcript: collapsed the extra blank line after each segment.",
      "Markdown transcript: added a 13/16/71 column-width hint for the Time/Speaker/Text table.",
      "RTF transcript: marked the first table row as a repeating header, set 13/16/71% column widths, and added a paragraph break after the summary.",
    ],
  },
  {
    version: "0.17.0",
    date: "2026-06-28",
    pr: 50,
    headline: "Choose a transcript download format",
    summary: `
"Download transcript" now opens a **"Download as …"** chooser with three formats — **Plain Text**,
**Markdown**, and **Rich Text Format** — plus OK/Cancel. Every format is structured like the emailed
transcript: a name heading, the summary, then the transcript itself — as readable paragraphs in plain text,
and as a Time / Speaker / Text **table** in Markdown and RTF.
`.trim(),
    changed: [
      "Download transcript now offers Plain Text, Markdown, or Rich Text Format, each laid out like the emailed transcript (name, summary, then the transcript — paragraphs for text, a table for Markdown/RTF).",
    ],
  },
  {
    version: "0.16.0",
    date: "2026-06-28",
    pr: 49,
    headline: "Welcome screen + guided tour for new users",
    summary: `
First-time users no longer land on a blank "select a recording" screen. The empty detail page now shows
the Diariz backdrop with a friendly welcome — "press Record or Upload to add your first recording" — and a
**guided tour** kicks off on first sign-in, walking through each area one at a time (capture, recordings,
transcript, chat, account) with a quick explanation. You can skip it (and turn it off) at any point, and
replay it whenever from **Show guided tour** in the account menu. Once you have recordings, the empty page
just prompts you to pick one or record/upload another.
`.trim(),
    added: [
      "First-run guided tour highlighting each part of the app, with skip/turn-off and a replay option in the account menu.",
      "Welcoming empty-state screen (with the app backdrop) instead of a blank panel.",
    ],
  },
  {
    version: "0.15.0",
    date: "2026-06-28",
    pr: 46,
    headline: "Drag-and-drop + multi-file uploads",
    summary: `
Uploading is now a batch affair. You can **drag audio files straight onto the recordings list** (it
highlights as you drag over it), and the **Upload** button accepts **several files at once**. A small status
list shows each file as **queued → uploading → done/failed**, and it's tolerant of partial failures — an
unsupported or oversized file is skipped with a reason while the rest carry on. Uploaded recordings now also
show an **"Uploaded"** source label.
`.trim(),
    added: [
      "Drag-and-drop audio files onto the recordings panel, and pick multiple files from the Upload button — with a per-file queued/uploading/done/failed status list.",
    ],
  },
  {
    version: "0.14.0",
    date: "2026-06-28",
    pr: 45,
    headline: "Upload an audio file to transcribe",
    summary: `
A new **Upload** button next to Record lets you transcribe an **existing audio file** instead of recording —
**WAV, MP3, FLAC, Ogg/Opus, WebM, and M4A** are accepted. The file runs through the same pipeline as a
recording (transcription, diarization, speaker identification, summarise), and its duration is filled in once
the worker has processed it.

Uploads are validated by their **actual bytes** (not the filename), size-capped (500 MB by default), and
counted against your storage quota. The royalty-free formats plus MP3 are always accepted; **M4A/AAC** is on
by default but can be disabled server-side for commercial caution (it still carries patents). Upload is
disabled while a live recording is in progress.
`.trim(),
    added: [
      "Upload button: transcribe an existing audio file (WAV/MP3/FLAC/Ogg/Opus/WebM/M4A), validated by content, size-capped, and quota-counted.",
      "Worker reports the measured audio duration (backfilled onto uploads) and rejects audio over a configurable maximum length.",
    ],
  },
  {
    version: "0.13.1",
    date: "2026-06-28",
    pr: 43,
    headline: "Correct the licence disclaimer + scrub default seed email",
    summary: `
Housekeeping ahead of open-sourcing. The About box wrongly stated the **pyannote** diarization models were
"non-commercial (CC-BY-NC)" — they are in fact **MIT-licensed** (just *gated*: you must accept the terms on
Hugging Face and supply an \`HF_TOKEN\`). The genuine non-commercial caveat belongs to **VoxCeleb**, the data
behind the SpeechBrain ECAPA voiceprints, so the disclaimer now says that instead. The default seed-admin
email also changes from a personal address to \`admin@example.com\`.
`.trim(),
    fixed: [
      "About box: pyannote models are MIT (gated), not CC-BY-NC; the non-commercial note now correctly refers to VoxCeleb (speaker-identification training data).",
      "Default seed-admin email is now admin@example.com (was a personal address) in appsettings + .env.example.",
    ],
  },
  {
    version: "0.13.0",
    date: "2026-06-28",
    pr: 41,
    headline: "Tidier speaker panel + smarter transcript merge",
    summary: `
Two refinements to the recording view. The **Speakers** panel now starts **collapsed** when every speaker
has already been named or identified (so there's nothing left to label) and stays expanded when speakers
still need assigning — you can still toggle it either way.

And **merging consecutive same-speaker segments** now follows the *assigned* speaker, not just the raw
diarization label: if two stretches were diarized as different speakers but you reassigned them to the same
person, they now merge into one block too.
`.trim(),
    changed: [
      "The detail page's Speakers panel starts collapsed once all speakers are assigned.",
    ],
    fixed: [
      "Merge consecutive same-speaker segments now also merges runs that were diarized as different speakers but reassigned to the same person.",
    ],
  },
  {
    version: "0.12.2",
    date: "2026-06-28",
    pr: 39,
    headline: "Fix the desktop release publish step",
    summary: `
The desktop release build packaged the app but then failed to publish: electron-builder couldn't determine
the GitHub repository (it only looks for \`.git/config\` in \`apps/desktop\`, not the monorepo root). The
release config now takes the owner/repo from the CI environment (\`GITHUB_REPOSITORY\`), which also keeps the
repo fork-friendly — a fork's CI publishes to its own Releases without editing the config.
`.trim(),
    fixed: [
      "Desktop release: set the GitHub publish owner/repo from $GITHUB_REPOSITORY (electron-builder can't detect it from a monorepo subdirectory).",
    ],
  },
  {
    version: "0.12.1",
    date: "2026-06-28",
    pr: 38,
    headline: "Fix the desktop release build",
    summary: `
The desktop installer's CI build failed before producing an installer: its unit-test step
(\`node --test "src/**/*.test.js"\`) needs the Node test runner's glob support, which only exists on
Node ≥ 21, but the workflow ran Node 20. The release workflow now builds on **Node 22**, and pins
electron-builder's tool cache to a writable directory so the self-hosted runner can package the installer.
`.trim(),
    fixed: [
      "Desktop release workflow: build on Node 22 so the unit-test glob runs, and pin the electron-builder cache to a writable path.",
    ],
  },
  {
    version: "0.12.0",
    date: "2026-06-28",
    pr: 37,
    headline: "Desktop auto-update + launch-at-startup — phase 3",
    summary: `
The Windows desktop app now **keeps itself up to date**. It checks for new releases in the background (on
launch and every few hours, plus a manual **Check for Updates…** in the tray), downloads them quietly, and
when one is ready raises a notification and a tray item — **Restart to update (x.y.z)** — so you apply it
when it suits you (it also installs on the next normal quit). Updates come from the same feed the installer
publishes to (GitHub Releases by default, or a fork's self-hosted feed).

A new **Start with Windows** checkbox in the tray menu lets the app launch automatically at login (off by
default). Builds are still unsigned for now, so Windows SmartScreen may warn on first install — code signing
is a later addition.
`.trim(),
    added: [
      "Desktop auto-update (electron-updater): background checks, a manual “Check for Updates…”, and a “Restart to update” tray item with a notification when a new version is ready.",
      "“Start with Windows” tray toggle to launch Diariz at login (off by default).",
    ],
  },
  {
    version: "0.11.1",
    date: "2026-06-27",
    pr: 36,
    headline: "Desktop polish: no menu bar, correctly-titled notifications",
    summary: `
Two small fixes to the Windows desktop app: the unused application **menu bar** is hidden (just the window
title bar remains), and the recording **notifications are now titled "Diariz"** instead of "Electron" (the
app now sets its Windows AppUserModelID).
`.trim(),
    fixed: [
      "Hide the desktop app's menu bar — it's a tray shell and didn't need one.",
      "Desktop notifications are titled \"Diariz\" rather than \"Electron\" (sets the Windows AppUserModelID).",
    ],
  },
  {
    version: "0.11.0",
    date: "2026-06-27",
    pr: 35,
    headline: "Record from the desktop tray menu — phase 2",
    summary: `
The Windows desktop app can now **start and stop recording from its system-tray menu**, without opening
the window. The tray shows **Record Microphone** and **Record System Audio**; while recording they collapse
to a single **Stop Recording (mm:ss)** item with a live timer, and the tray tooltip reflects the state.

Recording runs in the **background** — Windows notifications confirm when it starts and when the finished
clip has uploaded, so you can capture a meeting without leaving the tray. The same recorder powers the
on-screen button, so there's only ever one recording at a time. (The record items are disabled until the
app is loaded and signed in.)
`.trim(),
    added: [
      "Tray-driven recording: Record Microphone / Record System Audio in the tray menu, a live Stop Recording (mm:ss) item while active, and a state-aware tooltip.",
      "Background recording with Windows notifications on start and on upload completion.",
    ],
  },
  {
    version: "0.10.0",
    date: "2026-06-27",
    pr: 34,
    headline: "Windows desktop app (system tray) — phase 1",
    summary: `
A new **Windows desktop app** — a system-tray shell that loads your Diariz server in a native window and
adds microphone + Windows **system/loopback** audio capture (which a browser can't do).

On first run it asks for your **server address** (validated against \`/health\`) and remembers it; the app
then lives in the **system tray** with **Open Diariz**, **Settings…** (change server), and **Quit**, and
closing the window hides it rather than quitting. Because it loads the web app from your server's origin,
everything is same-origin and it rarely needs updating when the web app changes.

It ships as an NSIS installer built on CI and published to GitHub Releases (a fork can point the update
feed at its own server instead). Recording straight from the tray menu, and auto-update, come in later
phases.
`.trim(),
    added: [
      "Windows desktop app: system-tray shell, first-run server-address setup, loads the web app from your server, with mic + system-audio capture.",
      "electron-builder NSIS installer + a tag-triggered GitHub Actions release workflow (provider-configurable: GitHub Releases or a self-hosted feed).",
    ],
  },
  {
    version: "0.9.2",
    date: "2026-06-27",
    pr: 33,
    headline: "Move speaker-count hints into the re-transcribe dialog",
    summary: `
The always-visible **Expected speakers** panel is gone from the recording page. Instead, choosing
**Re-transcribe** from the kebab menu now opens a small dialog that asks for the optional min/max speaker
hints (pre-filled from the recording) before re-transcribing — keeping that exception-case control out of
the way for the normal case.
`.trim(),
    changed: [
      "Speaker-count hints (min/max) are now set in the Re-transcribe dialog instead of an always-on panel.",
    ],
  },
  {
    version: "0.9.1",
    date: "2026-06-27",
    pr: 32,
    headline: "Fix: audio playback & download work over a domain (not just localhost)",
    summary: `
Playing a recording and downloading its audio previously used a presigned MinIO URL pointing at
\`localhost\`, so they failed when the app was accessed from another machine over a domain. The API now
**streams the audio itself, same-origin** (with HTTP range support for seeking) — so playback and download
work behind any reverse proxy / TLS, and MinIO no longer needs to be reachable from the browser.

The \`STORAGE_PUBLIC_ENDPOINT\` setting is no longer needed and has been removed. (Transcript download was
already same-origin and unaffected.)
`.trim(),
    fixed: [
      "Audio playback and “Download audio” work when the app is accessed over a domain/reverse proxy, not only on localhost — the API streams audio same-origin instead of via presigned MinIO URLs.",
    ],
    changed: [
      "Removed the STORAGE_PUBLIC_ENDPOINT / Storage:PublicEndpoint setting (no longer required).",
    ],
  },
  {
    version: "0.9.0",
    date: "2026-06-27",
    pr: 31,
    headline: "Tell the diarizer how many speakers to expect",
    summary: `
When the diarizer **lumps two people into a single speaker**, you can now give it a hint. The recording
page has an **Expected speakers** control — set a **minimum** (e.g. 2) and/or **maximum**, then
re-transcribe, and pyannote is forced to split (or cap) accordingly.

The hints are saved on the recording and re-applied on every re-transcription until you change them; leave
both blank for automatic detection. A normal re-transcribe (from the list/menu) keeps whatever hints you've
set. This biases the diarization rather than guaranteeing it, but it's the direct fix for "two voices, one
speaker label".
`.trim(),
    added: [
      "“Expected speakers” min/max hints on the recording page, forwarded to pyannote on re-transcription, to split speakers it merged (or cap over-splitting).",
    ],
  },
  {
    version: "0.8.0",
    date: "2026-06-27",
    pr: 30,
    headline: "Re-identify speakers on demand & listen to training samples",
    summary: `
Two improvements for curating speaker identification.

- **Re-identify speakers** — a new action on a recording (kebab menu) re-runs identification against your
  **current** voiceprints using the speakers' already-computed embeddings, **without a full
  re-transcription**. After you add/curate training samples, run it to relabel a recording instantly.
  Manually-named speakers are never overwritten.
- **Listen to training samples** — in the **People** screen, each training contribution now has a **▶ Play**
  button that plays that recording from the start of the contributed speaker, so you can tell by ear who a
  sample actually is before keeping or removing it.
`.trim(),
    added: [
      "“Re-identify speakers” action — re-applies voiceprint matching to a recording without re-transcribing.",
      "Play button on each training contribution in People, to hear the sample.",
    ],
  },
  {
    version: "0.7.0",
    date: "2026-06-27",
    pr: 29,
    headline: "Interface tune-up: resizable panels, collapsible groups, tabbed settings",
    summary: `
A batch of interface refinements.

- **Collapsible recording groups** — click a section header to collapse or expand it; your choices are
  remembered.
- **Resizable recordings list** — drag its right edge to widen or narrow the left panel.
- **Settings in tabs** — **AI Settings** and (for the Platform Administrator) **Storage Quotas** are now
  separate tabs with a single **OK / Cancel** at the bottom that saves everything at once.
- **Release Notes** — the list now shows each release's title, and you can drag to resize it.
- **About** box is wider, so the capabilities and disclaimers read more comfortably.
`.trim(),
    changed: [
      "Recording groups collapse/expand from their header (remembered).",
      "The speakers panel on a recording collapses from an arrow in its top-right.",
      "The recordings list and the Release Notes list are drag-resizable.",
      "Settings split into AI Settings / Storage Quotas tabs with one OK/Cancel.",
      "Wider About box; Release Notes list shows release titles.",
    ],
  },
  {
    version: "0.6.0",
    date: "2026-06-27",
    pr: 28,
    headline: "Merge same-speaker rows & email yourself the transcript",
    summary: `
Two additions to the transcript page.

**Merge same-speaker rows** collapses consecutive segments from the same speaker into single, larger
blocks — run it once you've finished correcting speaker assignments to get a cleaner, easier-to-read
transcript. Each block grows to fit its text. It's permanent for that transcript version; re-transcribe
to regenerate the original granular segments.

**Email me the transcript** sends the current transcript to your account's email address, formatted with
bold headings (name, summary, transcript) and a table of timestamp, speaker, and text — handy for
sharing or keeping a copy. Requires the server's email (SMTP) to be configured.
`.trim(),
    added: [
      "“Merge same-speaker rows” on the transcript page — collapses consecutive same-speaker segments into single blocks (permanent; re-transcribe to undo).",
      "“Email me the transcript” — emails the formatted transcript (headings + timestamp/speaker/text table) to your account address.",
    ],
  },
  {
    version: "0.5.0",
    date: "2026-06-27",
    pr: 27,
    headline: "Storage quotas & usage visibility",
    summary: `
Each user now has a **storage quota** for their recorded audio, with usage visible throughout.

The **account menu** shows your storage under your name — e.g. *Storage 1.2 GB / 5 GB (24%)* — and each
recording's size appears on its transcript page. New users are granted a **starter quota** at account
creation; the **Platform Administrator** sets the starter amount and an overall **maximum** in Settings,
and **any administrator** can raise an individual user's quota (up to that maximum) from Manage Users,
where each user's used/quota is shown. Uploads that would exceed your quota are rejected with a clear
message (delete recordings or ask an admin for more). Quota counts audio bytes only — transcripts,
summaries, and other database data don't count against it.

The account menu now shows your **name** instead of your email, and the **sign-up** and **add-user**
forms collect a name up front (pre-filled into account setup).
`.trim(),
    added: [
      "Per-user storage quota (audio): starter + maximum set by the Platform Administrator in Settings; any admin can raise a user up to the maximum in Manage Users.",
      "Storage usage in the account menu (used / quota / %) and per-recording size on the transcript page.",
      "Name field on the sign-up and add-user forms; the account menu shows the name instead of the email.",
      "Existing recordings are backfilled with their stored size on startup, so usage is accurate from day one.",
    ],
    changed: ["Uploads that would exceed your storage quota are now rejected with a clear message."],
  },
  {
    version: "0.4.1",
    date: "2026-06-27",
    pr: 26,
    headline: "Photographic backdrop on the sign-in pages",
    summary: `
A visual polish release. The unauthenticated pages — **Sign in**, **Request access**, and **Account
setup** — now share a photographic backdrop behind their cards, via a small shared \`AuthShell\` wrapper.
The README was also brought up to date with the current feature set (speaker identification, the People
screen, chat, sections, and multi-user roles).
`.trim(),
    changed: ["Sign-in / request-access / account-setup pages now render over a shared background image."],
  },
  {
    version: "0.4.0",
    date: "2026-06-27",
    pr: 25,
    headline: "Manage enrolled people — rename, merge, prune, and erase voiceprints",
    summary: `
Builds on speaker identification with a **People** screen (account menu → **People**) for managing your
enrolled voiceprints in one place.

For each person you can **rename** them (linked speakers update to match), expand to see the **training
contributions** that feed their voiceprint — which recording and speaker each came from — and **remove**
any sample, which recomputes the voiceprint from what remains. You can **merge** two people (e.g. a
duplicate enrolled under a slightly different name): the source's training samples and labelled speakers
move to the target, the voiceprint is recomputed, and the duplicate is removed.

Erasure is now complete: delete a single person, or **erase all your voiceprints** at once. Either way the
stored biometric data and training samples are deleted, past recordings are unlinked, and only the
**auto-applied** labels revert to the anonymous speaker — names you typed by hand are kept.
`.trim(),
    added: [
      "People management screen (account menu → People): list enrolled voiceprints with their sample counts.",
      "Per-person rename (linked speakers follow), view/remove individual training contributions (recomputes the voiceprint), and merge two people.",
      "Erase a single voiceprint or all of them at once (GDPR) — auto-labels revert, hand-typed names kept.",
    ],
  },
  {
    version: "0.3.0",
    date: "2026-06-27",
    pr: 24,
    headline: "Speaker identification — recognise enrolled people across recordings",
    summary: `
Diariz now **identifies speakers**, not just diarizes them. Diarization groups a recording into
anonymous, recording-local speakers (\`SPEAKER_00\`…); identification recognises a **known person** across
recordings by their voice.

The transcription worker now computes a per-speaker **voiceprint** — a **SpeechBrain ECAPA-TDNN**
embedding (192-dimensional, Apache-2.0) — stored in pgvector alongside each speaker. Tag a recording's
speaker as a person ("Alice") to **enrol** their voiceprint; in later recordings, a matching speaker is
**labelled automatically** (shown with an *auto* badge) when the cosine similarity clears a configurable
threshold, and stays anonymous otherwise. You can **reassign** any speaker to a different enrolled person
or unassign them, and a free-text rename always detaches the voiceprint.

Voiceprints are biometric data, so **erasure is first-class**: deleting a person removes their voiceprint
and all training data, unlinks them from past recordings, and reverts only the **auto-applied** labels to
the anonymous speaker — names you typed by hand are kept. Identification is per-user (your voiceprints
only ever match your own recordings) and can be turned off via \`ENABLE_SPEAKER_EMBEDDINGS\` /
\`Identification__Enabled\`.
`.trim(),
    added: [
      "Per-speaker ECAPA voiceprint embeddings computed by the worker and stored in pgvector.",
      "Automatic speaker identification against your enrolled people (tunable cosine threshold, *auto* badge).",
      "Enrol a person from a recording's speaker, reassign/unassign speakers, and GDPR-erase a voiceprint (auto-labels revert, manual names kept).",
    ],
  },
  {
    version: "0.2.1",
    date: "2026-06-27",
    pr: 23,
    headline: "Research note: speaker identification & verification options",
    summary: `
A documentation-only release. Adds a research note,
\`docs/Speaker_Identification_and_Verification.md\`, surveying open-source options for **speaker
identification and verification by embedding comparison** — recognising an enrolled person ("is this Alice?")
across recordings, beyond anonymous diarization.

It covers how embedding-based recognition works (enrol → cosine-match → threshold, with open-set "unknown"),
compares the main toolkits (SpeechBrain ECAPA-TDNN, WeSpeaker, NVIDIA NeMo TitaNet, 3D-Speaker, WavLM,
Resemblyzer), untangles the code / weights / training-data licensing layers (the VoxCeleb non-commercial
caveat), and sketches how identification could reuse the embedding pyannote already computes plus the existing
pgvector storage. Recommendation: **SpeechBrain ECAPA-TDNN** for a true-open-source, lightweight start. No app
behaviour changes.
`.trim(),
    added: ["Research note on open-source speaker-embedding identification/verification (`docs/`)."],
  },
  {
    version: "0.2.0",
    date: "2026-06-27",
    pr: 22,
    headline: "Admins can add users directly from Manage Users",
    summary: `
Administrators can now **create a user by email** from the **Manage Users** modal, without waiting for
that person to request access. Adding a user creates the account and runs the same onboarding as an
approved request: a **one-time setup link** is emailed to them (or shown to the admin to share when SMTP
isn't configured), and they finish by setting their full name and password.

Each user's **onboarding status** is now surfaced as a pill in the modal — *Requested* (awaiting an
admin's grant), *Awaiting setup* (invited, link sent, not yet completed), or *Active* — so it's clear at
a glance where everyone is in the process.
`.trim(),
    added: [
      "“Add user” by email in the Manage Users modal — creates the account and sends the setup link (with the no-SMTP fallback link shown to the admin).",
      "Onboarding status pill per user (Requested / Awaiting setup / Active).",
    ],
  },
  {
    version: "0.1.0",
    date: "2026-06-27",
    pr: 21,
    headline: "First tagged release — versioning, release notes, and an About box",
    summary: `
The first versioned release of Diariz. It introduces a **Major.Minor.Build** scheme, this **Release
Notes** page, and an **About** box in the account menu (with the app version, build, a capabilities
summary, links to these notes and GitHub, third-party disclaimers, and copyright). The API now reports
its version at \`/health\`.

This entry also serves as the baseline summary of everything Diariz does today:

- **Capture** audio from the browser microphone, or Windows system/loopback audio via the Electron
  desktop shell.
- **Transcribe** server-side with WhisperX (faster-whisper large-v3) for word-level timestamps and
  **pyannote 3.1** for speaker diarization, orchestrated through a Redis-stream job queue with live
  status over SignalR.
- **Review** speaker-labelled, timestamped segments: rename speakers (preserved across re-transcribes),
  edit segment text, and play back per-segment or the whole recording. Re-transcribe with a chosen model.
- **Summarise** recordings (with automatic naming) and **chat across one or more transcripts** —
  streaming replies, a context-usage dial, PDF/text attachments, and saved/reloadable conversations —
  via a per-user (or server-default) OpenAI-compatible LLM endpoint, with the API key encrypted at rest.
- **Organise** recordings into sections with drag-and-drop ordering and cross-group moves.
- **Multi-user RBAC**: Standard / Administrator / Platform Administrator roles, an access-request →
  admin-grant → account-setup lifecycle (one-time email link, with an in-app fallback when SMTP is
  unconfigured), and admin user management. Every user's data is isolated to them.
- Light/Dark/Auto theming throughout.
`.trim(),
    added: [
      "Major.Minor.Build versioning with a single canonical source (version.json), surfaced in the About box and at GET /health.",
      "Release Notes page (fixed header, release list, per-release detail).",
      "About box in the account menu: version + build, capabilities, Release Notes & GitHub links, third-party disclaimers, copyright.",
    ],
  },
];
