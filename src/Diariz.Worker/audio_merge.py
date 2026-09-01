"""Concatenate several audio files into one with ffmpeg.

The command-building is a pure function (`build_concat_command`) so it can be unit-tested without
ffmpeg; `concat()` runs ffmpeg + ffprobe and is exercised end-to-end only in real/integration runs.
"""
import os
import shutil
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


# The EBML element id that opens a Matroska/WebM Cluster. Everything before the first one is the
# initialisation segment: the header and track definitions a decoder needs before any media.
_CLUSTER_ID = bytes.fromhex("1F43B675")


def webm_init_segment(path: str) -> bytes:
    """The initialisation segment of a WebM file - its bytes up to the first Cluster.

    Only fragment 0 of a MediaRecorder stream carries this, which is what makes every later fragment
    undecodable on its own. Prepending these few hundred bytes to a pair of later fragments makes them
    decodable without also prepending the first chunk's audio.

    Returns b"" for a fragment that opens on a cluster - it has no init segment to give, and handing
    back the whole file instead would silently prepend a duplicate of somebody's audio.
    """
    with open(path, "rb") as f:
        data = f.read()
    i = data.find(_CLUSTER_ID)
    return data[:i] if i > 0 else b""


def join_bytes(input_paths: list[str]) -> str:
    """Byte-join the inputs, in order, into one temp file. Returns its path.

    For live-capture chunks, which are slices of a single `MediaRecorder` stream: only the first
    carries the WebM/EBML header, so the later ones are not independently openable and the ordinary
    concat path cannot be used on them (see `join_then_concat`). Joining is a pure byte operation -
    it never opens the media - which is exactly why it works here.
    """
    if not input_paths:
        raise ValueError("at least one input is required")
    fd, joined_path = tempfile.mkstemp(suffix=".webm")
    try:
        with os.fdopen(fd, "wb") as out:
            for path in input_paths:
                with open(path, "rb") as src:
                    shutil.copyfileobj(src, out)
    except Exception:
        os.remove(joined_path)
        raise
    return joined_path


def join_then_concat(input_paths: list[str]) -> tuple[str, int, int]:
    """Concatenate live-capture chunks into one WebM/Opus file. Returns (path, duration_ms, size).

    `build_concat_command` opens every input independently, so handing it the raw chunk list makes
    ffmpeg fail on the second one with "EBML header parsing failed" - only chunk 0 has the header.
    Byte-join first, then hand ffmpeg the single joined file, which it can open normally. Measured
    during the S0 spike: the joined file decodes complete and ffprobe reports a proper duration, so
    everything downstream is unchanged.
    """
    joined = join_bytes(input_paths)
    try:
        return concat([joined])
    finally:
        if os.path.exists(joined):
            os.remove(joined)


def concat(input_paths: list[str]) -> tuple[str, int, int]:
    """Concatenate the inputs into a temp WebM/Opus file. Returns (path, duration_ms, size_bytes)."""
    fd, output_path = tempfile.mkstemp(suffix=".webm")
    os.close(fd)
    subprocess.run(build_concat_command(input_paths, output_path), check=True, capture_output=True)
    return output_path, probe_duration_ms(output_path), os.path.getsize(output_path)
