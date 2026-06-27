"""Tests for the worker -> API callback contract (callback.py)."""
import pytest

import callback


class _OkResponse:
    def raise_for_status(self):
        pass


class _ErrorResponse:
    def raise_for_status(self):
        raise RuntimeError("HTTP 500")


def test_post_result_sends_pascalcase_body_and_secret_header(monkeypatch):
    captured = {}

    def fake_post(url, json=None, headers=None, timeout=None):
        captured.update(url=url, json=json, headers=headers, timeout=timeout)
        return _OkResponse()

    monkeypatch.setattr(callback.requests, "post", fake_post)

    segments = [{"Speaker": "SPEAKER_00", "StartMs": 0, "EndMs": 1000, "Text": "hi"}]
    callback.post_result("tid-1", "en", segments)

    assert captured["url"].endswith("/internal/transcriptions/result")
    # The .NET WorkerCallbackController binds these PascalCase keys exactly.
    assert captured["json"] == {
        "TranscriptionId": "tid-1",
        "Language": "en",
        "Segments": segments,
        "Speakers": [],
    }
    assert captured["headers"]["X-Worker-Secret"] == callback.config.CALLBACK_SECRET


def test_post_result_includes_speaker_embeddings(monkeypatch):
    captured = {}
    monkeypatch.setattr(
        callback.requests, "post",
        lambda url, json=None, headers=None, timeout=None: captured.update(json=json) or _OkResponse())

    speakers = [{"Speaker": "SPEAKER_00", "Embedding": [0.1, 0.2, 0.3]}]
    callback.post_result("tid-1", "en", [], speakers)

    assert captured["json"]["Speakers"] == speakers


def test_post_result_raises_on_http_error(monkeypatch):
    monkeypatch.setattr(callback.requests, "post", lambda *a, **k: _ErrorResponse())
    with pytest.raises(RuntimeError):
        callback.post_result("tid-1", "en", [])


def test_post_failure_truncates_error_to_2000_chars(monkeypatch):
    captured = {}

    def fake_post(url, json=None, headers=None, timeout=None):
        captured.update(json=json)
        return _OkResponse()

    monkeypatch.setattr(callback.requests, "post", fake_post)

    callback.post_failure("tid-9", "x" * 5000)

    assert captured["json"]["TranscriptionId"] == "tid-9"
    assert len(captured["json"]["Error"]) == 2000


def test_post_failure_swallows_exceptions(monkeypatch):
    # Failure reporting is best-effort and must never raise.
    def boom(*a, **k):
        raise ConnectionError("api down")

    monkeypatch.setattr(callback.requests, "post", boom)
    callback.post_failure("tid-9", "some error")  # no exception => pass
