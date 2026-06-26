"""Tests for the segment-shaping contract in pipeline._shape_segments()."""
import pipeline


def test_converts_seconds_to_ms_and_keeps_pascalcase_keys():
    raw = [{"text": " Hello world ", "speaker": "SPEAKER_00", "start": 1.2, "end": 2.5}]
    assert pipeline._shape_segments(raw) == [
        {"Speaker": "SPEAKER_00", "StartMs": 1200, "EndMs": 2500, "Text": "Hello world"}
    ]


def test_drops_empty_and_whitespace_only_segments():
    raw = [
        {"text": "   ", "speaker": "S", "start": 0, "end": 1},
        {"text": None, "speaker": "S", "start": 1, "end": 2},
        {"text": "real", "speaker": "S", "start": 2, "end": 3},
    ]
    shaped = pipeline._shape_segments(raw)
    assert [s["Text"] for s in shaped] == ["real"]


def test_defaults_missing_speaker_to_unknown():
    raw = [{"text": "hi", "start": 0.0, "end": 0.4}]  # no "speaker" key
    assert pipeline._shape_segments(raw)[0]["Speaker"] == "UNKNOWN"


def test_rounds_milliseconds():
    raw = [{"text": "x", "speaker": "S", "start": 0.0014, "end": 0.0016}]
    out = pipeline._shape_segments(raw)[0]
    assert out["StartMs"] == 1  # 1.4 ms -> 1
    assert out["EndMs"] == 2    # 1.6 ms -> 2
