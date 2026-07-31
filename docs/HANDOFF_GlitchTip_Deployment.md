# Handoff: GlitchTip deployment

**Transient. Delete this file once prod is done** - it is session state, not a reference. The durable material lives in `docs/GlitchTip_Deployment.md`, which is now accurate and was written from what actually worked rather than from upstream docs.

Updated 2026-07-31 at repo version **0.174.13**, `main` clean, PRs #401-#407 all merged.

---

## State: dev is deployed and working

Every blocker is cleared. What remains is one rebuild, then verification, then prod.

| Step | State |
| --- | --- |
| Secrets, MinIO bucket + scoped key, proxy | done |
| Container up, **migrations applied**, task worker embedded | done |
| SMTP | **working** - proven by a real SMTP login |
| First account (`ken@stocks-hayward.com`) + org `diariz` | done |
| Team `diariz`, three projects with DSNs | done |
| DSNs in `.env`, app services reporting | done |
| Both proxy hosts in NPM | done |
| Source-map upload | **fixed in the repo (#406, #407), NOT yet proven on a real build** |
| **Verification checklist** | **in progress - this is the task** |
| **Prod recreate** | not started |

Proven live on dev, so do not re-test these:

- A real envelope through NPM returns 200 and becomes an issue.
- The API reports transactions over the in-network DSN, correctly parameterised as `GET /api/recordings/{id:guid}`.
- A 10 MB POST returns 403 (auth), not 413 (size), so the proxy body limit is fine for source maps.
- `sentry-cli` uploads and the server assembles the bundle, with the debug ID linked.

---

## Do this first

**Rebuild the web image** (`deploy\BringUpWebApi.cmd`), then confirm the maps actually landed. The build cannot tell you - see the async trap below - so ask the server:

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml exec glitchtip ./manage.py shell -c "
from apps.files.models import File
from apps.sourcecode.models import DebugSymbolBundle
print('files=%d bundles=%d' % (File.objects.count(), DebugSymbolBundle.objects.count()))"
```

Both are at **0** right now (test artifacts were cleaned up), so any non-zero result is the real upload landing. For ~92 bundles expect files in the high hundreds. Zero means assembly failed - the reason is in `docker compose logs glitchtip`, searching `assemble_artifacts`.

---

## Four things that cost real time - do not rediscover

**Migrations do not run by themselves.** The image entrypoint only runs `./manage.py migrate` when the Heroku `DYNO` variable is set, which Compose never sets. The symptom is not "migrations did not run": it is a login page that renders perfectly with **no register link**, because the settings API behind it 500s on a missing table. Two sessions went into adjusting registration flags. Fixed by `GLITCHTIP_EMBED_WORKER: "true"` (#401), which also embeds the task worker and creates the event partitions - both separately broken.

**`EMAIL_URL` needs a TLS scheme.** `new-secrets.ps1` hardcoded `smtp://`, so no URL it generated could authenticate anywhere. It surfaced as a 500 on the *first registration*, which created the user and then failed sending its verification mail - stranding the account and closing the first-run registration window behind it. Fixed in #402 (`smtp+tls`, or `smtp+ssl` on 465).

**`@glitchtip/cli` cannot upload source maps.** Version 1.0.0 is the only one ever published, and it uploads each file individually then calls `releases/{version}/assemble/` once per file - an endpoint that expects a single zip of every artifact plus a `manifest.json`. The server raises `BadZipFile` per artifact and registers nothing. `--release` is mandatory and selects that path, so no flag avoids it. Measured: `@glitchtip/cli` left 185 blobs, 0 files, 0 bundles; `sentry-cli` on the same server produced a bundle that assembled. Fixed in #407 by switching to a pinned `@sentry/cli@3.6.2`. **Do not "simplify" this back to GlitchTip's own CLI.**

**A restart does not re-read the environment.** `docker compose restart` reuses the container, so #406's new `AWS_STORAGE_BUCKET_NAME` never reached the process and uploads kept failing after the fix had landed. Use `up -d --force-recreate`, and when a fix "does not work", diff what compose *would* apply against what the container *has*:

```bash
docker compose -f ... config          # what should be set
docker compose -f ... exec glitchtip env   # what actually is
```

The pattern across the first three: a symptom pointing at a *policy* (a missing link, a permission) or at your own config, when the thing underneath was simply broken or stale. Check the layer below before adjusting the layer above.

---

## The async trap, which no build check can catch

Source-map **upload** and **assembly** are separate steps and only the first is synchronous. The server accepts the upload, returns 200 "pending", and assembles in a background task - long after the CLI has exited 0. So a green build honestly means "uploaded" and never "assembled". This is why the original failure was invisible: the build passed while every artifact was being rejected server-side. If traces are minified after a passing build, the answer is on the server, not in the build log.

---

## Decisions taken, so they are not reopened

- **Recording GUIDs in browser transaction names are accepted.** `beforeSendTransaction` does not scrub `event.transaction`, so names arrive as `/recordings/<guid>` rather than `/recordings/:id`. Reviewed and accepted: the GUIDs are anonymous. The consequence is cardinality - a distinct Performance entry per recording. If that view gets noisy, the fix is route parameterisation via the React Router tracing integration, not another scrubber rule.
- **`GLITCHTIP_BIND` stays `0.0.0.0`.** Deliberate: a fixed IP in `.env` is invisible to whoever runs the network and rots when the host is re-addressed, so the restriction belongs at the network layer. The runbook presents both options as a real choice.

---

## Outstanding

**The verification checklist** in `docs/GlitchTip_Deployment.md`. Notes that will save time:

- **Check 9 is the one that matters** - open a captured event and read the *whole* payload: no transcript text, no `access_token`, no voiceprint vector, no request body. Eight leak paths were found this way during the build; a ninth would be found the same way. Read the raw JSON, not the UI's sections:
  `https://errors.dev.diariz.stocks-hayward.com/api/0/issues/<id>/events/latest/` (works in a logged-in browser tab).
- **There is already a real event waiting for it**: issue 2 in `diariz-web`, `Error: scrubber check`, thrown from the browser console. It has genuine breadcrumbs and request context. It was deliberately left in place when the test artifacts were cleaned up.
- **Check 7 should now resolve to a readable frame** rather than a minified one - but only after the web rebuild above.
- **Check 8 (trace propagation) cannot be verified from the database.** DuckDB is enabled, so spans go to Parquet and no Postgres table carries a `trace_id`. It has to be a UI comparison of two spans' trace ids.
- Issues **soft-delete** (`Issue` extends `SoftDeleteModel`): the row stays with `is_deleted=True` and vanishes from the UI. A row still present in `Issue.objects` is not a failed delete - check `Issue.undeleted_objects`.

**Then the prod recreate.** Two things must differ from dev: fresh secrets (`NewGlitchTipSecrets.cmd` runs once per server - a shared `SECRET_KEY` would let a signed value from dev replay against prod) and `SENTRY_ENVIRONMENT=production`. Everything else follows the runbook, which now covers the team step, both proxy hosts, the `ALLOWED_HOSTS` trap and the CLI choice.

**`GLITCHTIP_ALLOWED_HOSTS`** is available but unset, so the wildcard warning still logs on every boot. If you set it, it **must** include `glitchtip` alongside the public hostname - the API and worker post in-network as `Host: glitchtip`, and Django drops any host not listed, stopping server-side reporting silently while the browser keeps working.

**Rotate the MinIO scoped key.** It was printed in full into a session transcript (`docker compose config` output). Re-run `ProvisionGlitchTipMinio.cmd`, paste the new values into `.env`, recreate `glitchtip`. The GlitchTip Postgres password was exposed the same way but is lower priority - that container publishes no host port.

---

## Environment notes for this box

| Thing | Detail |
| --- | --- |
| **Node IS now installed** | v24.18.1 / npm 11.16.0, but **not on the tool shell's PATH** - prefix with `export PATH="$PATH:/c/Program Files/nodejs"`. The web vitest suite can run here now |
| **Git identity was unset** | Set repo-local to `Ken Hayward <kenhayward@hotmail.com>`, matching the bulk of history |
| Host LAN address | `192.168.1.49` |
| The `minio` container | Busybox: no `grep`, `sed` or `which`. Text processing on the host |
| The `glitchtip` container | Full Python image - it *does* have grep, and `./manage.py shell -c` is the fastest way to ask the database a direct question |
| Piping to `grep` | Everything after a `\|` runs on the **host**, so it fails in PowerShell. Filter inside the container instead |
| `diffsettings` / `compose config` | Both print secrets in full. Never dump them unfiltered - two credentials leaked into a transcript that way |
| GlitchTip image tags | No `v` prefix from 6.0.4 onward. `6.2` is right, `v6.2` does not exist |
| `docker compose ps --services` | LF-only line endings even on Windows, so `findstr /x` never matches. Use `docker compose ps -q <svc>` |
| The six required env vars | Guarded with `${VAR:?}`, so `docker compose down` needs them present too |
