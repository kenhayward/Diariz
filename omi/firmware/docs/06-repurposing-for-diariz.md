# 6. Repurposing the device for offline ambient capture into Diariz

The goal: wear the device, let it record ambiently with no phone involved, then later pull the
audio off and get it into Diariz as a recording that goes through the normal
transcribe -> diarize -> summarise pipeline.

## 6.1 The good news

**Offline recording already is the default behaviour.** No firmware change is needed to make
the device record when it is not connected - `pusher()` writes to the SD ring whenever there
is no BLE connection. The 512 MB SD NAND holds about 33 hours of recorded audio
([04-offline-storage.md](04-offline-storage.md)), and hardware AAD means silence costs
nothing, so a normal working day is comfortably within budget.

**The audio is already in a format Diariz accepts, almost.** Diariz's upload endpoint sniffs
magic bytes and accepts `wav`, `mp3`, `flac`, `ogg`, `webm` unconditionally
(`src/Diariz.Api/Services/AudioFormats.cs`). The device stores **raw Opus frames**, which can
be remuxed into an **Ogg Opus** file with no re-encoding at all - so the path from device to
Diariz can be lossless and cheap.

## 6.2 The four things that stand in the way

| # | Problem | Where |
|---|---|---|
| 1 | The device records nothing until its clock is set once over BLE | [05-findings.md](05-findings.md) F1 |
| 2 | Any BLE connection that does not subscribe silently stops recording | F2 |
| 3 | There is no working client in this tree for the consumer ring protocol | F9 |
| 4 | Anyone in range can connect, listen live, and drain your recordings | F5 |

None is fatal. (1) is a one-time step per firmware wipe. (2) is a two-line firmware change or
an operational rule. (3) is the actual work. (4) is a firmware change you should want anyway.

## 6.3 Option A - no firmware change: write a sync client (recommended starting point)

Build a small host-side tool (Python + `bleak` is the obvious choice; the dependencies are
already listed in `scripts/devkit/requirements.txt`, plus `opuslib`) that:

1. Scans for the peripheral advertising as `Omi` / audio service `19B10000-...`.
2. Connects, requests 2M PHY and the largest MTU the OS will give.
3. **Writes the current UTC epoch** to `19B10031-...` as a 4-byte native-little-endian
   `uint32`. Do this every sync - it is cheap and it repairs the clock drift described in F3.
4. Subscribes to `30295781-...` notifications.
5. Writes `0x10` (`CMD_RING_INFO`), reads back `read_seq` / `write_seq` / `capacity` /
   `dropped_packets`.
6. Writes `0x11` + big-endian `uint64(read_seq)` and streams `NOTIFY_DATA` payloads
   **straight to a file on disk, flushed continuously** - the device frees its copy every 2 s
   whether or not you kept it (F4).
7. On `NOTIFY_DONE`, closes out.
8. Decodes the 444-byte ring packets per
   [02-audio-formats.md](02-audio-formats.md) section 2.6, **remembering the block-boundary
   guard** (F13).
9. **Splits into sessions on timestamp gaps.** Because AAD sleeps through silence and the
   device may be powered off, the packet timestamps are not continuous. Start a new session
   whenever the UTC timestamp jumps by more than, say, 60 s - or goes backwards, which means a
   reset happened (F3).
10. For each session, emit an audio file and `POST /api/recordings` (multipart) with
    `source=Upload`, `startedAt` = the session's first packet timestamp, `endedAt` = its last,
    `durationMs` = the decoded length, and a `title` derived from the timestamp. Diariz will
    enqueue transcription and, if the user has a summarisation endpoint configured, name and
    summarise it automatically.

**Which container to emit.** Two sensible choices:

- **Ogg Opus (recommended).** Remux the raw 20 ms Opus frames into an Ogg stream with an
  `OpusHead` (channel count 1, input sample rate 16000, pre-skip 0 since
  `RESTRICTED_LOWDELAY`/CELT has no encoder delay to declare beyond the standard) and
  `OpusTags` header, one granule position step of 960 per frame (Ogg Opus granule positions
  are always in 48 kHz units). No decoding, no quality loss, and about **15 MB per hour**. This
  keeps a whole card comfortably under Diariz's 500 MB upload cap when split into sessions.
- **WAV.** Decode with libopus and write 16 kHz mono 16-bit PCM. Trivially correct, but about
  **115 MB per hour** - a long session will exceed `Uploads:MaxBytes` (500 MB) after roughly
  4.3 hours, so splitting becomes mandatory rather than merely tidy.

Either way the worker measures the true duration itself and backfills it, so a slightly wrong
`durationMs` is not fatal.

**Sync time.** Audio accrues at about 4.1 kB/s. Whatever your link achieves, the drain ratio
is (link rate / 4.1 kB/s); a link doing tens of kB/s clears an hour of recording in a few
minutes and a completely full card in a few hours. Build the client to be resumable and to run
unattended - the firmware already checkpoints, so an interrupted sync picks up where it left
off.

**Where to run it.** A Raspberry Pi or a mini-PC on a desk, running the sync on a schedule
whenever the device is in range, is a better fit than a laptop: syncs are long, and the device
is not recording while connected (F2).

## 6.4 Option B - small firmware changes worth making

Each of these is a contained edit to the vendored tree. If you make them, keep this `docs/`
folder updated and note that you will need your own MCUboot signing key (F10) if you want OTA.

**B1. Never lose audio to a connected-but-idle central.** In `pusher()`
(`omi/src/lib/core/transport.c`), change the final `else` so that when a connection exists but
is not subscribed, audio falls through to `write_to_storage()` instead of being dropped. This
removes the single biggest silent-data-loss path for ambient use.

**B2. Record even before the first time sync.** In `process_write_data_req()`
(`omi/src/sd_card.c`), instead of dropping packets when `!rtc_is_valid()`, stamp them with a
sentinel (for example `0`, or a monotonic uptime-based value with the high bit set) and let
the host-side tool re-anchor them once it learns the real time. Losing exact wall-clock on the
first session is far better than losing the session.

**B3. Persist the clock periodically.** Have the SD flush path (or the 1 Hz main loop) call
`app_settings_save_rtc_epoch()` every few minutes. This bounds the backwards jump after an
ungraceful reset (F3) to minutes rather than days, at the cost of a little NVS wear.

**B4. Require an encrypted, bonded link.** Change the storage and audio characteristic
permissions from `BT_GATT_PERM_READ`/`_WRITE` to the `_ENCRYPT` variants, register a
`bt_conn_auth_cb`, and set `CONFIG_BT_SETTINGS=y` so bonds survive a reboot. Without this, a
device worn in meetings is an open microphone to anyone in range (F5). This is the change to
make first if the device leaves your desk.

**B5. Rename the device.** `CONFIG_BT_DEVICE_NAME="Omi"` makes it discoverable by, and
connectable from, the stock Omi app. Changing the name (and ideally the service UUIDs) stops
that app from grabbing the connection and, per F2, stopping your recording.

## 6.5 Option C - Wi-Fi upload direct to Diariz (the interesting one)

The consumer board **has an nRF7002 Wi-Fi companion chip populated** (BOM line 20,
`nRF7002-CEAA-R7`, U2), wired on QSPI and described in the board devicetree. The `test/` shell
application already enables it (`CONFIG_WIFI=y`, `CONFIG_WIFI_NRF70=y`, with `wifi scan` /
`wifi connect` / `wifi status` shell commands), so the hardware and driver path are known to
work at least to that level. `omi/README.md`'s own TODO list marks Wi-Fi as "partially" done
and "SD Card -> Transfer via Wi-Fi" as the one unticked storage item.

That means a firmware that, on charge or on a schedule, joins a known SSID and `POST`s the
ring contents straight to `POST /api/recordings` is **architecturally possible without any
hardware change** - and it would remove the BLE sync client, the host machine, and the
proximity requirement in one go.

Realistically, though:

- **RAM is the constraint, not flash.** The build currently reports 244,556 of 440 KB RAM used
  (54%) with about 720 KB of flash free. The nRF70 driver and a TLS stack are both
  RAM-hungry. Expect to have to claw back memory (the 19 KB codec stack, the 16 KB SD batch
  buffers, the BLE buffer counts) or to run Wi-Fi and BLE non-concurrently.
- **TLS to a Diariz server** means mbedTLS plus a trust store on a device that has no secure
  element. Plain HTTP to a LAN-local Diariz, or an on-LAN relay, is a far smaller problem.
- **Multipart upload of a multi-hundred-MB body** from a device with 440 KB of RAM needs
  chunked streaming straight off the ring. Diariz's endpoint takes a single multipart file, so
  the device would have to emit one session at a time and would need to do the session
  splitting itself - which in turn needs a reliable clock (B3).

Treat this as a genuine second phase, not a shortcut. Option A gets audio into Diariz this
week; Option C is a project.

## 6.6 If your hardware is a DevKit rather than the consumer device

Check what you actually built. If it is a **Seeed XIAO nRF52840 Sense** based DevKit1/DevKit2
rather than the nRF5340 consumer board, everything changes for the better:

- Storage is a **FAT filesystem on a removable microSD**, at `/SD:/audio/aNN.txt`.
- You can eject the card, read it on a PC, and skip BLE entirely.
- The frame layout is the DevKit's 3-byte prefix + 80-byte padded Opus entry, and
  `scripts/devkit/decode_audio.py` is a (crude but working) starting point for decoding it.

In that case Option A collapses to "copy files off the card, decode, upload", and none of F1,
F2, F4 or the ring protocol applies. Confirm which board you have before writing any client:
the consumer app advertises as `Omi` and reports DIS model `Omi CV 1`; the DevKit build
advertises as `Omi DevKit 2`.

## 6.7 Suggested order of work

1. Confirm which hardware you built (`Omi` vs `Omi DevKit 2` over BLE, or by the SoC).
2. Consumer device: **B5 + B4** first if it will be worn outside your home, so you are not
   running an open microphone.
3. Write the Option A sync client. Prove the decode against a short known recording before
   trusting a long one - the F13 block-boundary guard is the thing most likely to be wrong.
4. Add **B1** and **B3** to the firmware once you have a build environment working; they are
   small and they remove the two silent-data-loss paths.
5. Wire the client's output into `POST /api/recordings` with proper `startedAt`/`endedAt` so
   Diariz timelines line up with reality.
6. Only then consider Option C.
