# omi-sync

Pull a recording off an Omi card and into Diariz.

This is **Phase 1** of the plan in
[`../../firmware/docs/07-devkit2-target.md`](../../firmware/docs/07-devkit2-target.md)
section 7.6: no firmware change, no BLE, no phone. Eject the card, run this, get
transcripts.

```
a01.txt  ->  Opus frames  ->  split into sessions  ->  Ogg Opus  ->  POST /api/recordings
```

## What it does

The DevKit 2 writes one endless file, `/SD:/audio/a01.txt`, containing Opus frames packed
into 440-byte blocks. It has **no clock and no silence gating**, so that file is an
unbroken stream across every session since the card was last cleared, with no timestamps
and no session boundaries. This tool:

1. **Parses** the blocks into Opus frames, handling the firmware's block-boundary artifact
   (docs 05 finding F13) that corrupts one frame per block if you ignore it.
2. **Splits** the stream into sessions on long silences.
3. **Remuxes** each session into Ogg Opus - the original encoder's bytes, rewrapped. No
   decode, no re-encode, no quality loss, and about 15 MB/hour instead of WAV's 115.
4. **Dates** each session by working backwards from when you pulled the card.
5. **Uploads** to Diariz as `source=Upload` with `startedAt`/`endedAt`, so recordings land
   on the timeline where they actually happened.

Silence detection needs no audio decoding: at 32 kbps VBR an Opus frame's **byte length**
tracks how much signal is in it. The threshold is chosen automatically with Otsu's method
over the frame-length histogram, with an absolute floor for the case where the whole card
is silence (which Otsu cannot distinguish from the whole card being speech).

## Install

Python 3.9+. The only runtime dependency is `requests`, and only for uploading:

```bash
pip install requests
```

For the test suite, `pytest` and optionally `mutagen` (an independent Ogg parser used to
validate our container):

```bash
pip install pytest mutagen
```

## Use

**Copy `a01.txt` off the card before you put the card back in the device.** The firmware
has `CONFIG_FS_FATFS_MOUNT_MKFS=y` with no `FS_MOUNT_FLAG_NO_FORMAT`, so a card it fails
to mount gets reformatted (docs 07 section 7.4).

Look before you upload:

```bash
python -m omi_sync a01.txt --scan-only
```

Decode locally and listen to one:

```bash
python -m omi_sync a01.txt --dry-run --out ./sessions
```

Upload:

```bash
python -m omi_sync a01.txt --url https://diariz.example.com --token dz_api_...
```

`DIARIZ_URL` and `DIARIZ_TOKEN` work instead of the flags. Mint a token in Diariz under
**Settings -> Developers**; it needs write scope, because a read-only `dz_api_` token is
rejected on POST. `--email`/`--password` will also work, but a token is longer-lived and
revocable on its own.

Then delete `a01.txt` from the card so the next cycle starts clean.

### Timing

`--ended-at` is the anchor for everything. It means *when the recording stopped* - when you
powered the device down or pulled the card - and defaults to now.

```bash
python -m omi_sync a01.txt --ended-at 2026-08-25T17:00:00
```

A time with no offset is read as **local** time. The file is treated as one continuous
stream ending at that instant, which is exactly right if the device ran continuously up to
that point, and drifts by however long it spent powered off mid-file. There is no way to do
better until the firmware carries a clock - see docs 07 section 7.6, D-fix-1.

### Tuning the split

| Flag | Default | What it does |
|---|---|---|
| `--gap-minutes` | 5 | Silence this long ends a session |
| `--min-session-seconds` | 60 | Shorter sessions are discarded as noise |
| `--max-session-hours` | 2 | Longer sessions are split, to keep uploads manageable |
| `--threshold` | auto | Frame-length silence threshold in bytes; overrides Otsu |
| `--min-active-bytes` | 16 | Frames at or below this are always silence |

If `--scan-only` reports one enormous session, the gap is too long or the threshold too
low. If it reports dozens of fragments, raise `--gap-minutes`. The scan prints the chosen
threshold, which is the first thing to sanity-check.

### Consumer CV1

`--device cv1` parses the consumer device's 444-byte ring packets (4-byte big-endian
timestamp plus a 440-byte block) with 20 ms frames. Note that this only handles the
*framing*: the CV1's per-packet UTC timestamps are skipped rather than used for dating, so
sessions are still anchored from `--ended-at`. Reading the CV1's raw ring off the card also
needs block-level access, not a filesystem - see docs 04. Untested against real CV1 data.

## Verify before you trust it

The Ogg muxer is checked against RFC 3533/7845 in `tests/test_oggopus.py`, and
`tests/test_oggopus_external.py` re-checks the output with `mutagen`, a parser we did not
write. Neither proves the *payload* decodes, because there is no Opus decoder in this
tool's dependencies.

So on your first real card, decode one session and listen to it before deleting anything:

```bash
ffprobe -hide_banner sessions/omi-*.opus          # should report opus, 1 channel, 48000 Hz
ffmpeg -i sessions/omi-20260825-164600-00.opus check.wav
```

If ffprobe is happy and the WAV sounds like the room, the whole chain is good.

## Tests

```bash
cd omi/tools/omi-sync
python -m pytest
```

107 tests, no network, no card, no Opus library. The interesting ones are
`test_framing.py` (the block-boundary artifact, reproduced exactly as the firmware creates
it) and `test_oggopus.py` (container conformance, including a CRC cross-checked against an
independently written bitwise implementation).

## Layout

| File | Role |
|---|---|
| `omi_sync/framing.py` | Card file -> Opus frames. Pure. |
| `omi_sync/sessions.py` | Frame lengths -> session boundaries. Pure. |
| `omi_sync/oggopus.py` | Opus frames -> Ogg Opus container, and a demuxer. Pure. |
| `omi_sync/pipeline.py` | Two-pass scan and session materialisation. |
| `omi_sync/diariz.py` | `POST /api/recordings`. |
| `omi_sync/cli.py` | Argument parsing and orchestration. |

Everything except `diariz.py` and `cli.py` is pure and dependency-free, which is why the
suite runs in two seconds without a network or a device.

## Memory

Two passes over the card. The first builds a 256-bucket histogram (constant memory) and a
frame-length array (2 bytes per frame - about 17 MB per day of recording). The second
materialises one session at a time, so peak memory is bounded by `--max-session-hours`
rather than by the size of the card.
