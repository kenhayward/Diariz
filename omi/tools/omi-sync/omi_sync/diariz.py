"""Upload a session to Diariz.

Mirrors POST /api/recordings (src/Diariz.Api/Controllers/RecordingsController.cs): a
multipart form with `audio`, `title`, `durationMs`, `source=Upload`, plus `startedAt` and
`endedAt` so the recording lands on the timeline where it actually happened.

Auth is `Authorization: Bearer <token>`. Prefer a personal API token (`dz_api_...` from
Settings -> Developers): it is longer-lived than a session JWT, scope-limited, and
revocable without changing your password. Note that a read-only token is rejected on
POST by ApiTokenScopeMiddleware, so the token needs write scope.
"""

from __future__ import annotations

from datetime import datetime, timedelta
from typing import Any, Optional


class UploadError(RuntimeError):
    """A Diariz request failed, with an explanation worth showing the operator."""


def _new_session() -> Any:
    import requests  # imported lazily so the pure modules need no dependencies

    return requests.Session()


def _explain(status: int, body: str) -> str:
    hints = {
        401: "rejected: check your API token (Settings -> Developers) or your credentials",
        403: "forbidden: a read-only API token cannot upload, mint one with write scope",
        413: "rejected: the file is too large for this server's upload limit",
        415: "rejected: the server did not recognise the audio format",
    }
    hint = hints.get(status, f"unexpected response ({status})")
    detail = body.strip()[:400]
    return f"Diariz {hint}." + (f" Server said: {detail}" if detail else "")


class DiarizClient:
    def __init__(self, base_url: str, token: str, session: Optional[Any] = None):
        self.base_url = base_url.rstrip("/")
        self.session = session if session is not None else _new_session()
        self.session.headers["Authorization"] = f"Bearer {token}"

    @staticmethod
    def login(base_url: str, email: str, password: str,
              session: Optional[Any] = None) -> str:
        """Exchange credentials for a session JWT. Prefer an API token where you can."""
        http = session if session is not None else _new_session()
        response = http.post(
            base_url.rstrip("/") + "/api/auth/login",
            json={"email": email, "password": password},
        )
        if response.status_code != 200:
            raise UploadError(_explain(response.status_code, getattr(response, "text", "")))
        return response.json()["token"]

    def upload(self, blob: bytes, *, filename: str, title: str,
               started_at: datetime, duration_ms: int) -> Optional[str]:
        """Upload one recording. Returns the new recording's id."""
        data = {
            "source": "Upload",
            "title": title,
            "durationMs": duration_ms,
            "startedAt": started_at.isoformat(),
            "endedAt": (started_at + timedelta(milliseconds=duration_ms)).isoformat(),
        }
        files = {"audio": (filename, blob, "audio/ogg")}

        response = self.session.post(self.base_url + "/api/recordings",
                                     data=data, files=files)
        if response.status_code >= 400:
            raise UploadError(_explain(response.status_code, getattr(response, "text", "")))

        try:
            return response.json().get("recordingId")
        except Exception:                                  # noqa: BLE001 - body is optional
            return None
