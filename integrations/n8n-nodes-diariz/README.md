# n8n-nodes-diariz

An [n8n](https://n8n.io) community node for [Diariz](https://github.com/kenhayward/Diariz), a self-hosted
meeting transcription platform with speaker diarization.

Two nodes and one credential:

| Node | What it does |
|---|---|
| **Diariz Trigger** | Starts a workflow when something happens in Diariz. Registers its own webhook and verifies every delivery's signature. |
| **Diariz** | Drives the Diariz REST API: recordings, transcripts, formulas, folders, search, action items, chat, and everything else. |

## Installation

In n8n, go to **Settings > Community Nodes > Install** and enter:

```
n8n-nodes-diariz
```

Self-hosted n8n only - community nodes cannot be installed on n8n Cloud.

## Before you start

Two things must be switched on by whoever administers your Diariz server, in **Settings**:

| Setting | Needed for |
|---|---|
| **API access** | Everything. Without it, no personal API token works at all. |
| **Automations** | The Diariz Trigger only. Action nodes work without it. |

Then create a token in Diariz under **Settings > Developers**:

- **Read-only** tokens are enough for reading transcripts and searching. Every write operation is refused.
- A token can carry an **expiry date**, which is worth setting for a token pasted into another tool.
- The token is shown once. Copy it straight into the n8n credential.

## Credential

Create a **Diariz API** credential with:

- **Base URL** - your server, for example `https://diariz.example.com`
- **API Token** - the `dz_api_...` token from above

Press **Test**. As well as checking the token, the test reports whether API access or Automations are turned
off on the server, so you find out now rather than at the first execution.

## Diariz Trigger

Pick one or more events:

| Event | Fires when |
|---|---|
| Recording Created | A recording is uploaded or captured, before transcription |
| Recording Transcribed | The transcript is ready, with speaker labels and timings |
| Transcription Failed | A recording could not be transcribed |
| Summary Ready | The AI summary is ready. The text rides along in the event. |
| Meeting Minutes Ready | The minutes document is ready, and rides along in the event |
| Action Items Ready | Action items were extracted from the transcript |
| Tags Ready | Topic tags were generated |
| Formula Result Completed | A formula finished and produced a document |
| Formula Result Failed | A formula run failed |

**Simplify** (on by default) returns just the event's data. Turn it off to get the full envelope with the
event ID, type and timestamp.

### How it works

Activating the workflow creates a subscription in Diariz pointing at this node's webhook URL, and stores the
signing secret Diariz returns. Deactivating deletes it again. You will see it in Diariz under
**Settings > Automations**, named after your workflow.

Every delivery is verified against that secret using the
[Standard Webhooks](https://www.standardwebhooks.com) signature scheme. An unsigned or tampered request is
rejected with a 401 and starts no execution.

### Things worth knowing

- Diariz allows **20 automations per user**. Each active Diariz Trigger uses one, and clicking
  **Listen for test event** briefly registers a second, temporary one.
- Failed deliveries are retried with a growing backoff over about 24 hours, so a workflow that was briefly
  down still receives its events. Watch **Settings > Automations** in Diariz for the delivery log.
- If a workflow is not firing, check the delivery log there first - it records the HTTP status your n8n
  instance returned.

## Diariz (action node)

Every published Diariz endpoint is available, grouped by resource. Several are given extra handling:

- **Dropdowns.** Recording, folder, room, formula, speaker profile and meeting type fields list your actual
  records rather than asking for an ID.
- **Files.** Transcript exports (text, Markdown, RTF, subtitles), audio, attachments and formula documents
  come back as binary data. Uploading a recording or attaching a file takes binary data from a previous node.
- **Return All / Limit** on anything that returns a list.
- **Run a formula over a recording** has **Wait for Completion** on by default. Diariz answers immediately
  with a document that is still generating, so the node polls until it is finished and returns the Markdown.
  Turn it off if a Diariz Trigger will pick up the completion event instead.
- **Ask a question** consumes the chat stream and returns one finished answer, with any citations.
- **Custom API Call** on every resource, for anything added to the API after this version was published.

## Example: summarise a meeting and post it to Slack

1. **Diariz Trigger** - event **Recording Transcribed**
2. **Diariz** - resource **Formula**, operation **Run a formula over a recording**
   - Recording ID: `{{ $json.recordingId }}`
   - Formula: pick yours from the dropdown
   - Wait for Completion: on
3. **Slack** - post `{{ $json.text }}`

Add a second trigger event, **Summary Ready**, if you would rather act on the summary that Diariz generates
automatically than run a formula of your own.

## Compatibility

Requires Node.js 20.15 or newer, matching n8n's own requirement. Tested against Diariz 0.159 and later; the
AI-output trigger events (Summary Ready, Meeting Minutes Ready, Action Items Ready, Tags Ready) need 0.159 or
newer, and everything else works with 0.153 and newer.

## Development

This node lives in the [Diariz repository](https://github.com/kenhayward/Diariz) under
`integrations/n8n-nodes-diariz`, so it stays in step with the API it wraps.

```bash
npm install
npm test      # unit tests
npm run lint  # n8n community node linter
npm run build
```

The long tail of operations is generated from Diariz's own OpenAPI document
(`nodes/Diariz/generated/openapi.snapshot.json`). Regenerate with `npm run generate`. Continuous integration
regenerates both the snapshot and the operations on every build and fails if either has drifted, so the node
cannot fall behind the API unnoticed.

## Licence

[MIT](LICENSE). The Diariz platform itself is AGPL-3.0.
