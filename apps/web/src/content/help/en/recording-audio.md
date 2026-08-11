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

A recording that ends by itself always tells you why - the auto-stop time was reached, the meeting
ended, or the room went quiet (see below). Pressing Stop yourself stays silent, as you would expect.

## Recording a meeting from your calendar

Open a meeting on the Calendar tab and press **Join meeting**. The meeting opens and recording starts
in one step, and the recording is named after the invite rather than the time of day. That name sticks:
nothing renames it for you later.

The recording is also linked to that meeting from the outset, so the invite's details are on it as soon
as it appears, and any prep notes you wrote on the event move across to it. Recordings you start any
other way are matched to a meeting by time afterwards, which is a good guess but still a guess.

You can also let such a recording end by itself. In **Preferences -> Recordings**, turn on **Let a
calendar meeting end its own recording** and one sentence appears: "Stop 3 minutes after the meeting
was due to finish, or after 30 seconds of silence - whichever comes first." Change either number and a
worked example underneath updates as you type, spelling out exactly when a 10:00-11:00 meeting would
stop.

Whichever happens first ends the recording, which then transcribes as normal. Silence is only counted
once something has been heard, so joining a call before anyone speaks will not cut your recording
short, and time spent paused does not count either.

If people are still talking when the meeting's scheduled end arrives, Diariz asks whether to **Extend
this meeting** instead of ending outright - you also get a desktop notification, in case you are
looking at the meeting itself rather than Diariz. Answer Extend this meeting and it waits longer each
time you say yes (the wait doubles, so 3, 6, 12, 24 minutes by default), so a long overrun stops
interrupting you.
Leave the prompt unanswered and the recording simply keeps going too, ending once the room actually
goes quiet under the silence setting above.

These settings apply only to a recording started from a calendar event. One you start with the Record
button runs until you press Stop, and if you have set an auto-stop for the session yourself, whichever
stop comes first is the one that applies.

If you join a second meeting while a recording is still running, that first recording is stopped and
sent off to transcribe before the new one starts. This happens whether or not you turned any of the
above on - nothing is lost by moving straight from one meeting to the next.

### Repeating meetings

If a meeting repeats - a weekly stand-up, say - Diariz marks it with a **Repeats** badge wherever it
shows up: the Calendar tab, the meeting's own page, and a recording linked to it. Open the meeting's own
page, or click through to a linked recording's **Calendar Event** section, and you will also see
**Earlier recordings of this meeting** - your last few recordings of that same series, newest first, so
you can jump straight back to what was said last time instead of hunting through your folders. This
works for a repeating event from any calendar source you have connected - Google, a subscribed .ics
feed, or your synced Outlook calendar.

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
**Preferences -> Recordings**, where each choice is a card saying what it does: keep the open folder,
send everything to Ungrouped, or pick one fixed folder. Changes there are saved with the **Save changes**
button in the footer of the Preferences window. If a shared room is open when you record, the meeting is
filed into that room as well.
