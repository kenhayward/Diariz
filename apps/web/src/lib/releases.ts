// Release notes + About-box copy. This is the single source for the Release Notes page and the
// About modal. Per the CLAUDE.md versioning rule, every PR adds/updates the newest RELEASES entry
// and RELEASES[0].version must equal the app version (version.json).

export const TAGLINE = "Smart Meeting Transcription";
export const GITHUB_URL = "https://github.com/kenhayward/Diariz";
export const COPYRIGHT = "© Ken Hayward";
export const LICENSE = "AGPL-3.0";

/// Short summary of what the app does today, shown in the About box (markdown). Update this whenever
/// the app's scope changes.
export const CAPABILITIES = `
Diariz records audio — from your microphone (**choose a specific input and tune capture**, with a **live
input-level meter** while recording), or Windows
system/loopback audio via the desktop app
(which can also **start and stop recording straight from its system-tray menu**) — or you can **upload
an existing audio file** (WAV, MP3, FLAC, Ogg/Opus, WebM, M4A), and it transcribes it server-side with
**WhisperX** (word-level timestamps) and **pyannote** speaker
diarization. You get speaker-labelled, timestamped segments you can rename, edit, and play back
(per segment, per speaker, or the whole recording), and can re-transcribe at any time. Edits are kept **separately from
the model's original words**, so a **Show original / Show revised** toggle always gets you back to what the
model said. You can **merge** consecutive same-speaker rows into single blocks and **email yourself the
transcript**. The recording page is organised into **tabs** — Overview, Minutes, Actions, Speakers, Transcript,
and Attachments — each with its own toolbar.

It can **identify speakers** across recordings: enrol a person from a recording's speaker and Diariz
recognises that voice in later recordings automatically (using **SpeechBrain ECAPA** voiceprints), with
manual reassignment. A **People** screen manages enrolled voiceprints — rename, prune training samples,
merge duplicates, and erase one or all (GDPR) of the stored biometric voiceprints.

It can **summarise** recordings and generate a full set of **professional meeting minutes** (headings, lists,
tables — no emojis) that you can edit in a rich editor, re-create, and **email to yourself** (with or without
the recording's attachments). It **automatically extracts action items** (with actor and deadline) as part of
the pipeline into an editable table, and **tracks them across all your meetings** in a dedicated **Actions** view — filter by person, mark
items done (with a completion date), and jump from an action back to the transcript it came from. It can
**translate** a transcript (segments, summary, and actions) into your chosen language, and let you
**chat across one or more transcripts** — with file attachments, a context-usage dial, and saved
conversations — using an OpenAI-compatible LLM endpoint you configure. The chat can also call **built-in
tools** that search your **whole transcript library** — who said a phrase, what a person said about a topic,
which recordings exist or mention something, action items, summaries, attendees, talk time, and the lines
around a moment — answering with **When · Who · What** and **linking back to the transcript** (click a link
to open it and jump to the exact segment). It can also **email you** (yourself only) a message it composes —
a summary, action items, or notes — always delivering to your own registered address (and it **files a copy
of that email onto the transcript** as a Markdown attachment), or **save that output
to a transcript as a Markdown attachment**. Toggle
the tools on, and choose which, under Settings → AI — where you
can also **enable reasoning** (and pick a level) for reasoning-capable models. You can **attach supporting
documents** (files or URLs) to a recording, open them from the transcript page, and optionally **feed them
to the chat** (PDFs, text, Office docs, emails/calendar invites are read into text; URLs are fetched).
Recordings organise into **sections** (with sub-sections) and drag-and-drop ordering, can be **merged**
into one, and can be browsed as a **list, a calendar, or a cross-meeting Actions list** (the left panel is
**Meetings**). A **status bar** along the bottom shows live progress (transcribing, summarising, merging,
extracting, uploading) and your storage usage, transcription usage, and total transcripts.

Diariz is **multi-user** with role-based access: people request access (or an administrator adds them),
an administrator approves, and each user sets up their own account and keeps their own private
recordings, transcripts, and chats. Each user has a **storage quota** (audio): the Platform
Administrator sets the starter and maximum, any administrator can raise an individual user, and your
usage shows in the account menu. The Platform Administrator can also **back up and restore the whole
platform** (database + stored files) as a single transferable archive from Settings → Maintenance.

The interface is **localized** — pick your language (English, Spanish, French, German today) at signup or
in Preferences, and downloaded/emailed transcripts use it too. Translations are community-extensible via
simple JSON files.
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
    version: "0.62.6",
    date: "2026-07-02",
    pr: 142,
    headline: "Calmer Dependabot cadence",
    summary:
      "Dependabot now runs **monthly** and produces **one grouped PR per ecosystem** instead of many " +
      "individual weekly PRs. Maintenance only — no effect on the app.",
    changed: [
      "Dependabot version updates are monthly and grouped per ecosystem (npm web, npm desktop, nuget, pip, github-actions), cutting update-PR volume; security alerts/updates are unaffected.",
    ],
  },
  {
    version: "0.62.5",
    date: "2026-07-02",
    pr: 141,
    headline: "Harden the /tools table escaping",
    summary:
      "The `/tools` command builds a Markdown table from the tool names/descriptions. The escaping only " +
      "handled pipe characters; it now also escapes backslashes and collapses newlines, so a value can't " +
      "distort the table. (Flagged by CodeQL as incomplete sanitisation; the input is app-defined, so it " +
      "was cosmetic rather than exploitable.)",
    fixed: [
      "The /tools Markdown table now escapes backslashes and newlines in tool labels, not just pipes, so the table always renders intact.",
    ],
  },
  {
    version: "0.62.4",
    date: "2026-07-02",
    pr: 140,
    headline: "CodeQL security scanning (incl. C#)",
    summary:
      "Adds a CodeQL code-scanning workflow covering **C#, JavaScript/TypeScript, Python, and GitHub " +
      "Actions**. The C# leg does a real .NET build so the API is analysed properly (GitHub's default " +
      "setup can't build C#). Maintenance only — no effect on the app.",
    added: [
      "A CodeQL workflow (.github/workflows/codeql.yml) scanning C# (built), JS/TS, Python, and Actions on push, PR, and weekly.",
    ],
  },
  {
    version: "0.62.3",
    date: "2026-07-02",
    pr: 122,
    headline: "Repository hardening for going open-source",
    summary:
      "Housekeeping to prepare the source repository to be made public: a Dependabot config, a security " +
      "policy, contributor and attribution files, and NuGet lock files for precise dependency tracking. " +
      "No user-facing behaviour changes. Also corrects the licence shown in the About box to **AGPL-3.0** " +
      "(it previously read Apache-2.0).",
    added: [
      "Repository meta files for open-sourcing: .github/dependabot.yml, SECURITY.md, NOTICE, CONTRIBUTING.md, and CODEOWNERS.",
      "NuGet lock files (packages.lock.json) so the dependency graph and Dependabot track transitive .NET packages precisely.",
    ],
    fixed: [
      "The About box now shows the correct licence (AGPL-3.0) instead of Apache-2.0.",
      "Updated the transitive Microsoft.OpenApi dependency to 2.7.5 to clear a high-severity advisory (GHSA-v5pm-xwqc-g5wc).",
      "Attachment-text extraction now strips a smuggled directory path from the file name on every platform (it previously relied on the OS-dependent Path.GetFileName).",
    ],
  },
  {
    version: "0.62.2",
    date: "2026-07-02",
    pr: 121,
    headline: "CI moved to GitHub-hosted runners",
    summary:
      "Continuous integration now runs on **GitHub-hosted runners** instead of a self-hosted machine — the " +
      ".NET, web, worker, and locale checks run on `ubuntu-latest` and the desktop installer builds on " +
      "`windows-latest`. This is a maintenance change with no effect on the app itself; it's a prerequisite " +
      "for making the source repository public safely.",
    changed: [
      "CI runs on GitHub-hosted runners (ubuntu-latest for tests, windows-latest for the desktop installer) rather than a self-hosted runner; the .NET SDK and Python are now provisioned by setup actions.",
    ],
    fixed: [
      "Attachment names now strip a smuggled directory path on every platform. The previous sanitisation relied on Path.GetFileName, which only strips backslashes on Windows — so on the Linux production servers a name like “..\\..\\secret” slipped through unstripped. (Surfaced by running the tests on Linux.)",
    ],
  },
  {
    version: "0.62.1",
    date: "2026-07-02",
    pr: 120,
    headline: "Re-identify speakers now shows its progress",
    summary:
      "Re-identifying speakers worked but gave no feedback while it ran. It now shows a **“Re-identifying " +
      "speakers…”** progress message in the status bar (like the other actions) and disables its button " +
      "until it finishes.",
    fixed: [
      "Re-identify speakers now shows a “Re-identifying speakers…” progress message in the status bar while it runs, and disables the button until it completes.",
      "The status bar is shorter, and its top border now matches the side-panel borders (so the panels no longer look like they continue below it).",
    ],
  },
  {
    version: "0.62.0",
    date: "2026-07-02",
    pr: 119,
    headline: "A status bar along the bottom of the app",
    summary:
      "A **status bar** now sits locked along the bottom of the page (it never scrolls). On the left it shows " +
      "**progress messages** in their usual colours — transcribing, summarising, merging, extracting actions, " +
      "uploading, and errors — for work on any of your recordings. On the right it shows, `|`-delimited, your " +
      "**storage usage**, **transcription usage**, and **total number of transcripts** (the same figures as the " +
      "account menu).",
    added: [
      "A bottom status bar: left-aligned progress messages (transcribing/summarising/merging/extracting/uploading, in their tone colours) and right-aligned storage usage · transcription usage · total transcripts.",
    ],
  },
  {
    version: "0.61.1",
    date: "2026-07-02",
    pr: 118,
    headline: "Fix: merging recordings failed at the upload step",
    summary:
      "Merging two or more recordings failed while saving the combined audio (the worker logged a " +
      "`FileNotFoundError: 'recordings'`). The worker was passing the storage bucket name where the local " +
      "file path belongs — boto3's upload and download take their file/bucket arguments in opposite orders. " +
      "Fixed, with a regression test that checks the argument wiring directly (the worker's other tests stub " +
      "the upload wholesale, so it went unnoticed).",
    fixed: [
      "Merging recordings no longer fails at the final upload step (the worker uploaded with boto3's arguments in the wrong order, raising FileNotFoundError: 'recordings').",
    ],
  },
  {
    version: "0.61.0",
    date: "2026-07-02",
    pr: 117,
    headline: "Overview tab gains meeting date/time/duration; minutes' actions now match the Actions tab exactly",
    summary:
      "The **Overview** tab now opens with **Meeting Date** (long form in your language, e.g. \"23rd March " +
      "2026\"), **Meeting Time** (24-hour hh:mm), and **Duration** (hh:mm), followed by a **Summary** heading and " +
      "the summary text. And the **Meeting Minutes** no longer let the model write its own action list — the " +
      "minutes' **Action Items** are now compiled **deterministically from the recording's tracked actions** (the " +
      "Actions tab), so the two always match. The minutes prompt is explicitly told not to produce an action-items " +
      "section; the canonical table is appended automatically.",
    added: [
      "The Overview tab shows Meeting Date (extended, localized), Meeting Time (24-hour), and Duration (hh:mm) above the summary, under a Summary heading.",
    ],
    changed: [
      "Meeting minutes' Action Items are now built deterministically from the recording's tracked actions (the Actions tab) instead of being generated by the model — the minutes and the Actions tab always list the same items.",
      "The chat /tools command now shows the enabled tools as a two-column table (Tool / What it does).",
      "The chat input hints \"type / for the command list\" until you start typing.",
    ],
    fixed: [
      "Transcript deep-links in chat answers now open the Transcript tab and scroll to the cited moment (after the recording page moved to tabs, they landed on the wrong tab).",
    ],
  },
  {
    version: "0.60.0",
    date: "2026-07-02",
    pr: 116,
    headline: "Recording page redesigned into tabs",
    summary:
      "The recording page's stacked panels are now a row of **tabs** — **Overview** (the summary), " +
      "**Minutes**, **Actions**, **Speakers**, **Transcript**, and **Attachments** — so you see one section at " +
      "a time. Each tab shows **its own toolbar** directly beneath the tab strip, then its content, and the " +
      "strip + toolbar stay pinned while the content scrolls. **Attachments** now have a dedicated tab where you " +
      "add files or links and rename/open/delete them (the old \"Attachments\" button above the panels is gone; " +
      "drag-and-drop-to-attach still works). The last tab you used is remembered.",
    changed: [
      "The recording page is now organised into horizontal tabs (Overview / Minutes / Actions / Speakers / Transcript / Attachments) instead of stacked collapsible panels — each tab has its toolbar directly below the tab strip.",
      "Attachments have their own tab (add file/URL, rename, open, delete); the separate Attachments button and its pop-up manager above the panels were removed. Drag-and-drop onto the page still attaches files.",
    ],
  },
  {
    version: "0.59.0",
    date: "2026-07-02",
    pr: 115,
    headline: "\"Send email to me\" also saves a copy to the transcript",
    summary:
      "When the chat's **Send email to me** tool sends you an email, it now also **saves a copy onto the " +
      "transcript** as a Markdown attachment, titled **\"Email: <subject>\"** with the email body as its " +
      "content. It uses the same destination flow as **Add as attachment**: with one transcript in the chat's " +
      "context the copy is added there; with several selected, you pick which one. If no transcript is in " +
      "context the email is still sent — there's just nothing to attach the copy to.",
    added: [
      "The \"Send email to me\" chat tool now files a copy of each sent email onto the transcript as a Markdown attachment (\"Email: <subject>\"), using the same single/pick-one selection as Add as attachment.",
    ],
  },
  {
    version: "0.58.0",
    date: "2026-07-02",
    pr: 114,
    headline: "More chat slash commands + an autocomplete popup",
    summary:
      "Building on `/tools` and `/help`, chat gains more **client-side commands** (handled in the browser, " +
      "never sent to the model): **`/clear`** starts a new conversation, **`/context`** shows what the chat " +
      "can currently see (scope, transcript count, model, token usage), **`/save`** and **`/load`** manage " +
      "saved conversations, **`/copy`** copies the assistant's last reply, and **`/retry`** re-asks the last " +
      "question. Typing **`/`** now opens a small **autocomplete popup** listing the matching commands — click " +
      "one (or press Enter) to run it.",
    added: [
      "Chat commands: /clear (new conversation), /context (show current scope/model/token usage), /save, /load, /copy (last reply), /retry (re-ask last question).",
      "An autocomplete popup when the chat input starts with \"/\", listing the matching commands.",
    ],
  },
  {
    version: "0.57.0",
    date: "2026-07-02",
    pr: 113,
    headline: "Chat slash commands (/tools, /help) + emailed notes render properly",
    summary:
      "Chat now understands **slash commands**, handled entirely in the browser and **never sent to the " +
      "model**: **`/tools`** lists the chat tools you have enabled, and **`/help`** lists the commands. " +
      "Because they're client-side, typing `/tools` always just shows the list — it can no longer be " +
      "misread by the model as a request to run a tool. Fix: notes sent by the **Send email to me** tool now " +
      "render **properly formatted** — tables, lists and bold come through as formatting instead of raw " +
      "Markdown (the email previously used a basic renderer that didn't handle GitHub-flavoured Markdown).",
    added: [
      "Chat slash commands handled client-side (never sent to the model): /tools lists your enabled tools, /help lists the commands.",
    ],
    fixed: [
      "The \"Send email to me\" tool rendered the assistant's Markdown with a basic renderer, so tables/lists could arrive as raw Markdown — it now uses the full renderer and an email-friendly layout.",
    ],
  },
  {
    version: "0.56.0",
    date: "2026-07-01",
    pr: 112,
    headline: "New chat tools: email yourself a message, or save the assistant's output to a transcript",
    summary:
      "The chat assistant gains two **write** tools. **Send email to me** composes a subject and body and " +
      "emails it to you — it can **only ever email you** (your own registered account address, no way to send " +
      "to anyone else). **Add as attachment** saves content it has prepared (a summary, notes, a table…) onto a " +
      "transcript as a Markdown attachment: with one transcript in the chat's context it adds it there; with " +
      "several selected, you pick which one from a short list. Both are **on by default** with the other tools " +
      "(toggle any of them under **Settings → AI**). The chat also now knows **who you are** (your name and " +
      "email are part of its context), so the messages it writes read as being from you.",
    added: [
      "A \"Send email to me\" chat tool — the assistant composes a subject and body and emails it to your own account address.",
      "An \"Add as attachment\" chat tool — saves the assistant's output to a transcript as a Markdown attachment (you pick the transcript when several are selected).",
    ],
    changed: [
      "The chat context now includes the signed-in user's name and email, so the assistant knows who it is helping and can sign emails as them.",
    ],
  },
  {
    version: "0.55.0",
    date: "2026-07-01",
    pr: 111,
    headline: "Action items extract automatically — and the minutes now use the same set",
    summary:
      "**Action items now extract automatically** as part of the transcription pipeline (when an LLM endpoint " +
      "is configured), so a recording arrives with its actions already pulled out. The automatic pass runs once " +
      "and never overwrites actions you've added or edited; the panel's refresh button still re-extracts on " +
      "demand. The **extractor was reworked** to be more thorough (it was previously too conservative and " +
      "missed items the meeting minutes caught): it now reasons before answering and captures decisions and " +
      "follow-ups, not only explicit commitments. The **meeting minutes are generated from that same canonical " +
      "action set**, so the minutes' Action Items table and the recording's Actions panel always match. The " +
      "**Meeting Minutes** panel is now **always shown** (collapsed) even before minutes exist, with a refresh " +
      "button to generate them. The meeting-minutes prompt also receives the **meeting time** (a new " +
      "`{meeting_time}` placeholder) in addition to the date.",
    added: [
      "Action items are extracted automatically during the transcription pipeline (in-process actions worker), gated on the same LLM config as the summary.",
      "The meeting-minutes prompt gains {meeting_time} and {action_items} placeholders (the recording's time of day, and the extracted action set).",
    ],
    changed: [
      "Reworked the action-extraction prompt for completeness (reason-first, capture decisions/follow-ups, higher temperature) so it no longer under-reports.",
      "Meeting minutes now render the recording's already-extracted action items verbatim — so the minutes' Action Items table matches the Actions panel exactly (the pipeline runs actions, then the minutes).",
      "The Meeting Minutes panel is always shown (collapsed) with a refresh button to generate them, even when the recording has none yet.",
      "Automatic action extraction runs once per recording and never clobbers manually added/edited actions (an explicit re-extract still replaces them).",
    ],
  },
  {
    version: "0.54.1",
    date: "2026-07-01",
    pr: 110,
    headline: "Recording-page panel polish + README catch-up",
    summary:
      "Small refinements to the recording page's collapsible panels, plus a documentation catch-up. Each " +
      "panel's **re-run (refresh) icon now sits at the end of its toolbar**, just before the collapse chevron, " +
      "so the refresh buttons line up vertically down the Summary / Meeting Minutes / Actions / Speakers panels. " +
      "You can once again **click a panel's header** (anywhere outside its toolbar) to expand or collapse it, not " +
      "just the chevron. The **vertical spacing between the recording page's sections was roughly halved** so " +
      "more fits on screen. The **README**'s feature list was also brought in line with the recent " +
      "transcript-panel work (per-speaker talk time, the play bar plays the whole recording, the always-available " +
      "Action items panel).",
    changed: [
      "Each panel's refresh icon moved to the end of its toolbar (before the chevron) so the refresh buttons line up across the Summary, Meeting Minutes, Actions, and Speakers panels.",
      "Clicking a panel header (outside the toolbar) again toggles expand/collapse, not just the chevron.",
      "Roughly halved the vertical spacing between the recording page's sections.",
      "README feature list updated to match the 0.54.0 transcript-panel changes (per-speaker talk time, play-bar plays the whole recording, always-available Action items panel).",
    ],
  },
  {
    version: "0.54.0",
    date: "2026-07-01",
    pr: 109,
    headline: "Transcript panel polish + a generate-minutes shortcut and a delete fix",
    summary:
      "A batch of transcript-page refinements. The **Actions** (kebab) menu gains **Generate meeting minutes**, " +
      "so recordings that predate the feature (or never got minutes) can create them without the minutes panel. " +
      "The **Speakers** panel is cleaner — the redundant second name box is gone, the assignment box is wider " +
      "(any name fits) and the rows line up — and each speaker now shows their **total talk time** next to the " +
      "segment count. The **Action items** panel is now always present (starting collapsed) with its own " +
      "**refresh** button to (re-)extract, so extraction moves off the main toolbar; the **Summary** and " +
      "**Speakers** re-run controls use the same refresh icon. The **Summary** panel now sits **above** Meeting " +
      "Minutes and both start **collapsed**, and every panel's **collapse/expand chevron moved to the end** of " +
      "its header (after the toolbar). The redundant **Play all** button is gone (the player bar already plays " +
      "the whole recording). Bug fix: deleting the open recording from the list no longer leaves its transcript " +
      "on screen (which made follow-up actions fail) — the page clears on delete from either the list or the " +
      "transcript menu.",
    added: [
      "\"Generate meeting minutes\" in the transcript Actions (kebab) menu.",
      "Each speaker in the Speakers panel now shows their total talk time alongside the segment count.",
    ],
    changed: [
      "Speakers panel: removed the duplicate name field, widened the assignment box, and aligned the rows.",
      "The Action items panel is always shown (starting collapsed) with a refresh button to extract/re-extract; the standalone \"Extract actions\" toolbar button is gone.",
      "Re-run controls (Summary, Speakers re-identify, Actions extract) share a consistent refresh icon.",
      "Removed the redundant \"Play all\" button — the player bar already plays the whole recording.",
      "Summary panel moved above Meeting Minutes; both start collapsed.",
      "Each panel's collapse/expand chevron is now the last control in the header (after the toolbar).",
    ],
    fixed: [
      "Deleting the currently-open recording (from the list or the transcript menu) now clears the transcript page instead of leaving a stale, un-actionable transcript on screen.",
    ],
  },
  {
    version: "0.53.0",
    date: "2026-07-01",
    pr: 108,
    headline: "Meeting Minutes — a formatted, emailable write-up of every recording",
    summary:
      "Every transcribed recording now gets a set of **professional meeting minutes** — generated as part of " +
      "the pipeline from the transcript, with headings, lists, tables, and bold (and deliberately **no emojis**). " +
      "The new **Meeting Minutes** section on the recording page starts expanded and has a toolbar: **Re-create** " +
      "them, **Edit** them in a rich (WYSIWYG) editor, or **Email them to yourself**. Emailing sends only the " +
      "minutes, and — when the recording has attachments — asks whether to include them. The minutes also ride " +
      "along in the existing **Email transcript** action and the **Markdown / text / RTF downloads**. Minutes use " +
      "the same LLM endpoint you already configure for summaries.",
    added: [
      "A Meeting Minutes section on each recording (expanded by default) with Re-create, Edit (WYSIWYG), and Email-to-me.",
      "\"Email minutes to me\" sends only the minutes and offers to include the recording's attachments.",
      "Meeting minutes are included in the emailed transcript and in the .md / .txt / .rtf downloads.",
    ],
    changed: [
      "Minutes are generated automatically alongside the summary when an LLM endpoint is configured.",
      "The summarise, action-extraction, and meeting-minutes instruction prompts each live in an editable server file (prompts/*.md) — administrators can tweak the wording without a rebuild.",
    ],
  },
  {
    version: "0.52.0",
    date: "2026-07-01",
    pr: 107,
    headline: "Per-speaker toolbar in the transcript, and a collapsed summary",
    summary:
      "The transcript's **Speakers** panel is now one compact line per speaker, showing the segment **count** " +
      "and a small **toolbar**: play/pause that speaker, **jump to their previous / next segment**, and a " +
      "**delete** that removes all of that speaker's segments (with confirmation). Deleting a speaker's segments " +
      "removes them from the list — and now also clears their stored voiceprint for the recording, so the data " +
      "matches what you see. The **Summary** section also starts **collapsed** by default, so the transcript is " +
      "right there when you open a recording.",
    added: [
      "Each speaker row gains a segment count and a toolbar: play, delete-all-segments (confirmed), and previous/next-segment navigation.",
    ],
    changed: [
      "The Speakers panel is one line per speaker.",
      "The Summary section starts collapsed by default.",
      "Deleting all of a speaker's segments now also prunes that speaker's stored voiceprint for the recording.",
    ],
  },
  {
    version: "0.51.0",
    date: "2026-07-01",
    pr: 106,
    headline: "Denser recordings list and scrollable user/people tables",
    summary:
      "The **recordings list** is now much more compact — section headers and rows are roughly half the height, " +
      "so more meetings fit on screen. Each recording is a single line (name + duration); the source and date " +
      "moved into the **hover tooltip**. In the account menu, **Manage Users** and **People** are now proper " +
      "tables with a **sticky header** and a scrollable body, so the title, Add-User form, and column headings " +
      "stay put while long lists scroll — much better with many users or enrolled voiceprints. People keeps its " +
      "expandable per-person training-sample detail (play / remove).",
    changed: [
      "Recordings list is denser: single-line rows (source · date shown on hover) and shorter section headers.",
      "Manage Users is a columnar, scrollable table (User / Type / Status / Storage / Actions) with a sticky header.",
      "People is a scrollable table (one row per person) with a sticky header, keeping the expandable training-sample view.",
    ],
  },
  {
    version: "0.50.0",
    date: "2026-07-01",
    pr: 105,
    headline: "See your microphone level while recording",
    summary:
      "While a recording is in progress the toolbar now shows a **live input-level meter** — a small bar next " +
      "to the timer that fills green→amber→red with the sound coming in, so you can see at a glance that your " +
      "microphone is actually being picked up. It reads the same audio that's being recorded (nothing is echoed " +
      "to your speakers). If the input stays silent for about 15 seconds a **subtle grey hint** appears in case " +
      "the mic is muted — it's deliberately patient so normal pauses in a meeting don't trigger it, and it " +
      "vanishes the moment sound returns.",
    added: [
      "A live input-level meter next to the recording timer, so you can confirm your microphone is being received.",
      "A subtle 'no sound detected' hint after ~15s of silence while recording (clears as soon as sound returns).",
    ],
  },
  {
    version: "0.49.2",
    date: "2026-07-01",
    pr: 104,
    headline: "Tidier, simpler microphone picker",
    summary:
      "Cleanups to the microphone dropdown. It no longer lists the same physical mic three times — " +
      "Windows/Chromium expose synthetic **“Default - …”** and **“Communications - …”** entries that are just " +
      "aliases of a real device, so those are dropped (the **Microphone (default)** entry at the top already " +
      "covers the OS default), and the trailing USB hardware code (e.g. **(046d:0ab1)**) is stripped for " +
      "readability — a Blue Yeti now shows once as “Microphone (Yeti Nano)”. The awkward **“Allow microphone to " +
      "list devices…”** link is also gone: opening the picker now triggers the browser's normal microphone " +
      "prompt, and if no mic is available you get a plain **“No microphone detected — check your browser and " +
      "system microphone permissions”** hint instead of a click-then-error.",
    fixed: [
      "Microphone dropdown no longer duplicates each physical mic as Default/Communications aliases, and drops the (vendor:product) hardware code from device names.",
      "Replaced the “Allow microphone to list devices…” link with the browser's natural permission prompt (on opening the picker) plus a clear no-microphone hint.",
    ],
  },
  {
    version: "0.49.1",
    date: "2026-07-01",
    pr: 103,
    headline: "Microphone picker fixes: connected mics now list, and the settings popover closes",
    summary:
      "Two fixes to the new microphone controls. Your **connected microphones now appear in the dropdown** " +
      "whenever the browser already has mic access (e.g. after you've recorded once) — the previous build " +
      "gated the device list on a `navigator.permissions` query that is unreliable (it reports “prompt” in the " +
      "desktop app and some browsers even when access is granted), so specific mics could stay hidden. " +
      "Enumerating devices never triggers a permission pop-up, so it now always runs. Separately, the " +
      "**⚙ audio-settings popover** now closes on an outside click or Escape and has an explicit **✕ close** " +
      "button for discoverability.",
    fixed: [
      "Connected microphones now list in the recorder dropdown when mic access is already granted (device enumeration no longer gated on the unreliable permissions query).",
      "The audio-settings (⚙) popover closes on outside-click / Escape and has a close button.",
    ],
  },
  {
    version: "0.49.0",
    date: "2026-07-01",
    pr: 102,
    headline: "Choose a specific microphone and tune capture",
    summary:
      "The recorder's source dropdown now lists **Microphone (default)**, then each **specific microphone** " +
      "connected to your machine, with **System audio** at the bottom — so you can capture from a particular " +
      "headset or interface instead of just the OS default. Your choice is **remembered** between sessions, and " +
      "the list refreshes live when you plug or unplug a device. A new **⚙ audio-settings** popover next to the " +
      "dropdown lets you tune microphone capture: **echo cancellation**, **noise suppression**, **auto gain**, and " +
      "**mono** (all on by default, matching previous behaviour). Device labels appear once you grant microphone " +
      "access (there's an **Allow microphone to list devices…** prompt, or just record once). Desktop tray recording " +
      "honours whichever microphone you've selected.",
    added: [
      "Pick a specific microphone from the recorder's source dropdown (Microphone (default) → each device → System audio); the choice is remembered and the list refreshes on hot-plug.",
      "An audio-settings (⚙) popover to toggle echo cancellation, noise suppression, auto gain, and mono for microphone capture.",
    ],
  },
  {
    version: "0.48.0",
    date: "2026-06-30",
    pr: 101,
    headline: "Reasoning controls for the LLM, plus a batch of settings & action tweaks",
    summary:
      "Settings → AI gains a **reasoning** toggle and **level** (Low / Medium / High). Turn it on and Diariz " +
      "sends an OpenAI-style `reasoning_effort` on every LLM request — summaries, action extraction, " +
      "translation and chat — so reasoning-capable models think before answering (leave it off for models " +
      "that don't support it; it falls back to the server default). Alongside it, a handful of smaller fixes: " +
      "the **edit-action** text box now grows to fit its contents, the actions toolbar's **Select** tooltip " +
      "reads “Select actions” and leaving Select mode clears the selection (so the count badge disappears), and " +
      "the **platform backup** filename now ends with the app version (e.g. `…-V0_48_0.zip`) so you can match an " +
      "archive to a build at a glance.",
    added: [
      "Settings → AI: enable reasoning and pick a level (Low / Medium / High); applied as reasoning_effort across summaries, actions, translation and chat (per-user, with a server default).",
    ],
    changed: [
      "The edit-action dialog's action field auto-expands vertically as you type.",
      "The actions toolbar Select tooltip now reads “Select actions”, and turning Select mode off clears the selection.",
      "Platform backup archives are now named with the app version suffix (…-Vx_xx_x.zip).",
    ],
  },
  {
    version: "0.47.0",
    date: "2026-06-30",
    pr: 100,
    headline: "Transcript panel: pin-on-scroll, an icon toolbar, and segment selection",
    summary:
      "The transcript panel is reworked. As you scroll down it now **pins to the top** and the segments scroll " +
      "**inside** it (scroll back up and the page takes over again), so the controls stay in reach on long " +
      "transcripts. Those controls move into a compact **icon toolbar** in the panel header — with a small play " +
      "progress bar — and gain a **Select mode**: tick segments (or just click one) and act on the selection. " +
      "**Play selected** plays only those segments gaplessly, **Edit** opens a single segment, **Translate** and " +
      "**Delete** act on the selection (Delete confirms the count), while **Play all** and **Merge** always work " +
      "on the whole transcript. Clicking a segment no longer auto-plays — it just selects/highlights it.",
    added: [
      "The transcript panel pins to the top once reached and scrolls its segments internally.",
      "Header icon toolbar: Play all · Play selected · Merge · Select · Edit · Translate · Delete, plus a small play progress bar.",
      "Select mode with per-segment checkboxes; Play selected, Translate selected and Delete selected (with a count confirm) act on the picked segments.",
    ],
    changed: [
      "Clicking a transcript segment now selects/highlights it instead of immediately playing.",
    ],
  },
  {
    version: "0.46.1",
    date: "2026-06-30",
    pr: 99,
    headline: "Fix: speaker assignment dropdown was clipped / scrolled the panel",
    summary:
      "On the Speakers panel, opening the assignment dropdown on a bottom-row speaker cut its options off at " +
      "the panel's edge (you couldn't pick one), and opening it scrolled the panel so the “Speakers” header " +
      "slid out of view. The panel was clipping its own pop-ups; it no longer does, so the dropdown shows in " +
      "full and the header stays put.",
    fixed: [
      "Speaker assignment dropdown is no longer clipped at the bottom of the Speakers panel, and opening it no longer scrolls the panel header away.",
    ],
  },
  {
    version: "0.46.0",
    date: "2026-06-30",
    pr: 98,
    headline: "Action management — a cross-meeting Actions view with completion tracking",
    summary:
      "The left panel is now **Meetings** and gains an **Actions** tab listing every action item across all " +
      "your transcripts in one place. Filter by person, click an action's title to jump to the transcript it " +
      "came from, and mark actions **done** (with a completion date) — individually or in bulk via a Select " +
      "mode like the recordings list. A **Hide completed** toggle clears finished items out of the way. The " +
      "per-transcript actions table gains the same **Done** checkbox and **Completed** date column. Completion " +
      "is reversible.",
    added: [
      "Actions tab in the left panel: all action items across every meeting, filterable by person.",
      "Mark actions complete/incomplete (with a completion date) — inline on a transcript, or in bulk from the Actions tab.",
      "Each action links back to the transcript it was raised in; edit an action from the Actions tab.",
      "Hide completed toggle to focus on outstanding work.",
    ],
    changed: [
      "The left panel is renamed “Meetings” (it now covers both transcripts and the actions arising from them).",
    ],
  },
  {
    version: "0.45.5",
    date: "2026-06-30",
    pr: 97,
    headline: "Docs: AMD ROCm worker needs native Linux (not WSL2)",
    summary:
      "Documents a gotcha for the experimental AMD ROCm worker: it needs the native-Linux GPU device " +
      "`/dev/kfd`, which **WSL2 / Docker Desktop on Windows does not expose**, so it fails to start there with " +
      "*“no such file or directory”* for `/dev/kfd`. The worker README and `docker-compose.rocm.yml` now spell " +
      "this out and point Windows/WSL2 users at the CPU-only fallback (standard compose, `WORKER_DEVICE=cpu`). " +
      "Docs only — no behaviour change.",
    changed: [
      "Worker README + docker-compose.rocm.yml note the AMD ROCm worker requires native Linux (not WSL2) and link the CPU-only fallback.",
    ],
  },
  {
    version: "0.45.4",
    date: "2026-06-30",
    pr: 96,
    headline: "Fix: AMD ROCm worker image failed to build (pkg_resources)",
    summary:
      "The experimental AMD ROCm worker image (`Dockerfile.rocm`) stopped building: openai-whisper's setup " +
      "script imports `pkg_resources`, which the latest `setuptools` no longer ships, so its wheel build " +
      "aborted with *“No module named 'pkg_resources'”* on the `rocm/pytorch` base. The build now constrains " +
      "`setuptools` to a version that still provides `pkg_resources`, so the image builds again. AMD-only; the " +
      "NVIDIA worker, API, and web are unaffected.",
    fixed: [
      "AMD ROCm worker image (docker-compose.rocm.yml) builds again — pin setuptools<81 for the openai-whisper wheel build.",
    ],
  },
  {
    version: "0.45.3",
    date: "2026-06-30",
    pr: 95,
    headline: "Fix: the recordings list now auto-scrolls while you drag",
    summary:
      "Reordering a recording or section by drag-and-drop was awkward across a long list: if the drop target " +
      "sat off-screen, the list didn't scroll to reveal it, so you couldn't reach it. Now, dragging near the " +
      "top or bottom edge of the list (or the calendar day's list) scrolls it automatically — faster the closer " +
      "you are to the edge — so you can drop anywhere in a large structure.",
    fixed: [
      "The recordings list auto-scrolls when you drag a recording or section near its top/bottom edge, so off-screen drop targets are reachable.",
    ],
  },
  {
    version: "0.45.2",
    date: "2026-06-30",
    pr: 94,
    headline: "Deploy: one .env works for both the NVIDIA and AMD stacks",
    summary:
      "Tidies the two Docker Compose stacks so a single `.env` is safe for either. The Whisper ASR backend " +
      "is no longer an env var — it's intrinsic to the compose file you run (the AMD `docker-compose.rocm.yml` " +
      "hardcodes openai-whisper; the NVIDIA stack uses faster-whisper), so a shared `.env` carried between " +
      "machines can't accidentally force the wrong backend and break the AMD worker. No app behaviour change.",
    changed: [
      "The AMD ROCm compose hardcodes the openai-whisper ASR backend; WORKER_ASR_BACKEND removed from .env.example.",
    ],
  },
  {
    version: "0.45.1",
    date: "2026-06-30",
    pr: 93,
    headline: "Fix: platform backup download failed immediately",
    summary:
      "The 0.45.0 backup download failed straight away (the browser showed a tiny, broken `.zip`). The server " +
      "was streaming the archive directly to the HTTP response, but the zip writer needs synchronous writes " +
      "that the web server disallows on the network stream, so it aborted the moment the archive finished. The " +
      "backup is now assembled to a temp file and streamed back — downloads work, and a failed database dump " +
      "now returns a clear error instead of a truncated file.",
    fixed: [
      "Platform backup (Settings → Maintenance) now downloads correctly instead of failing with a broken zip.",
    ],
  },
  {
    version: "0.45.0",
    date: "2026-06-30",
    pr: 92,
    headline: "Platform backup & restore (Maintenance tab)",
    summary:
      "A new **Maintenance** tab in Settings (Platform Administrator only) backs up and restores the **whole " +
      "platform**. **Back up** downloads one archive containing a full Postgres dump and every stored file " +
      "(audio + attachments) — keep it safe or move it to another machine. **Restore** uploads such an archive " +
      "and replaces everything with it. Restore is **destructive** (it replaces ALL data, briefly takes the app " +
      "offline, and signs you out) and only accepts a backup from the same app version. The backup contains all " +
      "platform data (password hashes, every recording) so it's a sensitive file — and it deliberately omits the " +
      "encryption keyring, so after restoring on a fresh instance users re-enter their LLM API keys.",
    added: [
      "Maintenance tab: download a full platform backup (Postgres + all object-store files) as one archive.",
      "Restore the platform from a backup archive, with a strong confirmation and an automatic sign-out.",
    ],
    changed: [
      "The API image now includes the PostgreSQL client tools (pg_dump/pg_restore) used by backup/restore.",
    ],
  },
  {
    version: "0.44.0",
    date: "2026-06-30",
    pr: 91,
    headline: "Experimental AMD ROCm GPU support for the transcription worker",
    summary:
      "The transcription worker can now run on **AMD ROCm GPUs** as an alternative to NVIDIA. The Whisper " +
      "speech-to-text step is now pluggable (`ASR_BACKEND`): NVIDIA keeps faster-whisper/CTranslate2, while " +
      "AMD uses **openai-whisper** (pure PyTorch) because CTranslate2 has no AMD GPU support. Alignment, " +
      "speaker diarization and voiceprints already run on ROCm unchanged. Ships a separate ROCm image " +
      "(`Dockerfile.rocm`) and a standalone stack (`deploy/docker-compose.rocm.yml`), initially targeting " +
      "AMD Strix Halo (gfx1151). No app/UI change — only the worker differs; the LLM endpoint is unaffected. " +
      "**Note:** the ROCm path is unit-validated and the NVIDIA path is unchanged, but end-to-end AMD GPU " +
      "inference still needs confirming on real AMD hardware.",
    added: [
      "Pluggable Whisper ASR backend (ASR_BACKEND=whisperx | whisper) so the worker can transcribe on AMD via openai-whisper.",
      "AMD ROCm worker image (Dockerfile.rocm) and a standalone docker-compose.rocm.yml (GPU via /dev/kfd + /dev/dri).",
      "Docs for the ROCm path, the faster-whisper-vs-PyTorch tradeoff, and Strix Halo (gfx1151) setup notes.",
    ],
  },
  {
    version: "0.43.1",
    date: "2026-06-30",
    pr: 90,
    headline: "Fix: the calendar no longer resizes when you pick a day",
    summary:
      "Follow-up to the calendar work in 0.43.0. The month grid was still stretching to match the " +
      "longest recording name in the selected day's list, so picking a day with long titles widened the " +
      "calendar (and pushed Sat/Sun off the edge). Long recording names now truncate instead of forcing the " +
      "column wider, so the calendar stays a fixed width as you click between days — while still resizing " +
      "when you drag the panel divider. Verified in-browser against real data (identical grid width across days).",
    fixed: [
      "Calendar grid no longer changes width when selecting different days; long recording names truncate in the list.",
    ],
  },
  {
    version: "0.43.0",
    date: "2026-06-30",
    pr: 89,
    headline: "Faster speaker labelling, per-speaker playback, and panel quick-actions",
    summary:
      "Recording-detail refinements. The **Summary** and **Speakers** panels gain small toolbars next to " +
      "their collapse arrow — re-summarise / edit on Summary, and re-identify / manage people on Speakers. " +
      "The speaker assignment control is now a **typeahead**: start typing to find an enrolled person (handy " +
      "once you have many), or create a new one from what you typed. Each speaker has a **play button** that " +
      "auditions just that person's segments. Plus two fixes: renaming a recording updates the list " +
      "immediately, and the calendar no longer shifts when you pick a day (with clearer greys for empty days).",
    added: [
      "Summary panel toolbar: Re-summarise and Edit, beside the collapse control.",
      "Speakers panel toolbar: Re-identify speakers and Manage people.",
      "Per-speaker play/pause that plays only that speaker's segments, skipping everyone else.",
      "Typeahead for assigning a speaker to a person — filter as you type, or create a new person inline.",
    ],
    changed: [
      "The calendar's selected-day highlight sits inside the cell and no longer resizes the calendar; empty days use a darker, more legible grey.",
    ],
    fixed: [
      "Renaming a recording now updates its name in the left list immediately, without a manual refresh.",
    ],
  },
  {
    version: "0.42.1",
    date: "2026-06-30",
    pr: 88,
    headline: "Fix: deleting or merging recordings no longer leaks attachment files",
    summary:
      "Closed a storage leak where a recording's **uploaded attachment files** were left behind in object " +
      "storage when the recording was **deleted** or **merged** into another — the database rows went away but " +
      "the underlying files didn't. Now deleting a recording also frees its attachment files, and **merging keeps " +
      "the attachments**: they move onto the surviving recording (so you don't lose documents you attached to a " +
      "merged-away recording) rather than being discarded.",
    changed: [
      "Merging recordings now carries the merged-away recordings' attachments onto the surviving recording.",
    ],
    fixed: [
      "Deleting a recording now removes its uploaded attachment files from storage (previously orphaned).",
      "Merging recordings no longer orphans the source recordings' attachment files.",
    ],
  },
  {
    version: "0.42.0",
    date: "2026-06-30",
    pr: 87,
    headline: "Long recordings no longer sign you out or get lost",
    summary:
      "Fixed the bug where leaving the app untouched during a long recording (e.g. a 45-minute meeting) could " +
      "drop you to the sign-in screen on Stop and lose the recording. Two changes: your **session now refreshes " +
      "itself silently** before it expires, so it stays alive through a long meeting; and every recording is " +
      "**saved to your browser the moment you press Stop**, before it uploads — so even if an upload fails, the " +
      "audio is safe and you're offered to **upload it when you return**.",
    added: [
      "Silent sliding-session token refresh (a new /api/auth/refresh endpoint) keeps long sessions alive.",
      "An unsaved recording is stashed locally and offered for re-upload if its upload didn't complete.",
    ],
    fixed: [
      "Stopping a long, idle recording no longer bounces you to the login screen and discards the audio.",
    ],
  },
  {
    version: "0.41.1",
    date: "2026-06-30",
    pr: 86,
    headline: "Fix: action banner no longer carries over to another transcript",
    summary:
      "Fixed a bug where a transient status banner (e.g. \"Extracted 3 actions.\") stayed on screen when you " +
      "opened a different recording, making it look like the message belonged to the unrelated transcript. " +
      "These banners now clear when you switch recordings.",
    fixed: [
      "Action/status banners on the transcript page now reset when navigating to a different recording.",
    ],
  },
  {
    version: "0.41.0",
    date: "2026-06-30",
    pr: 85,
    headline: "Chat recognises transcript questions, links reliably, and jumps between matches",
    summary:
      "Three chat improvements. **(1) Better recognition:** the assistant now treats your questions as being " +
      "about your own meetings by default — asking \"what do we know about {customer}\" makes it search your " +
      "transcripts instead of replying that it doesn't know, without you having to say \"based on the " +
      "transcripts\". **(2) Reliable links:** when the model mentions a recording by name but forgets to keep " +
      "the link, the app now **links it for you** (from the recordings the tools actually used). **(3) Jump " +
      "between matches:** when an answer cites several moments in the same recording, opening one shows a " +
      "**Match k of n** control with ◀ / ▶ to step through the others, each highlighted in the transcript.",
    added: [
      "A Match k/n previous/next control in the transcript when a chat answer cited several moments there.",
      "Automatic linking of recording mentions the model didn't link (from tool references).",
    ],
    changed: [
      "The chat system prompt grounds questions in the user's own transcripts and tells the model to search before saying it doesn't know.",
    ],
  },
  {
    version: "0.40.0",
    date: "2026-06-30",
    pr: 84,
    headline: "More chat tools, and clickable transcript links in answers",
    summary:
      "The chat assistant gains **eight more tools** and now **links straight to the transcript** in its " +
      "answers. New tools: **search transcripts** (general topic retrieval), **when was it discussed** " +
      "(first/last mention), **count mentions** (by speaker), **list action items**, **get recording " +
      "summary**, **who attended**, **speaker talk time**, and **get segment context** (the lines around a " +
      "moment). When the assistant cites a recording or a specific moment, it includes a **link** — click it " +
      "to open that transcript in the middle panel and **jump to the exact segment** (highlighted). All " +
      "eleven tools appear in **Settings → AI** with individual on/off switches.",
    added: [
      "Tools: search_transcripts, when_was_discussed, count_mentions, list_action_items, get_recording_summary, who_attended, speaker_talk_time, get_segment_context.",
      "Chat answers link to recordings (and exact moments); clicking opens the transcript and scrolls to the segment.",
    ],
  },
  {
    version: "0.39.0",
    date: "2026-06-30",
    pr: 83,
    headline: "Chat tools: the assistant can search your whole transcript library",
    summary:
      "Chat can now **call built-in tools** to answer questions that go beyond the transcripts loaded into " +
      "the conversation — it searches your **entire library** when it needs to. Three tools ship: " +
      "**Who said that** (find who said a phrase), **What did they say** (what a named person said about a " +
      "topic), and **List recordings** (filter by date, name, speaker, or a *contains* topic that finds " +
      "recordings whose transcript is about something). Findings come back as **When · Who · What**. While a " +
      "tool runs you'll see a brief grey *“Tool call: …”* line that clears itself, so the conversation isn't " +
      "interrupted. Turn tools on (and pick which ones) under **Settings → AI**. Tools are **off by default** " +
      "and need an LLM endpoint that supports tool calling. Search is powered by a Postgres **trigram index** " +
      "for fast, typo-tolerant matching.",
    added: [
      "Three built-in chat tools: who_said_that, what_did_they_say, list_recordings.",
      "Settings → AI: a master “Enable chat tools” switch plus a per-tool on/off list.",
      "Server config: Chat__ToolsEnabled and Chat__DisabledTools env defaults.",
      "An ephemeral grey “Tool call: …” indicator in the chat while tools run.",
    ],
    changed: [
      "Chat now runs as a bounded tool-calling loop when tools are enabled; with tools off it behaves exactly as before.",
    ],
  },
  {
    version: "0.38.0",
    date: "2026-06-29",
    pr: 82,
    headline: "Merge recordings even after their audio is gone; indented list",
    summary:
      "You can now **merge recordings whose audio has been deleted** — that restriction is lifted. " +
      "Recordings that still have audio are stitched together exactly as before; ones without audio " +
      "contribute their **transcript and action items** only (the action items from every recording are " +
      "now folded into the survivor). The **summary isn't merged** — re-generate it on the combined " +
      "transcript. Separately, the recordings list now **indents recordings under their section heading** " +
      "(and a little more under a sub-section).",
    added: [
      "Action items from every merged recording are appended to the survivor.",
    ],
    changed: [
      "Merge no longer requires audio: audio-less recordings merge their transcript + actions; audio is still concatenated when present (none → the merge finishes immediately). The summary is not merged.",
      "Recordings in the list are slightly indented under their section (more so under a sub-section).",
    ],
  },
  {
    version: "0.37.1",
    date: "2026-06-29",
    pr: 81,
    headline: "Drag section headers by the whole row",
    summary:
      "The recordings list's **section and sub-section headers** now drag the same way as recording rows: " +
      "the **drag handle is gone** and you grab the **whole row**, which **highlights on hover**. Dragging " +
      "still nests a section onto a top-level header and reorders sub-sections.",
    changed: [
      "Section/sub-section headers are dragged by the whole row (no handle) and highlight on hover, matching the recording rows.",
    ],
  },
  {
    version: "0.37.0",
    date: "2026-06-29",
    pr: 80,
    headline: "Chat with a transcript's attachments",
    summary:
      "Turn on **Include attachments** in the chat context picker and the selected recordings' " +
      "attachments are fed to the model alongside the transcript. Uploaded **PDFs, text, Office docs " +
      "(.docx/.xlsx/.pptx) and emails/calendar invites (.eml/.ics)** are read into text; **URL " +
      "attachments are fetched** (behind safety guards that block private/internal addresses, cap the " +
      "size, and strip HTML to text). Unsupported or unreachable attachments are simply skipped.",
    added: [
      "“Include attachments” toggle in chat — adds the selected recordings' files & links to the LLM context.",
      "Attachment text extraction for Office (.docx/.xlsx/.pptx) and email/calendar (.eml/.ics), plus URL fetching with SSRF guards.",
    ],
  },
  {
    version: "0.36.0",
    date: "2026-06-29",
    pr: 79,
    headline: "Attach supporting documents to a transcript",
    summary:
      "Keep the **supporting material** for a meeting alongside its transcript. On the detail page, an " +
      "**Attachments (N)** split button opens a manager where you can **add files** (PDFs, Office docs, " +
      "emails, calendar invites, images — anything), **add a URL**, **rename**, **open**, and **remove** " +
      "them; the dropdown opens any attachment directly with your browser. You can also **drag files onto " +
      "the page** to attach them. Files are stored in object storage and count toward your storage quota; " +
      "URLs don't. (Using attachments as chat context comes next.)",
    added: [
      "Attachments on a recording: add files or URLs, rename, open, and remove from an “Attachments (N)” split button.",
      "Drag-and-drop files onto the transcript page to attach them.",
    ],
    changed: [
      "Your storage usage now includes attachment files (URLs don't count).",
    ],
  },
  {
    version: "0.35.0",
    date: "2026-06-29",
    pr: 78,
    headline: "See how long transcription took",
    summary:
      "Diariz now records the **full-pipeline wall-clock time** the worker spends on each transcription " +
      "(download + transcribe + diarize + voiceprint). It shows in the recording’s **subtitle** (e.g. " +
      "“transcribed in 4:12”), and your **account menu** shows a running **total transcription time** below " +
      "the storage line (`Transcription d days hh:mm:ss`). Useful for gauging throughput and capacity.",
    added: [
      "The worker measures and reports each job’s processing time; it’s stored per transcription version.",
      "Detail subtitle shows the transcription time; the account menu shows your total across all transcriptions.",
    ],
  },
  {
    version: "0.34.0",
    date: "2026-06-29",
    pr: 77,
    headline: "Copy a transcript link; edit the summary by hand",
    summary:
      "Two ways to work with a transcript more directly. **Copy link** (a toolbar button and a ⋮-menu " +
      "item on both the detail page and the recordings list) puts a **persistent link** to the transcript " +
      "on your clipboard as **rich text** — pasting into an email or meeting notes shows the recording’s " +
      "**name** as the clickable link. (It’s a personal deep-link: opening it still requires signing in as " +
      "you.) And you can now **edit the summary by hand** (⋮ → Edit summary) — write one even when no AI " +
      "endpoint is configured. A hand-edited summary is **protected**: the automatic summariser won’t " +
      "overwrite it, and re-summarising warns you first.",
    added: [
      "Copy a persistent rich-text link to a transcript (toolbar + ⋮ menu on the detail page and the list).",
      "Manually create or edit a recording’s summary; works without an LLM configured.",
    ],
    changed: [
      "The automatic summariser no longer overwrites a summary you’ve edited by hand (re-summarising warns first).",
    ],
  },
  {
    version: "0.33.0",
    date: "2026-06-29",
    pr: 76,
    headline: "Tidy transcripts: delete segments, mark overlapping speech",
    summary:
      "Two transcript-editing tools. You can now **delete a single segment** from the transcript " +
      "(via the row’s ⋮ menu) — handy for dropping a meaningless or misfired row; the remaining rows " +
      "renumber automatically, and re-transcribing always regenerates the full set. And a speaker can be " +
      "marked **“Multiple Speakers”** from the speaker dropdown, for stretches of overlapping or " +
      "simultaneous speech. Because that audio is a mix of voices, a “Multiple Speakers” slot is **never " +
      "used to recognise or train a voiceprint**.",
    added: [
      "Delete an individual transcript segment from its ⋮ menu (permanent for that version; re-transcribe to restore).",
      "Mark a speaker as “Multiple Speakers” for overlapping speech — excluded from automatic identification and voiceprint enrolment.",
    ],
  },
  {
    version: "0.32.1",
    date: "2026-06-29",
    pr: 75,
    headline: "Recordings-list polish & fixes",
    summary:
      "Follow-up fixes for the recordings list: the account-menu **storage figure now refreshes** right " +
      "after you delete a recording's audio (or merge/delete recordings); **merging shows progress** (a " +
      "“Merging audio…” indicator and a status pill) and surfaces an error if it fails; the **calendar is " +
      "more compact** (shorter rectangular day cells, no confusing next-month days, trimmed height) leaving " +
      "more room for the day's recordings; the section **select checkbox now sits before the drag handle** " +
      "to match the recording rows; and you can **drop a section onto a top-level section to nest it** as a " +
      "sub-section.",
    changed: [
      "Recording rows are dragged by the whole row now (the separate drag-handle icon is gone) — the row already highlights on hover.",
    ],
    fixed: [
      "Account-menu storage usage refreshes after delete-audio / merge / delete.",
      "Merge now shows a “Merging audio…” indicator and surfaces failures instead of doing nothing visibly.",
      "Calendar day cells are shorter/rectangular, next-month days are hidden, and the grid is trimmed.",
      "Section select-all checkbox is now before the drag handle, matching recording rows.",
      "Dropping a section onto a top-level section nests it as a sub-section.",
    ],
  },
  {
    version: "0.32.0",
    date: "2026-06-29",
    pr: 74,
    headline: "Browse recordings in a calendar",
    summary:
      "The recordings panel now has **List** and **Calendar** tabs (down the left side). The calendar shows " +
      "the month focused on today; days that have recordings are **green** (and the only selectable ones), " +
      "days without are grey. Click a day to see that day's recordings below — in the same format as the " +
      "list — with the calendar pinned and only the day's list scrolling. Navigate months with the arrows.",
    added: [
      "A Calendar view of recordings: month grid with green days-with-recordings, click a day to list that day's recordings.",
      "List / Calendar tabs on the recordings panel (your choice is remembered).",
    ],
  },
  {
    version: "0.31.0",
    date: "2026-06-29",
    pr: 73,
    headline: "Merge several recordings into one",
    summary:
      "Select two or more recordings and **Merge transcripts** to combine them into the **earliest-created** " +
      "one. Their transcripts are laid end-to-end (timestamps offset so they run in sequence) into a new " +
      "transcript on the survivor, and their **audio is concatenated** into a single file by the worker " +
      "(ffmpeg) so nothing is lost. The other recordings are then removed. Useful when a meeting was captured " +
      "in several parts. Speakers are kept distinct per source — re-identify or rename them afterward.",
    added: [
      "Merge transcripts: a Select-mode toolbar action (2+ recordings) that combines transcripts + audio into the earliest recording and deletes the rest, with a confirmation.",
    ],
    changed: [
      "New worker job + Redis stream (audio-merge-jobs) for server-side ffmpeg audio concatenation; a Merging status while it runs.",
    ],
  },
  {
    version: "0.30.0",
    date: "2026-06-29",
    pr: 72,
    headline: "Organise recordings into sub-sections",
    summary:
      "Sections can now be **nested one level deep** — e.g. a *Customers* section with an *Acme Corp* " +
      "sub-section inside it. Create a sub-section from a section's menu, drag a recording into any section " +
      "or sub-section, and **drag section headers** to reorder them or move a sub-section to a different " +
      "parent (or back out to the top level). Recordings can live at either level, and recording rows still " +
      "use the full panel width. Deleting a parent section removes its sub-sections and returns their " +
      "recordings to Ungrouped.",
    added: [
      "Two-level section nesting (e.g. Customers › Acme Corp); a New sub-section action on each top-level section.",
      "Drag-and-drop for section headers: reorder among siblings, reparent, or promote a sub-section to the top level.",
    ],
    changed: [
      "Sections are now manually ordered (drag to reorder) instead of always alphabetical.",
      "The Move to section picker lists sub-sections as “Parent › Child”.",
    ],
  },
  {
    version: "0.29.0",
    date: "2026-06-29",
    pr: 71,
    headline: "Delete a recording's audio while keeping its transcript",
    summary:
      "You can now **delete the audio** of a recording (from its kebab menu, or in bulk via the " +
      "recordings-list toolbar in Select mode) while keeping the transcript and everything derived from it. " +
      "Deleting the audio **frees that recording's storage** against your quota. The recordings list shows a " +
      "**microphone icon** at the start of each row — green when the audio is available, grey once it's been " +
      "deleted — and a **Refresh** button picks up changes made on another machine or browser. Audio-only " +
      "actions (play, download audio, re-transcribe, re-identify) hide once the audio is gone.",
    added: [
      "Delete audio (single, via the kebab) and bulk Delete audio (Select mode) — keeps the transcript, frees the quota.",
      "A green/grey microphone indicator showing audio presence on each recordings-list row.",
      "A Refresh button on the recordings-list toolbar.",
    ],
    changed: [
      "Playback, audio download, re-transcribe and re-identify are hidden for recordings whose audio has been deleted.",
    ],
  },
  {
    version: "0.28.1",
    date: "2026-06-29",
    pr: 70,
    headline: "Clearer translation docs in the README",
    summary:
      "Documentation-only: the README's **Translations** section is reorganised into clear **Web User " +
      "Interface** and **Server Side** subsections, spelling out that both sets of strings need translating " +
      "and that catalogs are auto-discovered (so adding a language stays a data-only PR). No application " +
      "behaviour changes.",
    changed: ["Reorganised and clarified the README Translations section (web vs server-side catalogs)."],
  },
  {
    version: "0.28.0",
    date: "2026-06-29",
    pr: 69,
    headline: "The whole interface is now localized",
    summary:
      "Localization now covers the last English-only corners of the app: the **chat panel** (context " +
      "picker, attachments, the context-usage dial, saved conversations), the admin **Manage Users** screen, " +
      "the **People** voiceprint manager, the **guided tour**, the audio **recorder** controls, the " +
      "collapsible workspace **panel headers**, the **About** box chrome, and the **Release Notes** page — all " +
      "appear in your chosen language (English, Spanish, French, or German). The new strings live in four new " +
      "JSON catalogs (`chat`, `admin`, `people`, `tour`) plus additions to the existing ones, so contributors " +
      "can translate them without touching code.",
    added: [
      "Translation of the chat panel, Manage Users, People, the onboarding tour, the recorder, the workspace panel headers, and the About / Release Notes chrome.",
      "Four new translation namespaces — `chat`, `admin`, `people`, `tour` (en + es/fr/de).",
    ],
    changed: [
      "The locale completeness gate now validates nested catalog values (e.g. the per-step tour copy), not just top-level keys.",
    ],
  },
  {
    version: "0.27.0",
    date: "2026-06-29",
    pr: 68,
    headline: "The recording workspace is fully localized",
    summary:
      "Localization now reaches the main workspace: the **recording detail view** (its toolbar, section " +
      "headings, status banners, confirmation prompts, the re-transcribe / edit-segment / rename dialogs, " +
      "and the speaker-labelling controls), the **recordings list** (sections, multi-select, the upload " +
      "tray and drop zone), and the **empty landing screen** all appear in your chosen language — English, " +
      "Spanish, French, or German. Like the rest of the UI, the new strings live in a `workspace` JSON " +
      "catalog that contributors can translate without code changes.",
    added: [
      "Full translation of the recording detail view, the recordings list panel, and the empty-state screen, plus the move-to-section and download-format dialogs.",
      "A new `workspace` translation namespace (en + es/fr/de) covering these screens.",
    ],
  },
  {
    version: "0.26.0",
    date: "2026-06-29",
    pr: 67,
    headline: "Downloaded and emailed transcripts speak your language",
    summary:
      "The headings in **downloaded transcripts** (.txt / .md / .rtf) and the **emailed transcript** — " +
      "things like *Summary*, *Transcript*, *Actions*, the table columns, the email subject and footer — now " +
      "appear in your **app language** (English, Spanish, French, or German). Like the rest of the " +
      "localization, these labels live in simple JSON files on the server, so they're community-extensible " +
      "with a data-only PR. (The transcript content itself was already in whatever language you translated " +
      "it to.) This completes the localization & translation feature.",
    added: [
      "Localized headings in transcript downloads and the emailed transcript, in the recording owner's app language.",
      "Server-side export string catalogs (src/Diariz.Api/locales) that contributors can translate without code changes.",
    ],
  },
  {
    version: "0.25.0",
    date: "2026-06-29",
    pr: 66,
    headline: "The app speaks your language",
    summary:
      "Diariz's interface is now **localized** — choose your language at signup or in **Preferences**, or " +
      "force it with `?lang=es` in the URL. **English, Spanish, French, and German** ship today; the account " +
      "menu, the recording menus and toolbar, the sign-in / request-access / setup screens, and the Settings " +
      "and Preferences dialogs all translate, with dates shown in your locale and right-to-left support built " +
      "in. Translations live in simple JSON files, so **anyone can add or improve a language with a data-only " +
      "PR** — no code changes. (This localizes the interface chrome; some deeper screens still follow in later " +
      "releases.)",
    added: [
      "A localized UI via react-i18next — English, Spanish, French and German, picked at signup or in Preferences (or via ?lang=).",
      "Contributor-friendly translation catalogs (apps/web/src/locales) with a guide and CI checks that keep every language complete.",
      "Right-to-left layout support and locale-aware date formatting.",
    ],
    changed: [
      "The app-language picker now lists the languages that have a shipped translation; your native language (for translating transcripts) still offers the full list.",
    ],
  },
  {
    version: "0.24.0",
    date: "2026-06-29",
    pr: 65,
    headline: "Translate transcripts into your language",
    summary:
      "**Translate to {your language}** now appears on a recording's menu (and on each segment's menu) once " +
      "you've set a native language in Preferences. It uses your configured OpenAI-compatible LLM to translate " +
      "the whole transcript — every segment, plus the summary and action items — into your language, keeping " +
      "speakers' names intact. Translations land in the **revised** layer, so the model's original words are " +
      "preserved: the ✎ marks translated rows and the **Show original / Show revised** toggle flips back to " +
      "the source at any time. You can also translate a single segment from its menu.",
    added: [
      "“Translate to {language}” on the recording menu/toolbar and on each segment — translates into your native language via the LLM.",
      "Whole-transcript translation covers the segments, the summary, and the action items in one go.",
    ],
    changed: [
      "Translations are stored as revisions (the model's original is kept) and are shown/exported like manual edits.",
    ],
  },
  {
    version: "0.23.0",
    date: "2026-06-29",
    pr: 64,
    headline: "Set your own name and language in Preferences",
    summary:
      "A new **Preferences** item in the account menu (for every user) lets you change your **display name** " +
      "and choose your **native language** and **app language** — and you can pick a language right on the " +
      "signup screen. This is the groundwork for the localization & translation feature: the languages come " +
      "from a shared supported-language list (`GET /api/languages`, ~50 languages with right-to-left flags) " +
      "and your native language will be the default target when translating transcripts. The app interface " +
      "itself isn't translated yet — that lands in a later release.",
    added: [
      "A Preferences modal (account menu) to edit your display name and native/app language — available to all users.",
      "A language selector on the account-setup screen.",
      "GET /api/languages — the supported-language reference list, used by the pickers.",
    ],
    changed: [
      "You can now rename yourself; the new name shows immediately (the app re-issues your session token).",
    ],
  },
  {
    version: "0.22.0",
    date: "2026-06-29",
    pr: 63,
    headline: "Edit transcripts without losing the model's original words",
    summary:
      "Each transcript segment now keeps two layers: the model's **original** words (never overwritten) and " +
      "your **revised** version (an edit — and, in a coming release, a translation). Editing a segment saves " +
      "to the revision and leaves the original intact, a small ✎ marks revised rows, and a **Show original / " +
      "Show revised** toggle flips the whole transcript between the two. You can **reset** any segment back to " +
      "the model's original from the edit box. This is the data foundation for the upcoming localization & " +
      "translation feature. (Re-transcribing still produces a fresh transcript from the model — a heads-up now " +
      "warns when that would set aside your edits.)",
    added: [
      "Each segment stores the model's original text and your revision separately; exports, email, chat and summaries use your revised text when present.",
      "A ✎ indicator on segments you've edited, and a Show original / Show revised toggle for the whole transcript.",
      "“Reset to original” in the segment editor clears a revision and restores the model's words.",
    ],
    changed: [
      "Editing a segment now updates a separate revision rather than overwriting the original.",
      "Re-transcribing a transcript that has edited segments now shows a heads-up first (the fresh transcript comes straight from the model).",
    ],
  },
  {
    version: "0.21.2",
    date: "2026-06-28",
    pr: 60,
    headline: "Saved chat appears in the list right away",
    summary: `
After you **Save** a chat conversation, it now shows up in the saved-conversations dropdown immediately —
previously the dropdown could keep showing a stale list (missing the one you'd just saved) until it was
closed and reopened.
`.trim(),
    fixed: [
      "Saving a chat conversation now refreshes the saved-conversations dropdown so the new one appears straight away.",
      "The saved-conversations and context dropdowns now close on an outside click or Escape.",
    ],
  },
  {
    version: "0.21.1",
    date: "2026-06-28",
    pr: 58,
    headline: "Toolbar layout fixups",
    summary: `
The recordings list's toolbar (New section / Select) now stays pinned at the top while the list scrolls
beneath it — matching the chat panel. Also finishes the chat-toolbar alignment: the context-usage dial, the
"Attach file" button, and the saved-conversations icon now line up exactly with their neighbours (their
wrapper elements were nudging them off-centre).
`.trim(),
    fixed: [
      "The recordings list toolbar stays fixed at the top instead of scrolling away with the list.",
      "The chat context dial, 'Attach file' button, and saved-conversations icon now align precisely with the rest of their toolbars.",
    ],
  },
  {
    version: "0.21.0",
    date: "2026-06-28",
    pr: 57,
    headline: "Collapsible detail panels + recording-list polish",
    summary: `
The recording detail page's **Summary**, **Speakers**, **Actions**, and the new **Transcript** panel are now
**collapsible** — click anywhere along the shaded header strip (title to chevron) to fold a section away. The
whole transcript (player + segments) lives in its own "Transcript" panel, and the detail subtitle now shows the
**local date** and the **clip duration** (h:mm:ss).

The recordings list shows each clip's **duration** as a tidy **m:ss / h:mm:ss** value, right-aligned so the
times line up, and its ⋮ menu gains the detail-only actions — **Extract actions** (with a replace-confirm when
actions already exist), **Re-identify speakers**, and **Email me the transcript**.

Plus small layout fixes: the chat context dial and "Attach file" button line up with their neighbours, and the
recordings/chat panel toolbars share the same height and bottom-border colour.
`.trim(),
    added: [
      "The Summary, Speakers, Actions, and a new Transcript panel on the detail page are collapsible — click anywhere on the header strip to fold them.",
      "The recordings list ⋮ menu now offers Extract actions (with a replace-confirm), Re-identify speakers, and Email me the transcript (parity with the detail page).",
    ],
    changed: [
      "Recording durations show as m:ss / h:mm:ss (right-aligned in the list), and the detail subtitle now shows the local date plus the clip duration.",
    ],
    fixed: [
      "The chat context-usage dial is now vertically aligned with the toolbar icons.",
      "The chat 'Attach file' button now lines up with the context-picker pill next to it.",
      "The recordings and chat panel toolbars now have a consistent height and bottom-border colour.",
      "Uploaded recordings now read \"Uploaded\" (not \"Microphone\") in the detail subtitle.",
    ],
  },
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
      "The Actions panel is collapsible, and the Actions/Speakers expand toggles are larger and easier to hit.",
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
