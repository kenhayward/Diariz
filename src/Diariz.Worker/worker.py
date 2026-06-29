"""Transcription worker: consumes jobs from a Redis Stream, runs the WhisperX +
pyannote pipeline, and posts results back to the API.

Job payload (Redis stream field "job") is JSON produced by the .NET API:
  { "RecordingId": "...", "TranscriptionId": "...", "BlobKey": "...", "Model": "..." }
"""
import json
import logging
import os
import time

import redis

import audio_merge
import callback
import pipeline
import storage
import torch_compat
from config import config

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(name)s %(message)s")
log = logging.getLogger("worker")


def ensure_group(r: redis.Redis, stream_key: str) -> None:
    try:
        r.xgroup_create(stream_key, config.CONSUMER_GROUP, id="0", mkstream=True)
        log.info("Created consumer group %s on %s", config.CONSUMER_GROUP, stream_key)
    except redis.ResponseError as e:
        if "BUSYGROUP" not in str(e):
            raise


def handle(job: dict) -> None:
    transcription_id = job["TranscriptionId"]
    blob_key = job["BlobKey"]
    log.info("Processing transcription %s (blob=%s, model=%s)",
             transcription_id, blob_key, job.get("Model"))

    audio_path = None
    try:
        audio_path = storage.download(blob_key)
        result = pipeline.transcribe(audio_path, job.get("MinSpeakers"), job.get("MaxSpeakers"))
        callback.post_result(transcription_id, result["language"], result["segments"],
                             result.get("speakers"), result.get("duration_ms"))
    except Exception as e:  # noqa: BLE001 - report and continue
        log.exception("Job failed for transcription %s", transcription_id)
        callback.post_failure(transcription_id, str(e))
    finally:
        if audio_path and os.path.exists(audio_path):
            os.remove(audio_path)


def handle_merge(job: dict) -> None:
    """Concatenate several recordings' audio into one and report back so the API can swap it onto the
    survivor and delete the merged sources."""
    recording_id = job["RecordingId"]
    blob_keys = job["BlobKeys"]
    output_key = job["OutputKey"]
    delete_ids = job.get("DeleteRecordingIds", [])
    log.info("Merging %d audio files into recording %s", len(blob_keys), recording_id)

    sources: list[str] = []
    output_path = None
    try:
        sources = [storage.download(k) for k in blob_keys]
        output_path, duration_ms, size_bytes = audio_merge.concat(sources)
        storage.upload(output_key, output_path, audio_merge.OUTPUT_CONTENT_TYPE)
        callback.post_merge_result(recording_id, output_key, audio_merge.OUTPUT_CONTENT_TYPE,
                                   size_bytes, duration_ms, delete_ids)
    except Exception as e:  # noqa: BLE001 - report and continue
        log.exception("Audio merge failed for recording %s", recording_id)
        callback.post_merge_failure(recording_id, str(e))
    finally:
        for path in sources + ([output_path] if output_path else []):
            if path and os.path.exists(path):
                os.remove(path)


def main() -> None:
    # Restore pre-2.6 torch.load behaviour before any model checkpoint is loaded
    # (pyannote/whisperx checkpoints fail under torch>=2.6's weights_only=True).
    torch_compat.restore_legacy_torch_load()

    r = redis.Redis.from_url(config.REDIS_URL, decode_responses=True)
    while True:
        try:
            r.ping()
            break
        except redis.ConnectionError:
            log.info("Waiting for Redis at %s ...", config.REDIS_URL)
            time.sleep(2)

    ensure_group(r, config.STREAM_KEY)
    ensure_group(r, config.MERGE_STREAM_KEY)
    log.info("Worker %s listening on streams %s, %s",
             config.CONSUMER_NAME, config.STREAM_KEY, config.MERGE_STREAM_KEY)

    while True:
        resp = r.xreadgroup(
            config.CONSUMER_GROUP, config.CONSUMER_NAME,
            {config.STREAM_KEY: ">", config.MERGE_STREAM_KEY: ">"}, count=1, block=5000)
        if not resp:
            continue
        for stream, messages in resp:
            for msg_id, fields in messages:
                try:
                    job = json.loads(fields["job"])
                    if stream == config.MERGE_STREAM_KEY:
                        handle_merge(job)
                    else:
                        handle(job)
                finally:
                    r.xack(stream, config.CONSUMER_GROUP, msg_id)


if __name__ == "__main__":
    main()
