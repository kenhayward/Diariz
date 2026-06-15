# Diariz

Personal, multi-user voice/meeting transcription platform: record audio (mic or system
loopback), transcribe it server-side with **speaker diarization** and timestamps, summarize,
and chat across notes. See [docs/Overall_Synopsis_of_Platform.md](docs/Overall_Synopsis_of_Platform.md)
for the original brief and the architecture plan for the full design.

## Architecture

| Component | Tech | Path |
|---|---|---|
| API / auth / orchestration | ASP.NET Core (C#) + EF Core + SignalR | [src/Diariz.Api](src/Diariz.Api) |
| Domain model + migrations | EF Core + Postgres/pgvector | [src/Diariz.Domain](src/Diariz.Domain) |
| Transcription + diarization | Python: WhisperX (large-v3) + pyannote 3.1 (GPU) | [src/Diariz.Worker](src/Diariz.Worker) |
| Web UI | React + TypeScript + Vite + Tailwind | [apps/web](apps/web) |
| Desktop shell | Electron (mic + Windows loopback) | [apps/desktop](apps/desktop) |
| Orchestration | docker-compose (postgres, redis, minio, ollama*) | [deploy](deploy) |

\* Ollama (local LLM for summaries/chat) lands in Milestone 2–3.

**Flow:** client records → uploads to API → audio stored in MinIO, metadata in Postgres →
job enqueued on a Redis Stream → Python worker transcribes + diarizes → posts segments back →
API stores them and notifies the client over SignalR → note view shows speaker-labeled,
timestamped segments.

## Quick start (Milestone 1 — capture → transcribe → view)

Prerequisites: Docker (+ NVIDIA Container Toolkit for the GPU worker), .NET 10 SDK, Node 20+.
For diarization you need a Hugging Face token with the `pyannote/speaker-diarization-3.1`
terms accepted — see [src/Diariz.Worker/README.md](src/Diariz.Worker/README.md).

```bash
# 1. Backend stack (API, Postgres, Redis, MinIO, GPU worker)
cd deploy
cp .env.example .env        # fill in JWT_KEY, CALLBACK_SECRET, HF_TOKEN, seed user
docker compose up --build

# 2. Web app (dev server, proxies /api and /hubs to the API)
cd ../apps/web
npm install && npm run dev  # http://localhost:5173

# 3. (optional) Desktop shell for system-audio capture
cd ../desktop
npm install && npm run dev
```

Sign in with the seeded user (`SEED_EMAIL` / `SEED_PASSWORD`), record a clip, and the
transcript appears automatically when the worker finishes.

## Roadmap

- **M1 (this scaffold):** capture → transcribe (timestamps + diarization) → view.
- **M2:** full multi-user auth hardening, LLM summaries, export, re-transcribe with model choice.
- **M3:** embeddings + RAG chat (per-note and cross-note).
- **M4:** macOS/mobile, TLS via Caddy, packaging, live streaming.
