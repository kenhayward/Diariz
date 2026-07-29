---
title: Building an n8n workflow
summary: A step-by-step guide to the n8n-nodes-diariz community node - installing it, setting up credentials, the trigger, the action node, and a complete worked example.
group: advanced
order: 60
---

Diariz publishes an n8n community node, **`n8n-nodes-diariz`**, with a trigger that fires on meeting
events and an action node covering the whole REST API.

## Before you start

Two things must be switched on by a Platform Administrator at **Settings**, **Integration**:

- **Enable user API access** - needed for everything. Off by default.
- **Automations (webhooks)** - needed for the trigger only. Off by default.

You also need a personal API token. Generate one in **Preferences**, **Developers**. See
[The Diariz API](/help/api-overview).

**Community nodes only install on self-hosted n8n.** n8n Cloud does not allow them.

## Step 1: install the node

In n8n, go to **Settings**, **Community Nodes**, **Install**, and enter:

```
n8n-nodes-diariz
```

## Step 2: create the credential

Add a **Diariz API** credential with two fields:

- **Base URL** - your server address with no trailing path, for example `https://diariz.example.com`.
- **API Token** - the `dz_api_...` token you generated.

Click **Test**. This does more than check the token: if API access or Automations are switched off on
the server, it tells you which one, rather than leaving you with a puzzling 403 later.

Note that a **read-only** token blocks every write operation, and a token with an expiry date stops
working on that date.

## Step 3: add the trigger

Add a **Diariz Trigger** node and tick the events you want.

| Event | Fires when |
|---|---|
| **Recording Created** | A recording was uploaded or captured, before transcription |
| **Recording Transcribed** | The transcript is ready, with speaker labels and timings |
| **Transcription Failed** | A recording could not be transcribed |
| **Summary Ready** | The AI summary is ready, **and the text rides along in the event** |
| **Meeting Minutes Ready** | The minutes document is ready, and rides along in the event |
| **Action Items Ready** | Action items were extracted |
| **Tags Ready** | Topic tags were generated |
| **Formula Result Completed** | A formula finished and produced a document |
| **Formula Result Failed** | A formula run failed |

You do **not** create an automation in Diariz by hand. Activating the workflow registers one
automatically, pointing at n8n's own webhook URL, and deactivating it removes the registration again.

**Simplify** (on by default) returns just the event data instead of the full envelope with its id, type
and timestamp.

The node verifies every delivery's signature before starting an execution, and rejects anything older
than five minutes as a replay. A failed check answers 401 and starts nothing, so Diariz simply retries.

## Step 4: add the action node

Add a **Diariz** node. Its operations are generated from the Diariz API itself, so it covers the whole
surface - recordings, formulas, actions, attachments, rooms, folders, search, tags, speaker profiles,
meeting types and more.

Useful things it does beyond plain API calls:

- **Dropdowns** for recording, formula, folder and room ids, listing your real data instead of making
  you paste ids.
- **Binary downloads**: transcript as text, Markdown, RTF or subtitles; the audio; attachments; formula
  documents.
- **Binary uploads**: upload a recording, attach a file.
- **Return All / Limit** on anything that returns a list.
- **Custom API Call** on every resource, so anything added to the API later is still reachable.

### Running a formula, and waiting for it

**Run a formula over a recording** is the one operation that waits. Diariz answers immediately with a
document that is still generating, so the node polls until it is ready.

- **Wait for Completion** - on by default. Turn it off only if a Diariz Trigger will pick up the
  completion event instead.
- **Poll Interval (Seconds)** - default 3.
- **Timeout (Seconds)** - default 300. On timeout the node throws; the document may still finish later,
  so fetch it by id or switch to the trigger-based approach.

## Routing on who was in the meeting

Every recording event carries an **attendees** list, so you can branch on the people rather than fetching
them. Each entry has the name, the person it was identified as, their job title and company, and whether
they are internal or external.

- Route by who attended: `{{ $json.attendees.map(a => a.name).join(", ") }}`
- Only continue for meetings with an external party:
  `{{ $json.attendees.some(a => a.isInternal === false) }}`
- Email the people who were there, once contact details are switched on for that automation:
  `{{ $json.attendees.filter(a => a.email).map(a => a.email).join(",") }}`

That last one needs **Include Attendee Contacts** turned on **in the Diariz Trigger node itself**. Without
it the `email` and `phone` fields are not in the payload at all, so the expression yields an empty string
rather than a wrong answer.

Set it on the node rather than on the automation in Diariz. The trigger node creates and owns its
automation, and re-creates it every time you publish the workflow - so a setting made on the Diariz side is
wiped the next time you edit the workflow, and events quietly stop carrying contact details with nothing
having visibly changed. Set on the node, it is re-applied on every publish. Turning it on or off also
re-registers the automation, so it takes effect immediately.

Note that a speaker Diariz could not identify has **no** `isInternal` field at all, rather than a null one -
nothing is claimed about someone it does not recognise. Test for `=== false` rather than relying on
falsiness if you mean "definitely external", since a missing field is falsy too.

## A worked example: summarise a meeting into Slack

1. **Diariz Trigger** - event **Recording Transcribed**.
2. **Diariz** - resource **Formula**, operation **Run a formula over a recording**.
   - Recording ID: `{{ $json.recordingId }}`
   - Formula: pick yours from the dropdown
   - Wait for Completion: **on**
3. **Slack** - post `{{ $json.text }}`.

If you would rather use the summary Diariz generates automatically than run your own formula, use the
**Summary Ready** trigger event instead and skip step 2 entirely - the summary text is already in the
event.

## Operational notes

- You can have **20 automations per user**. Each active trigger uses one, and n8n's "Listen for test
  event" briefly registers a second temporary one.
- Failed deliveries retry with growing backoff over roughly 24 hours.
- **Preferences**, **Automations** has a delivery log showing the HTTP status your n8n instance returned
  - the first place to look when a workflow is not firing.
