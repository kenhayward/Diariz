---
title: Recording audio
summary: Record from your microphone, from system audio, or both at once. You can pause, schedule an auto-stop, and watch the input level while you record.
group: getting-started
order: 20
---

Press **Record** in the capture panel to start. Press **Stop** to end the take and begin transcription.

## Choosing what to capture

- **Microphone.** Pick a specific input device from the dropdown. Your choice is remembered, and the
  list refreshes if you plug in a headset mid-session.
- **System audio.** Tick **System audio** to mix in what your computer is playing, which is how you
  capture both sides of a call from one device. In Chromium browsers you must also tick "Share audio"
  in the share dialog. In the desktop app it just works.
- **System audio only.** Choose **No microphone** to record system audio on its own.

If the browser cannot capture system audio, the checkbox is hidden. If you asked for system audio but
did not share it, the recording falls back to microphone-only rather than failing.

## While you are recording

- A **live input-level meter** shows you are actually capturing sound, with a hint if it stays silent.
- **Pause** and **Resume** are separate from Stop. Paused audio is never captured and does not count
  toward the recording's duration.
- **Auto-stop** ends the recording after 15, 30, or 60 minutes, or at a clock time you set. The
  recording stops and starts transcribing on its own, so you can walk away.
- The gear popover tunes capture: echo cancellation, noise suppression, auto gain, and mono.

## Taking notes as you go

The notes popover lets you jot lines while recording. Each line is stamped with the second you wrote
it, so it later links to that exact moment in the transcript. Notes survive a crash and attach to the
recording once it uploads.

## From the desktop app

The tray or menu-bar menu can start and stop recording without bringing the window forward, including
a **Record Both** item for microphone plus system audio. You get a notification when a recording starts
and when it finishes uploading.

## Where the recording lands

By default a new recording is filed into the folder you currently have open. You can change this in
**Settings -> Recordings** to always use a specific folder, or to leave new recordings ungrouped. If a
shared room is open when you record, the meeting is filed into that room as well.
