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

# XREADGROUP blocks server-side for this long per poll (ms). The socket read timeout is set a little
# larger than this so a normal empty poll never trips it — only a genuinely unreachable Redis does.
BLOCK_MS = 5000
RECONNECT_DELAY = 2  # seconds to back off after a Redis timeout/disconnect before retrying


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
    started = time.monotonic()
    try:
        audio_path = storage.download(blob_key)
        result = pipeline.transcribe(audio_path, job.get("MinSpeakers"), job.get("MaxSpeakers"))
        # Full-pipeline wall-clock time (download + transcribe + diarize + embed), reported to the API.
        processing_ms = int((time.monotonic() - started) * 1000)
        callback.post_result(transcription_id, result["language"], result["segments"],
                             result.get("speakers"), result.get("duration_ms"), processing_ms)
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


def run_loop(r: redis.Redis, keep_going=lambda: True) -> None:
    """Consume jobs until stopped. A long-running blocking consumer must survive transient Redis hiccups:
    a socket read timeout or a dropped connection (e.g. Redis restart) is caught and retried rather than
    crashing the worker. ``keep_going`` is a test seam; production runs forever."""
    while keep_going():
        try:
            resp = r.xreadgroup(
                config.CONSUMER_GROUP, config.CONSUMER_NAME,
                {config.STREAM_KEY: ">", config.MERGE_STREAM_KEY: ">"}, count=1, block=BLOCK_MS)
        except (redis.TimeoutError, redis.ConnectionError) as e:
            log.warning("Redis unavailable (%s); retrying in %ds", e, RECONNECT_DELAY)
            time.sleep(RECONNECT_DELAY)
            continue
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


def main() -> None:
    # Restore pre-2.6 torch.load behaviour before any model checkpoint is loaded
    # (pyannote/whisperx checkpoints fail under torch>=2.6's weights_only=True).
    torch_compat.restore_legacy_torch_load()

    # socket_timeout > BLOCK_MS so a normal blocking poll never trips it; socket_keepalive detects a
    # silently-dropped connection. (redis-py 8 otherwise lets a blocking XREADGROUP surface a socket
    # read timeout, which used to crash the worker on its very first poll.)
    r = redis.Redis.from_url(
        config.REDIS_URL, decode_responses=True,
        socket_timeout=BLOCK_MS / 1000 + 5, socket_keepalive=True)
    while True:
        try:
            r.ping()
            break
        except (redis.ConnectionError, redis.TimeoutError):
            log.info("Waiting for Redis at %s ...", config.REDIS_URL)
            time.sleep(2)

    ensure_group(r, config.STREAM_KEY)
    ensure_group(r, config.MERGE_STREAM_KEY)
    log.info("Worker %s listening on streams %s, %s",
             config.CONSUMER_NAME, config.STREAM_KEY, config.MERGE_STREAM_KEY)

    run_loop(r)


if __name__ == "__main__":
    main()
