"""The contract every candidate image implements: read /data/eval/audio/*.wav, write RTTM + run stats.

A candidate supplies `diarize(waveform: np.ndarray[float32], sample_rate: int) -> {variant: [(start, end,
speaker), ...]}` - more than one variant when one model run yields several outputs (pyannote's regular and
exclusive diarizations). Each variant lands in /data/out/<name>[-<variant>]/<uri>.rttm, with a runs.jsonl
line per file: audio seconds, wall seconds, and peak GPU memory.

Peak VRAM is the device's used memory sampled every 50 ms, minus what was in use before the model loaded,
so it includes the weights - which is what the ~9 GB budget is about. Device-level rather than
per-process because per-process accounting is not available under WSL2. Keep the GPU otherwise idle.
"""
from __future__ import annotations

import json
import os
import threading
import time
from pathlib import Path

import numpy as np
import soundfile as sf

try:
    import pynvml
    pynvml.nvmlInit()
    _HANDLE = pynvml.nvmlDeviceGetHandleByIndex(int(os.getenv("GPU_INDEX", "0")))
except Exception:  # noqa: BLE001 - CPU-only candidates still run, just without VRAM numbers
    _HANDLE = None


def gpu_used_mb() -> int | None:
    return pynvml.nvmlDeviceGetMemoryInfo(_HANDLE).used // (1 << 20) if _HANDLE else None


class PeakSampler:
    def __init__(self):
        self.peak = 0
        self._stop = threading.Event()

    def __enter__(self):
        self.peak = gpu_used_mb() or 0
        self._t = threading.Thread(target=self._run, daemon=True)
        self._t.start()
        return self

    def _run(self):
        while not self._stop.wait(0.05):
            self.peak = max(self.peak, gpu_used_mb() or 0)

    def __exit__(self, *exc):
        self._stop.set()
        self._t.join()


def write_rttm(path: Path, uri: str, turns):
    path.write_text("".join(
        f"SPEAKER {uri} 1 {s:.3f} {e - s:.3f} <NA> <NA> {spk} <NA> <NA>\n" for s, e, spk in turns if e > s))


def run_all(name: str, load, data: str = "/data", only: list[str] | None = None):
    """`load()` builds the model and returns the diarize callable; it is timed separately from the files."""
    idle = gpu_used_mb()
    t0 = time.perf_counter()
    with PeakSampler() as load_peak:
        diarize = load()
    print(f"[{name}] model loaded in {time.perf_counter() - t0:.1f}s", flush=True)

    audio_dir = Path(data) / "eval" / "audio"
    files = sorted(audio_dir.glob("*.wav"))
    if only:
        files = [f for f in files if f.stem in only]

    # Warm-up on 30 s so the first file's numbers are not dominated by kernel compilation / cuDNN autotune.
    wav, sr = sf.read(files[0], dtype="float32")
    diarize(wav[: 30 * sr], sr)

    for f in files:
        uri = f.stem
        wav, sr = sf.read(f, dtype="float32")
        if wav.ndim > 1:
            wav = wav.mean(axis=1)
        with PeakSampler() as peak:
            t = time.perf_counter()
            outputs = diarize(np.ascontiguousarray(wav), sr)
            wall = time.perf_counter() - t
        for variant, turns in outputs.items():
            out = Path(data) / "out" / (f"{name}-{variant}" if variant else name)
            out.mkdir(parents=True, exist_ok=True)
            write_rttm(out / f"{uri}.rttm", uri, turns)
            with open(out / "runs.jsonl", "a") as fh:
                fh.write(json.dumps({
                    "uri": uri, "audio_s": round(len(wav) / sr, 2), "wall_s": round(wall, 2),
                    "peak_vram_mb": (max(peak.peak, load_peak.peak) - idle) if idle is not None else None,
                }) + "\n")
        print(f"[{name}] {uri}: {len(wav) / sr / 60:.1f} min in {wall:.1f}s, "
              f"{len({s for _, _, s in next(iter(outputs.values()))})} speakers", flush=True)
