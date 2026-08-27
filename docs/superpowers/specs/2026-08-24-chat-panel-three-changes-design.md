# Three chat-panel-adjacent changes

Date: 2026-08-24
Status: approved, not yet implemented

> **Merged retrospectively on 2026-08-27.** The `Status` line above is preserved as written - this is a
> point-in-time design record, in the same style as the other documents in this folder. All three changes
> have since shipped: Feature 1 in **v0.246.0** (PR #597), Feature 2 in **v0.247.0** (PR #600), Feature 3
> in **v0.248.0** (PR #602). Read this as the rationale behind that code, not as outstanding work.
>
> Account names, email addresses and person ids in the production table below are **invented placeholders**
> substituted before merge; the counts are the real measurements.

Three independent changes, shipping as **three PRs with one release each**:

1. **Profile: "You in transcripts"** - surface the account's linked person and its voiceprint state, and fix
   the personal room name so it tracks the display name.
2. **`$USERNAME` in formulas** - substitute the running user's transcript name into a formula's template.
3. **Drag an attachment into chat** - drop an existing attachment onto the composer to add its text to the
   context pill.

They share no code. Implement and merge in any order; the only coupling is that each touches the published
OpenAPI surface (see [Cross-cutting](#cross-cutting)).

---

## Background: what the data actually shows

The premise that started this work was "there is no direct link between the logged-in user and their own
voiceprint". That is *half* true, and the half that is false changed the design.

`Person.LinkedUserId` already means "the account this person **is**", and `PeopleDirectory.EnsureForUserAsync`
mints a person for every account on first use, named from the account's display name and kept in step by
`SyncFromUserAsync`. So the link exists. What is missing is any way for an ordinary user to **see** it:
`PeopleController.List` is gated behind `ManagePeople`, so a non-admin cannot find their own person row at all.

A read of production confirmed the practical consequence:

| Account | Recordings | Personal room name | Linked person | Voiceprint |
|---|---|---|---|---|
| `ada@example.com` | 159 | **"Platform Administrator"** (stale) | "Ada Lovelace" `aaaa1111` | 0 samples, none |
| `ada.personal@example.com` | 1 | "Ada lovelace" | "Ada Lovelace" `bbbb2222` | 8 samples |

Two facts fell out of that:

- The working account's voiceprint sits on the *other* account's person. `PeopleController.Merge` deliberately
  refuses to merge two people who both have accounts ("they are two different humans"), so there is no in-app
  path out of this. **This is a two-accounts-one-human data situation, and it is fixed operationally, not in
  code.** The design therefore adds **no picker** - only a read-only display of the link that already exists.
- The personal room name is stamped once at creation by `RoomScope.PersonalRoomIdAsync` and never re-synced,
  while the *person* is re-synced on every profile save. Hence the drift. Personal rooms are immutable
  (`UpdateRoomAsync` refuses `RoomKind.Personal`), so there is no hand-typed name to clobber: tracking the
  display name is unambiguously correct.

---

## Feature 1: Profile "You in transcripts" + personal room name

### 1a. Read-only profile block

**Contract.** `UserProfileDto` gains one nested record:

```csharp
public record SelfPersonDto(Guid Id, string Name, bool HasVoiceprint, int SampleCount, bool VoiceprintOptOut);
```

added as a trailing optional parameter (`SelfPersonDto? Person = null`) so the positional record stays
source-compatible with its existing call sites.

`UserProfileController.Get` calls `await _people.EnsureForUserAsync(UserId)` **before** projecting - the same
self-heal `PeopleController.List` already performs, so an account created by a path that forgot to provision
still renders a block rather than a blank. `HasVoiceprint` mirrors `PersonDto`'s existing rule:
`p.Embedding is not null || p.SampleCount > 0`.

`UserProfileController.Update` is untouched by this half.

**UI.** `ProfileSection` renders a read-only block under the professional-details fields:

- Label: "You in transcripts".
- The person's name, as static text (not an input - it follows the display name above, and saying so is the
  point of the block).
- Voiceprint state, one of:
  - `Voiceprint: 8 samples`
  - `No voiceprint yet` plus a one-line hint: assign yourself as the speaker on one of your transcripts, then
    enrol your voice from that speaker.
  - `Voice-printing opted out` when `VoiceprintOptOut`.

No controls. Erasing a voiceprint and opting out already exist for `isSelf` in the people UI and are not
duplicated here.

**Copy rules.** Plain hyphens only, no em/en dashes. New keys go in the `account` namespace alongside the
existing profile keys.

### 1b. Personal room name tracks the display name

**Service.** New method on `IRoomScope`:

```csharp
Task SyncPersonalRoomNameAsync(Guid userId, CancellationToken ct = default);
```

It finds the user's personal room and sets `Name` to the same `Display(user)` value the room is created with,
saving only when it differs. Idempotent, and a no-op for a user with no personal room yet (the room's creation
path already names it correctly).

**Call sites.** Every place that writes `ApplicationUser.FullName`, beside the existing person sync:

| Site | Today | Change |
|---|---|---|
| `UserProfileController.Update` ([:109](../../../src/Diariz.Api/Controllers/UserProfileController.cs)) | calls `SyncFromUserAsync` | add the room sync |
| `AuthController` accept-invite ([:185](../../../src/Diariz.Api/Controllers/AuthController.cs)) | calls `SyncFromUserAsync` | add the room sync |
| `Seeder` ([:59](../../../src/Diariz.Api/Services/Seeder.cs)) | writes `FullName`, syncs **neither** | add both |

The `Seeder` gap is almost certainly how the production room got stuck on "Platform Administrator": the room
was created from the seeded name, the user later renamed themselves through the profile, the person followed
and the room did not.

`GoogleSignInHandler` only ever fills a **blank** `FullName`, and on the account-creation path the room does
not exist yet (it is created named correctly on first use), so it needs no call.

**Backfill.** A migration `UPDATE`s every `Kind = Personal` room whose `Name` differs from its owner's
resolved display name. A migration rather than boot-time reconciliation: the Seeder runs on every boot, and a
data move placed there re-applies itself forever (this repo has been bitten by exactly that). The migration is
**not** destructive and needs no `MaintenanceController.CurrentFormat` bump.

**Design note - why store-and-sync rather than derive-at-read.** Deriving a personal room's name from its
owner at projection time would make drift structurally impossible. It is rejected because it means editing
every room-name projection (`ListRoomsAsync`, the placement projection, the switcher) and joining `Users` on a
hot path, and because the codebase has already made the opposite choice for the same shape of problem:
`Speaker.DisplayName` is denormalised and fanned out on rename, with an explicit rationale. The accepted cost
is that a **fourth** `FullName` write site could forget to sync. The invariant test below is the guard.

### 1c. Tests

Red first, in this order:

- **Unit** (`Diariz.Api.Tests`): `GET /api/user/profile` returns the linked person's name and voiceprint state;
  returns a block even when no person row exists yet (proving the `EnsureForUserAsync` self-heal).
- **Unit**: renaming through `UserProfileController.Update` leaves the personal room's `Name` equal to the new
  display name. This is the invariant test that catches a forgotten fourth call site.
- **Unit**: the accept-invite path does the same.
- **Integration** (`Diariz.Api.IntegrationTests`): the backfill migration corrects an already-drifted room. Must
  be integration, not unit - it is raw SQL against real Postgres.
- **Web** (`vitest` + RTL): `ProfileSection` renders the name and the "no voiceprint yet" hint; renders the
  sample count when there is one. Plain assertions - `jest-dom` is not a dependency of `apps/web`.

---

## Feature 2: `$USERNAME` in formula templates

### Behaviour

A formula's template may contain `$USERNAME`. At run time it is replaced with the running user's **transcript
name**, everywhere in the template - prompt blocks *and* literal/boilerplate text. This makes prompts like
"What role did $USERNAME play in this meeting" and "What was the attitude of speakers apart from $USERNAME"
work.

### Why this is a new mechanism and not a `{{field}}`

The template system already has placeholders - `{{date}}`, `{{attendees}}`, `{{transcript}}` - resolved by
`TemplateFields`. Those are deliberately **output-only**: *"A field is stamped into the OUTPUT document; it
never enters a prompt."* That is a load-bearing rule (it is why `{{transcript}}` costs no tokens). `$USERNAME`
has to reach the model, so it cannot be a field. The two conventions will coexist; that is a known and
accepted learnability cost, chosen over weakening the field rule.

### Implementation

**A pure helper** (new, in `Diariz.Api/Services`), unit-testable with no DB:

```csharp
public static class PromptTokens
{
    public static string Substitute(string text, string? userName);
}
```

- Matches `\$USERNAME\b`, case-sensitive. The word boundary matters: a literal `$USERNAMES` in a prompt must
  survive intact rather than becoming `Ada LovelaceS`.
- A null/blank name substitutes nothing and leaves the token in place, which reads as an obvious fault rather
  than silently producing "What role did  play in this meeting".

**Applied to the whole parsed template**, via a `TemplateContent` transform that returns a new content with
every block's text substituted, at both parse sites in `FormulaRunProcessor`:

- [`RunOverRecordingAsync`](../../../src/Diariz.Api/Services/FormulaRunProcessor.cs) (single-recording run)
- the folder map/reduce run

Doing it on the parsed content, not on the prompt string, is what makes prompt blocks and literal blocks agree
for free.

**Name resolution.** The linked person's `Name`, read with a plain query
(`People.Where(p => p.LinkedUserId == userId)`), falling back to `ApplicationUser.FullName`, then `Email`. The
person's name is the right source even though the sync keeps it equal to the display name: it is *the name
that appears in transcripts*, which is what the prompt is asking the model to match against.

Deliberately **not** `IPeopleDirectory.EnsureForUserAsync`: that mints a person as a side effect, and running
a formula is not a reason to write to the directory. A user with no person row falls through to the display
name, which is the same string anyway.

**Whose name.** The user the run is attributed to:

- Synchronous path (`FormulaRunner.RunAsync`, also the chat `run_formula` tool and MCP): the caller.
- Async path (`FormulaRunProcessor`): `job.UserId` - so an automatic, meeting-type-triggered run uses the
  recording owner.

Resolved **once at the caller** and threaded into the helpers as a plain `string?`, so
`RunOverRecordingAsync` and the folder helper stay free of a directory dependency.

**Scope.** Formulas only. Meeting-type minutes templates share `TemplateContent` and the composer but are
explicitly out of scope: they are generated for a recording whose "current user" is only ever the owner, where
the token reads oddly.

### No editor UI

Deliberately none. Documented in the help content and the release notes only.

### Tests

- **Unit**, `PromptTokens`: substitutes a bare token; substitutes multiple occurrences; leaves `$USERNAMES`
  alone; leaves the token alone for a null or blank name; leaves unrelated `$` text alone.
- **Unit**, template transform: substitutes in a prompt block and in a literal text block in one pass.
- **Unit**, run path: a formula whose prompt contains `$USERNAME` reaches the fake chat client with the name
  already substituted. Assert on the **captured system message**, not on the helper - the helper's own test
  cannot prove it is wired in.
- **Unit**: the async job path resolves the name from `job.UserId`, not from whoever happens to be current.

---

## Feature 3: drag an attachment into the chat composer

### Behaviour

An attachment row can be dragged onto the chat composer. On drop, its extracted text joins the composer's
context pill, exactly as OCR-extracted text does today: appended under its own heading, with the pill's label
counting the parts, and clickable to open `ChatAttachmentPreviewModal`.

### Server

**Extract the resolver.** The per-attachment half of `ChatController.LoadAttachmentDocumentsAsync`
([:567](../../../src/Diariz.Api/Controllers/ChatController.cs)) moves into a new
`IAttachmentTextResolver`: a **File** attachment streams from storage and goes through `IAttachmentExtractor`;
a **Url** attachment goes through the existing SSRF-guarded `FetchTextAsync`. `LoadAttachmentDocumentsAsync`
then calls it in a loop, keeping its current skip-on-failure behaviour.

**New endpoint.**

```
POST /api/chat/attachment/library
body: { recordingId?: Guid, sectionId?: Guid, attachmentId: Guid }
200:  ChatAttachmentDto(Name, Chars, Text)
```

Exactly one of `recordingId` / `sectionId` must be supplied (400 otherwise). Access mirrors the owning
controller in each case, and is **not** re-invented:

- `recordingId`: the attachment's recording must satisfy `Recording.UserId == UserId` (as
  `LoadAttachmentDocumentsAsync` already requires).
- `sectionId`: `IRoomScope.ViewableSectionAsync`, matching `SectionAttachmentsController`'s read gate.

Unlike the bulk path, a single explicit drop **reports** its failures: 404 for an unknown or inaccessible
attachment, 400 for an unsupported type or an extraction that yields nothing. Silently swallowing those would
leave the user staring at a composer that did not change.

### Web

**Drag payload.** A new type beside `SCREENSHOT_DRAG_TYPE` in `lib/dragTypes.ts`:

```ts
export const ATTACHMENT_DRAG_TYPE = "application/x-diariz-attachment";
// payload: { scope: "recording" | "section", ownerId: string, attachmentId: string, name: string }
```

**Only this type is set on the drag.** Not `text/plain`: the recordings panel reads a bare `text/plain`
payload as a recording id being reordered, so setting it would let an attachment drag land as a reorder. Rows
are plain elements rather than images, so no `Files` type is advertised and `dragHasFiles` needs no new
exclusion (the one it already carries exists because dragging an `<img>` thumbnail does advertise `Files`).

**Drag sources.** All three attachment lists, because they are one feature to the user and making one row
draggable and its neighbour not would read as a bug:

| Component | Scope | Row identity |
|---|---|---|
| `AttachmentsManager` | recording | the page's `recordingId` + `a.id` |
| `FolderAttachmentsList` | recording | the row's own `recordingId` + `id` (rows span many recordings) |
| `FolderAttachmentsManager` | section | the page's `sectionId` + `a.id` |

Each gets a visible affordance (grab cursor plus a title). `ScreenshotsSection` makes the same point about its
thumbnails: the gesture is invisible otherwise.

**Composer.** `ChatPanel`'s existing drop zone branches on the MIME type: `SCREENSHOT_DRAG_TYPE` keeps its
current behaviour, `ATTACHMENT_DRAG_TYPE` calls the new endpoint (reusing the `uploading` flag for the
in-flight state and `setError` for failures) and merges the result into the pill.

### The accumulation rule (the one real behaviour change)

Today the pill's `origin` decides everything, and only `"ocr"` accumulates:

- OCR onto nothing -> new OCR pill.
- OCR onto OCR -> append under `---` + `## name`, label becomes "Extracted text (N captures)".
- OCR onto a file -> **confirm**, then replace.
- A file (paperclip) onto anything -> replace, unconditionally.

Dropped attachments are documents, so they carry `origin: "file"` - and files do not accumulate today. The
rule becomes symmetric:

- Document onto nothing -> new file pill, named after the document.
- Document onto a file pill -> **append** under `---` + `## name`, label becomes "N documents".
- Document onto an OCR pill -> **confirm**, then replace.
- OCR onto a file pill -> confirm, then replace (unchanged).

The counter field generalises from `captures?: number` to `parts?: number`; it is transient client state and
is not part of the saved-conversation contract, so nothing persisted changes.

Two consequences, both accepted deliberately:

- **The paperclip now accumulates too.** Leaving the paperclip replacing while drops append would mean two
  ways of adding the same kind of thing behaving differently. The cost is that re-picking a wrong file now
  needs one click on the pill's remove control first. Call this out in the release notes.
- **A pill is never mixed-origin.** The preview modal renders Markdown for `ocr` and preformatted plain text
  for `file`; a mixed pill would render half of itself wrong. The cross-origin confirm is what enforces that,
  and it is now symmetric rather than one-directional.

### What is out of scope

- **No second pill.** The single-slot model and the `attachmentName`/`attachmentText` saved-conversation
  contract are unchanged.
- **No module channel.** Screenshots have one because the viewer cannot reach the panel through the tree.
  Drag-and-drop needs a visible drop target, so a collapsed chat rail simply cannot be dropped on. Nothing
  auto-expands.

### Tests

- **Unit** (`Diariz.Api.Tests`): the new endpoint returns extracted text for a recording attachment; 404 for an
  attachment on someone else's recording; 400 when neither or both of `recordingId`/`sectionId` are supplied;
  400 for an unsupported type. Fakes come from `TestSupport` - no mocking library.
- **Unit**: a section attachment resolves for a viewer of the folder and 404s for a non-viewer.
- **Unit**: `LoadAttachmentDocumentsAsync` still skips a failing attachment after the refactor (proving the
  extraction did not change bulk behaviour).
- **Web**: dropping an `ATTACHMENT_DRAG_TYPE` payload on the composer calls the api method and shows a pill
  with the returned name.
- **Web**: a second drop appends and relabels to "2 documents"; the pill's text contains both headings.
- **Web**: dropping onto an OCR pill prompts before replacing.
- **Web**: attachment rows carry `draggable` and set **only** `ATTACHMENT_DRAG_TYPE` on `dragStart` (this is
  the test that protects the recordings panel from reading the drag as a reorder).
- **Web**: clicking the pill opens the preview with the dropped text.

Note for the implementer: `fireEvent` will happily fire handlers the browser would not; use `userEvent` (it is
installed) where a real gesture matters, and assert on `dataTransfer.setData` calls for the drag payload.

---

## Cross-cutting

**OpenAPI + n8n.** Features 1 and 3 change the published surface (`api/user/profile` and `api/chat`; neither
prefix is in the admin exclusion list). Each of those PRs must:

1. Run the OpenAPI snapshot test **twice** - it rewrites its own snapshot, so run 1 fails and run 2 passes -
   and commit the regenerated file.
2. Run `npm run generate` in `integrations/n8n-nodes-diariz` and commit `generated/index.ts`. That file does
   **not** self-heal, and a stale copy reds the community-node check.

Feature 2 changes no endpoint and needs neither.

**Release checklist**, per PR (current version at time of writing: `0.245.0`; all three are functional
enhancements, so each takes a Minor bump with Build reset):

1. `version.json` plus its mirrors - `apps/web/package.json`, **`apps/web/package-lock.json` (two places)**,
   `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`,
   `integrations/n8n-nodes-diariz/package.json`. `versionMirrors.test.ts` fails the build on any drift.
2. A `RELEASES[0]` entry in `apps/web/src/lib/releases.ts` with the real PR number.
3. `CAPABILITIES` table row in the same file - features 2 and 3 change scope; feature 1 arguably does too.
4. README Features table row, and
5. the matching `docs/features.md` bullet, always in lockstep with 4.
6. `docs/Overall_Synopsis_of_Platform.md` - feature 3 adds an endpoint and a service; feature 1 adds a
   cross-boundary invariant worth stating.
7. `docs/Data_Schema.md` - **feature 1 only**, for the backfill migration in its migration-history table.

**Issue vs no issue.** Feature 1 contains a genuine bug (the stale personal room name), so it opens a GitHub
issue first and its PR body carries `Fixes #<n>`. Features 2 and 3 are enhancements and need none.

**Deployment surface.** All three are **server redeploy only**. Nothing touches `apps/desktop/**`, so no
desktop release and no `v*` tag.

**Help content.** Feature 2 needs a help article edit (the token is otherwise undiscoverable, by design).
Feature 3 changes behaviour a user relies on (the paperclip now accumulates), so the chat help article needs a
line. Feature 1 is informational and probably needs none. Help content is ASCII only and carries the
`title`/`summary`/`group`/`order` front matter that `helpContent.test.ts` enforces.
