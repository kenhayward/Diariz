# 9. USB transfer mode (design)

**Status: designed, not built.** This is the agreed design for the first of three firmware
sub-projects. Nothing in this document has been implemented; it is written to be turned into an
implementation plan.

Target hardware is the **Omi DevKit 2** as described in
[07-devkit2-target.md](07-devkit2-target.md). Build and flash instructions are in
[08-build-and-flash-runbook.md](08-build-and-flash-runbook.md).

---

## 9.1 The problem

The card is physically buried. Getting recordings off the device currently means disassembling
it, and the card-swap workflow in [07 section 7.6](07-devkit2-target.md) assumes exactly that.
There is also no way to reformat the card, or to recover from a card the firmware refuses to
mount, without taking the device apart.

## 9.2 Where this sits in the wider plan

The four capabilities wanted from this device decompose into three sub-projects, built in order:

| | Sub-project | Delivers | Depends on |
|---|---|---|---|
| **A** | **USB transfer mode** - this document | File access over USB-C, and formatting | - |
| B | Settable clock, synced over USB | Real timestamps on sessions and files | A, for the transport |
| C | Silence-based splitting | Session boundaries on the device | B, to name files usefully |

Sub-project C additionally requires fixing **D2** and **D4** first. The firmware today counts
files and then discards the count, pinning `file_count = 1`, and indexes a two-element
`file_num_array` by file number - so it structurally cannot handle more than one recording file.
That work belongs to C, not here.

Against the existing D-fix list in [07 section 7.6](07-devkit2-target.md): this sub-project is
**new** - the plan predates the idea of USB access and assumed card-swap throughout. It does not
close any existing D-fix, though it does resolve **D8** (no serial console) as a side effect.

## 9.3 Constraints

Three constraints shape the design. The first is absolute.

**One filesystem owner at a time.** USB Mass Storage exposes the *raw block device*. While it is
active the host's operating system owns the filesystem, and the firmware must not have FatFs
mounted. Both at once corrupts the card. Every decision below follows from this.

**USB is the speed limit, not the card.** The nRF52840 has a Full Speed USB peripheral only
(12 Mbit/s), while the card is on SPI at 24 MHz. Expect roughly **1 MB/s** in practice. At the
32 kbps Opus this firmware records, one hour of audio is about 14 MB, so transfer costs about a
minute per recorded hour. A full 128 GB card is not a realistic thing to copy in one sitting;
the workflow should assume periodic transfers, not archival dumps.

**Formatting stays off the firmware.** `CONFIG_FS_FATFS_MOUNT_MKFS` is the configuration behind
the silent card-wipe that this firmware was recently changed to prevent - see
[08 section 8.4](08-build-and-flash-runbook.md) and `FS_MOUNT_FLAG_NO_FORMAT` in `sdcard.c`. It
stays disabled. Formatting is delivered by letting the host do it (9.6), not by adding a format
path to the device.

## 9.4 Behaviour

**Plugging in charges the device and changes nothing else.** The host sees no drive, and
recording continues. This is deliberate: charging should never cost you capture, and it keeps
the host's operating system away from the card unless you deliberately asked for it. Windows in
particular writes `System Volume Information` and indexing data to every volume it mounts.

**Double-tap enters transfer mode.** Recording stops, the filesystem is unmounted, and the card
appears on the host as a removable drive, signalled by a **blinking blue** LED.

No colour is free. `main.c` drives the LEDs from a 500 ms ticker in the main loop
(`set_led_state()`), and every colour already carries a steady-state meaning:

| Signal | Meaning | Status |
|---|---|---|
| Red, green, blue, white in sequence | Boot | Existing |
| Blue, steady | Recording, BLE connected | Existing |
| Red, steady | Recording, BLE disconnected | Existing |
| Green, blinking | Charging (toggled on the 500 ms tick) | Existing |
| Red, six blinks | Card unmountable | Existing |
| **Blue, blinking, with red and green forced off** | **Transfer mode** | **New** |

Blinking blue is unambiguous because nothing else blinks blue, and forcing green off
distinguishes it from every charging state - which matters, because transfer mode always implies
a USB connection and so would otherwise always show the charging blink too.

This carries an implementation constraint: **`set_led_state()` must be suppressed while in
transfer mode.** It runs every 500 ms from the main loop and would otherwise overwrite the
pattern on the next tick. The mode is the authority on the LED whenever it is not `CAPTURE`.

**Double-tap again, or unplug, returns to capture.** The toggle means you can leave transfer
mode without unplugging.

Double-tap is used because **long-press is already bound to power-off** (`button.c`,
`BUTTON_EVENT_LONG_PRESS` calls `turnoff_all()`). Double-tap currently only emits a BLE
notification and is otherwise free.

Double-tap with no USB connected does nothing - there is no host to present the card to.

## 9.5 Structure

### `usb_mode` - the state machine

A module with **no Zephyr dependencies**, so it can be compiled and tested on the host. It is
the single owner of the "one filesystem owner" invariant.

It consumes events and emits actions; it performs no I/O itself:

| Events in | Actions out |
|---|---|
| `USB_CONNECTED`, `USB_DISCONNECTED` | `STOP_CAPTURE`, `RESUME_CAPTURE` |
| `DOUBLE_TAP` | `UNMOUNT_FS`, `REMOUNT_FS` |
| `UNMOUNT_OK`, `UNMOUNT_FAIL` | `START_MSC`, `STOP_MSC` |
| `REMOUNT_OK`, `REMOUNT_FAIL` | `SET_LED`, `SIGNAL_CARD_FAIL` |

Three states:

| State | Recording | Host sees | Notes |
|---|---|---|---|
| `CAPTURE` | Yes | Nothing | Default. USB connection charges only |
| `TRANSFER` | No | The card as a drive | Entered by double-tap while connected |
| `CARD_FAIL` | No | Nothing | Remount failed. Six red blinks, as today |

Ordering matters and is the state machine's responsibility. Entering transfer mode:
stop the microphone, **quiesce the writer**, `fs_unmount()`, then enable MSC - in that order.
Leaving: disable MSC, `fs_mount()` (still with `FS_MOUNT_FLAG_NO_FORMAT`), resume capture.

> **Corrected against hardware, 2026-08-26.** This step originally read "stop the microphone,
> close and sync the current file, `fs_unmount()`", on the assumption that `mic_off()` stops the
> writing. **It does not.** The audio pipeline writes from its own thread
> (`transport.c` -> `write_to_storage()` -> `write_to_file()`), independent of the microphone and
> with data still buffered after it stops. Unmounting therefore raced `fs_write()` against
> `fs_unmount()` and faulted the device into a reboot on every double-tap.
>
> The mechanism to prevent it already existed and was simply not used: every write takes
> `write_sdcard_mutex` and checks `is_sd_on()` first. So the correct sequence is **clear the flag,
> then take the mutex** - the flag stops a new write starting, the mutex waits out one already
> running - and only then unmount. Note `sd_off()` is the wrong tool: it physically disconnects
> the SPI pins and drops the card's enable line, which would kill mass storage.
>
> A second race was found in the same place: `mount_sd_card()` set `sd_enabled = true` *before*
> `disk_access_init()` and `fs_mount()`, so the writer could start writing into a filesystem that
> was not mounted yet. It is now set at the end of a successful mount.
>
> The general lesson: **"stop capture" is not a single action on this firmware.** The microphone,
> the encoder pipeline and the writer are separate threads, and only the writer's own lock makes
> unmounting safe.

Note that **D5 helps here**. The firmware opens and closes the file on every 440-byte write,
which is a real inefficiency, but it means there is no long-lived file handle to reconcile at
unmount time. If D5 is fixed later (it belongs to sub-project C), the unmount path must start
closing the held handle explicitly.

### USB composite device

Extend the existing `usb.c` rather than replace it, but **do not keep its `usb_enable()` call at
boot**. This was corrected against hardware on 2026-08-26 - see the note at the end of this
section.

The USB device stack is enabled **only in transfer mode**. Outside it the stack is down, so
plugging in enumerates nothing at all: the host sees no device, and the card stays exclusively
the firmware's.

Two classes are added:

* **Mass Storage**, bound to the existing disk. `CONFIG_DISK_ACCESS=y` is already set.
* **CDC-ACM**, serving as console and as a command channel.

CDC is included now rather than later for three reasons: sub-project B needs a command channel
and can simply use it; it gives the device a console without re-enabling the UART that the v2
overlay explicitly disables, which resolves **D8** and the contradiction documented in
[08 section 8.8](08-build-and-flash-runbook.md); and the USB descriptor work is done once
instead of twice.

Charge detection therefore cannot come from the USB stack. It reads VBUS directly from the POWER
peripheral (`nrf_power_usbregstatus_vbusdet_get`), which works with the stack disabled. A poll on
the existing 500 ms main-loop tick edge-detects it and is the **single source** of both the
`usb_charge` flag and the state machine's connect and disconnect events.

`init_usb()` no longer enables anything, so its old `CONFIG_UART_CONSOLE` branch is gone rather
than merely deferred.

> **Corrected against hardware, 2026-08-26.** The original design said `usb.c` "already calls
> `usb_enable()` ... both are kept", and treated `usb_msc_start`/`usb_msc_stop` as the thing that
> controls host visibility. That was wrong. Zephyr's legacy stack exposes **every configured
> class the moment the device enumerates**, so keeping the boot-time `usb_enable()` - which
> existed only to detect charging - handed Windows the card on every plug-in, while the firmware
> still had FatFs mounted and was appending audio to it. Precisely the both-owners-at-once case
> section 9.3 forbids. The verification checklist caught it on the first item, before anything was
> copied off.
>
> **It then happened a second time, for a different reason.** Removing the explicit
> `usb_enable()` was necessary but not sufficient: `CONFIG_USB_DEVICE_INITIALIZE_AT_BOOT`
> *depends on* `CONFIG_USB_CDC_ACM` and defaults on, so adding CDC - for the clock sub-project,
> nothing to do with mass storage - made Zephyr call `usb_enable()` from `usb_device.c` during
> system init, bypassing `init_usb()` entirely. The card was exposed again. It is now explicitly
> `=n`.
>
> The lesson worth keeping: with the legacy USB stack, **enumeration is the thing to gate, not the
> class**. Anything that enables the stack for an unrelated reason silently publishes the card,
> and a Kconfig symbol you never typed can be that thing. **Assert the guard, do not infer it**:
> verify `CONFIG_USB_DEVICE_INITIALIZE_AT_BOOT` is unset in the *generated* `.config`, not in the
> project `.conf` - the whole failure was the gap between those two.

## 9.6 Formatting

Delivered for free, and deliberately not implemented in firmware. In transfer mode the host owns
the block device, so Windows, macOS or Linux can format the card with their own tools - which
understand the filesystem far better than FatFs does, and which are the reason the PC was made
the owner of formatting in the first place.

**No firmware format command, and `CONFIG_FS_FATFS_MOUNT_MKFS` stays `n`.** This is recorded as a
deliberate non-feature so that it is not later added as an apparent convenience.

This also covers the recovery case: a card the firmware cannot mount still presents as a block
device over MSC, so the host can reformat it without the device being opened.

## 9.7 Failure handling

| Situation | Behaviour |
|---|---|
| Unplugged during transfer | Tear down MSC, remount, resume capture |
| Unplugged mid-copy | As above. The host's incomplete file is the host's problem; the card is left consistent because the firmware wrote nothing during transfer |
| Remount fails on exit | `CARD_FAIL`: six red blinks, capture stays stopped. Never silently fail to record |
| Unmount fails on entry | Stay in `CAPTURE`, signal on the LED, do not start MSC |
| Long-press (power off) during transfer | **Ignored**, with an LED blink. Powering down mid-write would yank storage out from under the host |
| Double-tap during transfer | Returns to `CAPTURE` - the documented escape route |

## 9.8 Testing

**Host-compiled unit tests over `usb_mode`.** This mirrors the pattern CLAUDE.md endorses for the
Python worker, where `_shape_segments` was extracted specifically so it could be tested without
the models. The state machine is where the bugs will be, and it is pure logic, so it is testable
without hardware. Coverage must include the awkward transitions, not just the happy path:

* Double-tap with no USB connected - must do nothing
* Double-tap while already in transfer mode - must return to capture
* Unplug while in transfer mode
* Unmount failure on entry, and remount failure on exit
* Long-press during transfer - must be ignored
* That `START_MSC` is never emitted while the filesystem is mounted, and `REMOUNT_FS` never
  while MSC is running. This is the invariant from 9.3 and deserves its own explicit test

**Manual hardware checklist**, added to the runbook, for what cannot be automated: enumerate as a
drive, copy a file off, format from the host, unplug mid-copy, confirm capture resumes, confirm
the card still reads afterwards, confirm charging alone does not mount it.

## 9.9 Out of scope

Stated explicitly so the implementation does not drift:

* **No clock** - sub-project B. CDC is provided for it; the protocol is not.
* **No file rotation or silence splitting** - sub-project C.
* **No fixes to D1, D2, D4, D5** - these live in the recording path and belong to C, which
  rebuilds the file table anyway.
* **No BLE changes.** D6 - a stray BLE connection silently suppressing recording - remains, and
  remains D-fix-4.
* **No firmware format path** (9.6).

## 9.10 Risks

**Zephyr USB stack choice.** NCS 2.7 carries both the legacy `usb_device` stack and the newer
`usb_next`. The existing `usb.c` uses the legacy one. MSC plus CDC composite is well-trodden
there, so the plan should stay on the legacy stack and treat any migration as separate work.

**Console logging versus SD writes.** [08 section 8.8](08-build-and-flash-runbook.md) records
that enabling logging alongside offline storage needs the log thread's priority dropped or it
contends with SD writes. Turning on a CDC console re-opens that question, and the shipped
configuration should be decided deliberately rather than left at whatever makes debugging
convenient.

**Transfer speed disappoints.** 1 MB/s is a design constraint, not a bug to fix later - it is a
property of the SoC's Full Speed USB. If it proves unacceptable in use, the answer is smaller
files through silence splitting (sub-project C), not a faster transport.

**Enumeration while unpowered.** Behaviour on a flat battery, where USB provides power and the
host enumerates during charging, has not been considered in detail and should be checked on
hardware.
