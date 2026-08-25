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
    USB_MODE_ACTION_LED_CAPTURE,   /* Hand the LED back to set_led_state()    */
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
