"""Word-level speaker error: the share of transcript words a user would see under the wrong speaker.

DER scores time, but a user reads words. Production turns diarization into words the way
`whisperx.assign_word_speakers` does: a segment takes the speaker with the largest summed overlap,
with no overlap meaning no speaker (fill_nearest is off). The segment is what the transcript shows,
so every word in it inherits that speaker - mode="segment", the headline number. mode="word" assigns
each word on its own, which is what a word-snapped segment split would give.

The reference speaker of a word is the reference turn it overlaps most. Words with no reference
speaker are skipped: the reference says nobody is talking, so there is nothing to get wrong.
"""
from __future__ import annotations

from collections import Counter

from scipy.optimize import linear_sum_assignment

from rttm import Turn


def dominant_speaker(start: float, end: float, turns: list[Turn]) -> str | None:
    overlap: Counter = Counter()
    for t in turns:
        o = min(end, t.end) - max(start, t.start)
        if o > 0:
            overlap[t.speaker] += o
    return max(overlap.items(), key=lambda kv: kv[1])[0] if overlap else None


def label_words(segments: list[dict], turns: list[Turn], mode: str = "segment") -> list[str | None]:
    out = []
    for seg in segments:
        seg_speaker = dominant_speaker(seg["s"], seg["e"], turns) if mode == "segment" else None
        for w in seg["words"]:
            out.append(seg_speaker if mode == "segment" else dominant_speaker(w["s"], w["e"], turns))
    return out


def word_speaker_error(ref: list[str | None], hyp: list[str | None]) -> dict:
    """Errors under the one-to-one hyp->ref speaker mapping that matches the most words."""
    pairs = [(r, h) for r, h in zip(ref, hyp) if r is not None]
    refs = sorted({r for r, _ in pairs})
    hyps = sorted({h for _, h in pairs if h is not None})
    counts = Counter(pairs)
    matched = 0
    if refs and hyps:
        cost = [[-counts[(r, h)] for h in hyps] for r in refs]
        rows, cols = linear_sum_assignment(cost)
        matched = -sum(cost[i][j] for i, j in zip(rows, cols))
    return {"words": len(pairs), "errors": len(pairs) - matched,
            "unlabelled": sum(1 for _, h in pairs if h is None)}
