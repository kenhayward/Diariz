---
title: Worked example: EspoCRM
summary: Six n8n recipes that log meetings into EspoCRM with their summaries, raise tasks from action items, file recordings under the right customer, brief the AI from the CRM before a meeting, and keep people up to date.
group: crm
order: 20
---

This builds the pattern in [Connecting Diariz to your CRM](/help/crm-integration) end to end against
**EspoCRM**, chosen because it is open source, self-hostable, and lets you add custom fields from the
admin panel without touching code.

Nothing here is EspoCRM-specific in shape. Swap the HTTP calls and the recipes work against SuiteCRM,
Twenty, Odoo, Krayin or anything else with a REST API.

There is no first-party EspoCRM node in n8n, so these recipes use the generic **HTTP Request** node. That
is a feature rather than a limitation: you can read exactly what is being sent.

> **Check the field names against your own instance.** EspoCRM entity and field names vary a little
> between versions and installations, and a customised install varies more. Fetch one record by hand
> before you build a recipe on it.

## Before you start

**In Diariz**, a Platform Administrator turns on **Enable user API access** and **Automations
(webhooks)** at **Settings**, **Integration**. Generate a personal token at **Preferences**, **Integrations**,
on the **API access** card, and set up the **Diariz API** credential in n8n as described in
[Building an n8n workflow](/help/n8n-workflows).

**In EspoCRM**, create an API user:

1. **Administration**, **API Users**, **Create API User**.
2. Authentication method **API Key**. Copy the key.
3. Give it access to Contact, Account, Meeting and Task.

Every call below sends that key in an `X-Api-Key` header against a base URL of
`https://crm.example.com/api/v1`. In n8n, store it once as a **Header Auth** credential rather than
pasting it into each node.

**Add two custom fields.** At **Administration**, **Entity Manager**:

| Entity | Field | Type | Why |
|---|---|---|---|
| Contact | `diarizPersonId` | Varchar | Lets you match a person exactly on every future meeting, instead of guessing from their email each time. |
| Meeting | `diarizRecordingId` | Varchar | The key that stops one meeting turning into five CRM records. |

That second one matters more than it looks. A single Diariz meeting fires several events as it moves
through transcription, summarising and minutes. Without a key to match on, each event creates another
record.

---

## Recipe 1: log every meeting into EspoCRM with its summary

**The most useful single recipe.** When Diariz finishes summarising, put a Meeting record in EspoCRM
against the right account, with the summary as its description.

**1. Diariz Trigger**

- Event: **Summary Ready**
- **Include Attendee Contacts**: **on**. Without this there are no email addresses in the payload and
  step 2 finds nobody.

The event already carries the summary text, so no second call to Diariz is needed.

**2. Code node - pick the customer**

Take the first external attendee with an email address:

```
const external = ($json.attendees || []).filter(a => a.isInternal === false && a.email);
return [{ json: { ...$json, customerEmail: external[0]?.email || null } }];
```

Test for `isInternal === false` rather than for a falsy value. A speaker Diariz could not identify has
no `isInternal` field at all, and a missing field is falsy too, so the looser test quietly treats
strangers as customers.

**3. IF node** - continue only when `customerEmail` is not empty. Internal meetings stop here.

**4. HTTP Request - find the contact**

```
GET https://crm.example.com/api/v1/Contact
  ?where[0][type]=equals
  &where[0][attribute]=emailAddress
  &where[0][value]={{ $json.customerEmail }}
```

The account is on the contact as `accountId`.

**5. HTTP Request - has this meeting already been logged?**

```
GET https://crm.example.com/api/v1/Meeting
  ?where[0][type]=equals
  &where[0][attribute]=diarizRecordingId
  &where[0][value]={{ $json.recordingId }}
```

**6. IF node** on `{{ $json.total > 0 }}`, then either:

**Create** (`POST /api/v1/Meeting`):

```
{
  "name": "{{ $json.name }}",
  "status": "Held",
  "dateStart": "{{ $now.toUTC().format('yyyy-MM-dd HH:mm:ss') }}",
  "description": "{{ $json.summary }}\n\n{{ $json.links.web }}",
  "parentType": "Account",
  "parentId": "{{ $json.accountId }}",
  "contactsIds": ["{{ $json.contactId }}"],
  "diarizRecordingId": "{{ $json.recordingId }}"
}
```

**or Update** (`PUT /api/v1/Meeting/{id}`) with the same body.

EspoCRM expects datetimes as `YYYY-MM-DD hh:mm:ss` in **UTC**. Sending local time puts your meetings in
the wrong place on the calendar and nothing will warn you.

Including `links.web` in the description gives everyone in the CRM a way back to the recording, the
audio and the full transcript. It costs one line and saves a lot of asking.

### Attaching the minutes instead of the summary

Minutes are a full document, and pasting one into a description field makes the CRM record unreadable.
Trigger on **Meeting Minutes Ready** instead, convert `{{ $json.minutes }}` to a file, and attach it to
the Meeting. Keep the summary in the description.

---

## Recipe 2: raise EspoCRM tasks from action items

**1. Diariz Trigger** - event **Action Items Ready**.

**2. Item Lists node** - split out `actionItems`. Each item has `id`, `text`, `assignee`, `dueDate`
and `completed`.

**3. HTTP Request** - find the Meeting you created in recipe 1, by `diarizRecordingId`, so the tasks can
hang off it.

**4. HTTP Request** - `POST /api/v1/Task` for each item:

```
{
  "name": "{{ $json.text }}",
  "status": "Not Started",
  "description": "From: {{ $json.name }}\n{{ $json.links.web }}",
  "parentType": "Meeting",
  "parentId": "{{ $json.meetingId }}"
}
```

**`assignee` and `dueDate` are free text and often empty.** Diariz records what was said, and people say
"Sam" and "next Friday", not a user id and a date. Do not map them straight into EspoCRM's
`assignedUserId` and `dateEnd`.

Two workable approaches:

- **Keep them as text.** Append them to the description. Honest, and it never puts a wrong date on a
  task.
- **Resolve them.** Add a step that looks `assignee` up against EspoCRM users, and pass `dueDate`
  through a date parser. Only write the field when the lookup is confident, and fall back to the
  description when it is not.

Guard against duplicates the same way as before: this event can fire again if action items are
re-extracted. Either search for an existing Task with the same name under the same parent, or add a
`diarizActionId` custom field to Task and match on it.

---

## Recipe 3: file the recording under the right customer

This one runs the other way, from EspoCRM into Diariz, and turns your folder tree into an
account-by-account archive.

**1. Diariz Trigger** - event **Recording Transcribed**, **Include Attendee Contacts** on.

Use **Transcribed**, not **Created**. The `recording.created` event deliberately carries an empty
`attendees` array, because nobody has been identified yet. It is the right trigger only if you are
matching on the calendar invitation instead.

**2 and 3.** Pick the external attendee and look them up in EspoCRM, exactly as in recipe 1. Fetch the
Account to get its name.

**4. Diariz node** - resource **Folder**, operation **list**. Look for a folder named after the account.

**5. IF** the folder does not exist, **Diariz node** - resource **Folder**, operation **create**, with a
parent folder of `Customers` so accounts nest underneath it.

**6. Diariz node** - resource **Recording**, operation **move to folder**.

- Recording ID: `{{ $json.recordingId }}`
- Folder: the id from step 4 or 5

**7. Optional** - resource **Recording**, operation **rename**, to
`{{ $json.accountName }} - {{ $json.name }}`.

Once meetings are filed this way, a folder's own AI summary and minutes become an account review across
every conversation you have had with that customer. That is usually the point at which people stop
thinking of this as an export and start using it.

---

## Recipe 4: brief the AI before the meeting happens

The highest value for the least work, and the one that changes what Diariz produces rather than just
where it ends up.

Notes attached to an **upcoming calendar event** are adopted onto the recording when it is made, and
they feed minutes generation. So an automation can tell the model what the meeting is about before
anybody speaks.

**1. Schedule Trigger** - hourly.

**2. Google Calendar node** - events starting in the next two hours.

**3. Code node** - pull out the external attendee emails from the invitation.

**4. HTTP Request** - find the EspoCRM contact, then their Account, then their open Opportunities:

```
GET https://crm.example.com/api/v1/Opportunity
  ?where[0][type]=equals
  &where[0][attribute]=accountId
  &where[0][value]={{ $json.accountId }}
  &where[1][type]=notIn
  &where[1][attribute]=stage
  &where[1][value][]=Closed Won
  &where[1][value][]=Closed Lost
```

**5. HTTP Request** - post the briefing as a note on the calendar event:

```
POST {{ $credentials.baseUrl }}/api/calendar/events/{{ $json.calendarId }}/{{ $json.eventId }}/notes
{
  "lines": [
    "Account: {{ $json.accountName }}",
    "Open deal: {{ $json.opportunityName }}, stage {{ $json.stage }}, value {{ $json.amount }}",
    "Last contact: {{ $json.lastActivityDate }}"
  ]
}
```

When the meeting is recorded and linked to that calendar event, the briefing is adopted onto the
recording and the minutes come out already aware of the deal.

Worth knowing:

- The notes are **visible and editable by the user**, in the meeting notes panel. That is a feature -
  they can see the briefing during the call and correct it - but do not put anything in there you would
  not want on screen.
- Each note line is capped at 2048 characters.
- This needs the recording to be linked to the calendar event. See
  [Recording a meeting](/help/recording-audio).
- Unlike everything else on this page, this **is** a copy of CRM data. It is an acceptable one because
  it is scoped to a single meeting, never refreshed, and never presented as the truth about the deal.

---

## Recipe 5: send task completion back to Diariz

Closes the loop, so a recording stops showing outstanding work that is already done.

**1. Schedule Trigger** - every few hours.

**2. HTTP Request** - EspoCRM Tasks with status `Completed`, modified since the last run, with a
`diarizActionId` set.

**3. Diariz node** - resource **Action**, operation **complete**, passing the action ids.

This only works if you stored `diarizActionId` on the Task when you created it in recipe 2. It is the
main reason to add that custom field.

---

## Recipe 6: keep Diariz people up to date from EspoCRM

Your CRM already knows everyone's job title, company and phone number, and somebody maintains it. This
copies those three fields onto the matching people in Diariz, so a speaker's contact card is right
without anyone retyping it.

**Only those three fields.** The reasons are worth understanding, because each one is a way this recipe
can go wrong.

- **Never send the name or the email address of a person who has a Diariz account.** Those follow the
  account, and Diariz answers **400** if you try. A recipe that sends every field fails on every
  colleague.
- **Never send the email address at all.** It is what you matched on. Diariz stores the address that
  identified the person, which is often not the one your CRM lists first, so overwriting it can break
  the match that found them next time.
- **Never send internal/external or the voiceprint opt-out.** The first is a Diariz judgement, not a
  CRM one. The second is consent to hold biometric data, and nothing outside Diariz should touch it.

**Your API token needs the Manage people permission**, unless it happens to be your own record.

**1. Schedule Trigger** - daily is plenty. Job titles do not change hourly.

**2. HTTP Request** - EspoCRM contacts modified since the last run:

```
GET https://crm.example.com/api/v1/Contact
  ?where[0][type]=after
  &where[0][attribute]=modifiedAt
  &where[0][value]={{ $json.lastRun }}
  &maxSize=200
```

**3. HTTP Request** - find the person in Diariz:

```
GET {{ $credentials.baseUrl }}/api/people/search?q={{ $json.emailAddress }}
```

This endpoint is open to everyone and matches on name, email **and** company, so it is also how you
would find everybody from one client.

**4. IF node** - stop when nothing came back.

**Do not create a person here.** It is tempting, and it fills your directory with hundreds of people who
have never been in a meeting, which makes the speaker picker and the duplicate suggestions worse for
everyone. Let people appear when they actually attend something.

**5. Set node** - build a body containing only what is safe to send:

```
{
  "title": "{{ $json.crmTitle }}",
  "companyName": "{{ $json.accountName }}",
  "phone": "{{ $json.phoneNumber }}"
}
```

Use the **account's** name rather than whatever is typed on the contact. Diariz stores the company as
plain text, so the only thing that makes it useful for grouping is everybody spelling it the same way.
Taking it from the account record is what guarantees that.

Omitting a field leaves it alone, so send only what your CRM actually has. An empty string is a value
and will blank it.

**6. HTTP Request** - `PUT {{ $credentials.baseUrl }}/api/people/{{ $json.personId }}`

### What you get

Job title and company appear next to the speaker in the Speakers tab, and the contact card above their
segments gets a working phone link. Minutes that name a job title get it right. Nobody typed anything.

### What this recipe deliberately does not do

It does not bring across departments, addresses, social links, lifecycle stage, lead source or record
owner. Diariz has nowhere to put them and no use for them, and a field nobody reads is still a field
somebody has to keep correct.

## When it does not work

| Symptom | Usually |
|---|---|
| Workflow never runs | Automations turned off at **Settings**, **Integration**, or the workflow is not active in n8n |
| Contact lookups always find nothing | **Include Attendee Contacts** is off in the Diariz Trigger node. Check the node, not Preferences - the node re-creates its automation on every publish and overwrites what you set in the app. |
| Five CRM records for one meeting | No `diarizRecordingId` check. Add the search-then-create-or-update step. |
| Meetings on the wrong day | Datetimes sent as local time. EspoCRM wants UTC. |
| Everyone in the company appearing as a customer | Filtering on `isInternal` being falsy instead of `=== false`. Unidentified speakers have no such field. |
| It worked, then stopped | **Preferences**, **Integrations** has a delivery log per automation (**Recent deliveries**) with the HTTP status your n8n instance returned. An automation auto-pauses after repeated failures. |
| 403 from Diariz on a write | The API token is read-only, or has expired. Updating people also needs the **Manage people** permission. |
| 400 "Name and email follow the linked user account" | Recipe 6 is sending a name or an email for somebody who has a Diariz account. Send only title, company and phone. |

## Where to take it next

- Route several CRMs, or several teams, through one admin-owned automation using
  [Automations and signals](/help/automations-and-signals).
- Extract CRM-shaped data with a [formula](/help/formulas) instead of sending the plain summary. A
  formula can be told to return the competitor mentioned, the objection raised and the agreed next step,
  and you can map each to its own CRM field.
