---
title: Syncing your desktop Outlook calendar
summary: Copy the calendar from classic Outlook on your Windows PC into Diariz, so those meetings show on the Calendar tab and match your recordings. It is off until you turn it on, and turning it off deletes what was copied.
group: settings
order: 40
---

If your calendar lives in Outlook on your PC rather than in Google, Diariz can copy it across. Those
meetings then behave like any other: they show on the Calendar tab, a recording is matched to the
meeting it came from, and you can jot notes on one before it starts.

## Turning it on

Open **Preferences** from the account menu, then the **Calendars** tab. Find the **Desktop Outlook**
card and tick **Mirror enabled**. Nothing is copied until you do.

The tab works from any browser, so you can check which machines are syncing and turn it off from
anywhere. The copying itself only happens in the Diariz desktop app on Windows.

The same tab holds every other calendar source too - your Google account and any .ics feeds you have
subscribed to - one card each.

## What gets stored, and where

Meeting titles, times, locations, attendees and the invite text are stored on your Diariz server.
That is what lets those meetings still be there when you open Diariz in a browser, or after you have
closed the desktop app.

Two things are skipped by default, and you can change both per machine:

- **Private appointments** are not copied at all. They never leave your PC.
- **Invite text** can be excluded if you would rather only the title, time and people were stored.

You can also choose how far back and how far ahead to read. The default is 30 days back and 180 days
ahead.

## When it syncs

- When you open the Diariz desktop app
- From **Sync Outlook Calendar** in the tray menu
- From **Sync now** in Preferences, or **Sync Outlook** on the Calendar tab

A meeting you cancel or delete in Outlook disappears from Diariz on the next sync.

A meeting that repeats is marked with a **Repeats** badge, and opening it (or a recording linked to it)
shows your earlier recordings of that same series, newest first, so you can jump straight back to the
last one.

## Each PC is separate

Every machine you use appears on its own in the Desktop Outlook card, showing its mailbox, how many meetings it
holds, when it last synced, and anything that went wrong. Two PCs never interfere with each other, so
you can sync a work calendar from one and a personal one from another.

Because the last failure is stored with the machine, you can see that your work PC has stopped
syncing while you are sitting at your laptop.

## Removing what was copied

- **Disconnect** on a machine removes that machine and deletes every meeting copied from it.
- Turning the **Sync my desktop Outlook calendar** tick off does the same for every machine at once.

Both ask you to confirm first. Recordings already linked to one of those meetings keep their own
record of it, exactly as they would if you deleted the meeting in Outlook.

## Requirements, and what can go wrong

You need **classic Outlook for Windows**. The new Outlook does not let other apps read your calendar
at all, so Diariz cannot copy from it. If you are on the new Outlook, Diariz will tell you so rather
than failing quietly.

Other things you might see:

- **Outlook is not installed on this PC** - there is no Outlook for Diariz to read.
- **Outlook is busy** - usually a dialog box open in Outlook. Try again in a moment.
- **Outlook blocked access to your calendar** - some managed work PCs are configured to refuse this.

Two things worth knowing about how it reads:

- If Outlook is not running, reading the calendar will start it.
- Diariz never closes an Outlook you had open.
