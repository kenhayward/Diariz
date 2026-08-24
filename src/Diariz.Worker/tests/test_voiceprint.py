"""Tests for slicing chosen audio spans and embedding them (voiceprint.embed_spans).

The embedder is passed in, so none of this needs torch or a GPU - the same seam
pipeline._speaker_embeddings uses.
"""
import numpy as np
import pytest

import voiceprint


def _stub_embedder(seen):
    def embed(waveform):
        seen.append(len(waveform))
        return np.array([1.0, 0.0, 0.0], dtype="float32")
    return embed


def test_concatenates_only_the_chosen_spans():
    # 16 kHz: 500 ms == 8000 samples. Two 500 ms spans must give 16000 samples, not the whole clip.
    audio = np.arange(48000, dtype="float32")
    seen = []
    voiceprint.embed_spans(audio, [{"StartMs": 0, "EndMs": 500}, {"StartMs": 2000, "EndMs": 2500}],
                           _stub_embedder(seen), sample_rate=16000, max_seconds=120)
    assert seen == [16000]


def test_takes_the_audio_from_where_the_span_says():
    # Not just the right length - the right samples. A slice that started at 0 would be the same size
    # and completely wrong.
    audio = np.arange(32000, dtype="float32")
    captured = {}

    def embed(waveform):
        captured["first"] = float(waveform[0])
        return np.array([1.0], dtype="float32")

    voiceprint.embed_spans(audio, [{"StartMs": 1000, "EndMs": 1500}], embed,
                           sample_rate=16000, max_seconds=120)
    assert captured["first"] == 16000.0


def test_no_spans_means_the_whole_clip():
    # An empty list is "the whole speaker" - the state every sample that predates selection is in.
    audio = np.arange(32000, dtype="float32")
    seen = []
    voiceprint.embed_spans(audio, [], _stub_embedder(seen), sample_rate=16000, max_seconds=120)
    assert seen == [32000]


def test_truncates_to_the_cap_and_reports_what_it_used():
    audio = np.arange(16000 * 200, dtype="float32")
    result = voiceprint.embed_spans(audio, [{"StartMs": 0, "EndMs": 200000}],
                                    _stub_embedder([]), sample_rate=16000, max_seconds=120)
    # The caller must be able to say "using 2:00 of the 3:20 selected" rather than implying it used it all.
    assert result["UsedMs"] == 120000
    assert result["SelectedMs"] == 200000


def test_clamps_spans_that_run_past_the_end_of_the_audio():
    audio = np.arange(16000, dtype="float32")
    seen = []
    voiceprint.embed_spans(audio, [{"StartMs": 500, "EndMs": 99999}], _stub_embedder(seen),
                           sample_rate=16000, max_seconds=120)
    assert seen == [8000]


def test_l2_normalises_the_vector():
    # Cosine distance is the whole comparison, so an un-normalised vector would make every match wrong
    # in proportion to how loud the speaker was. float32, hence approx rather than equality.
    audio = np.arange(16000, dtype="float32")
    result = voiceprint.embed_spans(audio, [], lambda w: np.array([3.0, 4.0], dtype="float32"),
                                    sample_rate=16000, max_seconds=120)
    assert result["Embedding"] == pytest.approx([0.6, 0.8])
    assert np.linalg.norm(result["Embedding"]) == pytest.approx(1.0)


def test_returns_none_when_the_spans_select_no_audio():
    # A selection entirely past the end of the clip must not embed silence and store it as a voiceprint.
    audio = np.arange(16000, dtype="float32")
    assert voiceprint.embed_spans(audio, [{"StartMs": 5000, "EndMs": 6000}], _stub_embedder([]),
                                  sample_rate=16000, max_seconds=120) is None


def test_returns_none_for_an_empty_clip():
    assert voiceprint.embed_spans(np.zeros(0), [], _stub_embedder([]),
                                  sample_rate=16000, max_seconds=120) is None
