"""Cut a Whisper segment where the word-level speaker changes (issue #803).

`whisperx.assign_word_speakers` gives every segment the speaker it overlaps most, and every timed word
its own speaker. The transcript used to show only the first, so when someone spoke partway through a
segment their words appeared under the other person's name. On the public AMI test set that display
rule cost more accuracy than the choice of diarization model (8.6% of words under the wrong speaker,
against 6.4% when each run of words keeps its own).

Pure - raw whisperx segments in, raw whisperx segments out - so it runs ahead of `_shape_segments`
unchanged and is unit-tested without the models. Its conventions mirror the API's manual split
(`TranscriptSegmentSplit`): a cut lands between two words, because the pieces feed voiceprint training;
the outer edges keep the segment's own bounds; the gap at a cut belongs to neither side; and each
piece's text is cut out of the segment's own text rather than rebuilt from its words.

Three rules decide the runs:

- **Unassigned words** join a neighbour. A timed word with no diarization overlap (fill_nearest is off)
  joins the nearer timed, assigned word by gap, the previous one on a tie. A word the aligner could not
  time has no gap to measure, so it joins the previous word, or the next at the start of a segment.
- **Smoothing** absorbs only a stray run *inside* one speaker's turn: both neighbours the same speaker,
  and fewer than `min_words` words and shorter than `min_ms`. A short run at the edge of a segment is
  kept, because it is mostly a real interjection at a change of turn. Measured on AMI, absorbing those
  cost accuracy, and absorbing only the sandwiched ones cost none.
- **The label-set guard**: a word whose speaker won no whole segment in the same diarization pass is
  treated as unassigned. Otherwise a label that only ever won a few words would become a speaker of its
  own, with a voiceprint built from a second of audio and, on the live path, a new session speaker the
  stitcher could never merge back.

Anything that cannot be cut safely - no words, no word with a speaker, words that cannot be found in the
text - comes back exactly as it went in.
"""
from __future__ import annotations


def segment_labels(segments: list[dict]) -> set[str]:
    """The labels that won at least one whole segment: the only labels a split may use."""
    return {s["speaker"] for s in segments if s.get("speaker")}


def split_by_word_speaker(segments: list[dict], labels: set[str] | None = None,
                          min_words: int = 2, min_ms: float = 1000) -> list[dict]:
    """Split every segment at its word-speaker changes, in order.

    `labels` defaults to the labels the segments themselves won. The live path passes the labels of the
    whole decoded window, because it splits only the segments it keeps."""
    allowed = segment_labels(segments) if labels is None else labels
    out = []
    for segment in segments:
        out.extend(split_segment(segment, allowed, min_words, min_ms))
    return out


def _timed(word: dict) -> bool:
    return word.get("start") is not None and word.get("end") is not None


def _assigned_speakers(words: list[dict], allowed: set[str]) -> list[str | None]:
    """Each word's own speaker, or None when it is untimed, unassigned or not an allowed label."""
    return [w.get("speaker") if _timed(w) and w.get("speaker") in allowed else None for w in words]


def _fill(words: list[dict], speakers: list[str | None]) -> list[str]:
    """Give every unassigned word a neighbour's speaker. Needs at least one assigned word."""
    assigned = [i for i, s in enumerate(speakers) if s is not None]
    filled = list(speakers)

    # Timed words first, by the nearer assigned neighbour.
    for i, word in enumerate(words):
        if filled[i] is not None or not _timed(word):
            continue
        prev = max((j for j in assigned if j < i), default=None)
        nxt = min((j for j in assigned if j > i), default=None)
        if prev is None:
            filled[i] = speakers[nxt]
        elif nxt is None:
            filled[i] = speakers[prev]
        else:
            gap_prev = word["start"] - words[prev]["end"]
            gap_next = words[nxt]["start"] - word["end"]
            filled[i] = speakers[prev] if gap_prev <= gap_next else speakers[nxt]

    # Then untimed words: the previous word's speaker, or the first known one at the start.
    first = next(s for s in filled if s is not None)
    last = None
    for i in range(len(filled)):
        if filled[i] is None:
            filled[i] = last if last is not None else first
        last = filled[i]
    return filled


def _runs(speakers: list[str]) -> list[list]:
    """[speaker, first index, end index (exclusive)] for each stretch of one speaker."""
    runs: list[list] = []
    for i, s in enumerate(speakers):
        if runs and runs[-1][0] == s:
            runs[-1][2] = i + 1
        else:
            runs.append([s, i, i + 1])
    return runs


def _run_ms(words: list[dict], run: list) -> float:
    timed = [w for w in words[run[1]:run[2]] if _timed(w)]
    return (timed[-1]["end"] - timed[0]["start"]) * 1000 if timed else 0.0


def _smooth(words: list[dict], speakers: list[str], min_words: int, min_ms: float) -> list[str]:
    """Absorb stray runs sandwiched inside one speaker's turn, shortest first, to a fixed point."""
    speakers = list(speakers)
    while True:
        runs = _runs(speakers)
        stray = [
            k for k in range(1, len(runs) - 1)
            if runs[k - 1][0] == runs[k + 1][0]
            and runs[k][2] - runs[k][1] < min_words
            and _run_ms(words, runs[k]) < min_ms
        ]
        if not stray:
            return speakers
        k = min(stray, key=lambda k: (runs[k][2] - runs[k][1], _run_ms(words, runs[k])))
        for i in range(runs[k][1], runs[k][2]):
            speakers[i] = runs[k - 1][0]


def _word_offsets(text: str, words: list[dict]) -> list[int] | None:
    """Where each word starts in the text, walking a moving cursor so a repeated word is not matched at
    an earlier occurrence. None when any word is not found in order: the words no longer describe the
    text, and a cut would be a guess."""
    offsets, cursor = [], 0
    for word in words:
        token = (word.get("word") or "").strip()
        at = text.find(token, cursor) if token else cursor
        if at < 0:
            return None
        offsets.append(at)
        cursor = at + len(token)
    return offsets


def split_segment(segment: dict, labels: set[str], min_words: int = 2, min_ms: float = 1000) -> list[dict]:
    """One raw whisperx segment -> one or more, cut where the (smoothed) word speaker changes."""
    words = segment.get("words") or []
    speakers = _assigned_speakers(words, labels)
    if not any(s is not None for s in speakers):
        return [segment]

    final = _smooth(words, _fill(words, speakers), min_words, min_ms)
    runs = _runs(final)
    if len(runs) == 1:
        speaker = runs[0][0]
        return [segment if segment.get("speaker") == speaker else {**segment, "speaker": speaker}]

    text = segment.get("text") or ""
    offsets = _word_offsets(text, words)
    if offsets is None:
        return [segment]

    pieces = []
    for k, (speaker, first, end) in enumerate(runs):
        run_words = words[first:end]
        timed = [w for w in run_words if _timed(w)]
        text_from = 0 if k == 0 else offsets[first]
        text_to = len(text) if k == len(runs) - 1 else offsets[end]
        pieces.append({
            "start": segment["start"] if k == 0 else timed[0]["start"],
            "end": segment["end"] if k == len(runs) - 1 else timed[-1]["end"],
            "text": text[text_from:text_to].strip(),
            "speaker": speaker,
            "words": run_words,
        })
    return pieces
