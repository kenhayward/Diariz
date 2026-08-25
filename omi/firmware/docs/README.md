# Omi firmware - review notes

These documents are a **review of the vendored `omi/firmware` tree** as it stands in this
repository, written from reading the source (not from the upstream Omi documentation site).
The goal is to establish, precisely, what this firmware does, what it stores, what the
on-air and on-disk byte formats are, and what would have to change to use the device as an
**offline ambient recorder whose audio is later uploaded to Diariz**.

Nothing in this folder changes the firmware. It is analysis only.

| Doc | Contents |
|---|---|
| [01-architecture.md](01-architecture.md) | What the tree contains, the two hardware targets, boot sequence, build and flash |
| [02-audio-formats.md](02-audio-formats.md) | Mic -> Opus -> BLE/SD. Exact byte layouts. The reference doc for any decoder |
| [03-ble-protocol.md](03-ble-protocol.md) | Every GATT service, characteristic, command and notification |
| [04-offline-storage.md](04-offline-storage.md) | The raw SD ring buffer, capacity and timing maths, sync behaviour |
| [05-findings.md](05-findings.md) | Bugs, hazards, dead code, config conflicts and security posture found during review |
| [06-repurposing-for-diariz.md](06-repurposing-for-diariz.md) | Options for offline capture -> Diariz upload, ranked by effort |
| [07-devkit2-target.md](07-devkit2-target.md) | **The device we actually built.** DevKit 2 specifics, and the plan as revised for it |
| [08-build-and-flash-runbook.md](08-build-and-flash-runbook.md) | **How to build and flash it.** Prerequisites, Docker and VS Code routes, UF2 flashing, logging, troubleshooting |
| [09-usb-transfer-mode-design.md](09-usb-transfer-mode-design.md) | **Designed, not built.** Getting recordings off over USB-C without opening the device, and formatting the card in place |

## Tools

[`omi/tools/omi-sync`](../../tools/omi-sync/README.md) implements Phase 1 of the plan in
doc 07 section 7.6: decode a card file into sessions and upload them to Diariz, with no
firmware change and no BLE.

## Which device this project targets

**Omi DevKit 2** - a Seeed Studio XIAO nRF52840 Sense with a 128 GB microSD card.

Docs 01-06 were written before that was confirmed and describe the **consumer CV1** (nRF5340)
unless they say otherwise. They remain accurate for the CV1 and are worth reading for
background - the audio pipeline, Opus settings, 440-byte block packing and BLE audio service
are shared - but where the two devices differ,
**[07-devkit2-target.md](07-devkit2-target.md) is authoritative**.

The headline differences: the DevKit stores to a **removable, FAT/exFAT-formatted card you can
read directly on a PC** rather than a raw block-level ring buffer, which removes the need for a
BLE sync client entirely. In exchange it has **no clock of any kind** and **no silence gating**,
so it produces one endless untimestamped file that has to be split host-side.

## One paragraph summary

The firmware is a Zephyr / nRF Connect SDK 2.9.0 application for a wearable that captures
16 kHz mono audio from two PDM MEMS microphones, encodes it to Opus at 32 kbps, and either
**streams it live over BLE GATT notifications** to a phone or, **when no BLE connection
exists**, writes it to an on-board 512 MB SD NAND chip as a raw block-level ring buffer.
A connected client can then drain that ring back over BLE. There is no filesystem on the
consumer device's storage, no Wi-Fi in the shipped application, no USB mass storage, and no
authentication on any characteristic. Offline capture is therefore already a first-class
feature - but retrieving it is only possible through the BLE ring protocol described in
[03-ble-protocol.md](03-ble-protocol.md), and it silently refuses to record until the clock
has been set over BLE at least once.
