"""Concatenate several audio files into one with ffmpeg.

The command-building is a pure function (`build_concat_command`) so it can be unit-tested without
ffmpeg; `concat()` runs ffmpeg + ffprobe and is exercised end-to-end only in real/integration runs.
"""
import os
import subprocess
import tempfile

OUTPUT_CONTENT_TYPE = "audio/webm"


def build_concat_command(input_paths: list[str], output_path: str) -> list[str]:
    """Build the ffmpeg argv that concatenates the inputs (in order) into one Opus/WebM file.

    Uses the concat *filter* (not the demuxer) + re-encode, so heterogeneous inputs (WebM/Opus, WAV,
    MP3, M4A, …) stitch together cleanly regardless of their original codecs/sample rates.
    """
    if not input_paths:
        raise ValueError("at least one input is required")
    args = ["ffmpeg", "-y"]
    for path in input_paths:
        args += ["-i", path]
    streams = "".join(f"[{i}:a]" for i in range(len(input_paths)))
    args += [
        "-filter_complex", f"{streams}concat=n={len(input_paths)}:v=0:a=1[out]",
        "-map", "[out]", "-c:a", "libopus", output_path,
    ]
    return args


def probe_duration_ms(path: str) -> int:
    """Return the media duration in milliseconds via ffprobe."""
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration",
         "-of", "default=noprint_wrappers=1:nokey=1", path],
        check=True, capture_output=True, text=True).stdout.strip()
    return int(round(float(out) * 1000))


def concat(input_paths: list[str]) -> tuple[str, int, int]:
    """Concatenate the inputs into a temp WebM/Opus file. Returns (path, duration_ms, size_bytes)."""
    fd, output_path = tempfile.mkstemp(suffix=".webm")
    os.close(fd)
    subprocess.run(build_concat_command(input_paths, output_path), check=True, capture_output=True)
    return output_path, probe_duration_ms(output_path), os.path.getsize(output_path)
