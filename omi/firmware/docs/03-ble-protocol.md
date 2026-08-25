# 3. The BLE protocol

Everything the consumer device exposes over GATT, as registered in
`transport_start()` (`omi/src/lib/core/transport.c`) and `storage_init()`
(`omi/src/lib/core/storage.c`).

## 3.1 Advertising

- Device name: **`Omi`** (`CONFIG_BT_DEVICE_NAME`), appearance 22.
- Advertising data: flags, the 128-bit **audio service UUID**, complete local name.
- Scan response: the 16-bit Device Information Service UUID.
- Connectable, peripheral only, one connection maximum.

On connect the firmware requests, with retries: connection interval 7.5-15 ms with zero
latency and a 4 s supervision timeout, **2M PHY**, maximum data length (251 bytes), and an MTU
exchange (`CONFIG_BT_L2CAP_TX_MTU=498`). It rechecks the MTU up to 6 times at 800 ms intervals
because some phones report 23 for a while after connecting.

## 3.2 Services

| Service | UUID | Present when |
|---|---|---|
| Audio | `19B10000-E8F2-537E-4F6C-D104768A1214` | always |
| Settings | `19B10010-E8F2-537E-4F6C-D104768A1214` | always |
| Features | `19B10020-E8F2-537E-4F6C-D104768A1214` | always |
| Time sync | `19B10030-E8F2-537E-4F6C-D104768A1214` | always |
| Offline storage | `30295780-4301-EABD-2904-2849ADFEAE43` | `CONFIG_OMI_ENABLE_OFFLINE_STORAGE` (on) |
| Button | `23BA7924-0000-1000-7450-346EAC492E92` | `CONFIG_OMI_ENABLE_BUTTON` (on) |
| Haptic | `CAB1AB95-2EA5-4F4D-BB56-874B72CFC984` | `CONFIG_OMI_ENABLE_HAPTIC` (on) |
| Accelerometer | `32403790-0000-1000-7450-BF445E5829A2` | `CONFIG_OMI_ENABLE_ACCELEROMETER` (**off**, and see F6) |
| Battery (BAS) | `0x180F` standard | `CONFIG_BT_BAS=y` |
| Device Information | `0x180A` standard | `CONFIG_BT_DIS=y`; model "Omi CV 1", manufacturer "Based Hardware", FW rev string in `omi.conf` |
| MCUmgr SMP (OTA DFU) | Nordic SMP service | `CONFIG_NCS_SAMPLE_MCUMGR_BT_OTA_DFU=y` |

The haptic service and the (disabled) speaker service share UUID `CAB1AB95-...`.

### Audio service `19B10000-...`

| Characteristic | UUID | Properties | Payload |
|---|---|---|---|
| Audio data | `19B10001-...` | read, **notify** | See [02-audio-formats.md](02-audio-formats.md) section 2.4 |
| Audio codec | `19B10002-...` | read | 1 byte codec id, **21** = Opus 16 kHz mono |
| Speaker | `19B10003-...` | write, notify | Only registered when `CONFIG_OMI_ENABLE_SPEAKER` is set (it is not) |

Subscribing to the CCC of `19B10001` starts the live stream. Unsubscribing stops it - and, as
described in [05-findings.md](05-findings.md) finding F2, audio is then **discarded**, not
written to the SD card, for as long as the connection stays up.

### Settings service `19B10010-...`

| Characteristic | UUID | Properties | Payload |
|---|---|---|---|
| LED dim ratio | `19B10011-...` | read, write | 1 byte, 0-100, clamped, persisted to NVS |
| Mic gain | `19B10012-...` | read, write | 1 byte, 0-8, clamped, persisted and applied immediately |
| Charging status | `19B10013-...` | read, notify | 1 byte, 1 = charging |

### Features service `19B10020-...`

`19B10021-...`, read only, a native little-endian `uint32` bitmask
(`omi/src/lib/core/features.h`):

| Bit | Feature | Set in the shipped build? |
|---|---|---|
| 0 | Speaker | no |
| 1 | Accelerometer | no |
| 2 | Button | yes |
| 3 | Battery | yes |
| 4 | USB | no |
| 5 | Haptic | yes |
| 6 | Offline storage | yes |
| 7 | LED dimming | yes (always) |
| 8 | Mic gain | yes (always) |

### Time sync service `19B10030-...`

| Characteristic | UUID | Properties | Payload |
|---|---|---|---|
| Time write | `19B10031-...` | write | exactly 4 bytes, `uint32` UTC epoch seconds, **native little-endian** (`memcpy`) |
| Time read | `19B10032-...` | read | 4 bytes, `uint32` UTC epoch seconds, native little-endian |

**This write is mandatory before the device will record anything offline.** It sets the RTC,
persists the epoch to NVS (deferred to the system workqueue), and notifies the SD module.

Note the endianness inconsistency: the time-sync characteristics use native little-endian,
while the storage service control notifications below use big-endian.

## 3.3 The offline storage protocol `30295780-...`

Two characteristics:

| Characteristic | UUID | Properties | Role |
|---|---|---|---|
| Control / data | `30295781-...` | **write**, **notify** | Commands go in, ACKs / INFO / DATA / DONE come out |
| Status | `30295782-...` | read, notify | Cached status snapshot |

### Status read (`30295782-...`)

Four native little-endian `uint32` values, 16 bytes total:

```
[0] used_bytes        unread packets * 444
[1] unread_packets    write_seq - read_seq
[2] free_bytes        (capacity_packets - unread) * 444
[3] rtc_valid         1 if the clock has been set, 0 if not
```

The cache is refreshed at most every 250 ms while connected, and adjusted live during a sync
so free space visibly grows.

### Commands (write to `30295781-...`)

| Command | Byte 0 | Length | Body |
|---|---|---|---|
| `CMD_RING_INFO` | `0x10` | 1 | - |
| `CMD_RING_READ` | `0x11` | 9 or 13 | `uint64` big-endian start_seq, optional `uint32` big-endian packet count (0 or omitted = everything available) |
| `CMD_RING_ADVANCE` | `0x12` | 9 | `uint64` big-endian new read_seq - frees everything below it |
| `CMD_RING_CLEAR` | `0x13` | 1 | discards the whole ring |
| `CMD_STOP_SYNC` | `0x03` | >= 1 | aborts an in-flight transfer |

Commands are parsed in the GATT write callback but **executed on the storage thread**, so all
responses arrive as notifications, never as write responses.

### Notifications (from `30295781-...`)

| Marker | Name | Length | Layout |
|---|---|---|---|
| `0x01` | ACK | 2 | `[0x01][status]` |
| `0x02` | INFO | 31 | `[0x02][be64 read_seq][be64 write_seq][be32 capacity_packets][be64 dropped_packets][be16 444]` |
| `0x03` | DATA | up to MTU-3 | `[0x03][raw ring bytes]` |
| `0x04` | DONE | 10 | `[0x04][status][be64 next_seq]` |
| `0x05` | READ_BEGIN | 13 | `[0x05][be64 start_seq][be32 packet_count]` |

Status codes: `0` success, `6` invalid command, `9` storage not ready, `10` sequence out of
range.

### A complete sync

```
1. Connect, negotiate 2M PHY + MTU, subscribe to 30295781 notifications.
2. Write 0x10                       -> INFO (read_seq, write_seq, capacity, dropped, 444)
3. Write 0x11 + be64(read_seq)      -> READ_BEGIN, then a long run of DATA, then DONE
4. Concatenate every DATA payload minus its 0x03 marker.
   The result is (write_seq - read_seq) * 444 bytes of ring packets. Decode per 02-audio-formats.md.
5. Optionally write 0x12 + be64(next_seq from DONE) to free the space.
   (The firmware already auto-advances as it goes - see below.)
```

**The firmware auto-advances.** Every DATA notification carries a completion callback that
accumulates the bytes the phone's controller confirmed, and every 2 s (or on DONE, or on
disconnect) the ring read pointer is persisted up to that point. This means:

- Device storage is freed **as the sync proceeds**, not at the end.
- A mid-sync disconnect resumes from the last checkpoint rather than restarting.
- **A client that connects and issues `CMD_RING_READ` will cause the device to discard that
  audio even if the client then crashes or throws the data away.** There is no acknowledgement
  from the application layer, only from the BLE controller. Treat a sync as destructive and
  write to durable storage before the next checkpoint tick.

### Throughput and flow control

Audio streaming and storage sync share one throttle semaphore capped at
`CONFIG_BT_CONN_TX_MAX - 2` = 18 in-flight notifications, reserving two TX buffers for short
control notifications so battery and status updates cannot be starved during a sync.

The firmware logs the achieved sync rate every 2 s as `Sync speed (BLE): N KB/s` when logging
is compiled in (it is **off** in the shipped `omi.conf`). No measured figure is recorded in
this tree. As an order of magnitude: audio accrues at about 4.1 kB/s, so any sync rate above
that drains the backlog; a typical BLE 2M-PHY GATT-notification link runs tens of kB/s, which
would drain a full 512 MB card in a few hours.

## 3.4 Security posture

There is **none** worth the name for an ambient recorder:

- Every characteristic uses `BT_GATT_PERM_READ` / `BT_GATT_PERM_WRITE`, not the `_ENCRYPT` or
  `_AUTHEN` variants. No characteristic requires an encrypted or authenticated link.
- No `bt_conn_auth_cb` is registered in the shipped `omi` application (there is one in
  `src/lib/evt/ble.c`, which is not compiled).
- `CONFIG_BT_SMP=y` but `CONFIG_BT_SETTINGS` is not set, so bonds are not persisted.
- No allowlist, no pairing requirement, no application-level key.

Any BLE central in range can connect to a device named `Omi`, subscribe to the audio
characteristic and listen live, or run the ring protocol and download - and thereby delete -
everything recorded offline. If this device is going to be worn in meetings, that matters. See
[06-repurposing-for-diariz.md](06-repurposing-for-diariz.md) for the smallest change that
fixes it.
