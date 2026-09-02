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


# ---- Telemetry spans cover the whole job ----
#
# whisperx.load_audio() is a full ffmpeg decode - the slowest single stage on a long upload - and
# _shape_segments walks every word of the transcript. Both sat outside every span, so the span
# breakdown did not add up to the job's wall-clock time and a job could look "unaccounted for".

def test_transcribe_wraps_every_stage_including_the_decode_and_the_shaping(monkeypatch):
    import contextlib

    spans = []

    @contextlib.contextmanager
    def fake_span(op, name):
        spans.append((op, name))
        yield

    monkeypatch.setattr(pipeline.telemetry, "span", fake_span)
    monkeypatch.setattr(pipeline.whisperx, "load_audio", lambda path: np.zeros(16000))
    monkeypatch.setattr(pipeline.config, "MAX_AUDIO_SECONDS", 0)
    monkeypatch.setattr(pipeline, "_asr", lambda audio, language=None: {"language": "en", "segments": []})
    monkeypatch.setattr(pipeline, "_get_align", lambda language: ("model", "meta"))
    monkeypatch.setattr(pipeline.whisperx, "align", lambda *a, **k: {"segments": []})
    monkeypatch.setattr(pipeline, "_diarize", lambda *a, **k: "diarization")
    monkeypatch.setattr(pipeline.whisperx, "assign_word_speakers", lambda d, r: {"segments": []})
    monkeypatch.setattr(pipeline, "_extract_speakers", lambda audio, segments: [])

    out = pipeline.transcribe("/tmp/audio.wav")

    assert out["language"] == "en"
    # The decode and the shaping are now timed too, so the spans account for the wall clock.
    assert spans == [
        ("audio.decode", "decode"),
        ("ai.asr", "asr"),
        ("ai.align", "align"),
        ("ai.diarize", "diarize"),
        ("ai.shape", "shape"),
        ("ai.embeddings", "embeddings"),
    ]


# ---- Alignment fallback for languages whisperx has no align model for ----
#
# whisperx ships align models for 37 languages. Whisper detects ~99, and misdetects the language
# outright on short or quiet audio (a 2 m test recording in English came back as Welsh). Either way
# load_align_model raises, and that used to fail the whole job - discarding a transcript the ASR had
# already produced. Alignment only refines segment boundaries here: _shape_segments keeps no word-level
# data at all, and assign_word_speakers guards its word loop with `if 'words' in seg`, so an unaligned
# transcript still gets per-segment speakers.

def test_get_align_returns_none_when_whisperx_has_no_model_for_the_language(monkeypatch):
    monkeypatch.setattr(pipeline, "_align_cache", {})

    def no_model(language_code, device):
        raise ValueError(f"No default align-model for language: {language_code}")

    monkeypatch.setattr(pipeline.whisperx, "load_align_model", no_model)

    assert pipeline._get_align("cy") is None


def test_get_align_caches_the_missing_model_so_every_job_does_not_retry_the_load(monkeypatch):
    cache = {}
    monkeypatch.setattr(pipeline, "_align_cache", cache)
    calls = []

    def no_model(language_code, device):
        calls.append(language_code)
        raise ValueError(f"No default align-model for language: {language_code}")

    monkeypatch.setattr(pipeline.whisperx, "load_align_model", no_model)

    assert pipeline._get_align("cy") is None
    assert pipeline._get_align("cy") is None

    assert calls == ["cy"]  # the second call was served from the cache
    assert cache["cy"] is None


def test_get_align_lets_a_real_load_failure_through(monkeypatch):
    """A missing model is a ValueError and degrades; anything else (a failed download, a broken
    checkpoint) must still fail the job rather than silently costing every transcript its alignment."""
    monkeypatch.setattr(pipeline, "_align_cache", {})

    def boom(language_code, device):
        raise OSError("connection to huggingface.co failed")

    monkeypatch.setattr(pipeline.whisperx, "load_align_model", boom)

    with pytest.raises(OSError):
        pipeline._get_align("en")


def test_transcribe_keeps_the_transcript_when_the_language_has_no_align_model(monkeypatch):
    aligned = []
    diarized = {}
    monkeypatch.setattr(pipeline.whisperx, "load_audio", lambda path: np.zeros(16000))
    monkeypatch.setattr(pipeline.config, "MAX_AUDIO_SECONDS", 0)
    monkeypatch.setattr(pipeline, "_asr", lambda audio, language=None: {
        "language": "cy",
        "segments": [{"start": 0.0, "end": 1.5, "text": "hello world"}],
    })
    monkeypatch.setattr(pipeline, "_get_align", lambda language: None)
    monkeypatch.setattr(pipeline.whisperx, "align", lambda *a, **k: aligned.append(a) or {"segments": []})
    monkeypatch.setattr(pipeline, "_diarize", lambda *a, **k: "diarization")

    def assign(diarization, result):
        diarized.update(diarization=diarization, segments=result["segments"])
        return {"segments": [{**s, "speaker": "SPEAKER_00"} for s in result["segments"]]}

    monkeypatch.setattr(pipeline.whisperx, "assign_word_speakers", assign)
    monkeypatch.setattr(pipeline, "_extract_speakers", lambda audio, segments: [])

    out = pipeline.transcribe("/tmp/audio.wav")

    assert aligned == []  # alignment skipped, not attempted
    # The ASR segments still reached diarization, so speakers are assigned per segment...
    assert diarized["segments"] == [{"start": 0.0, "end": 1.5, "text": "hello world"}]
    # ...and the job returns a transcript instead of raising.
    assert out["segments"] == [
        {"Speaker": "SPEAKER_00", "StartMs": 0, "EndMs": 1500, "Text": "hello world"}
    ]
    assert out["language"] == "cy"


# ---- Pinned language (skips Whisper's auto-detection) ----

def test_asr_forwards_a_pinned_language_to_faster_whisper(monkeypatch):
    monkeypatch.setattr(pipeline.config, "ASR_BACKEND", "whisperx")
    monkeypatch.setattr(pipeline.config, "BATCH_SIZE", 8)
    captured = {}

    class FakeModel:
        def transcribe(self, audio, **kwargs):
            captured.update(kwargs)
            return {"language": "en", "segments": []}

    monkeypatch.setattr(pipeline, "_get_whisper", lambda: FakeModel())

    pipeline._asr("AUDIO", language="en")

    assert captured == {"batch_size": 8, "language": "en"}


def test_asr_forwards_a_pinned_language_to_openai_whisper(monkeypatch):
    monkeypatch.setattr(pipeline.config, "ASR_BACKEND", "whisper")
    monkeypatch.setattr(pipeline.config, "DEVICE", "cpu")
    captured = {}

    class FakeModel:
        def transcribe(self, audio, **kwargs):
            captured.update(kwargs)
            return {"language": "en", "segments": []}

    monkeypatch.setattr(pipeline, "_get_whisper_py", lambda: FakeModel())

    pipeline._asr("AUDIO", language="de")

    assert captured == {"fp16": False, "language": "de"}


def test_asr_omits_the_language_when_none_is_pinned(monkeypatch):
    """No language means auto-detect. Passing language=None explicitly would be the same thing to
    whisper, but leaving the kwarg out keeps the auto path exactly as it was."""
    monkeypatch.setattr(pipeline.config, "ASR_BACKEND", "whisperx")
    monkeypatch.setattr(pipeline.config, "BATCH_SIZE", 8)
    captured = {}

    class FakeModel:
        def transcribe(self, audio, **kwargs):
            captured.update(kwargs=kwargs)
            return {"language": "fr", "segments": []}

    monkeypatch.setattr(pipeline, "_get_whisper", lambda: FakeModel())

    out = pipeline._asr("AUDIO")

    assert captured["kwargs"] == {"batch_size": 8}
    assert out["language"] == "fr"  # whatever detection returned


def test_transcribe_pins_the_language_for_the_asr_and_the_aligner(monkeypatch):
    captured = {}
    monkeypatch.setattr(pipeline.whisperx, "load_audio", lambda path: np.zeros(16000))
    monkeypatch.setattr(pipeline.config, "MAX_AUDIO_SECONDS", 0)

    def fake_asr(audio, language=None):
        captured["asr_language"] = language
        # Whisper echoes back the language it was told to use.
        return {"language": language or "cy", "segments": []}

    monkeypatch.setattr(pipeline, "_asr", fake_asr)
    monkeypatch.setattr(pipeline, "_get_align", lambda language: captured.update(align_language=language) or None)
    monkeypatch.setattr(pipeline, "_diarize", lambda *a, **k: "diarization")
    monkeypatch.setattr(pipeline.whisperx, "assign_word_speakers", lambda d, r: {"segments": []})
    monkeypatch.setattr(pipeline, "_extract_speakers", lambda audio, segments: [])

    out = pipeline.transcribe("/tmp/audio.wav", language="en")

    assert captured["asr_language"] == "en"
    assert captured["align_language"] == "en"
    assert out["language"] == "en"

def test_keeps_aligned_word_timings():
    raw = [{
        "text": "Hello world", "speaker": "SPEAKER_00", "start": 1.2, "end": 2.5,
        "words": [
            {"word": "Hello", "start": 1.2, "end": 1.6},
            {"word": "world", "start": 1.7, "end": 2.5},
        ],
    }]
    assert pipeline._shape_segments(raw)[0]["Words"] == [
        {"W": "Hello", "S": 1200, "E": 1600},
        {"W": "world", "S": 1700, "E": 2500},
    ]


def test_drops_words_missing_timings_rather_than_guessing():
    # whisperx leaves start/end off a word it could not align. A guessed timing would cut the audio in
    # the wrong place, which is exactly what word snapping exists to prevent.
    raw = [{
        "text": "Hello world", "speaker": "S", "start": 0.0, "end": 2.0,
        "words": [
            {"word": "Hello", "start": 0.0, "end": 0.5},
            {"word": "world"},
        ],
    }]
    assert pipeline._shape_segments(raw)[0]["Words"] == [{"W": "Hello", "S": 0, "E": 500}]


def test_omits_the_words_key_when_no_word_is_usable():
    # The languages with no alignment model produce no usable words at all. The key must be absent, not
    # null, so the segment contract stays exactly what it was before this change.
    raw = [{"text": "Hola", "speaker": "S", "start": 0.0, "end": 1.0, "words": [{"word": "Hola"}]}]
    assert "Words" not in pipeline._shape_segments(raw)[0]
    assert "Words" not in pipeline._shape_segments([{"text": "Hola", "speaker": "S", "start": 0, "end": 1}])[0]


def test_strips_whitespace_around_words():
    raw = [{
        "text": "Hello world", "speaker": "S", "start": 0.0, "end": 2.0,
        "words": [{"word": " Hello ", "start": 0.0, "end": 0.5}, {"word": "world", "start": 0.6, "end": 2.0}],
    }]
    assert pipeline._shape_segments(raw)[0]["Words"][0]["W"] == "Hello"


# ---- live-chunk window trimming ----

def test_trim_to_window_drops_segments_from_the_prepended_overlap():
    """The worker prepends the previous chunk's tail so Whisper does not start mid-sentence.

    Everything from that prepended audio has already been reported by the previous chunk, so it must be
    discarded - otherwise the transcript repeats a sentence at every chunk boundary, which reads as a
    transcription bug rather than an overlap one.
    """
    # 3 s of overlap prepended: chunk audio starts at t=3000 in the decoded window.
    segments = [
        {"Speaker": "SPEAKER_00", "StartMs": 200, "EndMs": 2600, "Text": "already reported"},
        {"Speaker": "SPEAKER_00", "StartMs": 3100, "EndMs": 6000, "Text": "genuinely new"},
    ]
    kept = pipeline._trim_to_window(segments, overlap_ms=3000)
    assert [s["Text"] for s in kept] == ["genuinely new"]


def test_trim_to_window_keeps_a_segment_straddling_the_boundary():
    """A sentence that starts inside the overlap and finishes inside this chunk belongs to this chunk.

    Dropping it would lose the words entirely: the previous chunk ended before it finished, so nobody
    else reports them.
    """
    segments = [{"Speaker": "SPEAKER_00", "StartMs": 2500, "EndMs": 4500, "Text": "straddles"}]
    assert len(pipeline._trim_to_window(segments, overlap_ms=3000)) == 1


def test_trim_to_window_is_a_no_op_without_overlap():
    segments = [{"Speaker": "SPEAKER_00", "StartMs": 0, "EndMs": 1000, "Text": "first chunk"}]
    assert pipeline._trim_to_window(segments, overlap_ms=0) == segments


def test_offset_into_recording_time_shifts_every_segment():
    """The API stores what the worker sends without arithmetic, so the shift happens here."""
    segments = [
        {"Speaker": "SPEAKER_00", "StartMs": 0, "EndMs": 1000, "Text": "a"},
        {"Speaker": "SPEAKER_01", "StartMs": 1000, "EndMs": 2000, "Text": "b"},
    ]
    shifted = pipeline._offset_segments(segments, offset_ms=90_000, overlap_ms=3_000)
    # Offset is the chunk's start in the recording; the overlap sat BEFORE it, so subtract it back out.
    assert [(s["StartMs"], s["EndMs"]) for s in shifted] == [(87_000, 88_000), (88_000, 89_000)]


def test_window_helpers_consume_exactly_what_shape_segments_produces():
    """The trim and offset helpers must read the key names `_shape_segments` actually writes.

    This is the seam the live path runs through: transcribe_window shapes the segments and then
    immediately trims and offsets them. Both sides were unit-tested in isolation and both passed,
    because the helper tests built their own snake_case dicts - while `_shape_segments` emits
    PascalCase, as it must, since these same dicts go on to be the callback body that .NET binds.
    The mismatch could therefore only ever show up at runtime, and it did: every live chunk died with
    KeyError: 'start_ms' and no transcript ever appeared.

    Feeding real `_shape_segments` output through is what makes the two sides unable to drift apart.
    """
    shaped = pipeline._shape_segments([
        {"speaker": "SPEAKER_00", "start": 0.2, "end": 2.6, "text": "in the overlap"},
        {"speaker": "SPEAKER_01", "start": 3.1, "end": 6.0, "text": "genuinely new"},
    ])

    trimmed = pipeline._trim_to_window(shaped, overlap_ms=3000)
    shifted = pipeline._offset_segments(trimmed, offset_ms=30000, overlap_ms=3000)

    assert [s["Text"] for s in shifted] == ["genuinely new"]
    # 3100 in window time, minus the 3000 of prepended overlap, plus the 30000 offset.
    assert shifted[0]["StartMs"] == 30100
    assert shifted[0]["EndMs"] == 33000


def test_diarizer_is_built_against_the_pyannote_4_era_whisperx_api(monkeypatch):
    """whisperx 3.8 moved diarization and renamed its auth kwarg. Both changes are silent at import.

    `whisperx.DiarizationPipeline` is no longer re-exported at the top level - it lives in
    `whisperx.diarize` - and `use_auth_token` became `token`. The first fails with an AttributeError on
    the first job rather than at startup; the second would be accepted as an unexpected kwarg by
    nothing at all, so it fails too. Neither is caught by importing the module, which is why this test
    asserts the call rather than the import.
    """
    seen = {}

    class FakePipeline:
        def __init__(self, **kwargs):
            seen.update(kwargs)

    monkeypatch.setattr(pipeline, "_diarize_model", None, raising=False)
    monkeypatch.setattr(pipeline.config, "HF_TOKEN", "hf-token")
    monkeypatch.setattr(pipeline.config, "DEVICE", "cuda")
    monkeypatch.setattr(pipeline, "_DiarizationPipeline", FakePipeline, raising=False)

    pipeline._get_diarizer()

    assert seen.get("token") == "hf-token", "whisperx 3.8 takes `token`, not `use_auth_token`"
    assert seen.get("device") == "cuda"
    assert "use_auth_token" not in seen
