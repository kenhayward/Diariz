# 2. The audio pipeline and its byte formats

This is the reference document for anyone writing a decoder. Every layout below was read
directly out of the source; the file and symbol are cited so it can be re-checked.

## 2.1 The chain

```
2x T5838 PDM mics
   |  nrfx PDM, 16 kHz, 16-bit, 2 channels               mic.c
   v
100 ms interleaved stereo block (1600 frames)
   |  interleaved_stereo_to_mono(): (L+R)>>1, clamped    mic.c
   v
1600 mono int16 samples  --->  mic_handler()             main.c
   |  codec_receive_pcm() -> ring buffer (1 s deep)      codec.c
   v
Opus encoder, 20 ms frames (320 samples)                 codec.c
   |  codec_handler()                                    main.c
   v
broadcast_audio_packets() -> tx ring (32 frames)         transport.c
   |
   +-- BLE connected AND subscribed --> push_to_gatt()        see 2.4
   +-- no BLE connection            --> write_to_storage()    see 2.5
   +-- connected but NOT subscribed --> DISCARDED             see 05-findings.md
```

## 2.2 Opus encoder settings (`omi/src/lib/core/codec.c`, `config.h`)

| Parameter | Value |
|---|---|
| Sample rate | 16000 Hz |
| Channels | 1 (mono, mixed from the two mics) |
| Application | `OPUS_APPLICATION_RESTRICTED_LOWDELAY` (CELT only, no SILK, no lookahead) |
| Frame size | `CODEC_PACKAGE_SAMPLES` = 320 samples = **20 ms** |
| Bitrate | 32000 bps, **VBR on**, unconstrained |
| Complexity | 3 |
| Signal hint | `OPUS_SIGNAL_VOICE` |
| DTX | off |
| In-band FEC | off |
| Max encoded frame | `CODEC_OUTPUT_MAX_BYTES` = 160 bytes |
| Codec ID advertised over BLE | **21** (`0x15`) |

A typical encoded frame at 32 kbps / 20 ms is **about 80 bytes**; the 160-byte ceiling is the
hard limit, not the norm. A decoder must be told the frame is 320 samples at 16 kHz:
`opus_decode(dec, frame, len, pcm, 320, 0)`.

## 2.3 Ring-buffer framing inside the firmware (not visible externally)

`transport.c` stores each encoded frame in `tx_queue` as a fixed 162-byte slot: a 2-byte
little-endian length followed by up to 160 bytes of Opus. This is internal only; it never
leaves the device in this form. The queue is 32 slots deep, roughly 640 ms of audio.

## 2.4 Live BLE stream format (`push_to_gatt`, audio characteristic `19B10001-...`)

One Opus frame is split across one or more GATT notifications, each of at most
`current_mtu - 3` bytes. Every notification is:

```
byte 0   packet index, low byte      \  16-bit counter, increments per NOTIFICATION
byte 1   packet index, high byte     /  (not per Opus frame), wraps at 65536
byte 2   fragment index within the current Opus frame, starting at 0
byte 3.. Opus payload fragment
```

To reassemble: concatenate payloads while the fragment index increases; a fragment index of 0
starts a new Opus frame. A gap in the 16-bit packet index means notifications were lost.

At a negotiated MTU of 498 an 80-byte frame fits in one notification, so in practice the
fragment index is almost always 0 and each notification carries one whole Opus frame.

## 2.5 Offline storage format

Two nested layers. Layer A is what `transport.c` hands to the SD module; layer B is what the
SD module commits, and is also exactly what a client receives during a BLE sync.

### Layer A: the 440-byte payload block (`write_to_storage`, `transport.c`)

Opus frames are packed back to back into a `MAX_WRITE_SIZE` = **440**-byte block:

```
[len0][opus frame 0 (len0 bytes)][len1][opus frame 1]...[lenN][opus frame N][padding]
 1 B                              1 B                    1 B
```

`len` is a single byte, which is safe because frames are at most 160 bytes. When the next
frame will not fit, the block is flushed and that frame starts the following block.

**Parsing gotcha.** In the overflow branch the code writes the *next* frame's length byte at
the current offset and *then* flushes the block. So a full block can end with a length byte
that has no data behind it, describing a frame that actually lives at the start of the next
block. **A decoder must treat any length byte whose payload would run past offset 439 as
padding and stop parsing that block**, otherwise it emits a corrupt frame at every block
boundary. Bytes after the last complete frame are stale data from the previous block, not
zeros.

At about 81 bytes per framed 20 ms frame, one 440-byte block holds roughly 5.4 frames, which
is **about 108 ms** of audio.

### Layer B: the 444-byte ring packet (`sd_card.c: process_write_data_req`)

Each 440-byte block is committed with a 4-byte header:

```
bytes 0-3    uint32 BIG-ENDIAN UTC epoch SECONDS   (sys_put_be32, RAW_AUDIO_TIMESTAMP_BYTES)
bytes 4-443  the 440-byte layer-A block
```

`RAW_AUDIO_PACKET_BYTES` = 444. The timestamp is taken at commit time via `get_utc_time()`.
Resolution is 1 second while packets arrive every ~108 ms, so several consecutive packets
share a timestamp. It is a coarse wall-clock anchor, not a per-frame presentation timestamp.
Use it to date the recording and to detect gaps (AAD sleep, power-off, sync pauses); use frame
counting for timing within a packet.

**If the RTC is not valid, or the epoch is 0 or before 1700000000 (2023-11-14), the packet is
dropped silently.** See [05-findings.md](05-findings.md), finding F1.

### On-disk container

Packets are grouped into **batches** of 32 sectors (16384 bytes) with a 32-byte header:

```c
struct raw_batch_header {          /* little-endian, native ARM layout */
    uint32_t magic;                /* 0x4F4D4942 = "OMIB" */
    uint16_t version;              /* 1 */
    uint16_t packet_count;         /* <= 36 */
    uint64_t generation;
    uint64_t start_seq;            /* always a multiple of 36 */
    uint32_t reserved0, reserved1;
};
/* followed by packet_count * 444 bytes of ring packets */
```

`RAW_PACKETS_PER_BATCH` = (16384 - 32) / 444 = **36**, using 15,984 of 16,384 bytes, so 2.4%
of the card is padding.

Sectors 0..63 (`RAW_META_SECTORS`) are a 64-slot rotating metadata journal, one record per
sector, newest wins by `generation`:

```c
struct raw_meta_record {
    uint32_t magic;                /* 0x4F4D4952 = "OMIR" */
    uint16_t version;              /* 1 */
    uint16_t reserved0;
    uint64_t generation;
    uint64_t read_seq;             /* oldest packet not yet synced */
    uint64_t write_seq;            /* next packet to be written */
    uint64_t dropped_packets;
    uint32_t reserved1;
};
```

Data batches start at sector 64. The mapping from a packet sequence number to its location:

```
batch_index      = seq / 36
slot             = batch_index % data_batch_count
sector           = 64 + slot * 32
data_batch_count = (total_sectors - 64) / 32
capacity_packets = data_batch_count * 36
```

**There is no filesystem on the consumer device.** The SD NAND is written with raw
`disk_access_write` calls. Desoldering the chip and mounting it on a PC gets you nothing; a
reader has to implement the layout above against the raw block device. `omi.conf` still sets
`CONFIG_FILE_SYSTEM_LITTLEFS=y` and `CONFIG_FILE_SYSTEM=y`, but nothing in `sd_card.c` uses
either - see [05-findings.md](05-findings.md), finding F8.

## 2.6 What a BLE sync actually delivers

The `NOTIFY_DATA` stream (see [03-ble-protocol.md](03-ble-protocol.md)) is the **raw ring byte
stream** starting at the requested sequence number. Concatenate every `NOTIFY_DATA` payload,
dropping the leading 1-byte `0x03` marker from each, and the result is an unbroken run of
444-byte layer-B packets. Batch headers are not included; `sd_ring_read` copies packets only.

Full decode recipe:

```python
# stream = concatenation of all NOTIFY_DATA payloads with the 0x03 marker byte stripped
import opuslib
dec = opuslib.Decoder(16000, 1)
pcm = bytearray()
for i in range(0, len(stream) - 443, 444):
    pkt   = stream[i:i + 444]
    ts    = int.from_bytes(pkt[0:4], "big")     # UTC epoch seconds
    block = pkt[4:444]
    off = 0
    while off < 440:
        n = block[off]
        if n == 0 or off + 1 + n > 440:         # padding or trailing length byte
            break
        pcm += dec.decode(bytes(block[off + 1:off + 1 + n]), 320)
        off += 1 + n
# pcm is 16 kHz mono signed 16-bit little-endian; wrap in a WAV header to play it
```

## 2.7 The DevKit format is completely different

`devkit/src/storage.c` and `devkit/src/sdcard.c` use a **FAT filesystem** on a removable
microSD, with files at `/SD:/audio/aNN.txt` and a file-oriented BLE command set
(`READ_COMMAND 0`, `DELETE_COMMAND 1`, `NUKE 2`, `STOP_COMMAND 3`). Its on-file frame layout
is a 3-byte prefix plus an 80-byte padded Opus entry (`FRAME_PREFIX_LENGTH 3`,
`OPUS_ENTRY_LENGTH 80`), which is what the stale `scripts/devkit/decode_audio.py` parses with
its hard-coded 83-byte stride.

**None of that applies to the consumer device.** If your hardware is a DevKit, the microSD is
directly readable on a PC and the retrieval problem is far easier. If it is the consumer CV1,
it is not.

## 2.8 Data rates and volumes (consumer device)

| Quantity | Value |
|---|---|
| PCM before encoding | 32,000 B/s |
| Opus payload | about 4,000 B/s (32 kbps) |
| Framed (one length byte per 20 ms frame) | about 4,050 B/s |
| On the ring, including 4-byte timestamps | about 4,090 B/s = **about 14.7 MB/hour** |
| Including 2.4% batch padding | about 4,190 B/s = **about 15.1 MB/hour** |
| 512 MB SD NAND, full | **about 33 hours of recorded audio** |

That 33 hours is 33 hours of audio *actually written*. With hardware AAD gating silence
(default: sleep after 10 s below the amplitude threshold) the elapsed wall-clock coverage is
considerably longer, and depends entirely on how much sound is around the wearer.
