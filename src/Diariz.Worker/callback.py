"""Post transcription results/failures back to the .NET API internal endpoint."""
import logging

import requests

from config import config

log = logging.getLogger("callback")

_HEADERS = {"X-Worker-Secret": config.CALLBACK_SECRET}


def post_result(transcription_id: str, language: str, segments: list[dict],
                speakers: list[dict] | None = None, duration_ms: int | None = None) -> None:
    url = f"{config.API_BASE_URL}/internal/transcriptions/result"
    body = {
        "TranscriptionId": transcription_id,
        "Language": language,
        "Segments": segments,
        "Speakers": speakers or [],
        "DurationMs": duration_ms,
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
