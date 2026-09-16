---
title: Action items
summary: Diariz extracts Action, Actor, and Deadline from every meeting automatically, into a table on that meeting. Pin the ones you intend to track and they collect in the Actions tab, where you can filter and tick them off.
group: recordings
order: 30
---

Action items are pulled out automatically as part of the transcription pipeline, into an editable table
in the **Action items** panel on each recording. The panel is collapsed by default and has a refresh
button to re-extract.

The automatic pass runs **once** and never overwrites actions you have added or edited by hand.

## Per meeting

Each action has an **Action**, an **Actor**, and a **Deadline**, all editable. Each also has a **Done**
checkbox and a completion date.

Every action a meeting produced is listed here, whether or not you have pinned it. This page is the
complete record; nothing is ever hidden from it.

The meeting minutes are generated from this same action set, so the minutes' Action Items table and the
Actions panel never disagree. Actions also travel with the transcript: they are in the text, Markdown,
and RTF downloads, the emailed transcript, and the chat context.

## Pinning: choosing what to track

Most meetings produce actions that are minor, or that belong to someone else. So the cross-meeting list
is something you build rather than something you inherit.

Each action has a **pin**, on its meeting's table and on the row in the Actions tab. Pinning an action
puts it in the Actions tab; unpinning takes it back out. An unpinned action is not deleted or hidden -
it stays on its own meeting, exactly where it was extracted.

This means the Actions tab starts **empty**, and fills up only with what you have chosen to track.

You can only pin actions on your own recordings. In a shared room you will see the pins the recording's
owner has set, and the pin control on someone else's row is disabled.

**Re-extracting a meeting's actions keeps every pinned action**, including one you recorded live, and
replaces only the rest - so re-extracting no longer clears the pins or Done ticks of what you have
already chosen to track.

## Recording actions during a meeting

You do not have to wait for extraction to run. While a meeting is recording, switch the notes composer
to **Action** (or press **Alt+A**), or turn any note into an action with **Make action** on its row -
**Make note** reverses it. Text is enough; owner and due date are optional and editable in the row, and
owner defaults to you.

An action recorded this way is **pinned automatically**, so it reaches the Actions tab as soon as the
recording finishes uploading - you do not need to open the meeting and pin it yourself. It also shows
up straight away on the meeting's own **Action items** panel once the recording is there.

Automatic extraction still runs afterwards. It is told what you already recorded and adds only what it
finds beyond that, skipping anything that repeats an action you already have. If you re-extract by hand,
the actions you recorded live stay for as long as they are pinned - see above. Unpin one and the next
re-extract treats it like any other extracted action and replaces it.

## Across all your meetings

The left panel's **Actions** tab collects every action you have pinned, from every meeting. From there
you can:

- **Filter by person** to see what one actor owes.
- **Mark items done** individually or in bulk, with a completion date. This is reversible.
- **Hide completed** items to focus on what is outstanding.
- **Click an action** to jump to the transcript it came from.
- **Unpin** an action, which removes it from this list and leaves it on its meeting.

A folder's own **Actions** tab works the same way: it shows the pinned actions from the meetings in that
folder and its sub-folders.

In a shared room, the Actions tab is scoped to the recordings shared into that room.
