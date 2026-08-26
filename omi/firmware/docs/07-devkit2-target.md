# 7. DevKit 2 - the target hardware for this project

**Confirmed build: Omi DevKit 2** - a Seeed Studio **XIAO nRF52840 Sense** on the DevKit 2
carrier board, with a **128 GB microSD card**.

That settles the open question in [06-repurposing-for-diariz.md](06-repurposing-for-diariz.md)
section 6.6. This document is the DevKit-2-specific review; it supersedes the consumer-device
(CV1) material wherever the two disagree. Docs 01-05 describe the CV1 unless they say
otherwise - read them for background, but treat **this** file as authoritative for your device.

Build target: `devkit/`, board `xiao_ble_sense`, preset
`build_xiao_ble_sense_devkitv2-adafruit`, config
`prj_xiao_ble_sense_devkitv2-adafruit.conf`, overlay
`overlay/xiao_ble_sense_devkitv2-adafruit.overlay`.

## 7.1 What changes versus the consumer device

| | Consumer CV1 (docs 01-05) | **DevKit 2 (your device)** |
|---|---|---|
| SoC | nRF5340, dual core | **nRF52840**, single core |
| Storage medium | Soldered 512 MB SD NAND | **Removable microSD, 128 GB** |
| Storage layout | Raw block ring buffer, no filesystem | **FAT/exFAT filesystem** |
| Retrieval | BLE ring protocol only | **Eject the card and read it on a PC** |
| Capacity | ~33 hours | **effectively unlimited - see 7.4** |
| Files | n/a | A single, ever-growing `/SD:/audio/a01.txt` |
| Opus frame | 320 samples (20 ms) | **160 samples (10 ms)** |
| Mic | 2x T5838, stereo mixed to mono | **1x PDM mic, mono** (XIAO Sense onboard) |
| Silence gating | Hardware AAD, mic sleeps in silence | **None - records continuously** |
| Wall clock | Per-packet UTC timestamp + BLE time sync | **None at all - see 7.3** |
| Wi-Fi | nRF7002 populated | **None** |
| Serial console | UART enabled | **UART disabled in the overlay** |
| Bootloader | MCUboot, OTA via `dfu_application.zip` | **Adafruit UF2** - drag-and-drop `zephyr.uf2` |

The things that carry over unchanged: Opus at 32 kbps VBR mono 16 kHz, the 440-byte block
packing (including its parsing artifact), the BLE audio service and its 3-byte notification
header, and the "any BLE connection suppresses offline recording" hazard.

## 7.2 On-disk format

Simpler than the CV1's, and **missing the timestamp**:

```
/SD:/audio/a01.txt  =  a concatenation of 440-byte blocks, nothing else

each 440-byte block:
[len0][opus frame 0][len1][opus frame 1] ... [lenN][opus frame N][padding]
 1 B                 1 B                      1 B
```

There is **no 4-byte header** on a DevKit block - `write_to_file(storage_temp_data, 440)`
writes the packed block straight to the file. Frames are **10 ms** (160 samples at 16 kHz),
so a 440-byte block holds roughly 10 frames, about **100 ms** of audio.

The **same block-boundary trap applies** ([05-findings.md](05-findings.md) F13): the overflow
branch writes the next frame's length byte into the block it is about to flush, so a block can
end with a length byte that has no data behind it. Guard with
`if (n == 0 or off + 1 + n > 440) break`.

Decoding is the CV1 recipe minus the header:

```python
import opuslib
dec = opuslib.Decoder(16000, 1)
pcm = bytearray()
data = open("a01.txt", "rb").read()
for i in range(0, len(data) - 439, 440):
    block, off = data[i:i + 440], 0
    while off < 440:
        n = block[off]
        if n == 0 or off + 1 + n > 440:
            break
        pcm += dec.decode(bytes(block[off + 1:off + 1 + n]), 160)
        off += 1 + n
# pcm is 16 kHz mono signed 16-bit little-endian
```

Note `scripts/devkit/decode_audio.py` is stale for this firmware too, not just for the CV1: it
assumes a fixed 83-byte stride (a 3-byte prefix plus an 80-byte padded frame), which is the
*older* DevKit layout still visible commented out at `devkit/src/transport.c:619-635`. The
live code writes 440-byte packed blocks. Do not start from that script.

## 7.3 There is no clock. At all.

The DevKit firmware has **no RTC, no time-sync GATT service, and no per-packet timestamps**.
Nothing in `devkit/src/` references wall-clock time. Zephyr's FatFs also has no RTC hooked up
(`CONFIG_FS_FATFS_HAS_RTC` is unset), so even the file's modification time is a fixed
placeholder date rather than when the audio was recorded.

Combined with the fact that the file is opened `FS_O_APPEND` and never truncated or rotated,
this means:

> `a01.txt` is one unbroken stream of audio spanning every session since the card was last
> cleared, with no timestamps, no session boundaries, and no gaps marking power-offs.

For Diariz this is the single biggest thing to solve, and it has to be solved **host-side**:

- **Anchor at collection time.** Note when you pull the card, decode the file, measure its
  duration, and work backwards. Accurate to whenever the device was actually running.
- **Split on silence.** Because there is no VAD on the device, silence is recorded in full at
  roughly the same bitrate as speech. Run silence detection over the decoded PCM (ffmpeg
  `silencedetect`, or an RMS threshold) and cut sessions at long quiet stretches. Feed each
  segment to Diariz as its own recording with an estimated `startedAt`.
- **Consider clearing the card each time you sync**, so each card cycle maps to one known
  window. Simplest reliable approach until the firmware carries a clock.

Adding a timestamp is a small firmware change (a time-sync characteristic plus a 4-byte header
per block, copying the CV1 design) and is worth doing early - see 7.6.

## 7.4 The 128 GB card

**exFAT is supported.** `prj_xiao_ble_sense_devkitv2-adafruit.conf` sets
`CONFIG_FS_FATFS_EXFAT=y`, so a 128 GB SDXC card mounts in its factory exFAT format without
reformatting. (Note FatFs exFAT support is covered by Microsoft patents - fine for personal
use, worth knowing if this ever ships.)

**Capacity is no longer the constraint.** Without silence gating the device records
continuously at roughly 4.1 kB/s, about **15 MB/hour** or **355 MB/day**. A 128 GB card
therefore holds on the order of **a year** of continuous audio. You will hit the battery, the
file-size limits below, and your own patience long before you fill it.

**Three limits will bite before the card does:**

1. **Battery.** No VAD means the mic, codec and SD writes run 24/7. On a XIAO-class LiPo this
   is hours, not days. This is now the binding constraint on session length.
2. **4 GB, if you ever sync over BLE.** The BLE sync path is `uint32` throughout
   (`file_num_array`, `remaining_length`) and `read_audio_data` takes a **signed int** offset,
   so it breaks somewhere around 2 GB and certainly at 4 GB. At 15 MB/hour that is about
   **133 hours** of recording. Irrelevant if you always read the card directly - which you
   should.
3. **A single file.** Everything lands in `a01.txt`. `file_count` is computed at mount and then
   immediately overwritten with a hard-coded `1` (`devkit/src/sdcard.c:101`), and
   `generate_new_audio_header()` caps at `a99.txt` anyway. There is no rotation.

**Data-loss hazard: the firmware could reformat your card. FIXED - rebuild required.**
As shipped upstream, `CONFIG_FS_FATFS_MOUNT_MKFS=y` was set and `mount_point.flags` was left
at 0, so `FS_MOUNT_FLAG_NO_FORMAT` was *not* applied. If `fs_mount` failed because FatFs did
not recognise the filesystem, Zephyr ran `f_mkfs()` and **formatted the card**. A card that
Windows wrote in a layout FatFs dislikes, or one with a partition scheme it cannot parse, was
wiped on the next boot - silently, because this build has `CONFIG_CONSOLE=n`.

Our tree now sets `FS_MOUNT_FLAG_NO_FORMAT` in `mount_sd_card()` and
`CONFIG_FS_FATFS_MOUNT_MKFS=n` in the DevKit configs. The PC owns formatting and deleting;
the device only reads and appends. An unreadable card now fails the mount and `main.c` blinks
red six times instead of wiping it.

Two things follow:

- **The fix only exists in firmware you build and flash yourself.** A device still running a
  stock upstream image has the old behaviour, so keep copying `a01.txt` off the card before
  reinserting it until you have flashed your own build.
- **A blank card no longer self-provisions.** Format it on the PC as exFAT or FAT32 first -
  see [08-build-and-flash-runbook.md](08-build-and-flash-runbook.md) section 8.4.

## 7.5 DevKit-specific findings

Numbered `D*` to keep them distinct from the CV1 findings in
[05-findings.md](05-findings.md).

### D1 - Use-after-free in `initialize_audio_file()`. **Medium**

`devkit/src/sdcard.c`:

```c
char *header = generate_new_audio_header(num);
if (header == NULL) return -1;
k_free(header);
create_file(header);      /* <-- header was just freed */
```

The pointer is freed and then dereferenced. It happens to work today because `k_free` does not
scrub the block and nothing reallocates in between, but it is undefined behaviour and it is on
the boot path. Fix by moving `k_free` after `create_file`.

### D2 - `file_count` is computed and then discarded. **Low**

`sdcard.c:100-101` calls `get_file_contents()` to count files, then unconditionally assigns
`file_count = 1;`. The directory scan is dead work, and any pre-existing `a02.txt`+ are
invisible to the firmware. `initialize_audio_file(1)` is also called twice on the mount path.

### D3 - The storage size gate compares the wrong variable. **Low**

`transport.c:731` gates offline writes on `file_num_array[1] < MAX_STORAGE_BYTES`
(`0xFFFF0000`, about 4.29 GB). But `update_file_size()` puts the **file size** in
`file_num_array[0]` and the **sync read offset** in `file_num_array[1]`. So the gate tests the
read offset, never the size, and never fires. In practice there is no size cap at all.

### D4 - `file_num_array` is 2 entries but indexed by file number. **Low**

`sdcard.c:34` declares `uint32_t file_num_array[2]`, and `storage.c:145` and `:190` index it as
`file_num_array[current_read_num - 1]` / `[file_num - 1]` where the index comes from the BLE
command byte. Any file number above 2 reads out of bounds. Unreachable today only because
`file_count` is pinned to 1 (D2), which rejects `file_num > file_count` first.

### D5 - The file is opened and closed on every 440-byte write. **Medium**

`write_to_file()` does `fs_open(FS_O_WRITE|FS_O_APPEND)` / `fs_write` / `fs_close` per block,
i.e. roughly **ten times a second**, and checks none of the three return values. That is a FAT
metadata update per block: unnecessary wear, unnecessary power, and a much larger window for
power-loss corruption than keeping the handle open with a periodic `fs_sync` would give.

On the other hand it does mean the file length on disk is always current, so an unexpected
power cut loses at most one block.

### D6 - Same "connected suppresses recording" hazard, different mechanism. **High**

The CV1 loses audio when a central is connected but not subscribed
([05-findings.md](05-findings.md) F2). The DevKit gets there by a different route:
`transport.c:403` sets `storage_is_on = true` on **connect**, and the pusher only writes to
storage `if (!valid && !storage_is_on)`. So while any BLE connection exists, offline recording
is off - and if that connection has not subscribed to the audio characteristic, nothing is
streamed either. Audio is dropped.

Same operational rule applies: keep centrals away from the device while it is recording, or
patch the condition.

### D7 - Frame length is stored in one byte but can exceed 255. **Low, latent**

`CODEC_OUTPUT_MAX_BYTES` is `160 * 2 = 320` on the DevKit, while the storage framing writes the
length into a single byte (`storage_temp_data[buffer_offset] = tx_buffer_size`). A frame above
255 bytes would be silently truncated and would desynchronise the decoder for the rest of the
block. At 32 kbps with 10 ms frames the typical size is about 40 bytes, so this needs a
pathological VBR excursion to trigger - but it is not structurally prevented.

### D8 - No serial console by default. **Low, but it will cost you time**

The v2 overlay sets `&uart0 { status = "disabled"; }`, and `usb.c` only registers a
charge-detect callback - there is no CDC-ACM console. Debugging means re-enabling UART (see the
DevKit 2 section of `firmware/readme.md` for the exact `CONFIG_*` lines) and, per that same
note, disabling `CONFIG_OMI_ENABLE_OFFLINE_STORAGE` or raising the log thread priority, because
logging and SD writes interfere.

### D9 - `CONFIG_I2C=n` with `CONFIG_LSM6DSL=y`. **Low**

Same conflict as the CV1 ([05-findings.md](05-findings.md) F7): `prj_...devkitv2-adafruit.conf`
disables I2C while enabling the IMU driver, and the overlay puts the LSM6DS3TR-C on `i2c0`.
The IMU is unused by this application, so unlike the CV1 (where it backs the clock recovery)
nothing depends on it.

### D10 - Overlay comment contradicts the code. **Cosmetic**

`cs-gpios = <&gpio0 2 GPIO_ACTIVE_LOW>;  // CS pin on P0.28` - the code says P0.02, the comment
says P0.28. Worth resolving against your actual wiring before debugging any SD problem.

## 7.6 Revised plan for getting audio into Diariz

The DevKit changes the plan substantially, and mostly for the better. **Option A from
[06-repurposing-for-diariz.md](06-repurposing-for-diariz.md) - the BLE sync client - is no
longer needed.** Neither is F1 (no clock gate on writes), F4 (no destructive sync), or the ring
protocol.

**Phase 1 - card-swap workflow, no firmware change at all. BUILT: see
[`omi/tools/omi-sync`](../../tools/omi-sync/README.md).**

1. Record. Eject the card. Note the wall-clock time you ejected it.
2. Copy `a01.txt` off the card **before** reinserting it (protects against the auto-format
   hazard in 7.4).
3. Parse the blocks into Opus frames (7.2), including the F13 boundary guard.
4. Split into sessions on long silences, and derive each `startedAt` by working backwards
   from the ejection time.
5. Remux each session into **Ogg Opus** - the original frames rewrapped, no decode and no
   re-encode, about 15 MB/hour - and `POST /api/recordings` with `source=Upload`,
   `startedAt`, `endedAt` and a title. Diariz transcribes, diarizes and summarises from there.
6. Delete `a01.txt` from the card so the next cycle starts clean.

`omi-sync` implements steps 3-5:

```bash
cd omi/tools/omi-sync
python -m omi_sync /path/to/a01.txt --scan-only              # look first
python -m omi_sync /path/to/a01.txt --dry-run --out ./out    # decode locally
python -m omi_sync /path/to/a01.txt --url https://diariz.example.com --token dz_api_...
```

Its only runtime dependency is `requests`, and only for the upload - the framing, silence
detection and Ogg muxing are pure Python. Notably, **silence detection needs no audio
decoding**: at 32 kbps VBR an Opus frame's byte length tracks how much signal is in it, so
the threshold is picked with Otsu's method over the frame-length histogram. That is what
keeps the tool free of any native Opus dependency while still emitting a lossless remux.

**Phase 2 - the firmware changes worth making, in order.**

- **D-fix-1: add a clock.** Port the CV1's time-sync service (`19B10030`/`19B10031`) and its
  4-byte big-endian per-block timestamp. This removes the guesswork from step 4 above and is
  the single highest-value change. Copy `omi/src/rtc.c` and the header write in
  `process_write_data_req`; the format is documented in
  [02-audio-formats.md](02-audio-formats.md) section 2.5.
- **D-fix-2: `FS_MOUNT_FLAG_NO_FORMAT`. DONE.** Stops the firmware wiping a card it cannot
  read; `main.c` now blinks red six times instead. Needs a rebuild and reflash to take
  effect - see [08-build-and-flash-runbook.md](08-build-and-flash-runbook.md).
- **D-fix-3: rotate files.** Close `a01.txt` and start `a02.txt` on a size or time boundary.
  Gives natural session boundaries and keeps every file under the 2 GB signed-offset limit.
  Needs D2 and D4 fixed first so the file table is real.
- **D-fix-4: fix D6** so a stray BLE connection cannot silently stop recording.
- **D-fix-5: add silence gating.** The nRF52840 has no T5838 AAD to lean on, but a software
  amplitude gate in the mic callback (the CV1's `CONFIG_OMI_VAD_*` knobs are the model) would
  cut both battery draw and the amount of dead air the host has to split out. Biggest win for
  battery life.
- **D-fix-6: fix D1 and D5** - the use-after-free, and keeping the file handle open with a
  periodic `fs_sync` instead of open/close per block.

**Not applicable to this device:** the Wi-Fi upload path (Option C) - there is no nRF7002 on a
XIAO nRF52840. If direct upload ever matters more than the card-swap workflow, that is an
argument for building the consumer board, not for modifying this one.

## 7.7 Flashing

The DevKit 2 uses the **Adafruit UF2 bootloader**, not MCUboot. Double-tap reset to mount the
`XIAO-SENSE` drive, then copy `build/zephyr/zephyr.uf2` onto it - which is exactly what
`devkit/flash.sh` does:

```bash
west build && cp build/zephyr/zephyr.uf2 /Volumes/XIAO-SENSE/
```

`bootloader/bootloader0.9.0.uf2` and the files under `bootloader/deprecated/` are the XIAO
bootloader images, kept for recovery. None of the MCUboot signing, `dfu_application.zip`, or
J-Link material in `BUILD_AND_OTA_FLASH.md` applies to your device - that is all CV1. The
`FLASH_3.0.8/` directory of prebuilt CV1 images and J-Link scripts has been deleted for the same
reason.
