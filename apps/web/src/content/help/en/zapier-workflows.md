---
title: Building a Zapier workflow
summary: There is no Zapier app to install. You wire Diariz up with Catch Hook as the trigger and a custom request with an API token to read anything back.
group: advanced
order: 70
---

**Diariz does not publish a Zapier app.** There is nothing to install from the Zapier directory, and
nothing to search for.

That is not a gap you have to work around, though. Diariz sends standard signed webhooks and has a full
REST API, so Zapier connects with its built-in **Webhooks by Zapier** blocks. This page is the recipe.

If you use n8n instead, there **is** a purpose-built node - see
[Building an n8n workflow](/help/n8n-workflows).

## Before you start

A Platform Administrator must switch on **Automations (webhooks)** at **Settings**, **Integration**. It
is off by default. For the read-back half you also need **Enable user API access**.

## Step 1: create the Zap trigger

In Zapier, add a **Webhooks by Zapier** trigger with the **Catch Hook** event. Zapier gives you a
**Custom Webhook URL**. Copy it.

## Step 2: point Diariz at it

In Diariz, open **Preferences**, then the **Automations** tab.

1. Under **What should trigger it?**, tick the events you want. The full list is in
   [Automations and Workflow Signals](/help/automations-and-signals).
2. Paste the Zapier URL into **Destination URL**. Choosing the **Zapier** hint reminds you of the exact
   step above.
3. Name it and click **Create automation**.

Copy the **signing secret** now if you intend to verify deliveries - it is shown only once.

Then click **Send test event**, and Zapier will pick it up so you can map the fields.

## Step 3: use the data

Four of the events carry their output **inline**, which is what makes simple Zaps possible with no
second call:

- **A summary is ready** - the summary text
- **Meeting minutes are ready** - the minutes Markdown
- **Action items are ready** - the extracted actions
- **Tags are ready** - the tags

So "when a summary is ready, post it to Slack" or "append the minutes to a Google Doc" needs nothing but
the trigger and the destination step.

## Step 4 (optional): read more back from Diariz

Formula events deliberately carry only ids and a link, not the generated text, so a personal automation
can never leak content to somewhere the recording's owner did not choose. To fetch the document you call
the API.

1. Generate a token in **Preferences**, **Developers**. **Read-only** is the right choice for fetching.
   The Automations tab offers to create one for you when you tick a formula event.
2. In your Zap, add a **Webhooks by Zapier** action with the **Custom Request** event:
   - **Method**: GET
   - **URL**: `https://your-server/api/recordings/{recordingId}/formula-results/{id}` using ids from the
     trigger
   - **Headers**: `Authorization` set to `Bearer dz_api_...`

See [The Diariz API](/help/api-overview) for what else you can call.

## Verifying deliveries

Optional but recommended if the Zap does anything consequential. Every request carries three headers:

- `webhook-id` - stable across retries, so use it to avoid acting twice on one event
- `webhook-timestamp` - Unix seconds
- `webhook-signature` - `v1,` then base64 HMAC-SHA256 of `id.timestamp.body` using your secret

This is the Standard Webhooks scheme, so any off-the-shelf verification library works.

## If a Zap stops firing

Check **Recent deliveries** on the automation card - it records the HTTP status Zapier returned.

After 15 consecutive failures the automation pauses itself and the card reads "Paused - check the URL".
Fix the endpoint, then click **Re-enable**. Any single success resets the counter.

Zapier throttling is handled properly: a `429` response is not counted as a failure and does not move
the automation toward pausing.
