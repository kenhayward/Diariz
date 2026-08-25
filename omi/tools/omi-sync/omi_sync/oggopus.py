"""Wrap raw Opus frames in an Ogg container - a remux, not a re-encode.

The frames on the card are already Opus, so putting them in an Ogg stream costs no quality
and needs no Opus library: about 15 MB per hour instead of WAV's 115 MB, with the original
encoder's bytes preserved exactly. Diariz accepts `ogg` by magic bytes
(src/Diariz.Api/Services/AudioFormats.cs) and its worker decodes it with ffmpeg.

References: RFC 3533 (Ogg container), RFC 7845 (Ogg Opus).

A note on pre-skip: RFC 7845 uses it to trim the encoder's lookahead. We did not encode
this audio - the device did, with settings we cannot query after the fact - so we declare
0 and trim nothing. The cost is a few milliseconds of encoder warm-up left at the start of
each session, which is inaudible and irrelevant to transcription.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass, field
from typing import List, Sequence

OPUS_HEAD_MAGIC = b"OpusHead"
OPUS_TAGS_MAGIC = b"OpusTags"
CAPTURE_PATTERN = b"OggS"

VENDOR = b"omi-sync (Diariz)"

#: Ogg allows at most 255 lacing values per page.
MAX_SEGMENTS_PER_PAGE = 255

#: Arbitrary but fixed, so identical input produces identical output. A serial only has
#: to be unique among concurrent logical streams in one file, and we write exactly one.
DEFAULT_SERIAL = 0x4F4D4953  # "OMIS"

_HEADER_TYPE_CONTINUED = 0x01
_HEADER_TYPE_BOS = 0x02
_HEADER_TYPE_EOS = 0x04


def _build_crc_table() -> List[int]:
    table = []
    for index in range(256):
        crc = index << 24
        for _ in range(8):
            crc = ((crc << 1) ^ 0x04C11DB7) & 0xFFFFFFFF if crc & 0x80000000 else (crc << 1) & 0xFFFFFFFF
        table.append(crc)
    return table


_CRC_TABLE = _build_crc_table()


def ogg_crc(data: bytes) -> int:
    """Ogg's CRC-32: polynomial 0x04c11db7, init 0, no reflection, no final xor.

    Note this is NOT the common zlib CRC-32, which reflects and inverts.
    """
    crc = 0
    for byte in data:
        crc = ((crc << 8) & 0xFFFFFFFF) ^ _CRC_TABLE[((crc >> 24) & 0xFF) ^ byte]
    return crc


def _opus_head(channels: int = 1, input_rate: int = 16000, pre_skip: int = 0) -> bytes:
    return struct.pack(
        "<8sBBHIhB",
        OPUS_HEAD_MAGIC,
        1,              # version
        channels,
        pre_skip,
        input_rate,     # informational only; Opus always decodes at 48 kHz internally
        0,              # output gain
        0,              # channel mapping family: mono/stereo
    )


def _opus_tags() -> bytes:
    return (
        OPUS_TAGS_MAGIC
        + struct.pack("<I", len(VENDOR))
        + VENDOR
        + struct.pack("<I", 0)          # no user comments
    )


def _lacing(packet_lengths: Sequence[int]) -> List[int]:
    """Ogg segment table: each packet is 255-byte segments plus a shorter terminator.

    A packet whose length is an exact multiple of 255 still needs an explicit 0-length
    terminator, or the demuxer glues it to the next packet.
    """
    segments: List[int] = []
    for length in packet_lengths:
        segments.extend([255] * (length // 255))
        segments.append(length % 255)
    return segments


def _segments_for(length: int) -> int:
    return length // 255 + 1


def _page(header_type: int, granule: int, serial: int, sequence: int,
          packets: Sequence[bytes]) -> bytes:
    segments = _lacing([len(p) for p in packets])
    if len(segments) > MAX_SEGMENTS_PER_PAGE:
        raise ValueError("page would exceed 255 lacing segments")

    header = (
        CAPTURE_PATTERN
        + bytes([0, header_type])
        + struct.pack("<q", granule)
        + struct.pack("<I", serial)
        + struct.pack("<I", sequence)
        + b"\x00\x00\x00\x00"                   # CRC placeholder
        + bytes([len(segments)])
        + bytes(segments)
    )
    body = b"".join(packets)
    crc = ogg_crc(header + body)
    return header[:22] + struct.pack("<I", crc) + header[26:] + body


def mux(frames: Sequence[bytes], *, frame_ms: int, serial: int = DEFAULT_SERIAL,
        channels: int = 1, input_rate: int = 16000) -> bytes:
    """Build a complete Ogg Opus file from `frames`, which are copied verbatim."""
    if not frames:
        raise ValueError("refusing to write an Ogg Opus file with no audio frames")

    # Opus granule positions are always in 48 kHz samples, whatever the input rate.
    samples_per_frame = frame_ms * 48

    pages = [
        _page(_HEADER_TYPE_BOS, 0, serial, 0, [_opus_head(channels, input_rate)]),
        _page(0, 0, serial, 1, [_opus_tags()]),
    ]

    sequence = 2
    batch: List[bytes] = []
    batch_segments = 0
    frames_done = 0

    def flush(last: bool) -> None:
        nonlocal sequence, batch, batch_segments
        pages.append(_page(_HEADER_TYPE_EOS if last else 0,
                           frames_done * samples_per_frame,
                           serial, sequence, batch))
        sequence += 1
        batch = []
        batch_segments = 0

    for index, frame in enumerate(frames):
        needed = _segments_for(len(frame))
        if batch and batch_segments + needed > MAX_SEGMENTS_PER_PAGE:
            flush(last=False)
        batch.append(frame)
        batch_segments += needed
        frames_done = index + 1
        # A single packet needing the whole table has to be flushed on its own.
        if batch_segments == MAX_SEGMENTS_PER_PAGE:
            flush(last=False)

    if batch:
        flush(last=True)
    else:
        # The final frame exactly filled a page; re-mark that page as end-of-stream.
        pages[-1] = _page(_HEADER_TYPE_EOS, frames_done * samples_per_frame,
                          serial, sequence - 1, _packets_of(pages[-1]))

    return b"".join(pages)


# --- demuxing (used by the tests, and useful for inspecting a file) -------------------

@dataclass
class Page:
    header_type: int
    granule: int
    serial: int
    sequence: int
    segment_count: int
    packets: List[bytes] = field(default_factory=list)
    crc_ok: bool = False


def _packets_of(page_bytes: bytes) -> List[bytes]:
    return demux_pages(page_bytes)[0].packets


def demux_pages(data: bytes) -> List[Page]:
    """Parse an Ogg stream back into pages and packets, verifying every checksum."""
    pages: List[Page] = []
    offset = 0
    while offset < len(data):
        if data[offset:offset + 4] != CAPTURE_PATTERN:
            raise ValueError(f"no Ogg capture pattern at offset {offset}")

        header_type = data[offset + 5]
        granule = struct.unpack_from("<q", data, offset + 6)[0]
        serial = struct.unpack_from("<I", data, offset + 14)[0]
        sequence = struct.unpack_from("<I", data, offset + 18)[0]
        stored_crc = struct.unpack_from("<I", data, offset + 22)[0]
        segment_count = data[offset + 26]
        table = data[offset + 27:offset + 27 + segment_count]

        body_start = offset + 27 + segment_count
        body_end = body_start + sum(table)
        raw = data[offset:body_end]
        recomputed = ogg_crc(raw[:22] + b"\x00\x00\x00\x00" + raw[26:])

        page = Page(header_type, granule, serial, sequence, segment_count,
                    crc_ok=recomputed == stored_crc)

        cursor = body_start
        packet = bytearray()
        for value in table:
            packet += data[cursor:cursor + value]
            cursor += value
            if value < 255:
                page.packets.append(bytes(packet))
                packet = bytearray()
        if packet:
            page.packets.append(bytes(packet))   # continues onto the next page

        pages.append(page)
        offset = body_end

    return pages
