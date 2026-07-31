# Deploying GlitchTip

How to stand up self-hosted error tracking for a Diariz server, from nothing to verified.

Do this **once per environment**. Dev and prod get completely separate instances - separate databases, separate secrets, separate hostnames, separate retention. Nothing is shared, deliberately: a dev instance full of test noise must never be the place you look for a production failure, and a shared signing key would let a session from one be replayed against the other.

Everything here is optional. A Diariz server with no `SENTRY_*` values set reports nothing, loads no SDK, and opens no sockets.

## What you are building

| Piece | Where it runs |
| --- | --- |
| GlitchTip web + worker | One container, from `deploy/docker-compose.observability.yml` |
| Its database | Its own Postgres container, its own volume |
| Its cache/queue | The app's existing Redis, on **DB index 1** (the app's queues are on 0) |
| Its cold storage | The app's existing MinIO, in its **own bucket** with a **scoped key** |

Roughly 500 MB of RAM on top of the app.

**One honest limitation:** GlitchTip runs on the same box as the app it watches, so it cannot tell you the box is down. It reports application failures, not infrastructure death. Uptime monitoring is a different tool.

## Before you start

- The app stack is up (`docker compose up -d`), including `minio` and `redis`.
- You can add a DNS record and a proxy host for a new subdomain.
- You know the SMTP details GlitchTip will send invitations and password resets from.

---

## Choosing a hostname

```
errors.diariz.example.com          production
errors.dev.diariz.example.com      dev
```

The `errors.` prefix mirroring the app's hostname makes the pairing obvious, and separate hostnames keep the two instances independent as designed.

**Two things worth knowing:**

**Wildcard certificates only match one label.** `*.example.com` does **not** cover `errors.diariz.example.com`. If your proxy issues per-host Let's Encrypt certificates (Nginx Proxy Manager does, via HTTP-01), depth is irrelevant and you can ignore this. If you are installing a wildcard by hand, go flat instead: `errors-diariz.example.com`.

**Cookies are not a concern here.** No `CookieOptions` in the Diariz API sets a `Domain`, so every app cookie is host-only and will not be sent to a sibling subdomain.

---

## Topology: where the proxy is

This matters more than anything else in this document, because it decides three settings.

### If your reverse proxy is far away on the network

That is the assumption below: the proxy cannot join the app's Docker network, and cannot be moved.

**GlitchTip must listen on an interface the proxy can route to.** The overlay defaults to `GLITCHTIP_BIND=127.0.0.1`, which is correct only for a proxy running directly on the same host. Set it to the host's private-network address:

```bash
GLITCHTIP_BIND=10.0.5.20      # this host's address on the network the proxy reaches
GLITCHTIP_PORT=8000
```

Prefer a specific address over `0.0.0.0`: it limits the listener to one interface instead of every interface the host has.

> **Docker's published ports bypass the host firewall.** Docker writes its own DNAT rules, so a `ports:` entry is reachable regardless of what `ufw` or Windows Firewall say. The same warning is already on the `postgres` service in `docker-compose.yml`. If the network between the proxy and this host is not trusted, restrict access to the proxy's address at the network layer, or put the two on a private link.

**Then keep the server-side traffic off the proxy entirely.** The API and worker containers sit on the same Docker network as GlitchTip, so they can post events straight to it and never touch your distant proxy at all. That removes a whole class of failure: server-side error reporting keeps working when the proxy, its certificate, or the link is having a bad day.

Do this by rewriting the **host** portion of the DSNs GlitchTip gives you (details in pass 2). The key and project id stay exactly as issued:

| DSN | Host to use | Why |
| --- | --- | --- |
| `SENTRY_DSN` (worker) | `http://<key>@glitchtip:8000/<id>` | in-network, no proxy, no TLS |
| `Sentry__Dsn` (API) | `http://<key>@glitchtip:8000/<id>` | same |
| `SENTRY_BROWSER_DSN` | `https://<key>@errors.dev.diariz.example.com/<id>` | **must stay public** - this one runs in the user's browser |

### If your reverse proxy is on the same host

Leave `GLITCHTIP_BIND=127.0.0.1` and point the proxy at `127.0.0.1:8000`. Nothing is exposed beyond the host. You can still use the in-network DSNs above.

### If your proxy is a container that could join the network

Add it to the `diariz` network and forward to `glitchtip:8000` by service name. You can then delete the `ports:` block from the overlay entirely and publish nothing at all.

---

## Pass 1: before first boot

The DSNs do not exist until GlitchTip is running and you have created the projects, so this cannot be done in one sitting. Pass 1 is everything needed to get it started.

### Every variable, and where it comes from

This is the complete set. **The six marked required have no fallback** - leave one empty and the container starts anyway and then fails, in a way that rarely points at the missing value.

| Variable | Required | Comes from | If it is empty |
| --- | --- | --- | --- |
| `GLITCHTIP_SECRET_KEY` | **yes** | 1a, the secrets script | Container starts, then every request dies with `ImproperlyConfigured: The SECRET_KEY setting must not be empty` |
| `GLITCHTIP_DOMAIN` | **yes** | 1c, by hand | Login fails with a CSRF error that reads as a wrong password |
| `GLITCHTIP_EMAIL_URL` | **yes** | 1a, the secrets script | No invitations, no password resets |
| `GLITCHTIP_FROM_EMAIL` | **yes** | 1a, the secrets script | As above |
| `GLITCHTIP_MINIO_ACCESS_KEY` | **yes** | 1b, the MinIO script | Cold storage fails; spans are lost |
| `GLITCHTIP_MINIO_SECRET_KEY` | **yes** | 1b, the MinIO script | As above |
| `GLITCHTIP_POSTGRES_PASSWORD` | no | 1a, the secrets script | Falls back to `glitchtip` - fine locally, set it anywhere else |
| `GLITCHTIP_PORT` | no | 1c | Defaults to `8000` |
| `GLITCHTIP_BIND` | no | 1c | Defaults to `127.0.0.1` - see Topology, this is wrong for a remote proxy |
| `GLITCHTIP_COLD_STORAGE_BUCKET` | no | 1c | Defaults to `glitchtip` |
| `SENTRY_DSN` | pass 2 | GlitchTip UI | The worker reports nothing |
| `SENTRY_API_DSN` | pass 2 | GlitchTip UI | The API reports nothing |
| `SENTRY_BROWSER_DSN` | pass 2 | GlitchTip UI | The SPA reports nothing |
| `SENTRY_ENVIRONMENT` | pass 2 | 1c | Defaults to `development` |
| `SENTRY_TRACES_SAMPLE_RATE` | pass 2 | 1c | Defaults to `1.0` |
| `GLITCHTIP_URL` / `_ORG` / `_PROJECT` / `_TOKEN` | pass 3 | GlitchTip UI | Source maps are not uploaded; the build says so and continues |

The compose file rejects a missing required value up front with a message naming it, so a mistake here surfaces at `docker compose up` rather than in a Python traceback. Step 1e checks it explicitly before you start anything.

### 1a. Generate the secrets

```bash
cd deploy
NewGlitchTipSecrets.cmd
```

Linux/macOS: `./new-glitchtip-secrets.sh`

Nothing is written to disk. **Copy all four of these into `deploy/.env`** and close the window:

```bash
GLITCHTIP_SECRET_KEY=<generated>
GLITCHTIP_POSTGRES_PASSWORD=<generated>
GLITCHTIP_EMAIL_URL=<assembled from your SMTP answers>
GLITCHTIP_FROM_EMAIL=<your answer>
```

Three of those four are required, and `GLITCHTIP_SECRET_KEY` is the one whose absence is hardest to diagnose from the symptom. If you skip the SMTP prompts, come back and set `GLITCHTIP_EMAIL_URL` and `GLITCHTIP_FROM_EMAIL` before starting - GlitchTip needs them to send you a password reset, which is awkward to discover once you are locked out.

Pass `/noemail` (or `--noemail`) if you want the random secrets only.

**Why the secrets are hex.** A `.env` value gets read by docker compose, by a POSIX shell, and sometimes by a Windows shell. In `.env` a `$` is interpolated by compose and a `#` starts a comment; in a shell, quotes, spaces, backticks and `!` all need escaping. base64 avoids `$` and `#` but still produces `+`, `/` and `=`, which bite the moment someone pastes a value into a shell command. Hex is `[0-9a-f]` only, so it never needs escaping anywhere. 32 bytes is 256 bits.

**Why the `EMAIL_URL` scheme matters.** It is `smtp+tls://user:password@host:port`, and the scheme is where the encryption mode lives - GlitchTip parses this with django-environ, which turns on STARTTLS only for `smtp+tls` (or `smtps`) and implicit TLS only for `smtp+ssl`. The script picks for you: `smtp+ssl` for port 465, `smtp+tls` for everything else.

A plain `smtp://` connects in clear text, and servers only advertise the AUTH extension once the connection is secured - so authentication fails with `SMTPNotSupportedError: SMTP AUTH extension not supported by server`, which reads like a limitation of the server rather than a missing scheme. If you generated your `.env` with a script from before 0.174.10, check this line: it emitted `smtp://` unconditionally and no such URL can authenticate anywhere.

**Why `EMAIL_URL` needs encoding.** It is a URL, so the username and password are URL *components*, not free text. An `@` in the password ends the userinfo early, a `:` splits user from password, and a `/`, `?` or `#` terminates it. Any of those produces an authentication failure that looks like a wrong password rather than a parse error. The script percent-encodes everything outside the RFC 3986 unreserved set.

> If your username or password needed encoding, the script says so. **Verify it**: after first start, trigger a password reset and confirm the mail arrives. If it does not, the simplest fix is an SMTP app-password made only of letters and digits, which needs no encoding at all.

### 1b. Provision MinIO

```bash
cd deploy
ProvisionGlitchTipMinio.cmd
```

Linux/macOS: `./provision-glitchtip-minio.sh`

This creates the `glitchtip` bucket and an access key scoped to it, then **proves the key cannot read the `recordings` bucket** before it exits. If that check fails, it refuses and tells you not to use the key.

**Copy both lines it prints into `deploy/.env`** - both are required:

```bash
GLITCHTIP_MINIO_ACCESS_KEY=glitchtip-svc
GLITCHTIP_MINIO_SECRET_KEY=<generated>
```

**Why not just use the MinIO root credentials?** They would give an error-tracking service read/write access to every user's recorded audio. GlitchTip needs somewhere to write Parquet files, nothing more.

**Why its own bucket and never a prefix inside `recordings`?** Platform restore *wipes* the recordings bucket before repopulating it. Cold storage under a prefix there would be silently destroyed by any restore. A separate bucket is invisible to both backup and restore, because `AudioStorage` scopes every S3 call to the single configured `Storage:Bucket`.

The script runs `mc` **inside the MinIO container**, so the host needs nothing installed, it does not depend on MinIO's port being published, and the root credentials never appear in a host command line or shell history. It is safe to re-run: an existing key is left untouched unless you pass `/rotate`.

### 1c. The four you set by hand

These are the only ones not produced by a script. They are **in addition to** the six from 1a and 1b - do not treat this block as the whole file.

```bash
GLITCHTIP_DOMAIN=https://errors.dev.diariz.example.com   # required
GLITCHTIP_PORT=8000
GLITCHTIP_BIND=10.0.5.20          # see Topology above
GLITCHTIP_COLD_STORAGE_BUCKET=glitchtip
```

> **`GLITCHTIP_DOMAIN` must include the scheme.** GlitchTip is Django, and derives its trusted CSRF origins from this value. Get it wrong and every login attempt fails with a CSRF error that **presents as a wrong password**. This is the single most common self-hosting mistake and it will cost you an hour if you do not know about it.

### 1d. Set up the proxy

**Nginx Proxy Manager** - Proxy Hosts, Add Proxy Host:

| Tab | Field | Value |
| --- | --- | --- |
| Details | Domain Names | `errors.dev.diariz.example.com` |
| Details | Scheme | `http` |
| Details | Forward Hostname / IP | the GlitchTip host's address, e.g. `10.0.5.20` |
| Details | Forward Port | `8000` |
| Details | Cache Assets | off |
| Details | Block Common Exploits | **off** |
| Details | Websockets Support | **on** |
| SSL | Certificate | request a new Let's Encrypt cert |
| SSL | Force SSL | **on** |
| SSL | HTTP/2 Support | on |

**Turn Block Common Exploits off.** It pattern-matches query strings and request bodies, and error-tracking envelopes are opaque compressed payloads. It is a plausible source of mystery 403s on ingest that would look like the SDK silently failing.

**Force SSL on.** A plain-http hit would make Django's CSRF origin check disagree with `GLITCHTIP_DOMAIN`.

**Advanced tab** - NPM's default body limit is far below what a source-map upload needs:

```nginx
client_max_body_size 100m;
proxy_read_timeout 300s;
proxy_send_timeout 300s;
```

NPM's generated config already sets `Host` and `X-Forwarded-Proto`, so you should not need to add them. If login fails with a CSRF error anyway, that is the first thing to check.

**Raw nginx instead?** Use the server block in `docs/superpowers/plans/2026-07-31-observability-phase1-worker-api.md`, Task 1 Step 4. Note the `map $http_upgrade $connection_upgrade` directive it includes must sit at `http` level, outside `server`, or nginx will not start.

### 1e. Check `.env` is complete, then start

**Do this first.** It takes a second and it is the difference between a clear message and reading a Python traceback.

Start with the whole picture. This prints each variable's length and nothing else, so it is safe to paste anywhere:

```bash
cd deploy
awk -F= '/^GLITCHTIP_/{printf "%s length=%d\n", $1, length($2)}' .env
```

Any of the six required variables showing `length=0` - or missing from the list entirely - is a problem. A blank value is just as broken as an absent one, and is the easy mistake if you copied `.env.example` and filled it in as you went.

Then let compose confirm:

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml config >/dev/null
```

Silence means you are good. A missing or blank **required** value stops the command outright, naming the variable and the script that produces it. Note it reports only the **first** problem it hits, which is why the length check above is worth running first - otherwise you fix one, re-run, and meet the next.

Then start it:

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml up -d glitchtip-postgres glitchtip
```

Watch the first start. It runs migrations, builds the event partitions, and creates the cache table - a minute or so of output ending in `Listening at: http://0.0.0.0:8000`:

```bash
docker compose logs -f glitchtip
```

Confirm the database is actually populated before going further. This is worth the ten seconds:

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml exec glitchtip ./manage.py showmigrations | grep -c "\[X\]"
```

A number in the hundreds is right. **Zero means migrations did not run** - see the first Troubleshooting row, because the symptom you would otherwise meet is a login page with no way to sign up, which looks like a permissions problem and is not.

### 1f. Create the first account and organisation

Both of the lockdown flags in the overlay have a **first-run escape hatch**, so a brand new instance lets you in even though it is configured to let nobody in:

- registration is open while `User` count is zero,
- organisation creation is open while `Organization` count is zero (and superusers are exempt from that one permanently).

The backend and the frontend apply the same rule, so the register link and the create-organisation form both appear on a fresh instance and both disappear once you have used them. **You do not need `createsuperuser`, and you do not need to temporarily flip either flag to `true`** - if either door looks shut on an empty instance, the cause is an unmigrated database, not the flags.

So, in the UI:

1. Visit the site and follow **Register** from the login page. GlitchTip identifies users by email address.
2. It then takes you straight to **Create a New Organization**, captioned "This is the first step to get started with GlitchTip". Name it.
3. Both doors are now shut behind you. Everyone after this arrives by invitation from the UI, which is what the SMTP settings are for.

Then create **three** projects:

| Project | Platform | Feeds |
| --- | --- | --- |
| `diariz-worker` | Python | the transcription worker |
| `diariz-api` | .NET | the API |
| `diariz-web` | React | the SPA, and the desktop app |

**Three, not one.** Browser errors are far noisier than server errors - extensions, stale tabs, flaky mobile networks - and would bury the server-side failures you actually need to see.

---

## Pass 2: the DSNs

Copy each project's DSN from its settings page.

```bash
SENTRY_DSN=<diariz-worker DSN>
SENTRY_API_DSN=<diariz-api DSN>
SENTRY_BROWSER_DSN=<diariz-web DSN>
SENTRY_ENVIRONMENT=development        # "production" on the prod box
SENTRY_TRACES_SAMPLE_RATE=1.0
```

If your proxy is distant, rewrite the host on the two server-side DSNs as described in Topology - `http://<key>@glitchtip:8000/<id>`. Leave `SENTRY_BROWSER_DSN` public.

**Leave the sample rate at 1.0.** GlitchTip's documentation suggests 0.01 because every HTTP request becomes a transaction, but that advice targets high-traffic sites. Your volume will not approach the ~30 GB per *million* events.

Restart the app services to pick them up:

```bash
docker compose up -d --force-recreate api worker web
```

---

## Pass 3: source maps

Only once the source-map release has been deployed. Without these four values the web image still builds - it just prints a skip message, and production stack traces stay minified.

```bash
GLITCHTIP_URL=https://errors.dev.diariz.example.com
GLITCHTIP_ORG=<org slug>
GLITCHTIP_PROJECT=<the diariz-web project slug>
GLITCHTIP_TOKEN=<auth token from GlitchTip: profile -> auth tokens>
```

Three things to get right:

**`GLITCHTIP_PROJECT` must be the web project.** Maps uploaded to the worker or API project will never be applied to a browser stack trace.

**`GLITCHTIP_URL` must be reachable from inside the build container.** That container has neither your host's loopback nor the compose network. The public domain always works. If your proxy is distant and you would rather not push ~90 files out and back, the host's private address (`http://10.0.5.20:8000`) works too and skips the proxy's body-size limit entirely.

**The token is passed as a BuildKit secret, never a build ARG.** A build ARG is recorded in the image history and readable by anyone who can pull the image. The compose file already wires this correctly - you only need to set `GLITCHTIP_TOKEN` in `.env`.

Then rebuild the web image:

```bash
docker compose up --build -d web
```

---

## Verification

Work down this list. Each one has failed for somebody.

| # | Check | What it proves |
| --- | --- | --- |
| 1 | The UI loads over HTTPS on its own hostname | DNS, certificate, proxy routing |
| 2 | **You can log in** | `GLITCHTIP_DOMAIN` scheme and `X-Forwarded-Proto` - fails as "wrong password" |
| 3 | A password reset email arrives | SMTP, and your `EMAIL_URL` encoding |
| 4 | Re-run the MinIO script; it still reports the key cannot read `recordings` | The scoped key is still scoped |
| 5 | Transcribe something: a `transcribe` transaction appears with stage spans | Worker reporting and timing |
| 6 | Break the worker deliberately; a traceback appears **and** the recording still fails cleanly in the app | Reporting did not change behaviour |
| 7 | A browser error appears, attributed to release `0.174.x` | SPA reporting and `/api/config` |
| 8 | A browser XHR and its API request appear in **one** trace | Trace headers survive the proxy |
| 9 | **Open a captured event and read the whole payload** | See below |

**Check 9 is the one that matters.** No transcript text, no summary, no `access_token`, no voiceprint vector, no request body. Eight separate leak paths were found by inspection while this feature was built; a ninth would be found the same way.

**On check 8:** if the two halves appear as separate, unlinked traces, your proxy is stripping the `sentry-trace` and `baggage` request headers on the *app's* hostname. This fails silently - there is no error, the traces just never join.

---

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| **The login page renders but there is no register link**, and you cannot get in at all | The database is unmigrated. The page is a static SPA so it renders regardless, but the settings call behind it 500s on `relation "socialaccount_socialapp" does not exist` and the link never appears. Cause: `GLITCHTIP_EMBED_WORKER: "true"` is missing from the overlay - the image only self-migrates when the Heroku `DYNO` variable is set, which under Compose it never is. Confirm with the `showmigrations` count in 1e; do **not** chase the registration flags |
| `ImproperlyConfigured: The SECRET_KEY setting must not be empty` | `GLITCHTIP_SECRET_KEY` is unset or blank in `deploy/.env`. Run the 1e check; the fix is 1a |
| `manifest unknown` pulling the image | An image tag that does not exist. GlitchTip dropped the `v` prefix after 6.0.3, so it is `6.2`, not `v6.2` |
| Login says the password is wrong, and you are sure it is not | `GLITCHTIP_DOMAIN` is missing `https://`, or the proxy is not sending `X-Forwarded-Proto` |
| **Registering the first account returns a 500**, and afterwards the page says registration is unavailable | The account was created and the *verification email* failed, so the user now exists and the first-run registration window has closed behind it. You do not need to register again - `ACCOUNT_EMAIL_VERIFICATION` is `optional`, so sign in with the credentials you just chose and carry on to the organisation step. Then fix the mail: see the row below |
| `SMTPNotSupportedError: SMTP AUTH extension not supported by server` | The `EMAIL_URL` scheme is plain `smtp://`, so the connection is never secured and the server will not offer AUTH. Use `smtp+tls://` (or `smtp+ssl://` on port 465) - see pass 1a |
| No emails at all | `EMAIL_URL` encoding - see pass 1a, and try an alphanumeric app-password |
| The UI loads but events never arrive | Ingest blocked. Turn off Block Common Exploits; check the browser console for blocked requests |
| Browser events missing but server events fine | An ad blocker. They pattern-match error-tracking ingest paths. Expect to lose some proportion silently |
| Traces split into two halves | Proxy stripping `sentry-trace` / `baggage` |
| Source maps upload but stack traces stay minified | Wrong project in `GLITCHTIP_PROJECT`, or a release mismatch |
| The build cannot reach GlitchTip | `GLITCHTIP_URL` is a loopback address; the build container has its own |
| `Access Denied` writing cold storage | The MinIO key or bucket name does not match `.env`; re-run the provisioning script |

## Removing it

```bash
cd deploy
docker compose -f docker-compose.yml -f docker-compose.observability.yml down glitchtip glitchtip-postgres
docker volume rm diariz_glitchtipdata
```

Clear the `SENTRY_*` values from `.env` and recreate the app services. To clean up MinIO as well:

```bash
docker compose exec minio sh -c 'mc alias set r http://localhost:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" && mc rb --force r/glitchtip && mc admin user remove r glitchtip-svc && mc admin policy remove r glitchtip-only'
```

Nothing in the app depends on any of it.
