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

### On Linux, share a tab and not the whole screen

Chromium (Chrome, Edge) on **Linux** does not capture system audio when you share a **screen or a
window** - only when you share a **browser tab**. The share dialog still lets you pick a screen, and
the recording still runs, but the shared stream carries no sound: you get a microphone-only recording,
which is usually near-silent. Transcription then finds no speech and the recording is marked failed
with "No speech was detected in this recording."

So on Linux, either:

- **Share the tab** that is playing the audio, which does carry sound; or
- **Record system audio as a microphone.** Linux can expose whatever your speakers are playing as an
  ordinary input device, which you then pick from the microphone dropdown - no screen sharing at all,
  and it captures every application rather than one tab. This is the route that always works.

On PipeWire (Ubuntu 24.04 and newer) that input is not offered by default, because a speaker's "monitor"
is not published as a device of its own. Diariz ships a small configuration file that publishes it.
Download it and restart PipeWire:

```bash
mkdir -p ~/.config/pipewire/pipewire.conf.d
curl -o ~/.config/pipewire/pipewire.conf.d/99-diariz-system-audio.conf \
  "$(echo $DIARIZ_URL)/linux/99-diariz-system-audio.conf"
systemctl --user restart pipewire
```

Replace `$DIARIZ_URL` with the address you use for Diariz, or just open
`/linux/99-diariz-system-audio.conf` on your Diariz server and save it to that folder yourself. It is a
plain text file with comments explaining what it does - worth reading before you install it.

**System Audio (Diariz)** then appears in the microphone dropdown, and comes back automatically every
time you log in - there is no script to leave running. It follows your current output, so it keeps
working when you switch between speakers, headphones and HDMI.

It captures system audio **only**. To record your own voice at the same time, tick **System audio**
alongside a real microphone, or build a combined virtual sink.

Two things to be aware of: the device is visible to **every** application, so Zoom, Teams and Slack will
list it as a microphone too; and it stays idle until something actually records from it, so it costs
nothing when unused.

There is no Linux installer for Diariz, but you do not have to live in a browser tab either: open the
account menu and choose **Install app**, and Chromium adds Diariz to your applications with its own icon
and window. Recording works exactly the same way inside it, including the system-audio device above.

### Installing it for everyone on a machine

Administrators can install the same file system-wide instead of per user, which covers every account on
the machine and every account created later. Diariz ships a package that does exactly this and nothing
else:

```bash
sudo apt install ./diariz-system-audio_<version>_all.deb
```

It installs one configuration file to `/etc/pipewire/pipewire.conf.d/` and depends only on PipeWire. Each
logged-in user still has to restart PipeWire once (`systemctl --user restart pipewire`) or log out and
back in, because PipeWire only reads its configuration at start-up. If you manage machines with Ansible,
Puppet or an MDM, copying the same file into `/etc/pipewire/pipewire.conf.d/` achieves the same thing
without the package.

### If a system-audio recording comes out silent

Turn **Echo cancellation** off in the capture tuning (it is off by default). An echo canceller exists to
remove sound that is coming from your own speakers - which, when you record system audio, is the entire
recording. It lets the first second or two through and then silences the rest, so you get a recording
that looked fine on the level meter and transcribes to nothing. **Noise suppression** is worth leaving
off too: it is tuned to keep speech and discard everything else, so it damages music and video audio.

This is a browser limitation on Linux rather than something Diariz can work around - the desktop app
does not change it either, because the underlying loopback capture is Windows-only.

## While you are recording

- A **live input-level meter** shows you are actually capturing sound, with a hint if it stays silent.
- **Pause** and **Resume** are separate from Stop. Paused audio is never captured and does not count
  toward the recording's duration.
- **Auto-stop** ends the recording after 15, 30, or 60 minutes, or at a clock time you set. The
  recording stops and starts transcribing on its own, so you can walk away.
- The gear popover tunes capture: echo cancellation, noise suppression, auto gain, and mono. The three
  processing options start **off**, which is what you want for system audio and costs little on a modern
  microphone. Turn them on if you have a specific problem to solve - echo cancellation for a room where
  the far end comes back through your speakers, noise suppression for constant background noise.

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

### Keeping notes visible during a call (desktop app)

On one screen, a call usually wants the whole display, which leaves nowhere to type. In the desktop
app the notes popover has an **Open in a separate window** button: the notes move into a small window
that stays on top of everything else, including a full-screen Teams or Zoom call. Drag it wherever
suits you - its size and position are remembered for next time.

It is the same notes panel, so lines are stamped exactly as before, and the screenshot buttons come
with it. You can close the main Diariz window to the tray and carry on taking notes; the recording
keeps running. Stop the recording and the window closes on its own, with your notes filed against the
recording as usual.

If the main Diariz window closes or reloads underneath it, the small window says it has lost contact
and stops accepting new lines. That is deliberate - a note typed with nowhere to send it would look
saved when it was not. Bring the main window back and it picks up where it left off. Everything you
had already typed is safe.

## Capturing slides automatically

If you are sitting in a presentation, you do not have to take a screenshot of every slide yourself. Turn
on **Auto-capture** - the first of the three small buttons beside the screenshot count in the notes
panel, or the tray item of the same name - and Diariz watches the capture area and takes a screenshot
each time the screen settles on something new.

It only keeps a frame once the content has held still, which is what stops it filling your transcript
with everything that crossed the screen. A mouse pointer moving over a slide does not count as a new
slide, and neither does a blinking cursor, a half-drawn animation, or a fade between two slides. A video
playing inside the deck produces nothing at all until the deck settles again. If the presenter goes back
to a slide you already have, it is not captured twice.

A slide has to stay up for about three seconds to be captured. A deck being flicked through faster than
that will not be caught - that is deliberate, because the alternative is a transcript full of
half-finished transitions. You can always take a capture by hand at any moment; the hotkey, the tray item
and the capture button all keep working while auto-capture is running.

Each capture is timestamped from the moment its slide appeared rather than the moment Diariz decided it
had settled, so it sits in the transcript beside what was being said as it went up.

Auto-capture needs a capture area first, so if you have not chosen one the button waits and says so.
It pauses when you pause the recording, stops on its own if the meeting reaches its screenshot limit or
the screen it was watching is unplugged, and always switches off when the recording ends - it is never
left running into your next meeting. Changing the capture area stops it too, so turn it back on once you
have picked the new area.

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
