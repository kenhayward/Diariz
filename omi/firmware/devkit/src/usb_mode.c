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

static void push(usb_mode_actions_t *a, usb_mode_action_t action)
{
    if (a->count < USB_MODE_MAX_ACTIONS) {
        a->actions[a->count++] = action;
    }
}

usb_mode_actions_t usb_mode_handle(usb_mode_event_t event)
{
    usb_mode_actions_t a = none();

    if (event == USB_MODE_EVENT_USB_CONNECTED) {
        usb_connected = true;
    }
    if (event == USB_MODE_EVENT_USB_DISCONNECTED) {
        usb_connected = false;
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
    }

    return a;
}
