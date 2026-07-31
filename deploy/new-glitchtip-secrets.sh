#!/usr/bin/env bash
# ==========================================================================
# new-glitchtip-secrets.sh - generate GlitchTip's secrets and assemble its
# EMAIL_URL, ready to paste into deploy/.env. (Linux/macOS; the Windows
# equivalent is NewGlitchTipSecrets.cmd.)
#
# Run this ONCE PER SERVER. Dev and prod must NOT share secrets: the two
# instances are deliberately independent, and a shared SECRET_KEY would let a
# session or a signed value from one be replayed against the other.
#
# Generates:
#   GLITCHTIP_SECRET_KEY          Django signing key
#   GLITCHTIP_POSTGRES_PASSWORD   its own database, not the app's
#   GLITCHTIP_EMAIL_URL           correctly percent-encoded (prompted)
#   GLITCHTIP_FROM_EMAIL
#
# It does NOT generate the MinIO key - that one has to be created inside
# MinIO itself, so it lives in provision-glitchtip-minio.sh.
#
# Nothing is written to disk.
#
# Usage:   ./new-glitchtip-secrets.sh
#          ./new-glitchtip-secrets.sh --noemail
# ==========================================================================
set -euo pipefail

NOEMAIL=0
case "${1:-}" in
  --noemail|-noemail|/noemail) NOEMAIL=1 ;;
  "") ;;
  *) echo "Unknown argument '$1'. Usage: $(basename "$0") [--noemail]" >&2; exit 2 ;;
esac

# --------------------------------------------------------------------------
# Random secrets.
#
# HEX, deliberately, for all of them. A .env value is read by docker compose,
# by a POSIX shell and sometimes by a Windows shell. In .env a `$` is
# interpolated by compose and a `#` starts a comment; in a shell, quotes,
# spaces, backticks and `!` all need escaping. base64 avoids `$` and `#` but
# still yields `+` `/` `=`, which bite the moment someone pastes a value into
# a shell command. Hex is [0-9a-f] only: no escaping anywhere, ever.
# --------------------------------------------------------------------------
hex_secret() {
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -hex 32
  elif [ -r /dev/urandom ]; then
    od -An -vtx1 -N32 /dev/urandom | tr -d ' \n'
  else
    echo "ERROR: no openssl and no readable /dev/urandom." >&2
    exit 1
  fi
}

SECRET_KEY="$(hex_secret)"
PG_PASSWORD="$(hex_secret)"

echo
echo "=== GlitchTip secrets ==="
echo
echo "Paste into deploy/.env on THIS server:"
echo
echo "GLITCHTIP_SECRET_KEY=$SECRET_KEY"
echo "GLITCHTIP_POSTGRES_PASSWORD=$PG_PASSWORD"

if [ "$NOEMAIL" -eq 1 ]; then
  echo
  echo "Skipping EMAIL_URL (you passed --noemail)."
  echo
  exit 0
fi

# --------------------------------------------------------------------------
# EMAIL_URL.
#
# smtp://user:password@host:port is a URL, so the user and password are URL
# components, not free text. An `@` in the password ends the userinfo early;
# a `:` splits user from password; a `/`, `?` or `#` terminates it. Any of
# those gives a confusing authentication failure rather than a parse error.
#
# Percent-encode everything outside the RFC 3986 unreserved set
# (A-Z a-z 0-9 - . _ ~).
# --------------------------------------------------------------------------
urlencode() {
  if command -v python3 >/dev/null 2>&1; then
    python3 -c 'import sys,urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""))' "$1"
    return
  fi
  # Pure-shell fallback, same rule, no dependencies.
  local s="$1" out="" i c
  for (( i = 0; i < ${#s}; i++ )); do
    c="${s:i:1}"
    case "$c" in
      [A-Za-z0-9.~_-]) out="$out$c" ;;
      *) out="$out$(printf '%%%02X' "'$c")" ;;
    esac
  done
  printf '%s' "$out"
}

echo
echo "=== EMAIL_URL ==="
echo
echo "GlitchTip needs SMTP for invitations and password resets."
echo "Press Enter at the host prompt to skip and fill it in by hand later."
echo

read -r -p "SMTP host (e.g. smtp.fastmail.com): " SMTP_HOST
if [ -z "$SMTP_HOST" ]; then
  echo
  echo "Skipped. GLITCHTIP_EMAIL_URL must still be set before first start."
  echo
  exit 0
fi

read -r -p "SMTP port [587]: " SMTP_PORT
SMTP_PORT="${SMTP_PORT:-587}"

read -r -p "SMTP username: " SMTP_USER
read -r -s -p "SMTP password (not echoed): " SMTP_PASS
echo

ENC_USER="$(urlencode "$SMTP_USER")"
ENC_PASS="$(urlencode "$SMTP_PASS")"

read -r -p "DEFAULT_FROM_EMAIL [$SMTP_USER]: " FROM_EMAIL
FROM_EMAIL="${FROM_EMAIL:-$SMTP_USER}"

echo
echo "GLITCHTIP_EMAIL_URL=smtp://${ENC_USER}:${ENC_PASS}@${SMTP_HOST}:${SMTP_PORT}"
echo "GLITCHTIP_FROM_EMAIL=${FROM_EMAIL}"
echo

if [ "$ENC_USER" != "$SMTP_USER" ] || [ "$ENC_PASS" != "$SMTP_PASS" ]; then
  cat <<'NOTE'
NOTE: your username or password contained characters that had to be
      percent-encoded to survive being inside a URL. That is correct per
      RFC 3986, but VERIFY IT WORKS: after first start, trigger a password
      reset and confirm the mail arrives. If it does not, the simplest fix
      is an SMTP app-password made only of letters and digits, which needs
      no encoding at all.

NOTE
fi
