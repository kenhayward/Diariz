# Voiceprint review surface - implementation plan (PR 2 of 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan
> task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One list per person instead of two that describe the same recordings differently, warnings
that sit on the row rather than covering the card, and numbers that read the way they look.

**Architecture:** `PersonVoiceprintTab` additionally reads `api.getPersonDiagnostics` and joins on
`speakerId` - present on both DTOs, unlike `voiceSampleId` which is null for an attribution that
trains nothing. No API change. `PersonDiagnosticsTab` is deleted. In the directory, both hero panels
become a warning line per row plus a **Needs review** filter chip.

**Tech Stack:** React 19 + TS + Vite + Tailwind v4, vitest + @testing-library/react.

**Closes:** #622. Design: `docs/superpowers/specs/2026-08-25-voiceprint-review-surface-design.md`.

## Global Constraints

- **TDD.** Failing test first, watch it fail for the stated reason, then the minimal code.
- **Mutation-verify every new test.** Invert or delete the thing it covers and quote the failure. A
  test that cannot fail is the dominant defect class here.
- **No em or en dashes in user-facing text** - UI strings, locale catalogs, release notes. Plain `-`.
- **Four locale catalogs** (`en`, `de`, `es`, `fr`) and `locales.test.ts` enforces **key parity**.
  Every key added or removed must be done in all four. `de` is written without accents in this file;
  `es` and `fr` use them. Match each file's own convention.
- **`npm run build` runs `tsc` with test files excluded**, so a required prop added to a component
  will **not** fail the build from its test harness. Update harnesses by hand and run `npm test`.
- **`fireEvent.click` fires on a disabled control**; `userEvent` does not. Both are installed. Use
  `userEvent` for anything where being disabled is the point.
- **`vi.waitFor` checks once immediately**, so a "must not appear" assertion passes before the thing
  could have appeared. Flush a macrotask tick and assert synchronously instead.
- **No jest-dom.** 0 of 280+ web test files use its matchers. Plain assertions only - do not add the
  dependency or edit `test-setup.ts`.
- **jsdom computes no geometry.** Nothing here can assert that the panels stopped covering the card.
  That is verified in the running app, not in a test.
- **Never `git add -A`.** Stage explicit paths; check `git status` for `??` before committing.
- **Never put production data in the repo** - no real names, emails, company names or recording
  titles in fixtures, comments, docs, commit messages or the PR. Invent names.

---

### Task 1: Similarity, and the verdict in words

**Files:**
- Create: `apps/web/src/lib/voiceprintVerdict.ts`
- Test: `apps/web/src/lib/voiceprintVerdict.test.ts`

**Interfaces:**
- Produces:
  - `similarityPercent(distance: number): number` - `1 - distance` as a percentage, clamped to 0.
  - `type RowVerdict = "alone" | "unlinked" | "variant" | "core" | "only"`
  - `rowVerdict(d: SampleDiagnosis | undefined, stillLinked: boolean): RowVerdict`
  - `sortKey(v: RowVerdict): number` - what the list orders on.

The whole point of extracting this: the flip from distance to similarity, and the ordering, are the
two things most worth a test and the two least worth a DOM to test them through.

- [ ] **Step 1: Write the failing test**

```ts
import { describe, expect, it } from "vitest";
import { rowVerdict, similarityPercent, sortKey } from "./voiceprintVerdict";

describe("similarityPercent", () => {
  it("reads high when the voices are alike", () => {
    // The bug this fixes: the tab printed the cosine *distance* under a percentage label, so the
    // worst row on the screen showed the largest and most reassuring number.
    expect(similarityPercent(0.18)).toBe(82);
    expect(similarityPercent(0.82)).toBe(18);
  });

  it("never goes below zero", () => {
    // Cosine distance runs to 2 for opposed vectors. "-13% similar" is not a thing to show anyone.
    expect(similarityPercent(1.4)).toBe(0);
  });
});
```

Plus, for `rowVerdict`: an unlinked row is `"unlinked"` **whatever its diagnosis says**, because that
is a fact about the link and not a judgement about the voice; a row with no diagnosis is `"only"`;
and the three server verdicts map straight through. For `sortKey`: `alone` before `unlinked` before
everything else, so the rows worth acting on are at the top.

- [ ] **Step 2: Run it and watch it fail** - `npx vitest run src/lib/voiceprintVerdict.test.ts`.
  Expected: module not found.

- [ ] **Step 3: Implement.** Keep it pure - no i18n, no React. The caller maps a `RowVerdict` to a
  locale key, so the ordering and the arithmetic stay testable without a translation catalogue.

- [ ] **Step 4: Green, then mutation-verify.** Drop the `Math.max(0, ...)` and confirm the clamp test
  fails. Return `1` from `sortKey` unconditionally and confirm the ordering test fails.

- [ ] **Step 5: Commit.**

---

### Task 2: The Voiceprint tab absorbs Diagnostics

**Files:**
- Modify: `apps/web/src/components/PersonVoiceprintTab.tsx`
- Modify: `apps/web/src/components/PersonAttributionRow.tsx`
- Modify: `apps/web/src/components/PersonVoiceprintTab.test.tsx`
- Modify: all four `apps/web/src/locales/*/people.json`

**Interfaces:**
- Consumes: Task 1's helpers; `api.getPersonDiagnostics` (already exists, gated on `managePeople`).
- Produces: `PersonAttributionRow` gains an optional `diagnosis?: SampleDiagnosis` prop.

- [ ] **Step 1: Write the failing tests** in `PersonVoiceprintTab.test.tsx`:

1. **The header describes the list beneath it.** Two attributions, one training, diagnostics saying
   one is `Alone` - the header reads `Trained on 1 of 2 recordings` and `1 sounds unlike the rest`.
   This is the contradiction being fixed: the old header counted only outliers while the list showed
   everything, so "5 resemble none of the others" sat above rows saying "Matches the others".
2. **Outliers sort to the top**, whatever order the server returned. In the live report the one row
   that mattered was third.
3. **A distance renders as similarity** - a `0.82` distance shows `18`, not `82`.
4. **The "only ones worth checking" toggle** hides `Core` rows and keeps `Alone` ones, and is absent
   when there is nothing to check.
5. **One recording says so** rather than showing a comparison it cannot make.

- [ ] **Step 2: Run them and watch them fail.** Quote the failures.

- [ ] **Step 3: Implement.** In `PersonVoiceprintTab`, add the diagnostics query (`enabled:
  !person.voiceprintOptOut`), build `Map<speakerId, SampleDiagnosis>`, sort with `sortKey`, render
  the header, and hold the filter in `useState`. Pass `diagnosis` into each row. In
  `PersonAttributionRow`, render the verdict chip and the two similarity figures.

  The existing `attributionUnlinked` badge from PR 1 stays and takes precedence - `rowVerdict`
  already returns `"unlinked"` for it, so the two cannot both show.

- [ ] **Step 4: Green, then mutation-verify** the sort (return a constant key) and the header count
  (count all rows rather than training ones).

- [ ] **Step 5: Commit.**

---

### Task 3: Delete the Diagnostics tab

**Files:**
- Delete: `apps/web/src/components/PersonDiagnosticsTab.tsx`, `PersonDiagnosticsTab.test.tsx`
- Modify: `apps/web/src/components/PersonEditor.tsx`, `PersonEditor.test.tsx`
- Modify: all four `people.json` (drop `tabDiagnostics` and every `diag*` key)

Two tabs listing the same recordings is what made the report non-actionable. With Task 2 done, the
third tab is a second view of a list that now carries its own verdicts.

- [ ] **Step 1: Update `PersonEditor.test.tsx` first** - the two deferred-mount tests name the
  Diagnostics tab. Replace them with one asserting the editor offers exactly **Profile** and
  **Voiceprint**, and that no diagnostics fetch happens from the editor shell. Run it and watch it
  fail (the third tab is still there).

- [ ] **Step 2: Implement** - narrow the `tab`/`opened` unions to `"profile" | "voiceprint"`, drop
  the third `tabButton` and its panel, remove the import.

- [ ] **Step 3: Delete the component and its test**, remove the dead locale keys from all four
  catalogs, and run `npx vitest run src/locales` to confirm key parity still holds.

- [ ] **Step 4: Commit.**

---

### Task 4: Play voice is hidden, not disabled, and stops on collapse

**Files:**
- Modify: `apps/web/src/components/PersonAttributionRow.tsx`
- Modify: `apps/web/src/components/PersonVoiceprintTab.test.tsx`

Reported directly: the button greys out until the segments are expanded, which reads as broken rather
than unavailable - and collapsing the list mid-playback leaves it saying **Stop** while audio
continues.

- [ ] **Step 1: Write the failing tests.**

1. The button is **absent** before the segments load, not present-and-disabled. Assert on
   `queryByRole("button", { name: "Play voice" })` being null - a disabled-attribute assertion would
   pass against the current code.
2. Collapsing the segment list while a clip is playing calls `onStop`. Drive it with `userEvent`.

- [ ] **Step 2: Run them and watch them fail.**

- [ ] **Step 3: Implement.** Replace the `disabled` prop with a conditional render, and give the
  expand toggle a handler that stops playback when it is collapsing and this row owns the audio.

- [ ] **Step 4: Green, then mutation-verify** - restore `disabled` and confirm test 1 fails; drop the
  stop-on-collapse call and confirm test 2 fails.

- [ ] **Step 5: Commit.**

---

### Task 5: The directory says it on the row

**Files:**
- Modify: `apps/web/src/components/PeopleModal.tsx`, `PeopleModal.test.tsx`
- Modify: all four `people.json`

Both hero panels are removed. A person with something to say gets an amber line under their name;
the **actions** move to a slim strip above the editor, so they follow the person you opened.

**The behaviour change to be deliberate about:** merging now needs the person selected first. Today
"Review and merge" is reachable without selecting anyone. That is the cost of not covering the card,
and it is one click. The existing duplicate tests select a person first as a result.

- [ ] **Step 1: Write the failing tests.**

1. No hero panel: with both a duplicate and an unhealthy voiceprint reported, neither
   `Voiceprints worth checking` nor `Possible duplicates` appears as a heading.
2. The person's row carries `2 of 5 recordings sound different` and `Possible duplicate`.
3. A **Needs review** filter chip narrows the list to people with a warning.
4. Selecting a flagged person shows the merge strip with **Review and merge**.
5. Dismissing hides that person's warnings for the sitting, and reopening the modal brings them back
   (matching how duplicate dismissal already behaves - keyed on the person, not the group index, so a
   merge elsewhere cannot reorder it onto the wrong row).

- [ ] **Step 2: Run them and watch them fail.**

- [ ] **Step 3: Implement.** Remove both banner blocks. Build
  `Map<personId, {aloneCount, sampleCount}>` from `getDirectoryDiagnostics` and a
  `Set<personId>` from `findPersonDuplicates`. Render the warning line inside the existing row button
  (it is already a `<button>`, so the warnings are text, not controls - nesting a button inside a
  button is invalid). Add the filter chip and the editor strip.

- [ ] **Step 4: Update the existing duplicate tests** to select the person first, and mutation-verify
  the new ones.

- [ ] **Step 5: Commit.**

---

### Task 6: Release

**Version: `0.252.1` -> `0.253.0`.** A functional enhancement (a filter, a restructured surface, a
tab removed), so Minor +1 and Build 0.

- [ ] **Step 1: Bump `version.json` and all five mirrors** - `apps/web/package.json`,
  `apps/web/package-lock.json` (**two** places), `apps/desktop/package.json`,
  `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`.

- [ ] **Step 2: `RELEASES[0]`** in `apps/web/src/lib/releases.ts`. Build the `\n` escapes with
  `chr(92)` if writing via a script - a bash heredoc collapses them into real newlines and breaks the
  string literal.

- [ ] **Step 3: The scope did change**, so unlike PR 1 this updates all three inventories in
  lockstep: the **`CAPABILITIES`** row in `releases.ts` (the Diagnostics tab no longer exists as a
  tab), the **README Features** row, and the **`docs/features.md`** prose. Never one without the
  others.

- [ ] **Step 4: `apps/web/src/content/help/en/people-directory.md`** - it describes the Diagnostics
  tab as a separate place. Rewrite that section for one list, and check every `<HelpButton topic>`
  still resolves (`helpContent.test.ts` fails the build if not).

- [ ] **Step 5: No `docs/Data_Schema.md` change** (no schema change) and no OpenAPI or n8n
  regeneration (no API change). Say so in the PR.

- [ ] **Step 6: Full green** - `npm run build && npm test` in `apps/web`, and `dotnet build
  Diariz.slnx && dotnet test` since `PersonAttributionDto` is read by these components. **No edits
  after the last green run.**

- [ ] **Step 7: Push and open the PR** with `Fixes #622` on its own line and the deployment surface:
  **server redeploy only**.
