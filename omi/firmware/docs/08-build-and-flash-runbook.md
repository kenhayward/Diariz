# 8. Build and flash runbook (DevKit 2)

Everything needed to go from a clean Windows machine to modified firmware running on the
device. Written for the **Omi DevKit 2** - Seeed Studio XIAO nRF52840 Sense - which is the
hardware this project targets ([07-devkit2-target.md](07-devkit2-target.md)). The consumer
CV1 is different in almost every respect; see section 8.10.

> **Executed end to end on 2026-08-25.** This runbook was originally written by reading the
> build scripts, board definitions and configs in this tree rather than by running them. It has
> since been run for real on Windows 11 with Docker Desktop and produced a flashable
> `zephyr.uf2`, and this file has been corrected against what actually happened. Section 8.13
> records the exact configuration that worked.
>
> The **build** half (8.2, 8.11, 8.12) and the **flash** half (8.5) are now observed fact: the
> UF2 this produces has been flashed by the double-tap-and-copy route and the device runs it.
> Points still marked **[unverified]** are the ones nothing has tested - the VS Code route, OTA
> DFU, bootloader recovery, and re-enabling the console. Treat those the way this whole document
> used to be treated, and correct them as you go.

---

## 8.1 What you are building

| | |
|---|---|
| Application | `omi/firmware/devkit` |
| Board | `xiao_ble/nrf52840/sense` |
| SDK | nRF Connect SDK **v2.7.0** (the CV1 uses 2.9.0 - do not mix them up) |
| Config | `devkit/prj_xiao_ble_sense_devkitv2-adafruit.conf` |
| Overlay | `devkit/overlay/xiao_ble_sense_devkitv2-adafruit.overlay` |
| Bootloader | Adafruit UF2 (**not** MCUboot) |
| Primary output | `zephyr.uf2` - drag-and-drop onto the device |

---

## 8.2 Route A: Docker (recommended)

One prerequisite, no toolchain on your machine, and the same result on any OS. The build
scripts are already in the tree and already point at the DevKit 2 config.

### Install

1. **[Docker Desktop](https://www.docker.com/products/docker-desktop/)** with the WSL2
   backend. Start it and wait for the whale icon to settle before building.
2. **Git Bash** (ships with Git for Windows). The build scripts are bash; PowerShell will
   not run them.

That is the whole list. The container brings its own Zephyr SDK, west, CMake and Ninja.

### Build

From anywhere in the repo, in **Git Bash**:

```bash
./omi/firmware/scripts/build-docker.sh
```

That is the whole command - no environment prefix. Earlier revisions of this runbook told you to
prefix it with `MSYS_NO_PATHCONV=1`, because without it Git Bash rewrites the container-side
`/omi` mount target into a Windows path (`C:/Program Files/Git/omi`) and the build fails with
confusing "no such file" errors from inside the container. **The script now exports that itself**
(8.12) - verified to behave identically to prefixing it - so it is no longer the caller's problem.
The variable is meaningless on Linux and macOS, and setting it there is a harmless no-op.

First run downloads the container image and then ~1.5 GB of nRF Connect SDK sources into
`omi/firmware/v2.7.0/`. Budget 20-40 minutes and a good connection. Later runs reuse both
and take a few minutes.

To start completely fresh (deletes the in-tree SDK and build directories):

```bash
./omi/firmware/scripts/build-docker.sh --clean
```

You rarely want this. `west build` already runs with `--pristine always`, so the compile is clean
every time regardless; `--clean` additionally deletes `omi/firmware/v2.7.0/` and re-downloads the
~1.5 GB of SDK sources. Reach for it only when the west workspace itself is suspect.

### Where the output lands

```
omi/firmware/build/docker_build/
  zephyr.uf2   <- flash this (section 8.5)
  zephyr.hex   <- for a debugger / nrfjprog
  zephyr.bin   <- raw image
  zephyr.zip   <- OTA package (optional; absent if adafruit-nrfutil could not install)
```

`zephyr.zip` is the only optional one. It needs `adafruit-nrfutil`, which the build installs
on a best-effort basis - if it cannot, the build says so and carries on, because the UF2 does
not need it. A missing `zephyr.zip` is not a failed build.

All of it is gitignored, along with `omi/firmware/v2.7.0/`. Nothing from a build should
ever appear in `git status`; if it does, the ignore rules need extending.

### The image is pinned, and why it has to be

`build-docker.sh` pins `ghcr.io/zephyrproject-rtos/ci:v0.26.14`. This is not caution for its own
sake. The application targets **nRF Connect SDK 2.7.0**, which ships **Zephyr 3.6.99**, which
requires **Zephyr SDK 0.16.x**. That is a fixed target: no future SDK will ever satisfy it. So
there is no such thing as an image that is both current and correct here, and following `latest`
does not keep you up to date - it guarantees drifting out of the only range that can work.

It drifted twice on the same day, each time killing the build before a line was compiled:

| `latest` shipped | Failure |
|---|---|
| A Debian Python enforcing **PEP 668** | `pip install --user adafruit-nrfutil` died with `error: externally-managed-environment`, and the install was chained to the build with `&&` |
| **Zephyr SDK 1.0.1** | `CMake Error ... Could not find a configuration file for package "Zephyr-sdk" that is compatible with requested version "0.16"` |

Both are fixed in this tree (8.12). What the candidate tags actually carry:

| Tag | Zephyr SDK | |
|---|---|---|
| `latest` | 1.0.1 | too new - cannot configure |
| `v0.27.0` / `v0.27.4` | 0.17.0-rc1 / 0.17.0 | too new |
| `v0.26.15` | 0.16.9-rc2 | right series, but a release candidate |
| **`v0.26.14`** | **0.16.8** | **the pinned default** - the SDK NCS 2.7.0 specifies. Verified end to end |
| `v0.26.13` | 0.16.8 | equivalent fallback |
| `v0.26.4` | 0.16.1 | in range; the tag an earlier revision of this runbook guessed at |

The override still exists, so you can escape the pin without editing anything:

```bash
ZEPHYR_CI_IMAGE=ghcr.io/zephyrproject-rtos/ci:v0.26.13 ./omi/firmware/scripts/build-docker.sh
```

To read the SDK version out of a tag **before** pulling ~20 GB of image (this fetches only the
image config blob):

```bash
docker buildx imagetools inspect ghcr.io/zephyrproject-rtos/ci:v0.26.14 --format '{{json .Image}}' \
  | tr ',' '\n' | grep -i "zsdk_version=[0-9]"
```

Tags come from the [package listing](https://github.com/zephyrproject-rtos/docker-image/pkgs/container/ci).
The old advice still holds in spirit - if a build that worked last month stops working and nothing
in the tree changed, suspect the image - but with the tag pinned, that now takes someone changing
the pin.

### Windows gotchas

* **`MSYS_NO_PATHCONV=1`** - no longer the caller's problem; the script exports it (8.12).
  Verified both ways: without it, `-v "$REPO_ROOT:/omi"` mounts at `C:/Program Files/Git/omi` and
  the container cannot see the tree; with it, `/omi` lists `firmware hardware tools` as expected.
* **`cannot attach stdin to a TTY-enabled container because stdin is not a terminal`** - the script
  runs `docker run --rm -it`, so it needs a real terminal. Confirmed: it fails exactly this way
  from a non-interactive shell (a CI job, an editor task runner, an agent). Interactive Git Bash is
  fine. To run it headless, drop `-it` from the script; `winpty` is the other documented workaround
  but has not been tried. Depending on the Docker version you may see the older wording,
  `the input device is not a TTY`.
* **Slow builds / file-permission oddities** - the bind mount crosses the Windows/WSL2
  boundary. In practice this was a non-issue on 2026-08-25: a build from a repo on an NTFS drive
  completed with no permission problems and no notable slowness. Keep the fallback in mind - clone
  inside the WSL2 filesystem and build there - but do not pre-emptively reach for it.

---

## 8.3 Route B: nRF Connect for VS Code (native)

Use this when you want a debugger, live logs, or the IDE's Kconfig browser. It is more
setup, and it is the route the upstream project documents.

### Install

1. **[nRF Command Line Tools](https://www.nordicsemi.com/Products/Development-tools/nrf-command-line-tools)**
2. **[VS Code](https://code.visualstudio.com/)** + the **nRF Connect for VS Code Extension Pack**
3. In the extension's **Toolchain Manager**, install **nRF Connect SDK v2.7.0** and its
   matching toolchain.
4. **Python 3** on PATH, then `pip install adafruit-nrfutil` if you want to build OTA packages.

### Build

1. Open the **`omi/firmware`** folder in VS Code - **not** the repo root, and not
   `omi/firmware/devkit`. West only recognises the project and finds the board definitions
   from `omi/firmware`.
2. nRF Connect panel -> **Add build configuration**.
3. Pick the preset **`build_xiao_ble_sense_devkitv2-adafruit`** from
   `devkit/CMakePresets.json`. It sets the config file and overlay for you.
4. **Build**. Output appears under
   `devkit/build/build_xiao_ble_sense_devkitv2-adafruit/zephyr/`.

**Board-name discrepancy [unverified].** The CMake preset sets `BOARD=xiao_ble_sense`
(the pre-hardware-model-v2 name) while the Docker script uses `xiao_ble/nrf52840/sense`
(the NCS 2.7 name). If the preset fails to resolve a board, change it to
`xiao_ble/nrf52840/sense` - that is the spelling known to work in the container.

---

## 8.4 Prepare the card before first boot

**This changed, and it is the reason this runbook exists.** The firmware no longer formats
a card it cannot read. It used to: `CONFIG_FS_FATFS_MOUNT_MKFS` was enabled and the mount
did not set `FS_MOUNT_FLAG_NO_FORMAT`, so re-inserting a card FatFs disliked wiped every
recording on it, silently. The PC now owns formatting and deleting.

So, once per card:

1. Format it on the PC as **exFAT** (the factory format for a 128 GB SDXC card, and what
   `CONFIG_FS_FATFS_EXFAT=y` supports) or **FAT32**.
2. Insert it and power on.

If the card is unformatted, unreadable, or absent, the device now **blinks red six times
and stops** rather than booting into a state where it records nothing. That signal is the
only feedback you get, because this build ships with `CONFIG_CONSOLE=n`.

Routine cycle after that: pull the card, copy `a01.txt` off it, delete `a01.txt` **from the
PC**, re-insert. See [`omi/tools/omi-sync`](../../tools/omi-sync/README.md).

---

## 8.5 Flash over USB (UF2)

The DevKit uses the **Adafruit UF2 bootloader**, so flashing is a file copy. No debugger,
no J-Link, no driver.

1. Connect the XIAO over USB-C.
2. **Double-tap the reset button.** A removable drive named **`XIAO-SENSE`** appears.
3. Copy `zephyr.uf2` onto it.
4. The drive dismounts by itself and the device reboots into the new firmware.

If the drive does not appear, the double-tap was too slow or too fast - try again with a
brisk double-click rhythm. A cable that is charge-only will also do this; try another.

Confirmed working on 2026-08-25: a Docker-built `zephyr.uf2` flashed this way and the device
booted into it.

To find the drive from Git Bash - the UF2 bootloader volume is the one carrying `INFO_UF2.TXT`:

```bash
for d in /[a-z]; do [ -f "$d/INFO_UF2.TXT" ] && echo "bootloader drive: $d"; done
cp omi/firmware/build/docker_build/zephyr.uf2 /e/    # substitute the letter it reports
```

On macOS `devkit/flash.sh` automates step 3, **but not for a Docker build**: it looks for
`build/build_xiao_ble_sense_devkitv2-adafruit/zephyr/zephyr.uf2`, the nRF Connect for VS Code
output path (8.3), and exits with "Firmware file not found" if you built with Docker. Point it at
`build/docker_build/zephyr.uf2`, or just copy the file. There is no Windows equivalent in the
tree; drag-and-drop or `cp`.

---

## 8.6 Flash over the air (optional)

`build-firmware-in-docker.sh` also produces `zephyr.zip` via:

```bash
adafruit-nrfutil dfu genpkg --dev-type 0x0052 --dev-revision 0xCE68 \
    --application zephyr.hex zephyr.zip
```

That package is for the **Adafruit nRF52 bootloader's** DFU, which is a different mechanism
from the CV1's MCUboot OTA. The package is genuinely produced - a 303 KB `zephyr.zip` came out of
the verified build (8.13) - but **[unverified]** still applies to installing it: nothing has been
flashed this way. The UF2 route is simpler, needs no app, and is what this runbook recommends.
Reach for OTA only if you have a reason to.

---

## 8.7 Recovering a bricked bootloader

If the `XIAO-SENSE` drive stops appearing entirely, the bootloader itself may be damaged.
`omi/firmware/bootloader/` holds the images:

* `bootloader0.9.0.uf2` - the current bootloader, flashable over UF2 if the old one still
  enumerates.
* `deprecated/*.hex` - full images needing a debugger (J-Link or an nRF DK acting as one).

**[unverified]** - not something to try casually.

---

## 8.8 Getting logs out

The shipped DevKit 2 config is **silent**: `CONFIG_CONSOLE=n`, `CONFIG_PRINTK=n`, `CONFIG_LOG`
commented out, and the v2 overlay disables `uart0` outright. Every `LOG_INF`/`LOG_ERR` in
the firmware goes nowhere.

To get logs back, in `devkit/prj_xiao_ble_sense_devkitv2-adafruit.conf`:

```
CONFIG_CONSOLE=y
CONFIG_PRINTK=y
CONFIG_LOG=y
CONFIG_LOG_PRINTK=y
CONFIG_UART_CONSOLE=y
```

and, per `firmware/readme.md`, either disable offline storage while debugging:

```
CONFIG_OMI_ENABLE_OFFLINE_STORAGE=n
```

or keep it and drop the log thread's priority so logging does not fight SD writes:

```
CONFIG_LOG_PROCESS_THREAD_PRIORITY=5
CONFIG_LOG_PROCESS_THREAD_CUSTOM_PRIORITY=y
```

**You will also have to re-enable the UART**, which `firmware/readme.md` does not mention:
the v2 overlay contains `&uart0 { status = "disabled"; };`. Remove or comment that block,
or the console has no device to attach to. **[unverified]** - this contradiction between
the overlay and the documented debug steps has not been resolved on hardware. If serial
still gives nothing after both changes, USB CDC-ACM is the fallback, and it is not
configured in this tree either.

Then use the **nRF Serial Terminal** in VS Code, or any terminal at 115200 baud.

---

## 8.9 Verify the change actually landed

Firmware failures here are quiet, so check positively rather than assuming:

1. **It boots** - the boot LED sequence runs (red, green, blue, white).
2. **The card mounts** - no six-blink red pattern.
3. **It records** - leave it running somewhere noisy for a few minutes, power down, pull the
   card, and confirm `/SD:/audio/a01.txt` has grown.
4. **The audio is real** - run it through `omi-sync` and listen:

   ```bash
   cd omi/tools/omi-sync
   python -m omi_sync /path/to/a01.txt --dry-run --out ./out
   ffprobe -hide_banner ./out/omi-*.opus
   ```

   `ffprobe` should report opus, 1 channel, 48000 Hz.
5. **The no-format change works** - the deliberate test is to insert a card the firmware
   cannot read (an unformatted one, or one with an ext4 partition) and confirm you get the
   six red blinks and the card comes back **unchanged** on the PC. Do this with a scratch
   card, obviously.

---

## 8.10 The consumer CV1, for contrast

Do not follow this runbook for a CV1. It uses nRF Connect SDK **2.9.0**, board
`omi/nrf5340/cpuapp`, sysbuild with MCUboot, and produces `dfu_application.zip` /
`merged.hex` / `merged_CPUNET.hex` rather than a UF2. `scripts/ci/build-cv1.sh` is the
authoritative build, and `BUILD_AND_OTA_FLASH.md` covers OTA. Note that script references
`omi/firmware/omi/BUILD.md`, which does not exist ([05-findings.md](05-findings.md) F14).

---

## 8.11 Troubleshooting

| Symptom | Likely cause |
|---|---|
| `Could not find a configuration file for package "Zephyr-sdk"` compatible with `"0.16"` | The image's Zephyr SDK is outside the 0.16.x range NCS 2.7.0 requires. Fixed by the pin (8.12); if you overrode `ZEPHYR_CI_IMAGE`, choose a `v0.26.x` tag |
| `error: externally-managed-environment` | An image whose Python enforces PEP 668. Fixed in this tree (8.12); the pinned `v0.26.14` is not affected |
| `west: command not found` / `cmake: not found` inside the container | The host PATH is being injected with `-e PATH=...`. Fixed in this tree (8.12) - check the script has not been reverted |
| Build finishes but there is no `zephyr.zip` | Expected when `adafruit-nrfutil` could not install. The UF2 is what you flash. The pinned image installs it fine, so on `v0.26.14` you should get one |
| `no such file or directory` from inside the container | Git Bash path conversion. The script exports `MSYS_NO_PATHCONV=1` itself (8.12), so this should only bite when running docker by hand |
| `cannot attach stdin to a TTY-enabled container` / `the input device is not a TTY` | `docker run -it` under a non-TTY shell. Run it from an interactive terminal, drop `-it`, or try `winpty` |
| `west update` fails or hangs | Network, or a half-initialised workspace: `--clean` and retry |
| `No board named 'xiao_ble_sense'` | Board renamed in NCS 2.7; use `xiao_ble/nrf52840/sense` |
| Build succeeds, no `zephyr.uf2` | UF2 output is board-driven; check the build log for `CONFIG_BUILD_OUTPUT_UF2` |
| `XIAO-SENSE` drive never appears | Double-tap rhythm, or a charge-only USB cable |
| Device boots, six red blinks | The card is unformatted or unreadable - format it on the PC (8.4) |
| Device records nothing, no blinks | Something is holding a BLE connection open (finding D6), or the card is full |
| `git status` full of build files | The ignore rules in `.gitignore` need extending |

---

## 8.12 Changes we made to the upstream build scripts and docs

Recorded here because they are edits to vendored code, and a future diff against upstream
will show them.

**`scripts/build-docker.sh`**

* **Stopped passing the host `PATH` into the container.** The original had
  `-e PATH="/root/.local/bin:$PATH"`, which *replaces* the image's PATH with the host's - on
  Git Bash, a list of Windows paths that mean nothing inside a Linux container, so
  `west`/`cmake`/`ninja` would stop being found. It now prepends inside the container
  instead, in single quotes so the variable expands there.
* **Moved the `adafruit-nrfutil` install out of the `&&` chain** into the inner script, so a
  failure to install an optional packaging tool can no longer abort the build.
* **Added `ZEPHYR_CI_IMAGE`** so the image can be pinned without editing the script.
* **Pinned the default image to `ghcr.io/zephyrproject-rtos/ci:v0.26.14`** (Zephyr SDK 0.16.8),
  replacing the unpinned `:latest`. NCS 2.7.0 requires SDK 0.16.x, so an unpinned tag is not
  merely risky here, it is wrong - see 8.2. The override still works.
* **Exported `MSYS_NO_PATHCONV=1` inside the script**, so Windows callers no longer have to
  remember it. Verified equivalent to prefixing it on the invocation, and an inert no-op on
  Linux and macOS.

**`scripts/build-firmware-in-docker.sh`**

* Installs `adafruit-nrfutil` with a `--break-system-packages` fallback and then a graceful
  give-up - the same pattern `scripts/ci/build-cv1.sh` already uses for `ecdsa`.
* Makes OTA packaging conditional on the tool actually being present, and omits `zephyr.zip`
  from the summary when it was not produced.

**`scripts/docker-build.md`** (upstream documentation, not code)

* Added a banner marking it as a vendored copy whose paths assume the upstream layout, and
  pointing at this runbook as authoritative for this tree.
* Corrected the **Manual Build Process** recipe, which carried three real errors: it mounted
  `$(pwd)` (the repo root) rather than `$(pwd)/omi`, injected the host `PATH` with `-e`, and
  built `../app` - a directory that does not exist in this tree; the application is `../devkit`.
  Pinned the image in its example commands to match the script.

**Verified on 2026-08-25.** These edits were originally reasoned from the scripts and only
syntax-checked, because the sandbox they were written in could not pull container images. A
full build has since run with all of them in place and produced a flashable `zephyr.uf2`
(8.13), so the PEP 668 fallback, the PATH fix, the pin and the exported `MSYS_NO_PATHCONV`
are all now known to work together.

One caveat on the PATH fix specifically: the build that validated it also changed the image,
and `v0.26.14` may not have needed it. It is correct either way - injecting the host's Windows
`PATH` into a Linux container cannot be right - but it has not been isolated as independently
load-bearing.

---

## 8.13 The first verified build

The configuration that produced a flashable image, recorded so a future failure can be diffed
against a known-good state rather than guessed at.

| | |
|---|---|
| Date | 2026-08-25 |
| Host | Windows 11 (10.0.26200), Docker Desktop 29.6.1, WSL2 backend |
| Shell | Git Bash, interactive |
| Image | `ghcr.io/zephyrproject-rtos/ci:v0.26.14` - Zephyr SDK 0.16.8, ~19.9 GB on disk |
| SDK sources | `omi/firmware/v2.7.0/`, ~2.2 GB after `west update` |
| Command | `./omi/firmware/scripts/build-docker.sh` |
| Result | All four artifacts, including the optional `zephyr.zip` |

Artifact sizes, useful as a sanity check that a later build produced something comparable:

| File | Bytes |
|---|---|
| `zephyr.uf2` | 604,672 |
| `zephyr.hex` | 850,559 |
| `zephyr.zip` | 303,109 |
| `zephyr.bin` | 302,316 |

Confirmed at the same time:

* **`git status` stays clean.** Neither `v2.7.0/` nor `build/` appears - the ignore rules in
  `.gitignore` (lines 482-483) cover them, as 8.2 claims.
* **It builds correctly inside a git worktree.** `build-docker.sh` derives its mount from the
  script's own location rather than the caller's working directory, and mounts only `<repo>/omi`,
  so a worktree builds its own tree with no reference to the main checkout. Note the SDK download
  is **per-worktree** - it is not shared with the main clone.
* **`zephyr.zip` is produced on this image.** `v0.26.14` is Ubuntu 22.04 with Python 3.10, so
  `adafruit-nrfutil` installs without hitting the PEP 668 fallback.

### Disk cost

Not small, and worth knowing before you start:

| | |
|---|---|
| Pinned image | ~19.9 GB |
| SDK sources per worktree | ~2.2 GB |
| Build output | ~2 MB |

If a failed run already pulled `ghcr.io/zephyrproject-rtos/ci:latest` (~31.6 GB), it is now
useless and can be reclaimed:

```bash
docker image rm ghcr.io/zephyrproject-rtos/ci:latest
```

### Still not exercised

Flashing (8.5) has since been done, and the device runs the firmware this build produces. What
remains unconfirmed:

* **The six-blink failure signal** (8.4, 8.9 step 5) - no card has been deliberately presented
  unformatted or unreadable, so the no-format guard has not been seen refusing one.
* **A full audio round trip** (8.9 step 4) - no `a01.txt` has been decoded through `omi-sync` and
  listened to end to end.
* Everything already marked **[unverified]**: the VS Code route (8.3), OTA DFU (8.6), bootloader
  recovery (8.7), and re-enabling the console (8.8).
