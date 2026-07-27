---
title: Rooms and sharing
summary: Your Personal Room is private. Shared Rooms are workspaces you invite people into, each member carrying their own permissions, with their own folders and ordering.
group: settings
order: 20
---

Every account has a private **Personal Room**. Nothing in it is visible to anyone else.

## Shared Rooms

If you hold the **manage-rooms** permission you can create **Shared Rooms**: workspaces you invite
users and groups into. Each member carries their own permission grid, covering whether they can add
recordings, manage contents, remove other people's recordings, share out, edit other people's
recordings, and manage the room itself.

The **room switcher** above the recordings list shows each room with how many folders and meetings it
holds, and a tick marks the room you are in. **Manage Rooms** creates, renames, restyles, and deletes
rooms and edits their membership. Deleting a room needs its name typed to confirm.

The room lives in the URL (`/rooms/:roomId`), so switching keeps a clean, linkable address. Diariz also
remembers the room you were last in and returns you to it, unless the URL names one.

## What a shared room contains

A Shared Room has its **own folder structure** and its own recording order, separate from your Personal
Room. The List, Calendar, Actions, and Tags tabs all work inside it, each scoped to the recordings
shared into that room.

Recording or uploading while a shared room is open files the meeting into that room automatically,
while the original stays in your Personal Room. You can also **Share to room** or **Remove from room**
from an existing recording's toolbar.

A shared room can only ever **unshare** a recording, never delete it. Delete appears only in the
recording's home room, and its confirmation names the shared rooms it will also vanish from.

## What stays private

- **Your Google Calendar.** A Shared Room's Calendar tab shows only its recordings, with no personal
  event overlay, and a recording opened inside a Shared Room offers no calendar linking.
- **Notes and screenshots** are visible to room members who can read the recording, woven into the
  transcript exactly as you see them, but only the owner can add, edit, or delete them.

## Searching across rooms

Chat and the Claude/MCP tools search across every room you belong to, so a meeting shared into a room
you are in turns up in your searches.

## When a user is deleted

Their shared recordings are **kept**, and their Personal Room is orphaned rather than destroyed, so a
team does not lose history when someone leaves.
