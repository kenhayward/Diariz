#!/usr/bin/env bash
# Build diariz-system-audio_<version>_all.deb - a single configuration file that makes "what this machine
# is playing" selectable as a microphone, for every user on the machine.
#
# This is config, not software: no binaries, no dependencies beyond PipeWire, nothing to keep updated. It
# exists so a managed fleet can deploy the drop-in with apt instead of asking each user to paste commands.
# For a single machine, the per-user route in the Recording audio help article is simpler and needs no root.
#
# Usage:  packaging/linux/build-deb.sh [output-dir]
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT_DIR="${1:-$REPO_ROOT/dist}"

# Single source of truth: the same file the web app serves for the per-user install, so the two routes
# cannot drift apart. linuxSystemAudio.test.ts pins its contents and asserts this path is the one used.
CONF_SRC="$REPO_ROOT/apps/web/public/linux/99-diariz-system-audio.conf"
VERSION="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$REPO_ROOT/version.json")"

[ -f "$CONF_SRC" ] || { echo "missing $CONF_SRC" >&2; exit 1; }
[ -n "$VERSION" ] || { echo "could not read version from version.json" >&2; exit 1; }

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

# mktemp -d gives 0700; that would ship as the permissions of "/" inside the package.
chmod 0755 "$STAGE"
install -d -m 0755 "$STAGE/DEBIAN" "$STAGE/etc/pipewire/pipewire.conf.d"
install -m 0644 "$CONF_SRC" "$STAGE/etc/pipewire/pipewire.conf.d/99-diariz-system-audio.conf"

cat > "$STAGE/DEBIAN/control" <<EOF
Package: diariz-system-audio
Version: $VERSION
Section: sound
Priority: optional
Architecture: all
Depends: pipewire
Maintainer: Diariz <noreply@diariz.local>
Description: Make system audio recordable as a microphone (Diariz)
 Publishes what this machine is playing as an ordinary input device, named
 "System Audio (Diariz)", so it can be selected from the microphone list in a
 browser and recorded like any other source.
 .
 Chromium on Linux does not capture system audio when sharing a screen or a
 window, only when sharing a browser tab. PipeWire can expose the speaker
 monitor instead, but does not publish it as a device of its own, so browsers
 list nothing. This package installs the PipeWire drop-in that publishes it.
 .
 The device is visible to every application, not only Diariz.
EOF

# Marking the drop-in as a conffile means dpkg preserves local edits across upgrades and prompts on
# conflict, rather than silently overwriting a machine someone has deliberately tuned.
echo "/etc/pipewire/pipewire.conf.d/99-diariz-system-audio.conf" > "$STAGE/DEBIAN/conffiles"

# PipeWire only reads its configuration at start-up, so an install or removal has no effect on a running
# session. Tell the user rather than restarting audio underneath them mid-call.
cat > "$STAGE/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = "configure" ]; then
  echo "diariz-system-audio: installed."
  echo "Each logged-in user must run 'systemctl --user restart pipewire' (or log out and back in)"
  echo "before 'System Audio (Diariz)' appears in their microphone list."
fi
EOF
chmod 0755 "$STAGE/DEBIAN/postinst"

mkdir -p "$OUT_DIR"
DEB="$OUT_DIR/diariz-system-audio_${VERSION}_all.deb"
fakeroot dpkg-deb --build "$STAGE" "$DEB" >/dev/null
echo "$DEB"
