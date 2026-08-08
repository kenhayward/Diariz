---
title: The Diariz API
summary: Call Diariz as yourself with a personal token. Everything the app does is in the REST API, documented in a browsable reference and an OpenAPI document.
group: advanced
order: 80
---

The API gives you the same access you have in the app, authenticated with a personal token instead of a
browser session. Ownership and permissions resolve exactly as they do when you are signed in, so a token
can never reach anything your account cannot.

## Turning it on

A Platform Administrator enables **Enable user API access** at **Settings**, **Integration**. It is
**off** by default. While it is off the **API access** card says so and offers nothing, rather than
disappearing - so you can see the capability exists and who to ask.

## Generating a token

**Preferences**, then the **Integrations** tab.

1. Name the token after whatever will use it.
2. Decide the scope. **Read-only (cannot change anything)** rejects every POST, PUT, PATCH and DELETE.
   Leave it unticked for read-write.
3. Optionally set **Expires on**. Blank means it never expires.
4. Click **Generate token**.

**Scope and expiry are set at creation and cannot be changed afterwards.** If you need different
settings, generate a new token and revoke the old one.

**The token is shown once.** Only a hash is stored.

Prefer a read-only token whenever the thing you are building only reads. It is the cheapest possible
protection against a mistake in your own code.

## Making a request

The base URL is your server plus `/api`. Authenticate with a bearer header:

```
curl -H "Authorization: Bearer dz_api_..." https://your-server/api/recordings
```

An API token works only on `/api`. It is rejected on `/mcp`, and an MCP token is rejected here.

## The reference

Click **View API reference** on the API access card, or go to **`/developers/api`**. It is a full
browsable reference, signed-in users only, listing every endpoint with what it does, who may call it,
and what it changes.

The machine-readable document is at **`GET /api/openapi/v1.json`**. It needs authentication like any
other endpoint.

The document is curated rather than the raw surface: the administrative and OAuth internals are left
out. Every published endpoint is required to carry a summary and a description, enforced by a test, so
the reference cannot quietly fall behind the code.

## The people directory

The people who appear in your meetings live under **`/api/people`**. A person carries a name plus an
optional job title, company, email address, phone number and an internal/external marker, and an optional
voiceprint - so you can add a client you have never recorded, and their record is complete.

- `GET /api/people` lists the directory, with filters for a search term, internal or external, and whether
  a voiceprint is held. It needs the **Manage people** permission, because the directory is shared across
  the whole platform.
- `GET /api/people/search?q=` finds someone by name or email and needs **no permission at all**. It exists
  so that naming a speaker in your own recording never requires one. It returns nothing below two
  characters.
- `GET /api/people/duplicates` reports entries that look like the same human, matched by email or by name.
  It only reports - merging is left to you, because it deletes the source record and cannot be undone.

Erasing a voiceprint and keeping the person are separate operations, deliberately:
`DELETE /api/people/{id}/voiceprint` destroys the biometric and leaves the person in place, while
`DELETE /api/people/{id}` removes them entirely.

Note this replaces the older `/api/speaker-profiles` endpoints, which no longer exist.

## A common pattern: run a formula and collect the result

There are no special endpoints for this - it is the ordinary formula API.

1. `POST /api/recordings/{recordingId}/formulas/{formulaId}/run` returns **202** immediately with the id
   of a result that is still generating. This is a write, so a read-only token cannot do it.
2. `GET /api/recordings/{recordingId}/formula-results/{id}` returns the status: `Generating`, `Ready`,
   or `Failed`. Poll until it settles, then read the Markdown.

If you would rather not poll, subscribe to the **A formula finishes** event instead - see
[Automations and Workflow Signals](/help/automations-and-signals).

## Related

- [Building an n8n workflow](/help/n8n-workflows) - a purpose-built node, generated from this same API.
- [Building a Zapier workflow](/help/zapier-workflows) - webhooks plus custom requests.
- [Connecting Claude (MCP)](/help/claude-mcp-setup) - a different door, for Claude specifically.
