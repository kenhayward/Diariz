# Handoff: GlitchTip deployment

**Transient. Delete this file once prod is done** - it is session state, not a reference. The durable material lives in `docs/GlitchTip_Deployment.md`, which is accurate and was written from what actually worked rather than from upstream docs.

Updated 2026-08-01 at repo version **0.175.1**, `main` clean, PRs #401-#411 all merged, nothing open.

---

## State: dev is deployed and working

Every blocker is cleared. What remains is one rebuild, then verification, then prod.

| Step | State |
| --- | --- |
| Secrets, MinIO bucket + scoped key, proxy | done |
| Container up, **migrations applied**, task worker embedded | done |
| SMTP | **working** - proven by a real SMTP login |
| First account (`ada@example.com`) + org `diariz` | done |
| Team `diariz`, three projects with DSNs | done |
| DSNs in `.env`, app services reporting | done |
| Both proxy hosts in NPM | done |
| Source maps | **done and proven** - 93 bundles assembled for 0.174.13 |
| LLM call telemetry (#410, #411) | **in the repo; needs one API rebuild to see the token bands** |
| **Verification checklist** | **in progress - this is the task** |
| **Prod recreate** | not started |

Proven live on dev, so do not re-test these:

- A real envelope through NPM returns 200 and becomes an issue.
- The API reports transactions over the in-network DSN, correctly parameterised as `GET /api/recordings/{id:guid}`.
- A 10 MB POST returns 403 (auth), not 413 (size), so the proxy body limit is fine for source maps.
- `sentry-cli` uploads and the server assembles the bundle, with the debug ID linked.
- LLM spans arrive correctly parented: `summarize` 1697 ms, `minutes` 1960 ms, `embed` 2766 ms.

---

## Do this first

**Rebuild the API** (`deploy\BringUpWebApi.cmd`) to pick up #411, then run a **summary or minutes** - not a formula, which is the streaming path. The span breakdown should show `POST .../v1/chat/completions (~1k tokens)`.

---

## Five things that cost real time - do not rediscover

**Migrations do not run by themselves.** The image entrypoint only runs `./manage.py migrate` when the Heroku `DYNO` variable is set, which Compose never sets. The symptom is not "migrations did not run": it is a login page that renders perfectly with **no register link**, because the settings API behind it 500s on a missing table. Two sessions were spent adjusting registration flags. Fixed by `GLITCHTIP_EMBED_WORKER: "true"` (#401), which also embeds the task worker and creates the event partitions - both separately broken.

**`EMAIL_URL` needs a TLS scheme.** `new-secrets.ps1` hardcoded `smtp://`, so no URL it generated could authenticate anywhere. It surfaced as a 500 on the *first registration*, which created the user and then failed sending its verification mail - stranding the account and closing the first-run registration window behind it. Fixed in #402.

**`@glitchtip/cli` cannot upload source maps.** Version 1.0.0 is the only one published, and it assembles per file against an endpoint that expects a single zip of every artifact - so the server raises `BadZipFile` per artifact and registers nothing, while the CLI exits 0 because the upload part worked. `--release` is mandatory and selects that path, so no flag avoids it. Fixed in #407 with a pinned `@sentry/cli`. **Do not "simplify" this back to GlitchTip's own CLI.**

**A restart does not re-read the environment.** `docker compose restart` reuses the container, so #406's new `AWS_STORAGE_BUCKET_NAME` never reached the process and uploads kept failing after the fix had landed. Use `up -d --force-recreate`, and when a fix "does not work", diff `docker compose config` against `docker compose exec <svc> env`.

**GlitchTip stores no span-level attributes.** The span parquet schema is `(organization_id, project_id, transaction_name, span_id, transaction_id, op, description, duration, timestamp)` and nothing else, so anything set via `SetExtra` is transmitted by the SDK and dropped on ingest. That is why token counts ride in the **description**, bucketed into six size bands - the breakdown query is `GROUP BY op, description`, so exact per-call counts would give every call its own group and destroy the averages. Fixed in #411.

The pattern across all five: a symptom pointing at a *policy* or at your own config, when the layer underneath was broken, stale, or simply did not store what was assumed. Check the layer below before adjusting the layer above.

---

## Reading the Performance view without being misled

- **Transactions are in Postgres; spans are in Parquet in MinIO.** Spans stage in Postgres first and flush every ~5-20 minutes, so a transaction can show a duration with an **empty span breakdown** for a while. That is a flush delay, not a failure. `SpanStaging.objects.count()` shows what is waiting.
- **`Corrupt parquet file skipped` / HTTP 404 in the log is cosmetic.** Recreating the container orphaned chunk references whose parquet was never written; GlitchTip skips them and continues.
- **A streaming span looks impossibly fast.** `formula-run` shows a ~5 ms `gen_ai.request` span against a 2 s transaction because SSE responses end the span at response headers. The transaction carries the true duration. Summaries/minutes/tags/embeddings are non-streaming and show real model time.
- **Formulas do not touch the Python worker.** They run entirely in the API. To exercise the worker, transcribe something - that is verification check 5.
- **Transactions live under Performance, not Issues.** An empty Issues list with plenty of transactions is normal.

---

## Decisions taken, so they are not reopened

- **Recording GUIDs in browser transaction names are accepted.** `beforeSendTransaction` does not scrub `event.transaction`, so names arrive as `/recordings/<guid>`. Reviewed and accepted: the GUIDs are anonymous. The consequence is cardinality - a distinct Performance entry per recording. If that view gets noisy, the fix is route parameterisation via the React Router tracing integration, not another scrubber rule.
- **`GLITCHTIP_BIND` stays `0.0.0.0`.** Deliberate: a fixed IP in `.env` is invisible to whoever runs the network and rots when the host is re-addressed, so the restriction belongs at the network layer. The runbook presents both options as a real choice.
- **Token counts are bucketed, not exact.** See above - exact counts would wreck the aggregate view they exist to inform. The structured `SetExtra` values are still sent, so a deployment pointed at real Sentry gets exact numbers for free.

---

## Outstanding

**The verification checklist** in `docs/GlitchTip_Deployment.md`. Notes that will save time:

- **Check 9 is the one that matters** - open a captured event and read the *whole* payload: no transcript text, no `access_token`, no voiceprint vector, no request body. Eight leak paths were found this way during the build; a ninth would be found the same way. Read the raw JSON, not the UI's sections:
  `https://errors.dev.app.example.com/api/0/issues/<id>/events/latest/` (works in a logged-in browser tab).
- **A real event is already waiting for it**: issue 2 in `diariz-web`, `Error: scrubber check`, thrown from the browser console, with genuine breadcrumbs and request context. It was deliberately left in place when the test artifacts were cleaned up.
- **Check 7 should now resolve to a readable frame** - source maps are proven.
- **Check 8 (trace propagation) cannot be verified from the database.** DuckDB means spans are in Parquet and no Postgres table carries a `trace_id`. It has to be a UI comparison of two spans' trace ids.
- Issues **soft-delete** (`Issue` extends `SoftDeleteModel`): the row stays with `is_deleted=True` and vanishes from the UI. A row still in `Issue.objects` is not a failed delete - check `Issue.undeleted_objects`.

**Then the prod recreate.** Two things must differ from dev: fresh secrets (`NewGlitchTipSecrets.cmd` runs once per server - a shared `SECRET_KEY` would let a signed value from dev replay against prod) and `SENTRY_ENVIRONMENT=production`.

**`GLITCHTIP_ALLOWED_HOSTS`** is available but unset, so the wildcard warning still logs on every boot. If you set it, it **must** include `glitchtip` alongside the public hostname - the API and worker post in-network as `Host: glitchtip`, and Django drops any host not listed, stopping server-side reporting silently while the browser keeps working.

**Rotate the MinIO scoped key.** It was printed in full into a session transcript (`docker compose config` output). Re-run `ProvisionGlitchTipMinio.cmd`, paste the new values into `.env`, recreate `glitchtip`. The GlitchTip Postgres password went the same way but is lower priority - that container publishes no host port.

---

## Environment notes for this box

| Thing | Detail |
| --- | --- |
| **Node** | Installed (v24.18.1), but **not on the tool shell's PATH** - prefix with `export PATH="$PATH:/c/Program Files/nodejs"`. `npm ci` has been run in `apps/web`, so vitest works |
| **.NET SDK is NOT installed** | Only the runtime. Build and test via Docker: `docker run --rm -v "C:\Users\kenha\repos\Diariz:/src" -v diariz-nuget:/root/.nuget/packages -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests/Diariz.Api.Tests/Diariz.Api.Tests.csproj` (the named volume keeps the NuGet cache warm) |
| **Git identity was unset** | Set repo-local to `Ada Lovelace <ada.lovelace@example.com>`, matching the bulk of history |
| Host LAN address | `192.168.1.49` |
| The `minio` container | Busybox: no `grep`, `sed` or `which`. Text processing on the host |
| The `glitchtip` container | Full Python image - it *does* have grep, and `./manage.py shell -c` is the fastest way to ask the database a direct question |
| Piping to `grep` | Everything after a `\|` runs on the **host**, so it fails in PowerShell. Filter inside the container instead |
| `docker run` in Git Bash | Mangles absolute paths (`/src` becomes `C:/Program Files/Git/src`). Run docker from PowerShell instead |
| `diffsettings` / `compose config` | Both print secrets in full. Never dump them unfiltered - two credentials leaked into a transcript that way |
| GlitchTip image tags | No `v` prefix from 6.0.4 onward. `6.2` is right, `v6.2` does not exist |
| `docker compose ps --services` | LF-only line endings even on Windows, so `findstr /x` never matches. Use `docker compose ps -q <svc>` |
| The six required env vars | Guarded with `${VAR:?}`, so `docker compose down` needs them present too |
