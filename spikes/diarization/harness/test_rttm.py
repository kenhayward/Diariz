"""Tests for the pure reference-building helpers. Run: python -m pytest spikes/diarization/harness"""
from rttm import Turn, clip_turns, merge_turns, read_rttm, reference_from_segments, write_rttm


def test_write_then_read_round_trips():
    turns = [Turn(0.0, 1.5, "A"), Turn(2.25, 4.0, "B")]
    text = write_rttm("rec1", turns)
    assert text.splitlines()[0] == "SPEAKER rec1 1 0.000 1.500 <NA> <NA> A <NA> <NA>"
    assert read_rttm(text) == {"rec1": turns}


def test_read_rttm_ignores_blank_and_non_speaker_lines():
    text = "\n;; comment\nSPEAKER r 1 1.0 2.0 <NA> <NA> X <NA> <NA>\nSPKR-INFO r 1 <NA> <NA> <NA> unknown X <NA> <NA>\n"
    assert read_rttm(text) == {"r": [Turn(1.0, 3.0, "X")]}


def test_reference_maps_named_labels_to_people_and_merges_over_split_labels():
    # Two diarized labels both named as the same person collapse into one reference speaker.
    segments = [(0, 1000, "SPEAKER_00"), (1000, 2000, "SPEAKER_03"), (2500, 3000, "SPEAKER_01")]
    people = {"SPEAKER_00": "p-ada", "SPEAKER_03": "p-ada", "SPEAKER_01": "p-grace"}
    turns = reference_from_segments(segments, people)
    assert turns == [Turn(0.0, 2.0, "S01"), Turn(2.5, 3.0, "S02")]


def test_reference_keeps_unnamed_labels_as_their_own_speakers():
    segments = [(0, 1000, "SPEAKER_00"), (1000, 2000, "SPEAKER_07")]
    turns = reference_from_segments(segments, {"SPEAKER_00": "p-ada"})
    assert [t.speaker for t in turns] == ["S01", "S02"]


def test_reference_drops_unknown_label():
    turns = reference_from_segments([(0, 1000, "UNKNOWN"), (1000, 2000, None)], {})
    assert turns == []


def test_merge_turns_joins_same_speaker_across_small_gap_only():
    turns = [Turn(0, 1, "A"), Turn(1.1, 2, "A"), Turn(3, 4, "A"), Turn(4, 5, "B")]
    assert merge_turns(turns, max_gap=0.2) == [Turn(0, 2, "A"), Turn(3, 4, "A"), Turn(4, 5, "B")]


def test_clip_turns_trims_and_shifts_into_the_excerpt():
    turns = [Turn(5, 15, "A"), Turn(20, 30, "B"), Turn(40, 50, "C")]
    assert clip_turns(turns, 10, 25) == [Turn(0, 5, "A"), Turn(10, 15, "B")]
