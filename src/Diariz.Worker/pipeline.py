"""WhisperX transcription + pyannote diarization pipeline.

Models are loaded lazily once and reused across jobs (loading large-v3 + pyannote
is expensive). Produces a list of speaker-attributed, timestamped segments.
"""
import logging

import whisperx

from config import config

log = logging.getLogger("pipeline")

_whisper_model = None
_align_cache = {}  # language_code -> (model, metadata)
_diarize_model = None


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


def transcribe(audio_path: str) -> dict:
    """Run transcription -> alignment -> diarization. Returns {language, segments}."""
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

    segments = []
    for seg in result["segments"]:
        text = (seg.get("text") or "").strip()
        if not text:
            continue
        segments.append({
            "Speaker": seg.get("speaker", "UNKNOWN"),
            "StartMs": int(round(seg["start"] * 1000)),
            "EndMs": int(round(seg["end"] * 1000)),
            "Text": text,
        })

    return {"language": language, "segments": segments}
