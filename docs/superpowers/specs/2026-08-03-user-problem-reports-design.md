# Provide Feedback

**Date:** 2026-08-03
**Status:** Design, awaiting implementation plan

## 1. Goal

Let a user report something that looks or behaves wrong when **nothing has thrown** - a field enabled that should be disabled, a control that does not do what it says, a value that looks incorrect. The app is working as far as the code is concerned, so the existing error tracking sees nothing.

A submission captures three things together:

- what the user says, in their words
- the technical trail leading up to it, automatically
- optionally a screenshot, on the desktop app

All of it lands in Diariz's own database, is read and deleted by a Platform Administrator, and raises an outbound event so an automation can triage or email it.

## 2. Why not the obvious approaches

**Sentry's feedback widget.** Ruled out on fact, not preference: feedback widgets are in GlitchTip's unsupported set alongside Session Replay and Profiling, and **fail silently** - the SDK accepts the call and nothing arrives. The user-feedback endpoint has been an open request upstream since 2021.

**Description in Diariz, breadcrumbs in GlitchTip, joined by a correlation id.** Rejected: it puts two systems and a manual id lookup in front of the most common action - reading a report - and that friction is what stops reports being triaged at all.

**Everything into GlitchTip via `captureMessage`.** Works technically, but the description is user-authored free text. Every disclosure path found while building the telemetry stack was a *field name* or a *URL*: structural things a deny-list catches. Prose is not. A user describing a transcription problem will quote the transcript. Free text belongs where meeting content already lives and is already governed.

## 3. Non-goals

- Session replay, or any continuous visual capture.
- Detecting this class of bug automatically. Asserting UI invariants against the permission model is a real and arguably higher-value idea, but it is a different piece of work and out of scope here.
- Any dependency on GlitchTip. Feedback works identically with telemetry off, which is the constraint that shapes 4.2.
- Triage state: status, assignment, replies. Reports are read, acted on elsewhere, and deleted. The outbound event (4.7) is how a workflow adds anything richer.

## 4. Architecture

### 4.1 Flow

```
Account menu -> "Provide Feedback" -> movable modal (description + optional screenshot)
  -> POST /api/feedback { description, trail, route, release, screenshot? }
    -> Feedback row (+ screenshot blob)
      -> feedback.submitted event -> platform webhook subscriptions
      -> Settings -> Feedback tab (Platform Administrator: read, delete)
```

If a GlitchTip DSN happens to be configured, the submission may also `captureMessage` for visibility beside crashes. Additive and optional; nothing depends on it.

### 4.2 `lib/scrub.ts` - an extraction, not new code

`isSensitiveKey`, `stripQueryString`, `scrubUrlsIn` and the recursive walker are pure, but they live in `apps/web/src/lib/telemetry.ts`, which statically imports `@sentry/react`. **ES imports are hoisted**, so importing a scrubber from there drags the SDK in - the wrong dependency for a feature that must work with telemetry off.

Move them to `apps/web/src/lib/scrub.ts`. `telemetry.ts` imports from it; so does the trail. One copy of the deny-list that eight separate disclosure paths were found against, serving both features, with neither depending on the other.

This is the piece that makes the rest clean, and it is the only change to existing telemetry behaviour.

### 4.3 `lib/trail.ts` - the ring buffer

A ~30-entry ring buffer, no SDK dependency, fed from seams the app already owns:

| Source | Entry | Already exists? |
| --- | --- | --- |
| axios response interceptor | method, scrubbed path, status, duration | yes - `lib/api.ts:116` |
| axios request interceptor | request start, for duration | yes - `lib/api.ts:100` |
| react-router navigation | route change, query stripped | router is already in the tree |
| explicit app markers | anything worth naming, used sparingly | new |

**Entries are scrubbed on the way IN, not on the way out.** The buffer never holds a value that would be unsafe to send, so there is no escape route nobody thought about. That inversion is deliberate: scrubbing on the way out is how several of the telemetry leaks happened.

**Deliberately excluded:** DOM clicks (a CSS selector is weak evidence without replay, and a global listener is intrusive - the user's description says which control) and console (it can carry anything, which is why telemetry already drops it below `warn`; taking it here would reintroduce that risk into a store that also holds free text).

The highest-value entry is the **API response**: for "this field was enabled when it should not have been", what diagnoses it is the permissions payload the server returned.

### 4.4 Screenshot - desktop only, and it needs a shell change

The desktop shell already captures screenshots (`apps/desktop/src/main.js`, `captureScreenshot()`), exposed to the SPA as `window.diariz.captureScreenshot` and no-oped in a plain browser by `lib/trayScreenshots.ts`. Two consequences, both of which need stating before this is planned rather than discovered during it:

**It is Electron-only.** A browser user gets a text-and-trail submission with no screenshot option shown, exactly as the notes popover already hides its screenshot area outside the desktop app. That is consistent, and it means the screenshot is an enhancement for desktop users rather than a core part of the feature.

**The existing capture cannot be reused as-is.** `captureScreenshot()` opens with `if (!canCapture(recorder) || !mainWindow) return;` - it only fires **while recording**. Feedback is given at any time, so this needs a new IPC path in the shell that captures without the recorder gate (and without the capture-area picker's recording-specific state).

**That makes this the one part of the feature requiring a desktop release** - a new Windows installer and macOS `.dmg`, cut from a `v*` tag. Users on an older installer get feedback without screenshots, and nothing breaks for them. Everything else in this spec ships by redeploying the server.

Given that, the screenshot is a **separable second phase**. The feature is worth shipping without it.

**Storage:** the image goes to the existing object store under a `feedback/` prefix, with the key on the `Feedback` row. The platform backup enumerates the whole bucket, so it is captured automatically; restore repopulates it. `ScreenshotOptions.MaxBytes` (20 MB) is the existing cap and applies here too.

### 4.5 The movable modal

The screenshot captures the screen, so a modal sitting over the thing being reported makes it uncapturable. The feedback modal must therefore be **draggable by its header**.

Requirements: drag by the header only (not the body, which holds a textarea), stay within the viewport, and keep the dialog's focus trap and `Escape` handling intact - a modal that can be dragged but no longer traps focus is an accessibility regression. Position is not persisted; it resets each time it opens.

### 4.6 Domain and API

`Feedback` in `src/Diariz.Domain/Entities/`:

| Column | Notes |
| --- | --- |
| `Id` | Guid |
| `UserId` | FK to `ApplicationUser`, **cascade delete** |
| `CreatedAt` | `DateTimeOffset`, stored UTC |
| `Description` | user's text, length-capped |
| `Route` | the SPA route at submission |
| `Release` | `__APP_VERSION__` |
| `TrailJson` | the ring buffer, serialised |
| `ScreenshotBlobKey` | nullable; null on browser submissions |

Cascade-delete is deliberate: user-authored content must disappear with the user, like everything else they own.

`FeedbackController`:

| Endpoint | Who |
| --- | --- |
| `POST /api/feedback` | any authenticated user, own submission only |
| `GET /api/feedback` | **Platform Administrator only**, newest first, paged |
| `GET /api/feedback/{id}/screenshot` | **Platform Administrator only** |
| `DELETE /api/feedback/{id}` | **Platform Administrator only**; removes the row and the blob |

**Reading and deleting are Platform Administrator only.** A user can submit but cannot list, read or delete - including their own. That is deliberate: a per-user view implies a support conversation this feature does not have, and listing would need its own ownership rules for no benefit.

`DateTimeOffset` values are converted with `.ToUniversalTime()` before storing - Npgsql rejects a non-zero offset on a `timestamptz` column, and the in-memory test provider does not enforce that, so it only shows up against real Postgres.

Deleting must remove the blob as well as the row. A row deleted without its blob leaves an orphan that the backup keeps carrying forever.

### 4.7 The outbound event

A new event type `feedback.submitted`, published through the existing `IWebhookPublisher` so n8n, Zapier or a Workflow Signal can triage, email or file it.

**Platform subscriptions only. Never personal.** Personal subscriptions are owned by a user and receive events about that user's own data; feedback is readable only by a Platform Administrator, so a personal subscription firing on someone else's submission would be a disclosure. The event key must therefore stay out of `WebhookEventTypes.Subscribable`, which is the personal list.

**The description is gated behind an opt-in, off by default.** This follows the precedent already in `WebhookSubscription.IncludeAttendeeContacts`, whose reasoning applies verbatim: *"an automation points at an arbitrary URL, so without this every event would fan the directory's contact details out to whoever owns it."* The feedback description can quote meeting content, which is exactly why it was kept out of GlitchTip in section 2 - sending it to an arbitrary URL by default would undo that decision through a different door.

So the default payload is thin: id, user id, route, release, timestamp, `hasScreenshot`. With the opt-in set, the description is included. **The screenshot image is never in the payload** - an automation that needs it fetches it through the API.

The exact wiring into the platform/`SignalFilter` routing is left to planning, which should read `WebhookPublisher` properly rather than infer it. What is fixed here is the constraint: platform-only, thin by default.

### 4.8 The admin view

A fifth tab in `SettingsModal`: `type Tab = "ai" | "quotas" | "maintenance" | "integration" | "feedback"`, with a `FeedbackPanel` following `MaintenancePanel`'s shape - newest first, expand a row for the trail and screenshot, delete with a confirmation.

The modal is **already Platform Administrator only** (`isPlatformAdmin` from `useAuth`, hidden from the account menu otherwise), so the tab inherits correct gating with no new authorisation surface. The controller still enforces it server-side; UI gating is not access control.

### 4.9 Entry point

A `MenuRow` labelled **"Provide Feedback"** in `UserMenu.tsx` beside About - always reachable, costs no screen space, matches how Settings and About are surfaced. **Not** gated: any signed-in user can submit.

## 5. Privacy

**The description is free text, and moving it to Diariz's database does not make it safe - it makes it *appropriately located*.** It sits alongside meeting content, under the same retention, backup, export and deletion rules.

Three supporting measures:

- The trail is scrubbed on the way in (4.3), so the automatic half carries no credentials, no query strings and no content-bearing fields.
- The webhook payload omits the description unless a Platform Administrator opts that subscription in (4.7).
- Placeholder text steering people away from pasting meeting content. It will not always work; it costs nothing.

**A screenshot can contain anything on screen, including a transcript.** It cannot be scrubbed. It is opt-in per submission (the user chooses to attach it), Platform-Administrator-only to view, and deleted with the report. That is the whole mitigation, and it should be stated plainly in the help article rather than implied.

Feedback is included in the platform backup: it is app data, and excluding it would be the surprising choice.

## 6. Testing

| Test | Where | Why |
| --- | --- | --- |
| Ring buffer evicts at capacity | `trail.test.ts` | The only stateful logic here |
| A sensitive value put in comes back redacted | `trail.test.ts` | Proves scrubbing happens on the way in |
| The trail works with no SDK initialised | `trail.test.ts` | The constraint the design turns on |
| Existing scrubber tests pass unmoved | `scrub.test.ts` | The extraction must not change behaviour |
| `POST` stores against the calling user only | `FeedbackControllerTests` | Ownership |
| `GET` / `DELETE` refuse a non-Platform-Administrator | `FeedbackControllerTests` | Server-side gating, not UI gating |
| `DELETE` removes the blob as well as the row | `FeedbackControllerTests` | Orphaned blobs are invisible and permanent |
| Deleting a user removes their feedback | integration | Cascade needs real FK enforcement |
| The webhook payload omits the description by default | `WebhookPublisherTests` | The disclosure decision, asserted rather than assumed |
| `feedback.submitted` is not personally subscribable | `WebhookEventTypesTests` | Would leak other users' submissions |
| The modal drags and still traps focus | `FeedbackModal.test.tsx` | The accessibility regression this invites |
| Panel renders, expands and deletes | `FeedbackPanel.test.tsx` | Matches its neighbours |

## 7. Release and documentation obligations

A **functional enhancement**: Minor +1, so **0.176.0** from the current 0.175.1, across `version.json` and all four mirrors.

| Target | Needed? |
| --- | --- |
| `RELEASES[0]` entry | yes |
| `docs/Data_Schema.md` | yes - new table, FK, cascade, and the `feedback/` blob prefix |
| README Features row | yes |
| `docs/features.md` | yes - in lockstep with the README row |
| About-box `CAPABILITIES` | yes - scope change |
| `docs/Overall_Synopsis_of_Platform.md` | yes - new endpoints, new outbound event type |
| Help article | yes - and it must say plainly that a screenshot captures whatever is on screen |

**Deployment surface differs by phase.** Everything except the screenshot is **server redeploy only**. The screenshot needs a new IPC path in the Electron shell (4.4), so that phase requires a **desktop release** - a `v*` tag producing a Windows installer and a macOS `.dmg`. That is the argument for shipping the screenshot separately.

## 8. Risks

| Risk | Mitigation |
| --- | --- |
| Users paste meeting content into the description | Accepted and appropriately located; placeholder text discourages it. Cannot be prevented technically |
| A screenshot captures a transcript | Opt-in per submission, admin-only to view, deleted with the report, and stated plainly in the help article |
| The webhook fans feedback text to an arbitrary URL | Off by default, opt-in per subscription, following the `IncludeAttendeeContacts` precedent |
| Feedback collected and never read | The admin view and the outbound event exist so it reaches a human. If it still goes unread, the feature is not earning its place |
| Deleting a report orphans its blob | Asserted in a test; an orphan is invisible and rides in every backup thereafter |
| The trail is too thin to diagnose anything | Start with API + navigation, which is what diagnoses state bugs. Clicks are a deliberate later addition driven by real submissions |
| The draggable modal breaks focus trapping | Tested. Drag by header only; `Escape` and focus behaviour unchanged |
| Feedback spam from a compromised account | Out of scope. Per-user, deletes with the user; rate limiting can follow if it matters |

## 9. Open questions

- **Retention.** Feedback currently lives forever. Worth deciding once there is volume, not guessed now.
- **Whether the screenshot ships at all.** It is the only part needing a desktop release, and it is genuinely optional. Phase it second and decide with the feature in front of you.
