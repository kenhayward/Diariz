"""Card file in, uploadable sessions out.

Two passes over the file. The first builds a histogram of frame lengths (constant memory,
however big the card is) and counts frames; from that we pick the silence threshold and
work out where the session boundaries fall. The second walks the frames again and emits
each session as it completes, so only one session is ever held in memory.

Dating is the part to be honest about. The DevKit has no clock of any kind, so the only
anchor is knowledge the operator supplies: when they pulled the card. We treat the file as
one continuous stream ending at that instant and work backwards. That is exactly right if
the device ran continuously up to the moment it was stopped, and it drifts by however long
the device spent powered off mid-file. There is no way to do better without a firmware
change - see docs 07 section 7.6, D-fix-1.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta
from pathlib import Path
from typing import Iterator, List, Optional

from .framing import Layout, iter_frames
from .oggopus import mux
from .sessions import DEFAULT_MIN_ACTIVE_BYTES, Session, otsu_threshold, split_sessions


@dataclass
class PlannedSession:
    """One recording, ready to encode and upload."""

    index: int
    frames: List[bytes]
    started_at: datetime
    ended_at: datetime
    duration_ms: int
    frame_ms: int

    @property
    def title(self) -> str:
        return f"Omi {self.started_at:%Y-%m-%d %H:%M}"

    @property
    def filename(self) -> str:
        return f"omi-{self.started_at:%Y%m%d-%H%M%S}-{self.index:02d}.opus"

    def to_ogg_opus(self) -> bytes:
        return mux(self.frames, frame_ms=self.frame_ms)


@dataclass
class Plan:
    """What a scan of the card found, before any session is materialised."""

    total_frames: int
    threshold: Optional[int]
    sessions: List[Session]
    histogram: List[int]

    @property
    def total_ms_at(self):
        return lambda frame_ms: self.total_frames * frame_ms


def scan(path: Path, layout: Layout, *, threshold: Optional[int] = None,
         gap_ms: int, min_session_ms: int, max_session_ms: Optional[int] = None,
         min_active_bytes: int = DEFAULT_MIN_ACTIVE_BYTES) -> Plan:
    """Pass one: measure the card and decide where the sessions are."""
    histogram = [0] * 256
    total_frames = 0
    lengths: List[int] = []

    with open(path, "rb") as handle:
        for frame in iter_frames(handle, layout):
            length = len(frame)
            lengths.append(length)
            if length < 256:
                histogram[length] += 1
            total_frames += 1

    chosen = threshold if threshold is not None else otsu_threshold(histogram)

    sessions = split_sessions(
        lengths,
        threshold=chosen,
        frame_ms=layout.frame_ms,
        gap_ms=gap_ms,
        min_session_ms=min_session_ms,
        max_session_ms=max_session_ms,
        min_active_bytes=min_active_bytes,
    )
    return Plan(total_frames=total_frames, threshold=chosen,
                sessions=sessions, histogram=histogram)


def iter_sessions(path: Path, layout: Layout, *, ended_at: datetime,
                  threshold: Optional[int] = None, gap_ms: int, min_session_ms: int,
                  max_session_ms: Optional[int] = None,
                  min_active_bytes: int = DEFAULT_MIN_ACTIVE_BYTES,
                  plan: Optional[Plan] = None) -> Iterator[PlannedSession]:
    """Pass two: yield each session, with its frames, as it completes.

    Yielding rather than returning a list keeps memory bounded by the largest single
    session rather than by the whole card.
    """
    if ended_at.tzinfo is None:
        raise ValueError("ended_at must be timezone-aware: pass an explicit UTC or local offset")

    if plan is None:
        plan = scan(path, layout, threshold=threshold, gap_ms=gap_ms,
                    min_session_ms=min_session_ms, max_session_ms=max_session_ms,
                    min_active_bytes=min_active_bytes)

    frame_ms = layout.frame_ms
    total = plan.total_frames

    def at(frame_index: int) -> datetime:
        return ended_at - timedelta(milliseconds=(total - frame_index) * frame_ms)

    pending = iter(enumerate(plan.sessions))
    current = next(pending, None)
    if current is None:
        return

    buffer: List[bytes] = []
    with open(path, "rb") as handle:
        for index, frame in enumerate(iter_frames(handle, layout)):
            if current is None:
                break
            position, session = current
            if session.start <= index < session.end:
                buffer.append(frame)
            if index == session.end - 1:
                yield PlannedSession(
                    index=position,
                    frames=buffer,
                    started_at=at(session.start),
                    ended_at=at(session.end),
                    duration_ms=session.duration_ms(frame_ms),
                    frame_ms=frame_ms,
                )
                buffer = []
                current = next(pending, None)


def plan_sessions(path: Path, layout: Layout, *, ended_at: datetime,
                  threshold: Optional[int] = None, gap_ms: int, min_session_ms: int,
                  max_session_ms: Optional[int] = None,
                  min_active_bytes: int = DEFAULT_MIN_ACTIVE_BYTES) -> List[PlannedSession]:
    """Eager form of `iter_sessions`, for tests and small cards."""
    return list(iter_sessions(path, layout, ended_at=ended_at, threshold=threshold,
                              gap_ms=gap_ms, min_session_ms=min_session_ms,
                              max_session_ms=max_session_ms,
                              min_active_bytes=min_active_bytes))
