# Remote transcription worker (split deployment)

Date: 2026-08-19
Status: designed, not yet implemented

## Problem

Everything runs on one box: Postgres, Redis, MinIO, the API, the web front end, and the GPU transcription
worker. The worker is the only component that needs a GPU, and it is the component whose appetite for one
grows - `large-v3` plus alignment plus pyannote is a ~9 GB working set, and every concurrent job wants its
own.

Tying that to the machine that also holds the database means the GPU decides where the data lives.
Upgrading the GPU means moving Postgres. Adding a second GPU means a second copy of everything. And a
transcription running at full tilt competes for CPU and RAM with the API serving the app.

Splitting them lets the app server be a modest always-on machine and the GPU box be whatever is fastest,
replaceable without touching a byte of user data.

## What is already true

The worker was built for this without it being the goal. Its entire coupling to the rest of the platform is
three URLs in `src/Diariz.Worker/config.py`:

| Link | Today | Remote-ready? |
|---|---|---|
| Job queue | `REDIS_URL=redis://redis:6379/0` | Yes - `redis.Redis.from_url`, so `rediss://` and passwords work unchanged |
| Audio blobs | `S3_ENDPOINT=http://minio:9000` | Yes - boto3, path-style addressing already set |
| Result callback | `API_BASE_URL=http://api:8080` | Yes - already a shared-secret header (`X-Worker-Secret`), never JWT |

It never opens a Postgres connection, never touches a shared volume, and holds no state beyond a
`~/.cache` model cache it can rebuild by re-downloading. `reclaim_stale` in `worker.py` already handles a
worker that vanishes mid-job, which is exactly what a cutover looks like from the queue's point of view.

**So this design contains no production code changes.** It is compose files, provisioning, and
documentation. That is the single most important thing to know before implementing it: any pull towards
editing `worker.py`, `JobQueue.cs`, or `WorkerCallbackController.cs` is a sign of scope creep.

## Goal

One GPU machine on the same LAN runs the transcription worker. The app server runs everything else. The
LLM endpoints stay where they already are.

## Non-goals

- **No worker pool.** One remote worker. Redis consumer groups would load-balance several, but nothing else
  here is designed for it - `CONSUMER_NAME` defaults to `worker-1` in `config.py`, and a fleet needs a
  naming scheme, per-host caches, and a way to tell which machine ran a job. Deferred.
- **No TLS between the two machines.** The chosen topology is a trusted private LAN. Redis gets a password
  and MinIO gets a scoped credential, but the bytes on the wire are plaintext, including the audio and the
  callback secret. This is a deliberate, recorded trade: revisit it the moment either machine stops being on
  a network you control.
- **No GHCR image publishing.** The GPU box builds the worker image from a repo checkout, matching how the
  platform is deployed today. Publishing images is separately deferred work, and the worker image will not
  build on a 14 GB hosted runner regardless.
- **No cross-machine worker observability.** See "Known gaps" - this design makes a real gap worse and does
  not close it.
- **No change to what any user sees.** No feature rows, no help articles; the implementing PR carries only a
  release-notes entry.

## Design

### 1. Which machine runs what

| | App server | GPU box |
|---|---|---|
| Services | postgres, redis, minio, api, web | worker |
| Compose file | `deploy/docker-compose.yml` (existing) | `deploy/docker-compose.worker.yml` (new) |
| Env file | `deploy/.env` (existing, gains a few keys) | `deploy/.env` from a new `.env.worker.example` |
| Needs a GPU | No | Yes |
| Holds secrets | All of them | Three platform secrets, plus a Hugging Face read token (section 7) |

### 2. Compose layout, and why the worker service must be duplicated

The obvious way to avoid writing the worker service twice is Compose's `extends:`. **It does not work
here.** Measured on Docker 29.6.1 / Compose v5.3.0:

| Attempt | Result |
|---|---|
| `extends` a service carrying `profiles: ["local-worker"]` | `no service selected` - `profiles` is inherited |
| Reset it with `profiles: []` in the extending file | `no service selected` - the reset does not take |
| Satisfy it with `COMPOSE_PROFILES=local-worker` on the GPU box | Works; env override applies, base env inherited |
| ...but `depends_on: {redis, minio, api}` is inherited too | `service "worker" depends on undefined service "redis": invalid compose project` |
| Reset it with `depends_on: []` | Same error - the reset does not take |

There is no way to shed an inherited `depends_on`, and the main worker needs one for single-box startup
ordering. So `docker-compose.worker.yml` spells the worker service out in full.

That duplication is the real cost of this design, and it is the same failure mode that produced
`apps/web/src/lib/versionMirrors.test.ts` after the n8n node sat at `0.1.0` for ~70 releases. It gets the
same treatment: a parity test (section 9).

**ROCm.** `deploy/docker-compose.rocm.yml` carries a third copy of the worker service. If the GPU box is
the AMD machine, it needs `deploy/docker-compose.worker.rocm.yml` as a fourth. This design assumes both
NVIDIA and ROCm remote variants are wanted, and the parity test covers all copies. If only one GPU vendor
is ever going to be remote, drop the other file and the corresponding test case - nothing else changes.

### 3. The `local-worker` profile, and its ordering trap

`deploy/docker-compose.yml` gains one line on the worker service:

```yaml
  worker:
    profiles: ["local-worker"]
```

and `deploy/.env.example` gains one knob:

```bash
# Optional services this host runs. "local-worker" runs the GPU transcription worker inside this stack
# (the default: single-box install). Leave it EMPTY on an app server whose worker lives on a separate GPU
# machine - that machine runs deploy/docker-compose.worker.yml instead.
COMPOSE_PROFILES=local-worker
```

Compose reads `COMPOSE_PROFILES` from the project `.env`, so the whole opt-out is one line per machine.

**The trap:** a profile-gated service that is *already running* becomes unmanaged. Measured, after flipping
`COMPOSE_PROFILES` to empty:

| Command | Result |
|---|---|
| `docker compose up -d` | worker **left running**, no warning |
| `docker compose up -d --remove-orphans` | worker **left running** (not an orphan - a known service, unselected) |
| `docker compose down` | worker **left running**; network deletion then fails with `Resource is still in use` |
| `COMPOSE_PROFILES=local-worker docker compose down` | removes it |

Get the order wrong and the app server keeps a worker competing for the same queue on a machine with no
GPU - transcriptions silently fall to CPU (0.56-0.60x realtime, measured; slower than the meeting itself) -
while being invisible to `docker compose ps` habits and immune to `down`.

Hence the cutover sequence in section 10 tears down **with the profile still enabled**, and the
`.env.example` comment says so.

The same mechanic is a one-time hazard for any existing deployment that pulls this change without adding
the line: their worker keeps running until their next `docker compose down`, which then silently fails to
remove it. The implementing PR's release notes must lead with this.

### 4. Redis: exposure and a password

Redis is currently not published at all. It carries eight streams, most of which are internal to the API,
so exposing it exposes far more than transcription.

```yaml
  redis:
    command: ["redis-server", "--appendonly", "yes", "--requirepass", "${REDIS_PASSWORD:-}"]
    environment:
      REDIS_PASSWORD: ${REDIS_PASSWORD:-}
    ports:
      - "${REDIS_BIND:-127.0.0.1}:${REDIS_PORT:-6379}:6379"
```

`REDIS_BIND` defaults to `127.0.0.1`, so a single-box install is unchanged in practice; the split
deployment sets it to the app server's LAN address.

**An empty `--requirepass` means no password** (measured: the container starts, unauthenticated `ping`
returns `PONG`). So an existing `.env` with no `REDIS_PASSWORD` keeps working exactly as before. Good -
that removes one upgrade hazard.

**The healthcheck is a silent false-green and must change.** `redis-cli ping` against a password-protected
Redis prints `NOAUTH Authentication required.` **and exits 0** (measured). The current healthcheck would
therefore report the service healthy while rejecting every client, and `depends_on: service_healthy` would
be satisfied by a Redis nothing can talk to. Replace it:

```yaml
    healthcheck:
      test: ["CMD-SHELL", "redis-cli ${REDIS_PASSWORD:+--no-auth-warning -a \"$$REDIS_PASSWORD\"} ping | grep -q PONG"]
```

Two parts, both load-bearing. `| grep -q PONG` is what turns a `NOAUTH` into a failure at all. The
`${VAR:+...}` expansion adds `-a` only when a password is set, so a passwordless install does not spew
`AUTH failed: ERR AUTH <password> called without any password configured` every five seconds. Verified
across all four states: no password, correct password, wrong password (correctly fails), and the
unauthenticated case against a protected server (correctly fails).

The API side needs no code change - `JobQueue__RedisConnection` is a StackExchange.Redis connection string:

```yaml
      JobQueue__RedisConnection: "redis:6379,password=${REDIS_PASSWORD:-}"
```

and the worker's is a URL: `redis://:PASSWORD@APP_HOST:6379/0`.

### 5. MinIO: exposure and a scoped service account

MinIO's S3 API is already published on host 9002 across all interfaces, so the worker can reach it today.
What it must **not** get is `MINIO_ROOT_USER`.

This repo already has the pattern, and it should be copied rather than reinvented:
`deploy/provision-glitchtip-minio.sh` + `ProvisionGlitchTipMinio.cmd` + `deploy/glitchtip-minio/provision.sh`.
That trio generates a hex secret on the host, pipes a script into the running `minio` container (so `mc`
need not be installed, the published port is not depended on, and the root credentials never reach a host
command line or shell history), creates a scoped policy and service account, and then **proves the boundary
before exiting**.

The new equivalent - `deploy/provision-worker-minio.sh`, `deploy/ProvisionWorkerMinio.cmd`,
`deploy/worker-minio/provision.sh` - creates `worker-svc` with a policy over the `recordings` bucket only:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Action": ["s3:GetObject", "s3:PutObject",
               "s3:AbortMultipartUpload", "s3:ListMultipartUploadParts",
               "s3:ListBucketMultipartUploads"],
    "Resource": ["arn:aws:s3:::recordings", "arn:aws:s3:::recordings/*"]
  }]
}
```

Get and Put only. The worker downloads audio (`storage.download`) and uploads merged audio
(`storage.upload`); it never lists and never deletes. The multipart actions are there because boto3's
`upload_file` switches to multipart above its threshold - and because the exact set is a guess until it is
exercised, the provisioning script must **prove it with a real round trip** (put an object with the new
key, get it back, then remove it as root) rather than assume, exactly as the GlitchTip script proves its
own boundary.

Be honest about what this buys. The worker's whole job is the recordings bucket, so scoping does not hide
the audio from it. What it removes is everything else: no admin API (cannot create buckets, users, or
policies), no access to the `glitchtip` bucket, and **no `DeleteObject`** - so a compromised or
misconfigured GPU box cannot erase every recording on the platform. That is a real reduction in blast
radius, not a cosmetic one, but it is not confidentiality.

Add a `MINIO_BIND` knob alongside the existing port mapping, for symmetry with Redis, so a future
deployment can tighten it without editing the compose file.

### 6. The API callback

The worker POSTs to `internal/transcriptions/result`, `internal/transcriptions/failure`, and
`internal/recordings/merge-*`. On the LAN it reaches these directly at `http://APP_HOST:8080`, which is
already published.

**It must not go through the web origin.** `apps/web/nginx.conf` proxies `/api`, `/hubs`, `/mcp`,
`/connect` and `/.well-known` - there is no `/internal/` location, so such a request falls through to the
SPA fallback and returns `index.html` with a 200. A worker would see a successful POST and silently lose
every result.

Adding an `/internal/` location to nginx would "fix" that and is the wrong move: it would publish the
callback endpoints on the public HTTPS origin, where the only thing between the internet and arbitrary
transcript injection is `X-Worker-Secret`. Direct `:8080` on the LAN is both simpler and safer. State this
as a warning in the synopsis, because it is the natural thing for a future reader to try.

Accepted consequence: the callback secret and the transcript bodies cross the LAN in plaintext.

### 7. The GPU box gets its own, much smaller env file

A new `deploy/.env.worker.example`, roughly:

```bash
APP_HOST=192.168.1.50          # the app server on the LAN
REDIS_PASSWORD=
REDIS_PORT=6379
MINIO_PORT=9002
MINIO_WORKER_ACCESS_KEY=worker-svc
MINIO_WORKER_SECRET_KEY=
CALLBACK_SECRET=
HF_TOKEN=
WORKER_DEVICE=cuda
WORKER_COMPUTE_TYPE=float16
WHISPER_MODEL=large-v3
BATCH_SIZE=16
MAX_AUDIO_SECONDS=14400
ENABLE_SPEAKER_EMBEDDINGS=1
CONSUMER_NAME=gpu-1
```

Deliberately **not** a copy of the app server's `.env`. Today's file carries `JWT_KEY`, the Postgres
password, SMTP credentials, LLM API keys, the seed administrator's password and the GlitchTip token - none
of which a transcription machine has any use for. After the split, the GPU box holds exactly three platform
secrets - the Redis password, the scoped MinIO key, and `CALLBACK_SECRET` - two of which are scoped to one
queue and one bucket, plus `HF_TOKEN`, which is a Hugging Face read token and grants nothing on this
platform at all.

`CALLBACK_SECRET` is the one that cannot be scoped down: it authorises writing transcription results for
*any* transcription id. That is inherent to the current callback contract, not something this design
weakens, and it is the reason the callback stays on `:8080` inside the LAN rather than on the public origin
(section 6).

`CONSUMER_NAME` is set explicitly (`gpu-1`, not the `worker-1` default) so that pending-entry ownership in
the Redis consumer group names a real machine.

### 8. Model cache

The GPU box needs its own `workercache` volume and its own `HF_TOKEN`, with the pyannote 3.1 and
segmentation-3.0 terms accepted on that Hugging Face account. First boot re-downloads ~4 GB of models; the
volume makes that a one-off. `start_period: 120s` on the worker healthcheck already covers the first-boot
import, but the *download* is slower than that on a cold cache - expect the first container to report
unhealthy for a few minutes before settling. Worth a line in the runbook so it is not mistaken for a fault.

### 9. What is actually testable

Honest accounting, because most of this is deployment configuration and configuration tests are famously
capable of passing while the thing is broken.

**Worth writing** - `src/Diariz.Worker/tests/test_compose_worker_parity.py`, alongside the existing
`test_dockerfile_pins.py`:

- Parse the `worker` service from every compose file that defines one (`docker-compose.yml`,
  `docker-compose.worker.yml`, and the ROCm pair) and assert the sets of environment keys match.
- Assert the *values* match too, except for an explicit allowlist of keys that must differ: `REDIS_URL`,
  `S3_ENDPOINT`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`, `API_BASE_URL`, `CONSUMER_NAME`, and `ASR_BACKEND`
  (hardcoded on the ROCm side by design).
- Assert `docker-compose.worker.yml` declares no `depends_on`, since it defines no other services - the
  failure mode measured in section 2, caught at test time instead of on the GPU box.

This is a cross-file equality check, which is the one shape of configuration test that earns its place: it
catches drift, and drift is invisible to any amount of live testing on a single machine. It proves nothing
about whether the configuration *works*.

**Not worth writing** - a test asserting the Redis healthcheck string contains `grep -q PONG`. It would
pass against a healthcheck that is subtly wrong in some other way, and the real evidence is the measured
four-state matrix in section 4, which belongs in the doc rather than in an assertion.

**Proven only by the cutover** (section 10's verification steps): that Redis is reachable and
authenticating from the GPU box, that the scoped MinIO key can round-trip a blob, that the callback lands
on the API rather than nginx's SPA fallback, and that a transcription completes end to end. None of these
have a unit-test equivalent, and pretending otherwise is how a green suite ships a broken deployment.

**No .NET or web tests change.** There is no code change on either side.

### 10. Cutover sequence

Order matters at step 4; see section 3.

1. **App server, prepare.** Add `REDIS_PASSWORD` (generate hex), `REDIS_BIND=<lan-ip>`, and
   `COMPOSE_PROFILES=local-worker` (still enabled) to `deploy/.env`.
2. **App server, provision MinIO.** `./provision-worker-minio.sh`. Record the printed
   `MINIO_WORKER_SECRET_KEY`; it is not stored anywhere else.
3. **Drain.** Let any in-flight transcription finish. Anything still pending is safe - `reclaim_stale`
   picks it up on the other machine after `RECLAIM_MIN_IDLE_MS` (10 minutes).
4. **App server, tear down with the profile still on:**
   `COMPOSE_PROFILES=local-worker docker compose down`
5. **App server, flip the knob** to `COMPOSE_PROFILES=` in `.env`, then `docker compose up -d`. Confirm no
   worker container exists: `docker ps -a --filter name=diariz-worker` returns nothing.
6. **GPU box.** Clone the repo, copy `deploy/.env.worker.example` to `deploy/.env`, fill in `APP_HOST` and
   the four credentials (`REDIS_PASSWORD`, `MINIO_WORKER_SECRET_KEY`, `CALLBACK_SECRET`, `HF_TOKEN`), then
   `docker compose -f docker-compose.worker.yml up -d --build`.
7. **Verify, in order** - each step isolates one of the three links:
   - Redis: from inside the worker container, `redis.Redis.from_url(os.environ["REDIS_URL"]).ping()`.
   - MinIO: same container, a boto3 `head_object` against a known blob key.
   - Callback: `curl -s -o /dev/null -w '%{http_code}' -X POST http://APP_HOST:8080/internal/transcriptions/failure`
     with no secret header - expect **401**, not 200. A 200 with HTML means the request reached nginx and
     the SPA fallback, i.e. the wrong port.
   - End to end: upload a short recording in the web app and watch it reach Completed.

### 11. Rollback

Stop the GPU box's worker first (`docker compose -f docker-compose.worker.yml down`), then on the app
server set `COMPOSE_PROFILES=local-worker` and `docker compose up -d`. The Redis password and scoped MinIO
key can stay - both work fine in a single-box stack, and leaving them avoids a second config change under
pressure. Nothing in the database or object store is touched by either direction.

## Known gaps

- **No cross-machine visibility.** If the GPU box is off, recordings sit in `Transcribing` indefinitely with
  no signal anywhere in the UI. This is true today, but co-location made it self-evident; a separate machine
  makes it a silent failure. There is no queue-depth or worker-liveness surface in the admin area to build
  on. Worth a follow-up (a worker heartbeat key in Redis, read by an admin health panel) and worth recording
  in the deferred-work list rather than growing this design.
- **Plaintext on the LAN.** Audio, transcripts, and the callback secret. Accepted for a private network; it
  is the first thing to revisit if either machine moves.
- **Four copies of the worker service** once the ROCm variants exist, held together by a parity test rather
  than by construction. `extends` would have been the structural fix and is unavailable (section 2).

## Files touched

| File | Change |
|---|---|
| `deploy/docker-compose.yml` | `profiles:` on worker; Redis password, ports, healthcheck; `MINIO_BIND` |
| `deploy/docker-compose.rocm.yml` | the same Redis and MinIO changes, for parity |
| `deploy/docker-compose.worker.yml` | **new** - worker service only, NVIDIA |
| `deploy/docker-compose.worker.rocm.yml` | **new** - worker service only, ROCm |
| `deploy/.env.example` | `COMPOSE_PROFILES`, `REDIS_PASSWORD`, `REDIS_BIND`, `REDIS_PORT`, `MINIO_BIND` |
| `deploy/.env.worker.example` | **new** - the GPU box's much smaller env |
| `deploy/provision-worker-minio.sh` | **new** - mirrors the GlitchTip wrapper |
| `deploy/ProvisionWorkerMinio.cmd` | **new** - Windows wrapper |
| `deploy/worker-minio/provision.sh` | **new** - runs inside the minio container, proves the boundary |
| `src/Diariz.Worker/tests/test_compose_worker_parity.py` | **new** - the drift guard |
| `docs/Overall_Synopsis_of_Platform.md` | new deployment topology; the nginx `/internal/` warning |
| `src/Diariz.Worker/README.md` | the runbook: cutover, verification, rollback |
| `apps/web/src/lib/releases.ts` | release entry leading with the `COMPOSE_PROFILES` upgrade note |
| `version.json` + its four mirrors | Build +1 (this is infrastructure, not a functional enhancement). `versionMirrors.test.ts` fails the build if any mirror drifts |

Deployment surface: **server redeploy only** - no desktop release (nothing under `apps/desktop/**`).
