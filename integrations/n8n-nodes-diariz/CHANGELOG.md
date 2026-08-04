# Changelog

This package versions independently of the Diariz platform, so a node fix can ship without a platform
release and n8n users can pin a version that means something to them.

## 0.177.0

### Added

- **Scope** on the Diariz Trigger: **Personal** (the default, and what the node always did) or **Platform
  (Administrator)**. A platform subscription listens across every user, is routed by Workflow Signal, and is
  registered through the admin webhook endpoint rather than the personal one.
- **Feedback Received** (`feedback.submitted`) as a Platform-scope trigger event, firing when someone
  submits feedback through **Provide Feedback** in Diariz. It carries who sent it, the page they were on and
  the release. Platform scope only, because feedback is readable solely by a Platform Administrator - a
  personal subscription would deliver one user's words to another.
- **Include Feedback Text**, off by default, adds what the person actually wrote. The description is free
  text and may quote meeting content, so it only leaves Diariz on request.
- **Signal Filter** picker on the trigger, listing the Workflow Signals defined in Diariz. Required for
  signal-routed events; Feedback Received carries no signal and needs none.

### Notes

- Existing workflows are unaffected: Scope defaults to Personal, and a node saved without one behaves
  exactly as before.
- Changing Scope rebuilds the subscription, since personal and platform subscriptions are separate objects
  at separate endpoints. The old one is deleted from where it was created.
- Feedback Received needs Diariz **0.177.0** or newer on the server.

## 0.1.0

First release.

### Added

- **Diariz Trigger** node: a self-registering webhook trigger covering all nine Diariz events, including the
  four AI-output events (Summary Ready, Meeting Minutes Ready, Action Items Ready, Tags Ready). Every
  delivery is verified against the Standard Webhooks HMAC signature; an unverified request is rejected with a
  401 and starts no execution. The subscription is created on activation and deleted on deactivation.
- **Diariz** action node: all 179 published REST operations across 31 resources, generated from the
  platform's own OpenAPI document, plus a Custom API Call on every resource.
- Dropdowns listing your real recordings, folders, rooms, formulas, speaker profiles and meeting types
  instead of raw IDs.
- Binary handling: transcript exports, audio, attachments and formula documents download as files; recording
  uploads and file attachments take binary data from a previous node.
- Return All / Limit on every operation that returns a list.
- **Wait for Completion** on "Run a formula over a recording", which polls until the document is finished
  rather than returning one that is still generating.
- Chat questions consume the server-sent event stream and return a single finished answer with citations.
- **Diariz API** credential whose test also reports when API access or Automations are turned off on the
  server, so a misconfigured instance is diagnosed at save time.

### Notes

- The `Auth` endpoint group is deliberately excluded: it takes an account password, and this node
  authenticates with a token. Custom API Call still reaches it if you need it.
