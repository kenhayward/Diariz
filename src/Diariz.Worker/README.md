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

## GPU

The Dockerfile uses a CUDA base image and installs CUDA-enabled torch. The host needs
the NVIDIA Container Toolkit; the worker service in `docker-compose.yml` requests the GPU.
For CPU-only runs, set `DEVICE=cpu` and `COMPUTE_TYPE=int8` (much slower).

## Local run (outside Docker)

```bash
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu121
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
