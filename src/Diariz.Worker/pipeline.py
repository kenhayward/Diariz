"""WhisperX transcription + pyannote diarization pipeline.

Models are loaded lazily once and reused across jobs (loading large-v3 + pyannote
is expensive). Produces a list of speaker-attributed, timestamped segments.
"""
import logging

import numpy as np
import whisperx

import telemetry
from config import config

log = logging.getLogger("pipeline")

_whisper_model = None
_whisper_py_model = None
_align_cache = {}  # language_code -> (model, metadata)
_diarize_model = None
_embedder = None


def _get_whisper():
    """faster-whisper (CTranslate2) ASR model — the default backend (CUDA/CPU)."""
    global _whisper_model
    if _whisper_model is None:
        log.info("Loading Whisper model %s on %s (%s)",
                 config.WHISPER_MODEL, config.DEVICE, config.COMPUTE_TYPE)
        _whisper_model = whisperx.load_model(
            config.WHISPER_MODEL, config.DEVICE, compute_type=config.COMPUTE_TYPE)
    return _whisper_model


def _get_whisper_py():
    """openai-whisper (pure PyTorch) ASR model — the AMD ROCm backend, where CTranslate2 has no GPU
    support. Imported lazily so the default (faster-whisper) image doesn't need openai-whisper installed."""
    global _whisper_py_model
    if _whisper_py_model is None:
        import whisper
        log.info("Loading openai-whisper model %s on %s", config.WHISPER_MODEL, config.DEVICE)
        _whisper_py_model = whisper.load_model(config.WHISPER_MODEL, device=config.DEVICE)
    return _whisper_py_model


def _normalize_segments(segments) -> list[dict]:
    """Reduce ASR segments to the minimal {start, end, text} the word-aligner consumes. Pure — different
    backends return extra keys (tokens/ids/probabilities) the rest of the pipeline doesn't use."""
    return [{"start": s["start"], "end": s["end"], "text": s.get("text", "")} for s in segments]


def _asr(audio, language: str | None = None) -> dict:
    """Whisper transcription step, backend-pluggable. Returns {language, segments[{start,end,text}]}.
    The aligner re-times every word afterwards, so the backend only needs decent segment text + language.

    `language` pins the spoken language and skips Whisper's auto-detection. Detection reads the opening
    of the audio before any speech is known to be there, so a recording that starts quiet can come back
    as a language nobody spoke - a 2 m English test recording detected as Welsh, which then had no align
    model. None keeps auto-detection: the kwarg is left off entirely rather than passed as None, so the
    unpinned path stays byte-for-byte what it was."""
    kwargs = {"language": language} if language else {}
    if config.ASR_BACKEND == "whisper":
        result = _get_whisper_py().transcribe(audio, fp16=config.DEVICE != "cpu", **kwargs)
    else:
        result = _get_whisper().transcribe(audio, batch_size=config.BATCH_SIZE, **kwargs)
    return {"language": result.get("language", "en"), "segments": _normalize_segments(result["segments"])}


def _get_align(language_code: str):
    """The wav2vec2 alignment model for a language, or None when whisperx ships none for it.

    whisperx has align models for 37 languages; Whisper transcribes ~99 and misdetects outright on
    short or quiet audio. Letting the resulting ValueError escape failed the whole job and threw away a
    transcript the ASR had already produced, so a missing model degrades instead (see transcribe). The
    None is cached like a real model: without it every job in an unalignable language would re-attempt
    the load. Only the "no such model" ValueError is swallowed - a failed download or a broken
    checkpoint still raises, because silently dropping alignment for English would be a worse bug than
    the one this fixes."""
    if language_code not in _align_cache:
        try:
            _align_cache[language_code] = whisperx.load_align_model(
                language_code=language_code, device=config.DEVICE)
        except ValueError:
            log.warning("No alignment model for language %s; continuing with segment-level timings "
                        "(word-level alignment unavailable)", language_code)
            _align_cache[language_code] = None
    return _align_cache[language_code]


def _get_diarizer():
    global _diarize_model
    if _diarize_model is None:
        if not config.HF_TOKEN:
            raise RuntimeError(
                "HF_TOKEN is required for pyannote diarization. Set it and accept the "
                "pyannote/speaker-diarization-3.1 model terms on Hugging Face.")
        _diarize_model = _DiarizationPipeline(
            model_name=config.DIARIZATION_MODEL, token=config.HF_TOKEN, device=config.DEVICE)
    return _diarize_model


def _DiarizationPipeline(**kwargs):
    """whisperx 3.8's diarization entry point, imported at call time.

    Two things moved with the pyannote 4 upgrade and both are silent at import: it is no longer
    re-exported as `whisperx.DiarizationPipeline` (it lives in `whisperx.diarize`), and `use_auth_token`
    became `token`. Imported here rather than at module scope so the test suite - which stubs whisperx
    wholesale - can substitute it without needing the real package."""
    from whisperx.diarize import DiarizationPipeline
    return DiarizationPipeline(**kwargs)


def _get_embedder():
    """Lazy-load the SpeechBrain ECAPA speaker encoder and return a callable that maps a
    1-D 16 kHz waveform to a raw 192-d embedding. Loaded once and reused (like the other models).
    Heavy imports are function-local so the module imports without torch/speechbrain (test env)."""
    global _embedder
    if _embedder is None:
        import torch
        from speechbrain.inference.speaker import EncoderClassifier

        log.info("Loading speaker encoder %s on %s", config.EMBED_MODEL, config.DEVICE)
        model = EncoderClassifier.from_hparams(
            source=config.EMBED_MODEL,
            run_opts={"device": config.DEVICE},
            savedir=config.EMBED_CACHE_DIR or None,
        )

        def embed(waveform):
            wav = torch.from_numpy(np.asarray(waveform, dtype="float32")).unsqueeze(0)
            emb = model.encode_batch(wav)  # [batch=1, 1, 192]
            return emb.squeeze().detach().cpu().numpy()

        _embedder = embed
    return _embedder


def _speaker_embeddings(audio, segments: list[dict], embed_fn,
                        sample_rate: int = config.SAMPLE_RATE,
                        max_seconds: float = config.EMBED_MAX_SECONDS) -> list[dict]:
    """For each distinct (named) speaker, pool that speaker's segment audio up to `max_seconds`,
    embed it via `embed_fn`, and L2-normalise → one vector per speaker. Pure (model passed in)
    so it's unit-testable with a stub embedder. Skips UNKNOWN speakers."""
    max_samples = int(max_seconds * sample_rate)
    n = len(audio)

    by_speaker: dict[str, list[tuple[int, int]]] = {}
    for seg in segments:
        label = seg.get("Speaker") or "UNKNOWN"
        if label == "UNKNOWN":
            continue
        by_speaker.setdefault(label, []).append((seg["StartMs"], seg["EndMs"]))

    results = []
    for label, spans in by_speaker.items():
        chunks = []
        total = 0
        for start_ms, end_ms in spans:
            a = max(0, int(start_ms * sample_rate / 1000))
            b = min(n, int(end_ms * sample_rate / 1000))
            if b <= a:
                continue
            chunks.append(audio[a:b])
            total += b - a
            if total >= max_samples:
                break
        if not chunks:
            continue

        waveform = np.concatenate(chunks)[:max_samples]
        if waveform.size == 0:
            continue

        vec = np.asarray(embed_fn(waveform), dtype="float32").reshape(-1)
        norm = float(np.linalg.norm(vec))
        if norm > 0:
            vec = vec / norm
        results.append({"Speaker": label, "Embedding": [float(x) for x in vec]})

    return results


def _extract_speakers(audio, segments: list[dict]) -> list[dict]:
    """Gated, best-effort voiceprint extraction — never fails a job over identification."""
    if not config.ENABLE_SPEAKER_EMBEDDINGS:
        return []
    try:
        return _speaker_embeddings(audio, segments, _get_embedder())
    except Exception:  # noqa: BLE001 - identification is optional
        log.exception("Speaker embedding extraction failed; continuing without voiceprints")
        return []


def _shape_words(raw_words) -> list[dict]:
    """Aligned word timings in the API's contract shape, seconds -> ms. Words whisperx could not align
    (no start/end) are dropped rather than guessed: a guessed boundary would slice the wrong audio, which
    is the whole reason a split snaps to a word. Keys are single letters because this is stored as jsonb on
    every segment and a long meeting carries roughly 10k of them."""
    words = []
    for w in raw_words or []:
        text = (w.get("word") or "").strip()
        start, end = w.get("start"), w.get("end")
        if not text or start is None or end is None:
            continue
        words.append({"W": text, "S": int(round(start * 1000)), "E": int(round(end * 1000))})
    return words


def _shape_segments(raw_segments: list[dict]) -> list[dict]:
    """Convert whisperx segments to the API's contract: PascalCase keys, seconds -> ms,
    empty-text segments dropped, missing speaker defaulted to UNKNOWN, and aligned word
    timings carried through under "Words" when there are any."""
    segments = []
    for seg in raw_segments:
        text = (seg.get("text") or "").strip()
        if not text:
            continue
        shaped = {
            "Speaker": seg.get("speaker", "UNKNOWN"),
            "StartMs": int(round(seg["start"] * 1000)),
            "EndMs": int(round(seg["end"] * 1000)),
            "Text": text,
        }
        # Absent, never null, when there is nothing usable: the segment contract stays exactly what it
        # was for every language with no alignment model.
        words = _shape_words(seg.get("words"))
        if words:
            shaped["Words"] = words
        segments.append(shaped)
    return segments


def _diarize(audio, min_speakers=None, max_speakers=None):
    """Run pyannote diarization, forwarding optional speaker-count hints. Only non-None hints are
    passed through, so a recording with two people merged into one speaker can be split by setting
    min_speakers=2. Separated out so the hint forwarding is unit-testable without the models."""
    kwargs = {}
    if min_speakers is not None:
        kwargs["min_speakers"] = min_speakers
    if max_speakers is not None:
        kwargs["max_speakers"] = max_speakers
    return _get_diarizer()(audio, **kwargs)


def _duration_ms(audio) -> int:
    """Duration of the loaded 16 kHz waveform, in milliseconds. Pure (testable without models)."""
    return int(round(len(audio) / config.SAMPLE_RATE * 1000))


def _too_long(duration_ms: int, max_seconds: float) -> bool:
    """Whether the audio exceeds the configured cap (0 = unlimited). Pure."""
    return max_seconds > 0 and duration_ms > max_seconds * 1000


def transcribe_window(audio_path: str, offset_ms: float = 0, overlap_ms: float = 0,
                      language=None) -> dict:
    """Transcribe one live chunk, already byte-joined with the previous chunk's tail.

    The same passes as `transcribe`, minus the duration cap (a chunk is seconds long) and plus the two
    window corrections: drop what belonged to the prepended overlap, then shift into recording time.
    Diarization stays on - not to label anything yet, but because the per-speaker ECAPA vectors are what
    later stitches a speaker's identity across chunks, and a diarized chunk is affordable (measured:
    ~2.7 s for 30 s of audio).
    """
    with telemetry.span("audio.decode", "decode"):
        audio = whisperx.load_audio(audio_path)

    with telemetry.span("ai.asr", "asr"):
        asr = _asr(audio, language)
    language = asr["language"]

    with telemetry.span("ai.align", "align"):
        aligned = _get_align(language)
        if aligned is None:
            result = {"segments": asr["segments"]}
        else:
            align_model, metadata = aligned
            result = whisperx.align(asr["segments"], align_model, metadata, audio, config.DEVICE,
                                    return_char_alignments=False)

    with telemetry.span("ai.diarize", "diarize"):
        diarize_segments = _diarize(audio, None, None)
        result = whisperx.assign_word_speakers(diarize_segments, result)

    with telemetry.span("ai.shape", "shape"):
        segments = _shape_segments(result["segments"])

    with telemetry.span("ai.embeddings", "embeddings"):
        speakers = _extract_speakers(audio, segments)

    segments = _trim_to_window(segments, overlap_ms)
    segments = _offset_segments(segments, offset_ms, overlap_ms)
    return {"language": language, "segments": segments, "speakers": speakers}


def _trim_to_window(segments: list[dict], overlap_ms: float) -> list[dict]:
    """Drop segments that belong entirely to the prepended overlap.

    A live chunk is decoded with the tail of the previous one in front of it, so Whisper does not start
    mid-sentence. Everything wholly inside that prepended audio was already reported by the previous
    chunk; keeping it would repeat a sentence at every chunk boundary, which reads as a transcription
    bug rather than an overlap one.

    A segment that *straddles* the boundary is kept: it started in the overlap but finishes in this
    chunk, so the previous chunk ended before it did and nobody else reports those words.
    """
    if overlap_ms <= 0:
        return segments
    return [s for s in segments if s["EndMs"] > overlap_ms]


def _offset_segments(segments: list[dict], offset_ms: float, overlap_ms: float) -> list[dict]:
    """Shift chunk-relative times into recording time.

    `offset_ms` is where this chunk starts in the recording. The decoded window began `overlap_ms`
    earlier than that, so the prepended audio has to be subtracted back out or every segment lands late
    by the length of the overlap.
    """
    shift = offset_ms - overlap_ms
    return [
        {**s, "StartMs": s["StartMs"] + shift, "EndMs": s["EndMs"] + shift}
        for s in segments
    ]


def transcribe(audio_path: str, min_speakers=None, max_speakers=None, language=None) -> dict:
    """Run transcription -> alignment -> diarization -> per-speaker embeddings.
    Returns {language, segments, speakers, duration_ms}. min/max_speakers are optional pyannote hints;
    `language` pins the spoken language (None = let Whisper detect it)."""
    # 0. Decode to a 16 kHz mono waveform. This shells out to ffmpeg and is the slowest single
    # stage on a long upload, so it gets its own span - without it the stage timings below do not
    # add up to the job's wall-clock time and a slow job looks unaccounted for.
    with telemetry.span("audio.decode", "decode"):
        audio = whisperx.load_audio(audio_path)

    duration_ms = _duration_ms(audio)
    if _too_long(duration_ms, config.MAX_AUDIO_SECONDS):
        raise ValueError(
            f"Audio is too long ({duration_ms // 1000}s); the limit is {int(config.MAX_AUDIO_SECONDS)}s.")

    # 1. Transcribe (backend-pluggable: faster-whisper on CUDA, openai-whisper on AMD ROCm)
    with telemetry.span("ai.asr", "asr"):
        asr = _asr(audio, language)
    language = asr["language"]

    # 2. Word-level alignment. Optional: a language whisperx has no align model for keeps the ASR's own
    # segment timings rather than failing the job. Nothing downstream needs the words - _shape_segments
    # stores segment-level start/end/text/speaker and discards word data even when alignment ran, and
    # assign_word_speakers guards its word loop with `if 'words' in seg`, so speakers are still assigned
    # per segment. The cost is segment boundaries that are a little less precise.
    with telemetry.span("ai.align", "align"):
        aligned = _get_align(language)
        if aligned is None:
            result = {"segments": asr["segments"]}
        else:
            align_model, metadata = aligned
            result = whisperx.align(
                asr["segments"], align_model, metadata, audio, config.DEVICE,
                return_char_alignments=False)

    # 3. Diarization (with optional speaker-count hints) + speaker assignment
    with telemetry.span("ai.diarize", "diarize"):
        diarize_segments = _diarize(audio, min_speakers, max_speakers)
        result = whisperx.assign_word_speakers(diarize_segments, result)

    # 3b. Reshape into the callback's segment contract (walks every word of the transcript).
    with telemetry.span("ai.shape", "shape"):
        segments = _shape_segments(result["segments"])

    # 4. Per-speaker voiceprint embeddings (for identification against enrolled people)
    with telemetry.span("ai.embeddings", "embeddings"):
        speakers = _extract_speakers(audio, segments)

    return {"language": language, "segments": segments, "speakers": speakers, "duration_ms": duration_ms}
