#include <zephyr/pm/pm.h>
// #include <zephyr/usb/usb_device.h>
#include <hal/nrf_power.h>
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
    /* Charge state and mode events come from VBUS (usb_poll_vbus), not from
     * here: this callback only fires while the device stack is enabled, which
     * is now only during transfer mode. */
    case USB_DC_CONNECTED:
        LOG_INF("USB enumerated by host");
        break;
    case USB_DC_DISCONNECTED:
        LOG_INF("USB de-enumerated");
        break;
    default:
        break;
    }

    return;
}

int init_usb()
{
    /*
     * The USB device stack is deliberately NOT enabled here.
     *
     * With Zephyr's legacy stack every configured class becomes visible the
     * moment the device enumerates, so enabling it at boot exposed the SD card
     * as a drive on every plug-in - while the firmware still had FatFs mounted
     * and was appending audio to it. That is the exact both-owners-at-once case
     * the design forbids. Observed on hardware, 2026-08-26.
     *
     * The stack is now enabled only by usb_msc_start(), on an explicit
     * double-tap, and disabled again on the way out. Charge detection no longer
     * depends on it: usb_vbus_present() reads VBUS straight from the POWER
     * peripheral, which needs no USB stack at all.
     */
    return 0;
}

/* True while the host is supplying VBUS. Independent of the USB device stack,
 * so it works while the stack is disabled - which is most of the time. */
bool usb_vbus_present(void)
{
    return nrf_power_usbregstatus_vbusdet_get(NRF_POWER);
}

/*
 * Called from the main loop tick. Edge-detects VBUS and is the single source
 * of both the charging flag and the state machine's connection events.
 */
void usb_poll_vbus(void)
{
    static bool vbus_last;
    bool vbus = usb_vbus_present();

    if (vbus == vbus_last) {
        return;
    }
    vbus_last = vbus;
    usb_charge = vbus;

    usb_mode_dispatch(vbus ? USB_MODE_EVENT_USB_CONNECTED
                           : USB_MODE_EVENT_USB_DISCONNECTED);
}

static bool msc_active;

int usb_msc_start(void)
{
    if (msc_active) {
        return 0;
    }
    /* Enabling the device stack is what makes the card visible to the host.
     * The caller must already have unmounted the filesystem - usb_mode.c
     * enforces that ordering. */
    int ret = usb_enable(udc_status_cb);
    if (ret) {
        LOG_ERR("usb_enable failed: %d", ret);
        return ret;
    }
    msc_active = true;
    LOG_INF("USB mass storage active");
    return 0;
}

int usb_msc_stop(void)
{
    if (!msc_active) {
        return 0;
    }
    int ret = usb_disable();
    if (ret) {
        LOG_ERR("usb_disable failed: %d", ret);
        return ret;
    }
    msc_active = false;
    LOG_INF("USB mass storage stopped");
    return 0;
}
