# User problem reports

**Date:** 2026-08-03
**Status:** Design, awaiting implementation plan

## 1. Goal

Let a user report something that looks or behaves wrong when **nothing has thrown** - a field enabled that should be disabled, a control that does not do what it says, a value that looks incorrect. The app is working as far as the code is concerned, so the existing error tracking sees nothing.

A report captures two things together:

- what the user says is wrong, in their words
- the technical trail leading up to it, automatically

Both land in Diariz's own database and are read in the app.

## 2. Why not the obvious approaches

**Sentry's feedback widget.** Ruled out on fact, not preference: feedback widgets are in GlitchTip's unsupported set alongside Session Replay and Profiling, and **fail silently** - the SDK accepts the call and nothing arrives. The user-feedback endpoint has been an open request upstream since 2021.

**Description in Diariz, breadcrumbs in GlitchTip, joined by a correlation id.** Considered and rejected. It puts two systems and a manual id lookup in front of the most common action - reading a report - and that friction is what stops reports being triaged at all.

**Everything into GlitchTip via `captureMessage`.** Works technically, but the description is user-authored free text. Every disclosure path found while building the telemetry stack was a *field name* or a *URL*: structural things a deny-list catches. Prose is not. A user reporting "the speaker label is wrong where Sarah discusses the redundancies" has put meeting content into an error tracker, and no scrubber can prevent it. Free text belongs where meeting content already lives and is already governed.

## 3. Non-goals

- Session replay, or any visual capture.
- Detecting this class of bug automatically. Asserting UI invariants against the permission model is a real and arguably higher-value idea, but it is a different piece of work - closer to instrumenting the permission model than to reporting - and is out of scope here.
- Any dependency on GlitchTip. Reports work identically with telemetry off, which is the constraint that shapes section 4.2.
- Triage workflow: status, assignment, replies. Reports are read; that is all.

## 4. Architecture

### 4.1 Flow

```
Account menu -> "Report a problem" -> modal (description)
  -> POST /api/reports { description, trail, route, release }
    -> UserReport row
      -> Settings -> Reports (Platform Administrator)
```

If a GlitchTip DSN happens to be configured, the same submission may also `captureMessage` so reports are visible beside crashes. That is additive and optional; nothing depends on it.

### 4.2 `lib/scrub.ts` - an extraction, not new code

`isSensitiveKey`, `stripQueryString`, `scrubUrlsIn` and the recursive walker are pure, but they currently live in `apps/web/src/lib/telemetry.ts`, which statically imports `@sentry/react`. **ES imports are hoisted**, so importing a scrubber from there drags the SDK in - the wrong dependency for a feature that must work with telemetry off.

Move them to `apps/web/src/lib/scrub.ts`. `telemetry.ts` imports from it; so does the trail. One copy of the deny-list that eight separate disclosure paths were found and closed against, serving both features, with neither depending on the other.

This is the piece that makes everything else clean, and it is the only change to existing telemetry behaviour.

### 4.3 `lib/trail.ts` - the ring buffer

A ~30-entry ring buffer, no SDK dependency, fed from seams the app already owns:

| Source | Entry | Already exists? |
| --- | --- | --- |
| axios response interceptor | method, scrubbed path, status, duration | yes - `lib/api.ts:116` |
| axios request interceptor | request start, for duration | yes - `lib/api.ts:100` |
| react-router navigation | route change, query stripped | router is already in the tree |
| explicit app markers | anything worth naming, added as needed | new, used sparingly |

**Entries are scrubbed on the way IN, not on the way out.** The buffer never holds a value that would be unsafe to send, so there is no path where an unscrubbed entry escapes through a route nobody thought about. That inversion is deliberate: the reverse ordering is how several of the telemetry leaks happened.

**Deliberately excluded, with reasons:**

- **DOM clicks.** A CSS selector is weak evidence without replay, and a global click listener is intrusive. The user's own description says which control; the API trail says what the app did about it. Add later if reports prove thin.
- **Console.** It can carry literally anything, which is why telemetry already drops console breadcrumbs below `warn`. Taking them here would reintroduce exactly that risk into a store that also holds free text.

The highest-value entry is the **API response**: for "this field was enabled when it should not have been", what actually diagnoses it is the permissions payload the server returned. That is the strongest argument for the axios seam over any other source.

### 4.4 Domain and API

`UserReport` in `src/Diariz.Domain/Entities/`:

| Column | Notes |
| --- | --- |
| `Id` | Guid |
| `UserId` | FK to `ApplicationUser`, **cascade delete** |
| `CreatedAt` | `DateTimeOffset`, stored UTC |
| `Description` | user's text, length-capped |
| `Route` | the SPA route at submission |
| `Release` | `__APP_VERSION__` |
| `TrailJson` | the ring buffer, serialised |

Cascade-delete is deliberate: this is user-authored content and must disappear with the user, like everything else they own.

`ReportsController`:

- `POST /api/reports` - any authenticated user, own report only.
- `GET /api/reports` - **Platform Administrator only**, newest first, paged.

`DateTimeOffset` values are converted with `.ToUniversalTime()` before storing - Npgsql rejects a non-zero offset on a `timestamptz` column, and the in-memory test provider does not enforce that, so it only shows up against real Postgres.

### 4.5 The admin view

A fifth tab in `SettingsModal`: `type Tab = "ai" | "quotas" | "maintenance" | "integration" | "reports"`, with a `UserReportsPanel` following `MaintenancePanel`'s shape - list newest-first, expand a row for the trail.

The modal is **already Platform Administrator only** (`isPlatformAdmin` from `useAuth`, and the account menu hides it otherwise), so the tab inherits correct gating with no new authorisation surface. The controller still enforces it server-side; UI gating is not access control.

### 4.6 Entry point

A `MenuRow` in `UserMenu.tsx` beside About - always reachable, costs no screen space, and matches how Settings and About are already surfaced. **Not** gated: any signed-in user can report.

## 5. Privacy

**The description is free text, and moving it to Diariz's database does not make it safe - it makes it *appropriately located*.** It sits alongside meeting content, under the same retention, backup, export and deletion rules, rather than in a third-party-shaped tool nobody told the user about.

Two supporting measures:

- Placeholder text steering people away from pasting meeting content. It will not always work; it costs nothing.
- The trail is scrubbed on the way in (4.3), so the automatic half carries no credentials, no query strings and no content-bearing fields.

Reports are included in the platform backup: they are app data, and excluding them would be the surprising choice.

## 6. Testing

| Test | Where | Why |
| --- | --- | --- |
| Ring buffer evicts at capacity | `trail.test.ts` | The only stateful logic here |
| A sensitive value put in comes back redacted | `trail.test.ts` | Proves scrubbing happens on the way in, not merely on the way out |
| The trail works with no SDK initialised | `trail.test.ts` | The constraint the whole design turns on |
| Existing scrubber tests still pass, unmoved | `scrub.test.ts` | The extraction must not change behaviour |
| `POST` stores against the calling user only | `ReportsControllerTests` | Ownership, as every other controller here |
| `GET` refuses a non-Platform-Administrator | `ReportsControllerTests` | Server-side gating, not UI gating |
| Deleting a user removes their reports | integration | Cascade needs real FK enforcement; the in-memory provider does not enforce it |
| Panel renders a report and expands its trail | `UserReportsPanel.test.tsx` | Matches its neighbours |

## 7. Release and documentation obligations

A **functional enhancement**: Minor +1, so **0.176.0** from the current 0.175.1, across `version.json` and all four mirrors.

| Target | Needed? |
| --- | --- |
| `RELEASES[0]` entry | yes |
| `docs/Data_Schema.md` | yes - new table, new FK, new cascade |
| README Features row | yes - user-visible feature |
| `docs/features.md` | yes - in lockstep with the README row |
| About-box `CAPABILITIES` | yes - scope change |
| `docs/Overall_Synopsis_of_Platform.md` | yes - new endpoint and flow |
| Help article | yes - short; it is behaviour a user relies on, and the `?` popover summary is what they will read first |

Deployment surface: **server redeploy, no desktop release.** The desktop shell loads the SPA from the server origin.

## 8. Risks

| Risk | Mitigation |
| --- | --- |
| Users paste meeting content into the description | Accepted and located appropriately; placeholder text discourages it. Cannot be prevented technically |
| Reports collected and never read | The admin view is in scope precisely so this does not happen. If they still go unread, the feature is not earning its place |
| The trail is too thin to diagnose anything | Start with API + navigation, which is what actually diagnoses state bugs. Clicks are a deliberate later addition, driven by real reports rather than a guess |
| Report spam from a compromised account | Out of scope. Reports are per-user and delete with the user; rate limiting can follow if it ever matters |
| The scrubber extraction changes telemetry behaviour | Existing scrubber tests move with the code unchanged and must pass untouched |

## 9. Open questions

- **Retention.** Reports currently live forever. Whether that is right is worth deciding once there is any volume, not guessed now.
- **Notification.** Nothing tells an administrator a report has arrived; they have to look. An email or a Workflow Signal is the obvious follow-on, deliberately not in this scope.
