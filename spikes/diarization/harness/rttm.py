"""RTTM read/write and reference building. Pure - no database, audio or models - so it is unit-tested.

A reference speaker id is an anonymous `S01`, `S02`... assigned in order of first speech, so nothing that
leaves the data directory (RTTM files, reports) carries a person's name or profile id.
"""
from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class Turn:
    start: float  # seconds
    end: float
    speaker: str


def write_rttm(uri: str, turns: list[Turn]) -> str:
    return "".join(
        f"SPEAKER {uri} 1 {t.start:.3f} {t.end - t.start:.3f} <NA> <NA> {t.speaker} <NA> <NA>\n"
        for t in turns)


def read_rttm(text: str) -> dict[str, list[Turn]]:
    out: dict[str, list[Turn]] = {}
    for line in text.splitlines():
        parts = line.split()
        if len(parts) < 8 or parts[0] != "SPEAKER":
            continue
        start, dur = float(parts[3]), float(parts[4])
        out.setdefault(parts[1], []).append(Turn(start, round(start + dur, 3), parts[7]))
    return out


def merge_turns(turns: list[Turn], max_gap: float = 0.0) -> list[Turn]:
    """Join consecutive turns of the same speaker separated by at most `max_gap` seconds."""
    out: list[Turn] = []
    for t in sorted(turns, key=lambda t: (t.start, t.end)):
        if out and out[-1].speaker == t.speaker and t.start - out[-1].end <= max_gap:
            out[-1] = Turn(out[-1].start, max(out[-1].end, t.end), t.speaker)
        else:
            out.append(t)
    return out


def reference_from_segments(segments, people: dict[str, str]) -> list[Turn]:
    """Build a reference from a transcript's (start_ms, end_ms, label) segments.

    `people` maps a diarized label to the person it was named as. Labels named as the same person collapse
    into one reference speaker - that is the human correction of an over-split. Unnamed labels stay as
    their own speaker, and UNKNOWN / missing labels are dropped (no one is asserted to be speaking).
    """
    ids: dict[str, str] = {}
    turns = []
    for start_ms, end_ms, label in sorted(segments, key=lambda s: s[0]):
        if not label or label == "UNKNOWN":
            continue
        key = people.get(label, f"label:{label}")
        if key not in ids:
            ids[key] = f"S{len(ids) + 1:02d}"
        turns.append(Turn(start_ms / 1000, end_ms / 1000, ids[key]))
    return merge_turns(turns)


def clip_turns(turns: list[Turn], start: float, end: float) -> list[Turn]:
    """Keep the part of each turn inside [start, end), shifted so the excerpt starts at 0."""
    out = []
    for t in turns:
        a, b = max(t.start, start), min(t.end, end)
        if b > a:
            out.append(Turn(round(a - start, 3), round(b - start, 3), t.speaker))
    return out
