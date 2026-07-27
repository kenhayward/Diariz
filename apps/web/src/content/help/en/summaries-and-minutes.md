---
title: Summaries and meeting minutes
summary: Diariz summarises each recording automatically and can generate full meeting minutes in a structure you choose. Both are editable, re-runnable, and travel with exports.
group: recordings
order: 20
---

Summaries and minutes both need an AI model to be configured. See
[AI and model settings](/help/ai-model-settings) if these features are unavailable.

## Summaries

A summary is generated automatically after transcription, and the meeting is given a name at the same
time if it does not have one. The summary appears inline on the recording hub.

## Meeting minutes

Minutes are a full document, written in Markdown with headings, lists, and tables. The Meeting Minutes
panel is always available (collapsed) with a refresh button, so you can generate them on any recording.

You can:

- **Edit them** in a rich WYSIWYG editor.
- **Re-create them**, optionally in a different structure.
- **Email them to yourself**, optionally with the recording's attachments.

Minutes also travel with the emailed transcript and with the Markdown, text, and RTF downloads.

## Meeting types decide the structure

A **meeting type** is a name, icon, colour, and the framing you give the model, for example "this is a
customer call, keep it suitable to send back to them". Picking a type from the Minutes toolbar re-runs
the minutes in that structure.

A standard set ships with Diariz: General, Customer, Cadence Call, Weekly, 1:1, Interview, Town Hall,
and Webinar. You can create your own in **Manage Meeting Types**, either personal to you or,
with the right permission, shared platform-wide.

Under the hood a meeting type points at a **formula**, so minutes and formulas are the same machinery.
Anything you can express as a formula can produce your minutes. See [Formulas](/help/formulas).

## How your notes affect minutes

Every minutes section weights what you flagged in your notes. A template can also include an
**Enhanced notes** section, where each of your lines is expanded from the transcript with your original
words kept verbatim beside the expansion, and links to the exact transcript moments. Anything the
meeting never covered is kept and marked "not discussed" rather than silently dropped.
