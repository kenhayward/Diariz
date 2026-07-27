---
title: Users, groups and permissions
summary: Authority comes from groups, never from a user directly. This covers the four platform permissions, the access-request lifecycle, and how room permissions differ.
group: advanced
order: 40
---

Open **Manage users** from the account menu. You need the **Manage users** permission to see it.

## Permissions come from groups

This is the one rule that explains everything else. **A user has no permissions of their own.** They join
groups, and their effective permissions are the union of every group they belong to.

There is deliberately no "make this person an admin" button on a user. You add them to a group instead.

Permissions are re-read from the database **on every request**, not baked into the sign-in token. Remove
someone from a group and their authority is gone immediately, rather than lasting until their session
expires.

## The four platform permissions

| Permission | What it allows |
|---|---|
| **Manage rooms** | Create, edit and delete rooms and their membership. **Not** a grant to read a room's contents. |
| **Manage users** | Create, edit, enable and delete users and groups. |
| **Manage platform** | Read and write platform settings, and run maintenance including backup and restore. |
| **Manage formulas** | Create, edit and delete shared formulas. |

Two groups are seeded on every install:

- **Platform Administrators** holds all four. It is a system group: it cannot be deleted or renamed, its
  permissions cannot be changed, and **its last member cannot be removed** - so you can never lock
  yourself out of your own platform.
- **Administrators** holds Manage rooms, Manage users, and Manage formulas, but deliberately **not**
  Manage platform. That keeps backup, restore, and platform settings to a smaller circle.

## The Groups tab

A table of groups with a checkbox per permission, a member count, and delete.

Create one with **New group name** and **Add group**. A new group starts with no permissions at all.
Click the member count to open its membership, where a type-ahead adds people by name or email.

Deleting a group does not delete anyone: its members keep their accounts and simply lose whatever that
group granted.

## The access-request lifecycle

A user is in exactly one of three states:

| State | Meaning |
|---|---|
| **Requested** | Access requested, awaiting an administrator. No password yet. |
| **Awaiting setup** | Granted, and a one-time setup link was issued. Waiting for them to set a name and password. |
| **Active** | Set up and able to sign in. |

The flow:

1. Someone uses **Request access** on the sign-in page. Signing in with Google as an unknown user lands
   in the same place.
2. An administrator sees them under **Access requests** and clicks **Grant** or **Deny**.
   - **Deny deletes the request outright.**
   - Grant on a **Google-linked** account goes straight to Active with no setup link needed.
   - Grant on a password account issues a one-time setup link.
3. The user opens the link and sets their full name, password, and language. Passwords need at least 8
   characters with an uppercase letter, a lowercase letter, a number, and a symbol.

If email is not configured on the server, the setup link is shown to you on screen instead, to pass on
yourself.

You can also skip the request step: **Add user** creates the account directly at "Awaiting setup" and
sends the link.

## What else the Users tab does

Each row shows the person, the groups they are in, their status, and their storage use. From there you
can **edit their quota**, **disable or enable** the account, or **delete** it - which removes all their
recordings.

Two rows are deliberately protected: you cannot act on the Platform Administrator, and you cannot act on
yourself.

There is **no administrator password reset**. A user who has forgotten their password resets it
themselves.

## Room permissions are separate

Platform permissions are global. **Room** permissions are per-member, per-room, and are a different
grid. Holding **Manage rooms** at the platform level lets you administer rooms; it does **not** let you
read what is inside them.

| Room permission | What it allows |
|---|---|
| **Manage room** | Change the room's settings and membership |
| **Add recordings** | Record or upload into this room, and receive recordings shared into it |
| **Remove others' recordings** | Unshare other people's recordings from this room (never destroys them) |
| **Share out** | Share a recording from this room into another room |
| **Manage contents** | Create, rename and delete folders, and move recordings between them |
| **Edit others' recordings** | Edit or regenerate other people's summaries, minutes, actions and attachments |

A room member can be a **user or a group**, and effective permissions are the union of their own row and
every group row that applies.

Two details worth knowing:

- **Membership is the row existing, not holding any particular permission.** A member granted nothing
  can still see the room.
- **A non-member gets "not found", not "forbidden"**, so nobody can discover that a room exists by
  probing for it.

New members are added with **Add recordings** by default. Deleting a room asks you to type its name, and
recordings shared into it are unshared rather than deleted.

See also [Rooms and sharing](/help/rooms-and-sharing).
