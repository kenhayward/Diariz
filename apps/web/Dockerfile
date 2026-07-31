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

# Inject debug IDs into the bundles and upload the source maps to GlitchTip, so production stack
# traces stay readable; the runtime stage below deletes the maps regardless.
#
# The logic lives in scripts/upload-sourcemaps.sh, NOT inline here, because BuildKit prints the whole
# RUN command as the step label on every build - inline, a completely successful build printed eight
# lines of "ERROR: source map upload failed" at the operator as script text, identical whether the
# step passed or failed. See the script for the full reasoning: why sentry-cli rather than
# @glitchtip/cli, why the version is pinned, why injection failure is fatal, and the one failure mode
# this cannot catch (assembly is asynchronous and happens after the CLI exits, so a green build
# proves "uploaded", never "assembled").
#
# The token is a BuildKit secret, never a build ARG - an ARG is recorded in the image history and
# readable by anyone who can pull the image.
ARG GLITCHTIP_URL=""
ARG GLITCHTIP_ORG=""
ARG GLITCHTIP_PROJECT=""
ARG GLITCHTIP_SOURCEMAPS_OPTIONAL=""
RUN --mount=type=secret,id=glitchtip_token sh ./scripts/upload-sourcemaps.sh

# Serve the build with nginx, proxying /api and /hubs to the API (same-origin → no CORS).
FROM nginx:alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
# Source maps are uploaded to GlitchTip at build time (see the sourcemaps upload step above), never
# served: without this they stay fetchable by guessing the filename even though nothing links to them.
RUN find /usr/share/nginx/html -name '*.map' -delete
EXPOSE 80
