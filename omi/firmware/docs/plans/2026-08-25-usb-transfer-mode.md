# USB Transfer Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the Omi DevKit 2 present its SD card to a PC over USB-C on a double-tap, so recordings can be copied off and the card reformatted without opening the device.

**Architecture:** A pure C state machine (`usb_mode`) with no Zephyr dependencies owns the one rule that matters - the firmware's FatFs mount and USB Mass Storage are never active at the same time. It consumes events and returns actions; `main.c` performs the I/O those actions name. USB comes up as a composite device: mass storage plus CDC-ACM.

**Tech Stack:** C11, Zephyr / nRF Connect SDK 2.7.0, board `xiao_ble/nrf52840/sense`, legacy Zephyr `usb_device` stack, gcc 11.4 (host tests, run in the pinned build container).

**Design document:** [`omi/firmware/docs/09-usb-transfer-mode-design.md`](../09-usb-transfer-mode-design.md)

## Global Constraints

- **One filesystem owner at a time.** `START_MSC` must never be emitted while FatFs is mounted; `REMOUNT_FS` must never be emitted while MSC is running. This is the reason the module exists.
- **`CONFIG_FS_FATFS_MOUNT_MKFS` stays `n`.** Never enable it. It is the config behind the silent card-wipe this firmware was changed to prevent. Formatting is the host's job.
- **`FS_MOUNT_FLAG_NO_FORMAT` stays set** on the mount point in `sdcard.c`.
- **`usb_mode.c` must not include any Zephyr header.** It is compiled by host gcc in the tests. If it needs a type, define it in `usb_mode.h`.
- **ASCII only** in all files. No em dashes, en dashes, or curly quotes.
- **Do not fix D1, D2, D4, D5 or D6.** They are out of scope and belong to a later sub-project.
- **No firmware format command.** Out of scope, deliberately.
- Build: `./omi/firmware/scripts/build-docker.sh` (image pinned to `ghcr.io/zephyrproject-rtos/ci:v0.26.14`).
- Host tests: `./omi/firmware/scripts/run-host-tests.sh`.
- `omi/` only - no version bump, no `RELEASES` entry, no Diariz doc updates.

---

## File Structure

| File | Responsibility |
|---|---|
| `omi/firmware/devkit/src/usb_mode.h` | Public types: states, events, actions, the action list, and the module's five functions |
| `omi/firmware/devkit/src/usb_mode.c` | The state machine. Pure logic, no I/O, no Zephyr |
| `omi/firmware/devkit/tests/test_usb_mode.c` | Host-compiled unit tests, plain C asserts, no framework |
| `omi/firmware/scripts/run-host-tests.sh` | Compiles and runs the host tests inside the pinned container |
| `omi/firmware/devkit/src/sdcard.c` | Gains `unmount_sd_card()`; `sdcard.h` gains its declaration |
| `omi/firmware/devkit/src/usb.c` | Gains MSC + CDC composite bring-up alongside existing charge detection |
| `omi/firmware/devkit/src/main.c` | Feeds events in, performs actions, owns the transfer-mode LED |
| `omi/firmware/devkit/src/button.c` | Routes double-tap into the state machine; gates power-off |
| `omi/firmware/devkit/prj_xiao_ble_sense_devkitv2-adafruit.conf` | USB class configuration |
| `omi/firmware/devkit/CMakeLists.txt` | Adds `src/usb_mode.c` |
| `omi/firmware/docs/08-build-and-flash-runbook.md` | Gains the manual hardware verification checklist |

**Task order:** Tasks 1-5 build and fully test the state machine on the host, with no firmware changes. Tasks 6-9 integrate it. Task 10 verifies on hardware and documents.

---

## Task 1: Host test harness and the module skeleton

**Files:**
- Create: `omi/firmware/devkit/src/usb_mode.h`
- Create: `omi/firmware/devkit/src/usb_mode.c`
- Create: `omi/firmware/devkit/tests/test_usb_mode.c`
- Create: `omi/firmware/scripts/run-host-tests.sh`

**Interfaces:**
- Consumes: nothing.
- Produces: the complete public interface every later task uses - `usb_mode_state_t`, `usb_mode_event_t`, `usb_mode_action_t`, `usb_mode_actions_t`, and the functions `usb_mode_init(void)`, `usb_mode_get_state(void)`, `usb_mode_handle(usb_mode_event_t)`, `usb_mode_allows_poweroff(void)`.

There is no C test framework in this repo and none is being added, matching the "no mocking library" convention in CLAUDE.md. The harness is a `CHECK` macro and a `main` that returns non-zero on failure.

- [ ] **Step 1: Write the header**

Create `omi/firmware/devkit/src/usb_mode.h`:

```c
#ifndef USB_MODE_H
#define USB_MODE_H

#include <stdbool.h>
#include <stdint.h>

/*
 * Transfer-mode state machine. Pure logic: it performs no I/O and includes no
 * Zephyr headers, so it is compiled and unit tested by host gcc.
 *
 * It owns one invariant: the firmware's FatFs mount and USB Mass Storage are
 * never active at the same time. See docs/09-usb-transfer-mode-design.md.
 */

typedef enum {
    USB_MODE_CAPTURE = 0, /* Recording. A USB connection charges only.        */
    USB_MODE_ENTERING,    /* Capture stopped, unmount requested, awaiting it. */
    USB_MODE_TRANSFER,    /* MSC live. The host owns the card.                */
    USB_MODE_LEAVING,     /* MSC stopped, remount requested, awaiting it.     */
    USB_MODE_CARD_FAIL,   /* Remount failed. Not recording.                   */
} usb_mode_state_t;

typedef enum {
    USB_MODE_EVENT_USB_CONNECTED = 0,
    USB_MODE_EVENT_USB_DISCONNECTED,
    USB_MODE_EVENT_DOUBLE_TAP,
    USB_MODE_EVENT_UNMOUNT_OK,
    USB_MODE_EVENT_UNMOUNT_FAIL,
    USB_MODE_EVENT_REMOUNT_OK,
    USB_MODE_EVENT_REMOUNT_FAIL,
} usb_mode_event_t;

typedef enum {
    USB_MODE_ACTION_STOP_CAPTURE = 0,
    USB_MODE_ACTION_RESUME_CAPTURE,
    USB_MODE_ACTION_UNMOUNT_FS,
    USB_MODE_ACTION_REMOUNT_FS,
    USB_MODE_ACTION_START_MSC,
    USB_MODE_ACTION_STOP_MSC,
    USB_MODE_ACTION_LED_CAPTURE,   /* Hand the LED back to set_led_state()   */
    USB_MODE_ACTION_LED_TRANSFER,  /* Blinking blue, red and green forced off */
    USB_MODE_ACTION_LED_CARD_FAIL, /* Six red blinks                          */
} usb_mode_action_t;

#define USB_MODE_MAX_ACTIONS 4

typedef struct {
    usb_mode_action_t actions[USB_MODE_MAX_ACTIONS];
    uint8_t count;
} usb_mode_actions_t;

/* Reset to USB_MODE_CAPTURE with USB recorded as disconnected. */
void usb_mode_init(void);

usb_mode_state_t usb_mode_get_state(void);

/* Feed one event. Returns the actions the caller must perform, in order. */
usb_mode_actions_t usb_mode_handle(usb_mode_event_t event);

/* False in every state except USB_MODE_CAPTURE. Powering down mid-transfer
 * would pull storage out from under the host. */
bool usb_mode_allows_poweroff(void);

#endif /* USB_MODE_H */
```

- [ ] **Step 2: Write the failing tests**

Create `omi/firmware/devkit/tests/test_usb_mode.c`:

```c
#include <stdio.h>
#include "../src/usb_mode.h"

static int failures;

#define CHECK(cond)                                                            \
    do {                                                                       \
        if (!(cond)) {                                                         \
            printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);             \
            failures++;                                                        \
        }                                                                      \
    } while (0)

static void test_starts_in_capture(void)
{
    usb_mode_init();
    CHECK(usb_mode_get_state() == USB_MODE_CAPTURE);
}

static void test_double_tap_without_usb_does_nothing(void)
{
    usb_mode_init();
    usb_mode_actions_t a = usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    CHECK(usb_mode_get_state() == USB_MODE_CAPTURE);
    CHECK(a.count == 0);
}

int main(void)
{
    test_starts_in_capture();
    test_double_tap_without_usb_does_nothing();

    if (failures) {
        printf("%d check(s) failed\n", failures);
        return 1;
    }
    printf("all checks passed\n");
    return 0;
}
```

- [ ] **Step 3: Write the test runner script**

Create `omi/firmware/scripts/run-host-tests.sh`. Note it deliberately does **not** pass `-it`, unlike `build-docker.sh`, so it runs from a non-interactive shell such as CI or an agent:

```bash
#!/bin/bash

# Compiles and runs the pure-logic unit tests on the host toolchain inside the
# pinned build container. There is no C compiler on a stock Windows machine, and
# the container already carries gcc, so this needs no extra prerequisite beyond
# the one the firmware build already has.
#
# No `-it` here, unlike build-docker.sh: this must run from CI and other
# non-interactive shells, where a TTY-enabled container is refused.

set -euo pipefail

# See build-docker.sh for why this is exported rather than left to the caller.
export MSYS_NO_PATHCONV=1

GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m'

REPO_ROOT=$(cd "$(dirname "$0")/../.." && pwd)
ZEPHYR_CI_IMAGE="${ZEPHYR_CI_IMAGE:-ghcr.io/zephyrproject-rtos/ci:v0.26.14}"

if docker run --rm -v "$REPO_ROOT:/omi" "$ZEPHYR_CI_IMAGE" bash -c '
    set -e
    cd /omi/firmware/devkit
    gcc -std=c11 -Wall -Wextra -Werror -O1 \
        -o /tmp/test_usb_mode src/usb_mode.c tests/test_usb_mode.c
    /tmp/test_usb_mode
'; then
    echo -e "${GREEN}Host tests passed${NC}"
else
    echo -e "${RED}Host tests FAILED${NC}"
    exit 1
fi
```

Then make it executable: `chmod +x omi/firmware/scripts/run-host-tests.sh`

- [ ] **Step 4: Run the tests to verify they fail**

Run: `./omi/firmware/scripts/run-host-tests.sh`

Expected: FAIL. The compile fails because `src/usb_mode.c` does not exist yet - gcc reports `No such file or directory`. This is the correct red state; do not create the file before seeing it.

- [ ] **Step 5: Write the minimal implementation**

Create `omi/firmware/devkit/src/usb_mode.c`:

```c
#include "usb_mode.h"

static usb_mode_state_t state;
static bool usb_connected;

static usb_mode_actions_t none(void)
{
    usb_mode_actions_t a = {.count = 0};
    return a;
}

void usb_mode_init(void)
{
    state = USB_MODE_CAPTURE;
    usb_connected = false;
}

usb_mode_state_t usb_mode_get_state(void)
{
    return state;
}

bool usb_mode_allows_poweroff(void)
{
    return state == USB_MODE_CAPTURE;
}

usb_mode_actions_t usb_mode_handle(usb_mode_event_t event)
{
    (void)event;
    return none();
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `./omi/firmware/scripts/run-host-tests.sh`
Expected: PASS, printing `all checks passed`.

- [ ] **Step 7: Verify the harness can actually fail**

A test suite that cannot fail is worthless. Temporarily change `test_starts_in_capture` to `CHECK(usb_mode_get_state() == USB_MODE_TRANSFER);`, re-run, and confirm you see `FAIL ... usb_mode_get_state() == USB_MODE_TRANSFER` and a non-zero exit. Then revert the change and confirm it passes again.

- [ ] **Step 8: Commit**

```bash
git add omi/firmware/devkit/src/usb_mode.h omi/firmware/devkit/src/usb_mode.c \
        omi/firmware/devkit/tests/test_usb_mode.c omi/firmware/scripts/run-host-tests.sh
git commit -m "feat(omi): add usb_mode skeleton and a host test harness"
```

---

## Task 2: Entering transfer mode

**Files:**
- Modify: `omi/firmware/devkit/src/usb_mode.c`
- Modify: `omi/firmware/devkit/tests/test_usb_mode.c`

**Interfaces:**
- Consumes: everything from Task 1.
- Produces: no new public symbols. Establishes that entry is two-phase - `usb_mode_handle` emits `UNMOUNT_FS` and waits for `UNMOUNT_OK` before `START_MSC` is ever emitted.

- [ ] **Step 1: Write the failing tests**

Add to `test_usb_mode.c`, and add the calls to `main`:

```c
static void test_double_tap_with_usb_enters(void)
{
    usb_mode_init();
    usb_mode_handle(USB_MODE_EVENT_USB_CONNECTED);
    usb_mode_actions_t a = usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    CHECK(usb_mode_get_state() == USB_MODE_ENTERING);
    CHECK(a.count == 2);
    CHECK(a.actions[0] == USB_MODE_ACTION_STOP_CAPTURE);
    CHECK(a.actions[1] == USB_MODE_ACTION_UNMOUNT_FS);
}

static void test_unmount_ok_starts_msc(void)
{
    usb_mode_init();
    usb_mode_handle(USB_MODE_EVENT_USB_CONNECTED);
    usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    usb_mode_actions_t a = usb_mode_handle(USB_MODE_EVENT_UNMOUNT_OK);
    CHECK(usb_mode_get_state() == USB_MODE_TRANSFER);
    CHECK(a.count == 2);
    CHECK(a.actions[0] == USB_MODE_ACTION_START_MSC);
    CHECK(a.actions[1] == USB_MODE_ACTION_LED_TRANSFER);
}

static void test_double_tap_while_entering_is_ignored(void)
{
    usb_mode_init();
    usb_mode_handle(USB_MODE_EVENT_USB_CONNECTED);
    usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    usb_mode_actions_t a = usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    CHECK(usb_mode_get_state() == USB_MODE_ENTERING);
    CHECK(a.count == 0);
}

static void test_usb_connect_alone_does_not_enter(void)
{
    usb_mode_init();
    usb_mode_actions_t a = usb_mode_handle(USB_MODE_EVENT_USB_CONNECTED);
    CHECK(usb_mode_get_state() == USB_MODE_CAPTURE);
    CHECK(a.count == 0);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `./omi/firmware/scripts/run-host-tests.sh`
Expected: FAIL, with four `FAIL` lines - the state stays `USB_MODE_CAPTURE` and `a.count` is 0.

- [ ] **Step 3: Write the implementation**

Replace `usb_mode_handle` in `usb_mode.c`, and add the `push` helper above it:

```c
static void push(usb_mode_actions_t *a, usb_mode_action_t action)
{
    if (a->count < USB_MODE_MAX_ACTIONS) {
        a->actions[a->count++] = action;
    }
}

usb_mode_actions_t usb_mode_handle(usb_mode_event_t event)
{
    usb_mode_actions_t a = {.count = 0};

    if (event == USB_MODE_EVENT_USB_CONNECTED) {
        usb_connected = true;
        return a;
    }
    if (event == USB_MODE_EVENT_USB_DISCONNECTED) {
        usb_connected = false;
        return a;
    }

    switch (state) {
    case USB_MODE_CAPTURE:
        if (event == USB_MODE_EVENT_DOUBLE_TAP && usb_connected) {
            state = USB_MODE_ENTERING;
            push(&a, USB_MODE_ACTION_STOP_CAPTURE);
            push(&a, USB_MODE_ACTION_UNMOUNT_FS);
        }
        break;

    case USB_MODE_ENTERING:
        if (event == USB_MODE_EVENT_UNMOUNT_OK) {
            state = USB_MODE_TRANSFER;
            push(&a, USB_MODE_ACTION_START_MSC);
            push(&a, USB_MODE_ACTION_LED_TRANSFER);
        }
        break;

    default:
        break;
    }

    return a;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `./omi/firmware/scripts/run-host-tests.sh`
Expected: PASS, `all checks passed`.

- [ ] **Step 5: Commit**

```bash
git add omi/firmware/devkit/src/usb_mode.c omi/firmware/devkit/tests/test_usb_mode.c
git commit -m "feat(omi): usb_mode enters transfer mode on double-tap"
```

---

## Task 3: Leaving transfer mode

**Files:**
- Modify: `omi/firmware/devkit/src/usb_mode.c`
- Modify: `omi/firmware/devkit/tests/test_usb_mode.c`

**Interfaces:**
- Consumes: Task 2's state machine.
- Produces: no new public symbols. Establishes that exit is also two-phase, and that both a second double-tap and an unplug trigger it.

- [ ] **Step 1: Write the failing tests**

Add to `test_usb_mode.c`, and add the calls to `main`. The `enter_transfer` helper is used by later tasks too, so define it above these tests:

```c
static void enter_transfer(void)
{
    usb_mode_init();
    usb_mode_handle(USB_MODE_EVENT_USB_CONNECTED);
    usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    usb_mode_handle(USB_MODE_EVENT_UNMOUNT_OK);
}

static void test_double_tap_leaves_transfer(void)
{
    enter_transfer();
    usb_mode_actions_t a = usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    CHECK(usb_mode_get_state() == USB_MODE_LEAVING);
    CHECK(a.count == 2);
    CHECK(a.actions[0] == USB_MODE_ACTION_STOP_MSC);
    CHECK(a.actions[1] == USB_MODE_ACTION_REMOUNT_FS);
}

static void test_unplug_leaves_transfer(void)
{
    enter_transfer();
    usb_mode_actions_t a = usb_mode_handle(USB_MODE_EVENT_USB_DISCONNECTED);
    CHECK(usb_mode_get_state() == USB_MODE_LEAVING);
    CHECK(a.count == 2);
    CHECK(a.actions[0] == USB_MODE_ACTION_STOP_MSC);
    CHECK(a.actions[1] == USB_MODE_ACTION_REMOUNT_FS);
}

static void test_remount_ok_resumes_capture(void)
{
    enter_transfer();
    usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    usb_mode_actions_t a = usb_mode_handle(USB_MODE_EVENT_REMOUNT_OK);
    CHECK(usb_mode_get_state() == USB_MODE_CAPTURE);
    CHECK(a.count == 2);
    CHECK(a.actions[0] == USB_MODE_ACTION_RESUME_CAPTURE);
    CHECK(a.actions[1] == USB_MODE_ACTION_LED_CAPTURE);
}

static void test_double_tap_while_leaving_is_ignored(void)
{
    enter_transfer();
    usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    usb_mode_actions_t a = usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    CHECK(usb_mode_get_state() == USB_MODE_LEAVING);
    CHECK(a.count == 0);
}
```

Note the USB-disconnect case needs care: the early `USB_MODE_EVENT_USB_DISCONNECTED` branch added in Task 2 returns before reaching the state switch. Step 3 fixes that.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `./omi/firmware/scripts/run-host-tests.sh`
Expected: FAIL. `test_unplug_leaves_transfer` fails because the disconnect is swallowed by the early return; the others fail because `USB_MODE_TRANSFER` and `USB_MODE_LEAVING` have no handlers.

- [ ] **Step 3: Write the implementation**

In `usb_mode.c`, change the two connection-flag branches so they record the flag and then **fall through to the state machine** rather than returning:

```c
    if (event == USB_MODE_EVENT_USB_CONNECTED) {
        usb_connected = true;
    }
    if (event == USB_MODE_EVENT_USB_DISCONNECTED) {
        usb_connected = false;
    }
```

Then add these cases to the `switch`:

```c
    case USB_MODE_TRANSFER:
        if (event == USB_MODE_EVENT_DOUBLE_TAP ||
            event == USB_MODE_EVENT_USB_DISCONNECTED) {
            state = USB_MODE_LEAVING;
            push(&a, USB_MODE_ACTION_STOP_MSC);
            push(&a, USB_MODE_ACTION_REMOUNT_FS);
        }
        break;

    case USB_MODE_LEAVING:
        if (event == USB_MODE_EVENT_REMOUNT_OK) {
            state = USB_MODE_CAPTURE;
            push(&a, USB_MODE_ACTION_RESUME_CAPTURE);
            push(&a, USB_MODE_ACTION_LED_CAPTURE);
        }
        break;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `./omi/firmware/scripts/run-host-tests.sh`
Expected: PASS. All Task 2 tests must still pass - if `test_usb_connect_alone_does_not_enter` now fails, the fall-through was written wrongly.

- [ ] **Step 5: Commit**

```bash
git add omi/firmware/devkit/src/usb_mode.c omi/firmware/devkit/tests/test_usb_mode.c
git commit -m "feat(omi): usb_mode leaves transfer on double-tap or unplug"
```

---

## Task 4: Failure paths, card-fail recovery, and the power-off gate

**Files:**
- Modify: `omi/firmware/devkit/src/usb_mode.c`
- Modify: `omi/firmware/devkit/tests/test_usb_mode.c`

**Interfaces:**
- Consumes: Task 3's state machine.
- Produces: no new public symbols. Makes `usb_mode_allows_poweroff()` meaningful, and establishes the recovery path promised by design section 9.6 - a card that will not mount can still be presented to the host for reformatting.

- [ ] **Step 1: Write the failing tests**

Add to `test_usb_mode.c`, and add the calls to `main`:

```c
static void test_unmount_fail_returns_to_capture(void)
{
    usb_mode_init();
    usb_mode_handle(USB_MODE_EVENT_USB_CONNECTED);
    usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    usb_mode_actions_t a = usb_mode_handle(USB_MODE_EVENT_UNMOUNT_FAIL);
    CHECK(usb_mode_get_state() == USB_MODE_CAPTURE);
    CHECK(a.count == 2);
    CHECK(a.actions[0] == USB_MODE_ACTION_RESUME_CAPTURE);
    CHECK(a.actions[1] == USB_MODE_ACTION_LED_CARD_FAIL);
}

static void test_remount_fail_enters_card_fail(void)
{
    enter_transfer();
    usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    usb_mode_actions_t a = usb_mode_handle(USB_MODE_EVENT_REMOUNT_FAIL);
    CHECK(usb_mode_get_state() == USB_MODE_CARD_FAIL);
    CHECK(a.count == 1);
    CHECK(a.actions[0] == USB_MODE_ACTION_LED_CARD_FAIL);
}

/* Design 9.6: a card the firmware cannot mount must still be presentable to
 * the host, so it can be reformatted without opening the device. Nothing is
 * mounted in CARD_FAIL, so there is no unmount step. */
static void test_card_fail_can_still_enter_transfer(void)
{
    enter_transfer();
    usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    usb_mode_handle(USB_MODE_EVENT_REMOUNT_FAIL);
    usb_mode_handle(USB_MODE_EVENT_USB_CONNECTED);
    usb_mode_actions_t a = usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    CHECK(usb_mode_get_state() == USB_MODE_TRANSFER);
    CHECK(a.count == 2);
    CHECK(a.actions[0] == USB_MODE_ACTION_START_MSC);
    CHECK(a.actions[1] == USB_MODE_ACTION_LED_TRANSFER);
}

static void test_card_fail_without_usb_stays_put(void)
{
    enter_transfer();
    usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    usb_mode_handle(USB_MODE_EVENT_REMOUNT_FAIL);
    usb_mode_handle(USB_MODE_EVENT_USB_DISCONNECTED);
    usb_mode_actions_t a = usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    CHECK(usb_mode_get_state() == USB_MODE_CARD_FAIL);
    CHECK(a.count == 0);
}

/* Unplugged while the unmount was still in flight: the host is gone, so there
 * is nothing to present. Remount and go back to capturing rather than starting
 * MSC for an absent host. */
static void test_unplug_during_entering_skips_msc(void)
{
    usb_mode_init();
    usb_mode_handle(USB_MODE_EVENT_USB_CONNECTED);
    usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    usb_mode_handle(USB_MODE_EVENT_USB_DISCONNECTED);
    usb_mode_actions_t a = usb_mode_handle(USB_MODE_EVENT_UNMOUNT_OK);
    CHECK(usb_mode_get_state() == USB_MODE_LEAVING);
    CHECK(a.count == 1);
    CHECK(a.actions[0] == USB_MODE_ACTION_REMOUNT_FS);
}

static void test_poweroff_only_allowed_in_capture(void)
{
    usb_mode_init();
    CHECK(usb_mode_allows_poweroff());

    usb_mode_handle(USB_MODE_EVENT_USB_CONNECTED);
    usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    CHECK(!usb_mode_allows_poweroff()); /* ENTERING */

    usb_mode_handle(USB_MODE_EVENT_UNMOUNT_OK);
    CHECK(!usb_mode_allows_poweroff()); /* TRANSFER */

    usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    CHECK(!usb_mode_allows_poweroff()); /* LEAVING */

    usb_mode_handle(USB_MODE_EVENT_REMOUNT_FAIL);
    CHECK(!usb_mode_allows_poweroff()); /* CARD_FAIL */

    usb_mode_init();
    usb_mode_handle(USB_MODE_EVENT_USB_CONNECTED);
    usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    usb_mode_handle(USB_MODE_EVENT_UNMOUNT_OK);
    usb_mode_handle(USB_MODE_EVENT_DOUBLE_TAP);
    usb_mode_handle(USB_MODE_EVENT_REMOUNT_OK);
    CHECK(usb_mode_allows_poweroff()); /* back in CAPTURE */
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `./omi/firmware/scripts/run-host-tests.sh`
Expected: FAIL on the six new tests. `test_poweroff_only_allowed_in_capture` will fail only on its `CARD_FAIL` line and later, since the earlier states already exist.

- [ ] **Step 3: Write the implementation**

In `usb_mode.c`, extend the `ENTERING` case and add `LEAVING`'s failure branch and the `CARD_FAIL` case:

```c
    case USB_MODE_ENTERING:
        if (event == USB_MODE_EVENT_UNMOUNT_OK) {
            if (usb_connected) {
                state = USB_MODE_TRANSFER;
                push(&a, USB_MODE_ACTION_START_MSC);
                push(&a, USB_MODE_ACTION_LED_TRANSFER);
            } else {
                /* Host went away mid-unmount. Nothing to present. */
                state = USB_MODE_LEAVING;
                push(&a, USB_MODE_ACTION_REMOUNT_FS);
            }
        } else if (event == USB_MODE_EVENT_UNMOUNT_FAIL) {
            state = USB_MODE_CAPTURE;
            push(&a, USB_MODE_ACTION_RESUME_CAPTURE);
            push(&a, USB_MODE_ACTION_LED_CARD_FAIL);
        }
        break;
```

```c
    case USB_MODE_LEAVING:
        if (event == USB_MODE_EVENT_REMOUNT_OK) {
            state = USB_MODE_CAPTURE;
            push(&a, USB_MODE_ACTION_RESUME_CAPTURE);
            push(&a, USB_MODE_ACTION_LED_CAPTURE);
        } else if (event == USB_MODE_EVENT_REMOUNT_FAIL) {
            state = USB_MODE_CARD_FAIL;
            push(&a, USB_MODE_ACTION_LED_CARD_FAIL);
        }
        break;

    case USB_MODE_CARD_FAIL:
        /* Nothing is mounted, so entry needs no unmount step. This is the
         * recovery path: present the card so the host can reformat it. */
        if (event == USB_MODE_EVENT_DOUBLE_TAP && usb_connected) {
            state = USB_MODE_TRANSFER;
            push(&a, USB_MODE_ACTION_START_MSC);
            push(&a, USB_MODE_ACTION_LED_TRANSFER);
        }
        break;
```

Remove the now-unreachable `default:` label only if the compiler is satisfied every enum value is handled; `-Werror` with `-Wswitch` will tell you.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `./omi/firmware/scripts/run-host-tests.sh`
Expected: PASS, all tests including every earlier one.

- [ ] **Step 5: Commit**

```bash
git add omi/firmware/devkit/src/usb_mode.c omi/firmware/devkit/tests/test_usb_mode.c
git commit -m "feat(omi): usb_mode failure paths, card-fail recovery, poweroff gate"
```

---

## Task 5: The invariant test

**Files:**
- Modify: `omi/firmware/devkit/tests/test_usb_mode.c`

**Interfaces:**
- Consumes: the finished state machine.
- Produces: no new symbols. This is the test the whole module exists for, and it is deliberately separate so a reviewer can see it on its own.

Every test so far checks one transition. This one checks the property that must hold across *all* of them: the filesystem and MSC are never both live. It drives a long deterministic event sequence through the machine while maintaining a shadow model of what is mounted and running, and asserts after every single event.

- [ ] **Step 1: Write the test**

Add to `test_usb_mode.c`, and add the call to `main`:

```c
/*
 * The invariant from design section 9.3: FatFs and USB Mass Storage are never
 * active at the same time. Drives a long event sequence and checks after every
 * event. If this ever fails, the card can be corrupted on real hardware.
 */
static void test_never_both_mounted_and_msc(void)
{
    /* A fixed, deliberately awkward sequence: repeated taps, unplugs at every
     * phase, failures interleaved with successes. */
    static const usb_mode_event_t seq[] = {
        USB_MODE_EVENT_DOUBLE_TAP,       USB_MODE_EVENT_USB_CONNECTED,
        USB_MODE_EVENT_DOUBLE_TAP,       USB_MODE_EVENT_DOUBLE_TAP,
        USB_MODE_EVENT_UNMOUNT_OK,       USB_MODE_EVENT_DOUBLE_TAP,
        USB_MODE_EVENT_REMOUNT_OK,       USB_MODE_EVENT_DOUBLE_TAP,
        USB_MODE_EVENT_UNMOUNT_FAIL,     USB_MODE_EVENT_DOUBLE_TAP,
        USB_MODE_EVENT_UNMOUNT_OK,       USB_MODE_EVENT_USB_DISCONNECTED,
        USB_MODE_EVENT_REMOUNT_FAIL,     USB_MODE_EVENT_DOUBLE_TAP,
        USB_MODE_EVENT_USB_CONNECTED,    USB_MODE_EVENT_DOUBLE_TAP,
        USB_MODE_EVENT_USB_DISCONNECTED, USB_MODE_EVENT_REMOUNT_OK,
        USB_MODE_EVENT_DOUBLE_TAP,       USB_MODE_EVENT_USB_CONNECTED,
        USB_MODE_EVENT_DOUBLE_TAP,       USB_MODE_EVENT_UNMOUNT_OK,
        USB_MODE_EVENT_REMOUNT_OK,       USB_MODE_EVENT_DOUBLE_TAP,
    };

    /* Shadow model of the real world, updated only by emitted actions. */
    bool fs_mounted = true;
    bool msc_running = false;

    usb_mode_init();

    for (unsigned i = 0; i < sizeof(seq) / sizeof(seq[0]); i++) {
        usb_mode_actions_t a = usb_mode_handle(seq[i]);

        for (uint8_t j = 0; j < a.count; j++) {
            switch (a.actions[j]) {
            case USB_MODE_ACTION_UNMOUNT_FS:
                fs_mounted = false;
                break;
            case USB_MODE_ACTION_REMOUNT_FS:
                /* Must never be asked to remount while the host has the card */
                CHECK(!msc_running);
                fs_mounted = true;
                break;
            case USB_MODE_ACTION_START_MSC:
                /* The invariant. */
                CHECK(!fs_mounted);
                msc_running = true;
                break;
            case USB_MODE_ACTION_STOP_MSC:
                msc_running = false;
                break;
            default:
                break;
            }
        }

        CHECK(!(fs_mounted && msc_running));
    }
}
```

Note the shadow model treats `REMOUNT_FS` as succeeding. That is deliberate: the failure case is covered by Task 4, and modelling it here would test the test rather than the machine.

- [ ] **Step 2: Run to verify it passes, then prove it can fail**

Run: `./omi/firmware/scripts/run-host-tests.sh`
Expected: PASS.

This test passes on first write, which makes it exactly the kind of test that might be asserting nothing. Prove otherwise: temporarily change the `USB_MODE_CARD_FAIL` case in `usb_mode.c` to push `USB_MODE_ACTION_START_MSC` **without** the state machine having unmounted - for example by changing `test_card_fail_can_still_enter_transfer`'s path so `START_MSC` is emitted from `USB_MODE_CAPTURE`:

```c
    case USB_MODE_CAPTURE:
        if (event == USB_MODE_EVENT_DOUBLE_TAP && usb_connected) {
            state = USB_MODE_TRANSFER;              /* deliberately wrong */
            push(&a, USB_MODE_ACTION_START_MSC);    /* no unmount first   */
        }
        break;
```

Re-run and confirm you see `FAIL ... !fs_mounted`. Then revert and confirm the suite is green again.

- [ ] **Step 3: Commit**

```bash
git add omi/firmware/devkit/tests/test_usb_mode.c
git commit -m "test(omi): assert fs and msc are never both live"
```

---

## Task 6: Add `unmount_sd_card()`

**Files:**
- Modify: `omi/firmware/devkit/src/sdcard.h`
- Modify: `omi/firmware/devkit/src/sdcard.c`

**Interfaces:**
- Consumes: the existing static `mount_point` in `sdcard.c`.
- Produces: `int unmount_sd_card(void)` - returns 0 on success, negative errno on failure. Task 8 calls it.

This is firmware code with no host test. Keep it minimal and obviously correct; it is verified by the hardware checklist in Task 10.

- [ ] **Step 1: Declare it**

Add to `omi/firmware/devkit/src/sdcard.h`, immediately after the `mount_sd_card` declaration:

```c
/**
 * @brief Unmount the SD card so USB Mass Storage can take over the block device.
 *
 * The card stays powered - MSC needs it. Only the filesystem is released.
 *
 * @return 0 if successful, negative errno code if error
 */
int unmount_sd_card(void);
```

- [ ] **Step 2: Implement it**

Add to `omi/firmware/devkit/src/sdcard.c`, directly below `mount_sd_card()`:

```c
int unmount_sd_card(void)
{
    /* Deliberately does NOT call sd_off(): USB Mass Storage serves the same
     * physical card and needs it powered. Only the filesystem is released, so
     * the host can own the block device. See docs/09-usb-transfer-mode-design.md.
     */
    int res = fs_unmount(&mount_point);
    if (res != 0) {
        LOG_ERR("fs_unmount failed: %d", res);
        return res;
    }

    LOG_INF("SD card unmounted for USB transfer");
    return 0;
}
```

- [ ] **Step 3: Verify it compiles**

Run: `./omi/firmware/scripts/build-docker.sh`
Expected: build completes and `omi/firmware/build/docker_build/zephyr.uf2` is regenerated. Nothing calls `unmount_sd_card()` yet, so behaviour is unchanged - this step only proves it compiles and links.

- [ ] **Step 4: Commit**

```bash
git add omi/firmware/devkit/src/sdcard.h omi/firmware/devkit/src/sdcard.c
git commit -m "feat(omi): add unmount_sd_card for USB transfer mode"
```

---

## Task 7: USB composite device - mass storage and CDC

**Files:**
- Modify: `omi/firmware/devkit/prj_xiao_ble_sense_devkitv2-adafruit.conf`
- Modify: `omi/firmware/devkit/src/usb.h`
- Modify: `omi/firmware/devkit/src/usb.c`

**Interfaces:**
- Consumes: the existing `init_usb()` and `usb_charge` in `usb.c`.
- Produces: `int usb_msc_start(void)` and `int usb_msc_stop(void)`, both returning 0 on success and negative errno on failure. Task 8 calls them.

The existing `usb.c` already calls `usb_enable()` for charge detection; this extends that rather than replacing it. Stay on the legacy `usb_device` stack - see design section 9.10.

- [ ] **Step 1: Add the configuration**

Add to `omi/firmware/devkit/prj_xiao_ble_sense_devkitv2-adafruit.conf`, near the existing `CONFIG_OMI_ENABLE_USB=y` line:

```
# USB transfer mode: expose the SD card as a drive, plus a serial channel.
# The disk name must match the one sdcard.c passes to disk_access_init().
CONFIG_USB_DEVICE_STACK=y
CONFIG_USB_MASS_STORAGE=y
CONFIG_MASS_STORAGE_DISK_NAME="SD"
CONFIG_USB_CDC_ACM=y
CONFIG_USB_COMPOSITE_DEVICE=y
```

Leave `CONFIG_FS_FATFS_MOUNT_MKFS=n` exactly as it is.

- [ ] **Step 2: Declare the new functions**

Add to `omi/firmware/devkit/src/usb.h`:

```c
/**
 * @brief Present the SD card to the host as a USB Mass Storage device.
 *
 * The caller MUST have unmounted the filesystem first. Starting MSC while
 * FatFs is mounted corrupts the card.
 *
 * @return 0 if successful, negative errno code if error
 */
int usb_msc_start(void);

/**
 * @brief Stop presenting the card to the host.
 *
 * @return 0 if successful, negative errno code if error
 */
int usb_msc_stop(void);
```

- [ ] **Step 3: Implement them**

Add to `omi/firmware/devkit/src/usb.c`:

```c
static bool msc_active;

int usb_msc_start(void)
{
    if (msc_active) {
        return 0;
    }
    /* The mass storage class is bound at build time via
     * CONFIG_MASS_STORAGE_DISK_NAME. Enabling the device stack is what makes it
     * visible to the host; the filesystem must already be unmounted. */
    msc_active = true;
    LOG_INF("USB mass storage active");
    return 0;
}

int usb_msc_stop(void)
{
    if (!msc_active) {
        return 0;
    }
    msc_active = false;
    LOG_INF("USB mass storage stopped");
    return 0;
}
```

**Implementer's note:** Zephyr's legacy mass-storage class is enabled for the whole lifetime of the USB device once `CONFIG_USB_MASS_STORAGE=y`, so the host may see the drive whenever USB is enumerated. If it does, the invariant is enforced by the *filesystem* side rather than by MSC start/stop, and these two functions become bookkeeping. Confirm the actual behaviour on hardware in Task 10 step 3. If the drive appears without a double-tap, the fix is to keep `usb_disable()`/`usb_enable()` bracketing transfer mode inside these two functions - do not weaken the unmount ordering to compensate.

- [ ] **Step 4: Leave the console off, deliberately**

Design section 9.5 says CDC "serves as console and as a command channel", and 9.10 flags the
consequence: runbook section 8.8 records that enabling logging alongside offline storage makes
the log thread contend with SD writes, and needs its priority dropped.

**This task adds the CDC transport but does not turn the console on.** Do **not** add
`CONFIG_CONSOLE=y`, `CONFIG_LOG=y` or `CONFIG_UART_CONSOLE=y`. Reasons: the contention question
is unresolved, transfer mode must be trustworthy before logging is layered on top of it, and
nothing in this sub-project needs to read a log to work. Sub-project B, which actually needs a
command channel, is where that decision belongs.

Confirm no console symbols were added:

```bash
grep -n "CONFIG_CONSOLE\|CONFIG_LOG=\|CONFIG_UART_CONSOLE" \
    omi/firmware/devkit/prj_xiao_ble_sense_devkitv2-adafruit.conf
```

Expected: the existing commented-out lines only, unchanged.

Because the console stays off, `CONFIG_UART_CONSOLE` remains unset, so the existing
`#ifndef CONFIG_UART_CONSOLE` branch in `init_usb()` keeps taking its current path - the
`usb_disable()` / `usb_enable()` pair. **Leave that branch exactly as it is.** Design section 9.5
notes it needs revisiting when the console moves to CDC; that is sub-project B's problem, and
changing it now would alter USB bring-up underneath the feature being built. Add a comment above
it recording why it was left alone:

```c
    /* Left as-is deliberately. The console is not on CDC yet (see the plan for
     * sub-project A, task 7), so this branch still behaves as it always has.
     * Revisit when sub-project B enables the console. */
```

- [ ] **Step 5: Verify it builds**

Run: `./omi/firmware/scripts/build-docker.sh`
Expected: build completes. If Kconfig reports an unknown symbol, check it against the SDK: `grep -rn "config USB_MASS_STORAGE" omi/firmware/v2.7.0/zephyr/subsys/usb/`

- [ ] **Step 6: Commit**

```bash
git add omi/firmware/devkit/prj_xiao_ble_sense_devkitv2-adafruit.conf \
        omi/firmware/devkit/src/usb.h omi/firmware/devkit/src/usb.c
git commit -m "feat(omi): bring USB up as a composite mass-storage and CDC device"
```

---

## Task 8: Wire the state machine into the firmware

**Files:**
- Modify: `omi/firmware/devkit/CMakeLists.txt`
- Modify: `omi/firmware/devkit/src/main.c`
- Modify: `omi/firmware/devkit/src/button.c`
- Modify: `omi/firmware/devkit/src/usb.c`

**Interfaces:**
- Consumes: `usb_mode_*` (Task 1-4), `unmount_sd_card()` (Task 6), `usb_msc_start()`/`usb_msc_stop()` (Task 7), and the existing `mount_sd_card()`, `mic_on()`, `mic_off()`.
- Produces: `void usb_mode_dispatch(usb_mode_event_t event)` in `main.c`, declared in `main.h` if one exists or as an `extern` in `button.c` and `usb.c` following the file's existing `extern bool usb_charge;` pattern.

- [ ] **Step 1: Add the source file to the build**

In `omi/firmware/devkit/CMakeLists.txt`, add to the `target_sources(app PRIVATE ...)` list, after `src/usb.c`:

```cmake
    src/usb_mode.c
```

- [ ] **Step 2: Write the dispatcher**

Add to `omi/firmware/devkit/src/main.c`, above `set_led_state()`. Include `"usb_mode.h"`, `"sdcard.h"` and `"mic.h"` at the top of the file if they are not already included:

```c
/*
 * Performs the actions the state machine asks for, and feeds back the results
 * of the ones that can fail. Ordering is the state machine's business, not
 * ours - do exactly what it says, in the order it says.
 */
void usb_mode_dispatch(usb_mode_event_t event)
{
    usb_mode_actions_t a = usb_mode_handle(event);

    for (uint8_t i = 0; i < a.count; i++) {
        switch (a.actions[i]) {
        case USB_MODE_ACTION_STOP_CAPTURE:
            mic_off();
            break;

        case USB_MODE_ACTION_RESUME_CAPTURE:
            mic_on();
            break;

        case USB_MODE_ACTION_UNMOUNT_FS:
            if (unmount_sd_card() == 0) {
                usb_mode_dispatch(USB_MODE_EVENT_UNMOUNT_OK);
            } else {
                usb_mode_dispatch(USB_MODE_EVENT_UNMOUNT_FAIL);
            }
            break;

        case USB_MODE_ACTION_REMOUNT_FS:
            if (mount_sd_card() == 0) {
                usb_mode_dispatch(USB_MODE_EVENT_REMOUNT_OK);
            } else {
                usb_mode_dispatch(USB_MODE_EVENT_REMOUNT_FAIL);
            }
            break;

        case USB_MODE_ACTION_START_MSC:
            usb_msc_start();
            break;

        case USB_MODE_ACTION_STOP_MSC:
            usb_msc_stop();
            break;

        case USB_MODE_ACTION_LED_CAPTURE:
        case USB_MODE_ACTION_LED_TRANSFER:
        case USB_MODE_ACTION_LED_CARD_FAIL:
            /* Handled in Task 9. */
            break;
        }
    }
}
```

The recursion is bounded: `UNMOUNT_FS` and `REMOUNT_FS` are only ever emitted from states that cannot emit them again in response to the result event. It never nests more than two deep.

- [ ] **Step 3: Initialise the state machine at boot**

In `main()`, immediately before the `while (1)` main loop:

```c
    usb_mode_init();
```

- [ ] **Step 4: Feed USB connection events**

In `omi/firmware/devkit/src/usb.c`, inside `udc_status_cb`, alongside the existing `usb_charge` assignments. Declare `extern void usb_mode_dispatch(usb_mode_event_t event);` near the top of the file, and include `"usb_mode.h"`:

```c
    case USB_DC_CONNECTED:
        usb_charge = true;
        usb_mode_dispatch(USB_MODE_EVENT_USB_CONNECTED);
        break;
    case USB_DC_DISCONNECTED:
        usb_charge = false;
        usb_mode_dispatch(USB_MODE_EVENT_USB_DISCONNECTED);
        break;
```

- [ ] **Step 5: Route double-tap, and gate power-off**

In `omi/firmware/devkit/src/button.c`, include `"usb_mode.h"` and declare `extern void usb_mode_dispatch(usb_mode_event_t event);`.

Change the double-tap branch so it also drives the mode:

```c
    // Double tap
    if (event == BUTTON_EVENT_DOUBLE_TAP) {
        LOG_PRINTK("double tap detected\n");
        btn_last_event = event;
        notify_double_tap();
        usb_mode_dispatch(USB_MODE_EVENT_DOUBLE_TAP);
    }
```

Change the long-press branch so it refuses to power down mid-transfer:

```c
    // Long press, one time event
    if (event == BUTTON_EVENT_LONG_PRESS && btn_last_event != BUTTON_EVENT_LONG_PRESS) {
        LOG_PRINTK("long press detected\n");
        btn_last_event = event;

        if (!usb_mode_allows_poweroff()) {
            /* Powering down now would pull storage out from under the host. */
            LOG_WRN("power off ignored: USB transfer in progress");
        } else {
            // Enter the low power mode
            is_off = true;
            bt_off();
            turnoff_all();
        }
    }
```

- [ ] **Step 6: Verify it builds**

Run: `./omi/firmware/scripts/build-docker.sh`
Expected: build completes and produces `zephyr.uf2`.

- [ ] **Step 7: Verify the host tests still pass**

Run: `./omi/firmware/scripts/run-host-tests.sh`
Expected: PASS. The state machine was not changed, but this guards against an accidental edit to `usb_mode.c` while wiring.

- [ ] **Step 8: Commit**

```bash
git add omi/firmware/devkit/CMakeLists.txt omi/firmware/devkit/src/main.c \
        omi/firmware/devkit/src/button.c omi/firmware/devkit/src/usb.c
git commit -m "feat(omi): wire transfer mode into button, USB and storage"
```

---

## Task 9: The transfer-mode LED

**Files:**
- Modify: `omi/firmware/devkit/src/main.c`

**Interfaces:**
- Consumes: `usb_mode_get_state()`, and the existing `set_led_red/green/blue`.
- Produces: no new public symbols.

Design section 9.4: transfer mode is **blinking blue with red and green forced off**. No colour is free, so `set_led_state()` must be suppressed while not in `USB_MODE_CAPTURE` - it runs every 500 ms and would otherwise overwrite the pattern on the next tick.

- [ ] **Step 1: Suppress the normal LED machine and add the blink**

In `omi/firmware/devkit/src/main.c`, add at the very top of `set_led_state()`, before anything else:

```c
    /* The mode owns the LED whenever we are not capturing. Without this, the
     * 500 ms tick overwrites the transfer pattern immediately. */
    if (usb_mode_get_state() != USB_MODE_CAPTURE) {
        static bool transfer_blink;

        if (usb_mode_get_state() == USB_MODE_TRANSFER) {
            transfer_blink = !transfer_blink;
            set_led_blue(transfer_blink);
        } else {
            set_led_blue(false);
        }
        set_led_red(false);
        set_led_green(false);
        return;
    }
```

The main loop already ticks every 500 ms, so toggling once per call gives a 1 Hz blink with no new timer.

- [ ] **Step 2: Make the card-fail action blink**

Still in `main.c`, replace the `USB_MODE_ACTION_LED_CARD_FAIL` placeholder in `usb_mode_dispatch` from Task 8:

```c
        case USB_MODE_ACTION_LED_CARD_FAIL:
            for (int b = 0; b < 6; b++) {
                set_led_red(true);
                k_msleep(150);
                set_led_red(false);
                k_msleep(150);
            }
            break;

        case USB_MODE_ACTION_LED_CAPTURE:
        case USB_MODE_ACTION_LED_TRANSFER:
            /* set_led_state() reads the mode on its next tick. */
            break;
```

- [ ] **Step 3: Verify it builds**

Run: `./omi/firmware/scripts/build-docker.sh`
Expected: build completes.

- [ ] **Step 4: Commit**

```bash
git add omi/firmware/devkit/src/main.c
git commit -m "feat(omi): blinking blue LED for transfer mode"
```

---

## Task 10: Hardware verification and documentation

**Files:**
- Modify: `omi/firmware/docs/08-build-and-flash-runbook.md`

**Interfaces:**
- Consumes: the flashed firmware.
- Produces: the manual checklist that design section 9.8 promises.

- [ ] **Step 1: Build and flash**

```bash
./omi/firmware/scripts/build-docker.sh
```

Then flash per runbook section 8.5: double-tap reset, find the bootloader volume, copy the UF2.

```bash
for d in /[a-z]; do [ -f "$d/INFO_UF2.TXT" ] && echo "bootloader drive: $d"; done
```

- [ ] **Step 2: Work through the checklist on hardware**

Record the actual result of each, including anything that differs from the design:

1. Boot with a good card: normal boot LED sequence, no six-blink.
2. Plug into a PC **without** double-tapping: no drive appears on the host, the charging LED behaves as before, and recording continues (confirm `a01.txt` grows).
3. Double-tap while connected: recording stops, a drive appears, LED blinks blue with red and green off.
4. Copy `a01.txt` off the drive. Confirm it is readable and that `omi-sync --scan-only` parses it.
5. Format the card from the host while in transfer mode. Confirm it succeeds.
6. Double-tap again: drive disappears, capture resumes, LED returns to normal.
7. Enter transfer mode and **unplug** without double-tapping: capture resumes.
8. Long-press while in transfer mode: the device must **not** power off.
9. Enter transfer mode with a card the firmware cannot mount (an unformatted scratch card): the drive must still appear, so it can be formatted from the host. This is the recovery path from design section 9.6.
10. After a host format, confirm the device mounts it and records again.
11. **Flat battery.** Design section 9.10 lists this as unconsidered: run the battery down, then connect to a PC and observe what happens as the host enumerates a device that is simultaneously charging from empty. Record what you see even if it is uneventful - the risk is only closed by looking.
12. **Transfer speed.** Time a copy of a file of known size and record the observed rate. Design section 9.3 predicts roughly 1 MB/s from USB Full Speed. If it is drastically slower, say so in the runbook rather than leaving the prediction standing.

- [ ] **Step 3: Resolve the Task 7 open question**

Step 2 item 2 answers it. If a drive appears on the host **without** a double-tap, Zephyr is exposing mass storage for the whole USB lifetime, and `usb_msc_start`/`usb_msc_stop` must bracket transfer mode with `usb_disable()`/`usb_enable()` instead of setting a flag. Fix it in `usb.c`, rebuild, and re-run items 2, 3 and 6. **Do not** weaken the unmount ordering to work around it.

- [ ] **Step 4: Write the checklist into the runbook**

Add a new section `## 8.14 Verifying USB transfer mode` to `omi/firmware/docs/08-build-and-flash-runbook.md`, containing the twelve checks above **with the results you actually observed**. Follow the file's existing conventions: ASCII only, plain hyphens, CRLF line endings, and mark anything you could not test `[unverified]`.

- [ ] **Step 5: Update the design document status**

Change the status line at the top of `omi/firmware/docs/09-usb-transfer-mode-design.md` from `**Status: designed, not built.**` to a line recording that it is built and verified, with the date, in the style of runbook section 8.13.

- [ ] **Step 6: Commit**

```bash
git add omi/firmware/docs/08-build-and-flash-runbook.md \
        omi/firmware/docs/09-usb-transfer-mode-design.md
git commit -m "docs(omi): record USB transfer mode hardware verification"
```

- [ ] **Step 7: Open the pull request**

```bash
git push -u origin <branch>
```

The PR body must state: **`omi/` only - excluded from the release checklist**, and the deployment surface (**neither** a desktop release nor a server redeploy - no Diariz deployable is touched). If this fixes a GitHub issue, include `Fixes #<n>` on its own line.

---

## Verification Summary

| Layer | How | When |
|---|---|---|
| State machine | `./omi/firmware/scripts/run-host-tests.sh` | Every task, 1-5 and 8 |
| Compiles and links | `./omi/firmware/scripts/build-docker.sh` | Tasks 6-9 |
| Real behaviour | The twelve-item checklist | Task 10 |

The state machine tests are the only automated tests this firmware has. Do not let them go red, and do not weaken an assertion to make one pass - if a test fails after a wiring change, the wiring is wrong.
