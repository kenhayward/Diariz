"""The Diariz upload client.

Mirrors POST /api/recordings (src/Diariz.Api/Controllers/RecordingsController.cs): a
multipart form with `audio`, `title`, `durationMs`, `source=Upload`, and optional
`startedAt` / `endedAt`. Auth is `Authorization: Bearer <token>` - either a personal
`dz_api_...` token (preferred for scripts) or a session JWT from /api/auth/login.

No network in these tests: the client takes a session object, so a fake records what it
was asked to send.
"""

from datetime import datetime, timedelta, timezone

import pytest

from omi_sync.diariz import DiarizClient, UploadError


class FakeResponse:
    def __init__(self, status=200, payload=None, text=""):
        self.status_code = status
        self._payload = payload if payload is not None else {}
        self.text = text

    def json(self):
        return self._payload


class FakeSession:
    """Records calls instead of making them."""

    def __init__(self, *responses):
        self.responses = list(responses) or [FakeResponse()]
        self.calls = []
        self.headers = {}

    def post(self, url, **kwargs):
        self.calls.append(("POST", url, kwargs))
        return self.responses.pop(0) if len(self.responses) > 1 else self.responses[0]


STARTED = datetime(2026, 8, 25, 9, 30, tzinfo=timezone.utc)


def test_upload_posts_to_the_recordings_endpoint():
    session = FakeSession(FakeResponse(payload={"recordingId": "abc"}))
    client = DiarizClient("https://diariz.example.com", token="dz_api_x", session=session)

    client.upload(b"OggS-data", filename="s1.opus", title="Session 1",
                  started_at=STARTED, duration_ms=60_000)

    _, url, _ = session.calls[0]
    assert url == "https://diariz.example.com/api/recordings"


def test_base_url_trailing_slash_is_tolerated():
    session = FakeSession()
    client = DiarizClient("https://diariz.example.com/", token="t", session=session)
    client.upload(b"x", filename="a.opus", title="t", started_at=STARTED, duration_ms=1)
    assert session.calls[0][1] == "https://diariz.example.com/api/recordings"


def test_upload_sends_the_required_form_fields():
    session = FakeSession()
    client = DiarizClient("https://d.example", token="t", session=session)

    client.upload(b"OggS-data", filename="s1.opus", title="Standup",
                  started_at=STARTED, duration_ms=90_000)

    data = session.calls[0][2]["data"]
    assert data["source"] == "Upload"
    assert data["title"] == "Standup"
    assert data["durationMs"] == 90_000


def test_upload_sends_started_and_ended_derived_from_duration():
    session = FakeSession()
    client = DiarizClient("https://d.example", token="t", session=session)

    client.upload(b"x", filename="s.opus", title="t",
                  started_at=STARTED, duration_ms=90_000)

    data = session.calls[0][2]["data"]
    assert data["startedAt"] == STARTED.isoformat()
    assert data["endedAt"] == (STARTED + timedelta(milliseconds=90_000)).isoformat()


def test_upload_sends_the_audio_as_a_multipart_file():
    session = FakeSession()
    client = DiarizClient("https://d.example", token="t", session=session)

    client.upload(b"OggS-data", filename="s1.opus", title="t",
                  started_at=STARTED, duration_ms=1)

    name, (fname, payload, content_type) = next(iter(session.calls[0][2]["files"].items()))
    assert name == "audio"
    assert fname == "s1.opus"
    assert payload == b"OggS-data"
    assert content_type == "audio/ogg"


def test_bearer_token_is_set_on_the_session():
    session = FakeSession()
    DiarizClient("https://d.example", token="dz_api_secret", session=session)
    assert session.headers["Authorization"] == "Bearer dz_api_secret"


def test_upload_returns_the_recording_id():
    session = FakeSession(FakeResponse(payload={"recordingId": "3f2b", "name": "Standup"}))
    client = DiarizClient("https://d.example", token="t", session=session)

    got = client.upload(b"x", filename="s.opus", title="t",
                        started_at=STARTED, duration_ms=1)
    assert got == "3f2b"


@pytest.mark.parametrize("status,hint", [
    (401, "token"),
    (413, "too large"),
    (415, "format"),
])
def test_upload_failures_raise_with_an_actionable_message(status, hint):
    session = FakeSession(FakeResponse(status=status, text="server said no"))
    client = DiarizClient("https://d.example", token="t", session=session)

    with pytest.raises(UploadError) as err:
        client.upload(b"x", filename="s.opus", title="t",
                      started_at=STARTED, duration_ms=1)
    assert hint in str(err.value).lower()


def test_unexpected_error_status_still_raises():
    session = FakeSession(FakeResponse(status=500, text="boom"))
    client = DiarizClient("https://d.example", token="t", session=session)
    with pytest.raises(UploadError):
        client.upload(b"x", filename="s.opus", title="t",
                      started_at=STARTED, duration_ms=1)


def test_login_exchanges_credentials_for_a_token():
    session = FakeSession(FakeResponse(payload={"token": "jwt-abc", "expires": "2026-08-26T00:00:00Z"}))
    token = DiarizClient.login("https://d.example", "ken@example.com", "pw", session=session)

    method, url, kwargs = session.calls[0]
    assert (method, url) == ("POST", "https://d.example/api/auth/login")
    assert kwargs["json"] == {"email": "ken@example.com", "password": "pw"}
    assert token == "jwt-abc"


def test_login_failure_raises():
    session = FakeSession(FakeResponse(status=401, text="nope"))
    with pytest.raises(UploadError):
        DiarizClient.login("https://d.example", "ken@example.com", "bad", session=session)
