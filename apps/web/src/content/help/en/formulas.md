---
title: Formulas
summary: A formula is a document template plus the context it is allowed to see. Run one over a recording or a whole folder to generate a consistent, properly laid-out document.
group: asking-questions
order: 20
---

A formula answers the question "produce this document, from this material, every time". It is a
**template** plus a chosen **context**.

## The template

The template is built from blocks inside H1/H2/H3 sections. A block is one of:

- **Literal text**, written in an auto-growing Markdown box.
- **A substituted recording value**: date, time, title, attendees, duration, the action-items table,
  your notes, and so on.
- **An instruction to the model**.
- **A horizontal rule**.

Each block has a **Break-after** control (no break, line break, or paragraph) so you decide exactly
where content runs together. A drag handle moves a block within a section or into another one. A section
can be **headless**, which is what a formula that is just one instruction looks like.

Because the shape lives in the template, you get a properly laid-out document rather than whatever
structure the model felt like producing.

## The context

The context is any mix of the transcript, your notes, the summary, the minutes, and the action items.
It is exactly what the formula is allowed to see, and nothing more.

## Running one

Open the **Formulas** tab on a recording and pick a formula. Runs happen in the background: the result
appears immediately as "Generating..." and fills in when ready, so you can start several at once.

The tab is a two-panel view, with your generated results on the left and the selected document rendered
on the right. Open a result to edit it in the same rich editor as minutes, download it as `.md`, or
email it to yourself.

**Re-running a formula replaces its previous document** rather than piling up near-identical copies. If
you have edited a document by hand, an automatic re-run leaves it alone; running the formula yourself
still regenerates it.

You can also run one without opening the tab: type `/formula <name>` in the chat box, or ask Claude to
run it over the MCP connection.

## Over a whole folder

A folder page has its own Formulas tab that runs the formula over **every meeting in that folder and
its sub-folders**. The formula runs on each transcript, then over the combined results.

## Where formulas come from

- **Starter formulas** ship with Diariz: Follow-up email, Meeting recap, Decisions and risks, and Tone
  and sentiment read.
- **Personal formulas** are your own. Create and edit them in **Preferences -> Formulas**.
- **Platform formulas** are shared with everyone and need the Manage Formulas permission to change.

You can **share a personal formula** by turning on "Share this formula" in its editor. Others find it
under **Find shared formulas** in the run picker and add it to their collection. That is a live link,
not a copy, so your later edits reach them. Anyone can remove one they added, and deleting the original
removes it for everyone.
