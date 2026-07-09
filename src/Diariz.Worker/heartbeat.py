"""Liveness heartbeat for the transcription worker.

The worker has no HTTP server, so its Docker healthcheck can't hit a URL. Instead a daemon thread
touches a heartbeat file every few seconds; ``healthcheck.py`` (run by Docker HEALTHCHECK) checks the
file is recent. The thread ticks independently of the job loop, so a worker busy transcribing a long
recording still reports healthy - it only goes stale when the process crashes or hard-hangs.
"""
import os
import threading
import time

# Overridable so the healthcheck and the worker agree on the path (defaults line up with the compose file).
DEFAULT_PATH = os.environ.get("HEARTBEAT_FILE", "/tmp/diariz_worker_heartbeat")


def beat(path: str = DEFAULT_PATH) -> None:
    """Record a heartbeat by writing the current time to ``path`` (its mtime is what matters)."""
    with open(path, "w") as f:
        f.write(str(time.time()))


def is_healthy(path: str = DEFAULT_PATH, now: float | None = None, max_age: float = 30.0) -> bool:
    """True when the heartbeat file exists and was touched within ``max_age`` seconds of ``now``."""
    now = time.time() if now is None else now
    try:
        return (now - os.path.getmtime(path)) <= max_age
    except OSError:
        return False


def start(interval: float = 10.0, path: str = DEFAULT_PATH) -> threading.Thread:
    """Beat once now, then every ``interval`` seconds on a daemon thread. Returns the thread."""
    beat(path)

    def loop() -> None:
        while True:
            time.sleep(interval)
            try:
                beat(path)
            except OSError:
                pass  # a transient FS hiccup shouldn't kill the heartbeat thread

    t = threading.Thread(target=loop, name="heartbeat", daemon=True)
    t.start()
    return t
