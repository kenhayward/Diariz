# Release notes: epoch summaries over an archived history

**Date:** 2026-08-28
**Status:** approved, ready to plan
**Deployment surface:** server redeploy only (web app). No desktop release, no API change.
**Version:** 0.259.13 -> 0.260.0 (functional enhancement: Minor +1, Build reset)
**Issue:** none. CLAUDE.md requires an issue for *fixes*; this is a refactor plus a new view.

## Problem

`apps/web/src/lib/releases.ts` is 8,788 lines / 667 KB, of which the `RELEASES` array is 629 KB (94%).
It holds 496 entries dated 2026-06-27 to 2026-08-28 and grows by roughly 7.8 entries and 11 KB of source
per day. Nothing is incorrect today, but three costs are already real:

1. **It ships to everyone, always.** `App.tsx` imports `ReleaseNotes` statically and that page reads
   `RELEASES` in its render, so all 188 KB gzip of release history is in the initial bundle on every page
   load, for a page almost nobody opens.
2. **It has already caused a test defect.** `ReleaseNotes.test.tsx` carries a comment describing a
   quadratic assertion that reached ~5s locally and was intermittently blowing CI's 20s per-test timeout,
   because the cost grew with every release shipped.
3. **496 undifferentiated rows is a bad way to read a product's history.** The left-hand list is a flat
   wall of versions. A reader wanting to know what changed in Rooms, or when MCP landed, has to scroll.

The naive fixes are both wrong. Lazy-loading alone leaves the reading problem. Compacting old entries
into summaries destroys the changelog, drops the `pr:` links, and is a one-time win against a file that
regrows at 7.8 entries a day.

## Goals

1. Add an **epoch** layer: contiguous, named, curated spans of the release history, shown as the default
   view of `/release-notes`, newest first.
2. Keep the full history **intact and reachable** by drilling into an epoch. Summarisation is additive;
   no entry is edited, merged, or dropped.
3. Move the archived entries **off the initial bundle** behind the drill-down route.
4. Leave the **per-PR release ritual unchanged**: prepend one entry to one file, same as today.

## Non-goals

- Rewriting, merging or editing any historical `RELEASES` entry. They are the record of what shipped.
- Generating release prose at build time, or from an LLM at render time.
- Moving release data to JSON assets or to the API. Both were considered and rejected; see
  "Decisions taken".
- Splitting the archive into a chunk per epoch. One archive chunk, loaded on a deliberate click.
- Any change to `version.json`, the mirrors, or how the version assertion works.

## The epoch analysis

**Method.** Version numbers carry no era signal here (259 distinct minor lines across 496 releases - the
minor bumps nearly every other release). Dates carry almost none either (after the opening fortnight the
cadence is flat at 29-48 releases/week). What clusters is **theme**: the work runs in contiguous arcs of
one to three days that rarely interleave. Boundaries were set by reading all 496 headlines oldest-first
and cutting where the subject changes.

**An epoch is a narrative, not a taxonomy.** Some ranges swallow an off-theme release - desktop Google
sign-in sits inside the meeting-types arc. That is acceptable and deliberate: drill-down is lossless, so
an entry the summary does not mention is not lost, merely not part of that epoch's story.

**The 31 boundaries** (verified contiguous, ascending, covering all 496 entries with no gaps or overlaps):

| # | Range | Dates | Epoch | n |
|---|---|---|---|---|
| 1 | 0.1.0-0.13.1 | Jun 27-28 | Foundations, speaker identification, the first desktop app | 21 |
| 2 | 0.14.0-0.23.0 | Jun 28-29 | Uploads, action items, editable transcripts | 15 |
| 3 | 0.24.0-0.28.1 | Jun 29 | Speaking your language | 6 |
| 4 | 0.29.0-0.38.0 | Jun 29 | An organised library: sections, merges, attachments | 12 |
| 5 | 0.39.0-0.48.0 | Jun 30 | Chat tools, and the platform underneath | 19 |
| 6 | 0.49.0-0.58.0 | Jul 1-2 | Capture quality and Meeting Minutes | 13 |
| 7 | 0.59.0-0.62.6 | Jul 2 | Tabs, a status bar, and going open source | 11 |
| 8 | 0.63.0-0.68.0 | Jul 2 | Google sign-in and the first calendar | 11 |
| 9 | 0.69.0-0.80.6 | Jul 3-4 | Connecting Claude over MCP | 24 |
| 10 | 0.81.0-0.84.1 | Jul 4 | Search by meaning | 7 |
| 11 | 0.85.0-0.94.1 | Jul 5 | Every calendar, not just Google | 15 |
| 12 | 0.95.0-0.98.1 | Jul 6-7 | Meeting types and minutes templates | 17 |
| 13 | 0.99.0-0.105.3 | Jul 7 | Retention, an API of your own, live notes | 18 |
| 14 | 0.106.0-0.112.2 | Jul 8-9 | macOS, tags, and template control | 12 |
| 15 | 0.113.0-0.117.1 | Jul 9-10 | Folders that hold a whole meeting | 6 |
| 16 | 0.118.0-0.125.1 | Jul 10-11 | Rooms: shared spaces | 18 |
| 17 | 0.126.0-0.135.0 | Jul 12-13 | Formulas | 19 |
| 18 | 0.136.0-0.145.1 | Jul 14-21 | The recording hub, and minutes become a formula | 16 |
| 19 | 0.146.0-0.150.0 | Jul 22-24 | Meeting screenshots | 10 |
| 20 | 0.151.0-0.159.1 | Jul 24 | Integrations: API, Automations, n8n | 18 |
| 21 | 0.160.0-0.163.1 | Jul 25-29 | Help in the app | 12 |
| 22 | 0.163.2-0.172.0 | Jul 29-30 | People, and voiceprints as an attribute | 17 |
| 23 | 0.173.0-0.178.0 | Jul 30-Aug 4 | Knowing when something breaks | 23 |
| 24 | 0.178.1-0.187.0 | Aug 5-7 | Deep folders, cut and paste | 25 |
| 25 | 0.188.0-0.197.5 | Aug 7-9 | Outlook, and one Calendars tab | 15 |
| 26 | 0.198.0-0.208.0 | Aug 9-12 | Recording from your calendar | 19 |
| 27 | 0.209.0-0.215.0 | Aug 12-15 | Video, floating notes, and tags you own | 14 |
| 28 | 0.216.0-0.228.0 | Aug 16-18 | The usage log and administered models | 16 |
| 29 | 0.228.1-0.239.0 | Aug 19-21 | Making it fast, and choosing your model | 23 |
| 30 | 0.240.0-0.248.2 | Aug 22-24 | Screenshots into chat, and reading their text | 16 |
| 31 | 0.249.0-0.259.13 | Aug 24-28 | Review Voice Matches | 28 |

**Epochs 1-30 are closed at ship time.** Epoch 31 stays open: its 28 entries plus this release live in
`current.ts` and render as the "current" block at the top of the page. It gets an epoch record at its
first rollover. Closing it now would leave the open block holding a single lonely release.

**The ratio is the point.** 31 epochs from 496 releases, growing at roughly 1-2 epochs per month against
~2,800 releases per year. The default view stays scannable permanently; the archive can grow without
limit behind it.

## Design

### Module layout

One module becomes four. `apps/web/src/lib/releases.ts` is deleted.

| Module | Contents | Loading |
|---|---|---|
| `lib/appInfo.ts` | `TAGLINE`, `GITHUB_URL`, `COPYRIGHT`, `LICENSE`, `CAPABILITIES` | eager |
| `lib/releases/epochs.ts` | `EPOCHS` - the 30 closed epochs, ~15 KB | eager |
| `lib/releases/current.ts` | `RECENT` - releases since the last closed epoch | eager |
| `lib/releases/archive.ts` | `ARCHIVE` - every entry covered by a closed epoch | **lazy only** |

Splitting `appInfo.ts` out is load-bearing, not tidiness. `AboutModal` and `Help` import `TAGLINE` and
`CAPABILITIES` and are both eager; leaving them in the same module as the data means relying on Rollup's
tree-shaking to keep the archive out of the eager chunk rather than making it structural.

`lib/releases/index.ts` is the barrel. It re-exports `EPOCHS`, `RECENT`, the types, and:

```ts
export const loadArchive = () => import("./archive").then((m) => m.ARCHIVE);
```

**No module outside the drill-down route and the tests may import `./archive` statically.** That rule is
the whole lazy boundary, so it gets its own test (below) rather than living on discipline.

### Data shapes

`Release` is unchanged. Added:

```ts
export interface Epoch {
  id: string;      // stable URL slug, e.g. "rooms", "review-voice-matches"
  title: string;
  from: string;    // oldest version in the range (inclusive)
  to: string;      // newest version in the range (inclusive)
  summary: string; // markdown, rendered with renderMarkdown
  highlights?: string[];
}
```

`id` is stable and hand-authored because it appears in the URL. Renaming a `title` must not break a
bookmark, so the slug is not derived from the title.

### Derived, not stored

An epoch does **not** store its date span or release count. Both are computed from the entries in its
range. Storing them would create a second derivation of the same fact, and the two would agree only by
luck - the failure mode is a count that reads as authoritative and is wrong.

Deriving them on the epoch-list page means the eager bundle needs the version and date of archived
entries. Rather than pull the archive back in, `epochs.ts` also exports a small spine:

```ts
/// One entry per archived release, newest first, in the same order as ARCHIVE.
/// Extended at rollover, ~20 KB.
export const ARCHIVED_SPINE: ReadonlyArray<{ version: string; date: string }>;
```

An **ordered array, not a `Record`**: an epoch's release count is then the length of a slice, and its
date span the two ends of that slice, so neither needs a version comparator. Keying it by version would
make both depend on JS object key-insertion order - which happens to hold for these strings, and is
exactly the kind of incidental guarantee that breaks silently later.

This is the one derived value that is stored, and it is stored because the alternative defeats the lazy
split. A test asserts it matches `ARCHIVE` exactly - same versions, same dates, same order - so it
cannot drift.

### Routes and UI

- **`/release-notes`** - the epoch list. Newest first. The open block (`RECENT`) renders at the top as
  today's list of individual releases, so the newest release is still the first thing on the page. Below
  it, one card per closed epoch: title, version range, date span, release count, summary, highlights.
  Each card links to its drill-down.
- **`/release-notes/:epochId`** - `lazy()`. The full, verbatim entry list for that epoch, in the existing
  two-panel layout (list left, selected release right). An unknown `epochId` redirects to
  `/release-notes`.

`ReleaseNotes.tsx` splits into `ReleaseNotes.tsx` (epoch list, eager) and `EpochDetail.tsx` (lazy). The
existing `ReleaseDetail` and `ChangeList` components move to `EpochDetail.tsx` unchanged.

### Rollover

**Every PR:** prepend one entry to `lib/releases/current.ts`. Identical to today apart from the path.

**At a rollover** (a deliberate act, roughly when an arc finishes):

1. Write the epoch record - `id`, `title`, `from`, `to`, `summary` - into `epochs.ts`.
2. Move that range's entries from `current.ts` to the top of `archive.ts`.
3. Extend `ARCHIVED_SPINE`.

A test fails when `current.ts` exceeds **80 entries**. That is a safety net, not the trigger: the trigger
is judgement about when an arc is done, and the historical epochs average 16 entries. 80 leaves natural
sizing alone while guaranteeing the eager file never passes ~100 KB.

### Bundle effect

Eager: `EPOCHS` + `ARCHIVED_SPINE` + `RECENT`, roughly 15 KB gzip. Lazy: ~173 KB gzip, behind a click.
The default page render drops from 496 rows to 31.

## Testing

TDD throughout. The interesting risk is not "does a card render" - it is the completeness invariants,
which are exactly the shape this repo keeps getting wrong (a guard that covers part of its domain and
reads as if it covers all of it). So the union tests come first and are written to fail if any entry is
unaccounted for, not merely if each epoch is individually well-formed.

**Data invariants** (`lib/releases/releases.test.ts`, importing `archive.ts` directly - a static test
import does not affect the bundle):

1. `RECENT[0].version === APP_VERSION`. Preserves today's guarantee.
2. The union of `RECENT` and `ARCHIVE` has no duplicate versions and is non-increasing by date.
3. Every entry still has a version, date, headline and summary.
4. **Coverage:** every entry in `ARCHIVE` falls inside exactly one epoch range, and every epoch range
   contains at least one entry. No entry is in two epochs; no epoch is empty.
5. **Contiguity:** ordered by version, the epoch ranges are adjacent - epoch N+1's `from` is the entry
   immediately after epoch N's `to` in the archive. No gaps.
6. **The open boundary:** concatenating `RECENT` then `ARCHIVE` reproduces the full history in order,
   and `ARCHIVE[0].version` is the newest closed epoch's `to`. This is what makes "newer than the last
   closed epoch" precise without a version comparator.
7. `ARCHIVED_SPINE` has exactly one entry per `ARCHIVE` entry, in the same order, with matching dates.
8. Epoch `id`s are unique and URL-safe; `title` and `summary` are non-empty.
9. `current.ts` holds at most 80 entries.

**Structural invariant** (the lazy boundary): a test reads the source of every non-test module under
`apps/web/src` and asserts none of them contains a static import of `lib/releases/archive`. Without this
the lazy split silently regresses the first time someone adds an import, and nothing else would catch it.

**Component tests** (`@testing-library/react`, plain assertions - jest-dom is not used in this repo):
the epoch list renders one card per epoch with its derived count and date span; the open block shows the
newest release; clicking an epoch navigates to its route; an unknown `epochId` redirects.

**Existing tests to update:**

- `ReleaseNotes.test.tsx` - rewritten for the new page shape.
- `noFancyDashes.test.ts:79` hardcodes `apps/web/src/lib/releases.ts`. It must be widened to a glob over
  `apps/web/src/lib/releases/*.ts` plus `appInfo.ts`, or it silently stops covering the archived notes
  and the new epoch summaries. This is the single easiest thing in the change to get wrong.

## Build order

One PR - a half-migrated `releases.ts` is worse than either end state - built in this order:

1. Extract `appInfo.ts`; repoint `AboutModal` and `Help`. No behaviour change, suite stays green.
2. Split the data into `current.ts` and `archive.ts` with the barrel; port the existing tests to the
   union. Still no UI change.
3. Add the union, coverage, contiguity and structural lazy-boundary tests. Red first: they must fail
   against a deliberately broken boundary before the real `epochs.ts` exists.
4. Add `epochs.ts` with the 30 records and `ARCHIVED_SPINE`.
5. Split the page into the epoch list plus the lazy `EpochDetail` route.
6. Release checklist.

**Review checkpoint before the PR opens:** the 30 epoch summaries are user-facing prose drafted from the
headlines. They get read and corrected by hand before anything is pushed, not after.

## Release checklist for this PR

1. `version.json` -> `0.260.0`, plus all seven mirrors (web, desktop and n8n `package.json`, the API
   csproj, and the three `package-lock.json` files, two `version` fields in each).
2. A `RELEASES[0]` entry - which now means the top of `lib/releases/current.ts`.

Items 3-7 do not apply and the PR should say so: `CAPABILITIES` and the About-box disclaimers are
unchanged (no scope change, no new third-party library); the README Features table and `docs/features.md`
do not list release notes as a feature; `Overall_Synopsis_of_Platform.md` is untouched because no
component, queue, cross-boundary contract, dependency or port changes; `Data_Schema.md` is untouched
because there is no schema change. Help articles are unchanged - no behaviour a user relies on changes.

## Decisions taken

- **Epochs over compaction.** Summarising old entries in place was rejected: it destroys the changelog in
  the shipped app, drops the `pr:` links, and is a one-time win against a file that regrows daily. An
  epoch layer above an intact archive gets the readability without the loss.
- **TypeScript modules over generated JSON assets.** A JSON index plus per-release bodies scales better
  (the page would be constant-cost at 5,000 entries) but adds a generated-artifact sync target. This repo
  has a demonstrated failure record with those - the n8n node's `generated/index.ts` stayed stale across
  three merged PRs. Revisit only if the archive chunk itself becomes the complaint.
- **Not the API or the database.** The content is build-time-fixed, identical for every user, and the page
  is public and must work before sign-in. A migration, controller and cache buy nothing over a static
  module.
- **One archive chunk, not one per epoch.** ~173 KB gzip on a deliberate drill-down click is acceptable.
  Per-epoch splitting is the JSON-asset design in disguise; deferred with it.
- **Summaries hand-written, reviewed before shipping.** Drafted from the headlines for the 30 historical
  epochs, then corrected by hand. Going forward, one paragraph written at each rollover. Generating them
  at build time was rejected: non-deterministic, unverifiable, and it re-derives content that never
  changes.
