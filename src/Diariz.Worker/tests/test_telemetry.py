"""Tests for the telemetry scrubber. This is the code whose bugs leak customer data, so it is
tested directly rather than through the SDK."""
import telemetry


def test_is_sensitive_key_matches_secrets_case_insensitively():
    assert telemetry.is_sensitive_key("HF_TOKEN")
    assert telemetry.is_sensitive_key("callback_secret")
    assert telemetry.is_sensitive_key("S3_SECRET_KEY")
    assert telemetry.is_sensitive_key("Authorization")
    assert telemetry.is_sensitive_key("password")


def test_is_sensitive_key_matches_content_bearing_fields():
    assert telemetry.is_sensitive_key("text")
    assert telemetry.is_sensitive_key("transcript")
    assert telemetry.is_sensitive_key("segments")
    assert telemetry.is_sensitive_key("summary")


def test_is_sensitive_key_keeps_the_identifiers_needed_to_diagnose():
    # A scrubber that redacts everything is useless. These must survive.
    assert not telemetry.is_sensitive_key("transcription_id")
    assert not telemetry.is_sensitive_key("recording_id")
    assert not telemetry.is_sensitive_key("blob_key")
    assert not telemetry.is_sensitive_key("model")
    assert not telemetry.is_sensitive_key("language")


def test_scrub_redacts_nested_values_and_preserves_ids():
    event = {
        "extra": {
            "transcription_id": "tid-1",
            "blob_key": "user/abc.wav",
            "text": "the confidential meeting content",
        },
        "request": {"headers": {"Authorization": "Bearer abc", "Accept": "application/json"}},
    }

    cleaned = telemetry.scrub(event)

    assert cleaned["extra"]["transcription_id"] == "tid-1"
    assert cleaned["extra"]["blob_key"] == "user/abc.wav"
    assert cleaned["extra"]["text"] == telemetry.REDACTED
    assert cleaned["request"]["headers"]["Authorization"] == telemetry.REDACTED
    assert cleaned["request"]["headers"]["Accept"] == "application/json"


def test_scrub_walks_into_lists():
    event = {"segments": [{"text": "secret words"}], "breadcrumbs": [{"message": "ok", "text": "leak"}]}

    cleaned = telemetry.scrub(event)

    assert cleaned["segments"] == telemetry.REDACTED
    assert cleaned["breadcrumbs"][0]["message"] == "ok"
    assert cleaned["breadcrumbs"][0]["text"] == telemetry.REDACTED


def test_scrub_does_not_mutate_the_input():
    event = {"extra": {"text": "content"}}

    telemetry.scrub(event)

    assert event["extra"]["text"] == "content"


def test_is_sensitive_key_redacts_biometric_voiceprints_but_keeps_speaker_labels():
    # Embedding and embeddings are ECAPA voiceprint vectors (biometric data).
    assert telemetry.is_sensitive_key("Embedding")
    assert telemetry.is_sensitive_key("embeddings")
    # Speaker labels like SPEAKER_00 are diagnostic metadata with no personal info.
    assert not telemetry.is_sensitive_key("Speaker")
    assert not telemetry.is_sensitive_key("speakers")


def test_scrub_redacts_voiceprints_but_keeps_speaker_labels():
    event = {"extra": {"Speakers": [{"Speaker": "SPEAKER_00", "Embedding": [0.1, 0.2]}]}}

    cleaned = telemetry.scrub(event)

    speaker = cleaned["extra"]["Speakers"][0]
    assert speaker["Speaker"] == "SPEAKER_00"
    assert speaker["Embedding"] == telemetry.REDACTED


import sys
from unittest.mock import MagicMock

import pytest


def test_init_returns_false_and_does_nothing_when_dsn_is_empty(monkeypatch):
    monkeypatch.setenv("SENTRY_DSN", "")
    # If init tried to use the SDK with no DSN this would raise, proving the guard ran first.
    monkeypatch.setitem(sys.modules, "sentry_sdk", None)

    assert telemetry.init() is False


def test_init_returns_false_when_dsn_is_only_whitespace(monkeypatch):
    monkeypatch.setenv("SENTRY_DSN", "   ")
    monkeypatch.setitem(sys.modules, "sentry_sdk", None)

    assert telemetry.init() is False


def test_init_configures_the_sdk_with_pii_off_and_the_scrubber(monkeypatch):
    # init() sets the module-global _enabled directly (not via monkeypatch), so without this the flag
    # would leak True into every test that runs afterward and they'd try to import the real sentry_sdk.
    monkeypatch.setattr(telemetry, "_enabled", False)
    fake_sdk = MagicMock()
    monkeypatch.setitem(sys.modules, "sentry_sdk", fake_sdk)
    monkeypatch.setenv("SENTRY_DSN", "https://key@errors.example/1")
    monkeypatch.setenv("SENTRY_ENVIRONMENT", "development")
    monkeypatch.setenv("SENTRY_TRACES_SAMPLE_RATE", "0.5")
    monkeypatch.setattr(telemetry, "release", lambda: "0.174.2")

    assert telemetry.init() is True

    kwargs = fake_sdk.init.call_args.kwargs
    assert kwargs["dsn"] == "https://key@errors.example/1"
    assert kwargs["send_default_pii"] is False
    assert kwargs["include_local_variables"] is False
    assert kwargs["before_send"] is telemetry._before_send
    assert kwargs["before_send_transaction"] is telemetry._before_send
    assert kwargs["traces_sample_rate"] == 0.5
    assert kwargs["environment"] == "development"
    assert kwargs["release"] == "0.174.2"


def test_before_send_scrubs_the_event():
    event = {"extra": {"text": "content", "transcription_id": "tid-1"}}

    cleaned = telemetry._before_send(event, None)

    assert cleaned["extra"]["text"] == telemetry.REDACTED
    assert cleaned["extra"]["transcription_id"] == "tid-1"


def test_release_reads_the_version_the_api_reports(monkeypatch):
    class FakeResponse:
        def json(self):
            return {"status": "ok", "version": "0.174.2"}

    monkeypatch.setattr(telemetry.requests, "get", lambda url, timeout: FakeResponse())

    assert telemetry.release() == "0.174.2"


def test_release_returns_none_when_the_api_is_unreachable(monkeypatch):
    def boom(url, timeout):
        raise OSError("connection refused")

    monkeypatch.setattr(telemetry.requests, "get", boom)

    assert telemetry.release() is None


def test_span_and_transaction_are_no_ops_when_reporting_is_off(monkeypatch):
    monkeypatch.setattr(telemetry, "_enabled", False)

    with telemetry.transaction("job"):
        with telemetry.span("op.stage", "stage"):
            pass  # must not raise, and must not need sentry_sdk


def test_span_delegates_to_the_sdk_when_enabled(monkeypatch):
    fake_sdk = MagicMock()
    monkeypatch.setitem(sys.modules, "sentry_sdk", fake_sdk)
    monkeypatch.setattr(telemetry, "_enabled", True)

    with telemetry.span("op.asr", "ASR"):
        pass

    fake_sdk.start_span.assert_called_once_with(op="op.asr", name="ASR")
