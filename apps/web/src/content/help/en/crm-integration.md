---
title: Connecting Diariz to your CRM
summary: Diariz ships no CRM connector. Instead it emits signed events and exposes a full REST API, so you wire it to any CRM with n8n or Zapier. This explains the approach and what belongs on each side.
group: crm
order: 10
---

Diariz does not have a Salesforce button, or a HubSpot button, or an EspoCRM button. That is a
deliberate choice rather than a missing feature, and this section explains how to get the same result
without one.

## The approach

Diariz gives you two halves of an integration and lets you supply the middle:

- **Events going out.** Every stage of a meeting fires a signed webhook, and the AI output rides along
  inside it. See [Automations and signals](/help/automations-and-signals).
- **A full REST API coming in.** Anything you can do in the app, an automation can do too. See
  [The Diariz API](/help/api-overview).

You connect the two with **n8n** or **Zapier**, which already speak to your CRM. Diariz never holds your
CRM credentials, never calls your CRM directly, and never needs an update when you change CRM.

The trade is honest: you build the recipe once instead of clicking Connect. In exchange the integration
works with any CRM, including one nobody has heard of, and your operator keeps control of exactly what
leaves the building.

If you have not built a Diariz automation before, read
[Building an n8n workflow](/help/n8n-workflows) or [Using Zapier](/help/zapier-workflows) first. This
section assumes you have.

## What is worth sending to a CRM

| Diariz output | Lands in the CRM as | Notes |
|---|---|---|
| **Summary** | The body of a meeting or activity record | The best single thing to send. Short, readable, and already in the event. |
| **Action items** | Tasks | The cleanest match in the whole model, and the only one worth syncing in both directions. |
| **Attendees** | Links to contacts | Also how you work out which customer the meeting was with. |
| **Meeting minutes** | An attachment, or a long note | Often too long for a CRM note field. Attach it rather than paste it. |
| **Tags** | Topic or category fields | Regenerated from scratch every time a recording is transcribed again, so replace rather than append. |
| **Link back to Diariz** | A URL field on the activity | Every event carries one. Cheap to include, and it saves people hunting for the recording. |

The full transcript is usually a mistake to send. It is long, it contains everything anybody said, and
your CRM is a much less careful place to keep it than Diariz is.

## Matching a meeting to a customer

The join between the two systems is the **email address** of an external attendee.

Every recording event carries an `attendees` list. Each entry says whether that person is internal or
external, so the customer side of a meeting is the entries where `isInternal` is `false`. Look their
email up in your CRM, and you have your account.

**Email addresses are not in the payload unless you ask for them.** Turn on **Include Attendee
Contacts** in the Diariz Trigger node itself, not in Preferences. Without it you get names but no
addresses, and every lookup silently finds nothing. This catches almost everybody once.

Two further points:

- **Match once, then store the result.** Write the Diariz person id into a custom field on the CRM
  contact the first time you match it. Every later meeting with that person is then an exact lookup
  rather than a guess, and it keeps working when somebody changes their email address.
- **Do not create a contact for every unmatched attendee.** You will fill your CRM with colleagues,
  recruiters and delivery drivers within a fortnight. Create on purpose, or not at all.

## Organising by customer inside Diariz

You may not need to add anything to Diariz to make this work, because two things already do the job.

**Folders are the customer dimension.** They nest up to 8 levels deep, so `Customers` then `Acme Corp`
is exactly what folders are for, and a folder has its own AI summary and minutes across every recording
in it, at any depth beneath it. That
gives you an account-level view of every conversation you have ever had with a customer, which is
usually the thing people actually wanted. An automation can create folders and file recordings into
them, so your CRM can decide where a meeting belongs. See [Organizing recordings into folders](/help/organizing-folders).

**Meeting types are the meeting-kind dimension.** Discovery call, quarterly review, renewal. The type
also steers how the minutes are written, so setting it from the CRM improves the output rather than just
labelling it. See [Meeting types](/help/meeting-types).

## What not to copy into Diariz

It is tempting to mirror the deal into Diariz: stage, value, close date, owner. Resist it.

Those fields belong to the CRM and change on the CRM's schedule. A copy in Diariz starts drifting the
moment it is written, and it does not look broken while it drifts. A recording labelled
`Acme - Negotiation` six months after the deal was lost is confidently wrong, and it will be wrong
inside a set of minutes somebody emails to a customer.

Put a **link** to the deal in the meeting name or a folder instead. A link is never out of date, because
it shows you whatever the CRM says today.

For the same reason, do not try to store CRM identifiers in a recording's **tags**. Tags are generated
by the AI, and transcribing a recording again replaces all of them, so anything you put there disappears
without warning.

## Sending information the other way

The interesting recipes run from the CRM into Diariz, not just out of it.

- **File the recording under the right customer.** When a recording appears, look up the account and
  move it into that customer's folder.
- **Name the meeting properly.** `Acme - Renewal call` beats `Mic 7/30/2026, 2:15 PM`.
- **Brief the AI before the meeting happens.** This is the one most people miss. You can attach notes to
  an *upcoming* calendar event, and those notes are adopted onto the recording when it is made **and fed
  into the minutes**. So an automation can drop the deal context into the meeting before anybody speaks,
  and the minutes come out already aware of what is at stake. Nothing else on this page changes the
  output as much for as little work.
- **Close the loop on tasks.** When somebody completes the task in the CRM, mark the matching action
  item done in Diariz, so the recording stops claiming there is outstanding work.

## Before you build anything

Two switches must be on, and both are off by default. A Platform Administrator sets them at
**Settings**, **Integration**:

- **Enable user API access**, for everything.
- **Automations (webhooks)**, for anything triggered by a meeting.

You also need a personal API token from **Preferences**, **Developers**.

## A worked example

[Worked example: EspoCRM](/help/crm-espocrm) builds four of these recipes end to end against a real
open-source CRM. The shapes translate directly to any other CRM with a REST API - only the field names
change.
