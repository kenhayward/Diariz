"""Parse an Omi card file into the Opus frames it contains.

The on-card layout is documented in omi/firmware/docs/02-audio-formats.md (section 2.5)
and 07-devkit2-target.md (section 7.2). Both devices pack Opus frames into fixed-size
blocks as [len][frame][len][frame]...; the consumer device additionally prefixes each
block with a 4-byte big-endian UTC timestamp.

The one subtlety is the block-boundary artifact (05-findings.md F13). `write_to_storage()`
in the firmware flushes a block when `buffer_offset + packet_size > MAX_WRITE_SIZE - 1`,
and it writes the rejected frame's length byte at `buffer_offset` *before* flushing. That
inequality is exactly the condition for the declared length to overrun the block, so
"stop when the payload would run past the block end" is a complete guard rather than a
heuristic. Without it you get one corrupt frame per block - roughly ten a second.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import BinaryIO, Iterator, Union


@dataclass(frozen=True)
class Layout:
    """How one device writes its blocks."""

    name: str
    block_bytes: int      # packed payload per block
    header_bytes: int     # bytes before the payload (the CV1 timestamp)
    frame_ms: int         # duration of one Opus frame

    @property
    def packet_bytes(self) -> int:
        return self.header_bytes + self.block_bytes


#: Omi DevKit 2 (XIAO nRF52840). 10 ms frames, no timestamp.
DEVKIT = Layout(name="devkit", block_bytes=440, header_bytes=0, frame_ms=10)

#: Omi consumer CV1 (nRF5340). 20 ms frames, 4-byte big-endian UTC epoch per block.
#: Framing works; using the timestamps to date recordings is not implemented - see README.
CV1 = Layout(name="cv1", block_bytes=440, header_bytes=4, frame_ms=20)

LAYOUTS = {layout.name: layout for layout in (DEVKIT, CV1)}

Source = Union[bytes, bytearray, memoryview, BinaryIO]


def iter_frames(source: Source, layout: Layout) -> Iterator[bytes]:
    """Yield every Opus frame in `source`, in order.

    Accepts raw bytes or an open binary stream. Streaming matters: a card file can be
    gigabytes and must never be read into memory whole.
    """
    for block in _iter_blocks(source, layout):
        yield from _frames_in_block(block)


def _iter_blocks(source: Source, layout: Layout) -> Iterator[memoryview]:
    """Yield each complete block's payload, skipping any per-block header.

    A trailing partial block is discarded: the firmware only ever writes whole blocks, so
    a short tail means a truncated file, not data.
    """
    size = layout.packet_bytes
    start = layout.header_bytes

    if isinstance(source, (bytes, bytearray, memoryview)):
        data = memoryview(source)
        for offset in range(0, len(data) - size + 1, size):
            yield data[offset + start:offset + size]
        return

    while True:
        chunk = source.read(size)
        if len(chunk) < size:
            return
        yield memoryview(chunk)[start:]


def _frames_in_block(block: memoryview) -> Iterator[bytes]:
    size = len(block)
    offset = 0
    while offset < size:
        length = block[offset]
        if length == 0:
            return                       # padding: nothing more in this block
        if offset + 1 + length > size:
            return                       # the F13 artifact, or trailing stale data
        yield bytes(block[offset + 1:offset + 1 + length])
        offset += 1 + length
