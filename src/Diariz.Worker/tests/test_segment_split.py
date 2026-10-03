"""Tests for segment_split: cutting a Whisper segment where the word-level speaker changes (issue #803).

Inputs are raw whisperx segments as `assign_word_speakers` leaves them: seconds, a segment `speaker`,
and per-word `word`/`start`/`end`/`speaker`. A word the aligner could not time has no `start`, and a word
with no diarization overlap has no `speaker` (fill_nearest is off).
"""
import segment_split


def w(text, start=None, end=None, speaker=None):
    word = {"word": text}
    if start is not None:
        word["start"], word["end"] = start, end
    if speaker is not None:
        word["speaker"] = speaker
    return word


def seg(text, start, end, speaker, words):
    return {"text": text, "start": start, "end": end, "speaker": speaker, "words": words}


def split(segment, labels=("A", "B"), **kw):
    return segment_split.split_segment(segment, set(labels), **kw)


# ---- cutting ----

def test_cuts_a_segment_where_the_word_speaker_changes():
    s = seg(" Are you ready? Yes I am.", 0.0, 3.0, "A", [
        w("Are", 0.0, 0.3, "A"), w("you", 0.4, 0.6, "A"), w("ready?", 0.7, 1.1, "A"),
        w("Yes", 1.5, 1.8, "B"), w("I", 1.9, 2.0, "B"), w("am.", 2.1, 2.9, "B"),
    ])
    out = split(s)
    assert [(p["speaker"], p["text"]) for p in out] == [("A", "Are you ready?"), ("B", "Yes I am.")]
    assert [x["word"] for x in out[0]["words"]] == ["Are", "you", "ready?"]
    assert [x["word"] for x in out[1]["words"]] == ["Yes", "I", "am."]


def test_keeps_the_segments_own_outer_bounds_and_snaps_interior_cuts_to_words():
    """Mirrors TranscriptSegmentSplit in the API: the outer edges are the segment's, an interior cut is
    the last word's end on the left and the first word's start on the right, and the gap between them
    belongs to neither side - it is where the other voice starts."""
    s = seg("one two three four", 0.0, 5.0, "A", [
        w("one", 0.2, 0.5, "A"), w("two", 0.6, 1.0, "A"),
        w("three", 2.0, 2.5, "B"), w("four", 2.6, 4.8, "B"),
    ])
    out = split(s)
    assert [(p["start"], p["end"]) for p in out] == [(0.0, 1.0), (2.0, 5.0)]


def test_cuts_text_out_of_the_original_including_untimed_words():
    """A digit the aligner could not time stays in its run's text; it has no timing, so it is not one of
    that run's words (the shaper drops it anyway). The text is cut out of the original rather than
    rebuilt by joining words, so spacing and punctuation survive."""
    s = seg("We paid 20 dollars, fine.  Sure.", 0.0, 4.0, "A", [
        w("We", 0.0, 0.2, "A"), w("paid", 0.3, 0.6, "A"), w("20"), w("dollars,", 1.0, 1.4, "A"),
        w("fine.", 1.5, 1.9, "A"), w("Sure.", 2.5, 3.0, "B"),
    ])
    out = split(s)
    assert [p["text"] for p in out] == ["We paid 20 dollars, fine.", "Sure."]
    assert "20" in [x["word"] for x in out[0]["words"]]


def test_does_not_split_when_a_word_cannot_be_found_in_the_text():
    """The words no longer describe the text, so any cut would be a guess. Keep today's segment."""
    s = seg("hello there", 0.0, 2.0, "A", [w("hello", 0.0, 0.5, "A"), w("world", 1.0, 1.5, "B")])
    assert split(s) == [s]


def test_runs_come_out_in_time_order():
    """The API numbers segments in payload order, so the pieces must be in the order they were said."""
    s = seg("a b c d e f", 0.0, 6.0, "A", [
        w("a", 0.0, 0.5, "A"), w("b", 0.6, 1.0, "A"), w("c", 1.1, 2.5, "B"),
        w("d", 2.6, 3.0, "B"), w("e", 3.1, 4.5, "A"), w("f", 4.6, 5.0, "A"),
    ])
    out = split(s)
    assert [p["speaker"] for p in out] == ["A", "B", "A"]
    assert [p["start"] for p in out] == sorted(p["start"] for p in out)


# ---- fallbacks: today's behaviour unchanged ----

def test_leaves_a_segment_without_words_unchanged():
    """A language with no align model carries no words at all."""
    s = {"text": "bore da", "start": 0.0, "end": 1.0, "speaker": "A"}
    assert split(s) == [s]


def test_leaves_a_segment_unchanged_when_no_word_has_a_speaker():
    s = seg("a b", 0.0, 1.0, "A", [w("a", 0.0, 0.4), w("b", 0.5, 0.9)])
    assert split(s) == [s]


def test_leaves_a_one_speaker_segment_unchanged():
    s = seg("a b c", 0.0, 2.0, "A", [w("a", 0.0, 0.4, "A"), w("b", 0.5, 0.9, "A"), w("c", 1.0, 1.9, "A")])
    assert split(s) == [s]


def test_a_segment_whose_words_all_belong_to_another_speaker_takes_that_speaker():
    """The segment-level majority is over the whole span, gaps included; the words are the evidence of
    who was actually talking. Bounds and text are untouched."""
    s = seg("a b c", 0.0, 2.0, "A", [w("a", 0.0, 0.4, "B"), w("b", 0.5, 0.9, "B"), w("c", 1.0, 1.9, "B")])
    assert split(s) == [{**s, "speaker": "B"}]


# ---- unassigned words ----

def test_an_unassigned_timed_word_joins_the_nearer_neighbour_by_gap():
    s = seg("a b c d e f", 0.0, 6.0, "A", [
        w("a", 0.0, 0.5, "A"), w("b", 0.6, 1.0, "A"), w("c", 1.1, 1.5, "A"),
        w("d", 2.9, 3.0),  # 1.4 s after "c", 0.1 s before "e"
        w("e", 3.1, 4.0, "B"), w("f", 4.1, 5.0, "B"),
    ])
    out = split(s)
    assert [p["text"] for p in out] == ["a b c", "d e f"]


def test_an_unassigned_word_equidistant_from_both_sides_joins_the_previous_run():
    s = seg("a b c d e f", 0.0, 6.0, "A", [
        w("a", 0.0, 0.5, "A"), w("b", 0.6, 1.0, "A"), w("c", 1.1, 1.5, "A"),
        w("d", 2.0, 2.5),  # 0.5 s either side
        w("e", 3.0, 4.0, "B"), w("f", 4.1, 5.0, "B"),
    ])
    assert [p["text"] for p in split(s)] == ["a b c d", "e f"]


def test_an_untimed_word_joins_the_previous_run_or_the_next_at_the_start():
    s = seg("1 a b 2 c d", 0.0, 6.0, "A", [
        w("1"), w("a", 0.0, 0.5, "A"), w("b", 0.6, 1.5, "A"),
        w("2"), w("c", 2.0, 3.0, "B"), w("d", 3.1, 4.0, "B"),
    ])
    assert [p["text"] for p in split(s)] == ["1 a b 2", "c d"]


# ---- smoothing: only a stray run inside one speaker's turn is absorbed ----

def test_a_single_stray_word_inside_one_speakers_turn_is_absorbed():
    s = seg("a b c d e", 0.0, 5.0, "A", [
        w("a", 0.0, 0.5, "A"), w("b", 0.6, 1.0, "A"), w("c", 1.1, 1.4, "B"),
        w("d", 1.5, 2.0, "A"), w("e", 2.1, 3.0, "A"),
    ])
    assert split(s) == [s]


def test_a_short_run_at_the_edge_of_a_segment_is_kept():
    """Mostly a real interjection at a change of turn - "Yeah." - so absorbing it would hide who said it."""
    s = seg("Yeah. So the plan", 0.0, 3.0, "B", [
        w("Yeah.", 0.0, 0.3, "A"), w("So", 0.8, 1.0, "B"), w("the", 1.1, 1.3, "B"), w("plan", 1.4, 2.0, "B"),
    ])
    out = split(s)
    assert [(p["speaker"], p["text"]) for p in out] == [("A", "Yeah."), ("B", "So the plan")]


def test_a_sandwiched_run_with_enough_words_is_kept():
    s = seg("a b c d e f", 0.0, 5.0, "A", [
        w("a", 0.0, 0.5, "A"), w("b", 0.6, 1.0, "A"), w("c", 1.1, 1.2, "B"), w("d", 1.25, 1.4, "B"),
        w("e", 1.5, 2.0, "A"), w("f", 2.1, 3.0, "A"),
    ])
    assert [p["speaker"] for p in split(s)] == ["A", "B", "A"]


def test_a_sandwiched_single_word_lasting_long_enough_is_kept():
    s = seg("a b c d e", 0.0, 5.0, "A", [
        w("a", 0.0, 0.5, "A"), w("b", 0.6, 1.0, "A"), w("c", 1.1, 2.2, "B"),
        w("d", 2.3, 2.6, "A"), w("e", 2.7, 3.0, "A"),
    ])
    assert [p["speaker"] for p in split(s)] == ["A", "B", "A"]


def test_smoothing_thresholds_are_parameters():
    s = seg("a b c d e f", 0.0, 5.0, "A", [
        w("a", 0.0, 0.5, "A"), w("b", 0.6, 1.0, "A"), w("c", 1.1, 1.2, "B"), w("d", 1.25, 1.4, "B"),
        w("e", 1.5, 2.0, "A"), w("f", 2.1, 3.0, "A"),
    ])
    assert split(s, min_words=3, min_ms=1000) == [s]


# ---- label-set guard ----

def test_never_introduces_a_label_that_won_no_whole_segment():
    """A label that only ever wins a few words would become a speaker of its own - a Speaker row, a
    voiceprint from a second of audio, and on the live path a new session speaker the stitcher can
    never merge back. Its words go to a neighbouring run instead."""
    s = seg("a b c d e", 0.0, 5.0, "A", [
        w("a", 0.0, 0.5, "A"), w("b", 0.6, 1.0, "A"),
        w("c", 1.1, 1.5, "C"), w("d", 1.6, 2.5, "C"), w("e", 2.6, 3.0, "C"),
    ])
    assert split(s, labels=("A", "B")) == [s]


def test_split_by_word_speaker_takes_the_allowed_labels_from_the_segments_themselves():
    segments = [
        seg("a b c d", 0.0, 4.0, "A", [
            w("a", 0.0, 0.5, "A"), w("b", 0.6, 1.0, "A"), w("c", 2.0, 2.5, "B"), w("d", 2.6, 3.0, "B")]),
        seg("e f g h", 4.0, 8.0, "B", [
            w("e", 4.0, 4.5, "B"), w("f", 4.6, 5.0, "B"), w("g", 6.0, 6.5, "C"), w("h", 6.6, 7.0, "C")]),
    ]
    out = segment_split.split_by_word_speaker(segments)
    # B won a segment, so A's segment is cut; C won none, so B's is not.
    assert [(p["speaker"], p["text"]) for p in out] == [("A", "a b"), ("B", "c d"), ("B", "e f g h")]


def test_split_by_word_speaker_accepts_labels_from_a_wider_pass():
    """The live path splits only the segments it keeps, but the guard must judge labels over the whole
    decoded window, the same pass the voiceprints come from."""
    segments = [seg("a b c d", 0.0, 4.0, "A", [
        w("a", 0.0, 0.5, "A"), w("b", 0.6, 1.0, "A"), w("c", 2.0, 2.5, "B"), w("d", 2.6, 3.0, "B")])]
    out = segment_split.split_by_word_speaker(segments, labels={"A", "B"})
    assert [p["speaker"] for p in out] == ["A", "B"]
