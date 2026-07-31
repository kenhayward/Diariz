#!/usr/bin/env sh
# Upload the built source maps to GlitchTip. Invoked by apps/web/Dockerfile's build stage.
#
# THIS LIVES IN A FILE RATHER THAN INLINE IN THE DOCKERFILE for one specific reason: BuildKit prints
# the entire RUN command as the step's label, on every build. With the logic inline, a completely
# successful build printed eight lines of "ERROR: source map upload failed", "Refusing to ship",
# "NOTE: ..." at the operator - script text, not output, and identical whether the step passed or
# failed. A log that cries wolf on every build trains you to skim past the one that matters. Keeping
# it here means the step label is a single short command and those words only ever appear when
# something has actually gone wrong.
#
# Inputs (build ARGs, which Docker exposes to RUN as environment variables):
#   GLITCHTIP_URL / GLITCHTIP_ORG / GLITCHTIP_PROJECT   where to send them
#   APP_VERSION                                          release tag; falls back to package.json
#   GLITCHTIP_SOURCEMAPS_OPTIONAL                        non-empty downgrades failure to a warning
# Plus the BuildKit secret mounted at /run/secrets/glitchtip_token.
set -e

TOKEN_FILE=/run/secrets/glitchtip_token

# `-s` (non-empty), NOT `-f` (exists). A bare `docker build --secret` leaves the file absent when
# omitted, but deploy/docker-compose.yml's `environment:`-sourced secret ALWAYS mounts the file and
# leaves it empty when GLITCHTIP_TOKEN is unset in .env - `-f` would wrongly read that as "provided".
if [ ! -s "$TOKEN_FILE" ] || [ -z "$GLITCHTIP_URL" ] || [ -z "$GLITCHTIP_ORG" ] || [ -z "$GLITCHTIP_PROJECT" ]; then
  echo "No GlitchTip credentials; skipping source map upload."
  exit 0
fi

# Must match what telemetry.ts sends as `release` (__APP_VERSION__ via vite.config.ts's
# appVersion()), or GlitchTip cannot attach these maps to incoming browser events. Same fallback
# order as appVersion() so the two agree whether or not the ARG was passed.
RELEASE="${APP_VERSION:-$(node -p "require('./package.json').version")}"

# Passed as env, not --auth-token, so the token never appears in a process command line.
export SENTRY_URL="$GLITCHTIP_URL"
export SENTRY_ORG="$GLITCHTIP_ORG"
export SENTRY_PROJECT="$GLITCHTIP_PROJECT"
SENTRY_AUTH_TOKEN="$(cat "$TOKEN_FILE")"
export SENTRY_AUTH_TOKEN

# sentry-cli, NOT @glitchtip/cli, and pinned. GlitchTip speaks the Sentry protocol, and its own CLI
# (1.0.0, the only version ever published) cannot do this: it uploads each file individually then
# calls `releases/{version}/assemble/` once per file, an endpoint that expects a single zip of every
# artifact plus a manifest.json - so the server raises BadZipFile per artifact and registers nothing,
# while the CLI still exits 0 because the upload part worked. `--release` is mandatory and is what
# selects that path, so no flag avoids it. Measured against 6.2.3: @glitchtip/cli left 185 blobs, 0
# files and 0 bundles; sentry-cli produced 93 assembled bundles from the same dist.
#
# Pinned because an unpinned client against a server pinned to 6.2 breaks one day with nothing in the
# diff to explain it.
CLI="@sentry/cli@3.6.2"

# `inject` is load-bearing, not decorative. vite uses `sourcemap: "hidden"`, which strips the
# `//# sourceMappingURL=` comment - so an uploaded map has nothing tying it to the minified frame it
# explains, and GlitchTip silently leaves the trace minified. inject writes a matching debug ID into
# each bundle and its map. Its failure is treated exactly like an upload failure: maps uploaded
# without debug IDs do not resolve, so "uploaded" would be false reassurance.
if npx --yes "$CLI" sourcemaps inject ./dist \
   && npx --yes "$CLI" sourcemaps upload ./dist --release "$RELEASE"; then
  echo "Source maps uploaded for release $RELEASE."
  exit 0
fi

if [ -n "$GLITCHTIP_SOURCEMAPS_OPTIONAL" ]; then
  echo "Warning: source map upload failed, and GLITCHTIP_SOURCEMAPS_OPTIONAL is set - shipping without readable stack traces."
  exit 0
fi

# Credentials were supplied, so readable stack traces were explicitly asked for. Shipping without
# them silently is the wrong default - a misconfigured org slug went unnoticed for a whole deployment
# that way.
echo "ERROR: source map upload failed, and GlitchTip credentials ARE configured."
echo "       Refusing to ship an image whose stack traces cannot be read. The CLI's own error is above."
echo "       Common causes: SENTRY_ORG must be the lowercase SLUG from the URL, not the display name;"
echo "       a 500 from chunk-upload usually means AWS_STORAGE_BUCKET_NAME is unset on the server."
echo "       NOTE: this only proves the UPLOAD failed. Assembly happens server-side and"
echo "       asynchronously, so a PASSING build can still leave traces minified - check the"
echo "       glitchtip container log for assemble_artifacts if so."
echo "       To ship anyway, set GLITCHTIP_SOURCEMAPS_OPTIONAL=1."
exit 1
