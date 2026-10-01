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
- **Disk image location** (Settings -> Resources -> Advanced) on a drive with at least 50 GB free. Every volume
  lives in that VHDX, which grows on its own but never shrinks.
- A current **NVIDIA Windows driver**. Docker Desktop provides the GPU path, so no container toolkit is needed.
  Check with `docker run --rm --gpus all nvidia/cuda:12.8.0-base-ubuntu22.04 nvidia-smi`.
- **Git for Windows**, and the repo cloned.
- **Firewall:** the outer reverse proxy runs on another host, so set `WEB_BIND=0.0.0.0` (the default) and, with
  GlitchTip, `GLITCHTIP_BIND=0.0.0.0`. Docker Desktop publishes ports through a Windows process, so Windows
  Firewall controls LAN access. Add inbound rules for TCP **8081** and **8000** (GlitchTip) that allow **only the
  outer proxy's address**. Leave the API, Postgres and S3 ports on `127.0.0.1`.
- **Local LLM** on the host: containers reach it at `http://host.docker.internal:<port>/v1`, not `localhost`.

---

## 1. Build the new server

### 1.1 `deploy\.env`

Start from `.env.example`. Copy values server to server (SMB share or similar), never through chat or the repo.

- **Carry over from the old server:** `JWT_KEY`, `CALLBACK_SECRET`, `POSTGRES_PASSWORD`, `REDIS_PASSWORD`,
  `APP_PUBLIC_URL` (the **same** public origin, so desktop apps, OAuth redirect URIs, webhooks and the claude.ai
  MCP connector keep working), `HF_TOKEN`, `Seed__*`, Google/Microsoft OAuth, SMTP.
- **LLM and embeddings:** point `SUMMARY_API_BASE` and friends at the new host's LLM. **`EMBED_MODEL` and
  `EMBED_DIMENSION` must equal the old server's values, and the endpoint must serve the same model.** The backup
  carries every segment's vector; a different embedding model does not error, it silently makes search and chat
  return poor matches. If the model must change, plan a re-embed.
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
  Leave `SENTRY_DSN`, `SENTRY_BROWSER_DSN` and `GLITCHTIP_URL/ORG/PROJECT/TOKEN` empty until 1.4.

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
- Run a live session (merge jobs make the worker upload multipart).
- **Watch `nvidia-smi` during a transcription with the LLM loaded.** The worker and the live worker each hold
  their own models next to the LLM; if 24 GB is not enough, fix it now, not after cutover.
- **Scoped keys** - each must be **denied** outside its own bucket:
  ```powershell
  function s3as($ak, $sk) { docker run --rm --network diariz_default -e AWS_ACCESS_KEY_ID=$ak -e AWS_SECRET_ACCESS_KEY=$sk -e AWS_DEFAULT_REGION=us-east-1 amazon/aws-cli --endpoint-url http://s3:8333 @args }
  s3as <app-key> <app-secret> s3 ls s3://recordings      # works
  s3as <app-key> <app-secret> s3 ls s3://glitchtip       # AccessDenied
  s3as <gt-key>  <gt-secret>  s3 ls s3://recordings      # AccessDenied
  ```

### 1.4 GlitchTip

Create the organisation and the server and browser projects in GlitchTip, set `SENTRY_DSN`,
`SENTRY_BROWSER_DSN`, `GLITCHTIP_URL`, `GLITCHTIP_ORG`, `GLITCHTIP_PROJECT` and `GLITCHTIP_TOKEN`, then
`.\BringUpProd.cmd`. The web build **fails** if the source map upload fails while all four are set, so a green
build proves GlitchTip writes to SeaweedFS with its scoped key. Cause an API error and confirm it appears.

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
   the step 1 count**. If `restartRecommended` is true: `docker compose restart api worker` (add
   `--profile live-worker ... live-worker` if you run the live worker).
4. **Verify by count:** recordings, users and people in the admin pages match the old server. Pick a few
   recordings across a range of ages: each plays, seeks, and shows its transcript, summary and speakers.
   Re-transcribe one. Upload a new one. Ask search and chat about an **old** recording (the embedding-model
   check). A user's own LLM key still works (the keyring check). Errors reach GlitchTip.
5. **Write down** how long the backup, the copy and the restore took. Their sum is the cutover window.

---

## 3. Cutover

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
   prod's users). It wipes the rehearsal data. `objectsRestored` must match this zip's 2.1 count.
5. Repeat the 2.4 checks.
6. **Switch traffic:** point the outer reverse proxy (or DNS) for the Diariz origin at the new server's **8081**,
   and the GlitchTip host at its **8000**. The proxy must forward `/mcp` with buffering off and allow long
   timeouts on `/api/maintenance/`.
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
  recordings and the keys that decrypt users' stored secrets.
