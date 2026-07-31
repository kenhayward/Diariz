# Observability Phase 1 (worker + API) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up a self-hosted GlitchTip instance on the dev box and report unhandled exceptions plus stage/endpoint timings from the Python worker and the .NET API to it.

**Architecture:** GlitchTip (MIT, Sentry wire-compatible) runs as an optional Docker Compose overlay with its own Postgres, its own MinIO bucket, and a shared Redis on DB index 1. The worker and API use the official Sentry SDKs. A single module per runtime (`telemetry.py`, `SentryScrubber.cs`) owns all SDK knowledge so the rest of the code stays SDK-agnostic and testable without the SDK installed.

**Tech Stack:** GlitchTip 6.x, `sentry-sdk` (Python 2.x line), `Sentry.AspNetCore` (NuGet), Docker Compose, nginx.

**Design spec:** `docs/superpowers/specs/2026-07-31-observability-glitchtip-design.md`

## Global Constraints

- **Off by default.** Every integration is disabled when its DSN is empty - no SDK init, no network call. Mirrors the existing `Summarization__ApiBase` / `Dictation__ApiBase` convention.
- **`SendDefaultPii = false` / `send_default_pii=False`** on every SDK, always.
- **No transcript, summary, minutes or note content may ever leave the process.** Ids only.
- **TDD is required.** Write the failing test, run it, watch it fail, then implement. Task 1 is pure configuration and is the documented exception - it needs a human's sign-off instead.
- **No em dashes or en dashes** (`-` only) in release notes and any user-facing copy.
- **Every PR ships exactly one release:** bump `version.json` **and all four mirrors**, and add one `RELEASES[0]` entry. Current version at plan time: **0.174.0**.
- **Never commit or push to `main`.** Branch, push, open a PR.
- **Deployment surface for every PR in this plan: server redeploy, no desktop release.** State this in each PR description.
- **GlitchTip cold storage uses its own MinIO bucket, never a prefix inside `recordings`.** Platform restore wipes the recordings bucket.

## PR grouping

| PR | Tasks | Version | Sub-phase |
| --- | --- | --- | --- |
| 1 | Task 1 | 0.174.1 | 1a - infrastructure on dev |
| 2 | Tasks 2-5 | 0.174.2 | 1b - Python worker |
| 3 | Tasks 6-8 | 0.174.3 | 1c - API |
| - | Task 9 | none | 1d - prod rollout (operational, no code) |

**Getting the `pr:` number right:** `RELEASES[0].pr` must be the real PR number, and it is needed *before* `gh pr create` has told you what it is. Do not guess "last + 1" blindly - Dependabot PRs and issues share the sequence. Run `gh pr list --state all --limit 1` to see the highest number actually used, then use the next one, and correct it if `gh pr create` returns something different.

The design/plan PR took **#389**, so the expected numbers are **390** (PR 1), **391** (PR 2) and **392** (PR 3). Re-check before each one - anything else merged in between shifts them.

---

### Task 1: GlitchTip infrastructure overlay (sub-phase 1a, dev only)

Pure configuration and documentation. **No TDD** - this is the CLAUDE.md "pure config" exception and needs a human's sign-off that the stack came up correctly, per the verification steps below.

**Files:**
- Create: `deploy/docker-compose.observability.yml`
- Modify: `deploy/.env.example` (append the observability block)
- Modify: `docs/Overall_Synopsis_of_Platform.md` (new optional component)
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`

**Interfaces:**
- Consumes: nothing.
- Produces: two DSNs (worker + API), obtained from the GlitchTip UI and pasted into `deploy/.env` on the dev box. Consumed by Tasks 5 and 8 as `SENTRY_DSN` and `Sentry__Dsn`.

- [ ] **Step 1: Create the compose overlay**

Create `deploy/docker-compose.observability.yml`:

```yaml
# Optional observability overlay: self-hosted GlitchTip (error tracking + transaction timings).
#
# Opt-in, and deliberately a separate file so the core stack is unchanged for anyone who does not
# want it:
#   docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d
#
# GlitchTip gets its OWN Postgres (not a second database inside the app's pgvector container):
#   - the platform backup/restore operates on the app database; co-locating an observability tool's
#     tables entangles two things with different retention and recovery semantics,
#   - the postgres image only auto-creates POSTGRES_DB, and a second-database init script only runs
#     on a FRESH volume - on the existing dev/prod boxes it would silently not run,
#   - GlitchTip needs pg14+ while the app is pinned to 16 for pgvector; decoupling stops GlitchTip's
#     upgrade cycle blocking the app's.
#
# It DOES share the app's Redis, on DB index 1. VALKEY_URL is optional for GlitchTip and only affects
# caching and task-queue speed, so the blast radius is small and it saves a container.
name: diariz

services:
  glitchtip-postgres:
    image: postgres:16-alpine
    restart: unless-stopped
    environment:
      POSTGRES_DB: glitchtip
      POSTGRES_USER: glitchtip
      POSTGRES_PASSWORD: ${GLITCHTIP_POSTGRES_PASSWORD:-glitchtip}
    volumes:
      - glitchtipdata:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U glitchtip"]
      interval: 5s
      timeout: 3s
      retries: 10

  glitchtip:
    image: glitchtip/glitchtip:v6.2
    restart: unless-stopped
    depends_on:
      glitchtip-postgres: { condition: service_healthy }
      redis: { condition: service_healthy }
    environment:
      DATABASE_URL: "postgres://glitchtip:${GLITCHTIP_POSTGRES_PASSWORD:-glitchtip}@glitchtip-postgres:5432/glitchtip"
      # DB index 1 - the app's queues live on index 0 and must not be disturbed.
      VALKEY_URL: "redis://redis:6379/1"
      SECRET_KEY: ${GLITCHTIP_SECRET_KEY}
      # MUST include the scheme. Django derives its trusted CSRF origins from this, and a mismatch
      # presents as "wrong password" on every login attempt rather than anything mentioning CSRF.
      GLITCHTIP_DOMAIN: ${GLITCHTIP_DOMAIN}
      DEFAULT_FROM_EMAIL: ${GLITCHTIP_FROM_EMAIL}
      EMAIL_URL: ${GLITCHTIP_EMAIL_URL}
      # Internal tool on a public subdomain: no open registration, no new orgs.
      ENABLE_USER_REGISTRATION: "false"
      ENABLE_ORGANIZATION_CREATION: "false"
      # Spans (as opposed to bare transactions) require DuckDB. It runs inside the existing Python
      # process writing Parquet, so this costs ~128 MB of RAM and no extra container.
      GLITCHTIP_ENABLE_DUCKDB: "true"
      GLITCHTIP_COLD_STORAGE_BUCKET: ${GLITCHTIP_COLD_STORAGE_BUCKET:-glitchtip}
      AWS_S3_ENDPOINT_URL: "http://minio:9000"
      AWS_ACCESS_KEY_ID: ${GLITCHTIP_MINIO_ACCESS_KEY}
      AWS_SECRET_ACCESS_KEY: ${GLITCHTIP_MINIO_SECRET_KEY}
    ports:
      # Published for the OUTER reverse proxy only. Bind to localhost so it is not LAN-reachable:
      # Docker publishes to 0.0.0.0 by default AND writes its own DNAT rules, bypassing the host
      # firewall (same trap documented on the postgres service in docker-compose.yml).
      - "${GLITCHTIP_BIND:-127.0.0.1}:${GLITCHTIP_PORT:-8000}:8000"

volumes:
  glitchtipdata:
```

- [ ] **Step 2: Append the env block to `deploy/.env.example`**

```bash
# ---- Observability (optional): self-hosted GlitchTip error tracking + timings ----
# Enabled only when you run the overlay:
#   docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d
# Leaving SENTRY_DSN / Sentry__Dsn empty keeps the worker and API completely uninstrumented.

# Full external URL INCLUDING the scheme. Django derives its CSRF trusted origins from it; get this
# wrong and every login fails with a misleading "wrong password".
GLITCHTIP_DOMAIN=https://errors.dev.example.com
GLITCHTIP_SECRET_KEY=
GLITCHTIP_POSTGRES_PASSWORD=
# Host port for the outer reverse proxy. Keep the bind on 127.0.0.1 unless the proxy is off-box.
GLITCHTIP_PORT=8000
GLITCHTIP_BIND=127.0.0.1

# GlitchTip's own SMTP credentials, deliberately separate from the app's EMAIL__* settings so a
# technical-contact address can diverge from the app's sender without one change breaking the other.
# Format: smtp://user:password@host:port
GLITCHTIP_EMAIL_URL=
GLITCHTIP_FROM_EMAIL=

# DuckDB cold storage (Parquet) in MinIO. MUST be its OWN bucket, never a prefix inside `recordings`:
# a platform restore wipes the recordings bucket and would silently destroy the telemetry archive.
# Use a scoped MinIO key, NOT the root credentials - root would give an observability tool full
# read/write access to every user's audio.
GLITCHTIP_COLD_STORAGE_BUCKET=glitchtip
GLITCHTIP_MINIO_ACCESS_KEY=
GLITCHTIP_MINIO_SECRET_KEY=

# DSNs issued by the GlitchTip UI after creating the projects. Empty = that runtime reports nothing.
SENTRY_DSN=
SENTRY_API_DSN=
SENTRY_ENVIRONMENT=development
SENTRY_TRACES_SAMPLE_RATE=1.0
```

- [ ] **Step 3: Create the scoped MinIO bucket and access key on the dev box**

Run against the dev box's MinIO. This is a one-time operational step, not a repo change.

```bash
mc alias set devminio http://localhost:9002 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD"
mc mb devminio/glitchtip
```

Then create a policy file `glitchtip-policy.json` limiting access to that bucket alone:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": ["s3:*"],
      "Resource": ["arn:aws:s3:::glitchtip", "arn:aws:s3:::glitchtip/*"]
    }
  ]
}
```

```bash
mc admin policy create devminio glitchtip-only glitchtip-policy.json
mc admin user add devminio glitchtip-svc "$(openssl rand -hex 24)"
mc admin policy attach devminio glitchtip-only --user glitchtip-svc
```

Put the resulting access key and secret into `GLITCHTIP_MINIO_ACCESS_KEY` / `GLITCHTIP_MINIO_SECRET_KEY` in the dev box's `deploy/.env`.

Verify the key genuinely cannot reach the audio:

```bash
mc alias set gtcheck http://localhost:9002 glitchtip-svc "<the secret>"
mc ls gtcheck/recordings
```

Expected: **Access Denied**. If it lists objects, the policy is wrong - stop and fix it before continuing.

- [ ] **Step 4: Configure the outer nginx proxy for `errors.dev.<domain>`**

Add a DNS A record and a TLS certificate for the hostname first. Then add this server block to the outer proxy. The `map` goes at `http` level, outside `server`; nginx refuses to start with "unknown variable connection_upgrade" if it is missing, and many configs already declare it - do not declare it twice.

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 443 ssl;
    server_name errors.dev.example.com;

    # TLS config as per the existing app server blocks.

    location / {
        proxy_pass http://127.0.0.1:8000;

        proxy_set_header Host              $host;
        proxy_set_header X-Real-IP         $remote_addr;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        # Load-bearing. GlitchTip is Django: without this every login fails with a CSRF error that
        # presents as a wrong password. Same trap apps/web/nginx.conf documents for OpenIddict.
        proxy_set_header X-Forwarded-Proto $scheme;

        # Minidumps and source-map bundles are far larger than nginx's 1 MB default.
        client_max_body_size 100m;
        proxy_read_timeout 300s;
        proxy_send_timeout 300s;

        proxy_http_version 1.1;
        proxy_set_header Upgrade    $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
    }
}
```

Reload with `nginx -t && nginx -s reload`.

- [ ] **Step 5: Bring it up on dev and verify**

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d glitchtip-postgres glitchtip
```

Human sign-off checklist - all five must pass:

1. `https://errors.dev.<domain>` loads the GlitchTip UI over HTTPS.
2. Creating the first user and **logging in succeeds** (this is the CSRF/`X-Forwarded-Proto` check).
3. A password-reset email arrives (the SMTP check).
4. Two projects created: `diariz-worker` and `diariz-api`; both DSNs recorded in the dev `deploy/.env`.
5. `mc ls gtcheck/recordings` still returns Access Denied.

- [ ] **Step 6: Update the architecture doc**

In `docs/Overall_Synopsis_of_Platform.md`, add GlitchTip as an **optional** component: what it is, that it is opt-in via the overlay compose file, its own Postgres and MinIO bucket, the shared Redis on DB index 1, and that it is deployed per-environment with no sharing between dev and prod. Note explicitly that a co-located GlitchTip cannot report that the box itself is down.

Do **not** touch `docs/Data_Schema.md` (GlitchTip owns its own database), the README Features table, `docs/features.md`, or the About-box `CAPABILITIES` - nothing user-visible changes.

- [ ] **Step 7: Bump the version and add the release entry**

Set `0.174.1` in all five files: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj` (`<Version>`), `integrations/n8n-nodes-diariz/package.json`.

Add to the top of `RELEASES` in `apps/web/src/lib/releases.ts` (get the real PR number first - see the PR grouping section):

```ts
{
  version: "0.174.1",
  date: "2026-07-31",
  pr: 390,
  headline: "Optional self-hosted error tracking",
  summary:
    "Adds an optional Docker Compose overlay for GlitchTip, a self-hosted error-tracking and " +
    "performance-monitoring service. Nothing is reported unless you run the overlay and set a DSN, " +
    "so existing deployments are unchanged. This release only stands the service up - the worker and " +
    "API start reporting to it in later releases.",
  added: [
    "Optional GlitchTip overlay (deploy/docker-compose.observability.yml) with its own database and object-storage bucket.",
  ],
},
```

- [ ] **Step 8: Verify the version mirrors test passes**

```bash
cd apps/web && npm test -- versionMirrors releases
```

Expected: PASS. If it fails, a mirror was missed.

- [ ] **Step 9: Commit, push, open the PR**

```bash
git checkout -b feat/observability-glitchtip-infra
git add deploy/docker-compose.observability.yml deploy/.env.example docs/Overall_Synopsis_of_Platform.md version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts
git commit -m "feat: optional GlitchTip observability overlay"
git push -u origin feat/observability-glitchtip-infra
```

PR description must state: **server redeploy only, no desktop release**, and that the overlay is opt-in so existing deployments are unaffected.

---

### Task 2: Worker PII scrubber (TDD)

**Files:**
- Create: `src/Diariz.Worker/telemetry.py`
- Test: `src/Diariz.Worker/tests/test_telemetry.py`

**Interfaces:**
- Consumes: nothing.
- Produces: `telemetry.scrub(obj) -> object` (recursive, pure), `telemetry.is_sensitive_key(key: str) -> bool`, `telemetry.REDACTED: str`. Task 3 consumes `scrub` as the SDK's `before_send`.

- [ ] **Step 1: Write the failing tests**

Create `src/Diariz.Worker/tests/test_telemetry.py`:

```python
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
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd src/Diariz.Worker && python -m pytest tests/test_telemetry.py -v
```

Expected: FAIL, collection error `ModuleNotFoundError: No module named 'telemetry'`.

- [ ] **Step 3: Write the minimal implementation**

Create `src/Diariz.Worker/telemetry.py`:

```python
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
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd src/Diariz.Worker && python -m pytest tests/test_telemetry.py -v
```

Expected: all six PASS.

- [ ] **Step 5: Commit**

```bash
git checkout -b feat/observability-worker
git add src/Diariz.Worker/telemetry.py src/Diariz.Worker/tests/test_telemetry.py
git commit -m "feat: add worker telemetry scrubber"
```

---

### Task 3: Worker telemetry init, release lookup and span helpers (TDD)

**Files:**
- Modify: `src/Diariz.Worker/telemetry.py`
- Modify: `src/Diariz.Worker/tests/test_telemetry.py`
- Modify: `src/Diariz.Worker/requirements.txt`

**Interfaces:**
- Consumes: `scrub` from Task 2; `config.API_BASE_URL` from `config.py`.
- Produces: `telemetry.init() -> bool`, `telemetry.release() -> str | None`, and two context managers `telemetry.transaction(name: str, op: str = "queue.task")` and `telemetry.span(op: str, name: str)`. Task 4 consumes all four.

- [ ] **Step 1: Write the failing tests**

Append to `src/Diariz.Worker/tests/test_telemetry.py`:

```python
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
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd src/Diariz.Worker && python -m pytest tests/test_telemetry.py -v
```

Expected: FAIL - `AttributeError: module 'telemetry' has no attribute 'init'` (and the same for `release`, `span`, `transaction`, `requests`).

- [ ] **Step 3: Write the minimal implementation**

Add to the top of `src/Diariz.Worker/telemetry.py`, below the docstring:

```python
import logging
import os
from contextlib import contextmanager

import requests

from config import config

log = logging.getLogger("telemetry")

# Set by init(). The context managers below check it rather than importing sentry_sdk, so a worker
# with no DSN never loads the SDK at all.
_enabled = False
```

Then append below `scrub`:

```python
def _before_send(event, hint):
    """SDK hook. Scrubs every outgoing event and transaction."""
    return scrub(event)


def release() -> str | None:
    """The platform version this worker serves, read from the API's /health.

    The worker image carries no version of its own - version.json lives at the repo root, outside the
    worker's build context - and hard-coding one in .env would drift silently the moment someone
    forgot to bump it. The API already reports the canonical version, and the worker already waits
    for the API to be healthy before it starts.
    """
    try:
        return requests.get(f"{config.API_BASE_URL}/health", timeout=5).json().get("version")
    except Exception:  # noqa: BLE001 - a missing release tag must never stop the worker starting
        log.debug("Could not read the platform version from the API", exc_info=True)
        return None


def init() -> bool:
    """Start reporting if SENTRY_DSN is set. Returns whether reporting is on."""
    global _enabled

    dsn = os.getenv("SENTRY_DSN", "").strip()
    if not dsn:
        return False

    import sentry_sdk

    sentry_sdk.init(
        dsn=dsn,
        environment=os.getenv("SENTRY_ENVIRONMENT", "development"),
        release=release(),
        traces_sample_rate=float(os.getenv("SENTRY_TRACES_SAMPLE_RATE", "1.0")),
        # Never attach request bodies, headers or user identifiers automatically.
        send_default_pii=False,
        before_send=_before_send,
        before_send_transaction=_before_send,
    )
    _enabled = True
    log.info("Error reporting enabled")
    return True


@contextmanager
def transaction(name: str, op: str = "queue.task"):
    """Wrap one unit of work as a reportable transaction. A no-op when reporting is off."""
    if not _enabled:
        yield
        return
    import sentry_sdk
    with sentry_sdk.start_transaction(op=op, name=name):
        yield


@contextmanager
def span(op: str, name: str):
    """Time one stage inside the current transaction. A no-op when reporting is off."""
    if not _enabled:
        yield
        return
    import sentry_sdk
    with sentry_sdk.start_span(op=op, name=name):
        yield
```

- [ ] **Step 4: Add the dependency**

Append to `src/Diariz.Worker/requirements.txt`:

```
# Optional error + performance reporting (GlitchTip / Sentry-compatible). Imported lazily and only
# when SENTRY_DSN is set, so it costs nothing on deployments that do not use it.
sentry-sdk==X.Y.Z
```

Replace `X.Y.Z` with a real version - do not leave it as written. Determine it with:

```bash
pip index versions sentry-sdk
```

Take the newest release on the `2.x` line (the 2 line is what the GlitchTip docs target) and write that exact number into the file. Do **not** add it to `requirements-test.txt` - the tests stub it, exactly as `conftest.py` already stubs `whisperx`.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
cd src/Diariz.Worker && python -m pytest tests/test_telemetry.py -v
```

Expected: all PASS.

- [ ] **Step 6: Run the whole worker suite for regressions**

```bash
cd src/Diariz.Worker && python -m pytest
```

Expected: all PASS, no warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Worker/telemetry.py src/Diariz.Worker/tests/test_telemetry.py src/Diariz.Worker/requirements.txt
git commit -m "feat: add worker telemetry init and span helpers"
```

---

### Task 4: Wire telemetry into the worker and pipeline (TDD)

**Files:**
- Modify: `src/Diariz.Worker/worker.py` (`main`, `handle`, `handle_merge`)
- Modify: `src/Diariz.Worker/pipeline.py` (`transcribe`)
- Modify: `src/Diariz.Worker/tests/test_worker.py`

**Interfaces:**
- Consumes: `telemetry.init`, `telemetry.transaction`, `telemetry.span` from Task 3.
- Produces: no new API. Behaviour: a job runs inside a transaction named `transcribe` or `audio-merge`, with spans `download`, `asr`, `align`, `diarize`, `embeddings`, `callback`.

- [ ] **Step 1: Write the failing tests**

Append to `src/Diariz.Worker/tests/test_worker.py`:

```python
def test_handle_runs_inside_a_telemetry_transaction(monkeypatch, tmp_path):
    audio = tmp_path / "audio.wav"
    audio.write_text("fake")
    monkeypatch.setattr(worker.storage, "download", lambda key: str(audio))
    monkeypatch.setattr(worker.pipeline, "transcribe",
                        lambda path, min_s=None, max_s=None: {"language": "en", "segments": [], "speakers": []})
    monkeypatch.setattr(worker.callback, "post_result", lambda *a, **k: None)

    names = []

    import contextlib

    @contextlib.contextmanager
    def fake_transaction(name, op="queue.task"):
        names.append(name)
        yield

    monkeypatch.setattr(worker.telemetry, "transaction", fake_transaction)

    worker.handle(_job("tid-t"))

    assert names == ["transcribe"]


def test_handle_reports_a_failure_and_still_cleans_up(monkeypatch, tmp_path):
    audio = tmp_path / "audio.wav"
    audio.write_text("fake")
    monkeypatch.setattr(worker.storage, "download", lambda key: str(audio))

    def boom(path, min_s=None, max_s=None):
        raise RuntimeError("model load failed")

    monkeypatch.setattr(worker.pipeline, "transcribe", boom)

    posted = {}
    monkeypatch.setattr(worker.callback, "post_failure",
                        lambda tid, msg: posted.update(tid=tid, msg=msg))

    captured = []
    monkeypatch.setattr(worker.telemetry, "capture_exception", lambda e: captured.append(e))

    worker.handle(_job("tid-f"))

    # The existing contract is unchanged: the API is still told, and the temp file is still removed.
    assert posted["tid"] == "tid-f"
    assert not os.path.exists(str(audio))
    # And the exception is now also reported.
    assert len(captured) == 1
    assert isinstance(captured[0], RuntimeError)
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd src/Diariz.Worker && python -m pytest tests/test_worker.py -v
```

Expected: FAIL - `AttributeError: module 'worker' has no attribute 'telemetry'`.

- [ ] **Step 3: Add `capture_exception` to the telemetry module**

Append to `src/Diariz.Worker/telemetry.py`:

```python
def capture_exception(error: BaseException) -> None:
    """Report an exception that the worker has caught and handled. A no-op when reporting is off."""
    if not _enabled:
        return
    import sentry_sdk
    sentry_sdk.capture_exception(error)
```

- [ ] **Step 4: Wire it into `worker.py`**

Add `import telemetry` to the import block in `src/Diariz.Worker/worker.py`, alongside `import storage`.

Replace the body of `handle` with:

```python
def handle(job: dict) -> None:
    transcription_id = job["TranscriptionId"]
    blob_key = job["BlobKey"]
    log.info("Processing transcription %s (blob=%s, model=%s)",
             transcription_id, blob_key, job.get("Model"))

    audio_path = None
    started = time.monotonic()
    try:
        with telemetry.transaction("transcribe"):
            with telemetry.span("storage.download", "download"):
                audio_path = storage.download(blob_key)
            result = pipeline.transcribe(audio_path, job.get("MinSpeakers"), job.get("MaxSpeakers"))
            # Full-pipeline wall-clock time (download + transcribe + diarize + embed), reported to the API.
            processing_ms = int((time.monotonic() - started) * 1000)
            with telemetry.span("http.client", "callback"):
                callback.post_result(transcription_id, result["language"], result["segments"],
                                     result.get("speakers"), result.get("duration_ms"), processing_ms)
    except Exception as e:  # noqa: BLE001 - report and continue
        log.exception("Job failed for transcription %s", transcription_id)
        telemetry.capture_exception(e)
        callback.post_failure(transcription_id, str(e))
    finally:
        if audio_path and os.path.exists(audio_path):
            os.remove(audio_path)
```

Apply the same two changes to `handle_merge` - wrap its body in `with telemetry.transaction("audio-merge"):` and add `telemetry.capture_exception(e)` immediately before `callback.post_merge_failure(...)`.

In `main`, add the init call immediately after `torch_compat.restore_legacy_torch_load()`:

```python
    # Optional error/performance reporting. Inert unless SENTRY_DSN is set.
    telemetry.init()
```

- [ ] **Step 5: Add stage spans in `pipeline.py`**

Add `import telemetry` to the import block. Then in `transcribe`, wrap each numbered stage. The existing comments stay; only the indentation and the `with` lines are new:

```python
    # 1. Transcribe (backend-pluggable: faster-whisper on CUDA, openai-whisper on AMD ROCm)
    with telemetry.span("ai.asr", "asr"):
        asr = _asr(audio)
    language = asr["language"]

    # 2. Word-level alignment
    with telemetry.span("ai.align", "align"):
        align_model, metadata = _get_align(language)
        result = whisperx.align(
            asr["segments"], align_model, metadata, audio, config.DEVICE,
            return_char_alignments=False)

    # 3. Diarization (with optional speaker-count hints) + speaker assignment
    with telemetry.span("ai.diarize", "diarize"):
        diarize_segments = _diarize(audio, min_speakers, max_speakers)
        result = whisperx.assign_word_speakers(diarize_segments, result)

    segments = _shape_segments(result["segments"])

    # 4. Per-speaker voiceprint embeddings (for identification against enrolled people)
    with telemetry.span("ai.embeddings", "embeddings"):
        speakers = _extract_speakers(audio, segments)
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
cd src/Diariz.Worker && python -m pytest -v
```

Expected: all PASS, including the pre-existing `test_worker.py` and `test_pipeline.py` tests, with no warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Worker/worker.py src/Diariz.Worker/pipeline.py src/Diariz.Worker/telemetry.py src/Diariz.Worker/tests/test_worker.py
git commit -m "feat: report worker exceptions and stage timings"
```

---

### Task 5: Worker deploy config, docs and release (PR 2 close-out)

**Files:**
- Modify: `deploy/docker-compose.yml` (worker service `environment`)
- Modify: `docs/Overall_Synopsis_of_Platform.md`
- Modify: `version.json` + the four mirrors
- Modify: `apps/web/src/lib/releases.ts`

**Interfaces:**
- Consumes: `SENTRY_DSN`, `SENTRY_ENVIRONMENT`, `SENTRY_TRACES_SAMPLE_RATE` from `.env` (Task 1).
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Pass the DSN to the worker**

In `deploy/docker-compose.yml`, append to the `worker` service's `environment` block:

```yaml
      # Optional error + performance reporting. Empty DSN = completely inert.
      SENTRY_DSN: ${SENTRY_DSN:-}
      SENTRY_ENVIRONMENT: ${SENTRY_ENVIRONMENT:-development}
      SENTRY_TRACES_SAMPLE_RATE: ${SENTRY_TRACES_SAMPLE_RATE:-1.0}
```

- [ ] **Step 2: Update the architecture doc**

In `docs/Overall_Synopsis_of_Platform.md`, record that the worker optionally reports to GlitchTip: one transaction per job with `download`/`asr`/`align`/`diarize`/`embeddings`/`callback` spans, that the release tag is read from the API's `/health` at startup, and that all events pass through `telemetry.scrub` so no transcript content is transmitted.

- [ ] **Step 3: Bump to 0.174.2 and add the release entry**

Same five files as Task 1 Step 7. Add to the top of `RELEASES`:

```ts
{
  version: "0.174.2",
  date: "2026-07-31",
  pr: 391,
  headline: "Transcription worker reports failures and stage timings",
  summary:
    "When a GlitchTip DSN is configured, the transcription worker now reports unhandled failures " +
    "with a full traceback, and times each stage of a job - download, transcription, alignment, " +
    "diarization, voiceprints and callback - so a slow job can be traced to the stage responsible. " +
    "Transcript content is never transmitted. Deployments without a DSN are unchanged.",
  added: [
    "Optional worker error reporting and per-stage timings.",
  ],
},
```

- [ ] **Step 4: Verify the full worker suite and the mirrors test**

```bash
cd src/Diariz.Worker && python -m pytest
```

```bash
cd apps/web && npm test -- versionMirrors releases
```

Expected: both PASS.

- [ ] **Step 5: Verify on dev end to end**

Deploy the branch to dev with `SENTRY_DSN` set to the `diariz-worker` DSN from Task 1. Then:

1. Upload a short recording and let it transcribe. In GlitchTip, the `transcribe` transaction appears with a span breakdown whose stages account for the wall-clock time.
2. Force a failure (e.g. temporarily set `WHISPER_MODEL` to a nonexistent model and restart the worker). A traceback appears in GlitchTip, and the recording still moves to a failed state in the app - the existing behaviour must be unchanged.
3. **Inspect the captured event's payload in the GlitchTip UI and confirm no transcript text appears anywhere in it.** This is the gate on the whole approach; do not proceed to Task 6 until it passes.

- [ ] **Step 6: Commit, push, open the PR**

```bash
git add deploy/docker-compose.yml docs/Overall_Synopsis_of_Platform.md version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts
git commit -m "feat: wire worker telemetry into the deployment"
git push -u origin feat/observability-worker
```

PR description: **server redeploy only, no desktop release.**

---

### Task 6: API telemetry options and scrubber (TDD)

**Files:**
- Modify: `src/Diariz.Api/Configuration/AppOptions.cs`
- Create: `src/Diariz.Api/Services/SentryScrubber.cs`
- Test: `tests/Diariz.Api.Tests/SentryScrubberTests.cs`
- Test: `tests/Diariz.Api.Tests/TelemetryOptionsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Diariz.Api.Configuration.TelemetryOptions` with `Section = "Sentry"`, `Dsn`, `BrowserDsn`, `Environment`, `TracesSampleRate`, `Enabled`; and `Diariz.Api.Services.SentryScrubber` with `Redacted`, `IsSensitiveKey(string)`, `Scrub(SentryEvent)`. Task 7 consumes both.

**Naming note:** the class is `TelemetryOptions`, **not** `SentryOptions` - `Sentry.AspNetCore` already exports a type by that name and the collision would force awkward qualification throughout `Program.cs`. The configuration *section* is still `"Sentry"`.

- [ ] **Step 1: Write the failing options test**

Create `tests/Diariz.Api.Tests/TelemetryOptionsTests.cs`:

```csharp
using Diariz.Api.Configuration;

namespace Diariz.Api.Tests;

public class TelemetryOptionsTests
{
    [Fact]
    public void Enabled_IsFalse_WhenDsnIsEmpty()
    {
        Assert.False(new TelemetryOptions().Enabled);
    }

    [Fact]
    public void Enabled_IsFalse_WhenDsnIsOnlyWhitespace()
    {
        Assert.False(new TelemetryOptions { Dsn = "   " }.Enabled);
    }

    [Fact]
    public void Enabled_IsTrue_WhenDsnIsSet()
    {
        Assert.True(new TelemetryOptions { Dsn = "https://key@errors.example/1" }.Enabled);
    }

    [Fact]
    public void TracesSampleRate_DefaultsToCapturingEverything()
    {
        // This deployment's volume is small; the SDK docs' 1% advice targets high-traffic sites.
        Assert.Equal(1.0, new TelemetryOptions().TracesSampleRate);
    }
}
```

- [ ] **Step 2: Write the failing scrubber test**

Create `tests/Diariz.Api.Tests/SentryScrubberTests.cs`:

```csharp
using Diariz.Api.Services;
using Sentry;   // SentryEvent; reaches the test project transitively via the API project reference

namespace Diariz.Api.Tests;

public class SentryScrubberTests
{
    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Worker-Secret")]
    [InlineData("Cookie")]
    [InlineData("password")]
    [InlineData("ApiKey")]
    [InlineData("Summarization__ApiKey")]
    public void IsSensitiveKey_MatchesCredentials(string key)
    {
        Assert.True(SentryScrubber.IsSensitiveKey(key));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("transcript")]
    [InlineData("segments")]
    [InlineData("summary")]
    [InlineData("minutes")]
    public void IsSensitiveKey_MatchesMeetingContent(string key)
    {
        Assert.True(SentryScrubber.IsSensitiveKey(key));
    }

    [Theory]
    [InlineData("recordingId")]
    [InlineData("transcriptionId")]
    [InlineData("blobKey")]
    [InlineData("userId")]
    [InlineData("model")]
    public void IsSensitiveKey_KeepsTheIdentifiersNeededToDiagnose(string key)
    {
        // A scrubber that redacts everything is useless.
        Assert.False(SentryScrubber.IsSensitiveKey(key));
    }

    [Fact]
    public void Scrub_RedactsSensitiveExtras_AndKeepsIdentifiers()
    {
        var e = new SentryEvent();
        e.SetExtra("transcriptionId", "tid-1");
        e.SetExtra("text", "the confidential meeting content");

        var cleaned = SentryScrubber.Scrub(e);

        Assert.NotNull(cleaned);
        Assert.Equal("tid-1", cleaned!.Extra["transcriptionId"]);
        Assert.Equal(SentryScrubber.Redacted, cleaned.Extra["text"]);
    }
}
```

- [ ] **Step 3: Run both tests to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~TelemetryOptions|FullyQualifiedName~SentryScrubber"
```

Expected: FAIL to compile - `TelemetryOptions` and `SentryScrubber` do not exist.

- [ ] **Step 4: Add the options class**

Append to `src/Diariz.Api/Configuration/AppOptions.cs`, following the `DictationOptions` pattern already in that file:

```csharp
/// <summary>Optional error + performance reporting to a Sentry-compatible endpoint (GlitchTip).
/// Named TelemetryOptions rather than SentryOptions because Sentry.AspNetCore already exports that
/// type name; the configuration section is still "Sentry".</summary>
public class TelemetryOptions
{
    public const string Section = "Sentry";

    /// <summary>Server-side DSN. Empty disables reporting entirely - no SDK init, no network calls.</summary>
    public string Dsn { get; set; } = "";

    /// <summary>DSN handed to the browser at runtime. Public by design (it ships in the JS bundle).
    /// Unused until the SPA is instrumented; kept here so both live in one section.</summary>
    public string BrowserDsn { get; set; } = "";

    public string Environment { get; set; } = "development";

    /// <summary>Fraction of requests traced. Defaults to everything: this deployment's volume is small,
    /// and the SDK docs' 1% recommendation targets high-traffic sites.</summary>
    public double TracesSampleRate { get; set; } = 1.0;

    /// <summary>True when a DSN is configured; otherwise the SDK is never initialised.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Dsn);
}
```

- [ ] **Step 5: Add the `Sentry.AspNetCore` package**

```bash
dotnet add src/Diariz.Api/Diariz.Api.csproj package Sentry.AspNetCore
```

This pins whatever the current release is; check the resulting `<PackageReference>` line matches the style of its neighbours in the csproj.

- [ ] **Step 6: Write the scrubber**

Create `src/Diariz.Api/Services/SentryScrubber.cs`:

```csharp
using Sentry;   // SentryEvent

namespace Diariz.Api.Services;

/// <summary>Redacts credentials and meeting content from telemetry events before they leave the
/// process. Distinct from <see cref="LogSanitizer"/>, which defends against log-injection in log
/// lines rather than against disclosure.
///
/// Transcripts, summaries and minutes are this application's payload, and an event that leaks one
/// cannot be un-sent - so this denies by default and keeps only the identifiers needed to diagnose
/// a failure.</summary>
public static class SentryScrubber
{
    public const string Redacted = "[redacted]";

    // Exact field names that carry meeting content.
    private static readonly HashSet<string> DenyExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "transcript", "transcription", "segments", "words", "summary",
        "minutes", "note", "notes", "content", "authorization", "cookie", "cookies",
    };

    // Substrings marking a credential regardless of the surrounding name.
    private static readonly string[] DenySubstring =
        ["secret", "token", "password", "apikey", "api_key", "accesskey", "access_key"];

    /// <summary>True when a field with this name must never leave the process.</summary>
    public static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (DenyExact.Contains(key)) return true;
        foreach (var marker in DenySubstring)
            if (key.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>SDK hook: redact in place and return the event.</summary>
    public static SentryEvent? Scrub(SentryEvent e)
    {
        foreach (var key in e.Extra.Keys.ToList())
            if (IsSensitiveKey(key)) e.SetExtra(key, Redacted);

        foreach (var key in e.Request.Headers.Keys.ToList())
            if (IsSensitiveKey(key)) e.Request.Headers[key] = Redacted;

        // Request bodies can contain anything a user typed or dictated. Never send one.
        e.Request.Data = null;

        return e;
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~TelemetryOptions|FullyQualifiedName~SentryScrubber"
```

Expected: all PASS.

- [ ] **Step 8: Commit**

```bash
git checkout -b feat/observability-api
git add src/Diariz.Api/Configuration/AppOptions.cs src/Diariz.Api/Services/SentryScrubber.cs src/Diariz.Api/Diariz.Api.csproj tests/Diariz.Api.Tests/TelemetryOptionsTests.cs tests/Diariz.Api.Tests/SentryScrubberTests.cs
git commit -m "feat: add API telemetry options and event scrubber"
```

---

### Task 7: Wire Sentry into the API host

**Files:**
- Modify: `src/Diariz.Api/Program.cs`

**Interfaces:**
- Consumes: `TelemetryOptions` and `SentryScrubber` from Task 6.
- Produces: nothing consumed by later tasks.

**No new test.** `Program.cs` is not unit-testable in this codebase, and the testable units (`Enabled`, `IsSensitiveKey`, `Scrub`) are already covered by Task 6. Verification is the dev end-to-end check in Task 8.

- [ ] **Step 1: Move the version lookup earlier**

`appVersion` is currently computed at `src/Diariz.Api/Program.cs:515`, after the app is built, but Sentry needs it at host-configuration time. Cut this line:

```csharp
var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
```

and paste it immediately **above** `var builder = WebApplication.CreateBuilder(args);`. The existing `/health` endpoint keeps using it unchanged.

- [ ] **Step 2: Register the options section**

In the `---- Options ----` block, alongside the other `Configure` calls:

```csharp
builder.Services.Configure<TelemetryOptions>(builder.Configuration.GetSection(TelemetryOptions.Section));
```

- [ ] **Step 3: Initialise the SDK**

Immediately after the `ForwardedHeadersOptions` block (Sentry must be configured on the host before the rest of the pipeline is built):

```csharp
// ---- Optional error + performance reporting (GlitchTip / Sentry-compatible) ----
// Entirely absent unless a DSN is configured, matching how Summarization/Dictation are gated.
var telemetry = builder.Configuration.GetSection(TelemetryOptions.Section).Get<TelemetryOptions>()
                ?? new TelemetryOptions();
if (telemetry.Enabled)
{
    builder.WebHost.UseSentry(o =>
    {
        o.Dsn = telemetry.Dsn;
        o.Environment = telemetry.Environment;
        o.Release = appVersion;
        o.TracesSampleRate = telemetry.TracesSampleRate;
        // Never attach request bodies, cookies or user identifiers automatically.
        o.SendDefaultPii = false;
        o.SetBeforeSend(SentryScrubber.Scrub);
    });
}
```

Outbound `HttpClient` calls need no extra registration: the ASP.NET Core integration instruments clients created by `IHttpClientFactory` automatically, which covers every `AddHttpClient` registration in this file.

- [ ] **Step 4: Build and run the whole solution's tests**

```bash
dotnet build Diariz.slnx
```

Expected: builds clean. Building the solution (not just the unit test project) is deliberate - a unit-only run misses integration and CodeQL compile breaks.

```bash
dotnet test tests/Diariz.Api.Tests
```

Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Api/Program.cs
git commit -m "feat: report API exceptions and request timings"
```

---

### Task 8: API deploy config, docs and release (PR 3 close-out)

**Files:**
- Modify: `deploy/docker-compose.yml` (api service `environment`)
- Modify: `docs/Overall_Synopsis_of_Platform.md`
- Modify: `version.json` + the four mirrors
- Modify: `apps/web/src/lib/releases.ts`

- [ ] **Step 1: Pass the DSN to the API**

In `deploy/docker-compose.yml`, append to the `api` service's `environment` block:

```yaml
      # Optional error + performance reporting. Empty DSN = completely inert.
      Sentry__Dsn: ${SENTRY_API_DSN:-}
      Sentry__Environment: ${SENTRY_ENVIRONMENT:-development}
      Sentry__TracesSampleRate: ${SENTRY_TRACES_SAMPLE_RATE:-1.0}
```

- [ ] **Step 2: Update the architecture doc**

Record in `docs/Overall_Synopsis_of_Platform.md` that the API optionally reports unhandled exceptions and request transactions (p50/p95 per endpoint), that outbound LLM calls appear as child spans via `IHttpClientFactory` instrumentation, that the release tag is the assembly version reported at `/health`, and that every event passes through `SentryScrubber`.

- [ ] **Step 3: Bump to 0.174.3 and add the release entry**

```ts
{
  version: "0.174.3",
  date: "2026-07-31",
  pr: 392,
  headline: "API reports errors and endpoint timings",
  summary:
    "When a GlitchTip DSN is configured, the API now reports unhandled exceptions and records how " +
    "long each endpoint takes, including the time spent waiting on the configured language model. " +
    "Request bodies, credentials and meeting content are stripped before anything is sent. " +
    "Deployments without a DSN are unchanged.",
  added: [
    "Optional API error reporting and per-endpoint timings.",
  ],
},
```

- [ ] **Step 4: Verify**

```bash
dotnet build Diariz.slnx
```

```bash
dotnet test tests/Diariz.Api.Tests
```

```bash
cd apps/web && npm test -- versionMirrors releases
```

Expected: all PASS.

- [ ] **Step 5: Verify on dev end to end**

Deploy to dev with `SENTRY_API_DSN` set to the `diariz-api` DSN. Then:

1. Trigger a handled-but-logged failure (e.g. request a summary with a deliberately wrong `Summarization__ApiBase`) and confirm the exception appears with a stack trace.
2. Browse the app normally, then confirm the GlitchTip performance view lists endpoints with counts and p50/p95.
3. Run one summarisation and confirm the outbound LLM call appears as a **child span** of the request transaction with its own duration.
4. **Inspect a captured event's payload and confirm no request body, no `Authorization` header and no meeting content appears.**

- [ ] **Step 6: Commit, push, open the PR**

```bash
git add deploy/docker-compose.yml docs/Overall_Synopsis_of_Platform.md version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts
git commit -m "feat: wire API telemetry into the deployment"
git push -u origin feat/observability-api
```

PR description: **server redeploy only, no desktop release.**

---

### Task 9: Sub-phase 1d - promote to production

Operational only. **No code, no PR, no version bump** - the code shipped in PRs 1-3 and is inert on prod until a DSN is set.

**Gate:** do not start until dev has been running instrumented for long enough to have captured real failures, and those events have been reviewed for leaked content.

- [ ] **Step 1: Review what dev actually captured**

Open the last two weeks of dev events and read the payloads. Confirm: no transcript, summary, minutes or note text; no `Authorization` or `X-Worker-Secret` values; no request bodies. If anything leaked, stop - fix the scrubber and its tests as a new PR before touching prod.

- [ ] **Step 2: Stand up the prod instance**

Repeat Task 1 Steps 3-5 against the prod box, with prod's own values: its own `GLITCHTIP_SECRET_KEY`, its own Postgres password, its own MinIO scoped key, `errors.<domain>`, and `SENTRY_ENVIRONMENT=production`. Nothing is shared with dev.

Confirm again that the prod GlitchTip MinIO key cannot list `recordings`.

- [ ] **Step 3: Enable the worker first, alone**

Set only `SENTRY_DSN` (worker) in prod's `.env` and restart the worker. Leave `SENTRY_API_DSN` empty. Watch for a day: the worker handles far fewer, far more predictable payloads than the API, so it is the safer first exposure.

- [ ] **Step 4: Enable the API**

Set `SENTRY_API_DSN` and restart the API. Review the first day's events for leaked content specifically - the API sees far more user input than the worker does.

- [ ] **Step 5: Set retention**

Choose and apply an event-retention period on both instances. Do not leave it at the default. Dev can be aggressive; prod should be a deliberate choice recorded in `docs/Overall_Synopsis_of_Platform.md`.

- [ ] **Step 6: Confirm the phase-2 prerequisite**

Check whether the outer proxy preserves the `sentry-trace` and `baggage` request headers on the **app's** hostname. This is needed before the SPA work, and it fails silently, so verify it now rather than discovering it later. Record the answer in the phase 2 plan.

---

## Self-review notes

**Spec coverage.** Every section of the design spec maps to a task: topology and per-box composition (Task 1), MinIO bucket + scoped key constraint (Task 1 Steps 2-3), sub-phase 1a (Task 1), 1b (Tasks 2-5), 1c (Tasks 6-8), 1d (Task 9), off-by-default (Tasks 2/3/6), sampling (Task 6 Step 4), email separation (Task 1 Step 2), registration disabled (Task 1 Step 1), nginx and the CSRF trap (Task 1 Step 4), PII policy (Tasks 2, 6, and the verification gates in 5/8/9), testing (Tasks 2, 3, 4, 6), release obligations (Tasks 1, 5, 8), risks (mitigations distributed across the above).

**Deliberately out of scope**, per the spec: API-to-worker trace linking across the Redis stream, the SPA, source maps, and the `AboutModal.tsx` disclaimers question (a phase 2 decision).

**Deferred pins.** Two dependency versions are resolved at implementation time rather than written here - `sentry-sdk` (Task 3 Step 4, with the command to determine it) and `Sentry.AspNetCore` (Task 6 Step 5, via `dotnet add package`). Writing a specific version into this plan would ship a stale number.
