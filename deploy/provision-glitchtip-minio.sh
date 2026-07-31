#!/usr/bin/env bash
# ==========================================================================
# provision-glitchtip-minio.sh - give GlitchTip its own MinIO bucket and an
# access key that can reach nothing else. (Linux/macOS; the Windows
# equivalent is ProvisionGlitchTipMinio.cmd, and both drive the same
# glitchtip-minio/provision.sh.)
#
# Run this ONCE PER SERVER, before GlitchTip's first start. Safe to re-run:
# an existing key is left alone unless you ask for a rotation.
#
# WHY: GlitchTip's DuckDB cold storage needs somewhere to put Parquet files.
# Handing it MINIO_ROOT_USER would give an error-tracking service read/write
# access to every user's recorded audio. This creates a bucket and a key
# scoped to that bucket, then PROVES the key cannot read the recordings
# bucket before it exits.
#
# The bucket must be its own, never a prefix inside `recordings`: platform
# restore wipes the recordings bucket, which would silently destroy the
# telemetry archive.
#
# HOW: the real work is glitchtip-minio/provision.sh, piped into the running
# `minio` container. The host needs no `mc` installed, does not depend on the
# host publishing MinIO's port, and never sees the MinIO root credentials -
# the container already has them in its own environment.
#
# Usage:   ./provision-glitchtip-minio.sh
#          ./provision-glitchtip-minio.sh --rotate
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

BUCKET="glitchtip"
RECORDINGS="recordings"

echo
echo "=== GlitchTip MinIO provisioning ==="
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
docker compose exec -T minio sh -s -- "$SECRET" "$BUCKET" "$RECORDINGS" "$MODE" \
  < glitchtip-minio/provision.sh
STATUS=$?
set -e

case "$STATUS" in
  0)
    cat <<BANNER

==========================================================================
 Add these two lines to deploy/.env on THIS server:

GLITCHTIP_MINIO_ACCESS_KEY=glitchtip-svc
GLITCHTIP_MINIO_SECRET_KEY=$SECRET

 This secret is not stored anywhere else. If you lose it, re-run with
 --rotate to issue a new one.
==========================================================================

BANNER
    ;;
  3)
    cat <<'BANNER'

Nothing changed - 'glitchtip-svc' already exists on this server.
The GLITCHTIP_MINIO_SECRET_KEY already in deploy/.env is still the right one.
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
