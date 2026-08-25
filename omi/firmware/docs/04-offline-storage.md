# 4. Offline storage: behaviour, capacity, power

Read [02-audio-formats.md](02-audio-formats.md) section 2.5 for the byte layouts. This
document covers *when* the device records, *how much* it holds, and *how* it manages power.

## 4.1 When does it record to the card?

The decision is made once per encoded Opus frame in `pusher()` (`transport.c`):

| Situation | What happens to the audio |
|---|---|
| No BLE connection at all | **Written to the SD ring** (if `is_sd_on()`) |
| Connected **and** subscribed to `19B10001` | Streamed live over BLE, **not** stored |
| Connected but **not** subscribed | **Discarded.** Not streamed, not stored |
| No BLE connection and SD unavailable | Discarded, with a rate-limited warning |
| RTC never synced | Discarded inside `process_write_data_req` |

So offline recording is the *default* behaviour and needs no command - but it is suppressed by
the mere presence of a connection. Anything that holds a BLE link open to the device without
subscribing (a stray phone, a scanner app, an OS that auto-reconnects to a known peripheral)
silently stops the device recording. See [05-findings.md](05-findings.md), finding F2.

## 4.2 The ring, not a log

`sd_card.c` implements a **circular** buffer over the raw block device. When the write pointer
laps the read pointer, the oldest audio is overwritten and `dropped_packets` is incremented.
There is no "stop when full"; ambient capture will always be the last N hours, never the first
N hours.

`read_seq` only advances when a BLE client syncs (or when overwrite forces it). If you never
sync, the ring simply cycles.

## 4.3 Capacity and duration

For a card of `S` 512-byte sectors:

```
data_batch_count = (S - 64) / 32
capacity_packets = data_batch_count * 36
duration         = capacity_packets * ~108 ms
```

For the fitted **CSNP4GCR01-DPW** (4 Gbit / about 512 MB):

| Quantity | Value |
|---|---|
| Metadata reserve | 64 sectors = 32 KB |
| Batch efficiency | 15,984 / 16,384 = 97.6% |
| Sustained write rate | about 15.1 MB per hour of recorded audio |
| **Total recorded audio held** | **about 33 hours** |

`MAX_STORAGE_BYTES` (0x1E000000 = 480 MiB) is still declared in `sd_card.h` but is no longer
referenced anywhere; the ring sizes itself from the card's actual sector count.

## 4.4 Crash and power-loss behaviour

The design is genuinely careful here:

- Metadata is a **64-slot rotating journal**; the newest valid record by `generation` wins, so
  a torn metadata write costs at most one record.
- Every batch carries its own header with `magic`, `version`, `packet_count` and `start_seq`,
  validated on read. A batch whose `start_seq` does not match its slot is detected as a lapped
  (overwritten) window and `read_seq` is force-advanced.
- On mount, `restore_tail_batch()` reloads the partially filled batch and truncates `write_seq`
  to the packet count the header actually claims.
- Batches are flushed when full **or** every 1 s of inactivity (`RAW_FLUSH_INTERVAL_MS`), and
  on BLE connect, so the worst-case loss on a hard power cut is about one batch, roughly
  **4 seconds** of audio.

## 4.5 Power management

Three cooperating mechanisms:

**Hardware AAD (T5838).** `mic.c` tracks the average absolute amplitude of every 100 ms frame.
After `CONFIG_OMI_VAD_HOLD_MS` (10 s in the shipped config) below
`CONFIG_OMI_VAD_ABS_THRESHOLD` (250), the PDM peripheral is stopped and the mic is bit-banged
into its own Acoustic Activity Detection sleep (the header quotes roughly 15-20 uA). Its
`WAKE` pin (P1.02) raises an interrupt on sound, and the driver restarts PDM after an 800 ms
settle. AAD sleep is **deferred while a BLE sync transfer is active** so a sync is never
stalled.

**SD power gating.** Entering AAD sleep calls `sd_request_power(false)`, which flushes,
unmounts and drops the SD NAND's enable pin (P1.10). Waking calls `sd_request_power(true)`.
A BLE connect also forces the card on so it is mounted before the app issues its sync command,
with `STORAGE_SD_READY_TIMEOUT_MS` = 5 s of grace before the firmware replies "not ready".

**Idle polling.** The storage thread polls every 2 s when disconnected and every 1 ms when
connected.

Consequence for ambient use: the device only burns the SD write path and full PDM capture when
there is sound above the threshold. It also means **silence produces no packets at all**, so
gaps in the timestamp sequence are normal and expected, and a reconstructed recording is a
concatenation of voiced segments, not a continuous timeline. Any downstream tool has to use
the per-packet UTC timestamps to lay segments out in real time.

## 4.6 Legacy file API is a facade

`sd_card.h` still exposes a file-oriented API - `create_new_audio_file`, `get_audio_file_list`,
`delete_audio_file`, `save_offset`, `get_offset`, `clear_audio_directory`,
`get_current_filename`, `read_audio_data`. In `sd_card.c` these are **compatibility shims over
the ring**:

- `create_new_audio_file()` just flushes the current batch.
- `delete_audio_file(name)` ignores the name and clears the **entire** ring.
- `clear_audio_directory()` likewise clears the entire ring.
- `get_current_filename()` returns a synthesised `%08X.txt` name built from the last packet's
  timestamp; no such file exists.
- `save_offset` / `get_offset` write to two static variables and are never read back.

Do not build anything on these. They exist so older callers still link.

## 4.7 The DevKit is the opposite

For completeness: `devkit/` mounts a **FAT** filesystem (`FS_FATFS`) on a removable microSD at
`/SD:`, writes `/SD:/audio/aNN.txt` (up to 100 files), and serves them over the older
file-based BLE command set. On that hardware the offline audio can be retrieved by simply
taking the card out and reading it on a PC - which is a materially easier route to Diariz than
the consumer device's raw ring plus BLE-only protocol.
