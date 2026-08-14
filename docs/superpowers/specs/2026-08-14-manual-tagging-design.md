# Manual tagging, with auto tags as suggestions only

Date: 2026-08-14
Status: design approved, not yet implemented
Design handoff: `docs/design_handoff_manual_tagging/` (README + two screenshots + interactive prototypes)

## Problem

Every recording is auto-tagged by an LLM pass (`TagsProcessor`), and those tags are applied directly:
they land in `RecordingTags` and immediately populate the tag cloud, the Tags tab drill-down and the
`/api/tags` aggregate. Nobody chose them. The result is noisy and close to random - a cloud full of
words the user would not have picked, at sizes driven by a model's per-recording salience score rather
than by anything they care about. There is also **no way to add a tag by hand**: `RecordingTag`'s own
doc comment states it is machine-generated and never user-edited, there is no write endpoint, and the
`/api/tags` OpenAPI description advertises that hand-setting tags is impossible.

## Goal

Invert the relationship. **A tag exists because the user chose it.** The LLM keeps extracting topics,
but its output is only ever a *suggestion* offered next to the recording, to be picked or ignored. The
tag cloud, drill-down and search then describe the user's own vocabulary.

## Scope

**In scope:** the tag lifecycle (suggested / adopted / dismissed), the write API, the Tags pill and
popover on the recording hub per the handoff, and narrowing every existing tag read to adopted tags.

**Out of scope, deliberately:**

- **No bulk tooling.** No review queue, no multi-select tagging, no "adopt all". The Tags tab cloud is
  empty on day one and fills as recordings get tagged one at a time. Accepted knowingly: a bulk
  adoption tool would mostly re-create the noise this change removes.
- **No global never-suggest list.** Dismissal is per recording only (see Decisions).
- **No inline tags in the page body.** The hub shows a count on the pill and nothing else, per the
  handoff. The recording list rows are untouched.
- **No new event type.** See Decisions.
- **No type-ahead** across the user's existing tags. The handoff lists it as optional (option 1b); the
  entry field is sized for it so it can be added later without a redesign.

## Decisions

Each of these was settled explicitly, and each closes off an alternative that looks reasonable:

1. **Every existing tag demotes to a suggestion.** No grandfathering, not even for high-weight tags.
   The cloud and tag search start empty. This is the whole point: today's tags are the noise.
2. **The cloud, drill-down and search cover adopted tags exclusively.** Suggestions are never
   aggregated and never searchable. They exist only on the recording that produced them.
3. **Dismissal is per recording.** A word dismissed on one recording can be suggested again on the
   next. Rejected: a per-user global blocklist, which needs its own table, a settings screen to review
   and undo it, and a filter on every suggestion read - and a single misclick would silently suppress a
   word library-wide.
4. **Tags are stored verbatim, with spaces banned.** No case folding and no Title Case canonicalisation.
   Internal whitespace collapses to `-` (`"budget planning 2026"` -> `budget-planning-2026`), leading
   and trailing hyphens are trimmed, and duplicates are rejected case-insensitively per recording. The
   extraction prompt is **not** changed, so a promoted suggestion keeps its Title Case ("Data
   Collection") while hand-typed tags look like the handoff's chips. The cloud already merges case
   variants and picks the most frequent casing for display, so the two styles coexist without a data
   migration or a prompt rewrite.
5. **`recording.tags_ready` is unchanged and no new event is added.** It keeps firing when the LLM pass
   completes, with the same payload; its `tags` array now means "suggested". Rejected: a
   `recording.tags_changed` event on manual edits (no consumer asked for it, and it would need the n8n
   node regenerated and documented), and retiring `tags_ready` (would silently break existing
   subscriptions).
6. **Anyone who can see a recording can tag it.** Tagging is gated by
   `IRoomScope.CanReadRecordingAsync`, not the owner-only predicate `POST /{id}/meeting-type` uses. The
   tag cloud is already room-scoped, so a shared room gets a shared organising layer rather than only
   the owner's vocabulary.

   **This is a new precedent and worth knowing.** Meeting notes and screenshots use
   `CanReadRecordingAsync` for their *reads* while keeping create/update/delete strictly owner-only, and
   a sweep of every `[HttpPost/Put/Delete/Patch]` action in `src/Diariz.Api/Controllers` found **zero**
   mutating endpoints currently gated by read access. The three tag endpoints are therefore the first
   writes in the codebase a non-owner can perform. The controller doc comments should say so explicitly,
   so the next person copying a nearby pattern does not widen a gate by accident.
7. **Removing an adopted tag deletes the row.** It does not revert to a suggestion, so it will not pop
   back into the hint list; it can only return when a re-transcription regenerates suggestions. One
   predictable rule instead of "removal sometimes un-does an adoption and sometimes deletes".
8. **An adopted tag carries `Weight = 1.0`.** See Data model.

## Data model

`RecordingTag` (`src/Diariz.Domain/Entities/RecordingTag.cs`) gains two fields. The table stays plain
columns only, so it keeps loading under the in-memory test provider.

| Field | Type | Meaning |
| :--- | :--- | :--- |
| `Status` | `RecordingTagStatus` (int) | `Suggested = 0`, `Adopted = 1`, `Dismissed = 2`. Append-only ints, same rule as `RecordingStatus` / `Source`. |
| `AdoptedAt` | `DateTimeOffset?` | When the tag became the user's. Null for suggestions and dismissals. |

`AdoptedAt` exists because chip order in the popover has to be stable and meaningful: a promoted
suggestion's `CreatedAt` is whenever the LLM happened to run, so ordering by it would shuffle
hand-typed and promoted tags together arbitrarily. Chips order by `AdoptedAt`.

`Weight` semantics split by status:

- **Suggested:** the LLM's salience (0-1), as today. It orders the hint list.
- **Adopted:** always `1.0`, whether hand-typed or promoted.

The reason: the cloud sizes each word by *summed* weight across recordings, and per-recording salience
is meaningless for a tag a human applied deliberately. With `1.0`, summed weight equals the number of
recordings carrying the tag, so the cloud sizes words by how often the user actually used them - and
`fontSizeFor`, `tagColor`, `topTagsByCount` and `TagCloudEntryDto` all keep working untouched. A
promoted suggestion is *not* allowed to keep its generated weight, which would make promoted tags
render smaller than typed ones for no reason a user could explain.

The entity's doc comment currently asserts "Machine-generated only (never user-edited)" and the `Tag`
property's comment says "Canonical tag text (Title Case, 1-2 words per the extraction prompt)". Both
are now false and get rewritten.

### Migration

`AddRecordingTagStatus`, in three steps:

1. Add `Status` `integer not null default 0` and `AdoptedAt` `timestamptz null`. **The default is the
   demotion** - every existing row becomes a suggestion with no data script. An older backup restored
   later gets the same treatment on migrate-up, so the new semantics apply to it correctly.
2. De-duplicate any legacy case-variant rows within a recording (keep the lowest `Ordinal`). The parse
   step already dedupes case-insensitively and replace is wholesale, so this should be a no-op, but the
   next step would fail on prod if it is not.
3. Add a unique index on `(RecordingId, lower(Tag))`, applied only when
   `Database.IsNpgsql()` (the same guard the pgvector config and the one-Personal-room-per-user index
   use), so the in-memory provider can still build the model. This makes a double-add race between two
   room members impossible at the storage layer rather than by hope.

**Backup format:** additive column with a default, so an older dump restores correctly and
`MaintenanceController.CurrentFormat` is **not** bumped. Stated explicitly because a destructive or
semantic reshape would require it.

## Extraction path

`TagsProcessor.ProcessAsync` (`src/Diariz.Api/Services/TagsProcessor.cs:68-78`) changes in two ways:

- The wholesale replace narrows from `RemoveRange(rec.Tags)` to
  `RemoveRange(rec.Tags.Where(t => t.Status == Suggested))`, so adopted and dismissed rows survive a
  re-transcription. Today a re-transcribe would wipe every hand-applied tag.
- A freshly extracted tag matching (case-insensitively) an existing **Adopted or Dismissed** row is not
  inserted. This is load-bearing in both directions: without it the hint list re-offers words the user
  already holds, and per-recording dismissal would not stick past the next re-transcribe.

New rows insert with `Status = Suggested`, `AdoptedAt = null`. Everything else is untouched:
`TagsPrompt`, `MaxTags = 12`, the stale-job guard, `TagsExtractedAt`, the SignalR ping, `TagBackfill`
(which still fills suggestions for recordings with `TagsExtractedAt == null`), and the
`recording.tags_ready` payload.

## API

### Reads

`RecordingDetailDto` gains two flat lists - no nested DTO, because the UI wants exactly two arrays:

- `tags: string[]` - adopted, in `AdoptedAt` order.
- `suggestedTags: string[]` - `Suggested` only, weight descending.

Dismissed tags are never serialised. `RecordingsController.Get` needs `.Include(r => r.Tags)`, which it
does not have today.

`GET /api/tags` filters to `Status == Adopted`. Its `EndpointSummary`/`EndpointDescription` currently
state that no endpoint sets tags by hand, so that text changes.

### Writes

Three endpoints on `RecordingsController`, all gated by `CanReadRecordingAsync` (Decision 6), all
normalising input through the shared rule (Decision 4):

| Route | Body | Behaviour |
| :--- | :--- | :--- |
| `POST /api/recordings/{id}/tags` | `{ tag }` | Adopt. A case-insensitive match on that recording **flips that row** to `Adopted` and stamps `AdoptedAt` (this is promotion); otherwise insert a new adopted row with `Weight = 1.0`. Already-adopted is an idempotent `204`. `400` on blank or hyphen-only input. `404` when the caller cannot read the recording. |
| `DELETE /api/recordings/{id}/tags?tag=x` | - | Delete the row (Decision 7). `204` even when absent, so an optimistic UI cannot wedge. The tag travels as a query parameter, not a route segment, to dodge URL-encoding problems with dots and slashes. |
| `POST /api/recordings/{id}/tags/dismiss` | `{ tag }` | Mark an existing `Suggested` row `Dismissed`. `404` when there is no such suggestion. |

Promotion deliberately flips the existing row rather than inserting a new one, which keeps the "one row
per tag per recording" invariant the unique index enforces, and makes the case-insensitive dedupe fall
out of the same lookup.

Idempotent add matters: the popover persists per keystroke-commit with no Save button, so a retry or a
second member adding the same word must not 409 or duplicate.

## Web UI

Built exactly to the handoff (`docs/design_handoff_manual_tagging/README.md`), which is high-fidelity
and specifies both themes against the existing `--hub-*` token layer. Nothing is added to the page
body - only the pill and its popover.

**New files**

- `components/icons.tsx` gains `TagIcon({ size })` - the one new glyph, Feather-style on the 24 grid.
- `lib/tagInput.ts` - **pure**, and where the real logic lives: `normalizeTag(raw)` (collapse internal
  whitespace to `-`, trim hyphens) and `addTag(list, raw)` (normalise, reject case-insensitive
  duplicates, return the list unchanged on a duplicate). Separated from React precisely so the space /
  Enter / paste / Backspace rules can be tested without a DOM.
- `components/detail/TagsPill.tsx` - the trigger: tag glyph, `Tags` label, count in the muted tone,
  chevron. Hover `title` is the first 4 tags joined with ` - `, then ` - +N more`; with no tags,
  `No tags yet - click to add`. `aria-label="Tags"`, `aria-haspopup="dialog"`, `aria-expanded`. Stays
  visible while a recording is in progress.
- `components/detail/TagsPopover.tsx` - the 392px panel: header (`Tags` + `saved as you type` +
  close), the token entry field (chips + input, click anywhere focuses the input), the hint line, the
  divider, and the dashed auto-generated hints with their add and dismiss controls.

Split into pill and popover so neither file does two jobs.

**Changed files**

- `components/detail/HeroSummaryCard.tsx` - the pill goes in row 1 (`flex flex-wrap items-center
  gap-2.5`) immediately after `MeetingTypeMenu variant="pill"` and before the `ml-auto` toolbar
  cluster, wrapped in a `relative` container so the popover anchors under it.
- `lib/api.ts` - `addRecordingTag`, `removeRecordingTag`, `dismissRecordingTag`.
- `lib/types.ts` - `RecordingDetail` gains `tags` and `suggestedTags`.

`TagCloud.tsx`, `TagCloudModal.tsx`, `nav/TagsTab.tsx` and `lib/tagCloud.ts` need **no change at all**:
the cloud DTO shape is identical and the `Weight = 1.0` decision keeps their sizing arithmetic valid.

**Popover shell.** Reuse `components/hub/HubPopover.tsx` with `width={392}`; it already provides the
`absolute top-[calc(100%+8px)]`, the 14px radius, the token-driven background, `popIn .14s ease`, the
click-away backdrop and Escape. The pill keeps **local** `open` state and does **not** join the
`HubPopoverId` union: the hero card is outside `HubPopoverProvider`, so `useHubPopover()` would hand it
a private fallback instance and the "one open at a time" guarantee would be a comforting illusion.

**Interactions** (from the handoff): space commits the current word and keeps focus, so several tags
can be typed in a run; Enter commits **and closes**; paste hyphen-joins; Backspace on an empty input
removes the last chip; a chip's x removes immediately with no confirm; a hint's label promotes it (it
leaves the hint list and appears as a chip, `N left` drops); a hint's x dismisses it permanently for
that recording; when every hint is dealt with the row reads `All suggestions dealt with.`

**Data flow.** Each action fires its own mutation, optimistically patching `["recording", id]`, and on
settle invalidates `["recording", id]` plus `["tags", roomId]` so the cloud and the pill count stay in
step. Failures surface through the existing `apiErrorMessage` treatment and roll the optimistic patch
back.

**i18n.** New `workspace.json` keys in all four locales (`en`, `de`, `es`, `fr`). Note the handoff's
copy uses an em dash in `No tags yet — click to add`; the repo forbids em and en dashes in user-facing
text, so it ships with a plain hyphen.

## Testing

TDD throughout: failing test first, watch it fail, then the minimal code.

**The test that matters most.** The `Status == Adopted` filter on `GET /api/tags` needs a test that
genuinely fails without it: a fixture holding one adopted, one suggested and one dismissed tag,
asserting the cloud returns exactly the adopted one. A missed filter there silently restores the exact
noise this change exists to remove, and every other test in the suite would still pass. Mutation-verify
it by deleting the filter and confirming the failure.

**.NET unit (`tests/Diariz.Api.Tests`)**

- `TagsProcessorTests`: new rows are `Suggested`; replace spares `Adopted` and `Dismissed`; an extracted
  tag matching an adopted row is not re-inserted; ditto a dismissed row.
- `RecordingsController` tag endpoints: add inserts adopted with `Weight = 1.0`; add flips a matching
  suggestion instead of inserting (assert the row count stays put); case-insensitive duplicate is an
  idempotent no-op; blank and hyphen-only input give 400; whitespace collapses to hyphens; remove
  deletes the row; dismiss marks `Dismissed`; dismiss of a non-suggestion gives 404; a room member who
  is not the owner can do all three; a non-member gets 404 on each.
- `RecordingsControllerTests`: detail returns adopted in `AdoptedAt` order and suggested weight-desc,
  and never returns dismissed.
- `TagsControllerTests`: the adopted-only filter above, plus the existing aggregation tests still pass
  once fixtures are updated to create adopted rows.

**.NET integration (`tests/Diariz.Api.IntegrationTests`)**

- The `(RecordingId, lower(Tag))` unique index rejects a case-variant duplicate on real Postgres (the
  in-memory provider cannot enforce it).
- Cloud aggregation across mixed statuses and multiple recordings.
- The migration's de-duplication step, and cascade delete still removing tags with the recording.

**Web (`vitest` + RTL, which is wired)**

- `lib/tagInput.test.ts`: normalisation (spaces to hyphens, trimming, hyphen-only rejected),
  case-insensitive dedupe, order preservation.
- `TagsPill.test.tsx`: count renders; hover title lists the first 4 then `+N more`; empty state title;
  click toggles the popover; `aria-expanded` tracks it.
- `TagsPopover.test.tsx`: space commits and keeps the field focused; Enter commits and closes; paste
  hyphen-joins; Backspace on an empty input removes the last chip; chip x calls `removeRecordingTag`;
  a hint's label calls `addRecordingTag` and moves it out of the hint list; a hint's x calls
  `dismissRecordingTag`; `All suggestions dealt with.` appears when the list empties.
- Assert real call arguments rather than relying on a method being absent from the `vi.mock` factory.

**Browser verification.** jsdom computes no geometry, so no unit test can show that the pill fits row 1
next to a long meeting-type name, or that the 392px popover does not overflow the hub column. Check the
running app in both themes at the end and compare against the handoff screenshots.

## Release checklist

Functional enhancement, so **0.211.3 -> 0.212.0**.

1. `version.json` plus all four mirrors (`apps/web/package.json`, `apps/desktop/package.json`,
   `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`).
2. `RELEASES[0]` entry in `apps/web/src/lib/releases.ts`.
3. The `CAPABILITIES` tags row - its meaning changes from automatic to manual.
4. The README Features tags row.
5. The `docs/features.md` tags bullet.
6. `docs/Overall_Synopsis_of_Platform.md` - the three new endpoints, the suggestion lifecycle, the
   narrowed cloud, and the changed re-transcription behaviour.
7. `docs/Data_Schema.md` - the two new columns, the enum values, the unique index, and a migration
   history row.
8. `apps/web/src/content/help/en/search-and-tags.md` - behaviour users rely on genuinely changes
   (tags are now theirs to add). Also the `recording.tags_ready` line in `automations-and-signals.md`,
   which now describes suggestions.
9. The n8n node's `openapi.snapshot.json` and `generated/index.ts` regenerated for the three new
   operations. The snapshot test self-heals, so the first run fails and rewrites it - commit the
   regenerated file and re-run.

**Deployment surface:** web + API only. **Server redeploy, no desktop release** - nothing under
`apps/desktop/src/**` or the builder config is touched.

## Risks

- **A missed status filter leaks suggestions back into the cloud.** Mitigated by the mutation-verified
  test above. Only two read sites exist (`TagsController.List` and the detail projection), which is why
  the single-table model was chosen over splitting suggestions into their own table.
- **The Tags tab looks broken on day one** - an empty cloud with no explanation. Accepted, per the
  no-bulk-tooling decision. The existing `tagsEmpty` string is what a user will see; worth a read to
  confirm it reads sensibly for "you have not tagged anything yet" rather than "no tags were found".
- **Two coexisting tag styles** ("Data Collection" from promotion, `data-collection` from typing).
  Accepted as the price of not rewriting the prompt or migrating data; the cloud's existing
  case-merging keeps it tidy enough.
