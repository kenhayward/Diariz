#ifndef USB_H
#define USB_H

#include <stdbool.h>

int init_usb();

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

/**
 * @brief True while the host is supplying VBUS.
 *
 * Reads the POWER peripheral directly, so it works with the USB device stack
 * disabled - which is the normal state outside transfer mode.
 */
bool usb_vbus_present(void);

/**
 * @brief Edge-detect VBUS and drive the charge flag and mode events.
 *
 * Call from the main loop tick.
 */
void usb_poll_vbus(void);

#endif