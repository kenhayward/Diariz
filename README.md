# Diariz

![Diariz](images/Diariz%20Backdrop.png)

**Smart Meeting Transcription.** Personal, multi-user voice/meeting transcription platform: record audio
(mic or system loopback), transcribe it server-side with **speaker diarization** and word-level timestamps,
**identify known speakers** across recordings, **summarize**, and **chat across transcripts**. See
[docs/Overall_Synopsis_of_Platform.md](docs/Overall_Synopsis_of_Platform.md) for the original brief and the
architecture plan for the full design.

Versioned per the rule in [CLAUDE.md](CLAUDE.md); the current version and per-release notes live in
[`apps/web/src/lib/releases.ts`](apps/web/src/lib/releases.ts) (and [`/version.json`](version.json)) and on
the in-app **Release Notes** page (`/release-notes`), reachable from **About** in the account menu.

## Features

- **Capture** audio from the browser microphone — **choose a specific input device** (the choice is
remembered, and the list refreshes on hot-plug), **tune capture** (echo cancellation, noise suppression,
auto gain, mono) from a ⚙ popover, and watch a **live input-level meter** while recording (with a subtle
silence hint) — or Windows system/loopback audio via the Electron desktop
shell — which can also **start/stop recording from its system-tray menu** (in the background, with
notifications). Or **upload existing audio files** to transcribe (WAV, MP3, FLAC, Ogg/Opus, WebM, M4A) —
via the Upload button or by **dragging several onto the recordings list**, with per-file status.
- **Transcribe + diarize** server-side with WhisperX (large-v3, word-level timestamps) and pyannote 3.1,
producing speaker-labelled, timestamped segments you can rename, edit, and play back (per segment or whole).
Edits are kept **separately from the model's original words** — a ✎ marks revised rows and a **Show original /
Show revised** toggle flips the whole transcript, so you can always get back to what the model said.
Re-transcribe with a chosen model at any time (with optional **min/max speaker hints** for pyannote when
voices are merged), **merge** consecutive same-speaker rows, and **email yourself** the formatted transcript.
The transcript panel **pins to the top** as you scroll (its segments then scroll internally) and has a header
**icon toolbar** with a small play bar and a **Select mode** — tick segments (or click one) to **play, edit,
translate, or delete** just the selection, while **Play all** and **Merge** always act on the whole transcript.
- **Identify speakers** across recordings: enrol a person from a recording's speaker and Diariz recognises
that voice automatically in later recordings (SpeechBrain ECAPA voiceprints in pgvector, cosine matching),
with manual reassignment. A **People** screen renames, prunes training samples, merges duplicates, and
erases voiceprints (GDPR — biometric data).
- **Summarize** recordings (with automatic naming) and generate a full set of **professional meeting minutes**
(Markdown: headings, lists, tables — no emojis) as part of the pipeline; edit them in a **WYSIWYG editor**,
re-create them, or **email them to yourself** (optionally with the recording's attachments). The minutes also
travel with the emailed transcript and the Markdown/text/RTF downloads.
- **Chat across one or more transcripts** — streaming
replies, a context-usage dial, PDF/text attachments, and saved conversations — via a per-user (or
server-default) OpenAI-compatible LLM endpoint, with the API key encrypted at rest.
- **Chat tools** (opt-in, per-user): the assistant can call **built-in tools** that search your **whole
transcript library** — *who said a phrase*, *what a person said about a topic*, *search transcripts*, *when a
topic was discussed*, *count mentions*, *list recordings* (by date / name / speaker / topic), *list action
items*, *get a recording's summary*, *who attended*, *speaker talk time*, and *the lines around a moment* —
answering as **When · Who · What**. Answers **link back to the transcript**: click a citation to open that
recording and jump to the exact segment. Fuzzy search is backed by a Postgres `pg_trgm` trigram index; a
brief grey "Tool call: …" line shows while a tool runs.
- **Extract action items** from a transcript (Action / Actor / Deadline) with that same LLM, into an
editable table shown by exception (only after you run it); the actions also travel with the transcript —
included in the downloads (Text/Markdown/RTF), the emailed transcript, and the chat context.
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
- **Attach supporting documents** to a transcript — upload files (PDFs, Office docs, emails, calendar
invites, images, …) or add URLs, then rename, open, and remove them from an "Attachments (N)" button
(or drag files onto the page). Files are stored in object storage and count toward your quota. Turn on
**Include attachments** in chat to feed them to the LLM (documents are read into text; URLs are fetched
behind SSRF guards).
- **Manage audio & merge**: **delete a recording's audio** to reclaim its storage while keeping the
transcript, and **merge** several recordings into the earliest one — their transcripts are laid end-to-end
and their action items are folded in. Audio is concatenated server-side (ffmpeg) for the recordings that
still have it; recordings whose audio was deleted merge their transcript only (the summary is regenerated).
- **Multi-user RBAC**: Standard / Administrator / Platform Administrator roles, an access-request →
admin-grant → account-setup lifecycle (one-time email link, with an in-app fallback when SMTP is
unconfigured), and admin user management. Each user's data is isolated to them. Light/Dark/Auto theming.
- **Preferences**: every user can change their own **display name** and pick their **native** and **app**
language (chosen at signup or later from the account menu) — groundwork for upcoming UI localization and
transcript translation.
- **Storage quotas**: each user gets an audio-storage quota (starter + maximum set by the Platform
Administrator; any admin can raise a user up to the maximum). Usage shows in the account menu and
per-recording; over-quota uploads are rejected.
- **Backup & restore** (Platform Administrator, Settings → Maintenance): download the whole platform —
the Postgres database (`pg_dump`) plus every stored file — as one transferable archive, and restore from
one. Restore is destructive (replaces all data) and only accepts a backup from the same app version.

## Architecture

| Component | Tech | Path |
| :--- | :--- | :--- |
| API / auth / orchestration | ASP.NET Core (C#) + EF Core + SignalR | [src/Diariz.Api](src/Diariz.Api) |
| Domain model + migrations | EF Core + Postgres/pgvector | [src/Diariz.Domain](src/Diariz.Domain) |
| Transcription + diarization + voiceprints | Python: WhisperX (large-v3) + pyannote 3.1 + SpeechBrain ECAPA (GPU) | [src/Diariz.Worker](src/Diariz.Worker) |
| Web UI | React + TypeScript + Vite + Tailwind | [apps/web](apps/web) |
| Desktop app | Electron — Windows system-tray shell (first-run server config, mic + Windows loopback, tray recording, auto-update) | [apps/desktop](apps/desktop) |
| Orchestration | docker-compose (postgres/pgvector, redis, minio) | [deploy](deploy) |

Summaries and chat use any OpenAI-compatible LLM endpoint you configure (OpenAI, or a local server such
as Ollama / LM Studio / vLLM) — see the Settings modal and `deploy/.env.example`.

**Flow:** client records → uploads to API → audio stored in MinIO, metadata in Postgres →
job enqueued on a Redis Stream → Python worker transcribes + diarizes + extracts per-speaker voiceprints →
posts segments back → API stores them, auto-identifies enrolled speakers, and notifies the client over
SignalR → note view shows speaker-labelled, timestamped segments.

## Quick start

Prerequisites: Docker (+ NVIDIA Container Toolkit for the GPU worker), .NET 10 SDK, Node 20+.
For diarization you need a Hugging Face token with the `pyannote/speaker-diarization-3.1`
terms accepted — see [src/Diariz.Worker/README.md](src/Diariz.Worker/README.md). For **GPU/VRAM
requirements, tuning for smaller cards, and known-working GPUs**, see the worker's
[GPU and hardware requirements](src/Diariz.Worker/README.md#gpu-and-hardware-requirements).
On **AMD ROCm** (experimental) run `docker compose -f docker-compose.rocm.yml up --build` instead — the
worker transcribes with openai-whisper since CTranslate2 has no AMD GPU support; see the worker README's
[AMD ROCm](src/Diariz.Worker/README.md#amd-rocm-experimental) section.

```bash
# 1. Whole stack — web UI, API, Postgres, Redis, MinIO, GPU worker.
#    Runs as a single Compose project named "diariz".
cd deploy
cp .env.example .env        # fill in JWT_KEY, CALLBACK_SECRET, HF_TOKEN, seed user
docker compose up --build   # web UI at http://localhost:8081, API at http://localhost:8080

# 2. (dev alternative to the bundled UI) Vite dev server with hot reload,
#    proxying /api and /hubs to the API.
cd ../apps/web
npm install && npm run dev  # http://localhost:5173

# 3. (optional) Desktop shell for system-audio capture
cd ../desktop
npm install && npm run dev
```

Sign in with the seeded user (`SEED_EMAIL` / `SEED_PASSWORD`), record a clip, and the
transcript appears automatically when the worker finishes.

## Translations

Users pick their interface language at signup or in
**Preferences**, and `?lang=es` forces it to Spanish for example. The initial release includes English, French, German and Spanish translations. Languages catalogues are **auto discovered** so developers can extend or improve these translations with a data only pull request (No code changes needed). Make sure you translate both the Web UX and Server side strings (See below)

**Web User Interface**

The web UI is localized with **react-i18next**. Strings live in JSON catalogues under
[`apps/web/src/locales/`](apps/web/src/locales/), one folder per language (English is the authoritative
base; **Spanish, French, and German** ship by default). As catalogues are auto-discovered, adding or improving a
language is a data-only PR - no code changes. See
[`apps/web/src/locales/README.md`](apps/web/src/locales/README.md) for the contributor guide (CI checks
that every catalogue mirrors English and limits a translation PR to one language). 

**Server Side**

The headings in
**downloaded and emailed transcripts** are localized too, from server-side catalogues under
[`src/Diariz.Api/locales/`](src/Diariz.Api/locales/). Follow the same rules to change or extend them. 

## Roadmap

- **M1 — done:** capture → transcribe (timestamps + diarization) → view.
- **M2 — done:** multi-user auth + RBAC, LLM summaries, transcript export, re-transcribe with model choice.
- **M3 — done:** chat across transcripts; speaker identification via enrolled voiceprints (pgvector).
- **M4 — in progress:** windows desktop app, macOS/mobile,  packaging, live streaming.

> **Keep this README current.** When a PR changes what the app does (a new feature, a stack change, or a
> shipped roadmap item), update the **Features**, **Architecture**, and **Roadmap** sections in the same PR —
> alongside the [`releases.ts`](apps/web/src/lib/releases.ts) entry required by [CLAUDE.md](CLAUDE.md). (The
> version isn't repeated here on purpose — it lives in `version.json` / `releases.ts` so it can't drift.)

## Licensing & commercial use

This software is **dual-licensed**. Depending on your use case, you may use it under one of two options:

### 1. Open Source (GNU AGPLv3)

This project is completely free for **personal, academic, or non-profit use** under the terms of the GNU Affero General Public License v3.0.

- Anyone using, modifying, or hosting this code under this license must also make their entire project's source code publicly available under the same AGPLv3 terms.

### 2. Commercial License

**For-profit companies** or commercial projects that wish to use, integrate, or build upon this software *without* being bound by the AGPLv3 open-source requirements must secure a private commercial agreement.

To discuss commercial licensing, custom terms, or to obtain an exception, please contact me directly at: **ken@stocks-hayward.com**

A few parts of the ML/storage stack carry caveats worth understanding before a **commercial**
license is requested. *This is a summary for orientation, not legal advice.*

- **Transcription & diarization — clear for commercial use.** Whisper large-v3 (MIT) and the **pyannote**
models (`speaker-diarization-3.1`, `segmentation-3.0`) are **MIT-licensed**. They are *gated* — you must
accept their terms on Hugging Face and supply an `HF_TOKEN` — but gating is an access step, not a licence
restriction.
- **Speaker identification / voiceprints — the main caveat.** Recognising known speakers across recordings
uses **SpeechBrain ECAPA** embeddings. The model code is Apache-2.0, but the weights are **trained on the
VoxCeleb dataset, which is published for research / non-commercial use**. Whether a dataset's terms bind
the trained weights is legally unsettled; for a commercial deployment, get your own legal read, **or** swap
the embedder for one trained on commercially-cleared data (e.g. NVIDIA NeMo TitaNet, WeSpeaker), **or**
simply disable the feature with `ENABLE_SPEAKER_EMBEDDINGS=false` on the worker — transcription and
diarization still work, you just lose cross-recording speaker identification. Voiceprints are **biometric
data**: only enrol people with their consent, and use the People screen to erase them on request.
- **Object storage (MinIO) is AGPL-3.0.** Used unmodified as a separate container it does **not** impose
copyleft on Diariz's own code, but if AGPL is a concern, point storage at **any S3-compatible store** (AWS
S3, Cloudflare R2, …) and drop MinIO entirely.
- **Summaries & chat** send transcript text to whatever **OpenAI-compatible LLM endpoint** you configure;
that provider's terms and privacy policy govern the text you send.
- **Uploaded audio formats.** Decoding is done by ffmpeg in the worker (Diariz ships no codec). The
royalty-free formats — **WAV, FLAC, Ogg Vorbis, Opus, WebM** — plus **MP3** (its patents expired in 2017)
are always accepted. **M4A/AAC** is accepted by default but AAC still carries active patents, so it can be
disabled (`UPLOAD_ALLOW_AAC=false`) for maximum commercial caution. Operators are responsible for their
ffmpeg build's codec licensing.



