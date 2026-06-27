"""WhisperX transcription + pyannote diarization pipeline.

Models are loaded lazily once and reused across jobs (loading large-v3 + pyannote
is expensive). Produces a list of speaker-attributed, timestamped segments.
"""
import logging

import numpy as np
import whisperx

from config import config

log = logging.getLogger("pipeline")

_whisper_model = None
_align_cache = {}  # language_code -> (model, metadata)
_diarize_model = None
_embedder = None


def _get_whisper():
    global _whisper_model
    if _whisper_model is None:
        log.info("Loading Whisper model %s on %s (%s)",
                 config.WHISPER_MODEL, config.DEVICE, config.COMPUTE_TYPE)
        _whisper_model = whisperx.load_model(
            config.WHISPER_MODEL, config.DEVICE, compute_type=config.COMPUTE_TYPE)
    return _whisper_model


def _get_align(language_code: str):
    if language_code not in _align_cache:
        _align_cache[language_code] = whisperx.load_align_model(
            language_code=language_code, device=config.DEVICE)
    return _align_cache[language_code]


def _get_diarizer():
    global _diarize_model
    if _diarize_model is None:
        if not config.HF_TOKEN:
            raise RuntimeError(
                "HF_TOKEN is required for pyannote diarization. Set it and accept the "
                "pyannote/speaker-diarization-3.1 model terms on Hugging Face.")
        _diarize_model = whisperx.DiarizationPipeline(
            use_auth_token=config.HF_TOKEN, device=config.DEVICE)
    return _diarize_model


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


def _shape_segments(raw_segments: list[dict]) -> list[dict]:
    """Convert whisperx segments to the API's contract: PascalCase keys, seconds -> ms,
    empty-text segments dropped, missing speaker defaulted to UNKNOWN."""
    segments = []
    for seg in raw_segments:
        text = (seg.get("text") or "").strip()
        if not text:
            continue
        segments.append({
            "Speaker": seg.get("speaker", "UNKNOWN"),
            "StartMs": int(round(seg["start"] * 1000)),
            "EndMs": int(round(seg["end"] * 1000)),
            "Text": text,
        })
    return segments


def transcribe(audio_path: str) -> dict:
    """Run transcription -> alignment -> diarization -> per-speaker embeddings.
    Returns {language, segments, speakers}."""
    audio = whisperx.load_audio(audio_path)

    # 1. Transcribe
    result = _get_whisper().transcribe(audio, batch_size=config.BATCH_SIZE)
    language = result.get("language", "en")

    # 2. Word-level alignment
    align_model, metadata = _get_align(language)
    result = whisperx.align(
        result["segments"], align_model, metadata, audio, config.DEVICE,
        return_char_alignments=False)

    # 3. Diarization + speaker assignment
    diarize_segments = _get_diarizer()(audio)
    result = whisperx.assign_word_speakers(diarize_segments, result)

    segments = _shape_segments(result["segments"])

    # 4. Per-speaker voiceprint embeddings (for identification against enrolled people)
    speakers = _extract_speakers(audio, segments)

    return {"language": language, "segments": segments, "speakers": speakers}
