# 8. Build and flash runbook (DevKit 2)

Everything needed to go from a clean Windows machine to modified firmware running on the
device. Written for the **Omi DevKit 2** - Seeed Studio XIAO nRF52840 Sense - which is the
hardware this project targets ([07-devkit2-target.md](07-devkit2-target.md)). The consumer
CV1 is different in almost every respect; see section 8.10.

> **Not yet executed.** This runbook was written by reading the build scripts, board
> definitions and configs in this tree, not by running them - there is no nRF Connect SDK
> toolchain on the machine it was written on. The commands are transcribed from
> `scripts/build-docker.sh`, `scripts/build-firmware-in-docker.sh` and
> `devkit/CMakePresets.json`, so they should be right, but treat the first run as a
> shakedown and correct this file as you go. Known-uncertain points are marked **[unverified]**.

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
MSYS_NO_PATHCONV=1 ./omi/firmware/scripts/build-docker.sh
```

`MSYS_NO_PATHCONV=1` matters on Windows: without it Git Bash rewrites the container-side
`/omi` mount target into a Windows path and the build fails with confusing "no such file"
errors from inside the container.

First run downloads the container image and then ~1.5 GB of nRF Connect SDK sources into
`omi/firmware/v2.7.0/`. Budget 20-40 minutes and a good connection. Later runs reuse both
and take a few minutes.

To start completely fresh (deletes the in-tree SDK and build directories):

```bash
MSYS_NO_PATHCONV=1 ./omi/firmware/scripts/build-docker.sh --clean
```

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

### The image is unpinned, and it has already bitten us

`build-docker.sh` pulls `ghcr.io/zephyrproject-rtos/ci` with no tag, so it follows `latest`.
That is not hypothetical: on 2026-08-25 the current image shipped a Debian Python marked
**externally managed (PEP 668)**, and the script's `pip install --user adafruit-nrfutil`
died with `error: externally-managed-environment` - taking the whole build with it, before a
single line was compiled, because the install was chained to the build with `&&`.

Fixed in this tree (see section 8.12), but the general hazard remains: if a build that
worked last month stops working and nothing in the tree changed, **suspect the image
first**. You can pin one without editing anything:

```bash
ZEPHYR_CI_IMAGE=ghcr.io/zephyrproject-rtos/ci:v0.26.4 \
  MSYS_NO_PATHCONV=1 ./omi/firmware/scripts/build-docker.sh
```

Pick a tag from the [package listing](https://github.com/zephyrproject-rtos/docker-image/pkgs/container/ci).
If you find one that works end to end, record it here - a known-good tag is worth more than
the newest one.

### Windows gotchas

* **`MSYS_NO_PATHCONV=1`** - covered above. Confirmed necessary in practice.
* **`the input device is not a TTY`** - the script runs `docker run --rm -it`. If your
  terminal refuses, prefix with `winpty`, or edit the script to drop `-it`. **[unverified]**
* **Slow builds / file-permission oddities** - the bind mount crosses the Windows/WSL2
  boundary. If it is painful, clone the repo inside the WSL2 filesystem and build there.
  **[unverified]**

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

On macOS `devkit/flash.sh` automates step 3 (it copies to `/Volumes/XIAO-SENSE`). There is
no Windows equivalent in the tree; drag-and-drop or `cp`.

---

## 8.6 Flash over the air (optional)

`build-firmware-in-docker.sh` also produces `zephyr.zip` via:

```bash
adafruit-nrfutil dfu genpkg --dev-type 0x0052 --dev-revision 0xCE68 \
    --application zephyr.hex zephyr.zip
```

That package is for the **Adafruit nRF52 bootloader's** DFU, which is a different mechanism
from the CV1's MCUboot OTA. **[unverified]** - the UF2 route is simpler, needs no app, and
is what this runbook recommends. Reach for OTA only if you have a reason to.

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
| `error: externally-managed-environment` | An image whose Python enforces PEP 668. Fixed in this tree (8.12); if it returns, pin an older `ZEPHYR_CI_IMAGE` |
| `west: command not found` / `cmake: not found` inside the container | The host PATH is being injected with `-e PATH=...`. Fixed in this tree (8.12) - check the script has not been reverted |
| Build finishes but there is no `zephyr.zip` | Expected when `adafruit-nrfutil` could not install. The UF2 is what you flash |
| `no such file or directory` from inside the container | Missing `MSYS_NO_PATHCONV=1` on Windows |
| `the input device is not a TTY` | `docker run -it` under a non-TTY terminal; use `winpty` or drop `-it` |
| `west update` fails or hangs | Network, or a half-initialised workspace: `--clean` and retry |
| `No board named 'xiao_ble_sense'` | Board renamed in NCS 2.7; use `xiao_ble/nrf52840/sense` |
| Build succeeds, no `zephyr.uf2` | UF2 output is board-driven; check the build log for `CONFIG_BUILD_OUTPUT_UF2` |
| `XIAO-SENSE` drive never appears | Double-tap rhythm, or a charge-only USB cable |
| Device boots, six red blinks | The card is unformatted or unreadable - format it on the PC (8.4) |
| Device records nothing, no blinks | Something is holding a BLE connection open (finding D6), or the card is full |
| `git status` full of build files | The ignore rules in `.gitignore` need extending |

---

## 8.12 Changes we made to the upstream build scripts

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

**`scripts/build-firmware-in-docker.sh`**

* Installs `adafruit-nrfutil` with a `--break-system-packages` fallback and then a graceful
  give-up - the same pattern `scripts/ci/build-cv1.sh` already uses for `ecdsa`.
* Makes OTA packaging conditional on the tool actually being present, and omits `zephyr.zip`
  from the summary when it was not produced.

**[unverified]** - these were reasoned from the scripts and syntax-checked, not run: the
sandbox this was written in cannot pull container images. The PEP 668 failure they address
was real and reported from a live run; whether the PATH fix was also load-bearing will only
be known once a build gets past the pip step.
