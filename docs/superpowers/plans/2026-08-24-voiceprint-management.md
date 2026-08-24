# Voiceprint Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user tell two same-named people apart, split a mixed-speaker transcript segment at an exact word boundary and reassign the piece, and choose which spans of audio train a person's voiceprint.

**Architecture:** Three arcs on one branch. (A) A pure web presenter renders each person's account identity in the three places two people can look alike - no API change. (B) The worker starts persisting whisperx's word timings into a new nullable `jsonb` column, which gives a split an exact cut point; two new endpoints split a segment and reassign one segment's speaker. (C) A `VoiceSample` gains a nullable list of time spans; changing it queues a job on a third Redis stream, where the worker slices exactly those spans, re-embeds with ECAPA, and posts the vector back.

**Tech Stack:** ASP.NET Core (.NET 10) + EF Core + Npgsql/pgvector; Python worker (whisperx, SpeechBrain ECAPA, redis-py); React 19 + TS + Vite + Tailwind v4; xUnit + Testcontainers, pytest, vitest + @testing-library/react.

**Spec:** `docs/superpowers/specs/2026-08-24-voiceprint-management-design.md`. Read it before Task 1.

## Global Constraints

- **TDD is required.** Write the failing test, run it, watch it fail with the expected message, then write the minimal code. Never write production code first.
- **Never commit or push to `main`.** All work is on branch `feat/voiceprint-management`, already created.
- **No em dashes or en dashes in user-facing text** - UI strings, i18n catalogs (`apps/web/src/locales/**`), release notes. Use a plain hyphen `-`. Code, comments and internal docs are unaffected.
- **Never `git add -A`.** Stage explicit paths. This repo accumulates untracked agent scratch files (`AGENTS.md` is untracked right now).
- **`dotnet test --filter "Name=X"` does not work here.** Use `--filter "FullyQualifiedName~X"`.
- **No mocking library.** Add a fake to `tests/Diariz.Api.TestSupport` instead.
- **No `InternalsVisibleTo`.** Test through public seams.
- **Split queries are the app-wide default.** Just write the `Include`s; do not add `.AsSplitQuery()`.
- **Postgres-only model config goes behind `Database.IsNpgsql()`** in `OnModelCreating`, or the in-memory unit provider cannot build the model.
- **JSON columns are `string` properties** with `HasColumnType("jsonb")` behind the `isNpgsql` guard - the established convention here (see `MeetingType.ContentJson`, `LlmModel.ParametersJson`). Not owned types.
- **Web tests use plain assertions.** `@testing-library/jest-dom` is NOT installed and must not be added. Use `userEvent`, not `fireEvent` - `fireEvent.click` fires handlers on disabled controls.
- **Before pushing, build `Diariz.slnx`**, not just the unit test project - integration and CodeQL compile breaks do not show up in a unit-only run.
- Run `dotnet test tests/Diariz.Api.Tests` for fast unit tests (no Docker); `dotnet test tests/Diariz.Api.IntegrationTests` needs Docker.

---

## File Structure

**Created**

| File | Responsibility |
|---|---|
| `apps/web/src/lib/personIdentity.ts` | Pure: a `Person` to one identity line. The single source for all three surfaces. |
| `apps/web/src/lib/personIdentity.test.ts` | Its tests. |
| `src/Diariz.Api/Services/TranscriptSegmentSplit.cs` | Pure: split words + original text at a word index. |
| `tests/Diariz.Api.Tests/TranscriptSegmentSplitTests.cs` | Its tests. |
| `src/Diariz.Api/Services/VoiceprintSpans.cs` | Pure: reconcile stored spans against current segments; serialise spans. |
| `tests/Diariz.Api.Tests/VoiceprintSpansTests.cs` | Its tests. |
| `src/Diariz.Api/Controllers/WorkerVoiceprintCallbackController.cs` | `internal/people/voiceprint-*`, `X-Worker-Secret` auth. |
| `apps/web/src/components/detail/SegmentSplitModal.tsx` | The word-gap picker + speaker choice for the new half. |
| `apps/web/src/components/detail/SegmentSplitModal.test.tsx` | Its tests. |
| `apps/web/src/components/PersonProfileTab.tsx` | Today's `PersonEditor` body, extracted unchanged. |
| `apps/web/src/components/PersonVoiceprintTab.tsx` | Sample list, segment selection, recompute. |
| `apps/web/src/components/PersonVoiceprintTab.test.tsx` | Its tests. |
| `src/Diariz.Worker/voiceprint.py` | Pure: slice spans out of a waveform and embed. |
| `src/Diariz.Worker/tests/test_voiceprint.py` | Its tests. |

**Modified (principal edits only)**

| File | Change |
|---|---|
| `src/Diariz.Worker/pipeline.py` | `_shape_segments` keeps aligned words; `EMBED_MAX_SECONDS` raised. |
| `src/Diariz.Worker/worker.py` | Third stream in `run_loop`, `handle_voiceprint`. |
| `src/Diariz.Worker/callback.py` | `post_voiceprint_result` / `post_voiceprint_failure`. |
| `src/Diariz.Worker/config.py` | `VOICEPRINT_STREAM_KEY`, raised `EMBED_MAX_SECONDS`. |
| `src/Diariz.Domain/Entities/Segment.cs` | `WordsJson`. |
| `src/Diariz.Domain/Entities/Speaker.cs` | `EmbeddingStale`. |
| `src/Diariz.Domain/Entities/VoiceSample.cs` | `SpansJson`, `UsedMs`. |
| `src/Diariz.Domain/DiarizDbContext.cs` | Three `jsonb` mappings behind the `isNpgsql` guard. |
| `src/Diariz.Api/Contracts/WorkerContracts.cs` | `SegmentWord`, `SegmentResult.Words`, `VoiceprintJob`, `VoiceprintResult`, `VoiceprintFailure`. |
| `src/Diariz.Api/Contracts/ApiDtos.cs` | `SegmentDto.HasWords`, split/assign requests, `VoiceSampleDto` fields. |
| `src/Diariz.Api/Controllers/WorkerCallbackController.cs` | Persist words. |
| `src/Diariz.Api/Controllers/RecordingsController.cs` | Words / split / segment-speaker endpoints. |
| `src/Diariz.Api/Controllers/PeopleController.cs` | Spans endpoint, richer `VoiceSampleDto`. |
| `src/Diariz.Api/Services/SegmentMerger.cs` | `Part.Words`, concatenation. |
| `src/Diariz.Api/Services/TranscriptSegmentMerge.cs` | Carry words through the rebuild. |
| `src/Diariz.Api/Services/JobQueue.cs` | `EnqueueVoiceprintAsync`. |
| `apps/web/src/components/PeopleModal.tsx` | Identity line in rows and the duplicates banner. |
| `apps/web/src/components/MergePeopleDialog.tsx` | Identity under both names. |
| `apps/web/src/components/PersonEditor.tsx` | Becomes the tab shell. |
| `apps/web/src/pages/RecordingDetail.tsx` | Split toolbar button + modal wiring. |

**Note on migrations.** The spec says "one migration". Three tasks add columns and each must be independently testable, so this plan emits **three** migrations (Tasks 4, 9, 11). Functionally identical; all additive and nullable-or-defaulted, so still forward-restore-safe and still **no `MaintenanceController.CurrentFormat` bump**.

---

### Task 1: `personIdentity` presenter + People list rows

**Files:**
- Create: `apps/web/src/lib/personIdentity.ts`
- Create: `apps/web/src/lib/personIdentity.test.ts`
- Modify: `apps/web/src/components/PeopleModal.tsx` (the list `<li>` around line 207)
- Modify: `apps/web/src/locales/en/people.json`
- Test: `apps/web/src/components/PeopleModal.test.tsx`

**Interfaces:**
- Consumes: `Person` from `apps/web/src/lib/types.ts` - already has `email: string | null`, `linkedUserId: string | null`, `isSelf: boolean`.
- Produces: `personIdentity(p: Person): PersonIdentity` where
  `type PersonIdentity = { kind: "self" | "linked" | "none"; email: string | null; i18nKey: string }`.
  Tasks 2 and 16 both import it.

- [ ] **Step 1: Write the failing test**

```ts
// apps/web/src/lib/personIdentity.test.ts
import { describe, expect, it } from "vitest";
import { personIdentity } from "./personIdentity";
import type { Person } from "./types";

const base: Person = {
  id: "p1", name: "Ken Hayward", title: null, companyName: null, email: null, phone: null,
  isInternal: true, voiceprintOptOut: false, hasVoiceprint: false, sampleCount: 0,
  linkedUserId: null, isSelf: false, canManageBiometrics: false,
  createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
};

describe("personIdentity", () => {
  it("marks the signed-in user's own record", () => {
    expect(personIdentity({ ...base, linkedUserId: "u1", isSelf: true, email: "ken@example.com" }))
      .toEqual({ kind: "self", email: "ken@example.com", i18nKey: "identitySelf" });
  });

  it("names the account for someone else's linked record", () => {
    expect(personIdentity({ ...base, linkedUserId: "u2", email: "other@acme.com" }))
      .toEqual({ kind: "linked", email: "other@acme.com", i18nKey: "identityLinked" });
  });

  it("says plainly when there is no account", () => {
    expect(personIdentity(base)).toEqual({ kind: "none", email: null, i18nKey: "identityNone" });
  });

  it("still reports linked when the account has no email on file", () => {
    // LinkedUserId is the fact that decides; a missing email must not downgrade it to "no account",
    // because that would tell the user a merge is allowed when the server will refuse it.
    expect(personIdentity({ ...base, linkedUserId: "u2", email: null }))
      .toEqual({ kind: "linked", email: null, i18nKey: "identityLinked" });
  });
});
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `cd apps/web && npx vitest run src/lib/personIdentity.test.ts`
Expected: FAIL - `Failed to resolve import "./personIdentity"`.

- [ ] **Step 3: Write the minimal implementation**

```ts
// apps/web/src/lib/personIdentity.ts
import type { Person } from "./types";

/// Which account a person IS, reduced to one renderable fact.
///
/// Two people with the same name are indistinguishable in a platform-wide directory, and the merge dialog
/// refuses a linked/linked pair without saying which accounts it means. This is the single answer all three
/// surfaces render, so they cannot drift into describing the same person differently.
///
/// `kind` is decided by `linkedUserId` alone, never by the email. For a linked person `Person.Email` is the
/// account's email (PeopleDirectory keeps them in sync), but an account with no email on file must still
/// read as linked - "no Diariz account" there would promise a merge the server will reject.
export type PersonIdentity = {
  kind: "self" | "linked" | "none";
  email: string | null;
  i18nKey: "identitySelf" | "identityLinked" | "identityNone";
};

export function personIdentity(p: Person): PersonIdentity {
  const email = p.email && p.email.trim() !== "" ? p.email : null;
  if (p.linkedUserId == null) return { kind: "none", email: null, i18nKey: "identityNone" };
  return p.isSelf
    ? { kind: "self", email, i18nKey: "identitySelf" }
    : { kind: "linked", email, i18nKey: "identityLinked" };
}
```

- [ ] **Step 4: Run the test and verify it passes**

Run: `cd apps/web && npx vitest run src/lib/personIdentity.test.ts`
Expected: PASS, 4 tests.

- [ ] **Step 5: Add the i18n strings**

In `apps/web/src/locales/en/people.json`, add (plain hyphens only):

```json
"identitySelf": "{{email}} - your account",
"identitySelfNoEmail": "your account",
"identityLinked": "{{email}} - Diariz account",
"identityLinkedNoEmail": "has a Diariz account",
"identityNone": "no Diariz account"
```

- [ ] **Step 6: Write the failing component test**

Add to `apps/web/src/components/PeopleModal.test.tsx`, following the file's existing mock-and-render setup:

```tsx
it("shows each person's account identity in the list", async () => {
  // Two people with the same name: the list is useless unless the rows differ.
  mockPeople([
    { ...personFixture, id: "a", name: "Ken Hayward", linkedUserId: "u1", isSelf: true, email: "ken@example.com" },
    { ...personFixture, id: "b", name: "Ken Hayward", linkedUserId: "u2", isSelf: false, email: "ken@acme.com" },
  ]);
  render(<PeopleModal onClose={() => {}} />, { wrapper });
  expect(await screen.findByText("ken@example.com - your account")).toBeTruthy();
  expect(screen.getByText("ken@acme.com - Diariz account")).toBeTruthy();
});
```

- [ ] **Step 7: Run it and verify it fails**

Run: `cd apps/web && npx vitest run src/components/PeopleModal.test.tsx`
Expected: FAIL - "Unable to find an element with the text: ken@example.com - your account".

- [ ] **Step 8: Render the identity line in the list row**

In `PeopleModal.tsx`, replace the single-line name span inside the row button with a stacked block. The
outer flex child keeps `min-w-0 flex-1`; `truncate` goes on each block-level line, never on an inline span
(on an inline element it sets only `white-space: nowrap` and overflows the row):

```tsx
<span className="min-w-0 flex-1">
  <span className="block truncate text-gray-800 dark:text-gray-100">{p.name}</span>
  <span className="block truncate text-xs text-gray-500 dark:text-gray-400">
    {identityLine(p)}
  </span>
</span>
```

Add this local helper near the top of the component (it also serves Step 9 of Task 2):

```tsx
function identityLine(p: Person): string {
  const id = personIdentity(p);
  if (id.email == null) {
    return id.kind === "self" ? t("people:identitySelfNoEmail")
      : id.kind === "linked" ? t("people:identityLinkedNoEmail")
      : t("people:identityNone");
  }
  return t(`people:${id.i18nKey}`, { email: id.email });
}
```

- [ ] **Step 9: Run the tests and verify they pass**

Run: `cd apps/web && npx vitest run src/components/PeopleModal.test.tsx src/lib/personIdentity.test.ts`
Expected: PASS, no new warnings.

- [ ] **Step 10: Commit**

```bash
git add apps/web/src/lib/personIdentity.ts apps/web/src/lib/personIdentity.test.ts apps/web/src/components/PeopleModal.tsx apps/web/src/components/PeopleModal.test.tsx apps/web/src/locales/en/people.json
git commit -m "feat(people): show each person's account identity in the directory list"
```

---

### Task 2: Duplicates banner + merge dialog identity

**Files:**
- Modify: `apps/web/src/components/PeopleModal.tsx` (duplicates banner, around line 150-165)
- Modify: `apps/web/src/components/MergePeopleDialog.tsx`
- Modify: `apps/web/src/locales/en/people.json`
- Test: `apps/web/src/components/MergePeopleDialog.test.tsx`, `apps/web/src/components/PeopleModal.test.tsx`

**Interfaces:**
- Consumes: `personIdentity` from Task 1.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing merge-dialog test**

```tsx
// apps/web/src/components/MergePeopleDialog.test.tsx
it("names both accounts when it refuses a linked/linked merge", () => {
  const a = { ...personFixture, id: "a", name: "Ken Hayward", linkedUserId: "u1", isSelf: true, email: "ken@example.com" };
  const b = { ...personFixture, id: "b", name: "Ken Hayward", linkedUserId: "u2", isSelf: false, email: "ken@acme.com" };
  render(<MergePeopleDialog people={[a, b]} reason="name" onMerge={async () => {}} onClose={() => {}} />);

  // The refusal is only actionable if it says which two accounts it means.
  expect(screen.getByText("ken@example.com - your account")).toBeTruthy();
  expect(screen.getByText("ken@acme.com - Diariz account")).toBeTruthy();
  // And it must still refuse.
  expect(screen.queryByRole("button", { name: /merge/i })).toBeNull();
});

it("shows the no-account state for an unlinked person", () => {
  const a = { ...personFixture, id: "a", name: "Ken Hayward", linkedUserId: "u1", isSelf: true, email: "ken@example.com" };
  const b = { ...personFixture, id: "b", name: "Ken Hayward", linkedUserId: null, isSelf: false, email: null };
  render(<MergePeopleDialog people={[a, b]} reason="name" onMerge={async () => {}} onClose={() => {}} />);
  expect(screen.getByText("no Diariz account")).toBeTruthy();
});
```

- [ ] **Step 2: Run it and verify it fails**

Run: `cd apps/web && npx vitest run src/components/MergePeopleDialog.test.tsx`
Expected: FAIL - "Unable to find an element with the text: ken@example.com - your account".

- [ ] **Step 3: Render identity under both names in the dialog**

In `MergePeopleDialog.tsx`, inside the keep/delete box, add an identity line under each name. Add the same
`identityLine` helper as Task 1 Step 8 (it needs `t` from this component's `useTranslation`):

```tsx
<p className="font-medium text-gray-900 dark:text-gray-50">
  {t("people:mergeKeeps", { name: target.name })}
</p>
<p className="text-xs text-gray-500 dark:text-gray-400">{identityLine(target)}</p>
<p className="text-gray-500 line-through dark:text-gray-400">
  {t("people:mergeDeletes", { name: source.name })}
</p>
<p className="text-xs text-gray-500 dark:text-gray-400">{identityLine(source)}</p>
```

Leave the `bothLinked` guard and the hidden confirm button exactly as they are - the identity lines are what
make the refusal legible; the rule itself is unchanged.

- [ ] **Step 4: Run it and verify it passes**

Run: `cd apps/web && npx vitest run src/components/MergePeopleDialog.test.tsx`
Expected: PASS.

- [ ] **Step 5: Write the failing duplicates-banner test**

```tsx
// apps/web/src/components/PeopleModal.test.tsx
it("lists each duplicate with its identity rather than joining bare names", async () => {
  mockDuplicates([{ reason: "name", people: [
    { ...personFixture, id: "a", name: "Ken Hayward", linkedUserId: "u1", isSelf: true, email: "ken@example.com" },
    { ...personFixture, id: "b", name: "Ken Hayward", linkedUserId: null, email: null },
  ]}]);
  render(<PeopleModal onClose={() => {}} />, { wrapper });
  // The old banner rendered "Ken Hayward, Ken Hayward" and was undecidable.
  expect(await screen.findByText(/ken@example\.com - your account/)).toBeTruthy();
  expect(screen.getByText(/no Diariz account/)).toBeTruthy();
});
```

- [ ] **Step 6: Run it and verify it fails**

Run: `cd apps/web && npx vitest run src/components/PeopleModal.test.tsx`
Expected: FAIL on the first `findByText`.

- [ ] **Step 7: Replace the joined-names line in the banner**

In `PeopleModal.tsx`, replace `{group.people.map((p) => p.name).join(", ")}` with a nested list:

```tsx
<span className="text-gray-600 dark:text-gray-300">
  {group.reason === "email" ? t("people:duplicatesReasonEmail") : t("people:duplicatesReasonName")}
  <ul className="mt-0.5 list-disc pl-5">
    {group.people.map((p) => (
      <li key={p.id}>
        {p.name} <span className="text-gray-500 dark:text-gray-400">({identityLine(p)})</span>
      </li>
    ))}
  </ul>
</span>
```

- [ ] **Step 8: Run the full web suite and verify it passes**

Run: `cd apps/web && npm test`
Expected: PASS, and no new warnings in the output.

- [ ] **Step 9: Commit**

```bash
git add apps/web/src/components/PeopleModal.tsx apps/web/src/components/PeopleModal.test.tsx apps/web/src/components/MergePeopleDialog.tsx apps/web/src/components/MergePeopleDialog.test.tsx apps/web/src/locales/en/people.json
git commit -m "feat(people): name the accounts in the duplicates banner and merge dialog"
```

---

### Task 3: Worker keeps whisperx's word timings

**Files:**
- Modify: `src/Diariz.Worker/pipeline.py` (`_shape_segments`, around line 182)
- Test: `src/Diariz.Worker/tests/test_pipeline.py`

**Interfaces:**
- Produces: each shaped segment may now carry `"Words": [{"W": str, "S": int, "E": int}]`.
  **The key is absent entirely when there are no usable words**, not `None` - the existing
  `test_converts_seconds_to_ms_and_keeps_pascalcase_keys` compares whole dicts, and this keeps the contract
  additive. Task 4 consumes it.
- Single-letter keys are deliberate: a 60-minute meeting is roughly 10k words and this JSON is stored per
  segment. The rest of the worker contract stays PascalCase, and so does this.

- [ ] **Step 1: Write the failing tests**

Add to `src/Diariz.Worker/tests/test_pipeline.py`:

```python
def test_keeps_aligned_word_timings():
    raw = [{
        "text": "Hello world", "speaker": "SPEAKER_00", "start": 1.2, "end": 2.5,
        "words": [
            {"word": "Hello", "start": 1.2, "end": 1.6},
            {"word": "world", "start": 1.7, "end": 2.5},
        ],
    }]
    assert pipeline._shape_segments(raw)[0]["Words"] == [
        {"W": "Hello", "S": 1200, "E": 1600},
        {"W": "world", "S": 1700, "E": 2500},
    ]


def test_drops_words_missing_timings_rather_than_guessing():
    # whisperx leaves start/end off a word it could not align. A guessed timing would cut the audio in
    # the wrong place, which is exactly what word snapping exists to prevent.
    raw = [{
        "text": "Hello world", "speaker": "S", "start": 0.0, "end": 2.0,
        "words": [
            {"word": "Hello", "start": 0.0, "end": 0.5},
            {"word": "world"},
        ],
    }]
    assert pipeline._shape_segments(raw)[0]["Words"] == [{"W": "Hello", "S": 0, "E": 500}]


def test_omits_the_words_key_when_no_word_is_usable():
    # The 14 languages with no alignment model produce no words at all. The key must be absent, not null,
    # so the segment contract stays exactly what it was before this change.
    raw = [{"text": "Hola", "speaker": "S", "start": 0.0, "end": 1.0, "words": [{"word": "Hola"}]}]
    assert "Words" not in pipeline._shape_segments(raw)[0]
    assert "Words" not in pipeline._shape_segments([{"text": "Hola", "speaker": "S", "start": 0, "end": 1}])[0]
```

- [ ] **Step 2: Run them and verify they fail**

Run: `cd src/Diariz.Worker && python -m pytest tests/test_pipeline.py -v`
Expected: FAIL - `KeyError: 'Words'` on the first two.

- [ ] **Step 3: Write the minimal implementation**

In `pipeline.py`, add above `_shape_segments`:

```python
def _shape_words(raw_words) -> list[dict]:
    """Aligned word timings in the API's contract shape, seconds -> ms. Words whisperx could not align
    (no start/end) are dropped rather than guessed: a guessed boundary would slice the wrong audio, which
    is the whole reason the cut point snaps to a word. Keys are single letters because this is stored per
    segment and a long meeting carries ~10k of them."""
    words = []
    for w in raw_words or []:
        text = (w.get("word") or "").strip()
        start, end = w.get("start"), w.get("end")
        if not text or start is None or end is None:
            continue
        words.append({"W": text, "S": int(round(start * 1000)), "E": int(round(end * 1000))})
    return words
```

and inside `_shape_segments`'s loop, replace the `segments.append({...})` call with:

```python
        shaped = {
            "Speaker": seg.get("speaker", "UNKNOWN"),
            "StartMs": int(round(seg["start"] * 1000)),
            "EndMs": int(round(seg["end"] * 1000)),
            "Text": text,
        }
        words = _shape_words(seg.get("words"))
        if words:
            shaped["Words"] = words
        segments.append(shaped)
```

- [ ] **Step 4: Run the worker suite and verify it passes**

Run: `cd src/Diariz.Worker && python -m pytest -q`
Expected: PASS. The pre-existing `test_converts_seconds_to_ms_and_keeps_pascalcase_keys` must still pass
unchanged - if it fails, the `Words` key is being added when it should be absent.

- [ ] **Step 5: Commit**

```bash
git add src/Diariz.Worker/pipeline.py src/Diariz.Worker/tests/test_pipeline.py
git commit -m "feat(worker): emit aligned word timings on each segment"
```

---

### Task 4: `Segment.WordsJson` + the callback persists words

**Files:**
- Modify: `src/Diariz.Domain/Entities/Segment.cs`
- Modify: `src/Diariz.Domain/DiarizDbContext.cs`
- Create: migration `AddSegmentWords`
- Modify: `src/Diariz.Api/Contracts/WorkerContracts.cs`
- Create: `src/Diariz.Api/Services/SegmentWords.cs`
- Modify: `src/Diariz.Api/Controllers/WorkerCallbackController.cs` (segment creation, around line 78)
- Test: `tests/Diariz.Api.Tests/SegmentWordsTests.cs`, `tests/Diariz.Api.Tests/WorkerCallbackTests.cs`,
  `tests/Diariz.Api.IntegrationTests/SegmentWordsIntegrationTests.cs`

**Interfaces:**
- Consumes: the worker's `"Words"` key from Task 3.
- Produces, all consumed by Tasks 5-8:
  - `record SegmentWord(string W, long S, long E)` in namespace `Diariz.Api.Contracts`.
  - `SegmentResult(string Speaker, long StartMs, long EndMs, string Text, IReadOnlyList<SegmentWord>? Words = null)`.
  - `Segment.WordsJson` (`string?`) on the entity.
  - `static class SegmentWords` in `Diariz.Api.Services`, with
    `string? Serialize(IReadOnlyList<SegmentWord>? words)` and `IReadOnlyList<SegmentWord> Parse(string? json)`.

- [ ] **Step 1: Write the failing serialiser test**

Create `tests/Diariz.Api.Tests/SegmentWordsTests.cs`:

```csharp
using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class SegmentWordsTests
{
    [Fact]
    public void Serialize_ThenParse_RoundTrips()
    {
        var words = new List<SegmentWord> { new("Hello", 1200, 1600), new("world", 1700, 2500) };
        Assert.Equal(words, SegmentWords.Parse(SegmentWords.Serialize(words)));
    }

    [Fact]
    public void Serialize_NullOrEmpty_IsNull()
    {
        // Null means "this segment has no word timings" - the state every pre-existing recording is in,
        // and the one the split endpoint refuses on. An empty array would read as "aligned to nothing".
        Assert.Null(SegmentWords.Serialize(null));
        Assert.Null(SegmentWords.Serialize(new List<SegmentWord>()));
    }

    [Fact]
    public void Parse_NullOrGarbage_IsEmpty()
    {
        // A column value we cannot read must not throw a 500 into a transcript render.
        Assert.Empty(SegmentWords.Parse(null));
        Assert.Empty(SegmentWords.Parse("not json"));
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        // The app's global serializer is camelCase. If reading ever depended on which casing wrote the
        // row, a mismatch would return an empty list and read as "this segment has no words".
        var lower = "[{\"w\":\"Hi\",\"s\":1,\"e\":2}]";
        var upper = "[{\"W\":\"Hi\",\"S\":1,\"E\":2}]";
        Assert.Equal(new List<SegmentWord> { new("Hi", 1, 2) }, SegmentWords.Parse(lower));
        Assert.Equal(new List<SegmentWord> { new("Hi", 1, 2) }, SegmentWords.Parse(upper));
    }
}
```

- [ ] **Step 2: Run it and verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SegmentWordsTests"`
Expected: FAIL to compile - `SegmentWords` and `SegmentWord` do not exist.

- [ ] **Step 3: Add the contract and the serialiser**

In `src/Diariz.Api/Contracts/WorkerContracts.cs`, above `SegmentResult`:

```csharp
/// <summary>One aligned word inside a segment. Single-letter members are deliberate: this is stored as
/// jsonb on every segment and a long meeting carries roughly 10k of them. <c>S</c> and <c>E</c> are ms
/// from the start of the recording, matching <see cref="SegmentResult"/>.</summary>
public record SegmentWord(string W, long S, long E);
```

and widen `SegmentResult`:

```csharp
public record SegmentResult(
    string Speaker,
    long StartMs,
    long EndMs,
    string Text,
    /// <summary>Aligned word timings, or null when whisperx produced none (no alignment model for the
    /// language, or a recording transcribed before this existed). A segment without these cannot be
    /// split.</summary>
    IReadOnlyList<SegmentWord>? Words = null);
```

Create `src/Diariz.Api/Services/SegmentWords.cs`:

```csharp
using System.Text.Json;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>Reads and writes the <c>Segment.WordsJson</c> column. One place, one options object: the app's
/// global serializer is camelCase, so a hand-rolled Serialize/Deserialize pair written elsewhere could
/// emit one casing and read another, and silently return nothing at all.</summary>
public static class SegmentWords
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <returns>Null for a null or empty list - null is the column's "no word timings" state.</returns>
    public static string? Serialize(IReadOnlyList<SegmentWord>? words) =>
        words is null || words.Count == 0 ? null : JsonSerializer.Serialize(words, Options);

    /// <summary>Never throws: an unreadable column value reads as "no words", which degrades to a segment
    /// that cannot be split rather than a 500 in the middle of a transcript.</summary>
    public static IReadOnlyList<SegmentWord> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<SegmentWord>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
```

- [ ] **Step 4: Run it and verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SegmentWordsTests"`
Expected: PASS, 4 tests.

- [ ] **Step 5: Add the entity property and the model mapping**

In `src/Diariz.Domain/Entities/Segment.cs`:

```csharp
    /// <summary>Aligned word timings as JSON, or null when there are none - every recording transcribed
    /// before this existed, and any language with no alignment model. Read and written through
    /// <c>SegmentWords</c>; a segment with null here cannot be split.</summary>
    public string? WordsJson { get; set; }
```

In `DiarizDbContext.OnModelCreating`, **inside** the same `isNpgsql` guard that already configures
`Segment.Embedding` as `vector(768)` (around line 515):

```csharp
                e.Property(s => s.WordsJson).HasColumnType("jsonb");
```

- [ ] **Step 6: Create the migration**

```bash
dotnet ef migrations add AddSegmentWords --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Open the generated file and confirm it is a single nullable `AddColumn<string>` on `Segments` with type
`jsonb`. Anything else means the model change was wider than intended - stop and re-read the diff rather
than editing the migration to match.

- [ ] **Step 7: Write the failing callback test**

Add to `tests/Diariz.Api.Tests/WorkerCallbackTests.cs`, following the file's existing seeding and
controller-construction pattern:

```csharp
[Fact]
public async Task Result_PersistsWordTimingsOnEachSegment()
{
    await using var db = TestDb.Create();
    var (controller, transcriptionId) = await SeedAsync(db);

    await controller.Result(new TranscriptionResult(transcriptionId, "en", [
        new SegmentResult("SPEAKER_00", 1200, 2500, "Hello world",
            [new SegmentWord("Hello", 1200, 1600), new SegmentWord("world", 1700, 2500)]),
        new SegmentResult("SPEAKER_01", 3000, 3500, "Yes"),
    ]));

    var segs = db.Segments.OrderBy(s => s.Ordinal).ToList();
    Assert.Equal(2, SegmentWords.Parse(segs[0].WordsJson).Count);
    // A segment the worker sent no words for stays null, not "[]" - the split endpoint keys off null.
    Assert.Null(segs[1].WordsJson);
}
```

- [ ] **Step 8: Run it and verify it fails**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~Result_PersistsWordTimings"`
Expected: FAIL - `Assert.Equal() Failure: Expected: 2, Actual: 0`.

- [ ] **Step 9: Persist the words in the callback**

In `WorkerCallbackController.Result`, inside the `foreach (var s in body.Segments)` loop, add one line to
the `new Segment { ... }` initialiser:

```csharp
                WordsJson = SegmentWords.Serialize(s.Words),
```

- [ ] **Step 10: Run it and verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WorkerCallback"`
Expected: PASS, including the pre-existing tests in that class.

- [ ] **Step 11: Write the integration test for the jsonb round-trip**

Create `tests/Diariz.Api.IntegrationTests/SegmentWordsIntegrationTests.cs`, following the seeding helpers
the other files in that project use:

```csharp
[Collection("integration")]
public class SegmentWordsIntegrationTests(ContainersFixture fx)
{
    [Fact]
    public async Task WordsJson_SurvivesRealPostgresRoundTrip()
    {
        // Postgres reformats jsonb on write - key order and whitespace are not preserved - so byte
        // comparison of the column text never matches. The in-memory provider stores plain text and hides
        // this entirely. Compare the parsed values, not the strings.
        var written = new List<SegmentWord> { new("Hello", 1200, 1600), new("world", 1700, 2500) };

        Guid segmentId;
        await using (var db = fx.CreateDbContext())
            segmentId = await SeedSegmentWithWordsAsync(db, SegmentWords.Serialize(written));

        await using var read = fx.CreateDbContext();
        var stored = await read.Segments.Where(s => s.Id == segmentId)
            .Select(s => s.WordsJson).SingleAsync();
        Assert.Equal(written, SegmentWords.Parse(stored));
    }
}
```

`SeedSegmentWithWordsAsync` creates a user, recording, transcription and one segment with the given
`WordsJson`, using unique ids - the integration collection shares one database, so tests isolate by id and
not by a fresh database per test.

- [ ] **Step 12: Run it and verify it passes**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~SegmentWords"`
Expected: PASS. (Needs Docker.)

- [ ] **Step 13: Commit**

```bash
git add src/Diariz.Domain/Entities/Segment.cs src/Diariz.Domain/DiarizDbContext.cs src/Diariz.Domain/Migrations src/Diariz.Api/Contracts/WorkerContracts.cs src/Diariz.Api/Services/SegmentWords.cs src/Diariz.Api/Controllers/WorkerCallbackController.cs tests/Diariz.Api.Tests/SegmentWordsTests.cs tests/Diariz.Api.Tests/WorkerCallbackTests.cs tests/Diariz.Api.IntegrationTests/SegmentWordsIntegrationTests.cs
git commit -m "feat(api): persist per-segment word timings from the worker"
```

---

### Task 5: Auto-merge carries word timings through

**Files:**
- Modify: `src/Diariz.Api/Services/SegmentMerger.cs`
- Modify: `src/Diariz.Api/Services/TranscriptSegmentMerge.cs`
- Test: `tests/Diariz.Api.Tests/SegmentMergerTests.cs`, `tests/Diariz.Api.Tests/TranscriptSegmentMergeTests.cs`

**Why this task exists.** `TranscriptSegmentMerge` deletes every segment and rebuilds them. Auto-merge runs
on every transcription for users who turned it on (0.229.0). Without this, merging silently destroys
splittability for exactly the users most likely to want it, and no existing test would notice.

**Interfaces:**
- Consumes: `SegmentWord`, `SegmentWords` from Task 4.
- Produces: `SegmentMerger.Part` gains a trailing `IReadOnlyList<SegmentWord>? Words = null` member.
  Merged output concatenates the parts' word lists, or is **null if any part in the run has null**.

- [ ] **Step 1: Write the failing merger tests**

Add to `tests/Diariz.Api.Tests/SegmentMergerTests.cs`:

```csharp
[Fact]
public void Merge_ConcatenatesWordsOfMergedParts()
{
    var merged = SegmentMerger.Merge([
        new SegmentMerger.Part("k", "S0", 0, 1000, "Hello", Words: [new SegmentWord("Hello", 0, 1000)]),
        new SegmentMerger.Part("k", "S0", 1100, 2000, "world", Words: [new SegmentWord("world", 1100, 2000)]),
    ]);

    Assert.Single(merged);
    Assert.Equal([new SegmentWord("Hello", 0, 1000), new SegmentWord("world", 1100, 2000)], merged[0].Words);
}

[Fact]
public void Merge_DropsWordsWhenAnyPartInTheRunHasNone()
{
    // One unaligned part makes the block's word list an incomplete description of its own text. Splitting
    // on that would cut at a boundary that does not correspond to what is written, so the whole block
    // becomes unsplittable instead - visibly, via a null.
    var merged = SegmentMerger.Merge([
        new SegmentMerger.Part("k", "S0", 0, 1000, "Hello", Words: [new SegmentWord("Hello", 0, 1000)]),
        new SegmentMerger.Part("k", "S0", 1100, 2000, "world", Words: null),
    ]);

    Assert.Single(merged);
    Assert.Null(merged[0].Words);
}

[Fact]
public void Merge_KeepsEachUnmergedPartsOwnWords()
{
    var merged = SegmentMerger.Merge([
        new SegmentMerger.Part("a", "S0", 0, 1000, "Hello", Words: [new SegmentWord("Hello", 0, 1000)]),
        new SegmentMerger.Part("b", "S1", 1100, 2000, "world", Words: [new SegmentWord("world", 1100, 2000)]),
    ]);

    Assert.Equal(2, merged.Count);
    Assert.Equal([new SegmentWord("Hello", 0, 1000)], merged[0].Words);
    Assert.Equal([new SegmentWord("world", 1100, 2000)], merged[1].Words);
}
```

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SegmentMergerTests"`
Expected: FAIL to compile - `Part` has no `Words` member.

- [ ] **Step 3: Widen `Part` and concatenate in `Merge`**

In `SegmentMerger.cs`, add a member to the record with its own doc line:

```csharp
    /// <param name="Words">Aligned word timings for this part, or null when it has none. A merged block
    /// concatenates its parts' words, and is null when <em>any</em> part in the run is null - a partial
    /// word list would describe the block's text incompletely and make a split land in the wrong place.</param>
    public record Part(string SpeakerKey, string SpeakerLabel, long StartMs, long EndMs, string Text,
        bool BreakBefore = false, IReadOnlyList<SegmentWord>? Words = null);
```

In the merging branch of `Merge`, alongside the existing `EndMs`/`Text` update:

```csharp
                var words = prev.Words is null || p.Words is null
                    ? null
                    : (IReadOnlyList<SegmentWord>)[.. prev.Words, .. p.Words];
                result[^1] = prev with { EndMs = p.EndMs, Text = text, Words = words };
```

- [ ] **Step 4: Run them and verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SegmentMergerTests"`
Expected: PASS, including the pre-existing tests in that class.

- [ ] **Step 5: Write the failing `TranscriptSegmentMerge` tests**

Add to `tests/Diariz.Api.Tests/TranscriptSegmentMergeTests.cs`:

```csharp
[Fact]
public async Task ApplyAsync_CarriesWordsOntoTheMergedSegment()
{
    await using var db = TestDb.Create();
    var (recordingId, transcriptionId) = await SeedTwoAdjacentSameSpeakerSegmentsAsync(db,
        firstWords: [new SegmentWord("Hello", 0, 1000)],
        secondWords: [new SegmentWord("world", 1100, 2000)]);

    Assert.True(await TranscriptSegmentMerge.ApplyAsync(db, recordingId, transcriptionId));
    await db.SaveChangesAsync();

    var seg = Assert.Single(db.Segments.Where(s => s.TranscriptionId == transcriptionId));
    Assert.Equal(2, SegmentWords.Parse(seg.WordsJson).Count);
}

[Fact]
public async Task ApplyAsync_DropsWordsForASegmentWhoseTextWasEdited()
{
    // Merge writes EffectiveText into a fresh Original, so a revised segment's merged text is the user's
    // words while the timings describe the model's. Carrying them would let a split cut at a boundary
    // that is not in the text being cut.
    await using var db = TestDb.Create();
    var (recordingId, transcriptionId) = await SeedTwoAdjacentSameSpeakerSegmentsAsync(db,
        firstWords: [new SegmentWord("Hello", 0, 1000)],
        secondWords: [new SegmentWord("world", 1100, 2000)],
        secondRevised: "entirely different text");

    Assert.True(await TranscriptSegmentMerge.ApplyAsync(db, recordingId, transcriptionId));
    await db.SaveChangesAsync();

    var seg = Assert.Single(db.Segments.Where(s => s.TranscriptionId == transcriptionId));
    Assert.Null(seg.WordsJson);
}
```

- [ ] **Step 6: Run them and verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~TranscriptSegmentMerge"`
Expected: FAIL - `Assert.Equal() Failure: Expected: 2, Actual: 0`.

- [ ] **Step 7: Carry words through the rebuild**

In `TranscriptSegmentMerge.ApplyAsync`, when building each `SegmentMerger.Part`, pass the words - and pass
null for a revised segment, because the merged `Original` will be that revision:

```csharp
        var merged = SegmentMerger.Merge(segments
            .Select((s, i) => new SegmentMerger.Part(
                KeyFor(s.SpeakerLabel), s.SpeakerLabel, s.StartMs, s.EndMs, s.EffectiveText,
                breakBefore.Contains(i),
                // A revision replaces the text the timings describe, so its words no longer index into
                // what will be stored. Null here makes the merged block unsplittable rather than wrongly
                // splittable.
                s.Revised is null ? SegmentWords.Parse(s.WordsJson) is { Count: > 0 } w ? w : null : null))
            .ToList());
```

and on the `new Segment { ... }` it adds:

```csharp
                WordsJson = SegmentWords.Serialize(p.Words),
```

- [ ] **Step 8: Run the whole unit suite and verify it passes**

Run: `dotnet test tests/Diariz.Api.Tests`
Expected: PASS, no warnings.

- [ ] **Step 9: Commit**

```bash
git add src/Diariz.Api/Services/SegmentMerger.cs src/Diariz.Api/Services/TranscriptSegmentMerge.cs tests/Diariz.Api.Tests/SegmentMergerTests.cs tests/Diariz.Api.Tests/TranscriptSegmentMergeTests.cs
git commit -m "fix(api): carry word timings through a same-speaker segment merge"
```

---

### Task 6: `hasWords` on the segment DTO + the words endpoint

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (`SegmentDto`, line 265)
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs` (every `new SegmentDto(...)` site)
- Modify: `apps/web/src/lib/types.ts`, `apps/web/src/lib/api.ts`
- Test: `tests/Diariz.Api.Tests/RecordingSegmentWordsTests.cs`

**Interfaces:**
- Consumes: `Segment.WordsJson`, `SegmentWords` from Task 4.
- Produces:
  - `SegmentDto(..., string Original, string? Revised = null, bool HasWords = false)` - a trailing
    defaulted member, so the existing construction sites that do not set it still compile.
  - `GET /api/recordings/{id}/segments/{segmentId}/words` returning `IReadOnlyList<SegmentWord>`.
  - Web: `SegmentDto.hasWords: boolean`, `api.getSegmentWords(id, segmentId): Promise<SegmentWord[]>`,
    `interface SegmentWord { w: string; s: number; e: number }`.
  Tasks 8 and 10 consume these.

**Why words are not on the DTO.** Roughly 10k words per recording would balloon `GET /api/recordings/{id}`,
which feeds exports, MCP, webhooks and the n8n node. The flag tells the UI whether to offer the affordance;
the words themselves are fetched for one segment when the user actually splits it.

- [ ] **Step 1: Write the failing tests**

Create `tests/Diariz.Api.Tests/RecordingSegmentWordsTests.cs`:

```csharp
[Fact]
public async Task Get_ReportsHasWordsPerSegment()
{
    await using var db = TestDb.Create();
    var (controller, recordingId) = await SeedAsync(db,
        withWords: [new SegmentWord("Hello", 0, 500)], withoutWords: true);

    var rec = (await controller.Get(recordingId)).Value!;
    var segs = rec.Current!.Segments;
    Assert.True(segs[0].HasWords);
    Assert.False(segs[1].HasWords);
}

[Fact]
public async Task Words_ReturnsTheSegmentsWords()
{
    await using var db = TestDb.Create();
    var (controller, recordingId, segmentId) = await SeedOneSegmentAsync(db,
        [new SegmentWord("Hello", 0, 500), new SegmentWord("world", 600, 1000)]);

    var result = Assert.IsType<OkObjectResult>(await controller.Words(recordingId, segmentId));
    Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<SegmentWord>>(result.Value).Count);
}

[Fact]
public async Task Words_ForAnotherUsersRecording_IsNotFound()
{
    // Ownership, not authorisation-by-obscurity: every recording endpoint filters by the JWT's UserId,
    // and a 404 rather than a 403 keeps it from confirming the id exists.
    await using var db = TestDb.Create();
    var (controller, recordingId, segmentId) = await SeedOneSegmentAsync(db, [new SegmentWord("Hi", 0, 1)],
        ownerId: Guid.NewGuid());

    Assert.IsType<NotFoundResult>(await controller.Words(recordingId, segmentId));
}
```

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RecordingSegmentWords"`
Expected: FAIL to compile - `SegmentDto` has no `HasWords`, `RecordingsController` has no `Words`.

- [ ] **Step 3: Widen the DTO**

In `ApiDtos.cs`:

```csharp
public record SegmentDto(
    Guid Id, string Speaker, string SpeakerDisplay, long StartMs, long EndMs,
    string Original, string? Revised = null,
    /// <summary>True when this segment has aligned word timings and can therefore be split. False for
    /// anything transcribed before word timings existed, for a language with no alignment model, and for
    /// a merged block that swallowed an edited segment. The words themselves are fetched per segment.
    /// </summary>
    bool HasWords = false)
```

- [ ] **Step 4: Set it at every construction site**

In `RecordingsController.cs`, at each `new SegmentDto(...)` (lines ~223, ~707, ~1992 and the email/export
site around ~1745), append `s.WordsJson != null` as the final argument. Grep to be sure none are missed -
the flag defaulting to false means a missed site fails silently as "cannot split", not as a compile error:

```bash
grep -n "new SegmentDto(" src/Diariz.Api/Controllers/RecordingsController.cs
```

Sites that project in the database (`Select` inside a query) can compare `s.WordsJson != null` directly;
sites that already materialise the entity can too.

- [ ] **Step 5: Add the words endpoint**

In `RecordingsController.cs`, beside `UpdateSegment` (around line 912):

```csharp
    [HttpGet("{id:guid}/segments/{segmentId:guid}/words")]
    [EndpointSummary("Get a segment's word timings")]
    [EndpointDescription(
        "The aligned word timings for one segment, used to split it at an exact word boundary. Returned " +
        "per segment rather than on the transcript, because a long meeting carries roughly 10k words and " +
        "they would dominate the recording payload.\n\n" +
        "Empty when the segment has none - a recording transcribed before word timings existed, or a " +
        "language with no alignment model. Such a segment cannot be split; re-transcribe it first.")]
    public async Task<IActionResult> Words(Guid id, Guid segmentId)
    {
        var owned = await _db.Recordings.AnyAsync(r => r.Id == id && r.UserId == UserId);
        if (!owned) return NotFound();

        var seg = await _db.Segments.Include(s => s.Transcription)
            .FirstOrDefaultAsync(s => s.Id == segmentId);
        if (seg?.Transcription is null || seg.Transcription.RecordingId != id) return NotFound();

        return Ok(SegmentWords.Parse(seg.WordsJson));
    }
```

- [ ] **Step 6: Run them and verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RecordingSegmentWords"`
Expected: PASS, 3 tests.

- [ ] **Step 7: Add the web types and client method**

In `apps/web/src/lib/types.ts`, add `hasWords: boolean;` to `SegmentDto` and:

```ts
/// One aligned word inside a segment. Short keys because the server stores ~10k of these per recording.
export interface SegmentWord {
  w: string;
  s: number;
  e: number;
}
```

In `apps/web/src/lib/api.ts`, beside `updateSegment`:

```ts
  /// Word timings for one segment, fetched only when the user opens the split editor.
  async getSegmentWords(id: string, segmentId: string): Promise<SegmentWord[]> {
    const { data } = await http.get<SegmentWord[]>(`/api/recordings/${id}/segments/${segmentId}/words`);
    return data;
  },
```

- [ ] **Step 8: Regenerate the OpenAPI snapshot and the n8n client**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
```

This test **rewrites its own snapshot**: run 1 fails, run 2 passes with no code change. Commit the
regenerated file. Then:

```bash
cd integrations/n8n-nodes-diariz && npm run generate
```

`generated/index.ts` does **not** self-heal. Skipping this reds the "n8n community node" check, which has
stayed broken across merged PRs before.

- [ ] **Step 9: Build the whole solution and run both web and API suites**

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
cd apps/web && npm run build && npm test
```

Expected: all PASS. `dotnet build Diariz.slnx` (not just the unit project) is what catches an integration
or CodeQL compile break from the widened DTO.

- [ ] **Step 10: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/RecordingsController.cs apps/web/src/lib/types.ts apps/web/src/lib/api.ts tests/Diariz.Api.Tests integrations/n8n-nodes-diariz
git commit -m "feat(api): expose hasWords per segment and a per-segment words endpoint"
```

---

### Task 7: `TranscriptSegmentSplit` pure helper

**Files:**
- Create: `src/Diariz.Api/Services/TranscriptSegmentSplit.cs`
- Test: `tests/Diariz.Api.Tests/TranscriptSegmentSplitTests.cs`

**Interfaces:**
- Consumes: `SegmentWord` from Task 4.
- Produces, consumed by Task 8:

```csharp
public record Half(string Text, long StartMs, long EndMs, IReadOnlyList<SegmentWord> Words);
public record Result(Half Left, Half Right);
public static Result? Split(long startMs, long endMs, string original,
                            IReadOnlyList<SegmentWord> words, int wordIndex);
public static int? TextCutOffset(string original, IReadOnlyList<SegmentWord> words, int wordIndex);
```

**The non-obvious part.** Each half's text is cut out of the segment's own `Original` string, not rebuilt by
joining words with spaces. A merged block contains newlines between the parts it swallowed, and rejoining
would silently reflow it to one line. The cut offset is found by walking `Original` and locating each word
in order; if any word is not found (which would mean the words no longer describe the text), it falls back
to joining, which is worse but never wrong about which words went where.

- [ ] **Step 1: Write the failing tests**

Create `tests/Diariz.Api.Tests/TranscriptSegmentSplitTests.cs`:

```csharp
using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class TranscriptSegmentSplitTests
{
    private static readonly IReadOnlyList<SegmentWord> Words =
    [
        new("Hello", 1000, 1400),
        new("world", 1500, 1900),
        new("again", 2100, 2500),
    ];

    [Fact]
    public void Split_DividesTextAndWordsAtTheIndex()
    {
        var r = TranscriptSegmentSplit.Split(900, 2600, "Hello world again", Words, 2)!;

        Assert.Equal("Hello world", r.Left.Text);
        Assert.Equal("again", r.Right.Text);
        Assert.Equal(2, r.Left.Words.Count);
        Assert.Single(r.Right.Words);
    }

    [Fact]
    public void Split_LeavesTheInterWordGapInNeitherHalf()
    {
        // The silence between two speakers' words belongs to neither of them. Including it would put a
        // slice of the interloper's audio into whichever half gets trained on.
        var r = TranscriptSegmentSplit.Split(900, 2600, "Hello world again", Words, 2)!;

        Assert.Equal(1900, r.Left.EndMs);   // end of "world"
        Assert.Equal(2100, r.Right.StartMs); // start of "again"
    }

    [Fact]
    public void Split_KeepsTheOuterBoundsOfTheOriginalSegment()
    {
        // The left half must still start where the row started, or the transcript's timestamps shift
        // under the user for a segment they only divided.
        var r = TranscriptSegmentSplit.Split(900, 2600, "Hello world again", Words, 2)!;

        Assert.Equal(900, r.Left.StartMs);
        Assert.Equal(2600, r.Right.EndMs);
    }

    [Fact]
    public void Split_PreservesNewlinesInsideAMergedBlock()
    {
        // A merged block joins its parts with a line break. Rebuilding text by joining words with spaces
        // would silently reflow it to one line - a visible edit the user never asked for.
        var words = new List<SegmentWord> { new("Hello", 0, 500), new("world", 600, 900), new("again", 1000, 1400) };
        var r = TranscriptSegmentSplit.Split(0, 1500, "Hello\nworld again", words, 2)!;

        Assert.Equal("Hello\nworld", r.Left.Text);
        Assert.Equal("again", r.Right.Text);
    }

    [Fact]
    public void Split_PreservesPunctuationAttachedToWords()
    {
        var words = new List<SegmentWord> { new("Hello,", 0, 500), new("world.", 600, 900) };
        var r = TranscriptSegmentSplit.Split(0, 1000, "Hello, world.", words, 1)!;

        Assert.Equal("Hello,", r.Left.Text);
        Assert.Equal("world.", r.Right.Text);
    }

    [Fact]
    public void Split_FallsBackToJoiningWhenTheWordsNoLongerMatchTheText()
    {
        // Defensive: if the text and the words have drifted apart, dividing the words correctly still
        // beats refusing, and the text is rebuilt from the words that actually went to each side.
        var r = TranscriptSegmentSplit.Split(0, 1000, "completely unrelated text", Words, 2)!;

        Assert.Equal("Hello world", r.Left.Text);
        Assert.Equal("again", r.Right.Text);
    }

    [Theory]
    [InlineData(0)]   // nothing on the left
    [InlineData(3)]   // nothing on the right
    [InlineData(-1)]
    [InlineData(99)]
    public void Split_OutOfRangeIndex_IsNull(int index) =>
        Assert.Null(TranscriptSegmentSplit.Split(900, 2600, "Hello world again", Words, index));

    [Fact]
    public void Split_WithFewerThanTwoWords_IsNull() =>
        Assert.Null(TranscriptSegmentSplit.Split(0, 100, "Hi", [new SegmentWord("Hi", 0, 100)], 1));

    [Fact]
    public void TextCutOffset_IsTheStartOfTheWordAtTheIndex()
    {
        Assert.Equal(6, TranscriptSegmentSplit.TextCutOffset("Hello world again", Words, 1));
        Assert.Equal(12, TranscriptSegmentSplit.TextCutOffset("Hello world again", Words, 2));
    }

    [Fact]
    public void TextCutOffset_WhenAWordIsAbsent_IsNull() =>
        Assert.Null(TranscriptSegmentSplit.TextCutOffset("Hello there again", Words, 2));

    [Fact]
    public void TextCutOffset_MatchesInOrderNotAnywhere()
    {
        // "again" appears before "world" in this text. Searching from a moving cursor keeps the words in
        // their real order; a naive IndexOf per word would cut at the earlier, wrong occurrence.
        Assert.Null(TranscriptSegmentSplit.TextCutOffset("Hello again world", Words, 2));
    }
}
```

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~TranscriptSegmentSplit"`
Expected: FAIL to compile - `TranscriptSegmentSplit` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Diariz.Api/Services/TranscriptSegmentSplit.cs`:

```csharp
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>Pure (EF-free) division of one transcript segment into two at a word boundary, so a block that
/// contains a second voice can be separated and the interloper reassigned. Mirrors
/// <see cref="TranscriptSegmentMerge"/>'s split of pure arithmetic from persistence.
///
/// <para>The cut snaps to a word because the halves feed voiceprint training: an estimated boundary would
/// hand a slice of the wrong person's audio to whichever half gets enrolled.</para></summary>
public static class TranscriptSegmentSplit
{
    /// <param name="Words">The words that fell on this side. Never null - a half of a splittable segment
    /// always has at least one word.</param>
    public record Half(string Text, long StartMs, long EndMs, IReadOnlyList<SegmentWord> Words);

    public record Result(Half Left, Half Right);

    /// <summary>Split before <paramref name="wordIndex"/>.</summary>
    /// <param name="startMs">The segment's own start, kept as the left half's start so the transcript's
    /// timestamps do not shift under a user who only divided a row.</param>
    /// <param name="endMs">The segment's own end, kept as the right half's end for the same reason.</param>
    /// <returns>Null when the index would leave a half empty, or when there are fewer than two words -
    /// there is nothing to divide.</returns>
    public static Result? Split(long startMs, long endMs, string original,
        IReadOnlyList<SegmentWord> words, int wordIndex)
    {
        if (words.Count < 2 || wordIndex < 1 || wordIndex >= words.Count) return null;

        var leftWords = words.Take(wordIndex).ToList();
        var rightWords = words.Skip(wordIndex).ToList();

        var cut = TextCutOffset(original, words, wordIndex);
        var (leftText, rightText) = cut is int at
            ? (original[..at].TrimEnd(), original[at..].TrimStart())
            // The words and the text have drifted apart. Dividing the words correctly still beats
            // refusing; rebuild each side's text from the words that actually went to it.
            : (string.Join(' ', leftWords.Select(w => w.W)), string.Join(' ', rightWords.Select(w => w.W)));

        return new Result(
            new Half(leftText, startMs, leftWords[^1].E, leftWords),
            new Half(rightText, rightWords[0].S, endMs, rightWords));
    }

    /// <summary>Character offset in <paramref name="original"/> where the word at
    /// <paramref name="wordIndex"/> begins, found by walking the words in order from a moving cursor.
    /// Searching each word independently would match an earlier, unrelated occurrence.</summary>
    /// <returns>Null when any word up to the index is not found in order, which means the words no longer
    /// describe this text.</returns>
    public static int? TextCutOffset(string original, IReadOnlyList<SegmentWord> words, int wordIndex)
    {
        if (wordIndex < 1 || wordIndex >= words.Count) return null;

        var cursor = 0;
        for (var i = 0; i <= wordIndex; i++)
        {
            var at = original.IndexOf(words[i].W, cursor, StringComparison.Ordinal);
            if (at < 0) return null;
            if (i == wordIndex) return at;
            cursor = at + words[i].W.Length;
        }
        return null;
    }
}
```

- [ ] **Step 4: Run them and verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~TranscriptSegmentSplit"`
Expected: PASS, 14 test cases.

- [ ] **Step 5: Mutation-verify one assertion**

Change `rightWords[0].S` to `leftWords[^1].E` in the implementation, re-run, and confirm
`Split_LeavesTheInterWordGapInNeitherHalf` fails with `Expected: 2100, Actual: 1900`. **Edit the file back
in place** rather than restoring a copy - a restored file keeps its old timestamp and MSBuild will skip the
rebuild, leaving you testing the mutated binary. Re-run and confirm green.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/TranscriptSegmentSplit.cs tests/Diariz.Api.Tests/TranscriptSegmentSplitTests.cs
git commit -m "feat(api): pure segment-split arithmetic snapped to word boundaries"
```

---

### Task 8: The split endpoint

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs` (beside `UpdateSegment`)
- Modify: `apps/web/src/lib/api.ts`
- Test: `tests/Diariz.Api.Tests/RecordingSegmentSplitTests.cs`

**Interfaces:**
- Consumes: `TranscriptSegmentSplit` (Task 7), `SegmentWords` (Task 4).
- Produces:
  - `record SplitSegmentRequest(int WordIndex, bool DiscardRevision = false)`.
  - `POST /api/recordings/{id}/segments/{segmentId}/split` returning `204`, `404`, or `409`.
  - Web: `api.splitSegment(id, segmentId, wordIndex, discardRevision)`.
  Task 10 consumes the web method.

- [ ] **Step 1: Write the failing tests**

Create `tests/Diariz.Api.Tests/RecordingSegmentSplitTests.cs`:

```csharp
[Fact]
public async Task Split_ReplacesTheSegmentWithTwoAndRenumbers()
{
    await using var db = TestDb.Create();
    // Three segments; the middle one splits, so the third must renumber from 2 to 3.
    var (controller, recordingId, segmentId) = await SeedThreeWithMiddleSplittableAsync(db);

    Assert.IsType<NoContentResult>(await controller.SplitSegment(recordingId, segmentId,
        new SplitSegmentRequest(WordIndex: 2)));

    var segs = db.Segments.OrderBy(s => s.Ordinal).ToList();
    Assert.Equal(4, segs.Count);
    Assert.Equal([0, 1, 2, 3], segs.Select(s => s.Ordinal));
    Assert.Equal("Hello world", segs[1].Original);
    Assert.Equal("again", segs[2].Original);
}

[Fact]
public async Task Split_GivesBothHalvesTheSameSpeakerAndTheirOwnWords()
{
    await using var db = TestDb.Create();
    var (controller, recordingId, segmentId) = await SeedOneSplittableAsync(db);

    await controller.SplitSegment(recordingId, segmentId, new SplitSegmentRequest(2));

    var segs = db.Segments.OrderBy(s => s.Ordinal).ToList();
    Assert.Equal(segs[0].SpeakerLabel, segs[1].SpeakerLabel);
    Assert.Equal(2, SegmentWords.Parse(segs[0].WordsJson).Count);
    Assert.Single(SegmentWords.Parse(segs[1].WordsJson));
}

[Fact]
public async Task Split_WithoutWords_IsConflict()
{
    await using var db = TestDb.Create();
    var (controller, recordingId, segmentId) = await SeedOneSplittableAsync(db, words: null);

    Assert.IsType<ConflictObjectResult>(
        await controller.SplitSegment(recordingId, segmentId, new SplitSegmentRequest(1)));
}

[Fact]
public async Task Split_OfARevisedSegment_WithoutDiscardFlag_IsConflictAndChangesNothing()
{
    // The API must not throw away a user's correction on an unconfirmed call. The web asks first and
    // then sends the flag; a client that forgets gets a refusal, not silent data loss.
    await using var db = TestDb.Create();
    var (controller, recordingId, segmentId) = await SeedOneSplittableAsync(db, revised: "my correction");

    Assert.IsType<ConflictObjectResult>(
        await controller.SplitSegment(recordingId, segmentId, new SplitSegmentRequest(2)));
    Assert.Single(db.Segments);
    Assert.Equal("my correction", db.Segments.Single().Revised);
}

[Fact]
public async Task Split_OfARevisedSegment_WithDiscardFlag_DropsTheRevisionFromBothHalves()
{
    await using var db = TestDb.Create();
    var (controller, recordingId, segmentId) = await SeedOneSplittableAsync(db, revised: "my correction");

    Assert.IsType<NoContentResult>(await controller.SplitSegment(recordingId, segmentId,
        new SplitSegmentRequest(2, DiscardRevision: true)));

    var segs = db.Segments.OrderBy(s => s.Ordinal).ToList();
    Assert.Equal(2, segs.Count);
    Assert.All(segs, s => Assert.Null(s.Revised));
    // Both halves take their text from the model's Original, which is what the words describe.
    Assert.Equal("Hello world", segs[0].Original);
}

[Fact]
public async Task Split_AtAnIndexThatWouldLeaveAHalfEmpty_IsConflict()
{
    await using var db = TestDb.Create();
    var (controller, recordingId, segmentId) = await SeedOneSplittableAsync(db);

    Assert.IsType<ConflictObjectResult>(
        await controller.SplitSegment(recordingId, segmentId, new SplitSegmentRequest(0)));
    Assert.Single(db.Segments);
}

[Fact]
public async Task Split_ForAnotherUsersRecording_IsNotFound()
{
    await using var db = TestDb.Create();
    var (controller, recordingId, segmentId) = await SeedOneSplittableAsync(db, ownerId: Guid.NewGuid());

    Assert.IsType<NotFoundResult>(
        await controller.SplitSegment(recordingId, segmentId, new SplitSegmentRequest(2)));
}
```

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RecordingSegmentSplit"`
Expected: FAIL to compile - `SplitSegmentRequest` and `SplitSegment` do not exist.

- [ ] **Step 3: Add the request DTO**

In `ApiDtos.cs`, beside `UpdateSegmentRequest`:

```csharp
/// <summary>Split one segment in two, before the word at <paramref name="WordIndex"/>.</summary>
/// <param name="DiscardRevision">Required when the segment has a manual edit. Both halves take their text
/// from the model's original - there is no principled way to divide edited prose at a word index the edit
/// may not contain - so the caller must say explicitly that losing it is intended.</param>
public record SplitSegmentRequest(int WordIndex, bool DiscardRevision = false);
```

- [ ] **Step 4: Add the endpoint**

In `RecordingsController.cs`, after `UpdateSegment`:

```csharp
    [HttpPost("{id:guid}/segments/{segmentId:guid}/split")]
    [EndpointSummary("Split a segment at a word boundary")]
    [EndpointDescription(
        "Divides one segment in two before the word at `wordIndex`, so a block that contains a second " +
        "voice can be separated and the interloper reassigned with the segment-speaker endpoint. The cut " +
        "snaps to the stored word timings; the silence between the two words belongs to neither half, " +
        "which is what voiceprint training needs.\n\n" +
        "**409** when the segment has no word timings (re-transcribe first), when the index would leave a " +
        "half empty, or when the segment has a manual edit and `discardRevision` was not set - splitting " +
        "cannot divide edited prose, so both halves fall back to the model's original text.\n\n" +
        "Permanent for this transcription version. Re-transcribe to regenerate the original segments.")]
    public async Task<IActionResult> SplitSegment(Guid id, Guid segmentId, SplitSegmentRequest req)
    {
        var owned = await _db.Recordings.AnyAsync(r => r.Id == id && r.UserId == UserId);
        if (!owned) return NotFound();

        var seg = await _db.Segments.Include(s => s.Transcription)
            .FirstOrDefaultAsync(s => s.Id == segmentId);
        if (seg?.Transcription is null || seg.Transcription.RecordingId != id) return NotFound();

        var words = SegmentWords.Parse(seg.WordsJson);
        if (words.Count == 0)
            return Conflict("This segment has no word timings; re-transcribe the recording to split it.");
        if (seg.Revised is not null && !req.DiscardRevision)
            return Conflict("This segment has an edit. Splitting discards it; resend with discardRevision.");

        var split = TranscriptSegmentSplit.Split(seg.StartMs, seg.EndMs, seg.Original, words, req.WordIndex);
        if (split is null) return Conflict("That split point would leave one half empty.");

        var transcriptionId = seg.Transcription.Id;
        var label = seg.SpeakerLabel;
        _db.Segments.Remove(seg);

        foreach (var half in new[] { split.Left, split.Right })
            _db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(),
                TranscriptionId = transcriptionId,
                SpeakerLabel = label,
                StartMs = half.StartMs,
                EndMs = half.EndMs,
                Original = TranscriptText.Normalize(half.Text),
                WordsJson = SegmentWords.Serialize(half.Words),
            });

        await _db.SaveChangesAsync();
        await RenumberAsync(transcriptionId);
        return NoContent();
    }
```

`RenumberAsync` is the ordinal-renumbering already inlined in `DeleteSegment` (around line 960). Extract it
into a private method there and call it from both, rather than writing the loop twice:

```csharp
    /// <summary>Renumber a transcription's segments contiguously from 0 in start-time order, after a
    /// delete or a split changed how many there are.</summary>
    private async Task RenumberAsync(Guid transcriptionId)
    {
        var survivors = await _db.Segments.Where(s => s.TranscriptionId == transcriptionId)
            .OrderBy(s => s.StartMs).ThenBy(s => s.Ordinal).ToListAsync();
        for (var i = 0; i < survivors.Count; i++) survivors[i].Ordinal = i;
        await _db.SaveChangesAsync();
    }
```

Ordering by `StartMs` first is what places the two new halves correctly among their neighbours - they are
added with fresh ordinals of 0 and would otherwise sort to the front.

- [ ] **Step 5: Run them and verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~RecordingSegment"`
Expected: PASS. Run the pre-existing `DeleteSegment` tests too - the extracted `RenumberAsync` changed
their code path:

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~DeleteSegment"`
Expected: PASS.

- [ ] **Step 6: Add the web client method**

In `apps/web/src/lib/api.ts`:

```ts
  /// Split a segment before the given word. `discardRevision` is required when the segment has a manual
  /// edit - the server refuses otherwise rather than dropping it silently.
  async splitSegment(id: string, segmentId: string, wordIndex: number, discardRevision = false): Promise<void> {
    await http.post(`/api/recordings/${id}/segments/${segmentId}/split`, { wordIndex, discardRevision });
  },
```

- [ ] **Step 7: Regenerate the OpenAPI snapshot and the n8n client**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
cd integrations/n8n-nodes-diariz && npm run generate
```

The snapshot test rewrites itself: run 1 fails, run 2 passes. Commit the regenerated files.

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/RecordingsController.cs apps/web/src/lib/api.ts tests/Diariz.Api.Tests integrations/n8n-nodes-diariz
git commit -m "feat(api): split a segment at a word boundary"
```

---

### Task 9: `Speaker.EmbeddingStale` + per-segment speaker reassignment

**Files:**
- Modify: `src/Diariz.Domain/Entities/Speaker.cs`
- Modify: `src/Diariz.Domain/DiarizDbContext.cs` (only if a default is needed; the property is not null)
- Create: migration `AddSpeakerEmbeddingStale`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs`
- Modify: `apps/web/src/lib/api.ts`, `apps/web/src/lib/types.ts`
- Test: `tests/Diariz.Api.Tests/SegmentSpeakerAssignTests.cs`

**Interfaces:**
- Produces:
  - `Speaker.EmbeddingStale` (`bool`, not null, default false).
  - `record AssignSegmentSpeakerRequest(string? Label)` - null asks the API to mint a new speaker.
  - `record SegmentSpeakerDto(string Label, string DisplayName)`.
  - `PUT /api/recordings/{id}/segments/{segmentId}/speaker` returning `SegmentSpeakerDto`.
  - Web: `api.assignSegmentSpeaker(id, segmentId, label)`, `SpeakerInfo.embeddingStale: boolean`.
  Tasks 10, 14 and 16 consume these.

**Why a split alone marks nothing stale.** The same audio is still attributed to the same speaker, only
divided. Only a *reassignment* moves audio between speakers, and it marks **both** labels - the one losing
the segment and the one gaining it.

- [ ] **Step 1: Write the failing tests**

Create `tests/Diariz.Api.Tests/SegmentSpeakerAssignTests.cs`:

```csharp
[Fact]
public async Task AssignSegmentSpeaker_MovesOneSegmentToAnExistingLabel()
{
    await using var db = TestDb.Create();
    var (controller, recordingId, segmentId) = await SeedTwoSpeakersAsync(db);

    var result = Assert.IsType<OkObjectResult>(
        await controller.AssignSegmentSpeaker(recordingId, segmentId, new AssignSegmentSpeakerRequest("SPEAKER_01")));

    Assert.Equal("SPEAKER_01", Assert.IsType<SegmentSpeakerDto>(result.Value).Label);
    Assert.Equal("SPEAKER_01", db.Segments.Single(s => s.Id == segmentId).SpeakerLabel);
}

[Fact]
public async Task AssignSegmentSpeaker_MarksBothLabelsStale()
{
    // The audio behind each embedding changed: one speaker lost a segment, the other gained one. Marking
    // only the receiver would leave the donor's voiceprint quietly describing audio it no longer owns.
    await using var db = TestDb.Create();
    var (controller, recordingId, segmentId) = await SeedTwoSpeakersAsync(db);

    await controller.AssignSegmentSpeaker(recordingId, segmentId, new AssignSegmentSpeakerRequest("SPEAKER_01"));

    Assert.All(db.Speakers.Where(s => s.RecordingId == recordingId), s => Assert.True(s.EmbeddingStale));
}

[Fact]
public async Task AssignSegmentSpeaker_WithNullLabel_MintsTheNextFreeSpeaker()
{
    // The client must not invent a label into the worker's namespace. It asks; the API allocates.
    await using var db = TestDb.Create();
    var (controller, recordingId, segmentId) = await SeedTwoSpeakersAsync(db); // SPEAKER_00, SPEAKER_01

    var result = Assert.IsType<OkObjectResult>(
        await controller.AssignSegmentSpeaker(recordingId, segmentId, new AssignSegmentSpeakerRequest(null)));

    Assert.Equal("SPEAKER_02", Assert.IsType<SegmentSpeakerDto>(result.Value).Label);
    var minted = db.Speakers.Single(s => s.Label == "SPEAKER_02");
    Assert.Equal("SPEAKER_02", minted.DisplayName);
    Assert.Null(minted.Embedding);
}

[Fact]
public async Task AssignSegmentSpeaker_DropsASpeakerLeftWithNoSegments()
{
    // Same rule DeleteSegment already applies: a label with nothing under it is not a speaker in this
    // recording, and leaving it would put an empty row in the Speakers tab.
    await using var db = TestDb.Create();
    var (controller, recordingId, segmentId) = await SeedOneSegmentPerSpeakerAsync(db);

    await controller.AssignSegmentSpeaker(recordingId, segmentId, new AssignSegmentSpeakerRequest("SPEAKER_01"));

    Assert.DoesNotContain(db.Speakers.Where(s => s.RecordingId == recordingId), s => s.Label == "SPEAKER_00");
}

[Fact]
public async Task AssignSegmentSpeaker_ToAnUnknownLabel_IsNotFound()
{
    await using var db = TestDb.Create();
    var (controller, recordingId, segmentId) = await SeedTwoSpeakersAsync(db);

    Assert.IsType<NotFoundResult>(await controller.AssignSegmentSpeaker(
        recordingId, segmentId, new AssignSegmentSpeakerRequest("SPEAKER_99")));
}

[Fact]
public async Task AssignSegmentSpeaker_ForAnotherUsersRecording_IsNotFound()
{
    await using var db = TestDb.Create();
    var (controller, recordingId, segmentId) = await SeedTwoSpeakersAsync(db, ownerId: Guid.NewGuid());

    Assert.IsType<NotFoundResult>(await controller.AssignSegmentSpeaker(
        recordingId, segmentId, new AssignSegmentSpeakerRequest("SPEAKER_01")));
}
```

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SegmentSpeakerAssign"`
Expected: FAIL to compile.

- [ ] **Step 3: Add the entity property and migration**

In `src/Diariz.Domain/Entities/Speaker.cs`:

```csharp
    /// <summary>The audio behind <see cref="Embedding"/> no longer describes this speaker - a segment was
    /// reassigned into or out of them. Set by per-segment reassignment, surfaced in the Voiceprint tab,
    /// cleared when a re-embed job returns. Nothing recomputes silently: that needs the worker and the
    /// original audio.</summary>
    public bool EmbeddingStale { get; set; }
```

```bash
dotnet ef migrations add AddSpeakerEmbeddingStale --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Confirm the generated file is one `AddColumn<bool>` on `Speakers`, not null, `defaultValue: false`.

- [ ] **Step 4: Add the DTOs**

In `ApiDtos.cs`:

```csharp
/// <summary>Move one segment to a different speaker. A null <paramref name="Label"/> asks the API to mint
/// a new speaker for this recording - the client must not invent a label into the diarizer's namespace.
/// </summary>
public record AssignSegmentSpeakerRequest(string? Label);

/// <summary>The speaker a segment now belongs to, including a label the API minted.</summary>
public record SegmentSpeakerDto(string Label, string DisplayName);
```

Also add `bool EmbeddingStale` to the existing `SpeakerInfo`/speaker DTO the recording detail returns, and
set it from `Speaker.EmbeddingStale` at each construction site. Grep for them:

```bash
grep -rn "new SpeakerInfo(" src/Diariz.Api/
```

- [ ] **Step 5: Add the endpoint**

In `RecordingsController.cs`, after `SplitSegment`:

```csharp
    [HttpPut("{id:guid}/segments/{segmentId:guid}/speaker")]
    [EndpointSummary("Reassign one segment to a different speaker")]
    [EndpointDescription(
        "Moves a single segment to another speaker - what you need after splitting a block that contained " +
        "a second voice. Pass an existing label, or **null** to have a new speaker minted for this " +
        "recording when the interrupting voice has no diarization slot of its own.\n\n" +
        "Both the losing and the gaining speaker have their stored voiceprint marked stale, since the " +
        "audio behind it changed. Nothing is recomputed here; use the voiceprint recompute action. A " +
        "speaker left with no segments drops off the recording.")]
    public async Task<IActionResult> AssignSegmentSpeaker(
        Guid id, Guid segmentId, AssignSegmentSpeakerRequest req)
    {
        var owned = await _db.Recordings.AnyAsync(r => r.Id == id && r.UserId == UserId);
        if (!owned) return NotFound();

        var seg = await _db.Segments.Include(s => s.Transcription)
            .FirstOrDefaultAsync(s => s.Id == segmentId);
        if (seg?.Transcription is null || seg.Transcription.RecordingId != id) return NotFound();

        var speakers = await _db.Speakers.Where(s => s.RecordingId == id).ToListAsync();
        var from = speakers.FirstOrDefault(s => s.Label == seg.SpeakerLabel);

        Speaker to;
        if (req.Label is null)
        {
            var label = SpeakerLabels.NextFree(speakers.Select(s => s.Label));
            to = new Speaker { Id = Guid.NewGuid(), RecordingId = id, Label = label, DisplayName = label };
            _db.Speakers.Add(to);
            speakers.Add(to);
        }
        else
        {
            var found = speakers.FirstOrDefault(s => s.Label == req.Label);
            if (found is null) return NotFound();
            to = found;
        }

        seg.SpeakerLabel = to.Label;
        if (from is not null) from.EmbeddingStale = true;
        to.EmbeddingStale = true;

        await _db.SaveChangesAsync();

        // A label with nothing under it is not a speaker in this recording - the same rule DeleteSegment
        // applies when it empties one.
        if (from is not null &&
            !await _db.Segments.AnyAsync(s => s.TranscriptionId == seg.TranscriptionId && s.SpeakerLabel == from.Label))
        {
            _db.Speakers.Remove(from);
            await _db.SaveChangesAsync();
        }

        return Ok(new SegmentSpeakerDto(to.Label, to.DisplayName));
    }
```

- [ ] **Step 6: Add the label allocator**

Create `src/Diariz.Api/Services/SpeakerLabels.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace Diariz.Api.Services;

/// <summary>Allocates diarization labels for speakers the API mints, so a client never writes into the
/// worker's <c>SPEAKER_NN</c> namespace and never collides with a label a later re-transcription produces.
/// </summary>
public static partial class SpeakerLabels
{
    [GeneratedRegex(@"^SPEAKER_(\d+)$")]
    private static partial Regex Numbered();

    /// <summary>The next unused <c>SPEAKER_NN</c> - one past the highest number present, so it stays free
    /// even when the existing labels have gaps. Two digits minimum, matching pyannote's format.</summary>
    public static string NextFree(IEnumerable<string> existing)
    {
        var highest = -1;
        foreach (var label in existing)
        {
            var m = Numbered().Match(label ?? "");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > highest) highest = n;
        }
        return $"SPEAKER_{(highest + 1).ToString("D2", CultureInfo.InvariantCulture)}";
    }
}
```

Add unit tests for it in the same run: empty input gives `SPEAKER_00`; `["SPEAKER_00","SPEAKER_02"]` gives
`SPEAKER_03` (one past the highest, not the first gap - a gap may be filled again by a re-transcription);
non-numeric labels are ignored.

- [ ] **Step 7: Run them and verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SegmentSpeaker|FullyQualifiedName~SpeakerLabels"`
Expected: PASS.

- [ ] **Step 8: Add the web client method and type**

In `apps/web/src/lib/api.ts`:

```ts
  /// Move one segment to another speaker. Pass null to have the server mint a new speaker.
  async assignSegmentSpeaker(id: string, segmentId: string, label: string | null): Promise<SegmentSpeakerDto> {
    const { data } = await http.put<SegmentSpeakerDto>(
      `/api/recordings/${id}/segments/${segmentId}/speaker`, { label });
    return data;
  },
```

and in `types.ts` add `embeddingStale: boolean;` to `SpeakerInfo`, plus
`export interface SegmentSpeakerDto { label: string; displayName: string }`.

- [ ] **Step 9: Regenerate, build and commit**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
cd integrations/n8n-nodes-diariz && npm run generate && cd ../..
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
git add src/Diariz.Domain src/Diariz.Api apps/web/src/lib tests/Diariz.Api.Tests integrations/n8n-nodes-diariz
git commit -m "feat(api): reassign one segment's speaker and flag the affected voiceprints stale"
```

---

### Task 10: The split editor in the transcript

**Files:**
- Create: `apps/web/src/components/detail/SegmentSplitModal.tsx`
- Create: `apps/web/src/components/detail/SegmentSplitModal.test.tsx`
- Modify: `apps/web/src/pages/RecordingDetail.tsx` (toolbar around line 1442; modal block around line 1740)
- Modify: `apps/web/src/locales/en/workspace.json`

**Interfaces:**
- Consumes: `api.getSegmentWords`, `api.splitSegment` (Tasks 6, 8), `api.assignSegmentSpeaker` (Task 9),
  `SegmentDto.hasWords`.
- Produces: nothing later tasks depend on.

**Why a separate component.** `RecordingDetail.tsx` is already 1800+ lines. The split editor owns its own
fetch, its own confirm, and its own two-step flow; putting it inline would make that file harder to hold in
context and the flow impossible to test on its own.

- [ ] **Step 1: Write the failing tests**

Create `apps/web/src/components/detail/SegmentSplitModal.test.tsx`:

```tsx
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import SegmentSplitModal from "./SegmentSplitModal";

vi.mock("../../lib/api", () => ({
  api: {
    getSegmentWords: vi.fn(),
    splitSegment: vi.fn(),
    assignSegmentSpeaker: vi.fn(),
  },
  apiErrorMessage: (_e: unknown, fallback: string) => fallback,
}));
import { api } from "../../lib/api";

const seg = {
  id: "s1", speaker: "SPEAKER_00", speakerDisplay: "Ken", startMs: 1000, endMs: 2500,
  original: "Hello world again", revised: null, hasWords: true,
};
const speakers = [
  { label: "SPEAKER_00", displayName: "Ken" },
  { label: "SPEAKER_01", displayName: "Aidan" },
];

beforeEach(() => {
  vi.mocked(api.getSegmentWords).mockResolvedValue([
    { w: "Hello", s: 1000, e: 1400 },
    { w: "world", s: 1500, e: 1900 },
    { w: "again", s: 2100, e: 2500 },
  ]);
  vi.mocked(api.splitSegment).mockResolvedValue(undefined);
  vi.mocked(api.assignSegmentSpeaker).mockResolvedValue({ label: "SPEAKER_01", displayName: "Aidan" });
});

it("offers a cut point between each pair of words, and none at the ends", async () => {
  render(<SegmentSplitModal recordingId="r1" seg={seg} speakers={speakers} onClose={() => {}} onDone={() => {}} />);
  // Three words means two interior gaps. A gap before the first or after the last would leave a half
  // empty, which the server rejects - so it must not be offered.
  await waitFor(() => expect(screen.getAllByRole("button", { name: /split here/i })).toHaveLength(2));
});

it("splits at the chosen gap and reassigns the new half", async () => {
  const onDone = vi.fn();
  render(<SegmentSplitModal recordingId="r1" seg={seg} speakers={speakers} onClose={() => {}} onDone={onDone} />);

  const gaps = await screen.findAllByRole("button", { name: /split here/i });
  await userEvent.click(gaps[1]); // before "again" -> wordIndex 2
  await userEvent.selectOptions(screen.getByLabelText(/speaker for the new part/i), "SPEAKER_01");
  await userEvent.click(screen.getByRole("button", { name: /^split$/i }));

  await waitFor(() => expect(api.splitSegment).toHaveBeenCalledWith("r1", "s1", 2, false));
  await waitFor(() => expect(onDone).toHaveBeenCalled());
});

it("asks before discarding an edit, and passes the flag only after confirming", async () => {
  const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
  render(<SegmentSplitModal recordingId="r1" seg={{ ...seg, revised: "my correction" }} speakers={speakers}
    onClose={() => {}} onDone={() => {}} />);

  const gaps = await screen.findAllByRole("button", { name: /split here/i });
  await userEvent.click(gaps[0]);
  await userEvent.click(screen.getByRole("button", { name: /^split$/i }));

  expect(confirm).toHaveBeenCalled();
  await waitFor(() => expect(api.splitSegment).toHaveBeenCalledWith("r1", "s1", 1, true));
});

it("does not split when the discard confirmation is declined", async () => {
  vi.spyOn(window, "confirm").mockReturnValue(false);
  render(<SegmentSplitModal recordingId="r1" seg={{ ...seg, revised: "my correction" }} speakers={speakers}
    onClose={() => {}} onDone={() => {}} />);

  const gaps = await screen.findAllByRole("button", { name: /split here/i });
  await userEvent.click(gaps[0]);
  await userEvent.click(screen.getByRole("button", { name: /^split$/i }));

  // Flush a macrotask so a mistakenly-fired call would have landed. Asserting immediately would pass
  // before the call could have happened, which proves nothing.
  await new Promise((r) => setTimeout(r, 0));
  expect(api.splitSegment).not.toHaveBeenCalled();
});
```

- [ ] **Step 2: Run them and verify they fail**

Run: `cd apps/web && npx vitest run src/components/detail/SegmentSplitModal.test.tsx`
Expected: FAIL - `Failed to resolve import "./SegmentSplitModal"`.

- [ ] **Step 3: Write the component**

Create `apps/web/src/components/detail/SegmentSplitModal.tsx`. It:

- fetches `api.getSegmentWords(recordingId, seg.id)` on mount (react-query, key `["segmentWords", seg.id]`);
- renders the words in order, with a `<button>` between each adjacent pair - indices `1..words.length-1`.
  **No gap before the first or after the last word**: those would leave a half empty and the server rejects
  them, so they must not be offered;
- shows the chosen gap by rendering the two halves' text either side of a visible divider, so the user sees
  what they are about to create before committing;
- offers a `<select>` labelled "Speaker for the new part", listing `speakers` plus a "New speaker" option
  whose value is the empty string (mapped to `null` when calling `assignSegmentSpeaker`);
- on **Split**: if `seg.revised != null`, `window.confirm(t("workspace:confirmSplitDiscardsEdit"))` first and
  bail out on a decline. Then `await api.splitSegment(recordingId, seg.id, index, seg.revised != null)`, and
  when the chosen speaker differs from `seg.speaker`, look up the newly created right-hand segment from a
  refetch and `await api.assignSegmentSpeaker(...)` on it. Call `onDone()`, which invalidates
  `["recording", id]` in the parent;
- reports failures through `apiErrorMessage(e, t("workspace:errSplitSegment"))` in a single error banner,
  matching `PersonEditor`'s established shape.

Add the strings to `apps/web/src/locales/en/workspace.json` (plain hyphens only):

```json
"splitSegment": "Split segment",
"splitHere": "Split here",
"splitNoWords": "Re-transcribe this recording to split its segments",
"splitNewPartSpeaker": "Speaker for the new part",
"splitNewSpeaker": "New speaker",
"confirmSplitDiscardsEdit": "This segment has an edit. Splitting divides the model's original text and discards your edit. Continue?",
"errSplitSegment": "Could not split this segment."
```

- [ ] **Step 4: Run them and verify they pass**

Run: `cd apps/web && npx vitest run src/components/detail/SegmentSplitModal.test.tsx`
Expected: PASS, 4 tests.

- [ ] **Step 5: Write the failing toolbar test**

Add to `apps/web/src/pages/RecordingDetail.test.tsx`:

```tsx
it("disables Split for a segment with no word timings and explains why", async () => {
  // The affordance must not silently vanish: a user who cannot split needs to know it is because the
  // recording predates word timings, not because the feature is missing.
  renderDetail({ segments: [{ ...segFixture, id: "s1", hasWords: false }] });
  await userEvent.click(await screen.findByText(/Hello world/));

  const split = screen.getByRole("button", { name: /split segment/i });
  expect(split.hasAttribute("disabled")).toBe(true);
  expect(split.getAttribute("title")).toMatch(/re-transcribe/i);
});
```

- [ ] **Step 6: Run it and verify it fails**

Run: `cd apps/web && npx vitest run src/pages/RecordingDetail.test.tsx`
Expected: FAIL - no button named "Split segment".

- [ ] **Step 7: Wire the toolbar button and the modal**

In `RecordingDetail.tsx`, beside the existing edit button (line ~1442):

```tsx
<ToolbarButton
  label={t("workspace:splitSegment")}
  icon={ScissorsIcon}
  onClick={splitSelected}
  disabled={selectedSegIds.size !== 1 || !singleSelectedSeg?.hasWords}
  title={singleSelectedSeg && !singleSelectedSeg.hasWords ? t("workspace:splitNoWords") : undefined}
/>
```

with `singleSelectedSeg` derived the same way `editSelected` finds its segment, and:

```tsx
const [splittingSeg, setSplittingSeg] = useState<SegmentDto | null>(null);

function splitSelected() {
  if (selectedSegIds.size !== 1) return;
  const seg = rec?.current?.segments.find((s) => s.id === [...selectedSegIds][0]);
  if (seg?.hasWords) setSplittingSeg(seg);
}
```

Render the modal beside `SegmentEditModal` (line ~1740):

```tsx
{splittingSeg && (
  <SegmentSplitModal
    recordingId={id}
    seg={splittingSeg}
    speakers={speakerList}
    onClose={() => setSplittingSeg(null)}
    onDone={() => {
      setSplittingSeg(null);
      setSelectedSegIds(new Set());
      qc.invalidateQueries({ queryKey: ["recording", id] });
    }}
  />
)}
```

Add a `ScissorsIcon` to `apps/web/src/components/detail/icons.tsx` following the existing icons' shape.

- [ ] **Step 8: Run the full web suite and verify it passes**

Run: `cd apps/web && npm run build && npm test`
Expected: PASS, no new warnings. `npm run build` runs the `tsc` typecheck, which is what catches a missed
`hasWords` on a test fixture.

- [ ] **Step 9: Commit**

```bash
git add apps/web/src/components/detail/SegmentSplitModal.tsx apps/web/src/components/detail/SegmentSplitModal.test.tsx apps/web/src/components/detail/icons.tsx apps/web/src/pages/RecordingDetail.tsx apps/web/src/pages/RecordingDetail.test.tsx apps/web/src/locales/en/workspace.json
git commit -m "feat(web): split a mixed segment and reassign the new part"
```

- [ ] **Step 10: Verify it in the running app**

jsdom computes no geometry, so a passing component test says nothing about whether the word gaps are
clickable at a real size. Start the stack, open a recording with word timings, split a segment, and confirm
the two halves appear with the right text and timestamps. If live and the tests disagree, believe the tests
and force a fresh build - Vite HMR serves stale modules while looking fine.

---

### Task 11: `VoiceSample.SpansJson` / `UsedMs` + the `VoiceprintSpans` helper

**Files:**
- Modify: `src/Diariz.Domain/Entities/VoiceSample.cs`
- Modify: `src/Diariz.Domain/DiarizDbContext.cs`
- Create: migration `AddVoiceSampleSpans`
- Create: `src/Diariz.Api/Services/VoiceprintSpans.cs`
- Test: `tests/Diariz.Api.Tests/VoiceprintSpansTests.cs`,
  `tests/Diariz.Api.IntegrationTests/VoiceprintSpansIntegrationTests.cs`

**Interfaces:**
- Produces, consumed by Tasks 12, 14 and 16:
  - `VoiceSample.SpansJson` (`string?`) - **null means "the whole speaker"**, which is exactly today's
    behaviour, so the migration backfills nothing and every existing voiceprint keeps working.
  - `VoiceSample.UsedMs` (`int?`) - how much audio the last embed actually consumed. Also the **pending
    marker**: the enqueue clears it, the callback sets it, so "recompute in flight" survives a page reload.
  - `record VoiceprintSpan(long StartMs, long EndMs)` in `Diariz.Api.Contracts`.
  - `static class VoiceprintSpans` with `Serialize`, `Parse`, `TotalMs`, `Coverage`, `FromSegments`.

**Why spans and not segment ids.** Segment rows belong to a transcription *version*; a re-transcribe
replaces every one of them and stored ids would dangle. `Speaker` rows survive re-transcription, and so do
wall-clock spans - which are also exactly what the worker needs to slice audio.

- [ ] **Step 1: Write the failing helper tests**

Create `tests/Diariz.Api.Tests/VoiceprintSpansTests.cs`:

```csharp
using Diariz.Api.Contracts;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

public class VoiceprintSpansTests
{
    [Fact]
    public void Serialize_ThenParse_RoundTrips()
    {
        var spans = new List<VoiceprintSpan> { new(1000, 2000), new(5000, 6500) };
        Assert.Equal(spans, VoiceprintSpans.Parse(VoiceprintSpans.Serialize(spans)));
    }

    [Fact]
    public void Serialize_NullOrEmpty_IsNull()
    {
        // Null is the "whole speaker" state that every pre-existing sample is in. An empty array would
        // mean "train on nothing", which is a different and useless thing.
        Assert.Null(VoiceprintSpans.Serialize(null));
        Assert.Null(VoiceprintSpans.Serialize(new List<VoiceprintSpan>()));
    }

    [Fact]
    public void TotalMs_SumsTheSpans() =>
        Assert.Equal(2500, VoiceprintSpans.TotalMs([new(1000, 2000), new(5000, 6500)]));

    [Fact]
    public void Coverage_WithNoSpans_IsIncluded()
    {
        // No spans means the whole speaker, so every segment is in.
        Assert.Equal(SpanCoverage.Included, VoiceprintSpans.Coverage(1000, 2000, []));
    }

    [Theory]
    [InlineData(1000, 2000, SpanCoverage.Included)]  // exactly a span
    [InlineData(1200, 1800, SpanCoverage.Included)]  // inside a span
    [InlineData(3000, 4000, SpanCoverage.Excluded)]  // between spans
    [InlineData(1500, 2500, SpanCoverage.Partial)]   // straddles the end of one
    public void Coverage_ClassifiesASegmentAgainstTheSpans(long start, long end, SpanCoverage expected) =>
        Assert.Equal(expected, VoiceprintSpans.Coverage(start, end, [new(1000, 2000), new(5000, 6500)]));

    [Fact]
    public void Coverage_SpanningTwoAdjacentSpans_IsIncluded()
    {
        // Two spans that touch cover the segment between them completely. Reporting Partial here would
        // show a permanently half-ticked row that no amount of clicking could resolve.
        Assert.Equal(SpanCoverage.Included,
            VoiceprintSpans.Coverage(1500, 2500, [new(1000, 2000), new(2000, 3000)]));
    }

    [Fact]
    public void FromSegments_MergesOverlappingAndTouchingSpans()
    {
        // The user ticks segments; what gets stored is the audio those segments occupy. Adjacent picks
        // must collapse, or a 40-minute selection becomes hundreds of one-line spans in the job payload.
        Assert.Equal([new VoiceprintSpan(1000, 3000), new VoiceprintSpan(5000, 6000)],
            VoiceprintSpans.FromSegments([(2000, 3000), (1000, 2000), (5000, 6000)]));
    }

    [Fact]
    public void FromSegments_WithNothingSelected_IsEmpty() =>
        Assert.Empty(VoiceprintSpans.FromSegments([]));

    [Fact]
    public void FromSegments_DropsZeroAndNegativeLengthSpans() =>
        Assert.Equal([new VoiceprintSpan(1000, 2000)],
            VoiceprintSpans.FromSegments([(1000, 2000), (3000, 3000), (5000, 4000)]));
}
```

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~VoiceprintSpansTests"`
Expected: FAIL to compile.

- [ ] **Step 3: Write the helper**

In `src/Diariz.Api/Contracts/WorkerContracts.cs`:

```csharp
/// <summary>A span of a recording's audio, in ms from its start, that trains a voiceprint. Stored on the
/// voice sample and sent to the worker verbatim.</summary>
public record VoiceprintSpan(long StartMs, long EndMs);
```

Create `src/Diariz.Api/Services/VoiceprintSpans.cs`:

```csharp
using System.Text.Json;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>How much of a segment the stored spans cover.</summary>
public enum SpanCoverage
{
    /// <summary>Every millisecond of the segment is selected.</summary>
    Included,
    /// <summary>None of it is.</summary>
    Excluded,
    /// <summary>Some of it. Only reachable after a re-transcription moved the segment boundaries under a
    /// selection that was made against the old ones; re-ticking normalises it away.</summary>
    Partial,
}

/// <summary>Reads and writes the <c>VoiceSample.SpansJson</c> column, and reconciles the stored spans
/// against whatever segments the current transcription happens to have.
///
/// <para><b>Null spans mean the whole speaker</b> - today's behaviour for every sample that predates
/// selection. Nothing needs backfilling and no existing voiceprint changes.</para></summary>
public static class VoiceprintSpans
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string? Serialize(IReadOnlyList<VoiceprintSpan>? spans) =>
        spans is null || spans.Count == 0 ? null : JsonSerializer.Serialize(spans, Options);

    /// <summary>Never throws: an unreadable value reads as "the whole speaker", which is the behaviour
    /// the sample had before anyone selected anything.</summary>
    public static IReadOnlyList<VoiceprintSpan> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<VoiceprintSpan>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static long TotalMs(IReadOnlyList<VoiceprintSpan> spans) =>
        spans.Sum(s => Math.Max(0, s.EndMs - s.StartMs));

    /// <summary>Classify one segment against the selection. An empty selection is "the whole speaker", so
    /// everything is <see cref="SpanCoverage.Included"/>.</summary>
    public static SpanCoverage Coverage(long startMs, long endMs, IReadOnlyList<VoiceprintSpan> spans)
    {
        if (spans.Count == 0) return SpanCoverage.Included;

        // Walk the merged spans in order, consuming the segment from its start. Merging first is what
        // makes two touching spans cover the segment between them, rather than reporting a Partial that
        // the user could never resolve.
        var cursor = startMs;
        var sawOverlap = false;
        foreach (var s in Merge(spans))
        {
            if (s.EndMs <= cursor) continue;
            if (s.StartMs > cursor) break;          // a gap before the next span: not fully covered
            sawOverlap = true;
            cursor = s.EndMs;
            if (cursor >= endMs) return SpanCoverage.Included;
        }

        if (cursor > startMs) return SpanCoverage.Partial;
        return sawOverlap || spans.Any(s => s.StartMs < endMs && s.EndMs > startMs)
            ? SpanCoverage.Partial
            : SpanCoverage.Excluded;
    }

    /// <summary>Turn the segments the user ticked into the audio they occupy: sorted, zero-length dropped,
    /// and overlapping or touching spans collapsed. Without the collapse a long selection becomes hundreds
    /// of one-line spans in the job payload.</summary>
    public static IReadOnlyList<VoiceprintSpan> FromSegments(IEnumerable<(long StartMs, long EndMs)> selected) =>
        Merge(selected.Select(s => new VoiceprintSpan(s.StartMs, s.EndMs)).ToList());

    private static List<VoiceprintSpan> Merge(IReadOnlyList<VoiceprintSpan> spans)
    {
        var result = new List<VoiceprintSpan>();
        foreach (var s in spans.Where(s => s.EndMs > s.StartMs).OrderBy(s => s.StartMs).ThenBy(s => s.EndMs))
        {
            if (result.Count > 0 && s.StartMs <= result[^1].EndMs)
                result[^1] = result[^1] with { EndMs = Math.Max(result[^1].EndMs, s.EndMs) };
            else
                result.Add(s);
        }
        return result;
    }
}
```

- [ ] **Step 4: Run them and verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~VoiceprintSpansTests"`
Expected: PASS, 12 test cases.

- [ ] **Step 5: Add the entity properties and the migration**

In `src/Diariz.Domain/Entities/VoiceSample.cs`:

```csharp
    /// <summary>The spans of this recording's audio that train the voiceprint, as JSON. <b>Null means the
    /// whole speaker</b> - the state every sample enrolled before selection existed is in, and the
    /// behaviour that has always applied. Spans, not segment ids: a re-transcribe replaces every segment
    /// row, and ids would dangle where wall-clock times do not.</summary>
    public string? SpansJson { get; set; }

    /// <summary>How much audio the last embedding actually consumed, in ms, or null when a recompute is
    /// queued and has not returned. Doubles as the pending marker, so "recompute in flight" survives a
    /// page reload instead of living only in component state - and gives the UI the honest
    /// "using 1:20 of the 4:12 selected" it must show, since the worker still caps the pooled audio.</summary>
    public int? UsedMs { get; set; }
```

In `DiarizDbContext.OnModelCreating`, inside the `isNpgsql` guard that already configures
`VoiceSample.Embedding` (around line 628):

```csharp
                e.Property(v => v.SpansJson).HasColumnType("jsonb");
```

```bash
dotnet ef migrations add AddVoiceSampleSpans --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Confirm the generated file is exactly two `AddColumn` calls on `ProfileContributions` - `SpansJson` as
nullable `jsonb` and `UsedMs` as nullable `integer`. The table is `ProfileContributions`, not
`VoiceSamples`; see the table-naming note on `Person`.

- [ ] **Step 6: Write the integration test for the jsonb round-trip**

Create `tests/Diariz.Api.IntegrationTests/VoiceprintSpansIntegrationTests.cs` mirroring
`SegmentWordsIntegrationTests`: write spans through `Serialize`, read the column back in a second context,
and compare the **parsed** values. Byte-comparing the column text never matches on real Postgres, and the
in-memory provider stores plain text and hides it.

- [ ] **Step 7: Run it and verify it passes**

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~VoiceprintSpans"`
Expected: PASS. (Needs Docker.)

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Domain src/Diariz.Api/Contracts/WorkerContracts.cs src/Diariz.Api/Services/VoiceprintSpans.cs tests/Diariz.Api.Tests/VoiceprintSpansTests.cs tests/Diariz.Api.IntegrationTests/VoiceprintSpansIntegrationTests.cs
git commit -m "feat(api): store which spans of audio train each voice sample"
```

---

### Task 12: The `voiceprint-jobs` stream and the spans endpoint

**Files:**
- Modify: `src/Diariz.Api/Contracts/WorkerContracts.cs`
- Modify: `src/Diariz.Api/Configuration/AppOptions.cs`
- Modify: `src/Diariz.Api/Services/JobQueue.cs`
- Modify: `tests/Diariz.Api.TestSupport/Fakes.cs` (the existing `IJobQueue` fake)
- Modify: `src/Diariz.Api/Controllers/PeopleController.cs`
- Modify: `apps/web/src/lib/api.ts`, `apps/web/src/lib/types.ts`
- Test: `tests/Diariz.Api.Tests/VoiceprintRecomputeTests.cs`,
  `tests/Diariz.Api.IntegrationTests/VoiceprintQueueIntegrationTests.cs`

**Interfaces:**
- Consumes: `VoiceprintSpans`, `VoiceprintSpan` (Task 11).
- Produces, consumed by Tasks 13, 14 and 16:
  - `record VoiceprintJob(Guid VoiceSampleId, Guid RecordingId, string BlobKey, IReadOnlyList<VoiceprintSpan> Spans)`.
  - `IJobQueue.EnqueueVoiceprintAsync(VoiceprintJob job, CancellationToken ct = default)`.
  - `JobQueueOptions.VoiceprintStreamKey`, default `"voiceprint-jobs"`.
  - `record SetVoiceSampleSpansRequest(IReadOnlyList<VoiceprintSpan> Spans)`.
  - `PUT /api/people/{id}/voiceprint/samples/{sampleId}/spans` returning `202`.
  - `VoiceSampleDto` gains `SelectedMs`, `UsedMs`, `TotalMs`, `Stale`, `Pending`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Diariz.Api.Tests/VoiceprintRecomputeTests.cs`:

```csharp
[Fact]
public async Task SetSpans_StoresThemAndQueuesAJobCarryingTheBlobKey()
{
    await using var db = TestDb.Create();
    var queue = new FakeJobQueue();
    var (controller, personId, sampleId) = await SeedSampleAsync(db, queue, blobKey: "audio/r1.webm");

    var result = await controller.SetVoiceSampleSpans(personId, sampleId,
        new SetVoiceSampleSpansRequest([new VoiceprintSpan(1000, 3000)]));

    Assert.IsType<AcceptedResult>(result);
    Assert.Equal([new VoiceprintSpan(1000, 3000)],
        VoiceprintSpans.Parse(db.VoiceSamples.Single().SpansJson));
    var job = Assert.Single(queue.VoiceprintJobs);
    Assert.Equal("audio/r1.webm", job.BlobKey);
    Assert.Equal([new VoiceprintSpan(1000, 3000)], job.Spans);
}

[Fact]
public async Task SetSpans_ClearsUsedMsSoTheRowReadsAsPending()
{
    // Pending must be server-derived, or a page reload during a recompute shows a stale "using 0:30"
    // as though the new selection had already been applied.
    await using var db = TestDb.Create();
    var queue = new FakeJobQueue();
    var (controller, personId, sampleId) = await SeedSampleAsync(db, queue, usedMs: 30000);

    await controller.SetVoiceSampleSpans(personId, sampleId,
        new SetVoiceSampleSpansRequest([new VoiceprintSpan(1000, 3000)]));

    Assert.Null(db.VoiceSamples.Single().UsedMs);
}

[Fact]
public async Task SetSpans_WithNoSpans_RevertsToTheWholeSpeaker()
{
    await using var db = TestDb.Create();
    var queue = new FakeJobQueue();
    var (controller, personId, sampleId) = await SeedSampleAsync(db, queue);

    await controller.SetVoiceSampleSpans(personId, sampleId, new SetVoiceSampleSpansRequest([]));

    Assert.Null(db.VoiceSamples.Single().SpansJson);
    Assert.Single(queue.VoiceprintJobs); // still recomputes - the selection did change
}

[Fact]
public async Task SetSpans_ForAnOptedOutPerson_IsConflictAndQueuesNothing()
{
    await using var db = TestDb.Create();
    var queue = new FakeJobQueue();
    var (controller, personId, sampleId) = await SeedSampleAsync(db, queue, optedOut: true);

    Assert.IsType<ConflictObjectResult>(await controller.SetVoiceSampleSpans(personId, sampleId,
        new SetVoiceSampleSpansRequest([new VoiceprintSpan(1000, 3000)])));
    Assert.Empty(queue.VoiceprintJobs);
}

[Fact]
public async Task SetSpans_WithoutBiometricPermission_IsForbidden()
{
    await using var db = TestDb.Create();
    var queue = new FakeJobQueue();
    var (controller, personId, sampleId) = await SeedSampleAsync(db, queue, callerCanManage: false);

    Assert.IsType<ForbidResult>(await controller.SetVoiceSampleSpans(personId, sampleId,
        new SetVoiceSampleSpansRequest([new VoiceprintSpan(1000, 3000)])));
}

[Fact]
public async Task SetSpans_WhenTheAudioIsGone_IsConflict()
{
    // Audio can be deleted while the sample survives. Queueing a job the worker can only fail is worse
    // than saying so.
    await using var db = TestDb.Create();
    var queue = new FakeJobQueue();
    var (controller, personId, sampleId) = await SeedSampleAsync(db, queue, audioDeleted: true);

    Assert.IsType<ConflictObjectResult>(await controller.SetVoiceSampleSpans(personId, sampleId,
        new SetVoiceSampleSpansRequest([new VoiceprintSpan(1000, 3000)])));
    Assert.Empty(queue.VoiceprintJobs);
}
```

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~VoiceprintRecompute"`
Expected: FAIL to compile.

- [ ] **Step 3: Add the job contract, the option and the queue method**

In `WorkerContracts.cs`:

```csharp
/// <summary>Job payload for an on-demand voiceprint re-embed, consumed by the Python worker. It downloads
/// <paramref name="BlobKey"/>, slices exactly <paramref name="Spans"/> out of the waveform, embeds them
/// with ECAPA and reports back. No Whisper or pyannote involved - it is seconds of work, but it shares the
/// worker process, so it can queue behind an in-flight transcription.</summary>
public record VoiceprintJob(
    Guid VoiceSampleId,
    Guid RecordingId,
    string BlobKey,
    IReadOnlyList<VoiceprintSpan> Spans);

/// <summary>Callback body the worker POSTs when a re-embed succeeds.</summary>
public record VoiceprintResult(Guid VoiceSampleId, float[] Embedding, int UsedMs, int SelectedMs);

/// <summary>Callback body the worker POSTs when a re-embed fails.</summary>
public record VoiceprintFailure(Guid VoiceSampleId, string Error);
```

In `AppOptions.cs`, on `JobQueueOptions` beside `MergeStreamKey`:

```csharp
    public string VoiceprintStreamKey { get; set; } = "voiceprint-jobs";
```

In `JobQueue.cs`, add to the interface and implement following `EnqueueAudioMergeAsync` exactly:

```csharp
    Task EnqueueVoiceprintAsync(VoiceprintJob job, CancellationToken ct = default);
```

```csharp
    public async Task EnqueueVoiceprintAsync(VoiceprintJob job, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var payload = JsonSerializer.Serialize(job);
        await db.StreamAddAsync(_opts.VoiceprintStreamKey, "job", payload);
    }
```

Add `VoiceprintJobs` (a `List<VoiceprintJob>`) to the existing `IJobQueue` fake in
`tests/Diariz.Api.TestSupport/Fakes.cs` - **not** a mocking library, and not a second fake.

- [ ] **Step 4: Add the endpoint**

In `PeopleController.cs`, beside `RemoveVoiceSample`:

```csharp
    [HttpPut("{id:guid}/voiceprint/samples/{sampleId:guid}/spans")]
    [EndpointSummary("Choose which audio trains one voice sample")]
    [EndpointDescription(
        "Replaces the spans of the contributing recording's audio that this sample is embedded from, and " +
        "queues a re-embed. Send an **empty list** to go back to the whole speaker, which is what every " +
        "sample does by default.\n\n" +
        "Returns **202**: the worker does the work. Until it reports back the sample reads as pending " +
        "(`usedMs` is null). The worker still caps how much audio it pools, so `usedMs` may be less than " +
        "the total you selected - the UI states both.\n\n" +
        "**409** when the person has opted out of voice-printing, or the recording's audio has been " +
        "deleted. **403** without permission to manage this person's biometrics.")]
    public async Task<IActionResult> SetVoiceSampleSpans(
        Guid id, Guid sampleId, SetVoiceSampleSpansRequest req)
    {
        var person = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (person is null) return NotFound();
        if (!await CanManageBiometricsAsync(person)) return Forbid();
        if (person.VoiceprintOptOut) return Conflict("This person has opted out of voice-printing.");

        var sample = await _db.VoiceSamples.FirstOrDefaultAsync(v => v.Id == sampleId && v.PersonId == id);
        if (sample is null) return NotFound();

        var recording = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == sample.RecordingId);
        if (recording is null || recording.AudioDeletedAt is not null)
            return Conflict("This recording's audio is no longer available to re-embed from.");

        var spans = VoiceprintSpans.FromSegments(req.Spans.Select(s => (s.StartMs, s.EndMs)));
        sample.SpansJson = VoiceprintSpans.Serialize(spans);
        // Pending is server-derived: a reload during a recompute must not show the old figure as though
        // the new selection had already been applied.
        sample.UsedMs = null;
        await _db.SaveChangesAsync();

        await _queue.EnqueueVoiceprintAsync(
            new VoiceprintJob(sample.Id, recording.Id, recording.BlobKey, spans));
        return Accepted();
    }
```

`CanManageBiometricsAsync` is the rule already used elsewhere in this controller
(`person.LinkedUserId == UserId || await CanManagePeopleAsync()`, line 59) - reuse it, do not restate it.
`PeopleController` will need `IJobQueue` injected; add it to the constructor and update **every**
construction site, including `tests/Diariz.Api.IntegrationTests/RbacIntegrationTests.cs`, which builds
controllers by hand and is not caught by a unit-only test run.

- [ ] **Step 5: Widen `VoiceSampleDto`**

```csharp
public record VoiceSampleDto(
    Guid Id, Guid RecordingId, string RecordingName, string SpeakerLabel, long StartMs,
    DateTimeOffset CreatedAt,
    /// <summary>Total ms selected, or the speaker's whole duration when nothing is selected.</summary>
    long SelectedMs = 0,
    /// <summary>Ms the last embed actually consumed, or null while a recompute is queued. Less than
    /// <paramref name="SelectedMs"/> when the worker's cap truncated the selection.</summary>
    int? UsedMs = null,
    /// <summary>The contributing speaker's audio was re-attributed, so this snapshot no longer describes
    /// it. Derived by joining to the speaker's <c>EmbeddingStale</c>, never stored twice.</summary>
    bool Stale = false,
    /// <summary>A recompute is queued and has not reported back.</summary>
    bool Pending = false);
```

Populate these in `PeopleController.Get`'s existing in-memory stitch: `SelectedMs` from
`VoiceprintSpans.TotalMs` (falling back to the speaker's segment total when spans are empty), `Stale` from
the speaker row's `EmbeddingStale`, and `Pending` as `sample.SpansJson is not null && sample.UsedMs is null`.

- [ ] **Step 6: Run them and verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~VoiceprintRecompute"`
Expected: PASS, 6 tests.

- [ ] **Step 7: Add an integration test for the stream wire format**

Create `tests/Diariz.Api.IntegrationTests/VoiceprintQueueIntegrationTests.cs`: enqueue a `VoiceprintJob`
through the real `RedisJobQueue` against the fixture's Redis, `XREAD` it back, and assert the JSON has
**PascalCase** keys (`VoiceSampleId`, `BlobKey`, `Spans[0].StartMs`). The Python worker reads these by
name; a casing change here is silent until a job runs.

- [ ] **Step 8: Add the web client method and types**

```ts
  /// Replace the spans of audio that train one voice sample, and queue a re-embed. Empty = whole speaker.
  async setVoiceSampleSpans(id: string, sampleId: string, spans: { startMs: number; endMs: number }[]): Promise<void> {
    await http.put(`/api/people/${id}/voiceprint/samples/${sampleId}/spans`, { spans });
  },
```

and extend `VoiceSample` in `types.ts` with `selectedMs: number; usedMs: number | null; stale: boolean; pending: boolean;`.

- [ ] **Step 9: Build, regenerate and commit**

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
cd integrations/n8n-nodes-diariz && npm run generate && cd ../..
git add src/Diariz.Api apps/web/src/lib tests integrations/n8n-nodes-diariz
git commit -m "feat(api): queue an on-demand voiceprint re-embed from chosen audio spans"
```

---

### Task 13: The worker consumes `voiceprint-jobs`

**Files:**
- Create: `src/Diariz.Worker/voiceprint.py`
- Create: `src/Diariz.Worker/tests/test_voiceprint.py`
- Modify: `src/Diariz.Worker/config.py`
- Modify: `src/Diariz.Worker/callback.py`
- Modify: `src/Diariz.Worker/worker.py`
- Test: `src/Diariz.Worker/tests/test_worker.py`, `src/Diariz.Worker/tests/test_callback.py`

**Interfaces:**
- Consumes: the `VoiceprintJob` payload from Task 12 (PascalCase keys).
- Produces: `POST {API_BASE_URL}/internal/people/voiceprint-result` with `X-Worker-Secret` and body
  `{"VoiceSampleId", "Embedding", "UsedMs", "SelectedMs"}`; failure to `.../voiceprint-failure` with
  `{"VoiceSampleId", "Error"}`. Task 14 consumes these.

**The cap.** `EMBED_MAX_SECONDS` goes 30 -> 120, for **both** the transcription-time path and this one. One
cap, not two that drift. ECAPA on 120s versus 30s is a rounding error on GPU, and the transcription-time
embeddings stay comparable with existing centroids.

- [ ] **Step 1: Write the failing slicing tests**

Create `src/Diariz.Worker/tests/test_voiceprint.py`:

```python
"""Tests for slicing chosen audio spans and embedding them (voiceprint.embed_spans)."""
import numpy as np

import voiceprint


def _stub_embedder(seen):
    def embed(waveform):
        seen.append(len(waveform))
        return np.array([1.0, 0.0, 0.0], dtype="float32")
    return embed


def test_concatenates_only_the_chosen_spans():
    # 16 kHz: 1000 ms == 16000 samples. Two 500 ms spans must give 16000 samples, not the whole clip.
    audio = np.arange(48000, dtype="float32")
    seen = []
    voiceprint.embed_spans(audio, [{"StartMs": 0, "EndMs": 500}, {"StartMs": 2000, "EndMs": 2500}],
                           _stub_embedder(seen), sample_rate=16000, max_seconds=120)
    assert seen == [16000]


def test_no_spans_means_the_whole_clip():
    # An empty list is "the whole speaker" - the state every sample that predates selection is in.
    audio = np.arange(32000, dtype="float32")
    seen = []
    voiceprint.embed_spans(audio, [], _stub_embedder(seen), sample_rate=16000, max_seconds=120)
    assert seen == [32000]


def test_truncates_to_the_cap_and_reports_what_it_used():
    audio = np.arange(16000 * 200, dtype="float32")
    result = voiceprint.embed_spans(audio, [{"StartMs": 0, "EndMs": 200000}],
                                    _stub_embedder([]), sample_rate=16000, max_seconds=120)
    # The caller must be able to say "using 2:00 of the 3:20 selected" rather than implying it used it all.
    assert result["UsedMs"] == 120000
    assert result["SelectedMs"] == 200000


def test_clamps_spans_that_run_past_the_end_of_the_audio():
    audio = np.arange(16000, dtype="float32")
    seen = []
    voiceprint.embed_spans(audio, [{"StartMs": 500, "EndMs": 99999}], _stub_embedder(seen),
                           sample_rate=16000, max_seconds=120)
    assert seen == [8000]


def test_l2_normalises_the_vector():
    audio = np.arange(16000, dtype="float32")
    result = voiceprint.embed_spans(audio, [], lambda w: np.array([3.0, 4.0], dtype="float32"),
                                    sample_rate=16000, max_seconds=120)
    assert result["Embedding"] == [0.6, 0.8]


def test_returns_none_when_the_spans_select_no_audio():
    # A selection entirely past the end of the clip must not embed silence and call it a voiceprint.
    audio = np.arange(16000, dtype="float32")
    assert voiceprint.embed_spans(audio, [{"StartMs": 5000, "EndMs": 6000}], _stub_embedder([]),
                                  sample_rate=16000, max_seconds=120) is None
```

- [ ] **Step 2: Run them and verify they fail**

Run: `cd src/Diariz.Worker && python -m pytest tests/test_voiceprint.py -v`
Expected: FAIL - `ModuleNotFoundError: No module named 'voiceprint'`.

- [ ] **Step 3: Write the module**

Create `src/Diariz.Worker/voiceprint.py`:

```python
"""Re-embed a voiceprint from chosen spans of a recording's audio.

Pure with respect to the model - the embedder is passed in - so the slicing and the cap can be tested
without torch, exactly like pipeline._speaker_embeddings.
"""
import numpy as np

from config import config


def embed_spans(audio, spans, embed_fn, sample_rate: int = config.SAMPLE_RATE,
                max_seconds: float = config.EMBED_MAX_SECONDS):
    """Concatenate the audio inside `spans`, cap it, embed it, and L2-normalise.

    An empty `spans` means the whole clip - the state every sample that predates span selection is in.
    Spans are clamped to the clip, so a selection made before an audio merge cannot read past the end.

    Returns None when the spans select no audio at all: embedding silence and storing it as someone's
    voiceprint is worse than reporting that there was nothing to embed.
    """
    n = len(audio)
    max_samples = int(max_seconds * sample_rate) if max_seconds else n

    if not spans:
        selected_ms = int(round(n / sample_rate * 1000))
        chunks, total = [audio], n
    else:
        chunks, total, selected_ms = [], 0, 0
        for span in spans:
            start_ms, end_ms = int(span["StartMs"]), int(span["EndMs"])
            selected_ms += max(0, end_ms - start_ms)
            a = max(0, int(start_ms * sample_rate / 1000))
            b = min(n, int(end_ms * sample_rate / 1000))
            if b <= a:
                continue
            chunks.append(audio[a:b])
            total += b - a
            if total >= max_samples:
                break

    if not chunks or total == 0:
        return None

    waveform = np.concatenate(chunks)[:max_samples]
    if waveform.size == 0:
        return None

    vec = np.asarray(embed_fn(waveform), dtype="float32").reshape(-1)
    norm = float(np.linalg.norm(vec))
    if norm > 0:
        vec = vec / norm

    return {
        "Embedding": [float(x) for x in vec],
        "UsedMs": int(round(waveform.size / sample_rate * 1000)),
        "SelectedMs": selected_ms,
    }
```

- [ ] **Step 4: Run them and verify they pass**

Run: `cd src/Diariz.Worker && python -m pytest tests/test_voiceprint.py -v`
Expected: PASS, 6 tests.

- [ ] **Step 5: Raise the cap and add the stream key**

In `src/Diariz.Worker/config.py`:

```python
    VOICEPRINT_STREAM_KEY = os.getenv("VOICEPRINT_STREAM_KEY", "voiceprint-jobs")
```

and change the cap, keeping the reason in the comment:

```python
    # Cap on pooled audio per speaker. Raised from 30 s so a hand-picked selection is actually used;
    # ECAPA on 120 s vs 30 s costs a rounding error on GPU and the vectors stay comparable with
    # centroids built at 30 s. One cap for both the transcription-time and on-demand paths, so they
    # cannot drift.
    EMBED_MAX_SECONDS = float(os.getenv("EMBED_MAX_SECONDS", "120"))
```

- [ ] **Step 6: Write the failing callback test**

Add to `src/Diariz.Worker/tests/test_callback.py`, following its existing `requests.post` stubbing:

```python
def test_posts_voiceprint_result_with_the_shared_secret(monkeypatch):
    captured = {}
    monkeypatch.setattr(callback.requests, "post", _capture(captured))

    callback.post_voiceprint_result("11111111-1111-1111-1111-111111111111", [0.6, 0.8], 120000, 200000)

    assert captured["url"].endswith("/internal/people/voiceprint-result")
    assert captured["headers"]["X-Worker-Secret"] == config.CALLBACK_SECRET
    # PascalCase: .NET model binding reads these by name.
    assert captured["json"] == {
        "VoiceSampleId": "11111111-1111-1111-1111-111111111111",
        "Embedding": [0.6, 0.8], "UsedMs": 120000, "SelectedMs": 200000,
    }


def test_posts_voiceprint_failure(monkeypatch):
    captured = {}
    monkeypatch.setattr(callback.requests, "post", _capture(captured))

    callback.post_voiceprint_failure("22222222-2222-2222-2222-222222222222", "audio gone")

    assert captured["url"].endswith("/internal/people/voiceprint-failure")
    assert captured["json"] == {"VoiceSampleId": "22222222-2222-2222-2222-222222222222", "Error": "audio gone"}
```

- [ ] **Step 7: Run it, then add the callback functions**

Run: `cd src/Diariz.Worker && python -m pytest tests/test_callback.py -v` -> FAIL,
`AttributeError: module 'callback' has no attribute 'post_voiceprint_result'`.

Then add to `callback.py`, mirroring `post_merge_result`/`post_merge_failure` exactly (same `_HEADERS`,
same timeouts, same `raise_for_status` on the failure path only):

```python
def post_voiceprint_result(voice_sample_id: str, embedding: list, used_ms: int, selected_ms: int) -> None:
    url = f"{config.API_BASE_URL}/internal/people/voiceprint-result"
    body = {"VoiceSampleId": voice_sample_id, "Embedding": embedding,
            "UsedMs": used_ms, "SelectedMs": selected_ms}
    resp = requests.post(url, json=body, headers=_HEADERS, timeout=60)
    resp.raise_for_status()


def post_voiceprint_failure(voice_sample_id: str, error: str) -> None:
    url = f"{config.API_BASE_URL}/internal/people/voiceprint-failure"
    body = {"VoiceSampleId": voice_sample_id, "Error": error}
    with contextlib.suppress(Exception):
        requests.post(url, json=body, headers=_HEADERS, timeout=30).raise_for_status()
```

Re-run: PASS.

- [ ] **Step 8: Write the failing worker-dispatch tests**

Add to `src/Diariz.Worker/tests/test_worker.py`, following its existing fake-Redis pattern:

```python
def test_run_loop_dispatches_a_voiceprint_job(monkeypatch):
    seen = {}
    monkeypatch.setattr(worker, "handle_voiceprint", lambda job: seen.setdefault("job", job))
    r = FakeRedis({config.VOICEPRINT_STREAM_KEY: [("1-1", {"job": json.dumps({"VoiceSampleId": "abc"})})]})

    worker.run_loop(r, keep_going=_once())

    assert seen["job"]["VoiceSampleId"] == "abc"
    assert r.acked == [(config.VOICEPRINT_STREAM_KEY, "1-1")]


def test_handle_voiceprint_reports_failure_and_removes_the_temp_file(monkeypatch, tmp_path):
    # A failed re-embed must not strand the sample as permanently pending, and must not leak the download.
    path = tmp_path / "a.webm"
    path.write_bytes(b"x")
    monkeypatch.setattr(worker.storage, "download", lambda k: str(path))
    monkeypatch.setattr(worker, "_load_audio", _raises(RuntimeError("bad audio")))
    failures = []
    monkeypatch.setattr(worker.callback, "post_voiceprint_failure", lambda i, e: failures.append((i, e)))

    worker.handle_voiceprint({"VoiceSampleId": "abc", "RecordingId": "r", "BlobKey": "k", "Spans": []})

    assert failures and failures[0][0] == "abc"
    assert not path.exists()
```

- [ ] **Step 9: Run them, then add the handler and the third stream**

Run: `cd src/Diariz.Worker && python -m pytest tests/test_worker.py -v` -> FAIL.

Add to `worker.py`:

```python
def handle_voiceprint(job: dict) -> None:
    """Re-embed one voice sample from the spans the user chose. Cheap next to a transcription - no Whisper,
    no pyannote - but it shares this process, so it can queue behind one."""
    sample_id = job["VoiceSampleId"]
    log.info("Re-embedding voice sample %s from %d span(s)", sample_id, len(job.get("Spans") or []))

    path = None
    try:
        with telemetry.transaction("voiceprint-embed"):
            path = storage.download(job["BlobKey"])
            audio = _load_audio(path)
            result = voiceprint.embed_spans(audio, job.get("Spans") or [], pipeline._get_embedder())
            if result is None:
                callback.post_voiceprint_failure(sample_id, "The selected audio is empty.")
                return
            callback.post_voiceprint_result(
                sample_id, result["Embedding"], result["UsedMs"], result["SelectedMs"])
    except Exception as e:  # noqa: BLE001 - report and continue
        log.exception("Voiceprint re-embed failed for sample %s", sample_id)
        telemetry.capture_exception(e)
        callback.post_voiceprint_failure(sample_id, str(e))
    finally:
        if path and os.path.exists(path):
            os.remove(path)
```

with a `_load_audio(path)` seam (`return whisperx.load_audio(path)`) so the test can make loading fail
without touching whisperx.

In `run_loop`, add the third stream to the `xreadgroup` map, to the `reclaim_stale` list, and to the
dispatch:

```python
                {config.STREAM_KEY: ">", config.MERGE_STREAM_KEY: ">",
                 config.VOICEPRINT_STREAM_KEY: ">"}, count=1, block=BLOCK_MS)
```

```python
            resp = [(key, reclaim_stale(r, key))
                    for key in (config.STREAM_KEY, config.MERGE_STREAM_KEY,
                                config.VOICEPRINT_STREAM_KEY)]
```

```python
                    if stream == config.MERGE_STREAM_KEY:
                        handle_merge(job)
                    elif stream == config.VOICEPRINT_STREAM_KEY:
                        handle_voiceprint(job)
                    else:
                        handle(job)
```

and in `main()`, `ensure_group(r, config.VOICEPRINT_STREAM_KEY)` beside the other two, plus the stream name
in the startup log line.

- [ ] **Step 10: Run the whole worker suite and verify it passes**

Run: `cd src/Diariz.Worker && python -m pytest -q`
Expected: PASS, no warnings.

- [ ] **Step 11: Commit**

```bash
git add src/Diariz.Worker/voiceprint.py src/Diariz.Worker/tests/test_voiceprint.py src/Diariz.Worker/config.py src/Diariz.Worker/callback.py src/Diariz.Worker/worker.py src/Diariz.Worker/tests/test_worker.py src/Diariz.Worker/tests/test_callback.py
git commit -m "feat(worker): re-embed a voiceprint from chosen audio spans"
```

---

### Task 14: The voiceprint callback controller

**Files:**
- Create: `src/Diariz.Api/Controllers/WorkerVoiceprintCallbackController.cs`
- Test: `tests/Diariz.Api.Tests/WorkerVoiceprintCallbackTests.cs`

**Interfaces:**
- Consumes: `VoiceprintResult`, `VoiceprintFailure` (Task 12), `IPeopleDirectory.RecomputeVoiceprintAsync`.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Result_WithWrongSecret_IsUnauthorized()
{
    await using var db = TestDb.Create();
    var controller = Build(db, secret: "right", sent: "wrong");
    Assert.IsType<UnauthorizedResult>(
        await controller.Result(new VoiceprintResult(Guid.NewGuid(), [1f, 0f], 1000, 2000)));
}

[Fact]
public async Task Result_StoresTheEmbeddingAndClearsPending()
{
    await using var db = TestDb.Create();
    var (controller, sampleId) = await SeedPendingSampleAsync(db);

    Assert.IsType<NoContentResult>(
        await controller.Result(new VoiceprintResult(sampleId, [0.6f, 0.8f], 120000, 200000)));

    var sample = db.VoiceSamples.Single();
    Assert.Equal(120000, sample.UsedMs);   // no longer pending
    Assert.NotNull(sample.Embedding);
}

[Fact]
public async Task Result_ClearsTheContributingSpeakersStaleFlag()
{
    await using var db = TestDb.Create();
    var (controller, sampleId) = await SeedPendingSampleAsync(db, speakerStale: true);

    await controller.Result(new VoiceprintResult(sampleId, [0.6f, 0.8f], 1000, 1000));

    Assert.False(db.Speakers.Single().EmbeddingStale);
}

[Fact]
public async Task Result_RecomputesThePersonsCentroid()
{
    // The sample's vector changed, so the average of the samples did too. Without this the person's
    // voiceprint silently keeps averaging the old one.
    await using var db = TestDb.Create();
    var directory = new RecordingPeopleDirectory();
    var (controller, sampleId) = await SeedPendingSampleAsync(db, directory: directory);

    await controller.Result(new VoiceprintResult(sampleId, [0.6f, 0.8f], 1000, 1000));

    Assert.Single(directory.Recomputed);
}

[Fact]
public async Task Result_ForAnUnknownSample_IsNotFound()
{
    await using var db = TestDb.Create();
    var (controller, _) = await SeedPendingSampleAsync(db);
    Assert.IsType<NotFoundResult>(
        await controller.Result(new VoiceprintResult(Guid.NewGuid(), [1f], 1, 1)));
}

[Fact]
public async Task Failure_LeavesTheSampleUsableRatherThanPendingForever()
{
    // Pending is derived from usedMs being null. If a failure left it null the row would spin forever
    // with no way for the user to tell a slow job from a dead one.
    await using var db = TestDb.Create();
    var (controller, sampleId) = await SeedPendingSampleAsync(db);

    Assert.IsType<NoContentResult>(await controller.Failure(new VoiceprintFailure(sampleId, "boom")));

    Assert.NotNull(db.VoiceSamples.Single().UsedMs);
}
```

`RecordingPeopleDirectory` is a fake for `IPeopleDirectory` recording the person ids it was asked to
recompute. Put it in `tests/Diariz.Api.TestSupport/Fakes.cs` beside the existing fakes - no mocking library.

- [ ] **Step 2: Run them and verify they fail**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WorkerVoiceprintCallback"`
Expected: FAIL to compile.

- [ ] **Step 3: Write the controller**

Create `src/Diariz.Api/Controllers/WorkerVoiceprintCallbackController.cs`, modelled on
`WorkerMergeCallbackController` (same `X-Worker-Secret` check, same `[Route]` shape, JWT-free):

```csharp
[ApiController]
[Route("internal/people")]
public class WorkerVoiceprintCallbackController : ControllerBase
```

`Result` stores `new Vector(body.Embedding)` on the sample, sets `UsedMs = body.UsedMs`, clears
`EmbeddingStale` on the contributing `Speaker`, saves, then calls
`_people.RecomputeVoiceprintAsync(sample.PersonId)`.

`Failure` records the error and **sets `UsedMs` to 0** so the row stops reading as pending, then returns
`NoContent` - a callback that leaves the sample pending forever would be indistinguishable from a slow job.
Log the error at warning level with the sample id.

Neither method takes a `CancellationToken` from the request: a cancelled token in the catch path is how a
row gets stranded in its in-flight status forever (fixed across the four processors in 0.228.1). Keep the
saves on `CancellationToken.None`.

- [ ] **Step 4: Run them and verify they pass**

Run: `dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WorkerVoiceprintCallback"`
Expected: PASS, 6 tests.

- [ ] **Step 5: Add an integration test for the pgvector write**

The `vector(192)` column is Postgres-only and `Ignore`d under the in-memory provider, so the unit test
above cannot prove the embedding actually persisted. Add
`tests/Diariz.Api.IntegrationTests/VoiceprintCallbackIntegrationTests.cs`: post a result through the real
controller against the fixture's Postgres, then read the sample back in a fresh context and assert the
vector round-tripped and the person's centroid changed.

Run: `dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~VoiceprintCallback"`
Expected: PASS. (Needs Docker.)

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Controllers/WorkerVoiceprintCallbackController.cs tests/Diariz.Api.Tests/WorkerVoiceprintCallbackTests.cs tests/Diariz.Api.TestSupport/Fakes.cs tests/Diariz.Api.IntegrationTests/VoiceprintCallbackIntegrationTests.cs
git commit -m "feat(api): accept the worker's re-embedded voiceprint and recompute the centroid"
```

---

### Task 15: Split `PersonEditor` into a tab shell + Profile tab

**Files:**
- Create: `apps/web/src/components/PersonProfileTab.tsx`
- Modify: `apps/web/src/components/PersonEditor.tsx`
- Modify: `apps/web/src/components/PersonEditor.test.tsx`
- Modify: `apps/web/src/locales/en/people.json`

**Interfaces:**
- Produces, consumed by Task 16: `PersonEditor` renders a two-tab shell and mounts
  `<PersonVoiceprintTab person={person} canManageBiometrics={...} />` for the Voiceprint tab.
  `PersonProfileTab` takes exactly the props `PersonEditor` takes today.

**This task changes no behaviour.** It is a pure move so the voiceprint work in Task 16 lands in its own
file rather than growing a 231-line editor to 500. Do it as its own commit so a reviewer can see that the
diff is a move: the existing `PersonEditor.test.tsx` assertions must pass **unchanged** except for the tab
click needed to reach the fields.

- [ ] **Step 1: Confirm the current tests are green before touching anything**

Run: `cd apps/web && npx vitest run src/components/PersonEditor.test.tsx`
Expected: PASS. Note the count - it must not drop.

- [ ] **Step 2: Write the failing tab test**

Add to `apps/web/src/components/PersonEditor.test.tsx`:

```tsx
it("opens on the Profile tab and can switch to Voiceprint", async () => {
  render(<PersonEditor person={personFixture} canManagePeople onClose={() => {}} />, { wrapper });

  // Profile is the default: the common task is editing a job title, not auditing a biometric.
  expect(screen.getByLabelText(/name/i)).toBeTruthy();

  await userEvent.click(screen.getByRole("tab", { name: /voiceprint/i }));
  expect(screen.queryByLabelText(/job title/i)).toBeNull();
});
```

- [ ] **Step 3: Run it and verify it fails**

Run: `cd apps/web && npx vitest run src/components/PersonEditor.test.tsx`
Expected: FAIL - no element with role `tab`.

- [ ] **Step 4: Move the body into `PersonProfileTab`**

Cut everything from `const [name, setName] = useState(...)` through the returned form JSX out of
`PersonEditor.tsx` into a new `PersonProfileTab.tsx`, keeping the props and the doc comment (including the
note about opt-out being shown-but-disabled while erase is hidden entirely - that reasoning belongs with
the controls it explains). Change nothing else.

- [ ] **Step 5: Make `PersonEditor` the shell**

```tsx
/// Two tabs over one person. Profile is the default because the common task is fixing a job title while
/// reading a transcript; Voiceprint is the audit surface you go looking for.
///
/// The shell owns nothing but the tab state - both tabs read `person` from the caller, so switching tabs
/// never discards a half-typed edit in the other one.
export default function PersonEditor(props: PersonEditorProps) {
  const { t } = useTranslation("people");
  const [tab, setTab] = useState<"profile" | "voiceprint">("profile");
  // ... role="tablist" with two role="tab" buttons, aria-selected, then the active panel
}
```

Add the strings:

```json
"tabProfile": "Profile",
"tabVoiceprint": "Voiceprint"
```

- [ ] **Step 6: Run the tests and verify they pass**

Run: `cd apps/web && npx vitest run src/components/PersonEditor.test.tsx src/components/EditPersonModal.test.tsx`
Expected: PASS, with the same number of assertions as Step 1 plus the new one. A dropped test here means
behaviour moved, not just code.

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/components/PersonEditor.tsx apps/web/src/components/PersonProfileTab.tsx apps/web/src/components/PersonEditor.test.tsx apps/web/src/locales/en/people.json
git commit -m "refactor(web): split PersonEditor into a tab shell and a Profile tab"
```

---

### Task 16: The Voiceprint tab

**Files:**
- Create: `apps/web/src/components/PersonVoiceprintTab.tsx`
- Create: `apps/web/src/components/PersonVoiceprintTab.test.tsx`
- Modify: `apps/web/src/components/PersonEditor.tsx` (mount the tab)
- Modify: `apps/web/src/locales/en/people.json`

**Interfaces:**
- Consumes: `api.getPerson` (extended `VoiceSample` from Task 12), `api.setVoiceSampleSpans`,
  `api.removeVoiceSample`, `api.getRecording` (for a sample's segments).

**Behaviour.** The tab lists each contributing sample - recording name, speaker label, play from `startMs`,
selected-versus-used duration, a stale badge, the existing remove. Expanding one lists that speaker's
segments with tick boxes. Ticking marks the sample dirty; a single **Recompute voiceprint** button sends the
whole selection, so a run of clicks is one job and not fifteen.

**Progress is polled, not pushed.** The client wires only `RecordingStatusChanged` today, so a new hub event
would mean changing `createHub`'s signature and every caller for one modal. A queued sample is `pending`
(server-derived from `spansJson != null && usedMs == null`), and the query refetches every 3s while any
sample is pending - which survives a reload, unlike a client-only flag.

- [ ] **Step 1: Write the failing tests**

```tsx
it("states what was used against what was selected, rather than implying it used it all", async () => {
  // The worker caps pooled audio. Showing only the selection would quietly promise something untrue.
  mockPerson({ samples: [sample({ selectedMs: 252000, usedMs: 120000 })] });
  renderTab();
  expect(await screen.findByText(/using 2:00 of the 4:12 selected/i)).toBeTruthy();
});

it("batches a run of ticks into one recompute", async () => {
  mockPerson({ samples: [sample({ id: "vs1" })] });
  mockRecordingSegments([seg("g1", 0, 1000), seg("g2", 1000, 2000), seg("g3", 2000, 3000)]);
  renderTab();

  await userEvent.click(await screen.findByRole("button", { name: /show segments/i }));
  await userEvent.click(screen.getByRole("checkbox", { name: /g2/ }));
  await userEvent.click(screen.getByRole("checkbox", { name: /g3/ }));
  await userEvent.click(screen.getByRole("button", { name: /recompute voiceprint/i }));

  expect(api.setVoiceSampleSpans).toHaveBeenCalledTimes(1);
  expect(api.setVoiceSampleSpans).toHaveBeenCalledWith("p1", "vs1", [{ startMs: 0, endMs: 1000 }]);
});

it("does not queue anything until Recompute is pressed", async () => {
  mockPerson({ samples: [sample({ id: "vs1" })] });
  mockRecordingSegments([seg("g1", 0, 1000), seg("g2", 1000, 2000)]);
  renderTab();

  await userEvent.click(await screen.findByRole("button", { name: /show segments/i }));
  await userEvent.click(screen.getByRole("checkbox", { name: /g2/ }));

  // Flush a macrotask: asserting immediately would pass before a stray call could have landed.
  await new Promise((r) => setTimeout(r, 0));
  expect(api.setVoiceSampleSpans).not.toHaveBeenCalled();
});

it("shows a sample as pending while its recompute is in flight", async () => {
  mockPerson({ samples: [sample({ pending: true })] });
  renderTab();
  expect(await screen.findByText(/recomputing/i)).toBeTruthy();
});

it("flags a sample whose speaker's audio was re-attributed", async () => {
  mockPerson({ samples: [sample({ stale: true })] });
  renderTab();
  expect(await screen.findByText(/needs recomputing/i)).toBeTruthy();
});

it("explains and disables selection for someone who opted out", async () => {
  mockPerson({ person: { ...personFixture, voiceprintOptOut: true }, samples: [] });
  renderTab();
  expect(await screen.findByText(/opted out of voice-printing/i)).toBeTruthy();
});

it("does not offer selection without permission to manage biometrics", async () => {
  mockPerson({ person: { ...personFixture, canManageBiometrics: false }, samples: [sample({})] });
  renderTab();
  // The samples are still listed - someone needs to be able to see what a voiceprint learned from even
  // if they may not change it - but the controls that would change it are not offered.
  expect(await screen.findByText(/Standup/)).toBeTruthy();
  expect(screen.queryByRole("button", { name: /recompute voiceprint/i })).toBeNull();
});
```

- [ ] **Step 2: Run them and verify they fail**

Run: `cd apps/web && npx vitest run src/components/PersonVoiceprintTab.test.tsx`
Expected: FAIL - `Failed to resolve import "./PersonVoiceprintTab"`.

- [ ] **Step 3: Write the component**

Key points, in the order they matter:

- `useQuery({ queryKey: ["person", person.id], queryFn: () => api.getPerson(person.id), refetchInterval: (q) => q.state.data?.samples.some((s) => s.pending) ? 3000 : false })`.
- Per sample: recording name, speaker label, a play button seeking to `startMs`, and the duration line
  rendered from `selectedMs`/`usedMs`. When `usedMs < selectedMs`, render
  `t("people:voiceprintUsingOf", { used, selected })`. When `usedMs == null`, render
  `t("people:voiceprintRecomputing")`. When `stale`, render `t("people:voiceprintNeedsRecompute")`.
- **Show segments** expands the sample, fetching the contributing recording once
  (`queryKey: ["recording", sample.recordingId]`) and filtering its segments to `sample.speakerLabel`. Each
  gets a checkbox labelled by its text, ticked when the segment's span is covered by the sample's spans
  (empty spans = everything ticked, matching "the whole speaker").
- Ticking updates local state only. **Recompute voiceprint** sends `api.setVoiceSampleSpans(person.id,
  sample.id, spans)` once, where `spans` is every ticked segment's `{ startMs, endMs }` - the server merges
  adjacent ones. If everything is ticked, send `[]` so the sample reverts to "the whole speaker" rather
  than pinning a snapshot of today's boundaries.
- Gate every mutating control on `person.canManageBiometrics` - **the server's answer**, never recomputed
  here, or the two drift the first time either side is edited.
- `person.voiceprintOptOut` replaces the list with an explanation.

Add the strings (plain hyphens only):

```json
"voiceprintUsingOf": "Using {{used}} of the {{selected}} selected",
"voiceprintRecomputing": "Recomputing...",
"voiceprintNeedsRecompute": "Needs recomputing - this speaker's audio changed",
"voiceprintShowSegments": "Show segments",
"voiceprintHideSegments": "Hide segments",
"voiceprintRecompute": "Recompute voiceprint",
"voiceprintOptedOut": "This person has opted out of voice-printing, so there is nothing to select.",
"voiceprintNoSamples": "No recordings train this voiceprint yet.",
"errRecomputeFailed": "Could not queue the recompute."
```

- [ ] **Step 4: Run them and verify they pass**

Run: `cd apps/web && npx vitest run src/components/PersonVoiceprintTab.test.tsx`
Expected: PASS, 7 tests.

- [ ] **Step 5: Mount it in both places**

In `PersonEditor.tsx`, render `<PersonVoiceprintTab />` for the Voiceprint tab. `EditPersonModal` gets it
for free through `PersonEditor` - it is the moment you notice a voiceprint is wrong. Confirm
`showDestructiveActions={false}` still hides erase and delete there:

Run: `cd apps/web && npx vitest run src/components/EditPersonModal.test.tsx`
Expected: PASS.

- [ ] **Step 6: Run the whole web suite and build**

Run: `cd apps/web && npm run build && npm test`
Expected: PASS, no new warnings.

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/components/PersonVoiceprintTab.tsx apps/web/src/components/PersonVoiceprintTab.test.tsx apps/web/src/components/PersonEditor.tsx apps/web/src/locales/en/people.json
git commit -m "feat(web): choose which audio trains a person's voiceprint"
```

- [ ] **Step 8: Verify the whole flow in the running app**

jsdom computes no geometry and never runs a real job. With the stack up: open a person, expand a sample,
untick some segments, press Recompute, and confirm the row goes to Recomputing and then reports a new
"using X of Y" - which means the job reached the worker, the worker sliced the spans, and the callback
landed. Then split a mixed segment (Task 10), reassign the new half, and confirm the affected speakers show
"Needs recomputing".

---

### Task 17: Docs, release notes, version bump

**Files:**
- Modify: `version.json` and all five mirrors
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `README.md`, `docs/features.md`
- Modify: `docs/Overall_Synopsis_of_Platform.md`, `docs/Data_Schema.md`
- Modify: `apps/web/src/content/help/en/people-directory.md`, `merging-people.md`,
  `transcription-and-speakers.md`
- Modify: `deploy/.env.example`, `deploy/docker-compose.yml` (only if the new stream key needs an env knob)

**Interfaces:** none - this task ships what the others built.

This is a **functional enhancement**, so the bump is **Minor +1, Build reset**: `0.248.2` -> `0.249.0`.

- [ ] **Step 1: Bump the version and all five mirrors**

`version.json` is canonical. The mirrors are `apps/web/package.json`, `apps/web/package-lock.json` (**two
places** in that file), `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj` (`<Version>`), and
`integrations/n8n-nodes-diariz/package.json`. CLAUDE.md names four; the web lockfile is the fifth and
`versionMirrors.test.ts` fails the build on it.

```bash
cd apps/web && npx vitest run src/lib/versionMirrors.test.ts
```

Expected: PASS. It exists because the n8n node silently sat at `0.1.0` for ~70 releases, and an npm version
cannot be corrected once published.

- [ ] **Step 2: Confirm the PR number, do not guess it**

The `pr:` field must be written before `gh pr create` exists to report the real number, and "last + 1" is
wrong often enough to matter - Dependabot PRs and issues share the sequence. Check:

```bash
gh pr list --state all --limit 1 --json number
gh issue list --state all --limit 1 --json number
```

Take one past the higher of the two. No test catches a wrong number.

- [ ] **Step 3: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts` with `version: "0.249.0"`, today's date, the
confirmed `pr`, a headline, a **PR-level prose `summary`** a user can act on, and `added`/`changed`/`fixed`
bullets. Cover, in the user's terms: telling two same-named people apart by their account; splitting a
segment that contains a second voice and moving that part to another speaker; choosing which recordings and
segments train a voiceprint; and that word timings only exist for recordings transcribed from this release
onward, so older recordings must be re-transcribed before their segments can be split.

Run: `cd apps/web && npx vitest run src/lib/releases.test.ts`
Expected: PASS - it asserts `RELEASES[0].version` equals `version.json`.

- [ ] **Step 4: Update the About-box `CAPABILITIES` table**

The app's scope changed, so add or edit a row in the two-column markdown table in `releases.ts`. One
concise line - do not reintroduce prose. No new third-party library or model is introduced (ECAPA and
whisperx were already there), so `AboutModal.tsx`'s disclaimers need no edit.

- [ ] **Step 5: Update the README Features table and `docs/features.md` together**

Never one without the other. A row in the README's two-column table, and the matching full prose bullet in
`docs/features.md`. Both should say that voiceprint training is selectable per recording and per segment,
and that a segment can be split and reassigned.

- [ ] **Step 6: Update `docs/Overall_Synopsis_of_Platform.md`**

A new cross-boundary contract landed. Record: the third Redis stream `voiceprint-jobs` (group `workers`),
its job payload and both callback routes under `internal/people/`, the raised `EMBED_MAX_SECONDS`, and that
the worker now persists word timings. Keep it in the same register as the existing entries.

- [ ] **Step 7: Update `docs/Data_Schema.md`**

Four columns across three migrations: `Segments.WordsJson` (jsonb, null), `ProfileContributions.SpansJson`
(jsonb, null - **null means the whole speaker**), `ProfileContributions.UsedMs` (integer, null - also the
pending marker), `Speakers.EmbeddingStale` (boolean, not null, default false). Add the three migration rows
to the migration-history table. Note in the table that `VoiceSample` staleness is **derived** by joining to
the speaker and is deliberately not a column.

All four additions are nullable-or-defaulted and forward-restore-safe, so **`MaintenanceController.CurrentFormat`
does not move.** Say so explicitly in the PR body, since a reviewer will ask.

- [ ] **Step 8: Update the three help articles**

Only where **behaviour a user relies on** changed - these are task-oriented prose, not an inventory, and are
deliberately not kept line-for-line with the tables above.

- `people-directory.md`: how a person is identified by their account, and what the Voiceprint tab does.
- `merging-people.md`: that the refusal now names the two accounts.
- `transcription-and-speakers.md`: splitting a segment with a second voice, and that it needs word timings.

ASCII only, and keep each article's `title` / `summary` / `group` / `order` front matter. The `summary` is
what the contextual `?` popover shows - two or three sentences.

Run: `cd apps/web && npx vitest run src/content/help/helpContent.test.ts`
Expected: PASS. It fails the build if a `<HelpButton topic="...">` points at an article that does not exist.

- [ ] **Step 9: Run everything**

```bash
dotnet build Diariz.slnx
dotnet test tests/Diariz.Api.Tests
dotnet test tests/Diariz.Api.IntegrationTests
cd src/Diariz.Worker && python -m pytest -q && cd ../..
cd apps/web && npm run build && npm test && cd ../..
cd integrations/n8n-nodes-diariz && npm run generate && npm run build && cd ../..
```

Expected: all PASS, with no errors or warnings. A passing run here is pristine - if `npm run generate`
produces a diff, commit it; `generated/index.ts` does not self-heal and a stale one reds the n8n check.

- [ ] **Step 10: Check for em dashes before committing**

```bash
python -c "import io,glob;[print(f) for f in glob.glob('apps/web/src/locales/en/*.json')+['apps/web/src/lib/releases.ts','README.md'] if any(c in io.open(f,encoding='utf-8').read() for c in '—–')]"
```

Expected: no output. Decode UTF-8 explicitly like this - piping `git diff` into python decodes cp1252 on
this machine and reports a false zero.

- [ ] **Step 11: Commit**

```bash
git add version.json apps/web/package.json apps/web/package-lock.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts README.md docs apps/web/src/content/help
git commit -m "docs: release 0.249.0 - voiceprint selection, segment split, account identity"
```

- [ ] **Step 12: Push and open the PR**

```bash
git push -u origin feat/voiceprint-management
```

Then `gh pr create`. The body must state:

- What shipped, in the user's terms.
- **Deployment surface: server redeploy plus a worker image redeploy** (new Redis stream and new job
  handler). **No desktop release** - nothing under `apps/desktop/src`, `apps/desktop/build`,
  `electron-builder.config.js` or desktop dependencies is touched; the lockstep version bump to
  `apps/desktop/package.json` alone does not require one.
- **No `CurrentFormat` bump**, and why: all four schema additions are nullable or defaulted, so an older
  backup restores cleanly.
- The known limitation: word timings exist only from this release onward, so segments in older recordings
  cannot be split until those recordings are re-transcribed. The UI says so where the control is disabled.

No closing keyword: this is a feature arc, not a fix, so no issue was opened for it.

---

## Self-review notes

Checked against the spec, section by section.

- **Section A** -> Tasks 1-2. All three surfaces covered; the rejected company/sample-count fallback is
  absent, as agreed.
- **Section B** -> Tasks 3-10. Word storage, the worker, merge concatenation, `hasWords`, the three
  endpoints, the pure split helper, staleness, and the web editor.
- **Section C** -> Tasks 11-16. Spans not ids, the row keeping its meaning, reconciliation, the third
  stream, the cap, the tab.
- **Release checklist** -> Task 17, all nine items.

Two deviations from the spec, both discovered while reading the code, both already folded back into the
spec document:

1. **Three migrations, not one.** Tasks 4, 9 and 11 each add columns and each must be independently
   testable. Functionally identical and still forward-restore-safe.
2. **`VoiceSample.UsedMs` is a fourth column.** The spec's three-column list did not have it. It is needed
   regardless, to render "using 1:20 of the 4:12 selected", and it doubles as the pending marker so a
   recompute in flight survives a page reload.

Type consistency verified across tasks: `SegmentWord(W, S, E)` (Task 4) is used unchanged in Tasks 5, 6, 7,
8; `VoiceprintSpan(StartMs, EndMs)` (Task 11) in Tasks 12, 13, 16; `SegmentWords.Serialize/Parse` and
`VoiceprintSpans.Serialize/Parse/TotalMs/Coverage/FromSegments` are named identically everywhere they
appear; `EnqueueVoiceprintAsync` matches between the interface, the implementation and the fake.
