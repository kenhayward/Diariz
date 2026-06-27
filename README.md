# Diariz

![Diariz](images/Diariz%20Backdrop.png)

**Smart Meeting Transcription.** Personal, multi-user voice/meeting transcription platform: record audio
(mic or system loopback), transcribe it server-side with **speaker diarization** and word-level timestamps,
**identify known speakers** across recordings, **summarize**, and **chat across transcripts**. See
[docs/Overall_Synopsis_of_Platform.md](docs/Overall_Synopsis_of_Platform.md) for the original brief and the
architecture plan for the full design.

Current version: **0.4.1** — versioned per the rule in [CLAUDE.md](CLAUDE.md); per-release notes live in
[`apps/web/src/lib/releases.ts`](apps/web/src/lib/releases.ts) and on the in-app **Release Notes** page
(`/release-notes`), reachable from **About** in the account menu.

## Features

- **Capture** audio from the browser microphone, or Windows system/loopback audio via the Electron desktop shell.
- **Transcribe + diarize** server-side with WhisperX (large-v3, word-level timestamps) and pyannote 3.1,
  producing speaker-labelled, timestamped segments you can rename, edit, and play back (per segment or whole).
  Re-transcribe with a chosen model at any time, **merge** consecutive same-speaker rows, and **email
  yourself** the formatted transcript.
- **Identify speakers** across recordings: enrol a person from a recording's speaker and Diariz recognises
  that voice automatically in later recordings (SpeechBrain ECAPA voiceprints in pgvector, cosine matching),
  with manual reassignment. A **People** screen renames, prunes training samples, merges duplicates, and
  erases voiceprints (GDPR — biometric data).
- **Summarize** recordings (with automatic naming) and **chat across one or more transcripts** — streaming
  replies, a context-usage dial, PDF/text attachments, and saved conversations — via a per-user (or
  server-default) OpenAI-compatible LLM endpoint, with the API key encrypted at rest.
- **Organise** recordings into sections with drag-and-drop ordering and cross-group moves.
- **Multi-user RBAC**: Standard / Administrator / Platform Administrator roles, an access-request →
  admin-grant → account-setup lifecycle (one-time email link, with an in-app fallback when SMTP is
  unconfigured), and admin user management. Each user's data is isolated to them. Light/Dark/Auto theming.
- **Storage quotas**: each user gets an audio-storage quota (starter + maximum set by the Platform
  Administrator; any admin can raise a user up to the maximum). Usage shows in the account menu and
  per-recording; over-quota uploads are rejected.

## Architecture

| Component | Tech | Path |
|---|---|---|
| API / auth / orchestration | ASP.NET Core (C#) + EF Core + SignalR | [src/Diariz.Api](src/Diariz.Api) |
| Domain model + migrations | EF Core + Postgres/pgvector | [src/Diariz.Domain](src/Diariz.Domain) |
| Transcription + diarization + voiceprints | Python: WhisperX (large-v3) + pyannote 3.1 + SpeechBrain ECAPA (GPU) | [src/Diariz.Worker](src/Diariz.Worker) |
| Web UI | React + TypeScript + Vite + Tailwind | [apps/web](apps/web) |
| Desktop shell | Electron (mic + Windows loopback) | [apps/desktop](apps/desktop) |
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
terms accepted — see [src/Diariz.Worker/README.md](src/Diariz.Worker/README.md).

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

## Roadmap

- **M1 — done:** capture → transcribe (timestamps + diarization) → view.
- **M2 — done:** multi-user auth + RBAC, LLM summaries, transcript export, re-transcribe with model choice.
- **M3 — done:** chat across transcripts; speaker identification via enrolled voiceprints (pgvector).
- **M4 — in progress:** macOS/mobile, TLS via Caddy, packaging, live streaming.

> **Keep this README current.** When a PR changes what the app does (a new feature, a stack change, or a
> shipped roadmap item), update the **Features**, **Architecture**, and **Roadmap** sections and the version
> line above in the same PR — alongside the [`releases.ts`](apps/web/src/lib/releases.ts) entry required by
> [CLAUDE.md](CLAUDE.md).
