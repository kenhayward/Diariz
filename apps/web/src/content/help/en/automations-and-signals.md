---
title: Automations and Workflow Signals
summary: Send meeting events to Zapier, n8n, or any webhook. Workflow Signals let an admin wire a destination once, so formula authors just tick a box instead of pasting URLs.
group: advanced
order: 30
---

Automations are outbound webhooks: Diariz posts a signed JSON payload to a URL you control when
something happens.

**A Platform Administrator must turn this on first**, at **Settings**, **Integration**, using the
**Automations (webhooks)** switch. It is off by default, and every automation endpoint is refused while
it is off.

## Your own automations

**Preferences**, then the **Automations** tab.

1. Under **What should trigger it?**, tick the events you want.
2. Paste your tool's webhook URL into **Destination URL**. The hint tabs give you the exact steps for
   Zapier and n8n.
3. Give it a name and click **Create automation**.

The **signing secret is shown once**, at creation. Copy it then, or you will have to recreate the
automation to get another.

Each automation card then offers **Send test event**, **Recent deliveries** (the delivery log),
**Re-enable** if it has been paused, and **Delete**.

### The events

| Event | Fires when |
|---|---|
| `recording.created` | A recording is created |
| `recording.transcribed` | A recording finishes transcribing |
| `recording.transcription_failed` | A recording fails to transcribe |
| `recording.summarized` | A summary is ready |
| `recording.minutes_ready` | Meeting minutes are ready |
| `recording.action_items_ready` | Action items are ready |
| `recording.tags_ready` | Tags are ready |
| `formula_result.completed` | A formula finishes |
| `formula_result.failed` | A formula fails |

The four AI-output events carry **their output inline** - the summary text, the minutes Markdown, the
actions, the tags - so your workflow does not need a second call back to the API.

## Who was in the meeting

Every recording event also carries an **attendees** list, so a workflow can decide what to do based on who
was there. Each entry has:

- the **name** shown on the transcript, and the **person** in the directory it was identified as
- their **job title**, **company**, and whether they are **internal or external**
- whether the name was applied automatically, and whether the slot is "Multiple Speakers"

An unidentified speaker has no person and no internal/external answer - nobody has said. A "Multiple
Speakers" slot carries no person details either, because it is overlapping voices rather than one human.
Someone who has opted out of voice-printing **still appears by name**: opting out is about not holding their
voiceprint, not about pretending they were not in the room.

### Contact details are opt-in

Email addresses and phone numbers are **not** sent unless you tick **Include attendee contact details** on
that automation. An automation posts to whatever URL you give it, so leaving this off by default stops every
event quietly handing your contact list to the destination. When it is off, those fields are missing from the
payload entirely rather than empty, so your workflow can tell "not allowed" apart from "not known".

## Workflow Signals

The problem Workflow Signals solve: without them, every formula author who wants their output delivered
somewhere has to know a URL and set up their own automation.

With them, an administrator wires the destination **once**, and authors just tick a box.

### Step 1: define the signal (administrator)

**Settings**, **Integration**, **Workflow Signals**.

1. **Signal key** - lowercase letters, numbers, hyphens or underscores, for example
   `action_item_created`. **The key is permanent** - it is what formulas reference, so it cannot be
   renamed afterwards. Only the label, description, and active state can be changed later.
2. **Signal label** - what formula authors will see.
3. **Description** - optional, shown as sub-text under the label.
4. Click **Add signal**.

The **Active** checkbox controls whether the signal appears to formula authors at all.

### Step 2: create a platform automation routed to it (administrator)

Directly below, under **Platform automations**:

1. Name it and give it a **Destination URL**.
2. Tick the events under **What should trigger it?**.
3. Under **Which signals should route to it?**, tick the signals.
4. Click **Create platform automation**.

**An automation with no signal ticked never fires.** The form refuses to create one, because a platform
automation with no signal filter is silently dead rather than obviously broken.

Platform automations have no test button and no delivery log - those are personal-automation features.

### Step 3: attach the signal to a formula (any author)

In the formula editor, under **When this finishes, trigger**, tick the signals that should fire. Pick as
many as you like.

If you do not see that section at all, it is one of two things: the platform Automations toggle is off,
or no active signals have been defined.

### What each side receives

This split matters, and it is deliberate:

- **Personal** automations subscribed to formula events get a **thin** payload: ids, status, and a link
  to the result. No generated content.
- **Platform** automations matching the signal get the **full output**, plus the recording and formula
  names.

A personal automation must never leak generated content to a destination the recording's owner did not
choose. A platform automation exists precisely to route that output somewhere, and an administrator set
it up.

## Delivery, signing, and retries

Every request carries three headers, following the Standard Webhooks convention, so existing
verification libraries work without modification:

- `webhook-id` - the stable event id, **constant across retries**, so use it for idempotency.
- `webhook-timestamp` - Unix seconds.
- `webhook-signature` - `v1,` followed by base64 HMAC-SHA256 of `id.timestamp.body` using your secret.

**Retries** back off over roughly 8 attempts, from 5 seconds out to about 10 hours, covering around 24
hours in total.

**Auto-pause.** After 15 consecutive fully-failed deliveries the automation is paused and its card reads
"Paused - check the URL". Use **Re-enable** once the endpoint is fixed. **Any single success resets the
counter.**

**Rate limiting is handled properly.** A `429 Too Many Requests` is **not** counted as a failure: it
does not consume a retry attempt or move you toward auto-pause. The delivery is rescheduled using your
`Retry-After` header, or 60 seconds if you do not send one. Separately, deliveries are paced to at most
120 per automation per minute - excess is **delayed, never dropped**.

**URLs are validated for safety** on create *and* update, so an automation cannot be repointed at an
internal address after the fact.
