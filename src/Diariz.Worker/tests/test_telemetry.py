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
