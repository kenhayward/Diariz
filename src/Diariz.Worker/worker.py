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

import callback
import pipeline
import storage
import torch_compat
from config import config

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(name)s %(message)s")
log = logging.getLogger("worker")


def ensure_group(r: redis.Redis) -> None:
    try:
        r.xgroup_create(config.STREAM_KEY, config.CONSUMER_GROUP, id="0", mkstream=True)
        log.info("Created consumer group %s on %s", config.CONSUMER_GROUP, config.STREAM_KEY)
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
        result = pipeline.transcribe(audio_path)
        callback.post_result(transcription_id, result["language"], result["segments"],
                             result.get("speakers"))
    except Exception as e:  # noqa: BLE001 - report and continue
        log.exception("Job failed for transcription %s", transcription_id)
        callback.post_failure(transcription_id, str(e))
    finally:
        if audio_path and os.path.exists(audio_path):
            os.remove(audio_path)


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

    ensure_group(r)
    log.info("Worker %s listening on stream %s", config.CONSUMER_NAME, config.STREAM_KEY)

    while True:
        resp = r.xreadgroup(
            config.CONSUMER_GROUP, config.CONSUMER_NAME,
            {config.STREAM_KEY: ">"}, count=1, block=5000)
        if not resp:
            continue
        for _stream, messages in resp:
            for msg_id, fields in messages:
                try:
                    job = json.loads(fields["job"])
                    handle(job)
                finally:
                    r.xack(config.STREAM_KEY, config.CONSUMER_GROUP, msg_id)


if __name__ == "__main__":
    main()
