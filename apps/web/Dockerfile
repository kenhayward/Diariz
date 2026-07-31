# Build the static SPA. Node 22: @scalar/api-reference-react (the in-app API reference) requires Node >=22.
FROM node:22-alpine AS build
WORKDIR /app
# Optional build-time stamp shown in the About box (best-effort; blank if not provided).
ARG BUILD_COMMIT=""
ENV BUILD_COMMIT=$BUILD_COMMIT
# App version baked into __APP_VERSION__ (see vite.config.ts's appVersion()) - the same value
# telemetry.ts's initTelemetry() sends as the Sentry `release`. Blank by default: appVersion() then
# falls back to this package's package.json version (the repo-root version.json it prefers is outside
# this build context). The source map upload below re-derives the same fallback so the tagged release
# always matches what the SPA reports, whether or not this ARG is passed.
ARG APP_VERSION=""
ENV APP_VERSION=$APP_VERSION
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

# Inject debug IDs, then upload source maps to GlitchTip so production stack traces stay readable; the
# runtime stage below deletes the maps regardless.
#
# The `inject` step is load-bearing, not decorative. vite is configured with `sourcemap: "hidden"`,
# which strips the `//# sourceMappingURL=` comment from every bundle - so an uploaded map has NOTHING
# tying it back to the minified frame it explains, and GlitchTip silently leaves the trace minified.
# `sourcemaps inject` writes a debug ID into each bundle AND its map, which is the link the upload then
# registers; GlitchTip's documented flow is inject-then-upload for exactly this reason. It rewrites
# files in ./dist in place, so it must run before the upload and before the runtime stage copies dist.
# It contacts no server (no --url/--auth-token needed), but it is kept inside the credential gate all
# the same: with no upload to pair them with, injected debug IDs are dead weight in the shipped bundles.
#
# WHY sentry-cli AND NOT @glitchtip/cli. GlitchTip speaks the Sentry protocol, and its own CLI is
# broken for this: @glitchtip/cli 1.0.0 (the only version ever published) uploads each file
# individually and then calls `releases/{version}/assemble/` ONCE PER FILE. That endpoint expects a
# single ZIP containing every artifact plus a manifest.json, so the server tries to unzip a raw .js
# and dies with `BadZipFile: File is not a zip file` - once per file, forever. There is no flag that
# avoids it: `--release` is mandatory, and passing it is what selects the broken path.
#
# Measured on a real 6.2.3 server rather than reasoned about. @glitchtip/cli: 185 blobs uploaded,
# 0 files and 0 bundles created, one BadZipFile per artifact. sentry-cli, same server, same
# credentials: "Upload type: artifact bundle", and the bundle assembled with its debug ID linked.
#
# Pinned rather than floating. `npx --yes @glitchtip/cli` (no version) resolved to whatever npm
# served that day, against a server pinned to 6.2 - a moving client against a fixed server, which is
# how a working build starts failing with nothing in the diff to explain it.
#
# Gated on the auth token + URL + org + project: a build without all four just
# skips this, so a developer build and a CI build both still succeed. The token is a BuildKit secret,
# never a build ARG - an ARG is recorded in the image history and readable by anyone who can pull the
# image. It is passed via SENTRY_AUTH_TOKEN rather than `--auth-token` so it never appears in a
# process command line (visible to any `ps` in the same build step).
#
# ONE THING THIS CANNOT CATCH. Assembly is ASYNCHRONOUS: the server accepts the upload, returns
# 200 "pending", and assembles in a background task. A failure there happens after the CLI has
# exited 0, so a green build genuinely means "uploaded", never "assembled". If traces are still
# minified with a passing build, check the SERVER: `File`/`DebugSymbolBundle` rows, and the
# glitchtip container log for `assemble_artifacts`.
#
# NO CREDENTIALS -> skip quietly. A developer build and a CI build must both still succeed.
#
# CREDENTIALS SET BUT THE UPLOAD FAILS -> FAIL THE BUILD. This used to warn and continue, on the
# reasoning that a GlitchTip outage should not block shipping the web app. In practice that traded a
# loud failure now for a silent one later: the build "succeeds", ships traces nobody can read, and the
# operator finds out at the exact moment they need a production stack trace. A misconfigured org slug
# went unnoticed this way for a whole deployment. If you genuinely want to ship without maps, say so
# with GLITCHTIP_SOURCEMAPS_OPTIONAL - a deliberate choice beats a warning nobody reads.
#
# Injection failure is treated the same as upload failure, because maps uploaded without debug IDs do
# not resolve - "uploaded" would be a false reassurance.
#
# The presence check below is `-s` (non-empty), not `-f` (exists): a bare `docker build --secret`
# leaves the file absent when omitted, but deploy/docker-compose.yml's `environment:`-sourced secret
# always mounts the file and leaves it EMPTY when GLITCHTIP_TOKEN is unset in .env - `-f` alone would
# wrongly treat that empty file as "token provided" under `docker compose up --build`.
ARG GLITCHTIP_URL=""
ARG GLITCHTIP_ORG=""
ARG GLITCHTIP_PROJECT=""
ARG GLITCHTIP_SOURCEMAPS_OPTIONAL=""
RUN --mount=type=secret,id=glitchtip_token \
    if [ -s /run/secrets/glitchtip_token ] && [ -n "$GLITCHTIP_URL" ] && [ -n "$GLITCHTIP_ORG" ] && [ -n "$GLITCHTIP_PROJECT" ]; then \
      RELEASE="${APP_VERSION:-$(node -p "require('./package.json').version")}"; \
      export SENTRY_URL="$GLITCHTIP_URL"; \
      export SENTRY_ORG="$GLITCHTIP_ORG"; \
      export SENTRY_PROJECT="$GLITCHTIP_PROJECT"; \
      export SENTRY_AUTH_TOKEN="$(cat /run/secrets/glitchtip_token)"; \
      if npx --yes @sentry/cli@3.6.2 sourcemaps inject ./dist \
         && npx --yes @sentry/cli@3.6.2 sourcemaps upload ./dist --release "$RELEASE"; then \
        echo "Source maps uploaded for release $RELEASE."; \
      elif [ -n "$GLITCHTIP_SOURCEMAPS_OPTIONAL" ]; then \
        echo "Warning: source map upload failed, and GLITCHTIP_SOURCEMAPS_OPTIONAL is set - shipping without readable stack traces."; \
      else \
        echo "ERROR: source map upload failed, and GlitchTip credentials ARE configured."; \
        echo "       Refusing to ship an image whose stack traces cannot be read. The CLI's own error is above."; \
        echo "       Common causes: SENTRY_ORG must be the lowercase SLUG from the URL, not the display name;"; \
        echo "       a 500 from chunk-upload usually means AWS_STORAGE_BUCKET_NAME is unset on the server."; \
        echo "       NOTE: this check only proves the UPLOAD failed. Assembly happens server-side and"; \
        echo "       asynchronously, so a PASSING build can still leave traces minified - check the"; \
        echo "       glitchtip container log for assemble_artifacts if so."; \
        echo "       To ship anyway, set GLITCHTIP_SOURCEMAPS_OPTIONAL=1."; \
        exit 1; \
      fi; \
    else \
      echo "No GlitchTip credentials; skipping source map upload."; \
    fi

# Serve the build with nginx, proxying /api and /hubs to the API (same-origin → no CORS).
FROM nginx:alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
# Source maps are uploaded to GlitchTip at build time (see the sourcemaps upload step above), never
# served: without this they stay fetchable by guessing the filename even though nothing links to them.
RUN find /usr/share/nginx/html -name '*.map' -delete
EXPOSE 80
