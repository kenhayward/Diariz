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

/* Shared by the tests below and in later groups. */
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

    /*
     * Shadow model of the real world.
     *
     * The filesystem changes state on the RESULT event, not on the request
     * action: emitting UNMOUNT_FS only asks for an unmount, and UNMOUNT_FAIL
     * means it is still mounted. Modelling it on the action instead would
     * quietly assume every unmount and remount succeeds, which is exactly the
     * assumption the two-phase design exists to avoid.
     *
     * Result events are applied only when the machine was actually awaiting
     * one. The sequence deliberately feeds some spurious results - a REMOUNT_OK
     * while in TRANSFER, say - to prove the machine ignores them; the model has
     * to ignore them too or it drifts out of step with reality.
     */
    bool fs_mounted = true;
    bool msc_running = false;

    usb_mode_init();

    for (unsigned i = 0; i < sizeof(seq) / sizeof(seq[0]); i++) {
        usb_mode_state_t before = usb_mode_get_state();
        usb_mode_actions_t a = usb_mode_handle(seq[i]);

        if (before == USB_MODE_ENTERING && seq[i] == USB_MODE_EVENT_UNMOUNT_OK) {
            fs_mounted = false;
        }
        if (before == USB_MODE_LEAVING && seq[i] == USB_MODE_EVENT_REMOUNT_OK) {
            fs_mounted = true;
        }

        for (uint8_t j = 0; j < a.count; j++) {
            switch (a.actions[j]) {
            case USB_MODE_ACTION_REMOUNT_FS:
                /* Must never be asked to remount while the host has the card */
                CHECK(!msc_running);
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

int main(void)
{
    test_starts_in_capture();
    test_double_tap_without_usb_does_nothing();
    test_double_tap_with_usb_enters();
    test_unmount_ok_starts_msc();
    test_double_tap_while_entering_is_ignored();
    test_usb_connect_alone_does_not_enter();
    test_double_tap_leaves_transfer();
    test_unplug_leaves_transfer();
    test_remount_ok_resumes_capture();
    test_double_tap_while_leaving_is_ignored();
    test_unmount_fail_returns_to_capture();
    test_remount_fail_enters_card_fail();
    test_card_fail_can_still_enter_transfer();
    test_card_fail_without_usb_stays_put();
    test_unplug_during_entering_skips_msc();
    test_poweroff_only_allowed_in_capture();
    test_never_both_mounted_and_msc();

    if (failures) {
        printf("%d check(s) failed\n", failures);
        return 1;
    }
    printf("all checks passed\n");
    return 0;
}
