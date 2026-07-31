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
# Gated on the auth token + URL + org + project: a build without all four just
# skips this, so a developer build and a CI build both still succeed. The token is a BuildKit secret,
# never a build ARG - an ARG is recorded in the image history and readable by anyone who can pull the
# image. CLI verified via `npx @glitchtip/cli --help` (published as @glitchtip/cli, binary
# glitchtip-cli) - unlike Sentry's CLI, GlitchTip's requires an --org and --project slug in addition to
# --url/--auth-token/--release.
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
      if npx --yes @glitchtip/cli sourcemaps inject ./dist \
         && npx --yes @glitchtip/cli sourcemaps upload ./dist \
              --url "$GLITCHTIP_URL" \
              --auth-token "$(cat /run/secrets/glitchtip_token)" \
              --org "$GLITCHTIP_ORG" \
              --project "$GLITCHTIP_PROJECT" \
              --release "$RELEASE"; then \
        echo "Source maps uploaded for release $RELEASE."; \
      elif [ -n "$GLITCHTIP_SOURCEMAPS_OPTIONAL" ]; then \
        echo "Warning: source map upload failed, and GLITCHTIP_SOURCEMAPS_OPTIONAL is set - shipping without readable stack traces."; \
      else \
        echo "ERROR: source map upload failed, and GlitchTip credentials ARE configured."; \
        echo "       Refusing to ship an image whose stack traces cannot be read. The CLI's own error is above."; \
        echo "       Common causes: --org must be the lowercase SLUG from the URL, not the display name;"; \
        echo "       a 500 from chunk-upload usually means AWS_STORAGE_BUCKET_NAME is unset on the server."; \
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
