# Linux system-audio drop-in

Recording **system audio** on Linux cannot go through the browser's screen-share dialog: Chromium does not
implement system-audio capture when sharing a screen or a window, only when sharing a browser tab. The
route that always works is to record what the speakers are playing as though it were a microphone.

PipeWire can do that, but it does not publish a sink's *monitor* as a node of its own, so browsers have
nothing to list. The fix is one configuration file that publishes it as a real `Audio/Source`.

## One file, two ways to install it

The canonical copy is **`apps/web/public/linux/99-diariz-system-audio.conf`**, served by the web app so the
Recording audio help article can tell a user to download it. `build-deb.sh` packages that same file, so the
per-user and fleet routes cannot drift apart (`apps/web/src/lib/linuxSystemAudio.test.ts` pins both).

| Audience | Install to | Needs root |
| --- | --- | --- |
| One user | `~/.config/pipewire/pipewire.conf.d/` | no |
| Every user on a machine | `/etc/pipewire/pipewire.conf.d/` | yes |

Both are read by PipeWire at start-up, so after installing, each logged-in user runs
`systemctl --user restart pipewire` (or logs out and back in) once.

## Building the package

```bash
packaging/linux/build-deb.sh            # writes dist/diariz-system-audio_<version>_all.deb
packaging/linux/build-deb.sh /tmp/out   # or choose the output directory
```

Needs `dpkg-deb` and `fakeroot`. The version comes from `/version.json`, so it moves with the app release.

The package contains exactly one configuration file and depends only on `pipewire`. It carries no binaries
and nothing that needs updating - if the config never changes, an installed copy never needs to.
`/etc/pipewire/pipewire.conf.d/99-diariz-system-audio.conf` is registered as a **conffile**, so dpkg
preserves local edits across upgrades rather than overwriting a machine someone has tuned deliberately.

For Ansible / Puppet / MDM, skip the package and copy the same file into `/etc/pipewire/pipewire.conf.d/`.

## Verifying it works

On the target machine, after restarting PipeWire:

```bash
# the device should be listed
pw-dump | grep -A2 diariz_system_audio

# play something, then capture from it and check the recording is not silent
pw-record --target diariz_system_audio /tmp/check.wav
```

Verified on a Ryzen AI Max+ 395 (Radeon 8060S, PipeWire 1.6.2, Ubuntu/GNOME on Wayland): with this file as
the only mechanism, a 440 Hz tone played to the speakers was recovered from the published source at exactly
**440.0 Hz** at the expected amplitude, and a silence control recorded **RMS 0.0 / peak 0**.

## The trap this does not solve on its own

The browser's **echo canceller** treats a loopback capture as echo - it is, by definition, the sound coming
out of your own speakers - and removes it. A recording then runs for a second or two and falls silent while
the level meter looks healthy. Diariz defaults echo cancellation, noise suppression and auto gain to **off**
for this reason (see `apps/web/src/lib/audioDevices.ts`). If you deploy this config to a fleet running an
older Diariz, expect silent recordings until they upgrade.
