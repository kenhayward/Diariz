# Changelog

This package versions independently of the Diariz platform, so a node fix can ship without a platform
release and n8n users can pin a version that means something to them.

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
