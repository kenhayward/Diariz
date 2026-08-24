"""Post transcription results/failures back to the .NET API internal endpoint."""
import logging

import requests

from config import config

log = logging.getLogger("callback")

_HEADERS = {"X-Worker-Secret": config.CALLBACK_SECRET}


def post_result(transcription_id: str, language: str, segments: list[dict],
                speakers: list[dict] | None = None, duration_ms: int | None = None,
                processing_ms: int | None = None) -> None:
    url = f"{config.API_BASE_URL}/internal/transcriptions/result"
    body = {
        "TranscriptionId": transcription_id,
        "Language": language,
        "Segments": segments,
        "Speakers": speakers or [],
        "DurationMs": duration_ms,
        # Wall-clock time the full pipeline took (download + transcribe + diarize + embed).
        "ProcessingMs": processing_ms,
    }
    resp = requests.post(url, json=body, headers=_HEADERS, timeout=60)
    resp.raise_for_status()
    log.info("Posted %d segments, %d speaker embeddings for transcription %s",
             len(segments), len(speakers or []), transcription_id)


def post_failure(transcription_id: str, error: str) -> None:
    url = f"{config.API_BASE_URL}/internal/transcriptions/failure"
    body = {"TranscriptionId": transcription_id, "Error": error[:2000]}
    try:
        requests.post(url, json=body, headers=_HEADERS, timeout=30).raise_for_status()
    except Exception:  # noqa: BLE001 - best-effort failure reporting
        log.exception("Failed to report failure for %s", transcription_id)


def post_merge_result(recording_id: str, blob_key: str, content_type: str,
                      size_bytes: int, duration_ms: int, delete_recording_ids: list[str]) -> None:
    """Report a finished audio-merge: the combined blob + the source ids the API should now delete."""
    url = f"{config.API_BASE_URL}/internal/recordings/merge-result"
    body = {
        "RecordingId": recording_id,
        "BlobKey": blob_key,
        "ContentType": content_type,
        "SizeBytes": size_bytes,
        "DurationMs": duration_ms,
        "DeleteRecordingIds": delete_recording_ids,
    }
    resp = requests.post(url, json=body, headers=_HEADERS, timeout=60)
    resp.raise_for_status()
    log.info("Posted merge result for recording %s (blob=%s)", recording_id, blob_key)


def post_merge_failure(recording_id: str, error: str) -> None:
    url = f"{config.API_BASE_URL}/internal/recordings/merge-failure"
    body = {"RecordingId": recording_id, "Error": error[:2000]}
    try:
        requests.post(url, json=body, headers=_HEADERS, timeout=30).raise_for_status()
    except Exception:  # noqa: BLE001 - best-effort failure reporting
        log.exception("Failed to report merge failure for %s", recording_id)

def post_voiceprint_result(voice_sample_id: str, embedding: list, used_ms: int, selected_ms: int) -> None:
    """Report a finished voiceprint re-embed: the new vector, and how much audio went into it against how
    much was selected (the cap can make those differ, and the UI states both).

    Raises on a failed POST, unlike the failure path below: if the result cannot be delivered the caller
    must find out, or the sample sits pending forever with a vector nobody stored."""
    url = f"{config.API_BASE_URL}/internal/people/voiceprint-result"
    body = {
        "VoiceSampleId": voice_sample_id,
        "Embedding": embedding,
        "UsedMs": used_ms,
        "SelectedMs": selected_ms,
    }
    resp = requests.post(url, json=body, headers=_HEADERS, timeout=60)
    resp.raise_for_status()
    log.info("Posted voiceprint result for sample %s (used=%dms)", voice_sample_id, used_ms)


def post_voiceprint_failure(voice_sample_id: str, error: str) -> None:
    """Best-effort, like the other failure reporters: raising here would take down the consumer loop over
    a job that had already failed."""
    url = f"{config.API_BASE_URL}/internal/people/voiceprint-failure"
    body = {"VoiceSampleId": voice_sample_id, "Error": error[:2000]}
    try:
        requests.post(url, json=body, headers=_HEADERS, timeout=30).raise_for_status()
    except Exception:  # noqa: BLE001 - best-effort failure reporting
        log.exception("Failed to report voiceprint failure for %s", voice_sample_id)
