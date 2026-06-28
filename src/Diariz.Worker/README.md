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

The worker keeps Whisper + alignment + diarization (+ optional ECAPA) resident at once. Only the **Whisper**
part shrinks with the tuning knobs; the alignment + diarization models are a **fixed floor**:

| Component | Approx VRAM | Shrinks with |
|---|---|---|
| Whisper (faster-whisper / CTranslate2) | large-v3 fp16 ~3 GB · int8_float16 ~1.5 GB · medium ~0.8 GB | `COMPUTE_TYPE`, `WHISPER_MODEL`, `BATCH_SIZE` |
| wav2vec2 alignment | ~1–2 GB | — (fixed) |
| pyannote 3.1 diarization | ~2–3 GB | — (fixed) |
| SpeechBrain ECAPA voiceprints | ~0.5 GB | `ENABLE_SPEAKER_EMBEDDINGS=0` |
| CUDA context + PyTorch caching allocator | ~2 GB+ | — |

**The real working set at defaults is ~9 GB.** Measured cleanly on an RTX 3090 (24 GB): ~0.9 GB at idle,
**~9.2 GB during transcription, no spill**. The **alignment + diarization models dominate and don't shrink
with `COMPUTE_TYPE`/`BATCH_SIZE`/`WHISPER_MODEL`** (those only touch Whisper), so lowering the batch size
won't move the peak if the peak is the diarization stage.

> ⚠️ On a card that's *too small* (e.g. an 8 GB laptop), the numbers in Task Manager balloon and mislead:
> once the ~9 GB working set won't fit, PyTorch spills into Windows "shared GPU memory" and reserves in large
> blocks, and Windows counts dedicated + reserved-shared together — so an 8 GB 4070 *reports* ~13–16 GB total
> even though the genuine requirement is ~9 GB. Don't size from those inflated figures; size from the ~9 GB.

Guidance:
- **≥ 10 GB** — runs the defaults (`large-v3`, `float16`) **entirely in VRAM**, no spill (≈9 GB used). 12 GB+
  is comfortable.
- **8 GB** — just under the working set, so it **spills a little into shared/system memory**. It still works
  and isn't necessarily slow. To minimise the spill: `WHISPER_MODEL=medium`, `COMPUTE_TYPE=int8_float16`
  (and `ENABLE_SPEAKER_EMBEDDINGS=0` if you don't need cross-recording speaker identification).

### Tuning for less VRAM

All via env vars on the worker (see `config.py`):

| Var | Default | Effect |
|---|---|---|
| `COMPUTE_TYPE` | `float16` | `int8_float16` (or `int8`) — biggest single saving; large-v3 weights ~3 GB → ~1.5 GB |
| `WHISPER_MODEL` | `large-v3` | `medium` / `small` — lighter + faster, lower accuracy |
| `BATCH_SIZE` | `16` | lower it (`8`/`4`) to cut *transcription* activations — **no effect if the peak is diarization** |
| `ENABLE_SPEAKER_EMBEDDINGS` | `1` | `0` drops the voiceprint model (you keep transcription + diarization) |

On 8 GB the goal is to *minimise* spill — the ~9 GB working set just exceeds 8 GB:
`WHISPER_MODEL=medium COMPUTE_TYPE=int8_float16`.

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
| **RTX 3090** | 24 GB | Ampere (sm_86) | **Tested** — defaults, **~9.2 GB during transcription, no spill** (~0.9 GB idle). Lots of headroom. |
| **RTX 4070 Laptop** | 8 GB | Ada (sm_89) | **Tested** — works, but the ~9 GB working set just exceeds 8 GB, so it spills into shared memory (Task Manager *reports* ~13–16 GB, inflated by the allocator + WDDM accounting — the genuine need is ~9 GB). Not slow. `WHISPER_MODEL=medium` + `COMPUTE_TYPE=int8_float16` cut the spill; `int8`/`int8_float16` are fine on Ada. |

> RTX 5090, RTX 3090, and RTX 4070 Laptop are confirmed by testing. If you run Diariz on another card, a PR
> updating this table (with your settings and rough VRAM headroom) is welcome.

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
