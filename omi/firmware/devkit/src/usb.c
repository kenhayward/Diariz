#include <zephyr/pm/pm.h>
// #include <zephyr/usb/usb_device.h>
// #include <zephyr/drivers/usb/usb_dc.h>
// #include <zephyr/drivers/usb/device/usb_dc_nrfx.c>
#include <zephyr/logging/log.h>
#include <zephyr/usb/usb_device.h>

#include "speaker.h"
#include "transport.h"
#include "usb.h"
#include "usb_mode.h"
LOG_MODULE_REGISTER(usb, CONFIG_LOG_DEFAULT_LEVEL);

// add all device drivers here?
bool usb_charge = false;

/* Defined in main.c, which owns performing the actions. */
extern void usb_mode_dispatch(usb_mode_event_t event);

usb_dc_status_callback udc_status_cb(enum usb_dc_status_code status, const uint8_t *param)
{
    switch (status) {
    case USB_DC_CONNECTED:
        usb_charge = true;
        usb_mode_dispatch(USB_MODE_EVENT_USB_CONNECTED);
        break;
    case USB_DC_DISCONNECTED:
        usb_charge = false;
        usb_mode_dispatch(USB_MODE_EVENT_USB_DISCONNECTED);
        break;
    default:
        usb_charge = true;
    }

    return;
}

int init_usb()
{
/* Left as-is deliberately. The console is not on CDC yet (see the plan for
 * sub-project A, task 7), so this branch still behaves as it always has.
 * Revisit when sub-project B enables the console. */
#ifndef CONFIG_UART_CONSOLE
    usb_disable();
    int ret = usb_enable(udc_status_cb);
    LOG_INF("USB ret: %d\n", ret);
#else
    // Use this instead of the disable/enable lines above
    // as USB disabling messes up the UART logging
    usb_dc_set_status_callback(udc_status_cb);
#endif
    return 0;
}

static bool msc_active;

int usb_msc_start(void)
{
    if (msc_active) {
        return 0;
    }
    /* The mass storage class is bound at build time via
     * CONFIG_MASS_STORAGE_DISK_NAME. The caller must already have unmounted the
     * filesystem - see usb_mode.c, which enforces that ordering. */
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
