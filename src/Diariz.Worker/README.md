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

## AMD ROCm (experimental)

There's a parallel **AMD ROCm** worker (`Dockerfile.rocm` + `deploy/docker-compose.rocm.yml`). The pipeline
is identical except the **Whisper ASR step**: faster-whisper / **CTranslate2 has no AMD GPU support**, so on
ROCm the ASR runs on **openai-whisper** (pure PyTorch) selected via `ASR_BACKEND=whisper`. Alignment
(wav2vec2), diarization (pyannote) and voiceprints (SpeechBrain ECAPA) are all PyTorch and run on ROCm
unchanged — PyTorch-ROCm exposes the AMD GPU as device **`"cuda"`**, so `DEVICE` stays `cuda`.

**Tradeoff:** openai-whisper has no CTranslate2 acceleration, so ASR is slower than the NVIDIA path
(the word-aligner re-times every segment afterwards, so accuracy is unchanged). The LLM/summarisation
endpoint is unaffected — it's a separate, implementer-chosen HTTP service.

Run it:

```bash
cd deploy
cp .env.example .env   # set HF_TOKEN etc.; the ROCm compose hardcodes ASR_BACKEND=whisper (not from .env)
docker compose -f docker-compose.rocm.yml up --build
```

The compose file grants AMD GPU access with `devices: /dev/kfd, /dev/dri`, `group_add: video, render`,
`security_opt: seccomp:unconfined` (no NVIDIA Container Toolkit). The host needs ROCm installed and the
user in the `video`/`render` groups. **Both** groups are added deliberately: `/dev/kfd` is conventionally
`root:render` `0660`, so `video` on its own is not enough to open it.

> ⚠️ **Native Linux only — NOT WSL2 / Docker Desktop on Windows.** This image uses the native-Linux ROCm
> path (`/dev/kfd` + `/dev/dri`). WSL2 doesn't expose `/dev/kfd` at all — it bridges GPU compute through a
> different device (`/dev/dxg`) and a WSL-specific ROCm build, which our image doesn't use. Trying to run it
> under Docker Desktop/WSL2 fails at startup with
> `error gathering device information while adding custom device "/dev/kfd": no such file or directory`.
> AMD's "ROCm on WSL" also only supports a short list of discrete Radeon cards — Strix Halo (gfx1151) isn't
> among them — so WSL2 GPU acceleration for this APU isn't available today. **Confirmed on a Strix Halo box,
> 2026-08-12.** Note the error comes from the Docker *daemon*, while it resolves the `devices:` list — the
> container is never created, so no environment variable (`HSA_OVERRIDE_GFX_VERSION`, `DEVICE`, …) can
> affect it. Run the AMD worker on a **native Linux** install. If you must stay on Windows/WSL2, skip this image and run the
> **standard** worker **CPU-only** instead: use `docker-compose.yml`, comment out the worker's
> `deploy.resources` GPU block, and set `WORKER_DEVICE=cpu WORKER_COMPUTE_TYPE=int8` (functional everywhere,
> just far slower — see [CPU-only](#cpu-only)).

### Strix Halo (gfx1151) — the initial target

`Dockerfile.rocm` bases on `rocm/pytorch` (torch/torchaudio come from the image, matched to the ROCm
runtime — don't reinstall them). Strix Halo (Ryzen AI Max APU / Radeon 8060S, **gfx1151**) support is
recent, so:

- **Use a recent kernel.** 6.15+ is the reported sweet spot; 6.8 may not bring the GPU up at all. The
  kernel must have `CONFIG_HSA_AMD` (`=y` or `=m`), or there is no `/dev/kfd` to hand the container.
- **The base tag is pinned, and the window is narrow.** `Dockerfile.rocm` pins
  `rocm/pytorch:rocm7.2.4_ubuntu24.04_py3.12_pytorch_release_2.8.0`, confirmed on a Ryzen AI Max+ 395 /
  Radeon 8060S. Don't move it to `latest` and don't bump it blind — two opposing constraints squeeze it:
  - **ROCm must be new enough for gfx1151.** On `rocm7.0.2` + torch 2.7.1 the GPU is detected correctly
    (gfx1151, 137 GB) but *every* device allocation segfaults — `torch.randn(64, 64, device="cuda")` is
    enough, and `HSA_OVERRIDE_GFX_VERSION=11.0.0`/`11.5.1` do not rescue it. **7.2.4** is the known-good floor.
  - **torchaudio must be old enough for pyannote.** torchaudio **2.9** dropped the top-level
    `AudioMetaData` and `info` symbols that pyannote.audio 3.3.2 uses in `core/io.py`, so torch ≥ 2.9 dies at
    `import whisperx` with `AttributeError: module 'torchaudio' has no attribute 'AudioMetaData'` and the
    worker crash-loops before taking a job. torch **2.8.0** still has both.

  This is not hypothetical: `:latest` drifted to ROCm 7.14 / torch 2.13 / torchaudio 2.11 and took every
  ROCm worker down with exactly that `AudioMetaData` error, with no repo change to explain it.
  `tests/test_dockerfile_pins.py` now fails if either worker Dockerfile goes back to a floating tag.
- **`HSA_OVERRIDE_GFX_VERSION` is worth trying for speed, not just for errors.** Set it (e.g. `11.0.0` to
  borrow gfx1100 kernels) if model load fails with *"no kernel image" / "invalid device function"* — but
  also try it when things already work: on gfx1151 the gfx1100 kernels have been measured **2-6x faster**
  than the native ones. It's plumbed through the compose env.
  - ⚠️ **Empty is not the same as unset.** The compose file interpolates `${HSA_OVERRIDE_GFX_VERSION:-}`
    and `.env.example` ships the key blank, so the container gets the variable *defined but empty* — and
    the ROCm runtime then fails to enumerate the GPU at all. Measured on a Radeon 8060S with one image:
    no variable → `torch.cuda.is_available()` `True`; `HSA_OVERRIDE_GFX_VERSION=` → `False`, i.e. a silent
    fall back to CPU with nothing in the logs to explain it. `worker.py` therefore calls
    `rocm_env.clean_gfx_override()` **before importing torch**, which deletes an empty value (and logs a
    warning) and trims a padded one. Keep that call first if you reorder the imports.
- **No `/dev/kfd` on native Linux?** The usual cause is the `amdgpu` module being blacklisted (some GPU
  driver installers add one). Deleting the file in `/etc/modprobe.d/` is *not* enough — the blacklist stays
  cached in the initramfs, so rebuild it:
  ```bash
  grep -rn "blacklist.*amdgpu" /etc/modprobe.d/   # find it
  sudo update-initramfs -u                        # the step people miss
  sudo modprobe amdgpu && ls -l /dev/kfd
  ```
- Strix Halo is an **APU with unified memory** — its "VRAM" is carved from system RAM. Allocate enough
  GTT/VRAM in BIOS for the ~9 GB working set (large-v3 + align + pyannote).
- **Build pins `setuptools<81`** for the pip install step. openai-whisper's `setup.py` imports
  `pkg_resources`, which `setuptools >= 81` no longer ships; the `rocm/pytorch` base bundles a recent pip
  that otherwise pulls `setuptools >= 81` into the isolated wheel build and fails with *"No module named
  'pkg_resources'"*. `PIP_CONSTRAINT` scopes the pin to the build (it applies to the isolated build overlay).

> **Status: validated end-to-end on AMD hardware.** Confirmed on a Ryzen AI Max+ 395 (Radeon 8060S,
> gfx1151, kernel 7.0.0-29, Docker CE) with the pinned `rocm7.2.4 / torch 2.8.0` base: a 269 s meeting
> recording transcribed to **106 segments across 2 speakers**, with a 192-d ECAPA voiceprint per speaker -
> i.e. ASR, word alignment, diarization and speaker embeddings all running on the AMD GPU (`rocm-smi`
> showed 95-98% GPU use throughout).
>
> **Throughput (single sample, so treat as indicative, not a benchmark):** ~1.3-1.7x realtime for
> `large-v3` at default settings - 269 s of audio in 155 s and 201 s on two consecutive runs in the same
> process. The second run being *slower* despite reusing in-memory models points at power/thermal
> behaviour on an APU rather than anything in the pipeline; it has not been investigated.
>
> **Tuning levers, measured on this box - both of the obvious ones were dead ends.** Same 269 s file,
> 3 consecutive in-process runs per config, run 0 being the model-load run:
>
> | Config | Run 0 (cold) | Warm runs | Warm mean |
> | --- | --- | --- | --- |
> | baseline | 167.6 s | 149.2 / 184.5 s | 166.8 s |
> | `HSA_OVERRIDE_GFX_VERSION=11.0.0` | **381.5 s** | 165.2 / 162.2 s | 163.7 s |
> | TF32 attempt (see below) | 197.6 s | 200.2 / 123.3 s | 161.8 s |
> | **`DEVICE=cpu`** (same box, 16C/32T Zen 5) | 451.9 s | 481.4 s | **481.4 s** |
>
> **The GPU is worth about 2.8x over this machine's own CPU** (466.6 s mean vs 164.3 s), or 3.7x
> best-against-best. Put in operational terms: an hour of audio costs roughly **37 minutes on the GPU
> and 104 minutes on the CPU** - i.e. the CPU path runs at **0.56-0.60x realtime**, *slower than the
> meeting it is transcribing*, so it cannot keep up with a steady recording habit and is a
> get-it-working fallback rather than a deployment option. Note this is an APU: the CPU here is a
> strong 16-core Zen 5 sharing one package power budget with the GPU, so the gap is narrower than a
> discrete GPU would give.
>
> - **`HSA_OVERRIDE_GFX_VERSION=11.0.0` bought nothing here.** The warm means are within noise of baseline
>   (163.7 s vs 166.8 s), and the *cold* run cost **2.3x more** (381.5 s vs 167.6 s) - consistent with
>   MIOpen rebuilding its kernel cache for the substituted architecture. The widely-repeated "2-6x faster
>   on gfx1151" did **not** reproduce on ROCm 7.2.4 / torch 2.8.0. It may still help on older ROCm, where
>   the native gfx1151 kernels were worse; treat it as something to measure, not to set by default.
> - **TF32 is not available on this GPU at all, so pyannote's `ReproducibilityWarning` is a red herring.**
>   `torch.backends.cuda.matmul.allow_tf32` reads back `False` immediately after being set to `True`
>   (gfx1151 is RDNA 3.5 and has no TF32 path). The "TF32" row above is therefore just a third baseline
>   sample, which is exactly why it is useful: pooled with baseline, the no-override warm runs span
>   **123.3-200.2 s**, a **±25%** band on identical audio.
>
> That noise floor is the real headline: with N=3 nothing smaller than roughly a 1.5x effect is
> measurable this way, so treat any micro-tuning claim (including these numbers) with suspicion unless it
> is backed by many more runs. The MIOpen `IsEnoughWorkspace` fallbacks during alignment were not
> investigated for the same reason.
>
> **Output is not deterministic between runs** (65 / 116 / 105 segments across three runs of the same
> audio). That is inherent to Whisper rather than to ROCm: `_asr` calls openai-whisper's `transcribe()`
> with its default **temperature fallback**, so any segment failing the logprob / compression-ratio
> thresholds is retried with sampling at temperature up to 1.0.

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
