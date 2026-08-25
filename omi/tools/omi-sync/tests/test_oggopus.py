"""Ogg Opus muxing.

The frames on the card are already Opus. Wrapping them in an Ogg container is a remux,
not a re-encode: no decoder, no quality loss, no native dependency, and about 15 MB per
hour instead of WAV's 115 MB. Diariz accepts `ogg` by magic bytes
(src/Diariz.Api/Services/AudioFormats.cs), and the worker's ffmpeg decodes it.

Correctness matters more than usual here because a subtly malformed container fails late,
inside the transcription worker, long after the card has been wiped. These tests check the
output against RFC 7845 (Ogg Opus) and RFC 3533 (Ogg) structurally, and cross-check the
CRC against an independently written bitwise implementation.
"""

import struct

import pytest

from omi_sync.oggopus import (
    OPUS_HEAD_MAGIC,
    OPUS_TAGS_MAGIC,
    demux_pages,
    mux,
    ogg_crc,
)


def crc_bitwise(data: bytes) -> int:
    """Reference Ogg CRC: poly 0x04c11db7, init 0, no reflection, no final xor.

    Deliberately a different algorithm from the implementation (which uses a lookup
    table), so agreement is evidence rather than tautology.
    """
    crc = 0
    for byte in data:
        crc ^= byte << 24
        for _ in range(8):
            crc = ((crc << 1) ^ 0x04C11DB7) & 0xFFFFFFFF if crc & 0x80000000 else (crc << 1) & 0xFFFFFFFF
    return crc


# --- CRC ----------------------------------------------------------------------------

def test_crc_of_empty_input_is_zero():
    assert ogg_crc(b"") == 0


@pytest.mark.parametrize("data", [
    b"\x00",
    b"OggS",
    bytes(range(256)),
    b"the quick brown fox" * 7,
])
def test_crc_matches_an_independent_bitwise_implementation(data):
    assert ogg_crc(data) == crc_bitwise(data)


# --- container structure ------------------------------------------------------------

FRAMES = [bytes([i % 251 + 1]) * (40 + i % 7) for i in range(500)]


def test_output_begins_with_the_ogg_capture_pattern():
    assert mux(FRAMES, frame_ms=10).startswith(b"OggS")


def test_first_page_is_bos_and_carries_only_opushead():
    pages = demux_pages(mux(FRAMES, frame_ms=10))
    assert pages[0].header_type == 0x02, "first page must set the beginning-of-stream flag"
    assert len(pages[0].packets) == 1
    assert pages[0].packets[0].startswith(OPUS_HEAD_MAGIC)
    assert pages[0].granule == 0


def test_opushead_fields_describe_our_encoder():
    head = demux_pages(mux(FRAMES, frame_ms=10))[0].packets[0]
    assert len(head) == 19
    magic, version, channels, pre_skip, rate, gain, mapping = struct.unpack(
        "<8sBBHIhB", head)
    assert magic == OPUS_HEAD_MAGIC
    assert version == 1
    assert channels == 1                 # the DevKit mixes to mono on-device
    assert pre_skip == 0                 # we did not encode, so we trim nothing
    assert rate == 16000                 # informational: the original input rate
    assert gain == 0
    assert mapping == 0


def test_second_page_carries_opustags():
    pages = demux_pages(mux(FRAMES, frame_ms=10))
    assert pages[1].packets[0].startswith(OPUS_TAGS_MAGIC)
    assert pages[1].granule == 0
    assert pages[1].header_type == 0x00


def test_opustags_is_well_formed():
    tags = demux_pages(mux(FRAMES, frame_ms=10))[1].packets[0]
    vendor_len = struct.unpack_from("<I", tags, 8)[0]
    vendor = tags[12:12 + vendor_len]
    comment_count = struct.unpack_from("<I", tags, 12 + vendor_len)[0]
    assert b"omi-sync" in vendor
    assert comment_count == 0
    assert len(tags) == 12 + vendor_len + 4


def test_every_frame_survives_the_round_trip():
    pages = demux_pages(mux(FRAMES, frame_ms=10))
    audio = [p for page in pages[2:] for p in page.packets]
    assert audio == FRAMES


def test_page_sequence_numbers_are_contiguous_from_zero():
    pages = demux_pages(mux(FRAMES, frame_ms=10))
    assert [p.sequence for p in pages] == list(range(len(pages)))


def test_last_page_sets_the_end_of_stream_flag():
    pages = demux_pages(mux(FRAMES, frame_ms=10))
    assert pages[-1].header_type & 0x04
    assert not any(p.header_type & 0x04 for p in pages[:-1])


def test_granule_positions_count_48khz_samples():
    # Opus granule positions are always in 48 kHz units regardless of the encoder's
    # input rate, so a 10 ms frame advances the clock by 480.
    pages = demux_pages(mux(FRAMES, frame_ms=10))
    assert pages[-1].granule == len(FRAMES) * 480

    pages20 = demux_pages(mux(FRAMES, frame_ms=20))
    assert pages20[-1].granule == len(FRAMES) * 960


def test_granule_positions_increase_monotonically():
    granules = [p.granule for p in demux_pages(mux(FRAMES, frame_ms=10))[2:]]
    assert granules == sorted(granules)
    assert all(b > a for a, b in zip(granules, granules[1:]))


def test_all_pages_share_one_serial_number():
    pages = demux_pages(mux(FRAMES, frame_ms=10))
    assert len({p.serial for p in pages}) == 1


def test_every_page_checksum_validates():
    # demux_pages recomputes and compares; this asserts it actually did.
    for page in demux_pages(mux(FRAMES, frame_ms=10)):
        assert page.crc_ok


def test_no_page_exceeds_255_segments():
    for page in demux_pages(mux(FRAMES, frame_ms=10)):
        assert page.segment_count <= 255


# --- lacing edge cases ---------------------------------------------------------------

def test_packet_of_exactly_255_bytes_gets_a_terminating_zero_segment():
    # 255 is the lacing boundary: it needs a 255 segment AND an explicit 0, or the
    # demuxer will glue it to the following packet.
    frames = [b"\xa5" * 255, b"\x5a" * 10]
    pages = demux_pages(mux(frames, frame_ms=10))
    assert [p for page in pages[2:] for p in page.packets] == frames


def test_packet_longer_than_255_bytes_is_laced_correctly():
    frames = [b"\xa5" * 300]
    pages = demux_pages(mux(frames, frame_ms=10))
    assert [p for page in pages[2:] for p in page.packets] == frames


def test_single_frame_stream():
    frames = [b"\x01" * 40]
    pages = demux_pages(mux(frames, frame_ms=10))
    assert [p for page in pages[2:] for p in page.packets] == frames
    assert pages[-1].header_type & 0x04


def test_empty_frame_list_is_rejected():
    # An empty recording is a bug upstream, not a valid file to upload.
    with pytest.raises(ValueError):
        mux([], frame_ms=10)


def test_a_long_stream_spans_many_pages():
    frames = [b"\x33" * 40] * 3000
    pages = demux_pages(mux(frames, frame_ms=10))
    assert len(pages) > 10
    assert [p for page in pages[2:] for p in page.packets] == frames


def test_serial_number_can_be_pinned_for_reproducible_output():
    a = mux(FRAMES, frame_ms=10, serial=12345)
    b = mux(FRAMES, frame_ms=10, serial=12345)
    assert a == b
    assert demux_pages(a)[0].serial == 12345
