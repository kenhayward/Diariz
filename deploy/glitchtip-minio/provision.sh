#!/bin/sh
# ==========================================================================
# provision.sh - create GlitchTip's MinIO bucket and a SCOPED access key.
#
# THIS SCRIPT RUNS INSIDE THE `minio` CONTAINER. Do not run it on the host.
# Use ProvisionGlitchTipMinio.cmd (Windows) or provision-glitchtip-minio.sh
# (Linux/macOS), which pipe it in.
#
# Why inside the container:
#   - `mc` is already there (the healthcheck uses it), so the host needs
#     nothing installed,
#   - it reaches MinIO at localhost:9000 directly, so this does not depend on
#     the host publishing 9002 (which a hardened deployment may remove),
#   - MINIO_ROOT_USER / MINIO_ROOT_PASSWORD are already in the container's
#     environment, so the root credentials never appear in a host command
#     line, a shell history, or this script's arguments.
#
# The point of the exercise: GlitchTip must NOT hold the MinIO root
# credentials. Root would give an error-tracking service read/write access to
# every user's recorded audio. It gets its own bucket and a key that can
# reach nothing else - and this script proves that before it exits.
#
# Arguments (positional, supplied by the wrapper):
#   $1  secret key to create (hex; see the wrapper for why hex)
#   $2  bucket to create for GlitchTip           (default: glitchtip)
#   $3  the app's audio bucket, used for the negative test (default: recordings)
#   $4  literal "rotate" to replace an existing key; anything else is safe mode
#
# Idempotent: safe to re-run. Without "rotate" it will not touch an existing
# service account, so re-running never silently invalidates a working .env.
# ==========================================================================
set -e

SECRET="$1"
BUCKET="${2:-glitchtip}"
RECORDINGS="${3:-recordings}"
MODE="$4"

ACCESS_KEY="glitchtip-svc"
POLICY="glitchtip-only"
ALIAS_ROOT="gtroot"
ALIAS_CHECK="gtcheck"

fail() { echo "ERROR: $1" >&2; exit 1; }

[ -n "$SECRET" ] || fail "no secret supplied (the wrapper should have generated one)"
[ -n "$MINIO_ROOT_USER" ] || fail "MINIO_ROOT_USER is not set inside the container"
[ -n "$MINIO_ROOT_PASSWORD" ] || fail "MINIO_ROOT_PASSWORD is not set inside the container"

echo "==> Connecting as root"
mc alias set "$ALIAS_ROOT" http://localhost:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null \
  || fail "could not authenticate to MinIO as root"

# --------------------------------------------------------------------------
# 1. The bucket.
#
# MUST be its own bucket, never a prefix inside the recordings bucket: the
# platform restore in MaintenanceController wipes the recordings bucket
# before repopulating it, which would silently destroy the telemetry archive.
# A separate bucket is invisible to both backup and restore, because
# AudioStorage scopes every S3 call to the single configured Storage:Bucket.
# --------------------------------------------------------------------------
echo "==> Bucket: $BUCKET"
mc mb --ignore-existing "$ALIAS_ROOT/$BUCKET" >/dev/null || fail "could not create bucket $BUCKET"

# --------------------------------------------------------------------------
# 2. The policy - this bucket and nothing else.
# --------------------------------------------------------------------------
echo "==> Policy: $POLICY"
cat > /tmp/glitchtip-policy.json <<POLICY_JSON
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": ["s3:*"],
      "Resource": ["arn:aws:s3:::$BUCKET", "arn:aws:s3:::$BUCKET/*"]
    }
  ]
}
POLICY_JSON

# `policy create` replaces an existing policy of the same name, so this is
# idempotent and also repairs a policy that has been edited by hand.
mc admin policy create "$ALIAS_ROOT" "$POLICY" /tmp/glitchtip-policy.json >/dev/null \
  || fail "could not create policy $POLICY"
rm -f /tmp/glitchtip-policy.json

# --------------------------------------------------------------------------
# 3. The service account.
#
# Safe by default: if the key already exists we leave it completely alone,
# because rotating it would invalidate the GLITCHTIP_MINIO_SECRET_KEY in a
# live .env and break cold storage on the next restart with no obvious cause.
# --------------------------------------------------------------------------
if mc admin user info "$ALIAS_ROOT" "$ACCESS_KEY" >/dev/null 2>&1; then
  if [ "$MODE" = "rotate" ]; then
    echo "==> User: $ACCESS_KEY exists - rotating its secret (you asked for it)"
    mc admin user add "$ALIAS_ROOT" "$ACCESS_KEY" "$SECRET" >/dev/null \
      || fail "could not rotate the secret for $ACCESS_KEY"
    ROTATED=yes
  else
    echo "==> User: $ACCESS_KEY already exists - leaving it untouched"
    echo "    (re-run with the rotate option to replace its secret)"
    ROTATED=no
  fi
else
  echo "==> User: $ACCESS_KEY (creating)"
  mc admin user add "$ALIAS_ROOT" "$ACCESS_KEY" "$SECRET" >/dev/null \
    || fail "could not create user $ACCESS_KEY"
  ROTATED=yes
fi

# Attaching an already-attached policy is an error. Rather than parse the
# error text (this container has no grep/sed - it is a minimal busybox), make
# it idempotent by construction: detach first, ignoring the "wasn't attached"
# case, then attach and fail properly if that does not work.
mc admin policy detach "$ALIAS_ROOT" "$POLICY" --user "$ACCESS_KEY" >/dev/null 2>&1 || true
mc admin policy attach "$ALIAS_ROOT" "$POLICY" --user "$ACCESS_KEY" >/dev/null \
  || fail "could not attach policy $POLICY to $ACCESS_KEY"
echo "==> Policy attached to $ACCESS_KEY"

# --------------------------------------------------------------------------
# 4. Prove the boundary. This is the whole reason the script exists, so it
#    runs every time - including when nothing was changed.
# --------------------------------------------------------------------------
if [ "$ROTATED" = "yes" ]; then
  echo "==> Verifying the new key"
  mc alias set "$ALIAS_CHECK" http://localhost:9000 "$ACCESS_KEY" "$SECRET" >/dev/null \
    || fail "the new key cannot authenticate at all"

  printf 'provision check' | mc pipe --quiet "$ALIAS_CHECK/$BUCKET/.provision-check" >/dev/null 2>&1 \
    || fail "the new key cannot write to its own bucket ($BUCKET) - the policy is too tight"
  mc rm --quiet "$ALIAS_CHECK/$BUCKET/.provision-check" >/dev/null 2>&1 || true
  echo "    [ok] can write to $BUCKET"

  if mc ls "$ALIAS_ROOT/$RECORDINGS" >/dev/null 2>&1; then
    if mc ls "$ALIAS_CHECK/$RECORDINGS" >/dev/null 2>&1; then
      fail "THE NEW KEY CAN READ $RECORDINGS. The policy is wrong - do not use this key."
    fi
    echo "    [ok] cannot read $RECORDINGS"
  else
    echo "    [warn] bucket '$RECORDINGS' does not exist yet, so the negative test proved"
    echo "           nothing. Start the API once (it creates the bucket) and re-run to confirm."
  fi

  mc alias remove "$ALIAS_CHECK" >/dev/null 2>&1 || true
else
  echo "==> Skipping verification: the existing secret is not known to this script."
  echo "    Re-run with the rotate option if you need a key you can verify."
fi

mc alias remove "$ALIAS_ROOT" >/dev/null 2>&1 || true

echo "==> Done"

# Exit code tells the wrapper what to print, so it never has to parse output:
#   0 = a key was created or rotated, the wrapper's secret is the live one
#   3 = an existing key was left untouched, the wrapper's secret is not in use
#   1 = failed (via fail())
[ "$ROTATED" = "yes" ] && exit 0
exit 3
