"""Optional error + performance reporting to a Sentry-compatible endpoint (GlitchTip).

Completely inert unless SENTRY_DSN is set: the SDK is imported lazily inside init(), so a
deployment without a DSN pays nothing and the test suite never needs sentry_sdk installed.

This module is the ONLY place that knows about sentry_sdk. worker.py and pipeline.py call the
context managers below, which are no-ops when reporting is off.
"""
import logging
import os
from contextlib import contextmanager

import requests

from config import config

log = logging.getLogger("telemetry")

# Set by init(). The context managers below check it rather than importing sentry_sdk, so a worker
# with no DSN never loads the SDK at all.
_enabled = False

REDACTED = "[redacted]"

# Exact key names that carry meeting content. Transcripts are this application's payload; an event
# that leaks one cannot be un-sent.
_DENY_EXACT = frozenset({
    "text", "transcript", "transcription", "segments", "words", "summary",
    "minutes", "content", "authorization", "cookie", "cookies",
    # ECAPA voiceprint vectors (biometric data identifying a speaker by voice).
    "embedding", "embeddings",
})

# Substrings that mark a credential regardless of the surrounding name.
_DENY_SUBSTRING = ("secret", "token", "password", "api_key", "apikey", "access_key")


def is_sensitive_key(key: str) -> bool:
    """True when a field with this name must never leave the process."""
    lowered = str(key).lower()
    if lowered in _DENY_EXACT:
        return True
    return any(marker in lowered for marker in _DENY_SUBSTRING)


def scrub(obj):
    """Recursively redact sensitive values. Pure: returns a new structure, never mutates the input."""
    if isinstance(obj, dict):
        return {k: (REDACTED if is_sensitive_key(k) else scrub(v)) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [scrub(item) for item in obj]
    return obj


def _before_send(event, hint):
    """SDK hook. Scrubs every outgoing event and transaction."""
    return scrub(event)


def release() -> str | None:
    """The platform version this worker serves, read from the API's /health.

    The worker image carries no version of its own - version.json lives at the repo root, outside the
    worker's build context - and hard-coding one in .env would drift silently the moment someone
    forgot to bump it. The API already reports the canonical version, and the worker already waits
    for the API to be healthy before it starts.
    """
    try:
        return requests.get(f"{config.API_BASE_URL}/health", timeout=5).json().get("version")
    except Exception:  # noqa: BLE001 - a missing release tag must never stop the worker starting
        log.debug("Could not read the platform version from the API", exc_info=True)
        return None


def init() -> bool:
    """Start reporting if SENTRY_DSN is set. Returns whether reporting is on."""
    global _enabled

    dsn = os.getenv("SENTRY_DSN", "").strip()
    if not dsn:
        return False

    import sentry_sdk

    sentry_sdk.init(
        dsn=dsn,
        environment=os.getenv("SENTRY_ENVIRONMENT", "development"),
        release=release(),
        traces_sample_rate=float(os.getenv("SENTRY_TRACES_SAMPLE_RATE", "1.0")),
        # Never attach request bodies, headers or user identifiers automatically.
        send_default_pii=False,
        before_send=_before_send,
        before_send_transaction=_before_send,
    )
    _enabled = True
    log.info("Error reporting enabled")
    return True


@contextmanager
def transaction(name: str, op: str = "queue.task"):
    """Wrap one unit of work as a reportable transaction. A no-op when reporting is off."""
    if not _enabled:
        yield
        return
    import sentry_sdk
    with sentry_sdk.start_transaction(op=op, name=name):
        yield


@contextmanager
def span(op: str, name: str):
    """Time one stage inside the current transaction. A no-op when reporting is off."""
    if not _enabled:
        yield
        return
    import sentry_sdk
    with sentry_sdk.start_span(op=op, name=name):
        yield


def capture_exception(error: BaseException) -> None:
    """Report an exception that the worker has caught and handled. A no-op when reporting is off."""
    if not _enabled:
        return
    import sentry_sdk
    sentry_sdk.capture_exception(error)
