"""Measure the per-chunk floor of the transcription pipeline, stage by stage.

Answers one question: as a clip gets shorter, what does the per-job cost converge to? That
fixed floor decides whether live chunked transcription can keep up with a meeting, because a
30 s chunk costing 45 s of wall clock falls behind without bound.

Run INSIDE the worker container, where the models are cached and the GPU is attached:

    docker cp clips           <worker>:/tmp/clips
    docker cp chunk_floor.py  <worker>:/tmp/chunk_floor.py
    docker exec -it <worker> python3 /tmp/chunk_floor.py /tmp/clips

(run-local.ps1 does all of that against a locally-built image.)

It replicates pipeline.transcribe() stage by stage rather than calling it, so each stage is
timed separately. Nothing is written back to the API and no job queue is touched - this reads
the models and nothing else.

See docs/Streaming_Capture_and_Live_Transcript.md section 3 for measured results.
"""
import argparse
import glob
import json
import os
import statistics
import sys
import time

sys.path.insert(0, "/app")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# The same two shims worker.py applies before touching torch, in the same order. This script
# bypasses worker.py, so without them the pyannote VAD checkpoint fails to load on torch>=2.6
# ("Weights only load failed ... Unsupported global") and an empty HSA_OVERRIDE_GFX_VERSION
# silently drops a ROCm box to CPU. Both are easy to lose and hard to diagnose.
import rocm_env  # noqa: E402

rocm_env.clean_gfx_override()

import torch_compat  # noqa: E402

torch_compat.restore_legacy_torch_load()

import whisperx  # noqa: E402
import pipeline  # noqa: E402
from config import config  # noqa: E402

STAGES = ["decode", "asr", "align", "diarize", "shape", "embeddings"]


class Timer:
    """Accumulates stage timings for one pass."""

    def __init__(self) -> None:
        self.stages: dict[str, float] = {}

    def time(self, name: str, fn):
        t0 = time.perf_counter()
        result = fn()
        self.stages[name] = time.perf_counter() - t0
        return result

    @property
    def total(self) -> float:
        return sum(self.stages.values())


def one_pass(path: str) -> tuple[Timer, dict]:
    """One full pipeline pass over a clip, timed per stage. Mirrors pipeline.transcribe()."""
    t = Timer()

    audio = t.time("decode", lambda: whisperx.load_audio(path))
    duration_ms = pipeline._duration_ms(audio)

    asr = t.time("asr", lambda: pipeline._asr(audio, None))
    language = asr["language"]

    def do_align():
        aligned = pipeline._get_align(language)
        if aligned is None:
            return {"segments": asr["segments"]}
        model, meta = aligned
        return whisperx.align(asr["segments"], model, meta, audio, config.DEVICE,
                              return_char_alignments=False)

    result = t.time("align", do_align)

    def do_diarize():
        diar = pipeline._diarize(audio, None, None)
        return whisperx.assign_word_speakers(diar, result)

    result = t.time("diarize", do_diarize)
    segments = t.time("shape", lambda: pipeline._shape_segments(result["segments"]))
    speakers = t.time("embeddings", lambda: pipeline._extract_speakers(audio, segments))

    return t, {"duration_ms": duration_ms, "segments": len(segments),
               "speakers": len(speakers or []), "language": language}


def clip_seconds(path: str) -> float:
    """Length in seconds from a clip-NNNs.wav filename."""
    stem = os.path.splitext(os.path.basename(path))[0]
    return float(stem.split("-")[-1].rstrip("s"))


def fit(xs: list[float], ys: list[float]) -> tuple[float, float]:
    """Least-squares (intercept, slope). The intercept is the fixed per-job cost.

    Only meaningful when the curve has no regime change. A GPU that runs out of memory
    partway up the ladder produces a step, and fitting one line across it yields a negative
    intercept - an artefact, not a floor. Read such a ladder row by row instead.
    """
    n = len(xs)
    mx, my = sum(xs) / n, sum(ys) / n
    denom = sum((x - mx) ** 2 for x in xs)
    slope = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / denom if denom else 0.0
    return my - slope * mx, slope


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("clips_dir")
    ap.add_argument("--repeats", type=int, default=3,
                    help="timed passes per clip (default 3; run-to-run noise is real)")
    ap.add_argument("--json", dest="json_out", default=None, help="also write raw results here")
    args = ap.parse_args()

    paths = sorted(glob.glob(os.path.join(args.clips_dir, "*.wav")))
    if not paths:
        sys.exit(f"no .wav files in {args.clips_dir}")

    print("=" * 78)
    print("Diariz per-chunk floor measurement")
    print("=" * 78)
    print(f"device        {config.DEVICE}   compute {config.COMPUTE_TYPE}")
    print(f"asr backend   {config.ASR_BACKEND}   model {config.WHISPER_MODEL}   batch {config.BATCH_SIZE}")
    print(f"embeddings    {'on' if config.ENABLE_SPEAKER_EMBEDDINGS else 'OFF'}")
    try:
        import torch
        if torch.cuda.is_available():
            print(f"gpu           {torch.cuda.get_device_name(0)}  "
                  f"{torch.cuda.get_device_properties(0).total_memory / 1e9:.1f} GB")
    except Exception:  # noqa: BLE001 - a missing/!CUDA torch must not abort the run
        pass
    print(f"clips         {len(paths)}   repeats {args.repeats}")
    print()

    print("warming models (cold start, reported separately)...", flush=True)
    t0 = time.perf_counter()
    one_pass(paths[0])
    cold = time.perf_counter() - t0
    print(f"cold first pass: {cold:.1f}s   <- what a second worker process pays once at boot\n")

    header = f"{'clip':>7} {'total':>8} {'xRT':>6} " + " ".join(f"{s:>9}" for s in STAGES) + f" {'segs':>5}"
    print(header)
    print("-" * len(header))

    results = []
    for path in paths:
        secs = clip_seconds(path)
        runs, meta = [], {}
        for _ in range(args.repeats):
            t, meta = one_pass(path)
            runs.append(t)

        totals = [t.total for t in runs]
        median_total = statistics.median(totals)
        med = {s: statistics.median([t.stages[s] for t in runs]) for s in STAGES}
        xrt = median_total / secs

        flag = "  <-- SLOWER THAN REALTIME" if xrt >= 1.0 else ""
        print(f"{secs:>6.0f}s {median_total:>7.2f}s {xrt:>5.2f}x " +
              " ".join(f"{med[s]:>8.2f}s" for s in STAGES) +
              f" {meta['segments']:>5}{flag}")

        results.append({
            "clip_seconds": secs,
            "total_median_s": median_total,
            "total_min_s": min(totals),
            "total_max_s": max(totals),
            "spread_pct": (max(totals) - min(totals)) / median_total * 100 if median_total else 0,
            "realtime_factor": xrt,
            "stages_median_s": med,
            "segments": meta.get("segments"),
            "speakers": meta.get("speakers"),
        })

    print()
    print("=" * 78)
    print("Interpretation")
    print("=" * 78)

    floor, slope = fit([r["clip_seconds"] for r in results], [r["total_median_s"] for r in results])
    print(f"fixed floor per job     {floor:6.2f} s   (paid by every chunk regardless of length)")
    if slope > 0:
        print(f"marginal cost           {slope:6.3f} s per second of audio  ({1 / slope:.2f}x realtime)")
    if floor < 0:
        print("  NOTE: a negative floor means the ladder has a step change (typically a GPU running")
        print("  out of memory partway up). The fit is invalid here - read the rows individually.")
    print()

    for target in (20, 30, 45, 60):
        predicted = floor + slope * target
        verdict = "KEEPS UP" if predicted < target * 0.8 else ("TIGHT" if predicted < target else "FALLS BEHIND")
        print(f"  {target:>2}s chunk -> {predicted:5.1f}s to process   "
              f"{(target - predicted) / target * 100:+6.1f}% headroom   {verdict}")

    print()
    spreads = [r["spread_pct"] for r in results]
    print(f"run-to-run spread: median {statistics.median(spreads):.0f}%, worst {max(spreads):.0f}%")
    print("A chunk size only works if it keeps up at the WORST observed time, not the median.")
    print("Clips under ~20 s of this ladder contain one speaker, so their diarization cost is")
    print("unrepresentative of a real chunk - size against the 20 s row, not the 15 s one.")

    if args.json_out:
        with open(args.json_out, "w") as f:
            json.dump({
                "config": {
                    "device": config.DEVICE, "compute": config.COMPUTE_TYPE,
                    "backend": config.ASR_BACKEND, "model": config.WHISPER_MODEL,
                    "batch_size": config.BATCH_SIZE,
                    "embeddings": bool(config.ENABLE_SPEAKER_EMBEDDINGS),
                },
                "cold_start_s": cold,
                "results": results,
                "fit": {"floor_s": floor, "slope_s_per_s": slope},
            }, f, indent=2)
        print(f"\nraw results -> {args.json_out}")


if __name__ == "__main__":
    main()
