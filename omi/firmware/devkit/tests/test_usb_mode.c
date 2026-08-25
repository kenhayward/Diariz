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
