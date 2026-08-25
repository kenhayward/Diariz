"""Validate our Ogg Opus output with a parser we did not write.

test_oggopus.py checks the container against the RFCs using our own demuxer, which proves
internal consistency but not correctness - a shared misreading of the spec would pass both
sides. These tests run the output through `mutagen`, an independent Ogg Opus implementation,
and are skipped when it is not installed.

They still do not prove the *payload* decodes: verifying that needs a real Opus decoder
(ffmpeg, or libopus via opuslib), neither of which is a dependency here. See the README's
"Verify before you trust it" section for the one-line ffmpeg check to run on real audio.
"""

import io

import pytest

from omi_sync.oggopus import mux

mutagen_opus = pytest.importorskip("mutagen.oggopus",
                                   reason="mutagen not installed: pip install mutagen")


FRAMES_10MS = [bytes([i % 251 + 1]) * (40 + i % 9) for i in range(3000)]


def parse(blob: bytes):
    return mutagen_opus.OggOpus(io.BytesIO(blob))


def test_mutagen_parses_our_container():
    assert parse(mux(FRAMES_10MS, frame_ms=10)) is not None


def test_mutagen_agrees_on_duration_for_10ms_frames():
    audio = parse(mux(FRAMES_10MS, frame_ms=10))
    assert audio.info.length == pytest.approx(len(FRAMES_10MS) * 0.010, abs=0.001)


def test_mutagen_agrees_on_duration_for_20ms_frames():
    audio = parse(mux(FRAMES_10MS, frame_ms=20))
    assert audio.info.length == pytest.approx(len(FRAMES_10MS) * 0.020, abs=0.001)


def test_mutagen_reads_the_channel_count_we_declared():
    assert parse(mux(FRAMES_10MS, frame_ms=10)).info.channels == 1


def test_mutagen_rejects_a_corrupted_page():
    """Confirms mutagen is really validating, so the tests above mean something."""
    blob = bytearray(mux(FRAMES_10MS, frame_ms=10))
    blob[3] = ord("X")                      # break the first capture pattern: OggS -> OggX
    with pytest.raises(Exception):
        parse(bytes(blob))


def test_a_single_page_stream_is_still_valid():
    audio = parse(mux([b"\x42" * 40] * 10, frame_ms=10))
    assert audio.info.length == pytest.approx(0.100, abs=0.001)
