#!/usr/bin/env bash
# ==========================================================================
# new-s3-keys.sh - print a fresh S3 key pair for deploy/.env. (Linux/macOS;
# the Windows equivalent is NewS3Keys.cmd.)
#
# SeaweedFS (the S3 store) reads its identities from the s3_identities config
# in docker-compose.yml, so there is nothing to provision inside the store:
# paste the two printed lines into deploy/.env. If the stack is already
# running, also run
#   docker compose up -d --force-recreate s3
# because Compose does not notice a key change on its own.
#
# Usage:   ./new-s3-keys.sh            -> S3_APP_*        (the API and worker)
#          ./new-s3-keys.sh root       -> S3_ROOT_*       (administers the store)
#          ./new-s3-keys.sh glitchtip  -> GLITCHTIP_S3_*  (observability overlay)
#
# Hex only: .env is interpolated by Compose and the keys are embedded in JSON,
# so a $, #, " or \ would be expanded, start a comment or break the JSON.
# ==========================================================================
set -euo pipefail
case "${1:-app}" in
  app) P=S3_APP ;; root) P=S3_ROOT ;; glitchtip) P=GLITCHTIP_S3 ;;
  *) echo "Usage: $(basename "$0") [app|root|glitchtip]" >&2; exit 2 ;;
esac
echo "${P}_ACCESS_KEY=$(openssl rand -hex 10)"
echo "${P}_SECRET_KEY=$(openssl rand -hex 24)"
