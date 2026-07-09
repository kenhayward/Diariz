"""Tests for the worker liveness heartbeat (the Docker healthcheck reads it)."""
import os

import heartbeat


def test_beat_then_is_healthy(tmp_path):
    path = str(tmp_path / "hb")
    heartbeat.beat(path)
    assert os.path.exists(path)
    assert heartbeat.is_healthy(path=path, max_age=30.0)


def test_missing_file_is_unhealthy(tmp_path):
    path = str(tmp_path / "does-not-exist")
    assert not heartbeat.is_healthy(path=path, max_age=30.0)


def test_stale_file_is_unhealthy(tmp_path):
    path = str(tmp_path / "hb")
    heartbeat.beat(path)
    # Evaluate freshness from far in the future so the just-written beat looks stale.
    future = os.path.getmtime(path) + 10_000
    assert not heartbeat.is_healthy(path=path, now=future, max_age=30.0)


def test_fresh_within_max_age_is_healthy(tmp_path):
    path = str(tmp_path / "hb")
    heartbeat.beat(path)
    written = os.path.getmtime(path)
    assert heartbeat.is_healthy(path=path, now=written + 5, max_age=30.0)


def test_start_launches_a_daemon_thread_that_beats(tmp_path):
    path = str(tmp_path / "hb")
    t = heartbeat.start(interval=0.01, path=path)
    assert t.daemon
    # The initial beat happens synchronously in start(), so the file exists immediately.
    assert heartbeat.is_healthy(path=path, max_age=30.0)
