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

    if (failures) {
        printf("%d check(s) failed\n", failures);
        return 1;
    }
    printf("all checks passed\n");
    return 0;
}
