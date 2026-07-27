---
title: Connecting Claude (MCP)
summary: Diariz hosts an MCP server, so Claude can search and read your meetings. Set up claude.ai with OAuth, or Claude Desktop and Claude Code with a personal token.
group: advanced
order: 50
---

Diariz hosts a **Model Context Protocol** server at `/mcp`. Connecting Claude to it lets Claude search
your transcripts, read minutes and action items, and run your formulas.

Everything below lives in **Preferences**, on the **Claude Access** tab.

A Platform Administrator can turn this off for everyone with the **Claude / MCP access** switch at
**Settings**, **Integration**. It is **on** by default.

## Which method to use

| Client | Method |
|---|---|
| **claude.ai** (web) | OAuth. A static token does not work here. |
| **Claude Desktop** | Personal token, bridged through `mcp-remote`. |
| **Claude Code** | Personal token, direct. |

## Generating a personal token

On the **Claude Access** tab, name the token after where you will use it (for example "Claude Desktop")
and click **Generate token**.

**The token is shown once.** Only a hash of it is stored, so if you lose it you generate a new one. You
can have as many named tokens as you like, and revoke any of them individually.

An MCP token works **only** on `/mcp`. It is rejected on the REST API, and an API token is rejected on
`/mcp`.

## Claude Code

One command:

```
claude mcp add --transport http diariz https://your-server/mcp --header "Authorization: Bearer dz_mcp_..."
```

## Claude Desktop

Claude Desktop only speaks to local programs, so it bridges to Diariz through `mcp-remote`. **This needs
Node.js installed.**

Click **Show Claude Desktop config** on the Claude Access tab and copy the generated snippet - it already
has your server address and token filled in:

```json
{
  "mcpServers": {
    "diariz": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "https://your-server/mcp", "--header", "Authorization:${AUTH}"],
      "env": { "AUTH": "Bearer dz_mcp_..." }
    }
  }
}
```

The token goes in `env` rather than straight into `args` on purpose. `mcp-remote` splits arguments on
spaces, which would break `Bearer <token>` in half.

## claude.ai (the web connector)

The website connects by signing in to Diariz, not by pasting a token.

1. In claude.ai, add a **Custom Connector** pointing at `https://your-server/mcp`.
2. You are redirected to Diariz to sign in.
3. A consent screen tells you what the connector will be able to do. Click **Allow**.

The connector can search and read your meetings, transcripts, minutes and action items, and email
content **to your own registered address only** - it can never email anyone else.

Revoke it any time from the **Web connections** list on the same Claude Access tab.

## What Claude can do once connected

Claude gets the same tools the in-app chat has, respecting your per-tool choices in **Chat Tools**:
finding who said something, what a person said about a topic, searching transcripts, when a topic came
up, counting mentions, listing recordings and action items, summaries, attendees, talk time, transcript
context, minutes, and recording details.

Two of them **act** rather than read: **send an email** (always to your own address) and **run a
formula**.

Your recordings are also exposed as resources, so you can @-mention a specific meeting, and three
starter prompts are available: summarise the last meeting, open action items, and find a discussion on a
topic.

## If Claude cannot connect

Almost every failure is a reverse-proxy problem. If you run Diariz behind your own proxy, it must:

- Forward **`/mcp`** to the API **with response buffering off**. The connection is a stream; buffering
  stalls it. Without this you get the web app served at `/mcp`, or a 405.
- Forward **`/connect/`** and **`/.well-known/`** too, or an OAuth client gets HTML instead of the
  metadata it needs and the claude.ai connection never starts.
- **Not** proxy `/oauth/consent` - that is a page in the web app.
- Pass **`X-Forwarded-Proto: https`** through. Without it the server rejects its own OAuth endpoints as
  insecure.
