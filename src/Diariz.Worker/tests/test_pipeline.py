"""Tests for the segment-shaping contract in pipeline._shape_segments()."""
import numpy as np
import pytest

import pipeline


def test_duration_ms_from_sample_count():
    # 16 kHz mono: 16000 samples == 1000 ms.
    assert pipeline._duration_ms(np.zeros(16000)) == 1000
    assert pipeline._duration_ms(np.zeros(8000)) == 500
    assert pipeline._duration_ms(np.zeros(0)) == 0


def test_too_long_respects_cap_and_unlimited():
    assert pipeline._too_long(5000, max_seconds=4) is True       # 5 s > 4 s cap
    assert pipeline._too_long(3000, max_seconds=4) is False      # 3 s <= 4 s cap
    assert pipeline._too_long(10**9, max_seconds=0) is False     # 0 = unlimited


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


# ---- _diarize speaker-count hint forwarding ----

def test_diarize_forwards_only_supplied_hints(monkeypatch):
    calls = {}

    def fake_diarizer(audio, **kwargs):
        calls.update(audio=audio, kwargs=kwargs)
        return "diarization"

    monkeypatch.setattr(pipeline, "_get_diarizer", lambda: fake_diarizer)

    out = pipeline._diarize("AUDIO", min_speakers=2, max_speakers=None)

    assert out == "diarization"
    assert calls["audio"] == "AUDIO"
    assert calls["kwargs"] == {"min_speakers": 2}  # max omitted when None


def test_diarize_passes_no_hints_when_none(monkeypatch):
    calls = {}
    monkeypatch.setattr(pipeline, "_get_diarizer", lambda: lambda audio, **kw: calls.update(kw=kw))

    pipeline._diarize("AUDIO")

    assert calls["kw"] == {}


# ---- _asr backend dispatch (CUDA faster-whisper vs ROCm openai-whisper) ----

def test_config_asr_backend_defaults_to_whisperx():
    import importlib
    import config as config_module
    importlib.reload(config_module)
    assert config_module.config.ASR_BACKEND == "whisperx"


def test_normalize_segments_keeps_only_start_end_text():
    raw = [{"start": 1.0, "end": 2.0, "text": "hi", "tokens": [1, 2], "id": 0}]
    assert pipeline._normalize_segments(raw) == [{"start": 1.0, "end": 2.0, "text": "hi"}]


def test_asr_whisperx_backend_uses_faster_whisper(monkeypatch):
    monkeypatch.setattr(pipeline.config, "ASR_BACKEND", "whisperx")
    monkeypatch.setattr(pipeline.config, "BATCH_SIZE", 8)
    captured = {}

    class FakeModel:
        def transcribe(self, audio, **kwargs):
            captured["audio"] = audio
            captured["kwargs"] = kwargs
            return {"language": "fr", "segments": [{"start": 0.0, "end": 1.0, "text": "bonjour"}]}

    monkeypatch.setattr(pipeline, "_get_whisper", lambda: FakeModel())

    out = pipeline._asr("AUDIO")

    assert captured["kwargs"] == {"batch_size": 8}          # whisperx is batched
    assert out["language"] == "fr"
    assert out["segments"] == [{"start": 0.0, "end": 1.0, "text": "bonjour"}]


def test_asr_openai_whisper_backend_normalizes_and_sets_fp16(monkeypatch):
    monkeypatch.setattr(pipeline.config, "ASR_BACKEND", "whisper")
    monkeypatch.setattr(pipeline.config, "DEVICE", "cuda")  # ROCm also reports "cuda"
    captured = {}

    class FakeModel:
        def transcribe(self, audio, **kwargs):
            captured["audio"] = audio
            captured["kwargs"] = kwargs
            # openai-whisper shape: language + rich segments (extra keys must be dropped).
            return {
                "language": "en",
                "text": "hello world",
                "segments": [{"start": 0.0, "end": 1.2, "text": "hello world", "id": 0, "tokens": [1]}],
            }

    monkeypatch.setattr(pipeline, "_get_whisper_py", lambda: FakeModel())

    out = pipeline._asr("AUDIO")

    assert captured["kwargs"] == {"fp16": True}             # fp16 on GPU (DEVICE != cpu)
    assert out["language"] == "en"
    assert out["segments"] == [{"start": 0.0, "end": 1.2, "text": "hello world"}]


def test_asr_openai_whisper_uses_fp32_on_cpu(monkeypatch):
    monkeypatch.setattr(pipeline.config, "ASR_BACKEND", "whisper")
    monkeypatch.setattr(pipeline.config, "DEVICE", "cpu")
    captured = {}

    class FakeModel:
        def transcribe(self, audio, **kwargs):
            captured["kwargs"] = kwargs
            return {"language": "en", "segments": []}

    monkeypatch.setattr(pipeline, "_get_whisper_py", lambda: FakeModel())

    pipeline._asr("AUDIO")

    assert captured["kwargs"] == {"fp16": False}            # fp16 unsupported on CPU
