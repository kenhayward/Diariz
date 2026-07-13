# Diariz

![Diariz](images/Diariz%20Backdrop.png)

**Smart Meeting Transcription.** Personal, multi-user voice/meeting transcription platform: record audio
(mic or system audio), transcribe it server-side with **speaker diarization** and word-level timestamps,
**identify known speakers** across recordings, **summarize**, and **chat across transcripts**. 
See [diariz.app](https://www.diariz.app) for more details, videos and screenshots.

[docs/Overall_Synopsis_of_Platform.md](docs/Overall_Synopsis_of_Platform.md) has the original brief and the
architecture plan for the full design.

Versioned per the rule in [CLAUDE.md](CLAUDE.md); the current version and per-release notes live in
[`apps/web/src/lib/releases.ts`](apps/web/src/lib/releases.ts) (and [`/version.json`](version.json)) and on
the in-app **Release Notes** page (`/release-notes`), reachable from **About** in the account menu.

## Features

At a glance - see **[docs/features.md](docs/features.md)** for the full detail on each.

| Feature | Description |
| :--- | :--- |
| **Capture** | Record from the browser mic (device picker, DSP tuning, live level meter, pause/resume), system audio, or both mixed on one device; schedule a recording to auto-stop at a set time or after 15/30/60 minutes; system audio works in Chromium browsers ("Share audio") and seamlessly in the desktop app; upload files (WAV/MP3/FLAC/Ogg/Opus/WebM/M4A) or drag-drop several at once. |
| **Transcribe & diarize** | Server-side WhisperX (large-v3, word-level timestamps) + pyannote speaker diarization; speaker-labelled, editable, playable segments; re-transcribe any time, with an original/revised toggle. |
| **Speaker identification** | Enrol a voice once (SpeechBrain ECAPA voiceprints) and Diariz recognises it across later recordings; rename, merge, and erase voiceprints (GDPR). |
| **Summaries & minutes** | Auto summary plus full professional meeting minutes (WYSIWYG-editable, emailable), driven by reusable meeting-type templates with per-block layout control (H1-H3 headings, break, Markdown, horizontal rules, drag-to-reorder) and JSON import/export. |
| **Notes** | Jot your own note lines during or before a meeting (live, timestamped, crash-safe); they appear inline in the transcript at the moment you wrote them, steer the minutes, and can be woven into an "enhanced notes" section that links to the exact transcript moments. |
| **Action items** | Auto-extracted with owner and deadline into an editable table, tracked across every meeting with completion, a person filter, and links back to the transcript. |
| **Tag cloud** | Every meeting auto-tagged with weighted topics; a Tags tab shows a weighted cloud and lists the meetings behind each tag, with an expanded modal view. |
| **Chat over transcripts** | Stream answers over one meeting, a folder (its summary/minutes/actions), several selected, or all meetings (context inferred from what you're viewing) via an OpenAI-compatible LLM; context dial, file attachments, saved conversations, and slash commands. |
| **Formulas** | Save a prompt and a chosen context, then run it over any recording to generate a named Markdown document (follow-up email, recap, and more) you can edit, download, or email. Built-in, platform-wide, and personal; share a personal one so others can find and add it (a live link) and run it; run one with `/formula <name>` in chat or ask Claude via MCP; admins manage the platform-wide and built-in formulas from a Manage Formulas window. |
| **Search** | Keyword search across your library, upgraded to semantic (RAG - hybrid vector + trigram) when an embeddings endpoint is configured. |
| **Chat tools** | The assistant calls built-in tools (who-said-what, search, attendees, talk time, summaries, email-to-self, and more) and links answers to the exact segment. |
| **Voice dictation in chat** | Dictate chat questions by voice - browser speech recognition, or a server STT endpoint on the desktop app. |
| **Connect Claude (MCP)** | An in-process MCP server lets Claude connect to your own meetings via OAuth (claude.ai) or a personal token (Claude Desktop/Code), including a `run_formula` tool to trigger your saved Formulas. |
| **User API access** | When a Platform Administrator enables it, generate a personal API token to call the REST API as yourself, with a built-in API reference. |
| **Translate** | Translate a whole transcript (segments, summary, actions) or a single segment; stored as revisions you can flip back. |
| **Attachments** | Attach files or URLs (PDF, Office, email, calendar, images) to a recording or directly to a folder, edit Markdown attachments in place, save a chat conversation with /attach, and optionally feed them to chat. |
| **Rooms** | A private Personal Room per account plus shareable Rooms: invite users and groups with per-member permissions. Each Shared Room has its **own folder structure** (sections/sub-sections, drag-and-drop, per-room order) and its own List/Calendar/Actions/Tags scoped to it; record or upload files straight into a room (your Personal Room keeps the original), and search + chat over every room you belong to. Your Google Calendar and its linking stay personal. Manage rooms from the switcher. |
| **Organise & merge** | Sections and sub-sections with drag-and-drop; choose where a new recording is filed; browse as a list, calendar, cross-meeting actions, or tag cloud; merge recordings into one. |
| **Folder pages** | Open a folder as a page with a roll-up LLM summary and consolidated minutes across it and its sub-folders, plus aggregated actions, notes, and attachments tagged with their source meeting. |
| **Google sign-in & Calendar** | Optional Google OAuth sign-in; opt-in read-only Calendar linking, invite details, and a month overlay. |
| **External calendar feeds** | Subscribe to public iCalendar (.ics) URLs; their meetings appear on the Calendar tab. |
| **Multi-user & groups** | User groups grant platform permissions (manage rooms / users / platform), with an access-request to approval lifecycle; per-user data isolation; Light/Dark/Auto themes. |
| **Preferences & profile** | Per-user AI endpoint/model/key, reasoning, profile fields, native/app language, and a device-synced theme. |
| **Model settings** | Platform-wide LLM controls: minutes-generation mode and a global AI request timeout (default 120s) covering every AI call. |
| **Storage quotas & retention** | Per-user audio quotas plus an optional nightly auto-deletion of old audio (transcripts kept) with per-recording protection. |
| **Backup & restore** | A Platform Administrator can export the whole platform (database + files) as one archive and restore it. |
| **Desktop apps** | Electron thin shell for Windows (tray) and macOS (menu-bar, beta): system audio, tray recording, Google sign-in, auto-update on Windows. |
| **Status bar** | Live pipeline progress plus storage, transcription, and transcript counts along the bottom. |

See **[docs/features.md](docs/features.md)** for the full prose description of each feature.

## Architecture

| Component | Tech | Path |
| :--- | :--- | :--- |
| API / auth / orchestration | ASP.NET Core (C#) + EF Core + SignalR | [src/Diariz.Api](src/Diariz.Api) |
| Domain model + migrations | EF Core + Postgres/pgvector | [src/Diariz.Domain](src/Diariz.Domain) |
| Transcription + diarization + voiceprints | Python: WhisperX (large-v3) + pyannote 3.1 + SpeechBrain ECAPA (GPU) | [src/Diariz.Worker](src/Diariz.Worker) |
| Web UI | React + TypeScript + Vite + Tailwind | [apps/web](apps/web) |
| Desktop app | Electron thin shell — Windows system-tray + **macOS (beta) menu-bar** (first-run server config, mic + system audio, tray recording; auto-update on Windows, manual update check on macOS) | [apps/desktop](apps/desktop) |
| Orchestration | docker-compose (postgres/pgvector, redis, minio) | [deploy](deploy) |

Summaries and chat use any OpenAI-compatible LLM endpoint you configure (OpenAI, or a local server such
as Ollama / LM Studio / vLLM) — see the Settings modal and `deploy/.env.example`. The API also hosts an
in-process **MCP server** at `/mcp` (Streamable HTTP) so **Claude** can connect to a user's own transcripts
using the same built-in tools — authenticated with either a personal access token (Desktop/Code) or an
**OAuth 2.1 sign-in** (the claude.ai web connector; the API is also a spec-compliant OAuth authorization server,
built on OpenIddict).

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
- **M3 — done:** chat across transcripts, including **semantic (RAG) search** over an embedded transcript
  index (pgvector) fused with keyword search, and an **"All meetings"** chat mode that answers across your whole
  library; speaker identification via enrolled voiceprints (pgvector).
- **M4 — in progress:** Windows desktop app (done), **macOS desktop app (beta - unsigned)**, mobile,
  packaging, live streaming.

For the next major arc - note enhancement, workflows/automations, collaborative shared spaces, and
optional ambient capture - see the [long-term roadmap](docs/long_term_roadmap.md).

> **Keep this README current.** When a PR changes what the app does (a new feature, a stack change, or a
> shipped roadmap item), update the **Features** table (one concise row) and **[docs/features.md](docs/features.md)**
> (the full prose), plus the **Architecture** and **Roadmap** sections and the in-app About-box `CAPABILITIES`
> table - all in the same PR, alongside the [`releases.ts`](apps/web/src/lib/releases.ts) entry required by
> [CLAUDE.md](CLAUDE.md). (The version isn't repeated here on purpose - it lives in `version.json` /
> `releases.ts` so it can't drift.)

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
data**: only enrol people with their consent, and use the Voice Prints tab to erase them on request.
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



