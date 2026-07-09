"""Docker HEALTHCHECK entrypoint: exit 0 if the worker heartbeat is fresh, 1 otherwise.

Run as `python3 healthcheck.py`. Kept dependency-free (only the stdlib + heartbeat) so it stays fast.
"""
import sys

import heartbeat

sys.exit(0 if heartbeat.is_healthy() else 1)
