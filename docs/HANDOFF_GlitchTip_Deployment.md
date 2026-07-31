# Handoff: GlitchTip deployment

**Transient. Delete this file once prod is done** - it is session state, not a reference. The durable material lives in `docs/GlitchTip_Deployment.md`, which is now accurate and was rewritten from what actually worked rather than from upstream docs.

Updated 2026-07-31 at repo version **0.174.11**, `main` clean, PRs #401-#404 all merged.

---

## State: dev is deployed and working

Every blocker is cleared. What remains is verification, then the prod recreate.

| Step | State |
| --- | --- |
| Secrets, MinIO bucket + scoped key, proxy | done |
| Container up, **migrations applied**, task worker embedded | done |
| SMTP | **working** - proven by a real SMTP login, not just config |
| First account (`ken@stocks-hayward.com`) + org `diariz` | done |
| Team `diariz`, three projects with DSNs | done |
| DSNs in `.env`, app services recreated and reporting | done |
| Both proxy hosts in NPM | done |
| **Verification checklist** | **in progress - this is the task** |
| **Prod recreate** | not started |

Proven live on dev, so do not re-test these:

- A real envelope through NPM returns 200 and becomes an issue (browser ingest path works end to end).
- The API reports transactions over the in-network DSN - 23 transaction groups, correctly parameterised as `GET /api/recordings/{id:guid}`.
- A 10 MB POST returns 403 (auth), not 413 (size), so the proxy body limit is already adequate for source maps.

---

## Two things that cost two sessions - do not rediscover

**Migrations do not run by themselves.** The image entrypoint only runs `./manage.py migrate` when the Heroku `DYNO` variable is set, which Compose never sets. The symptom is not "migrations did not run": it is a login page that renders perfectly with **no register link**, because the settings API behind it 500s on a missing table. Two sessions were spent adjusting registration flags. Fixed by `GLITCHTIP_EMBED_WORKER: "true"` (#401), which also embeds the task worker and creates the event partitions - both separately broken.

**`EMAIL_URL` needs a TLS scheme.** `new-secrets.ps1` hardcoded `smtp://`, so no URL it ever generated could authenticate anywhere. It surfaced as a 500 on the *first registration*, which created the user and then failed sending its verification mail - stranding the account and closing the first-run registration window behind it. Fixed in #402 (`smtp+tls`, or `smtp+ssl` on 465).

The pattern in both: a symptom that pointed at a *policy* (a missing link, a permission) when the thing underneath was simply broken. Check the layer below before adjusting the layer above.

---

## Decisions taken, so they are not reopened

- **Recording GUIDs in browser transaction names are accepted.** `beforeSendTransaction` does not scrub `event.transaction`, so names arrive as `/recordings/<guid>` rather than `/recordings/:id`. Reviewed and accepted: the GUIDs are anonymous. The consequence is cardinality - a distinct Performance entry per recording instead of one aggregate. If that view gets noisy later, the fix is route parameterisation via the React Router tracing integration, not another scrubber rule.
- **`GLITCHTIP_BIND` stays `0.0.0.0`.** Deliberate: a fixed IP in `.env` is invisible to whoever runs the network and rots when the host is re-addressed, so the restriction belongs at the network layer. The runbook now presents both options as a real choice rather than implying the pinned IP is correct.

---

## Outstanding

**The verification checklist** in `docs/GlitchTip_Deployment.md`. None of it has been run. Notes that will save time:

- **Check 9 is the one that matters** - open a captured event and read the *whole* payload: no transcript text, no `access_token`, no voiceprint vector, no request body. Eight leak paths were found by exactly this method during the build; a ninth would be found the same way. Read the raw JSON, not the UI's tidy sections:
  `https://errors.dev.diariz.stocks-hayward.com/api/0/issues/<id>/events/latest/` (works in a logged-in browser tab).
- **There are no error events yet**, only transactions - and transactions live under Performance, not Issues, so the Issues list looks empty. To produce a real payload with request context, throw one from the app's devtools console:
  `setTimeout(() => { throw new Error("scrubber check"); });`
- **Check 8 (trace propagation) cannot be verified from the database.** DuckDB is enabled, so spans go to Parquet and no Postgres table carries a `trace_id`. It has to be a UI comparison of two spans' trace ids.
- A synthetic test issue titled "NPM proxy reachability test - safe to delete" is still in `diariz-web`.

**Then the prod recreate.** Two things must differ from dev: fresh secrets (`NewGlitchTipSecrets.cmd` runs once per server - a shared `SECRET_KEY` would let a signed value from dev replay against prod) and `SENTRY_ENVIRONMENT=production`. Everything else follows the runbook, which now includes the team step, both proxy hosts, and the `ALLOWED_HOSTS` trap.

**`GLITCHTIP_ALLOWED_HOSTS`** is available but unset, so the wildcard warning still logs on every boot. If you set it, it **must** include `glitchtip` alongside the public hostname - the API and worker post in-network as `Host: glitchtip`, and Django drops any host not listed, which stops server-side reporting silently while the browser keeps working.

---

## Environment notes for this box

| Thing | Detail |
| --- | --- |
| **Node is not installed** | The web vitest suite cannot run here. CI covers it; say so in the PR |
| **Git identity was unset** | Set repo-local to `Ken Hayward <kenhayward@hotmail.com>`, matching the bulk of history |
| Host LAN address | `192.168.1.49` |
| The `minio` container | Busybox: no `grep`, `sed` or `which`. Text processing on the host |
| The `glitchtip` container | Full Python image - it *does* have grep, and `./manage.py shell -c` is the fastest way to ask the database a direct question |
| Piping to `grep` | Everything after a `\|` runs on the **host**, so it fails in PowerShell. Filter inside the container instead |
| GlitchTip image tags | No `v` prefix from 6.0.4 onward. `6.2` is right, `v6.2` does not exist |
| `docker compose ps --services` | LF-only line endings even on Windows, so `findstr /x` never matches. Use `docker compose ps -q <svc>` |
| The six required env vars | Guarded with `${VAR:?}`, so `docker compose down` needs them present too |
