---
title: Meeting types
summary: A meeting type is presentation and selection - a name, icon, colour, and framing - that names the formula generating its minutes, plus any formulas to run alongside.
group: advanced
order: 20
---

A meeting type carries **no prompts of its own**. It decides *which formula* generates the minutes, and
gives the model a sentence of framing. That is why minutes and formulas are the same machinery: anything
you can express as a formula can produce your minutes.

## Opening the editor

From a recording's **Minutes** section toolbar, click **Manage templates**. The window is a list of
types on the left and the editor on the right.

A Platform Administrator sees all platform types plus their own. Everyone else sees only their own. A
type you cannot edit opens read-only, with a note saying only a Platform Administrator can change it.

## The fields

| Field | What it does |
|---|---|
| **Title** | Required. The name in the meeting-type dropdown. |
| **Group name** | Required. Types are grouped under this heading in the picker. |
| **Icon and colour** | A grid of 16 icons, tinted with the colour you pick. Presentation only. |
| **Meeting overview** | The framing you give the model - what this kind of meeting is about. |
| **Shared platform template** | Platform Administrators only. Flips the type between personal and platform. |
| **Minutes formula** | The formula whose template generates the minutes. |
| **Also run** | Formulas to run whenever the minutes are generated. |

**Meeting overview** is the only free text on the type itself, and it is worth writing carefully. It is
the place for things like "this is a customer call, keep it suitable to send back to them" - guidance
that should colour every section without being repeated in the template.

**Also run** formulas produce their documents in the recording's Formulas tab, not in the minutes.

## Which formulas you can pick

The picker only offers formulas the type is actually allowed to use:

- A **platform** type may point only at **enabled, non-personal** formulas.
- A **personal** type may point at your own personal formulas, or any enabled formula.

The reason is that minutes generate as the recording's owner, and a personal formula can only be run by
its owner. A shared type pointing at someone's private formula would produce nothing for everyone else.
The picker hides those rather than letting you save something that would silently fail.

**Minutes formula** also offers **General default**, which means "no explicit formula" and falls back to
the standard minutes.

## What ships

A standard set is seeded on every install: General, Customer, Cadence Call, Weekly, 1:1, Interview, Town
Hall, and Webinar, each with the built-in formula that generates it. Pick one from the Minutes toolbar
to re-run the minutes in that structure.

## Moving a type between installs

**Export** downloads the selected type as a JSON file. **Import** reads one back and asks you to name it.

The export references its formulas **by name**, not by id, because ids mean nothing on another instance.
On import, names are matched case-insensitively against the formulas you have. Anything that cannot be
matched is **reported, not silently dropped** - you get a message naming the missing formulas, and you
pick a replacement before generating minutes.

An imported type always arrives as **personal**. A Platform Administrator can flip it to platform
afterwards.

## Deleting a type

Recordings using a deleted type fall back to the General default. Note the reverse constraint too: a
formula cannot be deleted while a meeting type points at it. See
[Configuring a formula](/help/formula-configuration).
