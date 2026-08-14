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

**Preferences**, then the **Integrations** tab.

1. Under **What should trigger it?**, tick the events you want.
2. Paste your tool's webhook URL into **Destination URL**. The hint tabs give you the exact steps for
   Zapier and n8n.
3. Give it a name and click **Create automation**.

The **signing secret is shown once**, at creation. Copy it then, or you will have to recreate the
automation to get another.

Each automation card then offers **Send test event**, **Recent deliveries** (the delivery log),
**Pause** / **Resume**, and **Delete**.

**Pause is the reversible one.** A paused automation stops receiving events but keeps its settings and,
crucially, its signing secret. Deleting one throws the secret away, and re-creating it later issues a new
secret you would have to go and update at the receiving end. So if you only want deliveries to stop for a
while - a workflow you are rebuilding, a system that is down for maintenance - pause it.

### The events

| Event | Fires when |
|---|---|
| `recording.created` | A recording is created |
| `recording.transcribed` | A recording finishes transcribing |
| `recording.transcription_failed` | A recording fails to transcribe |
| `recording.summarized` | A summary is ready |
| `recording.minutes_ready` | Meeting minutes are ready |
| `recording.action_items_ready` | Action items are ready |
| `recording.tags_ready` | Suggested tags are ready |
| `formula_result.completed` | A formula finishes |
| `formula_result.failed` | A formula fails |

The four AI-output events carry **their output inline** - the summary text, the minutes Markdown, the
actions, the suggested tags - so your workflow does not need a second call back to the API.

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
that automation. If the automation was created by an **n8n Diariz Trigger**, set it on the node instead -
the node re-creates its automation on every publish and would otherwise reset your choice. An automation posts to whatever URL you give it, so leaving this off by default stops every
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

**An automation with no signal ticked never fires**, so the form refuses to create one - a platform
automation with no signal filter is silently dead rather than obviously broken.

One event is different. **Feedback Received** carries no signal at all, so it fires whatever the filter
says. An automation listening only for Feedback Received therefore needs no signal, and the form accepts
it. Add any other event alongside it and a signal is required again, because that other event is routed
the normal way.

**Feedback Received** is offered to platform automations only. It is not in the personal Automations list,
and Diariz refuses it there: a personal automation belongs to one person, and feedback is readable only by
a Platform Administrator, so a personal one would hand its owner someone else's words.

### Sending the feedback text

Tick **Feedback Received** and one more option appears: **Include what the person wrote**.

Leave it off and the automation still gets the useful part - who sent it, the page they were on and the
release, which is usually enough to route it or raise a ticket. Turn it on and the description itself is
sent to the destination URL. It is off by default because the description is free text and may quote
meeting content, and a webhook can point anywhere.

An automation carrying the text is marked **Feedback text included** in the list. There is no edit form
for a platform automation, so that badge is the only way to see the setting afterwards - to change it,
delete the automation and create it again.

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
"Paused - check the URL", which is how you tell it apart from one you paused yourself (that just reads
"Paused"). Use **Resume** once the endpoint is fixed - resuming also clears the failure count, so a single
further failure will not immediately pause it again. **Any single success resets the counter** too.

**Rate limiting is handled properly.** A `429 Too Many Requests` is **not** counted as a failure: it
does not consume a retry attempt or move you toward auto-pause. The delivery is rescheduled using your
`Retry-After` header, or 60 seconds if you do not send one. Separately, deliveries are paced to at most
120 per automation per minute - excess is **delayed, never dropped**.

**URLs are validated for safety** on create *and* update, so an automation cannot be repointed at an
internal address after the fact.
