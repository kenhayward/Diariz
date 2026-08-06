---
title: Organizing recordings into folders
summary: Recordings can be filed into folders nested up to 8 levels deep. The list shows one level at a time, with a path at the top showing where you are and letting you jump to any level above you.
group: recordings
order: 15
---

## Folders and depth

Folders nest up to 8 levels deep, so you can go as narrow as Customers, then a customer name, then a
project underneath it. A recording can be filed into a folder at any of those levels from its
**Move to section** action, not just the bottom one.

**Move to section** opens a picker rather than a plain list: type a folder's name into the filter box
to jump straight to it from anywhere in the tree, with its full path shown so you can tell two
same-named folders apart, or leave the filter empty to browse one level at a time the same way the
folder list does. **Ungrouped** is offered as a choice at every level, so you never have to back out to
the top to pick it. The same picker is used for choosing where a new recording is filed, in
**Settings -> Recordings**.

While browsing a level, clicking a folder's name opens it so you can look inside - it does not choose
it. Choosing that folder is a separate checkmark button next to its name (its label for assistive
technology is "Select", followed by the folder's name). A row you reached by typing into the filter
box has nothing to open, so clicking it chooses it right away.

## Moving between folders

The list shows one folder at a time rather than everything expanded at once. Opening a folder drills
into it; the path at the top of the list shows where you are, anchored to the room root.

- Click any part of that path, other than the folder you are already in, to jump straight to that level.
- When the path is too long to fit, the middle collapses behind an ellipsis, but the menu at the end
  still lists the whole hierarchy, including **Open section page** for that folder's own page.
- Drag a recording onto any part of the path to move it up to that folder in one step.
- If you have ticked several recordings, dragging any one of them moves the whole set - they land in the
  order the rows are shown, not the order you ticked them. Dragging a recording you have not ticked moves
  only that one, so an unrelated drag never sweeps up a selection you left elsewhere.

Browser back pops you up one level at a time. Your place in the folder list is kept while you navigate
elsewhere in the app, but a fresh visit starts back at the room root.

### Finding where an open recording lives

An open recording shows its folder path as a row of chips under its name, starting with the room. Click
any chip to take the list straight to that folder - the recording stays open, and only the list moves. A
recording filed at the top level shows just the room chip, which is also the quickest way back to the top
of the list from anywhere.

If you are on the Calendar, Actions or Tags tab when you click a chip, the panel switches back to the list
first, since that is where the folder you asked for is shown.

A recording shared into more than one room is filed independently in each, so the chips always describe the
room you are currently viewing.

## Cutting and pasting between folders

To move several recordings, or a whole folder, in one step:

1. Click **Select recordings** in the toolbar, tick the ones you want to move, then click **Cut**. To
   move a single folder instead, open its own menu and choose **Cut** there - folders move one at a
   time.
2. A bar appears under the toolbar naming what you cut, with a **Paste into** button for wherever you
   are currently looking, and a **Cancel** button.
3. Drill into the folder you want things moved into, then click **Paste into** that folder.

Cut rows grey out with a dashed outline and stay exactly where they are - nothing moves on the server
until you paste. Cancel drops the cut without moving anything.

Paste stays visible but disables itself, with the reason shown next to it, when the move is not allowed:
pasting back into the folder you cut from, into a shared room (only your personal room supports this so
far), deep enough to exceed the 8-level cap, or into a folder's own descendant. Once allowed, pasted
items land at the bottom of the destination, keeping the order you cut them in.
