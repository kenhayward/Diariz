"""Tests for the word-level speaker error. Run: python -m pytest spikes/diarization/harness"""
from rttm import Turn
from words import dominant_speaker, label_words, word_speaker_error


def test_dominant_speaker_is_the_one_with_most_overlap():
    turns = [Turn(0, 1.2, "A"), Turn(1.2, 3, "B")]
    assert dominant_speaker(0.5, 2.0, turns) == "B"  # A overlaps 0.7, B overlaps 0.8


def test_dominant_speaker_sums_split_turns_of_the_same_speaker():
    turns = [Turn(0, 0.4, "A"), Turn(0.4, 0.9, "B"), Turn(0.9, 1.4, "A")]
    assert dominant_speaker(0, 1.4, turns) == "A"


def test_dominant_speaker_is_none_without_overlap():
    assert dominant_speaker(5, 6, [Turn(0, 1, "A")]) is None


def test_label_words_segment_mode_gives_every_word_its_segments_speaker():
    # The segment overlaps B most, so its first word shows B even though A is speaking under it -
    # exactly what a user sees in the transcript.
    segments = [{"s": 0.0, "e": 3.0, "words": [{"s": 0.0, "e": 0.5}, {"s": 1.0, "e": 3.0}]}]
    turns = [Turn(0, 0.6, "A"), Turn(0.6, 3, "B")]
    assert label_words(segments, turns, mode="segment") == ["B", "B"]
    assert label_words(segments, turns, mode="word") == ["A", "B"]


def test_word_speaker_error_uses_the_best_one_to_one_mapping():
    ref = ["S1", "S1", "S2", "S2"]
    hyp = ["x", "x", "y", "x"]  # x->S1, y->S2: one error
    r = word_speaker_error(ref, hyp)
    assert (r["words"], r["errors"]) == (4, 1)


def test_word_speaker_error_counts_unlabelled_and_extra_speakers_as_errors():
    ref = ["S1", "S1", "S1", "S1"]
    hyp = ["x", None, "z", "x"]  # only one hyp speaker can map to S1
    r = word_speaker_error(ref, hyp)
    assert (r["errors"], r["unlabelled"]) == (2, 1)


def test_word_speaker_error_skips_words_the_reference_has_no_speaker_for():
    r = word_speaker_error([None, "S1"], ["x", "x"])
    assert (r["words"], r["errors"]) == (1, 0)
