# Handoff: GlitchTip deployment, in progress

**Transient. Delete this file once the deployment is finished** - it is session state, not a reference. The durable material lives in `docs/GlitchTip_Deployment.md`.

Updated 2026-07-31 on the dev box, at repo version **0.174.9**.

---

## The login blocker is resolved

**Cause: the database was never migrated.** Not a registration-policy problem, which is where the previous session spent its time.

The image's entrypoint (`bin/start.sh`) only runs `./manage.py migrate` when the Heroku `DYNO` environment variable is set. Under Compose it never is, so the plain `web` role started against a completely empty database - `showmigrations` showed **zero** applied.

The symptom was indirect enough to send the last session down the wrong path entirely. The login page is a **static SPA**, so it rendered perfectly. The register link was missing because the settings call behind it was 500ing on `relation "socialaccount_socialapp" does not exist`. That looked exactly like a permissions setting, and every hypothesis about `ENABLE_USER_REGISTRATION` was chasing it.

**Fix:** `GLITCHTIP_EMBED_WORKER: "true"` in the overlay. That flips `SERVER_ROLE` to `all_in_one`, which runs `migrate` + `maintain_partitions` + `createcachetable` on every boot and embeds the task worker in the web process.

Two things that were also broken and would have surfaced later, separately:

- **No task worker at all.** The `web` role runs none. Alert emails, uptime checks and cleanup would silently never have fired.
- **No event partitions.** Issue events live in partitioned tables; `maintain_partitions` creates them. Ingest would have failed later, looking like an unrelated bug.

### Both lockdown flags were innocent, and are back to `false`

Verified in the 6.2 source, not inferred:

- `apps/users/utils.py` - `ENABLE_USER_REGISTRATION or not User.objects.exists()`
- `apps/organizations_ext/utils.py` - `ENABLE_ORGANIZATION_CREATION or not Organization.objects.aexists()`, and `api.py:92` exempts superusers permanently
- The Angular frontend applies the same rule: `enableOrgCreation || user?.isSuperuser || orgCount === 0`

So the previous session's flagged risk - *"a `createsuperuser` account may land with no organization, and org creation is disabled"* - **is false**. The first org is always creatable. `createsuperuser` is not needed at all.

Confirmed live: with `ENABLE_USER_REGISTRATION=false` in the container's environment, `/api/settings/` returns `"enableUserRegistration": true`, because there are zero users.

---

## Where the deployment has got to

| Step | State |
| --- | --- |
| Secrets generated, `.env` populated | done |
| MinIO bucket + scoped key provisioned | done |
| Proxy configured (Nginx Proxy Manager, remote) | done |
| Container up, **migrations applied**, worker embedded | done |
| First user | created - `ken@stocks-hayward.com`, id 1, active, email unverified |
| **Organisation** | **not yet - sign in and create it, see below** |
| SMTP | **broken, fixed in the repo but `.env` still needs re-pasting** |
| Three projects (`diariz-worker`, `diariz-api`, `diariz-web`) | not started |
| Pass 2: DSNs into `.env`, restart app services | not started |
| Pass 3: source-map credentials | not started |
| Verification checklist | not started |

### The registration 500, and why it does not need re-registering

Signing up returned a 500 and afterwards the page said registration was unavailable. The account **was** created; the request then died sending the verification email:

```
smtplib.SMTPNotSupportedError: SMTP AUTH extension not supported by server.
```

With a user now in the table, the first-run registration window closed behind a half-finished signup.

**Cause:** `GLITCHTIP_EMAIL_URL` used a plain `smtp://` scheme. django-environ only sets `EMAIL_USE_TLS` for `smtps`/`smtp+tls` and `EMAIL_USE_SSL` for `smtp+ssl`, so the connection was clear text - and Gmail only advertises AUTH after STARTTLS. `deploy/glitchtip-secrets/new-secrets.ps1` hardcoded `smtp://`, so **no URL it ever generated could authenticate anywhere**. Fixed in the repo; the generated `.env` value still has to be replaced by hand.

**No need to register again.** `ACCOUNT_EMAIL_VERIFICATION` is `optional`, and the account is active with a usable password:

1. Sign in at the GlitchTip URL with the email and password you just chose.
2. It goes to **Create a New Organization** ("This is the first step to get started with GlitchTip"). Name it.
3. Carry on at **1f** in `docs/GlitchTip_Deployment.md` for the three projects, then pass 2 for the DSNs.

Fix the mail separately - nothing above is blocked on it, but invitations and password resets are:

```
GLITCHTIP_EMAIL_URL=smtp+tls://ken%40stocks-hayward.com:<app-password>@smtp.gmail.com:587
```

then `docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d glitchtip`. Verification checklist item 3 covers proving it.

---

## Still not verified by anyone, and only reachable from this box

1. **Does a captured event contain anything it should not?** Open one and read the whole payload: no transcript text, no `access_token`, no voiceprint vector, no request body. This is the gate that matters most - eight leaks were found by inspection, and a ninth would be found the same way.
2. **Does GlitchTip resolve a stack trace against the uploaded source maps?** The upload wiring is proven end to end; the server side is not.
3. **Does the proxy preserve `sentry-trace` and `baggage`?** This **fails silently** - the only symptom is browser and API spans arriving as two unlinked traces instead of one.

One more, noticed while fixing the above and **not** addressed: the container logs `ALLOWED_HOSTS is the wildcard default. Restrict to known hostnames via the ALLOWED_HOSTS env var`. It is on a public subdomain, so that is worth closing before this counts as finished. Left alone deliberately - it is a separate change from the migration fix.

---

## Gotchas already paid for - do not rediscover

| Thing | Detail |
| --- | --- |
| Migrations | Do NOT assume the container migrates itself. Check `showmigrations`, and see the note above |
| GlitchTip image tags | No `v` prefix from 6.0.4 onward. `6.2` is right, `v6.2` does not exist |
| The `minio` container | Minimal busybox: **no `grep`, `sed` or `which`**. Text processing must happen on the host |
| The `glitchtip` container | A full Python image - it *does* have `grep`, and `./manage.py shell -c` is the fastest way to ask the database a direct question |
| `docker compose ps --services` | Emits **LF-only** line endings even on Windows, so `findstr /x` never matches. Use `docker compose ps -q <svc>` |
| `.sh` files | `.gitattributes` forces LF. On a fresh Windows clone check `git ls-files --eol` shows `w/lf` |
| The six required env vars | Guarded with `${VAR:?}`. Note this makes `docker compose down` need them present too |
| Hardcoded dates in tests | One expired and broke CI on every branch. Anything compared against `UtcNow` must be relative |
| **Node is not installed on this box** | The web vitest suite cannot run here. Rely on CI, and say so in the PR |

---

## Working style that has been productive

Verify against the actual system rather than documentation or memory. Every error across these sessions came from assuming a convention held - the image tag, `grep` in the MinIO container, `findstr` line endings, and this time an entrypoint that looked like it migrated and only does so on Heroku.

The lesson from this round specifically: when a symptom points at a *policy* (a missing link, a permission), check that the thing underneath is even working before adjusting the policy. Two sessions of flag-flipping were spent on a 500 that one `curl` of the settings endpoint would have exposed immediately.
