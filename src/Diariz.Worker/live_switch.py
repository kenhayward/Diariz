"""The platform's live-transcription switch, as the worker sees it.

An administrator switches live transcription off to take pressure off an overloaded server. The API is
the authority - it stops queueing chunks on its own - and mirrors the switch into one Redis key, because
Redis is all the worker reads. The worker uses it for the two things only it can do: drop chunks that
were already queued rather than work through them, and, in a live-only worker, free its copy of the models.
"""
import redis

from config import config


def is_enabled(r) -> bool:
    """Whether live chunks should be transcribed. On unless the API has written "0".

    A missing key (a fresh deployment, or a Redis that lost its data) and an unreadable one both count as
    on: the API is still refusing new chunks by itself, so the cost of guessing wrong is only that a few
    queued chunks get transcribed."""
    try:
        value = r.get(config.LIVE_TRANSCRIPTION_FLAG_KEY)
    except redis.RedisError:
        return True
    return value not in ("0", b"0")
