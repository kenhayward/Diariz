import type { Release } from "./types";

/// Releases since the last closed epoch, newest first. **This is the file every PR edits**: add the
/// new entry at the top, exactly as before.
///
/// `RECENT[0].version` must equal version.json (asserted in releases.test.ts). When this list grows
/// past a natural stopping point, close it as an epoch - write the record in `epochs.ts` and move
/// these entries to the top of `archive.ts`. releases.test.ts fails above 80 entries, which is a
/// safety net rather than the trigger; the historical epochs average 16.
export const RECENT: Release[] = [
  {
    version: "0.270.2",
    date: "2026-09-03",
    pr: 751,
    headline: "The live meeting leaves the chat when the meeting does",
    summary:
      "Two corrections to the live notes panel, both from using it in a real meeting.\n\nThe **Live meeting** pill in the chat now disappears when you press Stop. It used to stay, on the theory that \"summarise the meeting I just had\" is the natural next question - but a pill labelled Live outliving the thing it names just reads as stale, and a finished recording can be asked about the ordinary way by opening it. Stopping one recording only clears its own pill, so starting a second meeting and stopping the first in the wrong order cannot take the wrong one away.\n\nThe **drag to chat** handle has gone from captures in the live notes panel. It could not work: the notes panel sits over the chat composer and holds focus, so there was nowhere for the drag to land. The **Chat** button on the same thumbnail does the same job and does it reliably. Dragging a thumbnail from a finished recording's Notes tab is unaffected - there the composer and the thumbnail are both on the page, and it works.",
    fixed: [
      "The Live meeting pill stayed in the chat after the recording was stopped, still calling a finished meeting live.",
    ],
    changed: [
      "Captures in the live notes panel no longer offer a drag-to-chat handle, which could not complete - the panel covers the chat composer. The Chat button on the capture is unchanged.",
    ],
  },
  {
    version: "0.270.1",
    date: "2026-09-03",
    pr: 748,
    headline: "The separate notes window stops being sent the same thing over and over",
    summary:
      "While a recording was running with the notes popped out into their own window, the main window was sending that window a complete copy of everything - every note, and a thumbnail of every screen capture you had taken - four times a second, for the whole meeting.\n\nIt was invisible: the notes window showed the right thing, and nothing looked slow. But a long meeting with a lot of captures meant the same pile of images being packed up and handed over 240 times a minute to say nothing had changed. On a machine already busy recording, encoding and streaming audio, that is work worth not doing.\n\nThe window is now told something only when there is something to tell it - a note filed, a capture taken, the recording paused, a new piece of transcript. Its clock is unaffected: it has been running on its own since the window gained one, which is exactly why the constant updates were unnecessary.",
    fixed: [
      "The separate notes window was sent the entire notes state - including a thumbnail of every screen capture - four times a second for the length of the recording, rather than when something it shows had actually changed.",
    ],
  },
  {
    version: "0.270.0",
    date: "2026-09-03",
    pr: 746,
    headline: "Take a note without leaving the call",
    summary:
      "The desktop app now holds two more global hotkeys while a recording is running, so a note or a question can be filed with the call still in front of you: **Ctrl+Shift+0** puts your cursor in the note box wherever you are, and **Ctrl+Shift+8** sends the running meeting to the chat. The existing capture hotkey is unchanged.\n\nThey are numbers rather than the obvious letters on purpose. A global shortcut is held for the whole meeting, and Ctrl+Shift+N is Chrome's incognito window and File Explorer's new folder, while Ctrl+Shift+C is copy in Windows Terminal and in VS Code. Taking either of those away for an hour would be a worse surprise than learning an unfamiliar number, so the two sit beside the capture hotkey on the number row. The panel prints the keys actually registered, so if you change one the hint changes with it.\n\nThe note hotkey follows the notes. If the panel is in its separate window, that window is raised and focused; if it is not, the main window opens the panel for you.\n\nThe separate notes window also gains two controls of its own. **On top** can be turned off so the window drops behind your call and back again, and **Compact** shrinks it to just the note box - for a single screen where the call needs all the room, but you still want somewhere to type.",
    added: [
      "Two more global hotkeys while recording: Ctrl+Shift+0 focuses the note box wherever you are, Ctrl+Shift+8 sends the running meeting to the chat. On macOS, the Command equivalents.",
      "A hint line at the bottom of the notes panel showing the hotkeys as they are actually registered, so it stays right if you change one.",
      "**On top** and **Compact** buttons in the separate notes window - let it fall behind a call, or shrink it to just the note box.",
    ],
    changed: [
      "The separate notes window opens larger by default (420 by 740), and can be made smaller than before when a screen is tight.",
      "The screenshot hotkey is unchanged, including one you have set yourself.",
    ],
  },
  {
    version: "0.269.0",
    date: "2026-09-03",
    pr: 745,
    headline: "Send the meeting you are in, or a capture from it, straight to the chat",
    summary:
      "The live notes panel gains a **Use in chat** button and a **Chat** button on every screen capture, so you can ask the assistant about a meeting while you are still in it without leaving the panel or copying anything out.\n\nUse in chat does not paste your transcript into the prompt. It hands the chat the recording itself, so every question you ask is answered against the transcript as it stands at that moment rather than a snapshot that went stale the second the meeting carried on. The server already knows a recording that is still running is unfinished and says so to the model, which is why it will not report an argument still in progress as settled. The pill stays put once attached, so a follow-up question needs no second press - and it deliberately stays after you stop, because \"summarise the meeting I just had\" is usually the next thing you want.\n\nCaptures reach the chat the same way, either from the button on the thumbnail or by dragging the thumbnail into the chat prompt. For that to work at all, a capture taken while the recording is streaming is now uploaded as you take it instead of waiting for you to press Stop. Nothing is lost if that upload fails - the capture stays where it was and goes up with the rest at the end, exactly as before.",
    added: [
      "**Use in chat** in the live notes panel puts the meeting you are recording into the chat prompt as sticky context, and confirms in place without closing the panel or moving your cursor.",
      "A **Chat** button on every capture in the panel, and drag-to-chat from the thumbnail, so a slide can be asked about while it is still on screen.",
      "A **Live meeting** pill in the chat composer, removable, that rides every question until you take it off.",
    ],
    changed: [
      "Screen captures taken while a recording is streaming now upload as they are taken rather than waiting for Stop. A capture the server refuses is kept and sent with the rest at the end, as before.",
      "Deleting a capture that has already uploaded now removes the server's copy too.",
    ],
  },
  {
    version: "0.268.0",
    date: "2026-09-03",
    pr: 744,
    headline: "Notes, captures and the live transcript on one timeline",
    summary:
      "The notes panel you use while recording no longer has tabs. Your notes, your screen captures and the live transcript now share a single stamped stream, with the box you type into fixed at the bottom of it.\n\nThe tabs were asking the wrong thing of you. What actually happens in a meeting is that you hear a sentence and want to write about it, and the Transcript tab put a click between those two moments at exactly the point you had the least attention to spare - and once you had clicked, the notes you were writing were the thing you could no longer see. Everything is now one list in the order it happened, so a note sits directly under the sentence it was about and a capture sits where the slide went up.\n\nEvery transcript line carries the time it was said, and hovering one reveals a small plus. Pressing it pins the composer to that moment, so a thought you have forty seconds late is still filed forty seconds back rather than wherever the clock has got to. The pin releases itself once you press Enter, and clicking the pinned time releases it without filing anything.\n\nThe separate notes window gets all of this too, and its clock now runs on its own rather than waiting for the main window to tell it the time - which matters, because that window is hidden behind your call precisely when you are using it.",
    added: [
      "A single stream in the notes panel: notes, screen captures and live transcript lines interleaved in the order they happened, each with its own timestamp.",
      "Filter chips - Everything, Notes, Captures - with counts that always show the whole meeting rather than the filtered view.",
      "A plus button on any transcript line pins the composer to that moment, so a note can be filed against something said earlier.",
      "An elapsed clock in the panel header, and a stamp badge on the composer showing exactly when the next note will be filed.",
    ],
    changed: [
      "The Notes and Transcript tabs are gone from both the notes popover and the separate notes window.",
      "The status line under the transcript is now short enough to read at a glance; the full explanation of why live text is not final is on its tooltip.",
      "The separate notes window ticks its own clock from a reading the main window sends, so it keeps time even while the main window is hidden to the tray.",
    ],
  },
  {
    version: "0.267.0",
    date: "2026-09-02",
    pr: 743,
    headline: "Drawing a capture area takes the screenshot",
    summary:
      "Choosing a capture area in the notes panel now takes a screenshot the moment you finish drawing it.\n\nNobody drags a rectangle across their screen for its own sake - they do it because they want a picture of what is inside it. Until now that took two steps: draw the area, watch the overlay disappear with nothing to show for it, then find the capture button and press it. The second step was pure ceremony, and the pause in between was long enough to lose the slide you were aiming at.\n\nThe pick is now the request. Cancelling with Escape still captures nothing, and neither does an area chosen after the recording has ended - a shot you did not ask for is worse than one you have to ask for twice. This applies wherever you choose an area: the notes popover, the separate notes window, and the tray item.",
    changed: [
      "Finishing a capture-area selection now takes a screenshot immediately, instead of only setting the area for a later capture.",
      "The capture area button's hint says what it now does - a screenshot is taken as soon as you finish drawing.",
    ],
  },
  {
    version: "0.266.4",
    date: "2026-09-02",
    pr: 740,
    headline: "The speaker count on a meeting summary is the real one",
    summary:
      "A meeting summary could claim far more speakers than the meeting had - one with four people reported twelve - while the Speakers page beneath it listed the right number all along.\n\nDiariz keeps a record of every speaker label a recording has ever carried, on purpose: it is what lets a name you typed survive the recording being transcribed again. The summary tile was counting that history rather than the transcript in front of you, so a recording that had been transcribed more than once - which every live-captured meeting has been - counted the same people several times over.\n\nThe tile now counts the speakers actually heard in the transcript, and agrees with the page it links to.",
    fixed: [
      "A meeting summary could report many more speakers than it had - twelve for a four-person call - because it counted every speaker label the recording had ever carried rather than the ones in the current transcript. It now matches the Speakers page.",
    ],
  },
  {
    version: "0.266.3",
    date: "2026-09-02",
    pr: 741,
    headline: "The live-meeting releases become a chapter of their own",
    summary:
      "The twenty-three releases that turned Diariz from something you read afterwards into something you read during the meeting are now a chapter of their own, called Reading the meeting while you are still in it. The release notes page opens on it rather than on a list of individual releases.\n\nNothing is lost or shortened by this. Clicking the chapter still lists every one of those releases in full, exactly as they were written - the summary is a heading over them, not a replacement for them.\n\nThe chapter closes here because the arc it describes is finished: capture that survives a crash, a transcript you can read mid-meeting, speakers named while they talk, and the performance work that let all of it keep up on ordinary hardware. What is being looked at next - how accurately speakers are told apart - is the start of a different story.",
    changed: [
      "The **Reading the meeting while you are still in it** epoch now covers 0.260.0 to 0.266.2 - twenty-three releases, still listed in full when you open it.",
    ],
  },
];
