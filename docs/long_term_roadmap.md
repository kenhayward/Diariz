# Diariz Long-Term Roadmap

This document sets out the next major arc of product work beyond the currently shipped feature set
(capture, server-side transcription with diarization and voiceprints, template-driven minutes, action
items, cross-library chat/RAG, MCP, and calendar integration). It is an internal planning document: it
carries no version number and does not gate releases. The
[synopsis](Overall_Synopsis_of_Platform.md) and [schema](Data_Schema.md) docs remain the source of
truth for what exists today.

## Guiding principles

Every item below is designed to hold the line on what makes Diariz distinctive:

- **Self-hosted and sovereign.** No capability may require sending audio or transcripts to a third party
  that the operator has not configured. Bring-your-own-LLM and on-prem storage remain the default.
- **Opt-in and governable.** New capture and sharing behaviours are **off by default** and controllable
  at the platform level, so a regulated deployment can disable them wholesale. Convenience never
  overrides consent.
- **Auditable.** Anything that captures automatically, shares data between people, or acts on a user's
  behalf leaves a record the operator can inspect.
- **Composable.** New features reuse the existing pillars (Meeting Types, the minutes generator,
  the job queue, calendar links, email/Gmail, MCP) rather than growing parallel stacks.
- **Delivered in thin, tested slices.** Each pillar ships as a sequence of small PRs under the project's
  TDD and one-release-per-PR conventions.

---

## Focus themes (next arc)

Four themes make up the near-to-mid-term focus. They are listed in the recommended build order, which is
driven by architectural risk and by how much each one leans on infrastructure that already exists.

| # | Theme | Why now | Primary risk |
|---|---|---|---|
| 1 | **Note enhancement** (write rough notes, get polished minutes) | Highest value per unit of effort; extends the existing minutes pipeline | Low |
| 2 | **Workflows / automations** (trigger → action rules) | Reuses the queue + email/Gmail/MCP infrastructure; multiplies existing features | Low-medium |
| 3 | **Shared spaces** (collaborative containers) | Unlocks team use; requires the deepest change to the ownership model | High |
| 4 | **Ambient capture** (optional auto-record + calendar auto-link) | High convenience; streaming transcription is genuinely hard, so it goes last | Medium-high |

Longer-term items (macOS shell, mobile capture, live collaborative editing) are deliberately deferred
until this set has settled - see [Deferred](#deferred-longer-term).

---

## Theme 1 - Note enhancement

**The interaction.** During (or after) a recording, the user types sparse, freeform notes - shorthand,
bullet fragments, a decision here, a to-do there. On demand, Diariz produces polished minutes that
*follow the user's own notes as the backbone* and use the transcript to fill in detail, correct
shorthand, and add context the user did not have time to write. The user's raw notes are never
discarded.

**Why this matters.** It changes the felt experience from "the machine wrote the notes" to "the machine
finished my notes." The output reflects what the note-taker judged important, not just what the model
found salient.

**How it builds on what exists.**

- A new lightweight **note store** attached to a recording (freeform Markdown, optionally with
  time-anchors that map a line to a transcript position). It sits alongside `MeetingMinutes` and reuses
  the same "user-edited is protected from regeneration" rule already in place for summaries and minutes.
- The **minutes generator** (`MeetingTypeMinutesGenerator` + strategies) gains an optional *user-notes*
  input and a generation mode/preamble that instructs the model to treat the notes as the outline and
  the transcript as supporting evidence. This composes with Meeting Types: a template can declare how
  notes are woven in, and both per-section and single-call modes still apply.
- The transcript panel gains a **notes pane**; flagged lines (decisions, to-dos) can seed action items
  through the existing extraction path.
- Presentation reuses the established **original-vs-revised** pattern so the user can always see their
  raw notes next to the enhanced output.

**Delivery sketch.**

1. Note entity + storage + API + minimal editor (type and save notes on a recording).
2. Generator accepts user notes; new "enhance from notes" preamble/mode; behind Meeting Types.
3. Flagged-line to action-item seeding; original-vs-enhanced toggle.

**Open questions.** Whether notes are per-user or shared once Shared Spaces land (Theme 3); how
time-anchoring behaves after a re-transcribe changes segment boundaries.

---

## Theme 2 - Workflows / automations

**The idea.** A **Workflow** is an operator- or user-defined rule of the shape **Trigger → Conditions →
Actions** that runs automatically as a recording moves through the pipeline. It turns the features Diariz
already has into a hands-off assembly line.

**Shape of the model.**

- **Triggers** hook the existing pipeline stages: transcription complete, minutes generated, actions
  extracted, a Meeting Type applied, a calendar match made, or a recording added to a space.
- **Conditions** gate a run: Meeting Type is X, space is Y, transcript/attendees contain a keyword,
  duration over a threshold, language is Z.
- **Actions** reuse existing capabilities: apply a specific Meeting Type / generation mode, generate
  minutes / summary / actions, translate into a language, email the owner, create a Gmail draft, save
  output to the recording as an attachment, **call an outbound webhook** with a templated payload, or
  notify a space's members. Actions are an extensible **registry**, mirroring the chat-tools registry
  pattern already used for the MCP/chat tools.

**Why a webhook is the integration primitive.** Rather than build and maintain a catalogue of native
connectors, a first-class **signed outbound webhook** action lets an operator wire Diariz into whatever
they already run (chat, docs, CRM, a queue) through their own automation layer - without any data
leaving the operator's control except where they explicitly send it. Native connectors can follow later
where demand is clear; the webhook keeps the self-hosted ethos intact meanwhile.

**Governance.** The Platform Administrator controls which action types are permitted (for example,
outbound webhooks can be disabled entirely in a locked-down deployment). Every workflow can be enabled or
disabled, and every run is recorded in a **run log** for audit.

**How it builds on what exists.** A `Workflow` definition (trigger/conditions/actions as JSON, following
the `ContentJson` precedent), an evaluation hook invoked from the existing transcription/summarization/
actions completion points, an action executor over the registry, and a run-history table. The queue,
email/Gmail path, and MCP surface are all reused.

**Delivery sketch.**

1. Workflow entity + a small set of triggers and built-in actions (auto-apply Meeting Type, auto-generate
   minutes, auto-email/draft) + platform gating + run log.
2. Conditions engine + webhook action (signed, templated) + more actions.
3. Space scoping (Theme 3) and a broader action catalogue.

---

## Theme 3 - Shared spaces

Today every recording is owned by one user and every query is scoped to that user. Shared Spaces
introduce **collaborative containers** so a team can work over a common set of meetings. This is the
largest architectural change in this roadmap and is described in detail because the model needs to be
agreed before code.

### What a Space is

A **Space** is a named container with **members** that holds shared recordings (and their transcripts,
minutes, actions, and attachments), shared **Meeting Type** templates, and - optionally, and only when
explicitly enabled - shared **people/voiceprints**. A user can belong to several spaces and also keep a
private area that no space can see.

### Membership and roles

- A Space has members, each with a **role**: **Owner/Admin** (manage members, settings, delete),
  **Contributor** (add recordings, edit minutes, rename speakers), **Viewer** (read, chat, export).
- Members are invited by email; existing user onboarding and the platform role model are reused. The
  Platform Administrator governs whether spaces may be created at all, mirroring how platform-wide
  settings already work.

### The ownership change (the crux)

- `Recording` gains an optional **SpaceId**. A recording is either **personal** (owner only, `SpaceId`
  null) or **in a space** (`SpaceId` set).
- The ownership check on every recording-scoped endpoint changes from *"the caller is the owner"* to
  *"the caller is the owner **or** a member of the recording's space with a sufficient role."* This must
  be applied consistently across every read and write path - it is the single most important correctness
  and security requirement of this theme, and it is where the integration-test effort concentrates.
- Users can **move** a personal recording into a space (granting members access) and, with the right
  role, move it back out.

### What is shared, and what stays personal

| Data | Default | Notes |
|---|---|---|
| Recordings, transcripts, minutes, actions, attachments | Shared within the space | The point of the feature |
| Meeting Type templates | Shareable to a space | Extends today's Personal / Platform scopes with a Space scope |
| Chat / RAG | Scoped to the space | "Chat across this space's meetings"; the existing all-meetings mode widens to space membership |
| People / voiceprints | **Personal by default** | Biometric data. A space *may* opt in to a shared people set so a person is recognised across the team's meetings, but this is off unless enabled, and remains erasable per the existing GDPR controls |

### Collaboration mechanics

- **Two granularities of sharing:** join a space (see everything in it) and, later, share a *single*
  recording into a space or with named users for finer control.
- **Activity log.** Each recording (and the space) records who did what - renamed a speaker, edited
  minutes, added a recording. This serves both collaboration ("who changed this") and compliance.
- **Notifications.** Members can be notified when a new recording lands or minutes become ready (reusing
  the SignalR + email paths).
- **Concurrent minutes editing.** The MVP uses last-write-wins with the existing user-edited protection
  plus the activity log; true real-time collaborative editing is a later stretch (see Deferred).

### Quota and governance

- **Quota accounting** needs a decision: recordings either count against the **uploader's** personal
  quota or against a **space-owned pool** set by an admin. Recommended starting point: space-owned pool,
  because it matches how teams reason about shared storage. Flagged as an explicit design decision.
- Space deletion, leaving a space, and member removal all **revoke access** cleanly (cascade rules
  mirror the existing owner-cascade behaviour and must be verified against real Postgres).

### Delivery sketch

1. `Space` + membership + roles; `Recording.SpaceId`; the owner-or-member access rule everywhere;
   a space switcher and member management UI; move-into-space. Heavy integration-test coverage of the
   access rule and cascades.
2. Per-recording sharing, activity log, notifications, space-scoped Meeting Types and chat.
3. Opt-in shared voiceprints; workflow triggers/actions scoped to a space.

### Open questions

- Shared-voiceprint default and consent flow (biometric, so conservative).
- Quota model (per-uploader vs space pool).
- Guest / external (non-user) access - likely out of scope initially.

---

## Theme 4 - Ambient capture (optional)

**The idea.** For users who want it, Diariz can capture a meeting **without a manual start**: when a
calendar event with a meeting is active, capture begins automatically, and the resulting recording is
**auto-associated** with that event (name, attendees, and calendar link pre-filled from the existing
calendar integration).

**Consent is the headline, not a footnote.** This is exactly the behaviour a regulated deployment may not
want running unattended, so it is built consent-first:

- **Off by default.** Ambient capture is a mode the user must explicitly turn on.
- **Platform kill switch.** The Platform Administrator can disable ambient capture for the whole
  deployment, or allow it only for specific roles/users.
- **Per-meeting control.** Modes ranging from "ask me before each meeting" to "auto-record", plus
  **exclusion rules**: skip events on certain calendars, matching keywords (for example private or 1:1),
  or with particular attendees; honour a "do not record" marker on an event.
- **Always visible.** A clear recording indicator and start notifications; nothing captures silently.
- **Auditable.** Ambient sessions are logged (what was captured, when, against which event).

**Phasing (the transcription question).** Live transcription with reliable diarization is hard, so the
theme is split so value lands early:

- **Phase 1 - ambient capture + auto-link, still batch.** Capture starts automatically and the recording
  is created and linked to the calendar event; transcription runs through the normal server-side pipeline
  when the meeting ends. This delivers the "it just recorded the right meeting for me" convenience with
  no change to the proven transcription path.
- **Phase 2 - near-real-time transcription.** Stream audio to the worker in windows and surface partial
  segments over SignalR during the meeting. Because live diarization is unreliable, the design shows
  interim un-attributed text live and **reconciles to full pyannote diarization + voiceprints once the
  recording finalises** - keeping today's speaker-identification quality rather than regressing it for the
  sake of being live.

**How it builds on what exists.** The desktop shell already owns tray capture and Windows loopback; the
calendar integration already matches recordings to events. Phase 1 adds a calendar poll + capture trigger
+ indicator in the desktop main process and the platform policy controls. Phase 2 adds a streaming path
to the worker and incremental SignalR updates.

**Delivery sketch.**

1. Platform + per-user ambient policy and consent controls (no capture yet) - the governance scaffold.
2. Desktop ambient trigger from the calendar; auto-create + auto-link; visible indicator; audit log.
3. Streaming/near-live transcription with post-meeting diarization reconciliation.

---

## Cross-cutting foundations

Some work underpins several themes and is best built once, early:

- **Consent and policy framework** (platform + per-user toggles, exclusion rules) - shared by ambient
  capture and, in spirit, by shared spaces and workflows.
- **Activity / audit log** - used by shared spaces (who changed what), workflows (run history), and
  ambient capture (what was captured).
- **Action/registry pattern reuse** - workflows and the existing chat/MCP tools should share one
  extensible registry idiom.

---

## Deferred (longer-term)

Explicitly out of scope for this arc, to be revisited once the four themes settle:

- **macOS desktop shell** and **mobile capture** (iOS/Android). Planned, but after the collaborative and
  note-taking feature set is stable, so the mobile/desktop clients target a settled surface.
- **Real-time collaborative editing** of minutes (concurrent multi-cursor / CRDT).
- **First-class native connectors** (beyond the generic webhook) where specific integrations earn their
  keep.
- **Shared voiceprints** across spaces graduating from opt-in experiment to a default, if consent and
  governance patterns prove out.

---

## Success signals

- **Note enhancement:** users routinely type notes and accept the enhanced minutes with light editing;
  minutes reflect the note-taker's structure.
- **Workflows:** common post-meeting steps (apply type, generate minutes, deliver output) run with no
  manual action; operators can wire Diariz into their stack without new code.
- **Shared spaces:** teams work from a common set of meetings with correct, auditable access control and
  no ownership leaks.
- **Ambient capture:** users who opt in stop starting recordings by hand, while regulated deployments can
  prove it is off.
