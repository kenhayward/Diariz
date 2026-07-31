# Handoff: GlitchTip deployment, in progress

**Transient. Delete this file once the deployment is finished** - it is session state, not a reference. The durable material lives in `docs/GlitchTip_Deployment.md`.

Written 2026-07-31, at repo version **0.174.8**, `main` clean with nothing open.

---

## Read this first: the live blocker

**GlitchTip is running and reachable, but there is no way to log in.** No account exists and the login page offers no way to create one.

### What is known, and how confident

| Fact | Confidence |
| --- | --- |
| The container starts, migrations ran, the login page renders over HTTPS | **observed** |
| The login page has **no register/sign-up link** | **observed** |
| Setting `ENABLE_USER_REGISTRATION: "true"` and recreating the container did **not** make a register link appear | **observed** |
| `deploy/docker-compose.observability.yml` also sets `ENABLE_ORGANIZATION_CREATION: "false"` | **in the file** |
| GlitchTip's docs say `ENABLE_USER_REGISTRATION=false` disables signup *"after the first user is registered"*, implying the first should be allowed | **read in docs, contradicted by observation** |
| `./manage.py createsuperuser` is the documented way to make the first user | **read in docs, not yet tried here** |
| A `createsuperuser` account may land with **no organization**, since org creation is disabled - and the three projects need an org | **speculation, unverified** |

The last row matters. It has been raised twice as a risk and never checked. Check it rather than repeating the guess.

### Run these first

All from `deploy/`, with both compose files:

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml exec glitchtip env | grep -i "regist\|organiz"
```
Settles whether the changed value ever reached the process, as opposed to being changed in the file and not applied.

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml exec glitchtip ./manage.py diffsettings | grep -i "regist\|organiz"
```
Shows what Django actually resolved, and the **real setting names for 6.2** - the names in the compose file came from the install docs, and have not been verified against this image.

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml logs glitchtip | tail -50
```

Also worth knowing: GlitchTip's frontend is an SPA that fetches settings from the API. A stale register link - or a missing one - can be **browser cache** rather than server state. Hard-reload before concluding anything from the page.

### Most likely unblock

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml exec glitchtip ./manage.py createsuperuser
```

Prompts for email and password; GlitchTip identifies users by email, not username. If `./manage.py` is not on that path in the 6.2 image, find it rather than guessing - the image layout has not been inspected.

After signing in, **check whether an organization exists**. If not, that is the second half of the problem, and `/admin/` (available to a superuser) is the likely route.

---

## Where the deployment has got to

| Step | State |
| --- | --- |
| Secrets generated, `.env` populated | done |
| MinIO bucket + scoped key provisioned | done - the script verified the key cannot read `recordings` |
| Proxy configured (Nginx Proxy Manager, remote) | done |
| Container up, migrations run | done |
| **First user / organization** | **blocked - this is the task** |
| Three projects (`diariz-worker`, `diariz-api`, `diariz-web`) | not started |
| Pass 2: DSNs into `.env`, restart app services | not started |
| Pass 3: source-map credentials | not started |
| Verification checklist | not started |

The full procedure is `docs/GlitchTip_Deployment.md`. It is accurate as far as the deployment has gone - two gaps in it were found and fixed today, and a third is open (below).

---

## Known gaps in the deployment doc

**Open, not yet fixed:** section 1f says "In the UI, create your account" as though that is self-evident. The overlay disables registration, so it is not. Once the route above is known to work, 1f needs rewriting to say exactly how the first user and organization get created. **Do not write it from the docs - write it from what actually worked on this box.**

Two already fixed today, for context on the failure pattern:

- `GLITCHTIP_SECRET_KEY` appeared only in prose while its neighbours were in code blocks, so it was missed; the container then started and failed every request with a Django traceback pointing nowhere near it. Fixed with a variable table, a pre-flight check, and `${VAR:?message}` guards on all six required values.
- The image tag was `v6.2`, which does not exist - GlitchTip dropped the `v` prefix after 6.0.3.

---

## Gotchas already paid for - do not rediscover

| Thing | Detail |
| --- | --- |
| GlitchTip image tags | No `v` prefix from 6.0.4 onward. `6.2` is right, `v6.2` does not exist |
| The `minio` container | Minimal busybox: **no `grep`, `sed` or `which`**. Text processing must happen on the host |
| `docker compose ps --services` | Emits **LF-only** line endings even on Windows, so `findstr /x` never matches. Use `docker compose ps -q <svc>` |
| `.sh` files | `.gitattributes` forces LF. On a fresh Windows clone check `git ls-files --eol` shows `w/lf`, or shebangs break with `bad interpreter: ...^M` |
| The six required env vars | Now guarded with `${VAR:?}`. Note this makes `docker compose down` need them present too |
| Hardcoded dates in tests | One expired today and broke CI on every branch. Anything compared against `UtcNow` must be relative |

---

## What exists, in one paragraph

Optional self-hosted error tracking and performance monitoring across all three runtimes: the Python worker, the .NET API, and the React SPA. Built over two phases (PRs #389-#399), all merged. Everything is inert unless a DSN is configured. Deployed as an opt-in compose overlay with its own Postgres, its own MinIO bucket, and the app's Redis on DB index 1.

Each runtime has a scrubber that redacts credentials and meeting content before anything is transmitted; the three deny-lists are kept in sync by paired tests and carry comments saying so. **Eight separate disclosure paths were found and closed during the build** - transcripts, voiceprints and the SignalR JWT all had routes out that a key-name deny-list could not reach.

---

## Not verified by anyone, and only reachable from this box

1. **Does a captured event contain anything it should not?** Open one and read the whole payload: no transcript text, no `access_token`, no voiceprint vector, no request body. This is the gate that matters most - eight leaks were found by inspection, and a ninth would be found the same way.
2. **Does GlitchTip resolve a stack trace against the uploaded source maps?** The upload wiring is proven end to end; the server side is not.
3. **Does the proxy preserve `sentry-trace` and `baggage`?** This **fails silently** - the only symptom is browser and API spans arriving as two unlinked traces instead of one.

---

## Working style that has been productive

Verify against the actual system rather than documentation or memory. Most errors this session came from assuming a convention held: the image tag, the `grep` in the MinIO container, `findstr` line endings, `import * as Sentry` tree-shaking, an SDK option that does not exist in the installed version. Every one was caught by running something.

When something fails, get the *evidence* before the fix - the CI failure today was diagnosed by noticing one run passed at 18:5x and the next failed at 19:01, which identified a hardcoded timestamp rather than a code change.
