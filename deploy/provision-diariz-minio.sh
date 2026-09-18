#!/usr/bin/env bash
# ==========================================================================
# provision-diariz-minio.sh - give the Diariz API and worker a MinIO access
# key scoped to the recordings bucket, instead of the MinIO root credentials.
# (Linux/macOS; the Windows equivalent is ProvisionDiarizMinio.cmd, and both
# drive the same diariz-minio/provision.sh.)
#
# Run this ONCE PER SERVER. Safe to re-run: an existing key is left alone
# unless you ask for a rotation.
#
# WHY: without it the API and the worker both authenticate to MinIO as root,
# which can administer MinIO and read every bucket on it. The scoped key can
# only work inside the recordings bucket. The script proves the key cannot
# read another bucket or administer MinIO before it exits.
#
# HOW: the real work is diariz-minio/provision.sh, piped into the running
# `minio` container. The host needs no `mc`, does not depend on MinIO's host
# port, and never sees the MinIO root credentials.
#
# AFTERWARDS: add the two printed lines to deploy/.env and recreate the api,
# worker (and live-worker, if you run it):  docker compose up -d
#
# Usage:   ./provision-diariz-minio.sh
#          ./provision-diariz-minio.sh --rotate
#
# Requires: the stack's `minio` service running (docker compose up -d minio)
# ==========================================================================
set -euo pipefail

cd "$(dirname "$0")"

MODE=""
case "${1:-}" in
  --rotate|-rotate|/rotate) MODE="rotate" ;;
  "") ;;
  *) echo "Unknown argument '$1'. Usage: $(basename "$0") [--rotate]" >&2; exit 2 ;;
esac

BUCKET="recordings"
OTHER="glitchtip"

echo
echo "=== Diariz app MinIO provisioning ==="
echo

# --- Is the minio container actually up? A clear message beats a docker stack trace.
# Ask for the container ID rather than matching service names in text, so this
# stays identical to the Windows wrapper (where text matching breaks: docker
# emits UNIX line endings and findstr /x then never matches).
if [ -z "$(docker compose ps -q --status running minio 2>/dev/null)" ]; then
  echo "ERROR: the 'minio' service is not running." >&2
  echo "       Start it first:  docker compose up -d minio" >&2
  exit 1
fi

# --- Generate the secret.
#
# HEX, deliberately. A .env value containing $ would be interpolated by
# docker compose, a # would start a comment, and quotes or spaces would need
# escaping in three different places. Hex is [0-9a-f] only, so it is safe
# unquoted in .env, in a shell, and in a docker command line.
# 32 bytes = 64 hex characters = 256 bits.
if command -v openssl >/dev/null 2>&1; then
  SECRET="$(openssl rand -hex 32)"
elif [ -r /dev/urandom ]; then
  SECRET="$(od -An -vtx1 -N32 /dev/urandom | tr -d ' \n')"
else
  echo "ERROR: no openssl and no readable /dev/urandom; cannot generate a secret safely." >&2
  exit 1
fi

if [ "${#SECRET}" -ne 64 ]; then
  echo "ERROR: generated secret is ${#SECRET} characters, expected 64. Refusing to continue." >&2
  exit 1
fi

# --- Run the provisioning script inside the container.
# The secret is passed as an argument rather than piped because stdin is
# already carrying the script itself. It is hex, so it needs no quoting.
set +e
docker compose exec -T minio sh -s -- "$SECRET" "$BUCKET" "$OTHER" "$MODE" \
  < diariz-minio/provision.sh
STATUS=$?
set -e

case "$STATUS" in
  0)
    cat <<BANNER

==========================================================================
 Add these two lines to deploy/.env on THIS server:

MINIO_APP_ACCESS_KEY=diariz-app
MINIO_APP_SECRET_KEY=$SECRET

 Then recreate the api and worker:  docker compose up -d

 This secret is not stored anywhere else. If you lose it, re-run with
 --rotate to issue a new one.
==========================================================================

BANNER
    ;;
  3)
    cat <<'BANNER'

Nothing changed - 'diariz-app' already exists on this server.
The MINIO_APP_SECRET_KEY already in deploy/.env is still the right one.
If you have lost it, re-run with --rotate

BANNER
    ;;
  *)
    echo
    echo "Provisioning FAILED. Nothing above should be treated as usable." >&2
    echo
    exit 1
    ;;
esac
