"""End-to-end planning: card file in, uploadable sessions out.

`plan_sessions` is the whole tool minus I/O - it reads the card file and returns what
should be uploaded, so the CLI stays a thin shell and the interesting behaviour is
testable without a network or a Diariz instance.

Dating is the delicate part. The DevKit has no clock (docs 07 section 7.3), so the only
anchor is wall-clock knowledge the operator supplies: when they pulled the card. We treat
the file as one continuous stream ending at that instant and work backwards.
"""

from datetime import datetime, timedelta, timezone

import pytest

from omi_sync.framing import DEVKIT
from omi_sync.pipeline import plan_sessions


PULLED = datetime(2026, 8, 25, 17, 0, tzinfo=timezone.utc)


def block(*frames, size=440):
    out = bytearray()
    for f in frames:
        out.append(len(f))
        out += f
    assert len(out) <= size
    return bytes(out) + b"\x00" * (size - len(out))


def stream(pattern):
    """Build a card file from a list of (frame_size, count) pairs.

    Packs frames into 440-byte blocks the way the firmware does: fill until the next
    frame will not fit, then flush.
    """
    frames = []
    for size, count in pattern:
        frames += [bytes([size]) * size] * count

    blocks = bytearray()
    batch, used = [], 0
    for frame in frames:
        if used + 1 + len(frame) > 440:
            blocks += block(*batch)
            batch, used = [], 0
        batch.append(frame)
        used += 1 + len(frame)
    if batch:
        blocks += block(*batch)
    return bytes(blocks)


def write(tmp_path, data):
    p = tmp_path / "a01.txt"
    p.write_bytes(data)
    return p


def test_two_meetings_separated_by_a_long_gap(tmp_path):
    # 60 s of speech, 5 minutes of silence, 60 s of speech.
    path = write(tmp_path, stream([(80, 6000), (5, 30000), (80, 6000)]))

    got = plan_sessions(path, DEVKIT, ended_at=PULLED,
                        gap_ms=120_000, min_session_ms=10_000)

    assert len(got) == 2
    assert [s.duration_ms for s in got] == [60_000, 60_000]


def test_session_start_times_are_derived_backwards_from_the_card_pull(tmp_path):
    path = write(tmp_path, stream([(80, 6000), (5, 30000), (80, 6000)]))

    got = plan_sessions(path, DEVKIT, ended_at=PULLED,
                        gap_ms=120_000, min_session_ms=10_000)

    # Total stream is 420 s. The second session occupies the final 60 s.
    assert got[1].ended_at == PULLED
    assert got[1].started_at == PULLED - timedelta(seconds=60)
    # The first occupies seconds 0-60 of a 420 s stream.
    assert got[0].started_at == PULLED - timedelta(seconds=420)
    assert got[0].ended_at == PULLED - timedelta(seconds=360)


def test_each_session_carries_its_own_opus_frames(tmp_path):
    path = write(tmp_path, stream([(80, 6000), (5, 30000), (70, 6000)]))

    got = plan_sessions(path, DEVKIT, ended_at=PULLED,
                        gap_ms=120_000, min_session_ms=10_000)

    assert all(len(f) == 80 for f in got[0].frames)
    assert all(len(f) == 70 for f in got[1].frames)
    assert len(got[0].frames) == 6000


def test_frames_are_copied_verbatim_from_the_card(tmp_path):
    """The output must be a remux, not a re-encode: bytes in == bytes out."""
    path = write(tmp_path, stream([(80, 3000)]))

    got = plan_sessions(path, DEVKIT, ended_at=PULLED,
                        gap_ms=120_000, min_session_ms=1_000)

    assert got[0].frames[0] == bytes([80]) * 80


def test_titles_are_derived_from_the_estimated_start_time(tmp_path):
    path = write(tmp_path, stream([(80, 6000)]))

    got = plan_sessions(path, DEVKIT, ended_at=PULLED,
                        gap_ms=120_000, min_session_ms=1_000)

    assert "2026-08-25" in got[0].title
    assert "Omi" in got[0].title


def test_filenames_are_unique_and_sortable(tmp_path):
    path = write(tmp_path, stream([(80, 6000), (5, 30000), (80, 6000)]))

    got = plan_sessions(path, DEVKIT, ended_at=PULLED,
                        gap_ms=120_000, min_session_ms=10_000)

    names = [s.filename for s in got]
    assert len(set(names)) == 2
    assert names == sorted(names)
    assert all(n.endswith(".opus") for n in names)


def test_a_silent_card_produces_nothing(tmp_path):
    path = write(tmp_path, stream([(5, 60000)]))

    got = plan_sessions(path, DEVKIT, ended_at=PULLED,
                        gap_ms=120_000, min_session_ms=10_000)

    assert got == []


def test_an_empty_card_produces_nothing(tmp_path):
    path = write(tmp_path, b"")
    assert plan_sessions(path, DEVKIT, ended_at=PULLED,
                         gap_ms=120_000, min_session_ms=10_000) == []


def test_uniform_audio_is_not_split(tmp_path):
    """No bimodal distribution means Otsu declines; we must not shred the recording."""
    path = write(tmp_path, stream([(60, 12000)]))

    got = plan_sessions(path, DEVKIT, ended_at=PULLED,
                        gap_ms=120_000, min_session_ms=1_000)

    assert len(got) == 1
    assert got[0].duration_ms == 120_000


def test_explicit_threshold_overrides_the_automatic_one(tmp_path):
    path = write(tmp_path, stream([(80, 6000), (5, 30000), (80, 6000)]))

    got = plan_sessions(path, DEVKIT, ended_at=PULLED, threshold=200,
                        gap_ms=120_000, min_session_ms=1_000)

    # With everything below the threshold, the whole card is "quiet" -> nothing.
    assert got == []


def test_encoding_a_session_produces_an_ogg_opus_file(tmp_path):
    path = write(tmp_path, stream([(80, 6000)]))
    session = plan_sessions(path, DEVKIT, ended_at=PULLED,
                            gap_ms=120_000, min_session_ms=1_000)[0]

    blob = session.to_ogg_opus()

    assert blob.startswith(b"OggS")
    # Diariz sniffs magic bytes: AudioFormats.Detect returns "ogg" for OggS.
    assert blob[:4] == b"OggS"


def test_max_session_forces_a_split(tmp_path):
    path = write(tmp_path, stream([(80, 60000)]))     # 600 s continuous

    got = plan_sessions(path, DEVKIT, ended_at=PULLED,
                        gap_ms=120_000, min_session_ms=1_000,
                        max_session_ms=200_000)

    assert len(got) == 3
    assert sum(s.duration_ms for s in got) == 600_000
    # Consecutive chunks must abut in time, with no overlap or gap.
    assert got[0].ended_at == got[1].started_at
    assert got[1].ended_at == got[2].started_at


def test_ended_at_must_be_timezone_aware(tmp_path):
    path = write(tmp_path, stream([(80, 3000)]))
    with pytest.raises(ValueError):
        plan_sessions(path, DEVKIT, ended_at=datetime(2026, 8, 25, 17, 0),
                      gap_ms=120_000, min_session_ms=1_000)
