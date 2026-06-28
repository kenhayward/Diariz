# Diariz transcription worker

Consumes transcription jobs from a Redis Stream, runs **WhisperX** (faster-whisper
`large-v3`) for transcription + word-level timestamps, then **pyannote.audio 3.1** for
speaker diarization, and posts the diarized, timestamped segments back to the API.

## Required: Hugging Face token for diarization

pyannote's diarization models are gated. Before the worker can diarize you must:

1. Create a Hugging Face access token.
2. Accept the user conditions for **`pyannote/speaker-diarization-3.1`** and
   **`pyannote/segmentation-3.0`** on huggingface.co.
3. Set `HF_TOKEN` in the worker environment (see `deploy/.env`).

Without `HF_TOKEN` the worker raises a clear error and the job is marked failed.

## GPU and hardware requirements

The worker is GPU-first. On each job it loads **Whisper large-v3**, a **wav2vec2** alignment
model, the **pyannote 3.1** diarizer, and (optionally) the **SpeechBrain ECAPA** voiceprint model,
and keeps them resident across jobs (lazy-loaded + cached). You need an **NVIDIA GPU with CUDA**;
the host also needs the **NVIDIA Container Toolkit** for the Dockerised worker (the `worker` service
in `docker-compose.yml` requests the GPU). CPU-only works but is far slower — see the end.

### VRAM

With the defaults (`WHISPER_MODEL=large-v3`, `COMPUTE_TYPE=float16`, `BATCH_SIZE=16`, diarization +
voiceprints on) all of the above are resident at once:

| Component | Approx VRAM (fp16) |
|---|---|
| Whisper large-v3 (faster-whisper / CTranslate2) | ~4.5–5 GB |
| WhisperX alignment (wav2vec2) | ~1–2 GB |
| pyannote diarization 3.1 (segmentation + embedding) | ~2–3 GB |
| SpeechBrain ECAPA voiceprints | ~0.5 GB |
| CUDA context + batch-16 activations | ~1–2 GB |
| **Peak (defaults)** | **~10–12 GB** |

- **≥ 12 GB** — runs the defaults comfortably, with headroom.
- **8–12 GB** — works with light tuning (int8 compute and/or a smaller batch).
- **~6 GB** — the practical floor: trim features (int8 + small batch + voiceprints off, or a smaller model).

### Tuning for less VRAM

All via env vars on the worker (see `config.py`):

| Var | Default | Effect |
|---|---|---|
| `COMPUTE_TYPE` | `float16` | `int8` (or `int8_float16`) — biggest single saving; large-v3 weights drop to ~2–3 GB |
| `BATCH_SIZE` | `16` | lower it (e.g. `4`–`8`) to cut activation memory (slightly slower) |
| `ENABLE_SPEAKER_EMBEDDINGS` | `1` | `0` drops the voiceprint model (you keep transcription + diarization) |
| `WHISPER_MODEL` | `large-v3` | `medium` / `small` — lighter + faster, lower accuracy |

Example, to fit ~8 GB: `COMPUTE_TYPE=int8 BATCH_SIZE=8`.

### GPU architecture support

The image pins the **cu128** torch stack (CUDA 12.8), which covers **Ampere (RTX 30-series),
Ada (RTX 40-series), and Blackwell (RTX 50-series)**. The cu128 pin is *required* for Blackwell
(sm_120): the older cu121 / torch 2.5 wheels only compile kernels up to sm_90 and fail at model load
on a 50-series card (*"no kernel image is available for execution on the device"*). **Turing
(RTX 20-series, sm_75) is a sensible minimum**; Pascal (GTX 10-series) has weak fp16 support and is
not recommended.

### Known-working GPUs

| GPU | VRAM | Architecture | Status |
|---|---|---|---|
| **RTX 5090** | 32 GB | Blackwell (sm_120) | **Tested** — runs the defaults with large headroom |
| RTX 3090 | 24 GB | Ampere (sm_86) | Expected — defaults should run with headroom |
| RTX 4070 Laptop | 8 GB | Ada (sm_89) | Expected — tune for 8 GB (`COMPUTE_TYPE=int8`, `BATCH_SIZE=4`–`8`); defaults will be tight |

> Only the RTX 5090 is confirmed by testing so far; the others are reasoned estimates from VRAM and
> architecture. If you run Diariz on another card, a PR updating this table (with your settings and
> rough VRAM headroom) is welcome.

### CPU-only

Set `DEVICE=cpu COMPUTE_TYPE=int8`. It works but is dramatically slower (think minutes of compute per
minute of audio) — intended for development/CI, not production.

## Local run (outside Docker)

```bash
# cu128 wheels — required for RTX 50-series (Blackwell); also fine on 30/40-series.
pip install torch==2.7.1 torchaudio==2.7.1 --index-url https://download.pytorch.org/whl/cu128
pip install -r requirements.txt
HF_TOKEN=... REDIS_URL=redis://localhost:6379/0 API_BASE_URL=http://localhost:8080 python worker.py
```

## Tests

Fast, GPU-free unit tests (pytest). `whisperx`/`torch` are **not** required — the suite stubs
`whisperx` (see `tests/conftest.py`), so it covers the callback contract, the job
orchestration/cleanup in `worker.handle`, and the segment-shaping in `pipeline._shape_segments`.

```bash
python -m venv .venv
.venv/Scripts/python -m pip install -r requirements-test.txt   # Linux/macOS: .venv/bin/python
.venv/Scripts/python -m pytest
```
