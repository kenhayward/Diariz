"""Cut one endless card file back into meetings.

The DevKit has no clock and no silence gating, so a01.txt runs unbroken across every
session since the card was cleared (docs 07 section 7.3). We split it host-side.

We do it *without decoding anything*. At 32 kbps VBR with DTX off, an Opus frame's byte
length tracks how much signal is in it: silence compresses to a handful of bytes, speech
does not. Using frame length as the energy proxy keeps this pure integer work, needs no
native Opus library, and lets the output be a byte-for-byte remux of the original frames.

Two mechanisms decide what counts as quiet:

* **Otsu's method** over the frame-length histogram picks the threshold automatically.
  It assumes a bimodal distribution (silence vs speech) and declines when it does not see
  one, rather than inventing a split that would shred the recording.
* **An absolute floor** applies regardless. Otsu cannot distinguish "all silence" from
  "all speech" - both are unimodal - but a frame of a few bytes is silence at this bitrate
  whatever the rest of the file looks like.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import List, Optional, Sequence

#: Frames at or below this many bytes are silence regardless of the computed threshold.
#: At 32 kbps a 10 ms frame averages ~40 bytes; near-silence in CELT VBR lands well under 16.
DEFAULT_MIN_ACTIVE_BYTES = 16


@dataclass(frozen=True)
class Session:
    """A half-open range of frame indices, [start, end)."""

    start: int
    end: int

    def duration_ms(self, frame_ms: int) -> int:
        return (self.end - self.start) * frame_ms


def otsu_threshold(histogram: Sequence[int], min_separation: float = 2.0) -> Optional[int]:
    """Pick a frame-length threshold separating silence from speech, or None.

    `histogram[n]` is the number of frames of exactly n bytes. Returns the largest frame
    length that should count as quiet.

    Returns None when the data is not convincingly bimodal - fewer than two distinct
    lengths, or two classes whose means differ by less than `min_separation`. Callers
    should fall back to the absolute floor rather than guessing.
    """
    total = sum(histogram)
    if total == 0:
        return None
    if sum(1 for count in histogram if count) < 2:
        return None

    weighted_total = sum(index * count for index, count in enumerate(histogram))

    below = 0
    weighted_below = 0
    variances: List[tuple] = []
    for threshold, count in enumerate(histogram):
        below += count
        weighted_below += threshold * count
        above = total - below
        if below == 0 or above == 0:
            continue
        mean_below = weighted_below / below
        mean_above = (weighted_total - weighted_below) / above
        variances.append((threshold, below * above * (mean_below - mean_above) ** 2))

    if not variances:
        return None

    best = max(value for _, value in variances)
    if best <= 0:
        return None

    # The optimum is usually a plateau spanning the empty gap between the two modes.
    # Taking its midpoint puts the threshold as far from both modes as possible.
    plateau = [threshold for threshold, value in variances if value >= best * (1 - 1e-12)]
    chosen = (plateau[0] + plateau[-1]) // 2

    below = sum(histogram[:chosen + 1])
    above = total - below
    if below == 0 or above == 0:
        return None
    mean_below = sum(i * histogram[i] for i in range(chosen + 1)) / below
    mean_above = (weighted_total - sum(i * histogram[i] for i in range(chosen + 1))) / above
    if mean_above < mean_below * min_separation:
        return None

    return chosen


def split_sessions(
    lengths: Sequence[int],
    *,
    threshold: Optional[int],
    frame_ms: int,
    gap_ms: int,
    min_session_ms: int,
    max_session_ms: Optional[int] = None,
    min_active_bytes: int = DEFAULT_MIN_ACTIVE_BYTES,
) -> List[Session]:
    """Group frames into sessions.

    A session runs from its first active frame to its last. A quiet run of at least
    `gap_ms` ends it; shorter pauses stay inside it, because people stop talking mid
    meeting. Sessions shorter than `min_session_ms` are dropped as noise, and any longer
    than `max_session_ms` are split so no single upload gets unwieldy.
    """
    quiet_at_or_below = max(threshold or 0, min_active_bytes)
    gap_frames = max(1, gap_ms // frame_ms)

    sessions: List[Session] = []
    start: Optional[int] = None
    end = 0
    quiet_run = 0

    for index, length in enumerate(lengths):
        if length > quiet_at_or_below:
            if start is None:
                start = index
            end = index + 1
            quiet_run = 0
        elif start is not None:
            quiet_run += 1
            if quiet_run >= gap_frames:
                sessions.append(Session(start, end))
                start = None
                quiet_run = 0

    if start is not None:
        sessions.append(Session(start, end))

    if max_session_ms:
        sessions = _enforce_max_length(sessions, frame_ms, max_session_ms)

    return [s for s in sessions if s.duration_ms(frame_ms) >= min_session_ms]


def _enforce_max_length(sessions, frame_ms: int, max_session_ms: int) -> List[Session]:
    max_frames = max(1, max_session_ms // frame_ms)
    out: List[Session] = []
    for session in sessions:
        cursor = session.start
        while cursor < session.end:
            stop = min(cursor + max_frames, session.end)
            out.append(Session(cursor, stop))
            cursor = stop
    return out
