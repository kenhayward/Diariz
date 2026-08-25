"""Framing: turning a DevKit a01.txt into a list of Opus frames.

The on-card layout (docs 07 section 7.2) is a concatenation of fixed-size blocks, each
packed as [len][frame][len][frame]... The awkward part is the block-boundary artifact
described in 05-findings.md F13: the firmware writes the *next* frame's length byte into
the block it is about to flush, so a block can end with a length byte that has no data
behind it. Every test below exists because getting this wrong corrupts one frame every
100 ms of audio.
"""

import pytest

from omi_sync.framing import DEVKIT, iter_frames


def block(*frames, size=440, tail=b""):
    """Build one packed block: [len][frame]... then `tail`, zero-padded to `size`."""
    out = bytearray()
    for f in frames:
        out.append(len(f))
        out += f
    out += tail
    assert len(out) <= size, "test block overflows"
    return bytes(out) + b"\x00" * (size - len(out))


def test_single_block_yields_its_frames():
    data = block(b"\x01\x02\x03", b"\x04\x05")
    assert list(iter_frames(data, DEVKIT)) == [b"\x01\x02\x03", b"\x04\x05"]


def test_zero_length_byte_ends_the_block():
    # Padding after the last frame is zero, which must terminate parsing.
    data = block(b"\xaa" * 10)
    assert list(iter_frames(data, DEVKIT)) == [b"\xaa" * 10]


def test_trailing_length_byte_that_overruns_is_ignored():
    """The F13 artifact, reproduced exactly as the firmware creates it.

    `write_to_storage()` flushes when `buffer_offset + packet_size > 439`, and it writes
    the rejected frame's length byte at `buffer_offset` first. So the stray byte's
    declared length ALWAYS overruns the block - that inequality is the trigger. Stopping
    on an overrun is therefore a complete guard, not a heuristic.
    """
    body = bytearray()
    body.append(150)
    body += b"\xaa" * 150       # next offset 151
    body.append(120)
    body += b"\xbb" * 120       # next offset 272
    body.append(100)
    body += b"\xcc" * 100       # next offset 373
    body.append(90)             # 373 + 1 + 90 = 464 > 440, so the firmware flushed here
    data = bytes(body) + b"\x77" * (440 - len(body))   # stale bytes, not zeros

    assert list(iter_frames(data, DEVKIT)) == [
        b"\xaa" * 150, b"\xbb" * 120, b"\xcc" * 100]


def test_length_byte_overrunning_the_block_end_is_ignored():
    # A length byte can only declare up to 255, so the overrun always comes from the
    # frame starting late in the block rather than from an absurd declared size.
    body = bytearray()
    body.append(255)
    body += b"\xbb" * 255       # next offset 256
    body.append(100)
    body += b"\xdd" * 100       # next offset 357
    body.append(255)            # 357 + 1 + 255 = 613 > 440
    data = bytes(body) + b"\xcc" * (440 - len(body))

    assert list(iter_frames(data, DEVKIT)) == [b"\xbb" * 255, b"\xdd" * 100]


def test_stale_bytes_are_never_reached_because_parsing_stops_at_the_artifact():
    # Blocks are reused without being cleared, so everything past the artifact byte is
    # last cycle's data. Parsing must stop before it, which the overrun guard guarantees.
    body = bytearray()
    body.append(200)
    body += b"\xde" * 200
    body.append(200)            # 201 + 1 + 200 = 402 <= 440, so this one DOES fit
    body += b"\xad" * 200       # ends at 402
    body.append(80)             # 402 + 1 + 80 = 483 > 440 -> stop here
    data = bytes(body) + b"\x77" * (440 - len(body))

    assert list(iter_frames(data, DEVKIT)) == [b"\xde" * 200, b"\xad" * 200]


def test_blocks_are_concatenated():
    data = block(b"\x01") + block(b"\x02", b"\x03")
    assert list(iter_frames(data, DEVKIT)) == [b"\x01", b"\x02", b"\x03"]


def test_partial_trailing_block_is_discarded():
    # Power was cut mid-block, or the file was truncated. Do not parse a partial block.
    data = block(b"\x01") + block(b"\x02")[:200]
    assert list(iter_frames(data, DEVKIT)) == [b"\x01"]


def test_frames_filling_the_block_exactly_are_kept():
    # 1 + 219 + 1 + 219 == 440 exactly, so there is no room for a terminator or an
    # artifact byte. Parsing must run to the last byte and stop cleanly.
    data = block(b"\x5a" * 219, b"\xa5" * 219)
    assert len(data) == 440
    assert list(iter_frames(data, DEVKIT)) == [b"\x5a" * 219, b"\xa5" * 219]


def test_empty_input_yields_nothing():
    assert list(iter_frames(b"", DEVKIT)) == []


def test_cv1_layout_skips_the_timestamp_header():
    # The consumer device prefixes each block with a 4-byte big-endian UTC timestamp
    # (docs 02 section 2.5). Same packing after that.
    from omi_sync.framing import CV1

    payload = block(b"\x11\x22", size=440)
    data = (1_700_000_123).to_bytes(4, "big") + payload
    assert list(iter_frames(data, CV1)) == [b"\x11\x22"]


def test_layout_frame_duration_differs_between_devices():
    from omi_sync.framing import CV1

    assert DEVKIT.frame_ms == 10      # 160 samples at 16 kHz
    assert CV1.frame_ms == 20         # 320 samples at 16 kHz


def test_iter_frames_accepts_a_stream():
    """The card file can be gigabytes, so parsing must not require it all in memory."""
    import io

    data = block(b"\x01") + block(b"\x02")
    assert list(iter_frames(io.BytesIO(data), DEVKIT)) == [b"\x01", b"\x02"]


@pytest.mark.parametrize("declared", [0, 255])
def test_boundary_length_values(declared):
    body = bytes([declared]) + b"\x99" * 255
    data = body + b"\x00" * (440 - len(body))
    got = list(iter_frames(data, DEVKIT))
    assert got == ([] if declared == 0 else [b"\x99" * 255])
