# 1. Architecture and contents

## 1.1 What is in `omi/firmware`

| Path | What it is | Built? |
|---|---|---|
| `omi/` | The **consumer device** application ("Omi CV 1"), nRF5340 | Yes - this is the shipping app |
| `devkit/` | The **DevKit1 / DevKit2** application, Seeed XIAO nRF52840 Sense | Yes - separate, older app |
| `test/` | An EVT bring-up app: a Zephyr **shell** with `ble`/`wifi`/`sd`/`mic`/`imu`/`motor` commands | Yes - separate app |
| `boards/omi/` | Out-of-tree board definition for `omi/nrf5340/cpuapp` (+ `_ns`, `cpunet`), devicetree, pinctrl, static partitions | Board root |
| `bootloader/` | MCUboot config **and its RSA-2048 signing keys**, plus legacy XIAO UF2 bootloaders | Signing input |
| `scripts/` | Docker/CI build scripts, plus `scripts/devkit/*.py` BLE client tools | Tooling |
| `BUILD_AND_OTA_FLASH.md` | Build + OTA guide (NCS 2.9.0, sysbuild, `dfu_application.zip`) | Docs |

The two applications are **not** variants of one codebase. They share the Opus 1.2.1 sources,
the general shape of the BLE audio service, and some file names, but `omi/src/lib/core/*` and
`devkit/src/*` have diverged substantially - most importantly in how offline audio is stored
and retrieved (see [04-offline-storage.md](04-offline-storage.md)).

## 1.2 Consumer hardware (from `boards/omi/omi_nrf5340_cpuapp.dts` + `hardware/consumer/bom/omi-bom.csv`)

| Function | Part | Connection |
|---|---|---|
| SoC | Nordic **nRF5340** (dual Cortex-M33: app core + net core) | - |
| Microphones | 2 x **TDK T5838** PDM MEMS, with hardware Acoustic Activity Detection | PDM0; `PDM_EN` P1.04, `THSEL` P1.05, `WAKE` P1.02, `CLK` P1.01 |
| Offline storage | **CSNP4GCR01-DPW**, 4 Gbit (512 MB) SD NAND, LGA8 | SPI3 `sdhc0`, CS `gpio1 11`, 24 MHz; power enable P1.10 |
| Code/OTA flash | Puya **P25Q16SH** 16 Mbit SPI NOR | SPI3 CS `gpio0 11`; holds the MCUboot secondary slot |
| Wi-Fi | Nordic **nRF7002** (populated, U2) | QSPI |
| IMU | ST **LSM6DS3TR-C** | I2C2 @ 0x6a, IRQ P1.13, power-gated by P1.12 |
| LEDs | RGB via PWM0 ch 0/1/2 (blue/green/red), inverted polarity | - |
| Button | Single user button, active low, pull-up | P0.26 |
| Haptic | Motor | P0.25 |
| Battery | 3.7 V **150 mAh** LiPo, SAADC sense on P0.06, charge-status on P0.07 | - |
| RF switch | Antenna switch enable | P1.03 |

## 1.3 Boot sequence (`omi/src/main.c`)

```
print reset reason (watchdog / NFC field / pin / soft / lockup / power-on)
watchdog_init()                       30 s timeout by default
haptic_init() + 100 ms buzz
led_start()
suspend_unused_modules()              powers down the SPI NOR
app_settings_init()                   NVS settings under the "omi/" subtree
init_rtc()                            restores the last persisted UTC epoch
lsm6dsl_time_boot_adjust_rtc()        adds IMU-measured off-time to that epoch
monitor_init()                        compiled out in production
battery_init() + battery_charge_start()
button_init() + activate_button_work()
app_sd_init()                         starts the SD worker thread
storage_init()                        starts the BLE storage-sync thread
transport_start()                     BLE on, GATT services registered, advertising, pusher thread
codec_start()                         Opus encoder + codec thread
mic_start()                           PDM capture + T5838 AAD
--> main loop: feed watchdog, update LED, sleep 1 s
```

Failures of `led`, `battery`, `button`, `sd_card`, `transport`, `codec` and `mic` are fatal
(`return ret` from `main`, which leaves the device dead until the watchdog resets it).
`settings`, `haptic`, `monitor` and `storage` failures are non-fatal.

## 1.4 Threads

| Thread | Stack | Priority | Job |
|---|---|---|---|
| `main` | 4096 | - | Watchdog feed + LED state, 1 Hz |
| mic | (in `mic.c`) | - | `dmic_read` 100 ms blocks, stereo->mono mix, AAD silence tracking |
| codec | 19000 | preempt 7 | Drains PCM ring, `opus_encode` per 20 ms frame, invokes callback |
| pusher | 4096 | preempt 7 | Drains the Opus ring: BLE notify **or** SD write |
| storage | 4096 | preempt 7 | Serves the BLE ring-sync protocol |
| sd worker | 8192 | 7 | The only thread that touches the SD block device; message-queue driven |
| aad | 1024 | 5 | Enters/exits T5838 hardware sleep |

## 1.5 LED semantics (`main.c: set_led_state`, 1 Hz)

| State | Indication |
|---|---|
| Device off | All off |
| **RTC never synced** | **Red blinking** (this is the "I am not recording" warning - see [05-findings.md](05-findings.md)) |
| Charging, battery >= 98% | Solid green |
| Charging, connected | Green/blue alternating |
| Charging, not connected | Green/red alternating |
| Running, BLE connected | Solid blue |
| Running, no BLE | Solid red |

Errors during boot use `feedback.c`: a red alert blink, a pause, then a colour-coded pattern
(red = system/LED, yellow = battery, green = button, cyan = storage, blue = comms,
magenta = audio).

## 1.6 Button

`omi/src/lib/core/button.c`, polled at 25 Hz:

- **< 300 ms** = single tap (twice within 600 ms = double tap) - notified over BLE only.
- **>= 3000 ms** = long press = `turnoff_all()`: haptic, BLE off, mic off, SD flush + unmount,
  save the IMU time base, then `sys_poweroff()` with the button armed as a level-low wake source.

There is no "start/stop recording" button. Recording is implicit and continuous.

## 1.7 Build

The blessed build is NCS **v2.9.0** with sysbuild, from `scripts/ci/build-cv1.sh`:

```bash
cp firmware/omi/omi.conf firmware/omi/prj.conf
west build -b omi/nrf5340/cpuapp firmware/omi --sysbuild -d build --pristine always \
  -- -DBOARD_ROOT=firmware
```

Note `omi.conf` is **copied to `prj.conf`** at build time; `prj.conf` is not in the tree, so
editing `prj.conf` directly is lost on the next CI build. `omi.conf` is the source of truth.

Sysbuild produces MCUboot + the net-core `ipc_radio` + `b0n`, signs everything with
`bootloader/mcuboot/root-rsa-2048.pem`, and emits:

- `dfu_application.zip` - the OTA package for the nRF Connect mobile app (DFU tab)
- `merged.hex` / `merged_CPUNET.hex` - full images for J-Link (flash **net core first**)

Reported footprint at the time `BUILD_AND_OTA_FLASH.md` was written: flash 262,908 / 982,528 B
(26.8%), RAM 244,556 / 440 KB (54.3%). Flash headroom is large; RAM headroom is not.

`scripts/ci/build-cv1.sh` references `omi/firmware/omi/BUILD.md`, which does not exist in this
tree - the equivalent content is in `BUILD_AND_OTA_FLASH.md` at the firmware root.
