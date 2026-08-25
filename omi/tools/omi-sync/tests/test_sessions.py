"""Session splitting.

The DevKit has no clock and no silence gating (docs 07 section 7.3), so a01.txt is one
unbroken stream across every session since the card was cleared. We have to cut it back
into meetings host-side.

We do it without decoding anything: at 32 kbps VBR with DTX off, an Opus frame's *byte
length* tracks how much signal is in it. Silence compresses to a handful of bytes, speech
does not. So the frame length is the energy proxy, the split is pure integer work, and the
output is a byte-for-byte remux of the original frames.
"""

import pytest

from omi_sync.sessions import Session, otsu_threshold, split_sessions


# --- threshold selection ------------------------------------------------------------

def test_otsu_separates_a_bimodal_distribution():
    # 1000 quiet frames around 12 bytes, 1000 speech frames around 70.
    hist = [0] * 256
    for n in range(10, 15):
        hist[n] = 200
    for n in range(65, 75):
        hist[n] = 100
    t = otsu_threshold(hist)
    assert t is not None
    assert 15 <= t <= 65, f"threshold {t} does not sit between the two modes"


def test_otsu_returns_none_for_a_unimodal_distribution():
    # All frames roughly the same size: either all speech or all silence. There is no
    # meaningful split, and inventing one would shred the recording.
    hist = [0] * 256
    for n in range(40, 45):
        hist[n] = 500
    assert otsu_threshold(hist) is None


def test_otsu_returns_none_for_an_empty_histogram():
    assert otsu_threshold([0] * 256) is None


def test_otsu_returns_none_for_a_single_populated_bucket():
    hist = [0] * 256
    hist[42] = 1000
    assert otsu_threshold(hist) is None


# --- splitting ----------------------------------------------------------------------

def quiet(n):
    return [5] * n


def loud(n):
    return [80] * n


def test_a_long_quiet_run_splits_two_sessions():
    lengths = loud(6000) + quiet(30000) + loud(6000)     # 60 s, 300 s gap, 60 s
    got = split_sessions(lengths, threshold=40, frame_ms=10,
                         gap_ms=120_000, min_session_ms=10_000)
    assert len(got) == 2
    assert got[0] == Session(start=0, end=6000)
    assert got[1] == Session(start=36000, end=42000)


def test_a_short_quiet_run_does_not_split():
    # A pause for breath inside one meeting must not end the session.
    lengths = loud(6000) + quiet(300) + loud(6000)       # 3 s gap
    got = split_sessions(lengths, threshold=40, frame_ms=10,
                         gap_ms=120_000, min_session_ms=10_000)
    assert len(got) == 1
    assert got[0] == Session(start=0, end=12300)


def test_leading_and_trailing_quiet_is_trimmed():
    lengths = quiet(30000) + loud(6000) + quiet(30000)
    got = split_sessions(lengths, threshold=40, frame_ms=10,
                         gap_ms=120_000, min_session_ms=10_000)
    assert got == [Session(start=30000, end=36000)]


def test_sessions_shorter_than_the_minimum_are_dropped():
    # A door slam between two meetings is not a meeting.
    lengths = loud(6000) + quiet(30000) + loud(50) + quiet(30000) + loud(6000)
    got = split_sessions(lengths, threshold=40, frame_ms=10,
                         gap_ms=120_000, min_session_ms=10_000)
    assert len(got) == 2, "the 0.5 s blip should not survive"


def test_a_session_longer_than_the_maximum_is_force_split():
    # Keeps any single upload under the platform's size cap.
    lengths = loud(100_000)                              # 1000 s
    got = split_sessions(lengths, threshold=40, frame_ms=10,
                         gap_ms=120_000, min_session_ms=1_000,
                         max_session_ms=300_000)         # 300 s
    assert [s.end - s.start for s in got] == [30000, 30000, 30000, 10000]
    assert got[0].start == 0 and got[-1].end == 100_000


def test_all_quiet_input_produces_no_sessions():
    assert split_sessions(quiet(100_000), threshold=40, frame_ms=10,
                          gap_ms=120_000, min_session_ms=10_000) == []


def test_all_loud_input_is_one_session():
    got = split_sessions(loud(6000), threshold=40, frame_ms=10,
                         gap_ms=120_000, min_session_ms=10_000)
    assert got == [Session(start=0, end=6000)]


def test_empty_input_produces_no_sessions():
    assert split_sessions([], threshold=40, frame_ms=10,
                          gap_ms=120_000, min_session_ms=10_000) == []


def test_threshold_none_falls_back_to_the_absolute_floor():
    # Otsu declined (unimodal). We still apply an absolute floor, because a frame of a
    # handful of bytes is silence at 32 kbps whatever the rest of the file looks like.
    # Here the 30 s quiet stretch is shorter than the gap, so it stays inside one session.
    lengths = loud(3000) + quiet(3000) + loud(3000)
    got = split_sessions(lengths, threshold=None, frame_ms=10,
                         gap_ms=120_000, min_session_ms=1_000)
    assert got == [Session(start=0, end=9000)]


def test_a_uniformly_silent_card_yields_nothing_even_without_a_threshold():
    # The case the floor exists for: Otsu cannot tell "all silence" from "all speech",
    # but 5-byte frames are unambiguously silence. Without the floor this would upload
    # ten minutes of nothing.
    assert split_sessions(quiet(60_000), threshold=None, frame_ms=10,
                          gap_ms=120_000, min_session_ms=1_000) == []


def test_a_uniformly_loud_card_is_one_session_without_a_threshold():
    got = split_sessions(loud(60_000), threshold=None, frame_ms=10,
                         gap_ms=120_000, min_session_ms=1_000)
    assert got == [Session(start=0, end=60_000)]


def test_an_explicit_threshold_below_the_floor_does_not_weaken_it():
    # Asking for threshold=0 must not turn silence into audio.
    assert split_sessions(quiet(60_000), threshold=0, frame_ms=10,
                          gap_ms=120_000, min_session_ms=1_000) == []


def test_the_floor_can_be_lowered_for_unusual_encoder_settings():
    got = split_sessions(quiet(6000), threshold=None, frame_ms=10,
                         gap_ms=120_000, min_session_ms=1_000, min_active_bytes=1)
    assert got == [Session(start=0, end=6000)]


def test_session_duration_helper():
    assert Session(start=0, end=6000).duration_ms(frame_ms=10) == 60_000
    assert Session(start=100, end=400).duration_ms(frame_ms=20) == 6_000


def test_gap_exactly_at_the_threshold_splits():
    lengths = loud(1000) + quiet(12000) + loud(1000)     # gap == 120 s exactly
    got = split_sessions(lengths, threshold=40, frame_ms=10,
                         gap_ms=120_000, min_session_ms=1_000)
    assert len(got) == 2


@pytest.mark.parametrize("frame_ms", [10, 20])
def test_gap_threshold_respects_frame_duration(frame_ms):
    # The same gap in milliseconds is a different number of frames per device.
    gap_frames = 120_000 // frame_ms
    lengths = loud(1000) + quiet(gap_frames) + loud(1000)
    got = split_sessions(lengths, threshold=40, frame_ms=frame_ms,
                         gap_ms=120_000, min_session_ms=1_000)
    assert len(got) == 2
