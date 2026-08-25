# 5. Findings

Everything below came out of reading the tree. Nothing was built or run - there is no NCS
toolchain in this repository - so items marked **(verify on build)** are read from the source
but not confirmed by a compiler.

Severity is relative to the goal of **using this device as an offline ambient recorder whose
audio ends up in Diariz**, not to Omi's own product.

---

## F1 - Offline recording is silently disabled until the clock is set. **High**

`omi/src/sd_card.c`, `process_write_data_req`:

```c
if (!rtc_is_valid()) {
    return;
}
uint32_t timestamp = get_utc_time();
if (timestamp == 0U || timestamp < 1700000000U) {
    return;
}
```

Audio packets are dropped, with no log and no counter, until a BLE client has written a UTC
epoch to the time-sync characteristic `19B10031-...` at least once. A freshly flashed device
that is never connected to a phone records **nothing**, indefinitely.

The only user-visible signal is in `main.c: set_led_state()`: when `rtc_is_valid()` is false
the LED blinks **red**. That is the same colour family as "not connected" (solid red), so it
is easy to miss.

*Implication:* any offline-first workflow must begin with a one-time (per firmware wipe) BLE
time sync. See [06-repurposing-for-diariz.md](06-repurposing-for-diariz.md).

---

## F2 - A BLE connection that does not subscribe stops recording entirely. **High**

`omi/src/lib/core/transport.c`, `pusher()`:

```c
if (conn && is_subscribed) {
    push_to_gatt(conn);            /* live stream */
} else if (!conn) {
    write_to_storage();            /* offline capture */
} else {
    bt_conn_unref(conn);           /* connected, not subscribed: audio is dropped */
    k_sleep(K_MSEC(10));
}
```

Storage is chosen only when there is **no connection at all**. While a central is connected
but not subscribed to `19B10001`, audio is neither streamed nor stored. The same happens if
the negotiated MTU stays below `MINIMAL_PACKET_SIZE` (100).

Real-world triggers: a phone that auto-reconnects to a remembered peripheral, a BLE scanner
app left open, a paired laptop, a failed MTU negotiation.

*Implication:* for reliable ambient capture, either keep every central away from the device
while recording, or change this branch to fall through to `write_to_storage()`.

---

## F3 - The clock travels backwards after an ungraceful reset. **High**

`omi/src/rtc.c`:

- `rtc_set_utc_time()` persists the epoch to NVS - but only when a client writes the time-sync
  characteristic. Nothing persists the clock periodically.
- `init_rtc()` on boot restores that saved epoch and re-bases it on the current (zero) uptime,
  then sets `utc_valid = true`.

After a watchdog reset, a brownout or a battery pull, the RTC therefore restarts at **the
moment of the last BLE time sync**, which may be hours or days earlier. `rtc_is_valid()` is
true, so recording resumes normally, the red-blink warning does **not** appear, and every
packet is stamped with a wrong - and possibly *decreasing* - timestamp.

The mitigation, `lsm6dsl_time_boot_adjust_rtc()`, only helps on the **graceful** path: the IMU
timestamp base is saved in the `turnoff_all()` / system-off preparation, not on a crash. It
also wraps: the LSM6DS3TR-C timestamp is 24 bits at 6.4 ms per tick, so it can only measure up
to about **29.8 hours** of off-time before aliasing.

*Implication:* a downstream tool cannot assume packet timestamps are monotonic across a reset.
Detect backwards jumps and treat them as segment boundaries.

---

## F4 - A sync is destructive, and only the BLE controller acknowledges it. **High**

`omi/src/lib/core/storage.c`, `sync_checkpoint_advance()` / `storage_data_tx_done()`.

During a `CMD_RING_READ`, bytes whose GATT notification completed are accumulated and the ring
read pointer is persisted forward every 2 s (and forcibly on DONE or disconnect). The
acknowledgement is the **link-layer** completion callback, not any application-level
confirmation from the client.

A client that requests a read and then crashes, runs out of disk, or discards the buffer will
have caused the device to free that audio anyway. There is no way to re-read below `read_seq`
(the firmware answers `SEQ_OUT_OF_RANGE`).

*Implication:* any Diariz-side sync tool must `fsync` received bytes to durable storage
continuously, not buffer the whole transfer in RAM.

---

## F5 - No link security at all. **High for this use case**

Every characteristic is `BT_GATT_PERM_READ` / `BT_GATT_PERM_WRITE` - never the `_ENCRYPT` or
`_AUTHEN` variants. No `bt_conn_auth_cb` is registered in the shipped `omi` app (there is one
in `src/lib/evt/ble.c`, which is not compiled). `CONFIG_BT_SMP=y` but `CONFIG_BT_SETTINGS` is
unset, so bonds are not persisted anyway.

Any BLE central in range can connect to a device advertising as `Omi`, stream the microphone
live, download the entire offline ring, and clear it. For a device intended to sit in
meetings, this is the most serious finding in the list. Details and the minimal fix in
[03-ble-protocol.md](03-ble-protocol.md) section 3.4 and
[06-repurposing-for-diariz.md](06-repurposing-for-diariz.md).

---

## F6 - Two Kconfig options cannot be enabled as shipped. **Medium** (verify on build)

`omi/CMakeLists.txt` compiles a fixed list of sources. **`src/lib/core/accel.c`,
`src/lib/core/speaker.c` and `src/lib/core/nfc.c` are not in it**, yet
`CONFIG_OMI_ENABLE_ACCELEROMETER` and `CONFIG_OMI_ENABLE_SPEAKER` guard calls into them from
`transport.c` and `button.c`. Turning either option on without also adding the source file to
`CMakeLists.txt` should fail to link.

Related: `transport.c` calls `speak(len, buf)` at line 370 but never includes `speaker.h`, so
that call site relies on an implicit declaration. It is unreachable in the shipped config
because the speaker is disabled.

Also unused: `src/lib/evt/*` is an entire second application (its own `main.c`) that no
CMakeLists in `omi/` references. The equivalent code lives in `test/src/`.

---

## F7 - `omi.conf` sets `CONFIG_I2C` both ways. **Medium** (verify on build)

```
line   5:  CONFIG_I2C=y
line 197:  CONFIG_I2C=n     # under "Disable unused peripherals"
```

The last assignment wins, so I2C is disabled. But the **only** I2C device in the board
devicetree is the LSM6DS3TR-C IMU on `i2c2` at 0x6a, and `CONFIG_LSM6DSL=y` is set 7 lines
later. `imu.c` talks to it through `zephyr/drivers/i2c.h`.

The IMU is what `lsm6dsl_time_boot_adjust_rtc()` uses to recover elapsed time across a
power-off (the F3 mitigation). If the bus is genuinely disabled, that recovery cannot work and
every graceful power cycle loses the elapsed time. `omi/README.md` lists IMU as the one
still-unchecked item in its bring-up TODO list, which is consistent with this.

Worth confirming with a real build and a boot log (`boot adjust: ...` lines are logged at INF,
so logging must be enabled to see them).

---

## F8 - Dead configuration and dead code. **Low**

Config (`omi/omi.conf`):

- `CONFIG_FILE_SYSTEM=y` and `CONFIG_FILE_SYSTEM_LITTLEFS=y` - the consumer app uses raw
  `disk_access_*` calls only; no filesystem is mounted. Flash and RAM spent for nothing.
- `CONFIG_OMI_ENABLE_VAD_GATE=y` is never tested in any `.c` file. It exists only as a Kconfig
  dependency for the threshold/hold/AAD symbols. There is no software VAD dropping audio - the
  threshold and hold only decide when to enter T5838 hardware sleep, which the comment in
  `omi.conf` correctly states.
- `CONFIG_OMI_VAD_DEBOUNCE_FRAMES` is declared in Kconfig and used nowhere.
- `CONFIG_SERIAL` / `CONFIG_AUDIO` / `CONFIG_AUDIO_DMIC` / `CONFIG_DISK_DRIVER_SDMMC` /
  `CONFIG_ENTROPY_GENERATOR` / `CONFIG_BT` / `CONFIG_BT_PERIPHERAL` are each set twice.
- The comment "Max transmit power supported by nRF52840" sits above the nRF5340 radio settings.

Code:

- `transport.c`: `test_pusher()`, `use_storage`, `MAX_FILES`, `MAX_AUDIO_FILE_SIZE`,
  `recent_file_size_updated`, `heartbeat_count` and the file-scope `offset` are all unused.
- `storage_is_on` is written in four places and **read nowhere**.
- `sd_write_pause()` is exported and never called.
- `MAX_STORAGE_BYTES` in `sd_card.h` is no longer referenced.
- The whole legacy file API in `sd_card.h` is a set of shims over the ring - see
  [04-offline-storage.md](04-offline-storage.md) section 4.6. `delete_audio_file(name)` ignores
  its argument and wipes everything.

---

## F9 - The bundled Python tools do not work against this firmware. **Medium**

`scripts/devkit/*.py` implement the **DevKit** file protocol (`[command, file_num, size32]`,
commands 0/1/2/3) against the **same** storage service UUIDs the consumer device uses for the
ring protocol (commands 0x10-0x13). A CV1 device will answer every one of those writes with
`ACK status 6` (invalid command).

`decode_audio.py` parses a fixed 83-byte stride, which matches neither device: it is an
*older* DevKit layout (3-byte prefix plus 80-byte padded entry), still visible commented out at
`devkit/src/transport.c:619-635`. The current DevKit writes 440-byte packed blocks and the
consumer device writes 444-byte ring packets. `get_audio_file.py` also has a hard-coded macOS
device UUID.

*Implication:* there is **no working reference client for offline retrieval from the consumer
device in this tree.** One has to be written. The recipe is in
[02-audio-formats.md](02-audio-formats.md) section 2.6 and
[03-ble-protocol.md](03-ble-protocol.md) section 3.3.

---

## F10 - MCUboot signing keys are committed in the clear. **Medium**

`bootloader/mcuboot/root-rsa-2048.pem` (the private signing key referenced by
`sysbuild.conf`) and `bootloader/mcuboot/enc-rsa2048-priv.pem` are both in the tree. Anyone
with this repository can sign an image that a stock Omi will accept over OTA.

That is a deliberate upstream choice for an open device, but if you are going to run this
firmware on a device you rely on, generate your own key pair and point
`SB_CONFIG_BOOT_SIGNATURE_KEY_FILE` at it. Note that doing so means the stock Omi app can no
longer push OTA updates to your device, which for this use case is a feature.

---

## F11 - Boot failures of the SD card brick the device. **Low/Medium**

In `main.c`, failure of `led_start`, `battery_init`, `battery_charge_start`, `button_init`,
`app_sd_init`, `transport_start`, `codec_start` or `mic_start` causes `main()` to return. The
main thread exits, the watchdog stops being fed, and 30 s later the device resets - into the
same failure. An intermittently failing SD card therefore produces a boot loop rather than a
degraded device that still streams live audio.

`app_sd_init()` does retry the mount five times with escalating delays before giving up, so
this is not hair-trigger.

---

## F12 - Endianness is inconsistent across the GATT surface. **Low**

- Storage service control notifications and commands: **big-endian** (`sys_put_be64` etc.).
- Ring packet timestamps on disk: **big-endian** (`sys_put_be32`).
- Time-sync characteristics: **native little-endian** (plain `memcpy` of a `uint32_t`).
- Storage status read characteristic: **native little-endian** (`uint32 payload[4]`).
- Features bitmask: **native little-endian**.

Easy to get wrong when writing a client. Documented per characteristic in
[03-ble-protocol.md](03-ble-protocol.md).

---

## F13 - Block-boundary parsing artifact in stored audio. **Medium for decoder authors**

Covered in detail in [02-audio-formats.md](02-audio-formats.md) section 2.5: the overflow
branch of `write_to_storage()` writes the *next* frame's length byte into the block it is
about to flush. A naive decoder will emit one corrupt Opus frame at every 440-byte boundary,
i.e. roughly **every 108 ms of audio**. Guard with `if (off + 1 + n > 440) break;`.

---

## F14 - Documentation drift. **Low**

- `scripts/ci/build-cv1.sh` cites `omi/firmware/omi/BUILD.md`, which does not exist. The
  content is in `firmware/BUILD_AND_OTA_FLASH.md`.
- `BUILD_AND_OTA_FLASH.md` claims "File System: EXT2 support for SD card storage". The
  consumer app mounts no filesystem at all; `CONFIG_FILE_SYSTEM_EXT2` belongs to the separate
  `test/` shell app.
- Its "Rollback Protection" claim *is* accurate (`CONFIG_MCUBOOT_DOWNGRADE_PREVENTION=y` in
  `omi/sysbuild/mcuboot.conf`), but note that `sysbuild.conf` also selects
  `SB_CONFIG_MCUBOOT_MODE_OVERWRITE_ONLY`, so there is no revert-on-failure slot: a bad image
  that boots far enough to look healthy cannot be rolled back automatically.
- `firmware/readme.md` describes the *DevKit* storage model ("a new file is created... it will
  try to delete the file on the device"), which no longer matches the consumer device.
- `omi.conf` pins `CONFIG_BT_DIS_FW_REV_STR="3.0.21"` while the prebuilt binaries in the tree
  are `FLASH_3.0.8`.

---

## What is good

Worth saying, because it affects how much of this you would want to rewrite:

- The **raw ring design is solid**: rotating metadata journal, self-describing batches with
  magic and sequence validation, lapped-window detection, tail-batch recovery on mount, and a
  bounded worst-case loss of about one batch (4 s) on a hard power cut.
- **Resumable sync with incremental checkpointing** is a genuinely thoughtful piece of work -
  a mid-transfer disconnect resumes rather than restarting.
- The **shared TX-throttle semaphore** between the audio pusher and the sync path, reserving
  two buffers for control notifications, is the right fix for a real starvation problem and is
  clearly commented as such.
- **Power management is serious**: hardware AAD on the mic, SD power gating tied to it,
  deferred AAD sleep during a sync, connection-interval tuning with retries.
- Comments throughout `transport.c` and `storage.c` explain *why*, including what the previous
  broken behaviour was. That is unusually good for firmware.
