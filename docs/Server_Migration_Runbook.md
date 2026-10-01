# Server migration runbook - moving Diariz to a new server (and from MinIO to SeaweedFS)

How to move a running Diariz instance to a new server by **platform backup and restore**. It was written for
the 0.273.1 move, where the old server runs MinIO and the new one runs SeaweedFS, but it works for any move:
the backup is store-agnostic (objects go through `IAudioStorage` both ways, and
`BackupRestoreS3IntegrationTests` proves the round trip on SeaweedFS), so there is **no bucket-level copy**.

The old server keeps running, untouched, the whole time. A restore wipes and replaces everything on the new
server, so you can restore the same kind of backup as many times as you like: rehearse first, then cut over.

Commands are **PowerShell on Windows 11 + Docker Desktop (WSL2)**, run from the repo's `deploy\` folder. In
Windows PowerShell 5.1 `curl` is an alias for `Invoke-WebRequest`, so always type **`curl.exe`**. Everything here
uses tools Windows already has (`curl.exe`, `tar.exe`); S3 checks run in the `amazon/aws-cli` image, so nothing
else needs installing on either server.

> **Never redeploy the old server from `main` once SeaweedFS has merged**, and never run `BringUpProd.cmd` or
> `BringUpWebApi.cmd` on it: both `git pull` first. It would come up on an empty SeaweedFS while its database
> still points at blobs in MinIO, and every recording would lose its audio. The old server stays on its current
> build until it is retired.

---

## 0. Prerequisites

### Sizing

- **New server:** free space inside the **Docker Desktop disk image** of at least 2.5x the backup zip. Restore
  writes the whole zip to the API container's `/tmp`, fills the store, and spills each object to a temp file
  before uploading it. Add 1x again on the Windows drive that holds the copied zip.
- **Old server:** about 1x the zip free in its Docker disk image (backup builds the zip in `/tmp` first).
- The new server must run the **same or a newer** app version than the old one. Restore refuses a backup from
  a newer schema and rolls an older one forward.

### One-time new-server preparation

- **Docker Desktop**, WSL2 backend, with **Start Docker Desktop when you sign in** turned on. Docker Desktop
  only runs inside a signed-in session, so after every reboot (Windows Update included) the stack is down until
  someone signs in. Set up automatic sign-in, a power plan that never sleeps, and Windows Update active hours.
  Every long-running service is `restart: unless-stopped`, so once Docker Desktop is up the stack follows on its
  own. Check the toggle in the UI rather than trusting the registry: a `Docker Desktop` entry in the `Run` key
  can coexist with `AutoStart=False` in `%APPDATA%\Docker\settings-store.json`. See
  [Unattended restart](#unattended-restart) for the whole chain (power loss -> boot -> sign-in -> Docker -> LLM).
- **Disk image location** (Settings -> Resources -> Advanced) on a drive with at least 50 GB free. Every volume
  lives in that VHDX, which grows on its own but never shrinks.
- A current **NVIDIA Windows driver**. Docker Desktop provides the GPU path, so no container toolkit is needed.
  Check with `docker run --rm --gpus all nvidia/cuda:12.8.0-base-ubuntu22.04 nvidia-smi`.
- **Git for Windows**, and the repo cloned.
- **Firewall:** the outer reverse proxy runs on another host, so set `WEB_BIND=0.0.0.0` (the default) and, with
  GlitchTip, `GLITCHTIP_BIND=0.0.0.0`. Docker Desktop publishes ports through `com.docker.backend.exe`, and it
  installs its own inbound rule, **Docker Desktop Backend**, that allows **every TCP port from every address**.
  So 8081 and 8000 are open to the whole LAN from the first `up`, and an *allow* rule for the proxy restricts
  nothing - Windows admits traffic if any allow rule matches. Use a **block** rule, which outranks allow rules,
  covering every address except the proxy. In an elevated PowerShell:
  ```powershell
  $proxy = '<proxy-ip>'
  $b = [Net.IPAddress]::Parse($proxy).GetAddressBytes(); [Array]::Reverse($b); $n = [BitConverter]::ToUInt32($b, 0)
  function ToIp([uint32]$v) { $x = [BitConverter]::GetBytes($v); [Array]::Reverse($x); ([Net.IPAddress]::new($x)).ToString() }
  $ranges = @("0.0.0.0-$(ToIp ($n - 1))", "$(ToIp ($n + 1))-255.255.255.255", "::-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")
  New-NetFirewallRule -DisplayName 'Diariz: block 8081 and 8000 except the proxy' -Direction Inbound -Protocol TCP -LocalPort 8081,8000 -RemoteAddress $ranges -Action Block -Profile Any
  ```
  Leave Docker's own rule alone (Docker Desktop recreates it). Loopback is not filtered, so the host itself still
  reaches both ports. Check from another LAN machine with `Test-NetConnection <server> -Port 8081` (expect
  `False`) and from the proxy (expect `True`). The API, Postgres and S3 ports stay on `127.0.0.1`, which no
  firewall rule is needed for.
- **Local LLM** on the host: containers reach it at `http://host.docker.internal:<port>/v1`, not `localhost`.
  It must listen on `0.0.0.0` (LM Studio: Developer -> Server settings -> serve on local network).
- **GPU budget.** Since 0.273.2 the worker hands each job's cached GPU memory back when the job ends and keeps
  only its loaded models between jobs (issue #782). Before that it held its job peak - about **10.8 GB** on a
  3090 - for the life of the container. Its peak *during* a job is unchanged, and the optional `live-worker` is a
  second ~9 GB copy of the models. With an LLM on the same 24 GB card, leave the live worker off and say so in
  `.env` with an explicit, commented `COMPOSE_PROFILES=`.

### Unattended restart

The stack only comes back by itself if every link in this chain holds. Test it once by pulling the power.

1. **Power returns -> the machine boots.** UEFI setting, usually *Advanced -> APM Configuration -> Restore AC
   Power Loss = Power On* (ASUS; other boards call it *AC Back* or *After Power Loss*). A UPS that signals a clean
   shutdown is better still.
2. **Boot -> a user is signed in.** Docker Desktop cannot run without one. Use Sysinternals **Autologon**
   (`autologon.exe`), which stores the password as an LSA secret rather than in plain text in the registry. For a
   Microsoft account, first turn off *Settings -> Accounts -> Sign-in options -> For improved security, only allow
   Windows Hello sign-in*, then sign in to Autologon with the account's password, not its PIN. To keep the
   console locked after the automatic sign-in, add a logon task that runs `rundll32.exe user32.dll,LockWorkStation`;
   Docker keeps running in a locked session.
3. **Windows Update restarts** are covered by the same Autologon. Also turn on *Sign-in options -> Use my sign-in
   info to automatically finish setting up after an update*.
4. **Sign-in -> Docker Desktop -> containers.** *Start Docker Desktop when you sign in*, plus the services'
   `restart: unless-stopped`.
5. **Sign-in -> the LLM.** LM Studio in the `Run` key with `--run-as-service`, its server set to start on launch,
   and either the model auto-loaded or JIT loading on. Otherwise every summary and chat fails until someone loads
   it.

Never sleep on AC (`powercfg /change standby-timeout-ac 0`, and the same for `hibernate-timeout-ac`).

---

## 1. Build the new server

### 1.1 `deploy\.env`

Start from `.env.example`. Copy values server to server (SMB share or similar), never through chat or the repo.

- **Carry over from the old server:** `JWT_KEY`, `CALLBACK_SECRET`, `POSTGRES_PASSWORD`, `REDIS_PASSWORD`,
  `APP_PUBLIC_URL` (the **same** public origin, so desktop apps, OAuth redirect URIs, webhooks and the claude.ai
  MCP connector keep working), `HF_TOKEN`, `Seed__*`, Google/Microsoft OAuth, SMTP.
- **LLM and embeddings:** point `SUMMARY_API_BASE` and friends at the new host's LLM. **`EMBED_MODEL` and
  `EMBED_DIMENSION` must equal the old server's values, and the endpoint must serve the same model.** The backup
  carries every chunk's vector (`TranscriptChunks.Embedding`); a different embedding model does not error, it
  silently makes search and chat return poor matches. If the model must change, plan a re-embed.
  **These `.env` values only govern a fresh database.** Once a backup is restored, the endpoints come from the
  restored database - the admin's **LLM models** (`LlmModels`, each with its own `ApiBase` and optionally a stored
  key) and the platform default model - and they override `.env`. They still point at whatever the old server
  used. Repoint them in the admin UI **after the final restore** (2.4 and 3.4), never before: a restore
  overwrites them.
- **Generate new** (never reuse the old server's keys):
  ```powershell
  .\NewS3Keys.cmd root        # S3_ROOT_ACCESS_KEY / S3_ROOT_SECRET_KEY
  .\NewS3Keys.cmd             # S3_APP_ACCESS_KEY  / S3_APP_SECRET_KEY
  ```
- **With GlitchTip** (see `docs/GlitchTip_Deployment.md` for the full list): `.\NewS3Keys.cmd glitchtip`,
  `.\NewGlitchTipSecrets.cmd`, `GLITCHTIP_DOMAIN` (the real external URL), `GLITCHTIP_ALLOWED_HOSTS` (that host
  plus `glitchtip`), and **switch the overlay on** by uncommenting:
  ```
  COMPOSE_PATH_SEPARATOR=,
  COMPOSE_FILE=docker-compose.yml,docker-compose.observability.yml
  ```
  (An older `.env` may not have these lines at all, even commented - add them.) Leave **all three DSNs** -
  `SENTRY_DSN` (the workers), **`SENTRY_API_DSN`** (the API) and `SENTRY_BROWSER_DSN` (the SPA) - and
  `GLITCHTIP_URL/ORG/PROJECT/TOKEN` empty until 1.4. `SENTRY_API_DSN` is the easy one to miss: a `.env` copied
  from the old server still carries the old DSN, and the new API would report its errors to the **old** GlitchTip.
- **Starting from a copy of the old `.env`** instead of `.env.example`: delete `MINIO_ROOT_*`, `MINIO_APP_*` and
  `GLITCHTIP_MINIO_*`, add the new `S3_*`/`GLITCHTIP_S3_*` keys above, and rename `MINIO_BIND` to `S3_BIND` - or
  add `S3_BIND=127.0.0.1` if the old file never set it. Check the result with `docker compose config --quiet`
  (silent on success, so no values are printed). If you edit `.env` from a .NET script, note that it is often a
  **Hidden** file, and `File.WriteAllText` refuses to recreate a hidden file - open it with `FileMode.Truncate`.

### 1.2 Copy the keyring before the first start

The `apikeys` volume holds the Data Protection keyring (decrypts users' stored LLM keys, webhook secrets, Google
refresh tokens) and the OpenIddict signing certificates (every MCP/OAuth connector). It is **not** in the backup.
If the API ever starts on an empty `apikeys` volume it mints a new keyring and this step has to be redone.

On the **old** server, from its `deploy\`:
```powershell
docker run --rm -v diariz_apikeys:/keys -v "${PWD}:/out" alpine tar czf /out/apikeys.tgz -C /keys .
```
Copy `apikeys.tgz` to the new server (treat it as a secret). On the **new** server:
```powershell
docker compose up --no-start --build
docker run --rm -v diariz_apikeys:/keys -v "${PWD}:/in" alpine tar xzf /in/apikeys.tgz -C /keys
docker compose up -d
```
`up --no-start` lets Compose create the volume with its own labels. Wait until `docker compose ps` shows every
service healthy, including `s3` (and `s3-buckets` exited 0 with GlitchTip). On first boot the API creates the
empty `recordings` bucket itself with the scoped app key - no manual bucket step.

### 1.3 Smoke checks

- Upload a short recording: it transcribes (worker reads S3 through boto3), plays, and seeks (ranged GETs).
- Make a clip or screenshot (presigned GET through ffmpeg). Delete a recording and confirm its object is gone.
- Run a live session (merge jobs make the worker upload multipart). If you script it instead of using the
  browser, the chunks must be **byte slices of one WebM stream** - only chunk 0 carries the header, and the
  worker byte-joins the chunks before ffmpeg sees them. Cut at Cluster boundaries (EBML id `1F 43 B6 75`).
  Independently encoded files each have their own header; the join then decodes only partway, and live chunks
  fail with `'waveform' must be provided as a (channel, time) torch Tensor`. That is the test's fault, not the
  server's.
- **Watch `nvidia-smi` during a transcription with the LLM loaded.** The worker and the live worker each hold
  their own models next to the LLM; if 24 GB is not enough, fix it now, not after cutover. The peak is the
  worker's **voiceprint stage**, not the LLM. Measure the *idle* figure after a job too: from 0.273.2 it should
  fall back to the LLM plus the worker's model weights (#782); on an older build it stays at the job's peak until
  `docker compose restart worker`. On Windows an overfull card does not fail; it spills into shared system memory
  and slows down.
- **Scoped keys** - each must be **denied** outside its own bucket:
  ```powershell
  function s3as($ak, $sk) { docker run --rm --network diariz_default -e AWS_ACCESS_KEY_ID=$ak -e AWS_SECRET_ACCESS_KEY=$sk -e AWS_DEFAULT_REGION=us-east-1 amazon/aws-cli --endpoint-url http://s3:8333 @args }
  s3as <app-key> <app-secret> s3 ls s3://recordings      # works
  s3as <app-key> <app-secret> s3 ls s3://glitchtip       # AccessDenied
  s3as <gt-key>  <gt-secret>  s3 ls s3://recordings      # AccessDenied
  ```

### 1.4 GlitchTip

Follow `docs/GlitchTip_Deployment.md` passes 2 and 3. In short: create the organisation, a **team** (projects
need one), and **three** projects - `diariz-worker`, `diariz-api` and `diariz-web` - plus an auth token with only
`project:releases`. Then set:

| Variable | Value |
|---|---|
| `SENTRY_DSN` | the worker project's DSN, host rewritten to `http://<key>@glitchtip:8000/<id>` |
| `SENTRY_API_DSN` | the API project's DSN, host rewritten the same way |
| `SENTRY_BROWSER_DSN` | the web project's DSN on the **public** GlitchTip host |
| `GLITCHTIP_ORG` / `GLITCHTIP_PROJECT` | the **slugs** (from the address bar), not the display names |
| `GLITCHTIP_TOKEN` | the token |
| `GLITCHTIP_URL` | an address the **build container** can reach (see below) |

The web build **fails** if the source map upload fails while all four are set, so a green build proves
GlitchTip writes to SeaweedFS with its scoped key. Only the upload POST checks the org and project slugs: GET
endpoints answer 200 even for a wrong slug, and a `project:releases` token gets 403 on project reads, which is
correct. To deploy, run `docker compose build` then `docker compose up -d`. `.\BringUpProd.cmd` does the same
plus a `git pull` first, which on an unmerged branch silently changes the code under test.

**Before the outer proxy points here** (a rehearsal), GlitchTip is reachable only on `http://<server>:8000`, and
its login fails as "wrong password" because Django's CSRF check is derived from `GLITCHTIP_DOMAIN`. Temporarily
set `GLITCHTIP_DOMAIN=http://<server>:8000`, append `,<server>,localhost` to `GLITCHTIP_ALLOWED_HOSTS`, and set
`GLITCHTIP_URL=http://<server>:8000` - the doc confirms the build container reaches a private address. Note that
the GlitchTip API answers **400** to a `Host` it does not allow, so `127.0.0.1:8000` fails where `<server>:8000`
works. Keep a copy of the prod values and restore them at cutover (3.4).

Confirm reporting: break a worker job (upload a few KB of random bytes as `source=Microphone`, which skips the
format sniff) - the issue lands in `diariz-worker` and the recording fails cleanly in the app; the API's
transactions appear in `diariz-api`. Without a proxy you can count events in GlitchTip's own database:
`docker compose exec -T glitchtip-postgres psql -U glitchtip -d glitchtip`, tables `issue_events_issue` and
`projects_*hourlystatistic`.

> **Changing any S3 key later:** `docker compose up -d --force-recreate s3`. Compose does not notice a change to
> the identity list on its own, so without it SeaweedFS keeps serving the old keys.

---

## 2. Rehearsal restore

1. **Old server**, at a quiet moment: Admin -> Maintenance -> **Download backup**. Count its objects without
   unpacking it (`tar.exe` reads zips):
   ```powershell
   $zip = "D:\diariz-restore\diariz-backup-XXXX.zip"
   (tar -tf $zip | Select-String '^objects/.*[^/]$').Count
   ```
2. **Copy** it to the new server. `robocopy` resumes a broken copy:
   ```powershell
   robocopy \\<old-server>\<share> D:\diariz-restore diariz-backup-XXXX.zip /Z /J
   ```
3. **Restore on the new server, directly against the API on `127.0.0.1:8080`.** That bypasses nginx, the outer
   proxy and the browser, and their body-size and timeout limits:
   ```powershell
   $cred  = Get-Credential   # a platform admin: the seed admin the first time; a prod admin once prod data is loaded
   $body  = @{ email = $cred.UserName; password = $cred.GetNetworkCredential().Password } | ConvertTo-Json
   $token = (Invoke-RestMethod -Method Post -Uri http://127.0.0.1:8080/api/auth/login -ContentType 'application/json' -Body $body).accessToken
   curl.exe -sS -X POST -T $zip -H "Authorization: Bearer $token" -H "Content-Type: application/zip" http://127.0.0.1:8080/api/maintenance/restore
   ```
   Use `curl.exe -T`, which streams the file - not `--data-binary @file` and not `Invoke-WebRequest`, which both
   buffer it in memory. Expect `{"restored":true,...,"objectsRestored":N,"bytesRestored":B}` with **N equal to
   the step 1 count**.

   `pg_restore --clean` recreates the `vector` extension, so the type comes back with a new OID. **From 0.273.2
   the restore refreshes the API's type cache itself** (issue #783) and the instance is usable straight away;
   restart only if the response says `restartRecommended: true` (the backup needed a migration):
   ```powershell
   docker compose restart api worker web
   ```
   (add `live-worker` if you run it; `web` is in the list for the reason in [Troubleshooting](#troubleshooting)).
   **On a build before 0.273.2, always run that restart**, whatever the response says: otherwise every query
   that reads a vector column fails with `Reading as 'System.Object' is not supported for fields having
   DataTypeName '-.-'` - including **login**, which returns 500 for everyone.
4. **Verify by count:** recordings, users and people in the admin pages match the old server, and the bucket
   holds exactly the step 1 count (`s3 ls s3://recordings --recursive --summarize` with the app key, as in 1.3).
   Pick a few recordings across a range of ages: each plays, seeks, and shows its transcript, summary and
   speakers. Re-transcribe one. Upload a new one. Ask search and chat about an **old** recording (the
   embedding-model check). A user's own LLM key still works (the keyring check). Errors reach GlitchTip.

   **Mind where the LLM calls go.** After the restore they follow the restored LLM-model settings (1.1), which
   still point at the old server's LLM hosts - so re-transcribing, uploading, summarising, tagging, chat and even
   the *query* embedding for search all send prod content there. If the old server must not be contacted, repoint
   the models in the admin UI before these checks.

   **A sharper embedding-model check** than eyeballing search results: take a few `TranscriptChunks` rows from an
   old recording, embed `EMBED_DOCUMENT_PREFIX + Text` with the new endpoint, and compare with the stored
   `Embedding`. The same model gives a cosine similarity of about **0.9999**; a different model is near **0**.

   **These checks change prod data** (a new transcript version, a new recording, regenerated summaries). If the
   rehearsal data will become the live data (see 3), restore the same zip once more afterwards to undo them.
5. **Write down** how long the backup, the copy and the restore took. Their sum is the cutover window.

---

## 3. Cutover

> **If the old server has been down since the rehearsal backup was taken**, nothing has been written since, so
> skip steps 2, 3 and 7. Re-run the 2.3 restore with **the same zip** (it undoes the rehearsal's own changes),
> then carry on from step 4's restart. This is how the 0.273.1 move was done.

1. Announce the window (2.5). Ask users to stop recording and close desktop apps; desktop apps reconnect
   afterwards because the address does not change.
2. **Drain in-flight work on the old server.** Redis queues are not in the backup, so a job still running when
   the backup is taken arrives stuck. On the old server, in `deploy\`:
   ```powershell
   'select "Status", count(*) from "Recordings" group by 1 order by 1;' | docker compose exec -T postgres psql -U diariz -d diariz
   ```
   Wait until nothing is in **0, 1, 2, 6, 7 or 8** (Uploaded, Queued, Transcribing, Summarizing, Merging,
   Capturing). The SQL goes in on stdin because PowerShell 5.1 mangles embedded double quotes in native-command
   arguments.
3. **Old server:** take the **final backup** and note its time `T` (UTC). Copy it across as in 2.2.
4. **New server:** run the 2.3 restore again, signing in as a **prod** platform admin (the rehearsal loaded
   prod's users). It wipes the rehearsal data. `objectsRestored` must match this zip's 2.1 count. If 1.4 used
   the temporary LAN values, put the prod `GLITCHTIP_DOMAIN` and `GLITCHTIP_ALLOWED_HOSTS` back, set
   `GLITCHTIP_URL` to the public GlitchTip URL, and point `SENTRY_BROWSER_DSN` at the public host (same key and
   project id). Then apply `.env` and restart:
   ```powershell
   docker compose up -d
   docker compose restart web
   ```
   (`up -d` recreates `api` when its environment changed, and nginx must then re-resolve it. On a build before
   0.273.2 restart `api worker web` instead, as in 2.3.)
   From here the GlitchTip UI only accepts logins through the proxy. A web **build** before step 6 would push
   source maps to the old GlitchTip and fail; set `GLITCHTIP_SOURCEMAPS_OPTIONAL=1` if you must build sooner.
5. **Repoint the LLM models** in the admin UI if the LLM is moving (1.1), then repeat the 2.4 checks. From inside
   a container, `docker run --rm --network diariz_default curlimages/curl -sS <ApiBase>/models` proves the new
   endpoint is reachable.
6. **Switch traffic:** point the outer reverse proxy (or DNS) for the Diariz origin at the new server's **8081**,
   and the GlitchTip host at its **8000**. The proxy must forward `/mcp` with buffering off, upgrade WebSockets on
   `/hubs`, and allow long timeouts on `/api/maintenance/`. Then sign in on the public URL, check a desktop app
   reconnects and the claude.ai MCP connector still works (its signing keys came over in the keyring), and log in
   to GlitchTip through its public host.
7. **Stragglers.** On the **old** server, count anything written after `T`:
   ```powershell
   'select count(*) from "Recordings" where "CreatedAt" > ''<T>'';' | docker compose exec -T postgres psql -U diariz -d diariz
   ```
   If it is not zero, download those recordings' audio from the old UI and upload them to the new one.

---

## 4. Afterwards

- Leave the old server running until the new one has been in use for a while, then `docker compose down`
  **without `-v`**, and keep its `miniodata` and `pgdata` volumes until you are sure.
- GlitchTip error history does not move (its own Postgres and bucket are not in the Diariz backup); the new
  server starts fresh.
- Delete every copy of the backup zips and `apikeys.tgz` you no longer need. They contain everyone's
  recordings and the keys that decrypt users' stored secrets. Neither is gitignored, so keep them out of
  `deploy\` or out of any `git add`.
- Repoint or remove any LLM model still on the old server's address before you switch its LLM off.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| **502 on every `/api` call** (public URL *and* `localhost:8081`), while `http://127.0.0.1:8080/health` is fine | `up -d` recreated the `api` container, which came back on a new IP; nginx in `web` resolved `api` once at startup and still sends to the old one (`connect() failed ... upstream: "http://<old-ip>:8080/..."` in `docker compose logs web`) | `docker compose restart web` - and restart `web` whenever `api` is recreated. `BringUpWebApi.cmd` already orders this |
| **Login returns 500** after a restore; the API log shows `DataTypeName '-.-'` from `PeopleDirectory.EnsureForUserAsync` | Stale `vector` OID in Npgsql's type cache (2.3, #783) - builds before 0.273.2, which do not refresh it themselves | `docker compose restart api worker web` |
| GlitchTip login says **wrong password** | `GLITCHTIP_DOMAIN` does not match the URL you are using (CSRF) | See 1.4 - temporary LAN values before the proxy, public values after |
| GlitchTip API calls return **400** | The request's `Host` is not in `GLITCHTIP_ALLOWED_HOSTS` | Call it on an allowed host |
| Tag extraction fails with **Failed to process regex** (400 from LM Studio) | LM Studio rejecting the structured-output grammar for that request; not migration-related | Retried on every API start; try another model for tags |
