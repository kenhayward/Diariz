# CRM Component Analysis — Open Source Landscape

> **Provenance.** Sections 1–4 were written in a Claude chat session (2026-07-30) from 2026
> vendor/benchmark write-ups; treat their pricing and feature claims as directional, not contractual.
> Sections 5 onward were written in a Claude Code session against the Diariz repository on the same
> day, and every claim about Diariz carries a file reference so it can be re-checked as the code moves.
>
> **Status: research only.** Nothing here has been built. No CRM has been installed or called.

## 1. Reference component model

### Data layer
- Contact / account / lead model — people, organisations, hierarchies, dedupe and merge rules
- Activity history — calls, emails, meetings, notes, timestamped against records
- Custom objects and fields — schema extension without forking the product

### Sales operations
- Pipeline / opportunity management — stages, weighted forecasting, close probability
- Quoting, products, price books, contracts (CPQ)
- Territory, team and quota assignment

### Marketing
- Campaign management and attribution
- Email marketing, sequences, drip automation
- Lead scoring and routing

### Service
- Case / ticket management, SLAs, escalation
- Knowledge base and self-service portal

### Cross-cutting platform capabilities
- Workflow automation — triggers, conditions, actions, approvals
- Reporting, dashboards, analytics
- Integration surface — REST/GraphQL API, webhooks, iPaaS, email/calendar sync, telephony (CTI)
- Security model — RBAC, field-level permissions, record ownership, audit trail
- Compliance — GDPR/consent, data residency, retention, encryption
- Admin and governance — sandbox/dev environments, versioned config, migration tooling
- Mobile and offline access
- AI layer — summarisation, next-best-action, forecasting

## 2. Open source coverage against the model

| Component | Open source position |
|---|---|
| Contacts, accounts, activities, pipeline | Fully covered everywhere. Solved problem. |
| Custom objects and fields | Strong. EspoCRM ~90% configurable from admin panel, no fork. Twenty easiest to extend in code. Odoo Community needs code (Studio is Enterprise-only). |
| Quoting / CPQ / contracts | Thin except SuiteCRM (quoting, contracts, projects) and Odoo via ERP modules. |
| Marketing automation | Weakest area. Basic campaigns only; multi-channel nurture means bolting on Mautic/Listmonk. |
| Lead scoring | Rule-based, rarely model-driven. Usually pushed to n8n or a custom service. |
| Case management / portal | SuiteCRM, EspoCRM, Odoo yes. Absent from Twenty, Krayin, Atomic CRM. |
| Workflow automation | Adequate in-product; pragmatic pattern is webhooks out to n8n or Temporal. |
| Reporting / BI | Operational reports only. Serious deployments export to a warehouse. |
| API | Good. All majors expose REST. Twenty ships GraphQL + REST. Odoo uses XML-RPC / JSON-RPC. |
| Security / compliance | Genuine strength. Self-hosted SuiteCRM used in GDPR/HIPAA/SOC 2 contexts; YetiForce targets EU GDPR. |
| Mobile | Notable gap. Odoo native apps, SuiteCRM via partners; most others web-responsive only. |
| AI features | Largest gap vs SaaS. No Einstein/Copilot equivalent — build it. |
| Sandbox / ALM tooling | Weak across the board. Config promotion between environments is DIY scripting. |

## 3. Candidate shortlist

| Product | Stack | Licence | Integration surface | Best fit |
|---|---|---|---|---|
| Twenty | TypeScript / React | AGPL-3.0 | GraphQL + REST | Developer-extensible core CRM, modern UX |
| EspoCRM | PHP / MySQL | GPL-3.0 | REST | No-code configurability, light hosting footprint |
| SuiteCRM | PHP | AGPL-3.0 | REST | Most feature-complete, Salesforce-replacement scope |
| Odoo Community | Python | AGPL-3.0 | XML-RPC / JSON-RPC | CRM inside a broader ERP |
| Krayin | PHP / Laravel | MIT | REST | Permissive licence, commercial redistribution |

## 4. Decision factors carried forward

1. **Licensing.** AGPL-3.0 (Twenty, Odoo, SuiteCRM) is unrestricted for internal self-hosting, but
   the network clause bites if modified code is offered as a hosted service. MIT (Krayin) has no
   such constraint. Material if anything built on top becomes customer-facing.
2. **Total cost.** Licence-free is not cost-free — hosting, implementation, customisation,
   integration, training, maintenance. Indicative three-year figures: self-hosted Twenty roughly
   $4.5k–$10.8k vs Salesforce Enterprise $86k–$132k at comparable seat counts. The trade is licence
   spend for engineering time.
3. **Gap ownership.** Marketing automation, AI and mobile are where build effort lands regardless of
   product choice.

---

## 5. Open questions — answered against the codebase

### 5.1 What is Diariz's relationship to CRM?

**Integration source. Not a data sink, and explicitly not a parity goal.**

Diariz produces *meeting evidence* — a transcript, identified attendees, and four LLM-derived outputs.
A CRM owns contacts, accounts, deals and activity history. The two meet at exactly one place: the CRM's
**activity record**. Diariz has no opportunity, account, quote or case object and no reason to grow one;
section 2 shows that whole column is a solved problem in open source.

The strategic read runs the other way and is worth stating plainly, because it is the reason this
research is worth doing: **the AI layer is the single largest gap in every open-source CRM** (section 2,
last-but-one row) and it is the only thing Diariz has. Diariz is not competing with these products, it
is the missing row in their feature table. "Expand its reach" therefore means *becoming the meeting-
intelligence layer an OSS CRM cannot build for itself*, not absorbing CRM functionality.

The repo has already committed to this shape. `docs/long_term_roadmap.md:118-122` argues that a signed
outbound webhook is the integration primitive precisely so that Diariz can wire into "chat, docs, CRM, a
queue" through the operator's own automation layer, rather than maintaining a catalogue of native
connectors — "Native connectors can follow later where demand is clear." CRM integration is that
argument's first real test, and the honest starting position is that **most of it already ships**.

A **narrow inbound direction** is still worth having: pulling CRM contacts into the People directory so
that external attendees arrive already named. That is enrichment, not a data sink — Diariz would hold a
cached name/email/company, never deals or pipeline.

### 5.2 Which surface is the join key between a recording/participant and a CRM contact?

There are three candidate surfaces in the codebase, and the answer is that **`Person` is the join
entity, `Person.Email` is the matching key, and `Person.Id` should become the persisted link.**

**The person model** (`src/Diariz.Domain/Entities/Person.cs`) is already a contact-lite record:
`Name`, `Title`, `CompanyName`, `Email`, `Phone`, and `IsInternal`. That last field matters more than it
looks — `IsInternal = false` marks the *customer side* of a meeting, which is exactly the set of
attendees a CRM cares about. A voiceprint is an optional attribute of a person, not the person, so
someone added by hand with no biometric is a first-class directory entry.

**How a speaker becomes a person.** `Speaker.PersonId` (`Entities/Speaker.cs:26`) links a diarization
slot to a directory person, set either by automatic voiceprint identification (`IdentifiedAuto = true`)
or by a manual assignment in the UI. So the participant→contact chain is
`Segment → Speaker → Person → CRM contact`.

**What the outbound payload actually carries.** `AttendeePayload.ForRecordingAsync` puts an
`attendees[]` array on every `recording.*` event with `label, name, personId, isMultiSpeaker,
identifiedAuto, isInternal`, plus `title, companyName, email, phone`
(`docs/Overall_Synopsis_of_Platform.md:790-807`).

> **The one trap that will bite a CRM automation first.** The contact fields are behind
> `WebhookSubscription.IncludeAttendeeContacts`, which is **off by default, per subscription**
> (`Entities/WebhookSubscription.cs:31-35`). A CRM automation built on the default subscription
> receives names but **no email addresses**, i.e. no join key at all. Worse, an n8n-created
> subscription is owned by the Diariz Trigger node and re-created on every publish, so the toggle must
> be set *in the node*, not in the Diariz UI (`Overall_Synopsis_of_Platform.md:1466-1472`). Any CRM
> recipe has to open with "turn on Include Attendee Contacts."

**Key ranking:**

| Rank | Key | Strength | Weakness |
|---|---|---|---|
| 1 | `Person.Id` (GUID) written into the CRM as a `diariz_person_id` custom field | Stable, survives renames and email changes, unambiguous | Requires a one-time match *and* a custom field on the CRM contact; Diariz stores no reciprocal link today |
| 2 | `Person.Email`, lower-cased | The only natural key both sides share | **Nullable** and never verified; absent for guests; absent from the payload unless the contacts gate is on |
| 3 | Email **domain** → CRM account | Cheap account/company resolution for external attendees | Breaks on gmail.com-style addresses and on consultancies |
| 4 | `Person.CompanyName` → CRM account name | Present without email | Free text, no account entity, no dedupe, no normalisation |
| 5 | Calendar attendee list via `RecordingCalendarLink` | Gives emails for people never voice-printed, and the organiser | Fetched **live** from Google and never stored (`Overall_Synopsis_of_Platform.md:1235-1237`), so it is unavailable in an async webhook consumer and disappears when the event is deleted |

**Recommended pattern:** match once on email (rank 2), then persist the mapping in **both** directions —
`diariz_person_id` on the CRM contact, and a CRM contact id back on the Diariz side. The Diariz side of
that does not exist yet; see gap G1 in section 8.

**Deduplication is a shared problem, and Diariz already has an opinion about it.**
`PersonDuplicates` reports likely duplicates by email and normalised name and deliberately
**never merges automatically** (`Overall_Synopsis_of_Platform.md:1445-1454`). A CRM sync must inherit
that stance: propose, never auto-merge. An automation that creates a contact on every unmatched
attendee will pollute the CRM within a week.

### 5.3 Does the AGPL network clause apply?

**Not to a wire-protocol integration. But the real constraint is Diariz's own dual licence, not the
CRM's AGPL.**

Diariz is itself **AGPL-3.0** (`LICENSE`, `NOTICE`) **dual-licensed with a private commercial
agreement** (`README.md:160-174`). That changes the analysis from the one sketched in section 4.

- **Talking to a CRM over HTTP does not combine the works.** Diariz POSTs a signed webhook to a CRM's
  REST endpoint, or a CRM calls Diariz's REST/MCP API. No CRM source is copied, linked or distributed.
  Each program stays under its own licence. The AGPL network clause obliges you to publish *the CRM's*
  corresponding source only if you **modify the CRM** and offer that modified version over a network —
  and an unmodified upstream deployment triggers nothing.
- **The dual licence is the thing to protect.** A commercial Diariz licence can only be granted over
  code whose copyright the licensor controls. Anything AGPL or GPL **vendored into this repository,
  linked into the API, or forked as a Diariz component** would be unrelicensable and would quietly
  break the commercial option. So the rule is: **HTTP only. Never vendor a CRM, never fork one into
  this tree, never take a GPL/AGPL client library as a dependency of `Diariz.Api`.**
- **The repo already has two precedents for exactly this discipline.** MinIO is AGPL-3.0 and is used
  "unmodified as a separate container", which the README states does *not* impose copyleft on Diariz's
  own code (`README.md:191-193`). And the n8n node carries its own MIT licence inside its package
  directory while "the repository root stays AGPL-3.0" (`Overall_Synopsis_of_Platform.md:903`). A CRM
  integration should follow the second pattern if any client code ships: a separate, separately
  licensed package, not a `Diariz.Api` dependency.
- **Check client SDKs separately from the product.** A CRM's licence does not govern its client
  libraries. EspoCRM is GPL-3.0 rather than AGPL, which is *more* permissive about network use and
  equally fatal if linked. Odoo's client surface is XML-RPC/JSON-RPC — a wire protocol, so no library
  is strictly required at all.
- **Nothing changes for self-hosters.** A user who modifies their own Diariz already owes AGPL
  obligations; adding a CRM alongside it introduces no new ones.

**Net:** licensing does not constrain *which* CRM to target, only *how* to talk to it. That collapses
decision factor 1 in section 4 for this use case — and it makes Krayin's MIT licence much less of an
advantage than it first appeared, since we are not redistributing CRM code either way.

### 5.4 Which AI pipeline outputs map onto CRM activity records?

There are more than four. The pipeline emits **six** things a CRM could consume, and the sixth is the
one that makes this tractable.

| Diariz output | Webhook event | CRM target | Fit |
|---|---|---|---|
| **Summary** (`Entities/Summary.cs`) | `recording.summarized` | Activity / meeting-log **note body** | **Strong.** The canonical "what happened". Carried **inline** in the event, so no second REST call. |
| **Meeting minutes** (`Entities/MeetingMinutes.cs`) | `recording.minutes_ready` | Long-form **note or attachment** on the activity | **Strong but bulky.** GitHub-flavoured Markdown, template-driven per Meeting Type; usually too long for a CRM note field. |
| **Action items** (`Entities/RecordingAction.cs`) | `recording.action_items_ready` | **Tasks** | **Closest 1:1 object match in the whole model**, and the only genuinely bidirectional candidate. See caveats below. |
| **Tags** (`Entities/RecordingTag.cs`) | `recording.tags_ready` | Activity **category / topic picklist** | **Moderate.** Title Case, 1–2 words, each with a 0–1 salience `Weight` — maps to a multi-select or a tag taxonomy. Machine-generated only; a re-transcribe replaces the whole set, so the CRM side must replace, not append. |
| **Attendees** (`AttendeePayload`) | on every `recording.*` | Contact ↔ activity **relationship** | **Strong**, and the join itself (5.2). `isInternal:false` selects the customer side. |
| **Formula results** (`Entities/Formula.cs`) | `formula_result.completed` + a Workflow Signal | **Anything** — including custom deal fields | **The interesting one.** See below. |
| Raw transcript | — (`recording.transcribed` fires before the model runs) | Attachment, rarely | **Weak.** Volume and PII; better left as a deep link. |

**Formulas are the CRM field-mapping mechanism, and they already exist.** A `Formula` is a saved
template plus a context, run over a recording to produce a result; a platform-scoped one is available to
every user. An admin can therefore author a formula whose *output is the CRM payload* — "extract deal
stage, budget, competitor, blocker and next step" — attach a **Workflow Signal** to it, and have a
single admin-owned platform automation deliver it for every user who routes through that signal
(`Overall_Synopsis_of_Platform.md:907-940`). That is CRM-specific extraction with **no Diariz code
change at all**, and it is the single most important finding for scoping this work: the hard part of a
CRM connector — mapping unstructured conversation onto the customer's own custom fields — is already
solvable per deployment by an admin editing a prompt template.

**Deep links back.** Every payload carries `data.links.api` / `data.links.web`
(`Overall_Synopsis_of_Platform.md:778-783`), so a CRM activity can link straight back to the recording.
That link depends on the server's configured public URL, so a misconfigured `AppPublicOptions` produces
activities pointing at localhost.

**Caveats a connector must handle:**

1. **Action items are free text.** `Actor` and `Deadline` are strings and "may be empty"
   (`Entities/RecordingAction.cs:15-18`) — `Deadline` is documented as holding things like
   `"next Friday"`. Neither resolves to a CRM user or a date without a mapping or LLM pass. `Completed`
   / `CompletedAt` do map cleanly to task status, which is what makes two-way task sync plausible.
2. **One meeting fires up to five events.** `recording.created`, `.transcribed`, `.summarized`,
   `.minutes_ready`, `.action_items_ready`, `.tags_ready` all fire for a single recording as it moves
   through the pipeline. A naive automation creates five activity records. **Upsert on `recordingId`.**
3. **Deliveries retry; writes must be idempotent.** The envelope's `webhook-id` (`evt_…`) is a stable
   idempotency key held constant across roughly eight retry attempts
   (`Overall_Synopsis_of_Platform.md:784-786`, `808-822`). Use it.
4. **Don't clobber hand-edited content.** `Summary.IsUserEdited` marks a summary a human wrote; the
   automatic summariser skips those, and `SummarizationProcessor` still emits the event on that
   short-circuit path (`Overall_Synopsis_of_Platform.md:768-770`). A re-sync that overwrites the CRM
   note from a stale generated summary would undo the user's edit.
5. **Markdown, not HTML.** Summaries and minutes are Markdown. Most CRM note fields are plain text or
   HTML; conversion belongs on the automation side.
6. **Re-transcription replaces things.** A new transcription version replaces the tag set and can
   regenerate the AI outputs, so the CRM side needs a replace-by-recording semantic, not append.

---

## 6. What Diariz already ships that a CRM integration would use

Inventory, so no one designs a connector for capability that exists.

| Surface | Where | Relevance |
|---|---|---|
| **Signed outbound webhooks** — nine subscribable event types, HMAC (Standard Webhooks headers), SSRF-validated URLs, Postgres-backed delivery queue, ~8-attempt backoff, `429`-aware, per-subscription rate limit, auto-disable, per-delivery log | `src/Diariz.Api/Webhooks/`, `Overall_Synopsis_of_Platform.md:744-844` | The push half of any CRM integration. Production-grade already. |
| **AI outputs carried inline on their events** | same | A CRM automation needs no callback to fetch the summary/minutes/actions/tags. |
| **Workflow Signals + platform automations** | `Entities/WorkflowSignal.cs`, `Overall_Synopsis_of_Platform.md:907-960` | "Author tags intent, admin wires it once" — the routing model a `push-to-crm` signal would use. |
| **Formulas (incl. platform scope, shared, built-in)** | `Entities/Formula.cs` | Per-deployment CRM field extraction with no code. |
| **Inbound REST API with scoped personal tokens** (`ReadOnly` / `ReadWrite`, optional expiry) | `Entities/ApiAccessToken.cs`, `ApiTokenScope.cs` | Lets a CRM or iPaaS pull from Diariz. |
| **MCP server** | `Overall_Synopsis_of_Platform.md:646-743` | `list_recordings`, `get_transcript`, `get_meeting_minutes`, `list_action_items`, `who_attended`, `search_transcripts`, … — an LLM-native read surface a CRM copilot could consume directly. |
| **n8n community node** — self-registering trigger + full REST action node, published to npm | `integrations/n8n-nodes-diariz`, `Overall_Synopsis_of_Platform.md:846-905` | **The zero-code path.** Diariz Trigger → CRM node or HTTP Request. |
| **People directory + API** (`/api/people`, search, duplicates, merge, RBAC) | `Overall_Synopsis_of_Platform.md:1418-1464` | The contact side of the join, including a duplicate model to inherit. |
| **Google Calendar link** | `Entities/RecordingCalendarLink.cs` | Attendee emails and organiser for meetings whose participants were never voice-printed. |
| **Platform toggles** (`WebhooksEnabled`, API access, MCP — all default off) | `Entities/PlatformSettings.cs` | An operator can refuse CRM egress entirely. Any design must respect them. |

**Conclusion: the integration primitive is complete.** What is missing is not plumbing but
(a) a persisted person↔contact mapping, (b) recipes, and (c) optionally a native action.

---

## 7. Proposed delivery ladder

Three tiers, each independently useful. Deliberately ordered so the cheapest one is also the one that
ships today.

### Tier 0 — Document what already works (no code) — **DELIVERED**

A user connects Diariz to a CRM **today** with the shipped n8n node: Diariz Trigger (subscribe to
`recording.summarized` + `recording.action_items_ready`, **Include Attendee Contacts on**) → match
contact by email → upsert activity → create tasks. The work is a **recipe**, not a feature.

**Shipped as a help section rather than a feature**, per the 2026-07-30 decision to treat CRM
integration as a worked pattern:

- New help group **`crm` / "CRM integration"** (`apps/web/src/lib/help/groups.ts`, label key
  `groupCrm` in all four `locales/*/help.json`).
- **`crm-integration.md`** — the approach: why there is no connector, what is worth sending, the email
  join key and the contacts-gate trap, folders/meeting types as the customer and kind dimensions, the
  link-not-mirror rule, and the four sync directions.
- **`crm-espocrm.md`** — six worked n8n recipes against EspoCRM: log the meeting with its summary
  (incl. the `diarizRecordingId` upsert key), tasks from action items, file into a customer folder,
  the pre-meeting CRM briefing, task completion back, and the person-details sync of section 11.
  Plus a troubleshooting table.

*Still open:* an exportable n8n workflow JSON per recipe would lower the effort further; the articles
describe the nodes but the reader still wires them. Worth doing only once someone has actually used one.

*Resolved:* n8n ships a first-party **Odoo** node but no EspoCRM node, so the EspoCRM recipes use the
generic HTTP Request node — which turns out to read better in a help article anyway, since every field
being sent is visible.

### Tier 1 — Close the mapping gap (small, CRM-agnostic)

- **G1 (below):** persist an external reference on `Person` so the match survives.
- A seeded, platform-scoped **"CRM Update" formula** emitting strict JSON (attendees, company, next
  step, deal signals), plus a seeded **`push-to-crm` Workflow Signal**.

Both are CRM-agnostic — they make *every* CRM easier and add no vendor coupling.

### Tier 2 — A native action, only on demonstrated demand

The remaining half of roadmap Theme 2 is an internal rules engine with an extensible **action
registry** (`long_term_roadmap.md:101-116`). "Create CRM activity" would be one action over a small
pluggable client interface. The roadmap's own position is that native connectors follow "where demand
is clear" (`:121`), and nothing in this research contradicts it. **Do not start here.**

### Which CRM first, if one must be picked

**EspoCRM.** Custom fields and entities are ~90% configurable from the admin panel with no fork
(section 2), which is exactly what Tier 1's `diariz_person_id` field and a meeting-intelligence panel
need; its REST API is conventional; the hosting footprint is light enough to stand up beside a Diariz
dev stack; and GPL-3.0 is irrelevant to an HTTP-only integration (5.3). **Twenty** is the natural
second — GraphQL + REST and a modern UX make it the better demo — and **Odoo** only matters where the
user already runs it as ERP, where an existing n8n node does most of the work.

---

## 8. Gaps on the Diariz side

| # | Gap | Why it matters | Rough shape |
|---|---|---|---|
| **G1** | **No external-reference store on `Person`.** Nothing records "this person is contact 4711 in the CRM at that URL." | Without it every sync re-matches on a nullable, unverified email, and a person renamed or re-emailed silently forks into a second CRM contact. | A `PersonExternalLink` table (`PersonId`, `System`, `ExternalId`, `ExternalUrl`, `LinkedAt`) rather than columns on `Person` — one person can live in several systems. Schema change: needs `docs/Data_Schema.md` and a restore-compatibility check. |
| **G2** | **`Person.Email` is nullable and unverified**, and `PersonDuplicates` already shows real-world cases of an email belonging to the wrong account (`Overall_Synopsis_of_Platform.md:1449-1454`). | The primary join key is the weakest field in the model. | Not necessarily fixable — but a CRM sync must treat an email match as a *suggestion* on first contact, mirroring the never-auto-merge stance. |
| **G3** | **The contacts gate defaults off and the n8n node owns it**, so the join key is absent from a default subscription. | Guaranteed first support question for every CRM recipe. | Documentation, and possibly a warning in the Automations UI when a subscription selects attendee-bearing events with contacts off. |
| **G4** | **No company/account entity.** `Person.CompanyName` is free text with no normalisation. | Account-level rollup ("every meeting with Acme") is impossible on the Diariz side; the CRM has to do it. | Probably correct to leave alone — that is the CRM's job. Worth stating so nobody builds it twice. |
| **G5** | **Action-item `Actor` and `Deadline` are free text.** | Blocks clean task sync and any two-way status flow. | An optional resolution pass (actor → `Person`, deadline → date) would benefit Diariz's own UI as much as any CRM. Independently valuable. |
| **G6** | **No inbound contact enrichment.** Nothing pulls CRM contacts into the People directory. | External attendees stay unnamed until someone types a name. | A read-only import against `/api/people`, run by the operator's automation layer — Tier 1 at the earliest, and arguably never needed in-product. |

---

## 9. Still open

- **n8n node coverage** for EspoCRM / Twenty / SuiteCRM / Krayin — verify before scoping Tier 0.
- **Does any shortlisted CRM ship a usable inbound webhook/trigger** for the reverse direction (task
  completed in CRM → mark the Diariz action done)? Two-way task sync depends on it.
- **Activity object shape per CRM** — field-by-field: which have a meeting/call activity with a
  duration, an attendee collection, and an attachable long-text body. This determines how much of
  minutes/summary survives the trip.
- **Custom-field cost per CRM** — how much admin work `diariz_person_id` plus a handful of extracted
  deal fields actually is. Claimed to be trivial in EspoCRM; unverified.
- **Would an operator accept the egress at all?** Every relevant platform toggle defaults to off and
  the self-hosted ethos is explicit. Worth a user conversation before Tier 1.
- **Which real user wants this?** Nothing in this document establishes demand — it establishes
  feasibility and cost. Tier 0 is cheap enough not to need a business case; Tier 1 and 2 are not.

---

## 10. Should a recording carry customer / lead / deal tags?

**Direction confirmed (2026-07-30): integration goes through n8n / Zapier only, to stay vendor-neutral.
No native CRM connector.** That settles Tier 2 in section 7 as out of scope for now and makes the
question below the live design decision.

### 10.1 Most of this already exists

Diariz already classifies a recording along three dimensions, and **two of them are writable over the
REST API today** — meaning n8n can already set them:

| Dimension | Mechanism | Writable by n8n? |
|---|---|---|
| **Customer / account** | **Folders** (`Entities/Section.cs`). Two-level hierarchy whose own doc-comment example is literally `"Customers" › "Acme Corp"` (`:18-19`). The folder is a property of the room placement (`Entities/RoomRecording.cs:23-26`). Folders carry a **folder-level LLM summary and minutes** (`SectionSummary`, `SectionMinutes`) — so an account-level roll-up across every meeting with a customer already works. | **Yes.** Full CRUD on `/api/sections` + `PUT /api/recordings/{id}/section`. |
| **Meeting kind** (discovery / QBR / renewal) | **Meeting types** (`Entities/MeetingType.cs`), which already ship a seeded `customer` key (`:27`). Carries a free-text `Overview` prepended to every model prompt. | **Yes.** `POST /api/recordings/{id}/meeting-type`. |
| **Topic** | `RecordingTag` — LLM-extracted, weighted. | **No, and it must stay that way.** See the warning below. |

Plus `PUT /api/recordings/{id}/name`, so an automation can title a recording `"Acme - Renewal call"`.

> **Never put a CRM identifier in `RecordingTag`.** Tags are "machine-generated only (never user-edited):
> a (re-)transcription **replaces the recording's whole tag set**" (`Entities/RecordingTag.cs:4-5`). A CRM
> link stored there would be silently destroyed the first time anyone re-transcribed. This is the most
> likely wrong turn available and it fails quietly.

So "tag the recording with the customer" is largely a **documentation and recipe** problem, not a
feature problem.

### 10.2 The distinction that decides the maintenance question

Your instinct about maintenance load is right, and it lands almost entirely on one side of a line:

- **A pointer** — one opaque id, a URL, and a display label. It has no lifecycle of its own. The worst
  it can do when stale is 404. Maintenance: approximately zero.
- **A copy** — deal stage, amount, close date, owner. Every one of those fields is owned by the CRM and
  changes on the CRM's clock, not Diariz's. Holding them means owning a refresh path, a staleness rule,
  a conflict rule when both sides edited, and UI that explains which side won. Maintenance: high,
  permanent, and it grows with every field.

A copy also fails in the worst possible way: it does not look broken. A recording labelled
*"Acme - Negotiation"* six months after the deal closed-lost is confidently wrong, and it will be wrong
in an exported set of minutes.

**Verdicts:**

| Proposal | Verdict | Reasoning |
|---|---|---|
| Tag with **customer / account** | **Already have it** — use folders | Zero code, and folder-level minutes come free |
| Tag with **meeting kind** | **Already have it** — meeting types | Also steers the minutes prompt |
| Store **deal properties** (stage, value, close date, owner) | **No** | A second copy of the CRM's most volatile data. This *is* the maintenance load. |
| Store an **opaque external link** (system + id + url + label) | **Yes — if anything** | The one genuinely missing primitive, and it is a pointer, not a copy |
| A **CRM record picker inside the Diariz UI** | **No** | Requires live CRM credentials in Diariz. Breaks vendor-neutrality, and every relevant platform egress toggle defaults to off. |
| CRM ids in `RecordingTag` | **Never** | Wiped by re-transcription |

### 10.3 The one addition worth making: a generic external link

This is gap **G1** from section 8, generalised from `Person` to any entity:
`(EntityType, EntityId, System, ExternalId, ExternalUrl, Label, LinkedAt)`.

Diariz **stores and displays; it never interprets**. `System` is an arbitrary string, so the design is
vendor-neutral by construction and costs nothing per additional CRM.

What it buys:

- A **deterministic upsert key for n8n.** Today an automation must re-derive "which CRM account" by
  re-matching emails on every event — and a single meeting fires up to five events (section 5.4,
  caveat 2). A stored link makes every event after the first cheap and deterministic.
- **Deep links both ways**, so a CRM activity and a Diariz recording each point at the other.
- **"Which deal was this?"** answered by the stored `Label`, refreshed only when an automation chooses
  to refresh it — an explicitly stale-tolerant field, not a synced one.
- It **survives a rename on either side**, which email matching does not.

Cost: one table, a small API, a chip in the UI, a `docs/Data_Schema.md` entry, and a backup-restore
check. Bounded and one-time.

**Write the non-goals down** so it cannot drift into a copy: no stage, no amount, no dates, no owner,
no picker, no live CRM calls originating from Diariz.

### 10.4 Synchronising from the CRM — four directions

All four are n8n/Zapier-side. Three of them work today with no Diariz change.

**A. CRM → filing (works today).** On a new recording, resolve the account and file it:
find-or-create a folder via `/api/sections`, then `PUT /api/recordings/{id}/section`, optionally
`PUT /{id}/name` and `POST /{id}/meeting-type`.

> **Sequencing gotcha.** `recording.created` carries `attendees: []` deliberately, because no speakers
> exist yet (`Overall_Synopsis_of_Platform.md:798-799`). Account resolution at create time must use the
> **calendar link or the uploader**, not speakers. Wait for `recording.transcribed` if you need the
> attendee list.

**B. CRM → People directory (works today).** `GET /api/people/search?q=` is ungated; `POST`/`PUT
/api/people` need `ManagePeople`.

> **Do lazy enrichment, not bulk import.** Creating a directory row for every contact in a CRM would
> flood a directory built around voiceprints, and degrade both the duplicates screen and the
> speaker-assignment picker. Enrich on first *unmatched attendee* instead.

**C. CRM → pre-meeting briefing (works today, and is the sleeper).** Notes can be anchored to an
**upcoming calendar event** — `POST /api/calendar/events/{calendarId}/{eventId}/notes` — and when a
recording later links to that event those notes are **adopted onto the recording**. Critically, meeting
notes "**feed minutes generation** (steering + the Enhanced notes section)" (`Entities/MeetingNote.cs:8`).

So an n8n workflow can drop *"Acme renewal, stage Negotiation, open risk: pricing, last call promised a
revised quote"* into the meeting **before it happens**, and the minutes come out deal-aware — **CRM
context reaching the model with no schema change whatsoever.** This is almost certainly the highest
perceived-intelligence-per-unit-of-effort item in this entire document.

Caveats: it needs the calendar link; notes are user-visible and user-editable (a feature here, not a
bug); there is a 2048-character cap per note; and it is genuinely a *copy* of CRM data — but a
deliberately disposable one, scoped to a single meeting, never displayed as authoritative, and never
refreshed. That is the acceptable form of copying.

**D. Diariz → CRM tasks, and status back (half works today).** Out via
`recording.action_items_ready`; back via `POST /api/actions/complete`, so two-way task status is
achievable now. Limited by gap G5 — `Actor` and `Deadline` are free text, so assignee and due date need
a resolution step on the n8n side.

### 10.5 Recommendation

1. **Build the Tier 0 recipes first**, using folders + meeting type + pre-meeting notes. No Diariz code.
   This also tests demand honestly, which nothing so far has.
2. **Only if those recipes get used**, add the external-link table (10.3). Its value is proportional to
   how many automations exist to use it, which is currently zero.
3. **Never mirror deal properties.** If a future user genuinely needs live deal state next to a
   transcript, the correct answer is a deep link into the CRM, which always shows the truth.

If only one thing gets built, make it direction **C** — it is free, and it changes the output the user
actually reads.

---

## 11. CRM contact fields vs the Diariz person record

Prompted by the idea of a recipe that keeps Diariz people up to date from CRM contacts. The question
underneath it: **is the Diariz person model missing anything important?**

### 11.1 What Diariz holds

`Person` (`src/Diariz.Domain/Entities/Person.cs`), editable fields only:
`Name` (one string), `Title`, `CompanyName` (free text), `Email` (one), `Phone` (one), `IsInternal`,
`VoiceprintOptOut`. Plus `Id`, `LinkedUserId`, timestamps, and the voiceprint.

`UpdatePersonRequest` accepts exactly `Name, Title, CompanyName, Email, Phone, IsInternal,
VoiceprintOptOut`, and **null means "not supplied", not "clear it"**
(`src/Diariz.Api/Contracts/ApiDtos.cs:352-356`).

### 11.2 The union of what CRMs keep

Common across Salesforce, HubSpot, EspoCRM, SuiteCRM, Twenty and Odoo:

| Group | Typical CRM fields | Diariz equivalent |
|---|---|---|
| **Identity** | salutation, first name, middle name, last name, suffix, preferred name, pronouns | `Name` — **one string** |
| **Work** | job title, department, **account (a relation)**, reports-to, assistant, buying role | `Title`; `CompanyName` as **free text**; nothing else |
| **Email** | primary + additional addresses, each typed, one flagged primary | `Email` — **one, untyped** |
| **Phone** | mobile / work / home / fax / other, typed, one primary | `Phone` — **one, untyped** |
| **Address** | mailing + other: street, city, region, postcode, country | none |
| **Web** | website, LinkedIn, social handles | none |
| **Ownership** | record owner, team/territory, created/modified by and at, last activity | timestamps only |
| **Lifecycle** | lead source, lifecycle stage (lead/MQL/SQL/customer), active flag | none |
| **Consent** | email opt-out, do-not-call, marketing subscription, GDPR lawful basis | `VoiceprintOptOut` only, which is about biometrics, not contact |
| **Other** | description, tags, language, timezone, birthdate, custom fields | none |

Diariz also holds two things a CRM does not: **`IsInternal`** (a CRM implies this by Contact-vs-User
rather than storing it) and **`VoiceprintOptOut`** (biometric consent, which has no CRM analogue and must
never be written by a sync).

### 11.3 Are we missing anything important?

Judged strictly by whether it would improve speaker naming, the contact card, the minutes, or an
automation Diariz cannot otherwise run. Most of the list above is **correctly absent** — Diariz is not a
CRM, and address, social, lifecycle stage, owner, source, timezone and birthday have no job here.

**Worth having:**

| Gap | Importance | Assessment |
|---|---|---|
| **Structured name (first / last)** | Medium-high | The most consequential structural difference. Transcripts say "Sam"; the CRM says `firstName=Samir, lastName=Patel`; Diariz stores the single string "Samir Patel". Duplicate detection normalises the whole name, so "Sam Patel" and "Samir Patel" never group. A first-name field would improve both matching and duplicate reporting. **But** `Name` is what renders on a transcript row, and one display string is exactly right for that — splitting it risks the displayed name drifting from the spoken one. **Verdict: useful, not urgent.** The cheap fix is for the sync to compose `first + " " + last` consistently so at least everything agrees. |
| **External id (G1)** | High | Already identified. Without it every sync re-matches on email. |
| **Account as a relation (G4)** | Medium | Real, but the CRM's job. The cheap fix is to sync the CRM's **canonical** account name, so every Diariz person from one customer at least carries the identical string and the existing free-text search behaves like a grouping. |
| **Typed multi-value email / phone** | Low | One of each is enough for a contact card. **But see the trap below** — the single `Email` is not "the primary address", it is "the address we matched on", and those differ. |
| **Contact consent / do-not-contact** | Low in Diariz, real in the automation | Diariz never emails a contact itself: `POST /recordings/{id}/email` has **no recipient parameter and mails only the caller** (`RecordingsController.cs:626-632`). But Diariz hands attendee addresses to automations, and the n8n help article shows exactly that. There is no field saying "this person must not be contacted". **Do not add one.** A copied consent flag is precisely the mirror mistake section 10.2 warns against, and a stale consent record is the one kind of stale data with legal consequences. The automation should read consent from the CRM at send time. |

**Not worth having, stated so nobody builds them:** department, addresses, social links, lifecycle
stage, lead source, record owner, territory, language, timezone, birthdate.

### 11.4 The sync recipe, and why it is smaller than it looks

Three traps decide the design:

1. **`PUT /api/people/{id}` returns 400 for a linked person** if the body carries `Name` or `Email`:
   *"Name and email follow the linked user account and cannot be set here"*
   (`PeopleController.Update`). A naive send-everything sync therefore **fails on every colleague who
   has a Diariz account**.
2. **Never overwrite `Email`.** It is the join key. The stored address is the one that matched an
   attendee, which is often not the CRM's primary; replacing it can break the very match that found
   the person.
3. **Never send `IsInternal` or `VoiceprintOptOut`.** The first is a Diariz judgement (a colleague can
   also exist as a CRM contact), the second is biometric consent.

Strip those out and **only three fields are left worth syncing: `title`, `companyName`, `phone`.**

That is a much better outcome than it sounds, because those three are exactly what the Speakers tab and
the contact card display. The recipe is small, safe, cannot corrupt the join key, cannot fight the
account directory, and its effect is immediately visible where users actually look.

Shipped as **Recipe 6** in `crm-espocrm.md`.

Notes for whoever builds it: `GET /api/people/search?q=` is ungated and matches name, email and company;
`PUT /api/people/{id}` needs **Manage people** unless it is you, so the token owner needs that
permission; and the sync should **not** create a person on a miss (lazy enrichment only, per 10.4 B).

---

## Change log

| Date | Change |
|---|---|
| 2026-07-30 | Sections 1–4 written in a Claude chat session. |
| 2026-07-30 | Sections 5–9 added in a Claude Code session: section 5's open questions answered against the repository, plus the Diariz-side capability inventory, a three-tier delivery ladder, gaps, and remaining unknowns. |
| 2026-07-30 | Section 10 added: n8n/Zapier confirmed as the only integration route (Tier 2 native connector shelved); customer/lead/deal tagging assessed and largely answered by existing folders + meeting types; recommendation to add only an opaque external link and never mirror deal properties; four CRM→Diariz sync directions, three of which need no code. |
| 2026-07-30 | **Tier 0 delivered.** CRM integration adopted as a documented worked pattern rather than a feature: new `crm` help group plus `crm-integration.md` (approach) and `crm-espocrm.md` (five worked n8n recipes). Section 7 updated. Research in this document now has a user-facing home; keep the two in step when either changes. |
