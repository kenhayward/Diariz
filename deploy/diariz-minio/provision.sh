#!/bin/sh
# ==========================================================================
# provision.sh - give the Diariz API and worker a MinIO access key scoped to
# the recordings bucket, so neither needs the MinIO root credentials.
#
# THIS SCRIPT RUNS INSIDE THE `minio` CONTAINER. Do not run it on the host.
# Use ProvisionDiarizMinio.cmd (Windows) or provision-diariz-minio.sh
# (Linux/macOS), which pipe it in. The reasons are the same as for
# glitchtip-minio/provision.sh: `mc` is already in the container, it reaches
# MinIO on localhost:9000 without the host port, and the root credentials are
# read from the container's own environment rather than a command line.
#
# The key can do anything to the recordings bucket (the API uploads, streams,
# deletes, and wipes-and-refills it on a platform restore) and can list bucket
# NAMES (the API checks the bucket exists at startup). It cannot read any other
# bucket - in particular GlitchTip's - and cannot administer MinIO. The script
# proves both before it exits.
#
# Arguments (positional, supplied by the wrapper):
#   $1  secret key to create (hex; see the wrapper for why hex)
#   $2  the recordings bucket                       (default: recordings)
#   $3  another bucket used for the negative test   (default: glitchtip)
#   $4  literal "rotate" to replace an existing key; anything else is safe mode
#
# Idempotent: safe to re-run. Without "rotate" it will not touch an existing
# account, so re-running never silently invalidates a working .env.
# ==========================================================================
set -e

SECRET="$1"
BUCKET="${2:-recordings}"
OTHER="${3:-glitchtip}"
MODE="$4"

ACCESS_KEY="diariz-app"
POLICY="diariz-app-recordings"
ALIAS_ROOT="dzroot"
ALIAS_CHECK="dzcheck"

fail() { echo "ERROR: $1" >&2; exit 1; }

[ -n "$SECRET" ] || fail "no secret supplied (the wrapper should have generated one)"
[ -n "$MINIO_ROOT_USER" ] || fail "MINIO_ROOT_USER is not set inside the container"
[ -n "$MINIO_ROOT_PASSWORD" ] || fail "MINIO_ROOT_PASSWORD is not set inside the container"

echo "==> Connecting as root"
mc alias set "$ALIAS_ROOT" http://localhost:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null \
  || fail "could not authenticate to MinIO as root"

# --------------------------------------------------------------------------
# 1. The bucket. Normally the API creates it on first start; creating it here
#    too means this works on a brand-new server before the API has ever run.
# --------------------------------------------------------------------------
echo "==> Bucket: $BUCKET"
mc mb --ignore-existing "$ALIAS_ROOT/$BUCKET" >/dev/null || fail "could not create bucket $BUCKET"

# --------------------------------------------------------------------------
# 2. The policy - the recordings bucket, plus listing bucket names.
# --------------------------------------------------------------------------
echo "==> Policy: $POLICY"
cat > /tmp/diariz-app-policy.json <<POLICY_JSON
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": ["s3:*"],
      "Resource": ["arn:aws:s3:::$BUCKET", "arn:aws:s3:::$BUCKET/*"]
    },
    {
      "Effect": "Allow",
      "Action": ["s3:ListAllMyBuckets"],
      "Resource": ["arn:aws:s3:::*"]
    }
  ]
}
POLICY_JSON

# `policy create` replaces an existing policy of the same name, so this is idempotent.
mc admin policy create "$ALIAS_ROOT" "$POLICY" /tmp/diariz-app-policy.json >/dev/null \
  || fail "could not create policy $POLICY"
rm -f /tmp/diariz-app-policy.json

# --------------------------------------------------------------------------
# 3. The account. Left alone if it exists, unless a rotation was asked for.
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

# Detach first so attach is idempotent (this busybox container has no grep/sed to parse errors with).
mc admin policy detach "$ALIAS_ROOT" "$POLICY" --user "$ACCESS_KEY" >/dev/null 2>&1 || true
mc admin policy attach "$ALIAS_ROOT" "$POLICY" --user "$ACCESS_KEY" >/dev/null \
  || fail "could not attach policy $POLICY to $ACCESS_KEY"
echo "==> Policy attached to $ACCESS_KEY"

# --------------------------------------------------------------------------
# 4. Prove the boundary, both ways.
# --------------------------------------------------------------------------
if [ "$ROTATED" = "yes" ]; then
  echo "==> Verifying the new key"
  mc alias set "$ALIAS_CHECK" http://localhost:9000 "$ACCESS_KEY" "$SECRET" >/dev/null \
    || fail "the new key cannot authenticate at all"

  printf 'provision check' | mc pipe --quiet "$ALIAS_CHECK/$BUCKET/.provision-check" >/dev/null 2>&1 \
    || fail "the new key cannot write to $BUCKET - the policy is too tight"
  mc cat "$ALIAS_CHECK/$BUCKET/.provision-check" >/dev/null 2>&1 \
    || fail "the new key cannot read from $BUCKET - the policy is too tight"
  mc rm --quiet "$ALIAS_CHECK/$BUCKET/.provision-check" >/dev/null 2>&1 \
    || fail "the new key cannot delete from $BUCKET - the policy is too tight"
  echo "    [ok] can write, read and delete in $BUCKET"

  mc ls "$ALIAS_CHECK" >/dev/null 2>&1 \
    || fail "the new key cannot list bucket names - the API's startup bucket check would fail"
  echo "    [ok] can list bucket names"

  if mc ls "$ALIAS_ROOT/$OTHER" >/dev/null 2>&1; then
    if mc ls "$ALIAS_CHECK/$OTHER" >/dev/null 2>&1; then
      fail "THE NEW KEY CAN READ $OTHER. The policy is wrong - do not use this key."
    fi
    echo "    [ok] cannot read $OTHER"
  else
    echo "    [info] no '$OTHER' bucket on this server, so there was no other bucket to test against"
  fi

  if mc admin user list "$ALIAS_CHECK" >/dev/null 2>&1; then
    fail "THE NEW KEY CAN ADMINISTER MINIO. The policy is wrong - do not use this key."
  fi
  echo "    [ok] cannot administer MinIO"

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
