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
