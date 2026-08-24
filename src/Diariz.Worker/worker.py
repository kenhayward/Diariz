"""Transcription worker: consumes jobs from a Redis Stream, runs the WhisperX +
pyannote pipeline, and posts results back to the API.

Job payload (Redis stream field "job") is JSON produced by the .NET API:
  { "RecordingId": "...", "TranscriptionId": "...", "BlobKey": "...", "Model": "...",
    "MinSpeakers": null, "MaxSpeakers": null, "Language": null }

``Language`` is the owner's pinned transcription language as a Whisper code ("en"), resolved by the
API from the recording's own override or the user's default; null means let Whisper detect it.
"""
import json
import logging
import os
import threading
import time

import redis

# MUST come before any import that pulls in torch (pipeline/audio_merge do): an empty
# HSA_OVERRIDE_GFX_VERSION - which the ROCm compose file and .env.example both produce by default -
# stops the ROCm runtime finding the GPU at all. See rocm_env for the measured before/after.
import rocm_env

rocm_env.clean_gfx_override()

import audio_merge  # noqa: E402
import callback  # noqa: E402
import heartbeat  # noqa: E402
import pipeline  # noqa: E402
import storage
import telemetry
import torch_compat
import voiceprint  # noqa: E402
from config import config

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(name)s %(message)s")
log = logging.getLogger("worker")

# XREADGROUP blocks server-side for this long per poll (ms). The socket read timeout is set a little
# larger than this so a normal empty poll never trips it — only a genuinely unreachable Redis does.
BLOCK_MS = 5000
RECONNECT_DELAY = 2  # seconds to back off after a Redis timeout/disconnect before retrying

# ---- Orphaned-job recovery ----
#
# XACK lives in a `finally`, which never runs when the process is *killed* rather than raising - a
# container restart mid-transcription, an OOM. The message then stays in the consumer group's pending
# list, and because the loop only reads ">" (new messages) nothing ever looks at it again: the recording
# sits in Transcribing forever and needs a manual re-transcribe. Reclaiming stale pending messages is
# what closes that.
#
# The threshold has to clear the longest *legitimate* job or a reclaim would steal work from a healthy
# worker and duplicate it on the GPU. Rather than set it to hours, the worker refreshes its own claim
# while it works (see refresh_claim / claim_keepalive), so "idle" really does mean "nobody is on it".
RECLAIM_MIN_IDLE_MS = int(os.getenv("RECLAIM_MIN_IDLE_MS", str(10 * 60 * 1000)))  # 10 minutes
RECLAIM_REFRESH_SECONDS = float(os.getenv("RECLAIM_REFRESH_SECONDS", "60"))
# Reclaiming reintroduces exactly the poison-message loop that acking-in-finally exists to prevent: a
# job that kills the worker would be picked up again by the next one, forever. Past this many deliveries
# the message is acked and abandoned instead, loudly.
RECLAIM_MAX_DELIVERIES = int(os.getenv("RECLAIM_MAX_DELIVERIES", "3"))


def refresh_claim(r: redis.Redis, stream_key: str, msg_id: str) -> None:
    """Reset a pending message's idle clock, marking it as still being worked on.

    ``min_idle_time=0`` re-claims it for this same consumer unconditionally - it is a keepalive, not a
    steal. Best-effort: a Redis hiccup here must not interrupt a transcription that is going fine."""
    try:
        r.xclaim(stream_key, config.CONSUMER_GROUP, config.CONSUMER_NAME,
                 min_idle_time=0, message_ids=[msg_id])
    except Exception:  # noqa: BLE001 - a failed keepalive is not worth losing the job over
        log.debug("Claim refresh failed for %s on %s", msg_id, stream_key, exc_info=True)


def claim_keepalive(r: redis.Redis, stream_key: str, msg_id: str) -> threading.Event:
    """Refresh the claim on ``msg_id`` every RECLAIM_REFRESH_SECONDS until the returned event is set."""
    done = threading.Event()

    def loop() -> None:
        while not done.wait(RECLAIM_REFRESH_SECONDS):
            refresh_claim(r, stream_key, msg_id)

    threading.Thread(target=loop, name=f"claim-keepalive-{msg_id}", daemon=True).start()
    return done


def reclaim_stale(r: redis.Redis, stream_key: str) -> list:
    """Take over messages left pending by a worker that died, returning them as (id, fields) to process.

    A message delivered more times than RECLAIM_MAX_DELIVERIES is acked and dropped rather than returned:
    it is far more likely to be the thing that killed the previous workers than a victim of them."""
    try:
        pending = r.xpending_range(stream_key, config.CONSUMER_GROUP, min="-", max="+", count=10,
                                   idle=RECLAIM_MIN_IDLE_MS)
    except Exception:  # noqa: BLE001 - recovery is opportunistic; never break the main loop over it
        log.debug("Could not read the pending list for %s", stream_key, exc_info=True)
        return []

    recovered = []
    for entry in pending or []:
        msg_id = entry["message_id"]
        if int(entry.get("times_delivered", 1)) > RECLAIM_MAX_DELIVERIES:
            log.error("Abandoning %s on %s after %s deliveries - it is likely the cause of the failures, "
                      "not a casualty of them", msg_id, stream_key, entry.get("times_delivered"))
            r.xack(stream_key, config.CONSUMER_GROUP, msg_id)
            continue
        log.warning("Reclaiming %s on %s, abandoned by %s after %s ms idle",
                    msg_id, stream_key, entry.get("consumer"), entry.get("time_since_delivered"))
        claimed = r.xclaim(stream_key, config.CONSUMER_GROUP, config.CONSUMER_NAME,
                           min_idle_time=RECLAIM_MIN_IDLE_MS, message_ids=[msg_id])
        recovered.extend(claimed or [])
    return recovered


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
        with telemetry.transaction("transcribe"):
            with telemetry.span("storage.download", "download"):
                audio_path = storage.download(blob_key)
            result = pipeline.transcribe(audio_path, job.get("MinSpeakers"), job.get("MaxSpeakers"),
                                         language=job.get("Language"))
            # Full-pipeline wall-clock time (download + transcribe + diarize + embed), reported to the API.
            processing_ms = int((time.monotonic() - started) * 1000)
            with telemetry.span("http.client", "callback"):
                callback.post_result(transcription_id, result["language"], result["segments"],
                                     result.get("speakers"), result.get("duration_ms"), processing_ms)
    except Exception as e:  # noqa: BLE001 - report and continue
        log.exception("Job failed for transcription %s", transcription_id)
        telemetry.capture_exception(e)
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
        with telemetry.transaction("audio-merge"):
            sources = [storage.download(k) for k in blob_keys]
            output_path, duration_ms, size_bytes = audio_merge.concat(sources)
            storage.upload(output_key, output_path, audio_merge.OUTPUT_CONTENT_TYPE)
            callback.post_merge_result(recording_id, output_key, audio_merge.OUTPUT_CONTENT_TYPE,
                                       size_bytes, duration_ms, delete_ids)
    except Exception as e:  # noqa: BLE001 - report and continue
        log.exception("Audio merge failed for recording %s", recording_id)
        telemetry.capture_exception(e)
        callback.post_merge_failure(recording_id, str(e))
    finally:
        for path in sources + ([output_path] if output_path else []):
            if path and os.path.exists(path):
                os.remove(path)


def _load_audio(path: str):
    """Decode an audio file to the 16 kHz mono waveform the embedder expects. A seam, so the voiceprint
    handler's failure path can be tested without whisperx."""
    return pipeline.whisperx.load_audio(path)


def handle_voiceprint(job: dict) -> None:
    """Re-embed one voice sample from the spans the user chose.

    Cheap next to a transcription - no Whisper, no pyannote - but it shares this process, so it can queue
    behind one. Failures are reported rather than raised: leaving the sample pending forever would be
    indistinguishable from a slow job.
    """
    sample_id = job["VoiceSampleId"]
    spans = job.get("Spans") or []
    log.info("Re-embedding voice sample %s from %d span(s)", sample_id, len(spans))

    path = None
    try:
        with telemetry.transaction("voiceprint-embed"):
            path = storage.download(job["BlobKey"])
            audio = _load_audio(path)
            result = voiceprint.embed_spans(audio, spans, pipeline._get_embedder())
            if result is None:
                callback.post_voiceprint_failure(sample_id, "The selected audio is empty.")
                return
            callback.post_voiceprint_result(
                sample_id, result["Embedding"], result["UsedMs"], result["SelectedMs"])
    except Exception as e:  # noqa: BLE001 - report and continue
        log.exception("Voiceprint re-embed failed for sample %s", sample_id)
        telemetry.capture_exception(e)
        callback.post_voiceprint_failure(sample_id, str(e))
    finally:
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
                {config.STREAM_KEY: ">", config.MERGE_STREAM_KEY: ">",
                 config.VOICEPRINT_STREAM_KEY: ">"}, count=1, block=BLOCK_MS)
        except (redis.TimeoutError, redis.ConnectionError) as e:
            log.warning("Redis unavailable (%s); retrying in %ds", e, RECONNECT_DELAY)
            time.sleep(RECONNECT_DELAY)
            continue
        # Nothing new: use the idle moment to pick up anything a dead worker left behind. Doing it here
        # rather than only at startup also recovers from another worker dying while this one runs.
        if not resp:
            resp = [(key, reclaim_stale(r, key))
                    for key in (config.STREAM_KEY, config.MERGE_STREAM_KEY,
                                config.VOICEPRINT_STREAM_KEY)]
            if not any(messages for _, messages in resp):
                continue

        for stream, messages in resp:
            for msg_id, fields in messages:
                # Keep the claim fresh for as long as this takes, so a long transcription is never
                # mistaken for an abandoned one and stolen by another worker.
                done = claim_keepalive(r, stream, msg_id)
                try:
                    job = json.loads(fields["job"])
                    if stream == config.MERGE_STREAM_KEY:
                        handle_merge(job)
                    elif stream == config.VOICEPRINT_STREAM_KEY:
                        handle_voiceprint(job)
                    else:
                        handle(job)
                finally:
                    done.set()
                    r.xack(stream, config.CONSUMER_GROUP, msg_id)


def main() -> None:
    # Restore pre-2.6 torch.load behaviour before any model checkpoint is loaded
    # (pyannote/whisperx checkpoints fail under torch>=2.6's weights_only=True).
    torch_compat.restore_legacy_torch_load()

    # Optional error/performance reporting. Inert unless SENTRY_DSN is set.
    telemetry.init()

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
    ensure_group(r, config.VOICEPRINT_STREAM_KEY)
    log.info("Worker %s listening on streams %s, %s, %s",
             config.CONSUMER_NAME, config.STREAM_KEY, config.MERGE_STREAM_KEY,
             config.VOICEPRINT_STREAM_KEY)

    # Start the liveness heartbeat (read by the Docker healthcheck) once we're up and consuming.
    heartbeat.start()

    run_loop(r)


if __name__ == "__main__":
    main()
