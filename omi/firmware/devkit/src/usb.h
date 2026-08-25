#ifndef USB_H
#define USB_H

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

#endif