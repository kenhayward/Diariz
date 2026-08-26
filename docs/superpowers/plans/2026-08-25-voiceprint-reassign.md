# Reassign from the Voiceprint tab - implementation plan (PR 3 of 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan
> task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a recording behind someone's voiceprint turns out to be somebody else, fix it where you
found it - without opening the transcript and hunting for the speaker.

**Architecture:** Reuse `SpeakerAssign`, the typeahead the transcript and Speakers tab already use, on
each attribution row. No new picker, and no new endpoints: its three handlers map onto
`api.assignSpeaker`, `api.markMultiSpeaker` and `api.createPerson`, all of which the API already
gates on **recording ownership** - which is exactly what `canReassign` reports.

**Tech Stack:** React 19 + TS + Vite + Tailwind v4, vitest + @testing-library/react.

**Closes:** the last part of #622. Design:
`docs/superpowers/specs/2026-08-25-voiceprint-review-surface-design.md`.

**Depends on:** #625 (`canReassign` on the DTO) and #627 (the merged list this row lives in). This
branch is cut from #627, so it must be **rebased onto main once #627 merges** before its PR is opened.

## Global Constraints

- **TDD.** Failing test first, watch it fail for the stated reason, then the minimal code.
- **Mutation-verify every new test** and quote the failure.
- **No em or en dashes in user-facing text.** Plain `-`.
- **Four locale catalogs** (`en`, `de`, `es`, `fr`) with **key parity** enforced by `locales.test.ts`.
  `de` is written without accents in these files; `es` and `fr` use them.
- **`npm run build` runs `tsc` with test files excluded**, so a required prop added to a component
  will not fail the build from its harness. Update harnesses by hand and run `npm test`.
- **`fireEvent.click` fires on a disabled control**; `userEvent` does not. Use `userEvent` where being
  unavailable is the point.
- **No jest-dom.** Plain assertions only.
- **Never `git add -A`.** Stage explicit paths; check `git status` for `??`.
- **Never put production data in the repo** - invent names for fixtures.

## The one design decision worth stating

The spec called this a **"Not this person..."** button opening a picker. It is instead the
**`SpeakerAssign` typeahead already used on every transcript row and the Speakers tab**, with the
person's own name on the trigger.

That is a deliberate deviation. A bespoke button would be a second way to do a thing the app already
does one way, and `SpeakerAssign` brings four behaviours a new control would have to reinvent:
search against the **ungated** `/api/people/search` (so it works without Manage people), a **Create**
row for someone not yet in the directory, **Multiple speakers** - which matters here, because
overlapping speech is one of the commonest reasons a recording sounds unlike a person's others - and
**Unassign**. The wording changes; the capability is a superset of what was specified.

---

### Task 1: Reassign on the row

**Files:**
- Modify: `apps/web/src/components/PersonAttributionRow.tsx`
- Modify: `apps/web/src/components/PersonVoiceprintTab.test.tsx`
- Modify: all four `apps/web/src/locales/*/people.json`

**Interfaces:**
- Consumes: `attribution.canReassign` (from #625), `api.assignSpeaker`, `api.markMultiSpeaker`,
  `api.createPerson`, `api.searchPeople`.
- Produces: nothing new. `onChanged` already exists and re-reads both queries.

- [ ] **Step 1: Write the failing tests.**

1. **Reassigning calls through with this recording and speaker label.** Open the typeahead, type a
   name, pick the match, and assert `api.assignSpeaker` was called with
   `(recordingId, speakerLabel, thatPersonId)`. The label matters: the endpoint is keyed on it, and
   passing the speaker **id** instead would 404 in a way no type would catch.
2. **Unassign passes null.** The footer's Unassigned row calls `assignSpeaker(recordingId, label, null)`.
3. **It is absent without ownership.** With `canReassign: false` the control does not render - assert
   absence, not a disabled attribute. `ManageVoiceprints` lets someone listen for assessment; the API
   refuses the write regardless, so offering it would be a control that always fails.
4. **The list re-reads after a reassign**, or the row goes on claiming the old person until the modal
   is reopened. Assert `api.getPersonAttributions` was called again.

- [ ] **Step 2: Run them and watch them fail.** Quote the failures.

- [ ] **Step 3: Implement.** Render `SpeakerAssign` in the row's control strip when
  `attribution.canReassign`, with `subtle`, a narrow `width`, `displayName` set to the person's name,
  and `isMulti={false}` (a multi-speaker row is never listed as still linked). Route all three
  handlers through the existing `run(...)` helper so a failure shows the row's error line and
  `onChanged` fires on success.

  `SpeakerAssign` reads the `workspace` namespace; `PersonAttributionRow` already loads
  `["people", "common"]`, so add `workspace` to its `useTranslation` call or the picker's own strings
  fall back to keys.

- [ ] **Step 4: Green, then mutation-verify.** Pass the speaker **id** instead of the label and
  confirm test 1 fails. Drop the `canReassign` guard and confirm test 3 fails.

- [ ] **Step 5: Commit.**

---

### Task 2: Release

**Version: `0.253.0` -> `0.254.0`.** A functional enhancement, so Minor +1 and Build 0.

- [ ] **Step 1: Bump `version.json` and all five mirrors** - `apps/web/package.json`,
  `apps/web/package-lock.json` (**two** places), `apps/desktop/package.json`,
  `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`.

- [ ] **Step 2: `RELEASES[0]`** in `apps/web/src/lib/releases.ts`. Build the `\n` escapes with
  `chr(92)` if writing via a script - a bash heredoc collapses them and breaks the string literal.
  **Confirm the PR number** with `gh pr list` rather than assuming last + 1: issues share the
  sequence, and no test catches a wrong one.

- [ ] **Step 3: Scope changed**, so update all three inventories in lockstep - the `CAPABILITIES`
  row, the **README Features** row, and the **`docs/features.md`** prose.

- [ ] **Step 4: `apps/web/src/content/help/en/people-directory.md`** - what to do when a recording
  turns out to be somebody else. This is behaviour a user relies on, which is the bar for touching a
  help article.

- [ ] **Step 5: No schema or API change** - no `Data_Schema.md` edit, no `CurrentFormat` bump, no
  OpenAPI snapshot or n8n regeneration. Say so in the PR.

- [ ] **Step 6: Full green** - `npm run build && npm test` in `apps/web`. **No edits after the last
  green run.**

- [ ] **Step 7: Rebase onto main once #627 has merged**, then push and open the PR with
  `Fixes #622` only if #627 did not already close it - otherwise reference it. Deployment surface:
  **server redeploy only**.
