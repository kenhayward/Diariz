"""Tests for the segment-shaping contract in pipeline._shape_segments()."""
import numpy as np
import pytest

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


# ---- _speaker_embeddings (voiceprint extraction) ----

def test_speaker_embeddings_one_per_speaker_l2_normalised():
    audio = np.ones(16000 * 4, dtype="float32")  # 4 s of audio
    segments = [
        {"Speaker": "SPEAKER_00", "StartMs": 0, "EndMs": 1000, "Text": "a"},
        {"Speaker": "SPEAKER_01", "StartMs": 1000, "EndMs": 2000, "Text": "b"},
        {"Speaker": "SPEAKER_00", "StartMs": 2000, "EndMs": 3000, "Text": "c"},
    ]

    def embed(_waveform):
        return [3.0, 4.0]  # raw vector, norm 5 -> normalised (0.6, 0.8)

    out = pipeline._speaker_embeddings(audio, segments, embed)

    assert sorted(s["Speaker"] for s in out) == ["SPEAKER_00", "SPEAKER_01"]
    for s in out:
        assert s["Embedding"] == pytest.approx([0.6, 0.8])


def test_speaker_embeddings_skips_unknown_speakers():
    audio = np.ones(16000, dtype="float32")
    segments = [{"Speaker": "UNKNOWN", "StartMs": 0, "EndMs": 500, "Text": "x"}]

    assert pipeline._speaker_embeddings(audio, segments, lambda w: [1.0]) == []


def test_speaker_embeddings_pools_segments_and_caps_at_max_seconds():
    audio = np.ones(16000 * 10, dtype="float32")  # 10 s
    segments = [{"Speaker": "S", "StartMs": 0, "EndMs": 10000, "Text": "long"}]
    seen = []

    def embed(waveform):
        seen.append(len(waveform))
        return [1.0, 0.0]

    pipeline._speaker_embeddings(audio, segments, embed, sample_rate=16000, max_seconds=2)

    assert seen[0] == 16000 * 2  # pooled audio capped to 2 s


def test_speaker_embeddings_empty_when_no_segments():
    assert pipeline._speaker_embeddings(np.ones(16000, dtype="float32"), [], lambda w: [1.0]) == []
