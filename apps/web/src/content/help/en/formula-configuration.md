---
title: Configuring a formula
summary: Every field in the formula editor and what it does - the template blocks, the merge fields, the context toggles, sharing, and what stops a formula being deleted.
group: advanced
order: 10
---

This is the reference for the formula editor. For what formulas are and how to run one, see
[Formulas](/help/formulas).

## Where the editor lives

- **Personal formulas** - account menu, **Preferences**, then the **Formulas** tab. Click **New
  formula**.
- **Platform formulas** - account menu, **Manage Platform Formulas**. This row only appears if you hold
  the **Manage Formulas** permission.

Both open the same editor.

**Scope is fixed when you create a formula and cannot be changed afterwards.** A personal formula
created in Preferences stays personal; a formula created from the admin window is a platform formula.
Built-in formulas (badged **Diariz**) are seeded on every install and cannot be created in the UI.

## The fields

| Field | What it does |
|---|---|
| **Name** | Required. What you will see in the run picker and in meeting-type pickers. |
| **Description** | Optional one-liner shown under the name in lists. |
| **Prompt** | The template itself - sections and blocks. See below. |
| **Context** | Which parts of the recording the model may read. |
| **When this finishes, trigger** | Workflow Signals to fire on completion. See [Automations and Workflow Signals](/help/automations-and-signals). |
| **Share this formula** | Personal formulas only. Publishes it for others to find and add. |

**Save stays disabled** until the name is filled in, the template has at least one section, and every
block is valid. The specific rules: a section with a heading level needs a title, a Field block needs a
field chosen, and Text and Prompt blocks need non-blank text.

The editor closes on **Cancel** or **Escape**, but deliberately **not** on a click outside it, so a long
prompt is not lost by a stray click.

## Building the template

Click **+ Add section** to start. Each section has:

- A **Heading level**: **No heading**, **H1**, **H2**, or **H3**. "No heading" is what a formula that is
  simply one instruction looks like.
- A **Section title**, required unless the level is "No heading".
- A **Section actions** menu to add blocks, move the section, or delete it.

### Block types

| Block | What it is |
|---|---|
| **Text** | Literal Markdown that appears verbatim in the output. |
| **Field** | A value substituted from the recording. See the table below. |
| **Prompt** | An instruction to the model. |
| **Line** | A horizontal rule. |

Drag a block by its handle to move it within a section **or into another section**.

### Break after

Every block except a Line has a **Break after** control deciding what follows it:

- **No break** - the next block runs straight on.
- **Line break** - a new line.
- **Paragraph** - a blank line.

New Field blocks default to **No break** (so `Date: ` and the date sit together); Text, Prompt, and Line
blocks default to **Paragraph**.

### Merge fields

A **Field** block substitutes one of:

| Field | Contains |
|---|---|
| **Date** | The recording's date |
| **Time** | The recording's time |
| **Title** | The meeting name |
| **Attendees** | Identified people, then a count of the rest |
| **Duration** | How long the recording ran |
| **Action items table** | The extracted actions as a table |
| **Enhanced notes (from your notes)** | Your notes expanded from the transcript |

Note that in a **folder-wide** run there is no single recording, so field blocks resolve to nothing and
are dropped. Keep merge fields for formulas you intend to run on one meeting.

## Context

Tick what the formula is allowed to see. Nothing you do not tick reaches the model.

**Transcript**, **Notes**, **Summary**, **Minutes**, and **Actions**.

Being specific here is worth the effort. A formula that only needs the summary runs faster and cheaper
than one handed the whole transcript, and it is less likely to wander off into detail you did not want.

## Sharing a personal formula

Tick **Share this formula** and others can find it under **Find shared formulas** in the run picker,
read what it does, and add it.

What that means in practice: they can run it but not edit it, **your later edits reach them**, and it
runs against their own model configuration, not yours. Anyone can remove one they added, and deleting
the original removes it for everyone.

## Enabling and disabling

Only platform and built-in formulas have an **Enabled** switch, in the **Manage Platform Formulas**
table. Disabling one removes it from the meeting-type pickers without deleting it. Personal formulas
have no switch - they are always available to their owner.

## What stops a formula being deleted

- **Built-in Diariz formulas can never be deleted.** The table shows "Built-in" instead of a delete
  button.
- **A formula that generates some meeting type's minutes cannot be deleted** until those meeting types
  point somewhere else. The error names them.
- Deleting a platform or built-in formula needs the **Manage Formulas** permission.

Documents a formula has already produced **survive** its deletion. They are stored text, not a live view.
