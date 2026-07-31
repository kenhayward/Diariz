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
# --url/--auth-token/--release. A failed upload (unreachable server, bad credentials) warns rather than
# failing the build - the web image must still ship even if GlitchTip is unavailable at build time.
# The presence check below is `-s` (non-empty), not `-f` (exists): a bare `docker build --secret`
# leaves the file absent when omitted, but deploy/docker-compose.yml's `environment:`-sourced secret
# always mounts the file and leaves it EMPTY when GLITCHTIP_TOKEN is unset in .env - `-f` alone would
# wrongly treat that empty file as "token provided" under `docker compose up --build`.
ARG GLITCHTIP_URL=""
ARG GLITCHTIP_ORG=""
ARG GLITCHTIP_PROJECT=""
RUN --mount=type=secret,id=glitchtip_token \
    if [ -s /run/secrets/glitchtip_token ] && [ -n "$GLITCHTIP_URL" ] && [ -n "$GLITCHTIP_ORG" ] && [ -n "$GLITCHTIP_PROJECT" ]; then \
      RELEASE="${APP_VERSION:-$(node -p "require('./package.json').version")}"; \
      npx --yes @glitchtip/cli sourcemaps inject ./dist \
      || echo "Warning: GlitchTip debug-ID injection failed; uploaded maps may not resolve."; \
      npx --yes @glitchtip/cli sourcemaps upload ./dist \
        --url "$GLITCHTIP_URL" \
        --auth-token "$(cat /run/secrets/glitchtip_token)" \
        --org "$GLITCHTIP_ORG" \
        --project "$GLITCHTIP_PROJECT" \
        --release "$RELEASE" \
      || echo "Warning: GlitchTip source map upload failed; continuing without it."; \
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
