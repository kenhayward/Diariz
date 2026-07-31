"""Optional error + performance reporting to a Sentry-compatible endpoint (GlitchTip).

Completely inert unless SENTRY_DSN is set: the SDK is imported lazily inside init(), so a
deployment without a DSN pays nothing and the test suite never needs sentry_sdk installed.

This module is the ONLY place that knows about sentry_sdk. worker.py and pipeline.py call the
context managers below, which are no-ops when reporting is off.
"""

REDACTED = "[redacted]"

# Exact key names that carry meeting content. Transcripts are this application's payload; an event
# that leaks one cannot be un-sent.
_DENY_EXACT = frozenset({
    "text", "transcript", "transcription", "segments", "words", "summary",
    "minutes", "content", "authorization", "cookie", "cookies",
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
