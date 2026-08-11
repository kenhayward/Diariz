# People Modal Refreshes The Recording Behind It - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Closing the People directory modal refreshes the recording behind it, so a person edited there stops showing stale details on the Speakers panel.

**Architecture:** The Speakers panel renders from a snapshot of each person carried inside the recording payload (`["recording", id]`), not from the People directory. `PersonEditor` invalidates only `["people"]`, so edits made through the directory never reach that snapshot. `PeopleModal` gains a `useEffect` cleanup that invalidates the `["recording"]` query prefix on unmount - one hook covering all four mutations the modal offers (save, delete, erase voiceprint, merge) from both ways in (the Speakers toolbar and the account menu).

**Tech Stack:** React 19 + TypeScript, TanStack Query v5, Vitest + @testing-library/react (jsdom).

**Spec:** `docs/superpowers/specs/2026-08-11-people-modal-refreshes-speakers-design.md`

## Global Constraints

- **TDD is required.** Write the failing test first, run it, watch it fail for the right reason, then write the minimal code. This is a bug fix, so the test must reproduce the bug (red) before the fix (green).
- **Keep test output pristine** - a passing run has no errors or warnings.
- **No em/en dashes in user-facing text.** Use a plain hyphen `-` in release notes and any UI string. (Code comments and internal docs are unaffected.)
- **Never `git add -A` in this repo.** Stage explicit paths only.
- **Never commit or push to `main`.** Work happens on the branch `fix/people-modal-refreshes-speakers`, which already exists and already holds the spec commit.
- **Version bump this PR:** `0.205.1` -> `0.205.2` (a fix, so Build +1). It must land in `version.json` and all four mirrors, or `versionMirrors.test.ts` fails the build.
- **The PR number for the release entry is `506`.** The highest existing issue/PR number is 505; issues and PRs share one sequence, so confirm with `gh pr view` after opening the PR and correct the entry if it differs.
- All commands below run from the repo root unless the step says otherwise.

---

### Task 1: The refresh, driven by a failing test

**Files:**
- Modify: `apps/web/src/components/PeopleModal.tsx` (imports on line 1; the `qc` handle already exists on line 24)
- Test: `apps/web/src/pages/RecordingDetail.test.tsx` (add to the `api` mock factory at lines 48-108; add the test at the end of the first `describe("RecordingDetail")` block, immediately after the existing pencil-path test that ends on line 1257)

**Interfaces:**
- Consumes: `useQueryClient()` from `@tanstack/react-query`, already called in `PeopleModal` as `const qc = useQueryClient()` (line 24). `useEffect` is already imported on line 1.
- Produces: no new exports. The observable contract is "unmounting `PeopleModal` invalidates the `["recording"]` query prefix".

**Why the test lives in `RecordingDetail.test.tsx` and not `PeopleModal.test.tsx`:** the thing that can break is the *wiring* between the modal and the recording query. A test in `PeopleModal.test.tsx` could only spy on `invalidateQueries` and assert it was called - which passes even if the recording never refetches or renders anything new. Asserting the speaker row's visible text after close is what makes this test able to fail.

- [ ] **Step 1: Add the two api methods `PeopleModal` calls on mount to the test file's mock**

In `apps/web/src/pages/RecordingDetail.test.tsx`, inside the `vi.mock("../lib/api", ...)` factory (the object starting at line 49), add these two lines next to the existing `searchPeople: vi.fn(),` entry:

```ts
    listPeople: vi.fn(),
    findPersonDuplicates: vi.fn(),
```

`PeopleModal` calls both on mount. Without them the modal throws "api.listPeople is not a function" and the test fails for the wrong reason.

- [ ] **Step 2: Write the failing test**

Append this test to the end of the first `describe("RecordingDetail")` block - directly after the existing test that closes on line 1257 (`await waitFor(() => expect(api.getRecording).toHaveBeenCalledTimes(2));` followed by `});`), and before that describe block's own closing `});`.

```tsx
  /// The People directory opens over the Speakers panel from its toolbar, and can change everything the
  /// speaker rows show about a person - job title, company, internal or external. Those details are a
  /// snapshot inside this recording's payload, so without a refresh when the modal closes the row keeps the
  /// details it was first rendered with, and a correct edit reads as one that did not save.
  ///
  /// This asserts the row's visible text, not just that a refetch happened: counting getRecording calls
  /// would pass against a page that refetched and rendered nothing new.
  it("refreshes the speaker rows when the People directory closes, so an edit made there is visible", async () => {
    const speaker = {
      label: "SPEAKER_00", displayName: "Lizzie Mcneil", personId: "p1", title: null,
      companyName: null, email: null, phone: null, isInternal: false,
      identifiedAuto: true, isMultiSpeaker: false,
    };
    const before = { ...base, speakers: [speaker] };
    const after = { ...base, speakers: [{ ...speaker, title: "Presenter" }] };

    (api.listPeople as ReturnType<typeof vi.fn>).mockResolvedValue([
      {
        id: "p1", name: "Lizzie Mcneil", title: null, companyName: null, email: null, phone: null,
        isInternal: false, voiceprintOptOut: false, hasVoiceprint: true, sampleCount: 2,
        linkedUserId: null, isSelf: false, canManageBiometrics: true,
        createdAt: "2026-07-30T00:00:00Z", updatedAt: "2026-07-30T00:00:00Z",
      },
    ]);
    (api.findPersonDuplicates as ReturnType<typeof vi.fn>).mockResolvedValue([]);
    (api.updatePerson as ReturnType<typeof vi.fn>).mockResolvedValue(undefined);

    renderPage(before);
    await loaded();
    openTab("Speakers");

    fireEvent.click(screen.getByRole("button", { name: "Manage people" }));
    const dialog = await screen.findByRole("dialog", { name: "People" });
    fireEvent.click(await within(dialog).findByRole("button", { name: /Lizzie Mcneil/ }));
    fireEvent.change(within(dialog).getByLabelText("Job title"), { target: { value: "Presenter" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save" }));
    await waitFor(() => expect(api.updatePerson).toHaveBeenCalled());

    // The person now carries a job title on the server. The payload the page is still holding does not.
    (api.getRecording as ReturnType<typeof vi.fn>).mockResolvedValue(after);
    fireEvent.keyDown(document, { key: "Escape" });

    expect(await screen.findByText("Presenter")).toBeTruthy();
  });
```

Notes on the query choices, so they are not "simplified" into something ambiguous:
- `within(dialog)` scopes to the People modal. "Lizzie Mcneil" also appears on the speaker row behind it, and the page has its own buttons; unscoped queries would be ambiguous.
- The person's list button has the voiceprint marker inside it, so its accessible name is more than the name - hence the `/Lizzie Mcneil/` regex, matching `PeopleModal.test.tsx`.
- Escape rather than a click on Close: the modal has **two** buttons named "Close" (the header cross and the footer button). Escape is unambiguous and is a real user path.
- `renderPage` sets `getRecording` to resolve `before`; re-setting it to `after` mid-test changes what the refetch returns.

- [ ] **Step 3: Run the test and confirm it fails for the right reason**

```bash
cd apps/web && npm test -- src/pages/RecordingDetail.test.tsx -t "refreshes the speaker rows when the People directory closes"
```

Expected: FAIL - `Unable to find an element with the text: Presenter`, after the `findByText` timeout. The modal closed, nothing refetched, and the row still shows the person with no job title.

If it fails any other way (a missing api method, an ambiguous query, a "not wrapped in act" warning), fix the test until it fails on that assertion. A bug fix whose test fails for an unrelated reason proves nothing.

- [ ] **Step 4: Write the fix**

In `apps/web/src/components/PeopleModal.tsx`, add this effect directly below the existing Escape-key `useEffect` (which ends on line 42, `}, [onClose]);`):

```tsx
  /// The directory can change anything a recording shows about a speaker: their name, job title, company,
  /// internal-or-external marker, or the person record itself through a delete or a merge. The Speakers panel
  /// renders those from a snapshot inside the recording payload, so an open recording behind this modal keeps
  /// the old details and a correct edit reads as one that did not save.
  ///
  /// On unmount, not in `onClose`: every exit - the cross, the footer button, Escape - funnels through
  /// unmount, and so will any exit added later. One hook here rather than an `onSaved` per mutation, because
  /// merge lives in this component and delete and erase-voiceprint live in the editor, and a fifth mutation
  /// added later would silently join whichever ones forgot.
  ///
  /// The `["recording"]` prefix, not one id: this modal does not know which recording is behind it, and it
  /// opens from the account menu as well as a recording's Speakers toolbar. React Query only refetches
  /// *active* queries, so with no recording open this costs nothing.
  useEffect(() => {
    return () => void qc.invalidateQueries({ queryKey: ["recording"] });
  }, [qc]);
```

No import changes: `useEffect` (line 1) and `qc` (line 24) both already exist.

- [ ] **Step 5: Run the test and confirm it passes**

```bash
cd apps/web && npm test -- src/pages/RecordingDetail.test.tsx -t "refreshes the speaker rows when the People directory closes"
```

Expected: PASS, 1 passed, no warnings in the output.

- [ ] **Step 6: Mutation-check the test**

Comment out the body of the new `useEffect` (leave the hook, remove the `return () => ...` line), re-run the command from Step 5, and confirm it goes **red** again. Then restore it and confirm green. A test that passes both ways is not testing anything.

- [ ] **Step 7: Run the whole web suite**

```bash
cd apps/web && npm test
```

Expected: all files pass. `PeopleModal.test.tsx` in particular still passes - it unmounts the modal in "brings a dismissed suggestion back when the directory is reopened", which now triggers the invalidation against a query client with no recording query. That is a no-op and must stay silent; if it warns, the plan's assumption is wrong and it needs raising rather than suppressing.

- [ ] **Step 8: Typecheck**

```bash
cd apps/web && npm run build
```

Expected: `tsc` clean, vite build succeeds.

- [ ] **Step 9: Commit**

```bash
git add apps/web/src/components/PeopleModal.tsx apps/web/src/pages/RecordingDetail.test.tsx
git commit -m "fix(people): refresh the recording behind the People directory when it closes"
```

---

### Task 2: Release chores

**Files:**
- Modify: `version.json`
- Modify: `apps/web/package.json:4`
- Modify: `apps/desktop/package.json:4`
- Modify: `integrations/n8n-nodes-diariz/package.json:3`
- Modify: `src/Diariz.Api/Diariz.Api.csproj:8`
- Modify: `apps/web/src/lib/releases.ts` (new first entry in `RELEASES`, which currently begins on line 64)

Nothing else. This is a bug fix with no scope change, so the README Features table, `docs/features.md`, the About-box `CAPABILITIES` block, `docs/Overall_Synopsis_of_Platform.md` and `docs/Data_Schema.md` are all correct as they stand and must not be touched.

- [ ] **Step 1: Bump the version and all four mirrors to `0.205.2`**

- `version.json`: `{ "version": "0.205.2" }`
- `apps/web/package.json` line 4: `"version": "0.205.2",`
- `apps/desktop/package.json` line 4: `"version": "0.205.2",`
- `integrations/n8n-nodes-diariz/package.json` line 3: `"version": "0.205.2",`
- `src/Diariz.Api/Diariz.Api.csproj` line 8: `    <Version>0.205.2</Version>`

- [ ] **Step 2: Add the release entry**

In `apps/web/src/lib/releases.ts`, insert this as the new first element of `RELEASES`, directly after `export const RELEASES: Release[] = [` on line 63 and before the existing `0.205.1` entry:

```ts
  {
    version: "0.205.2",
    date: "2026-08-11",
    pr: 506,
    headline: "An edit made in the People directory now shows on the recording behind it",
    summary:
      "Opening **Manage people** from a recording's Speakers tab, changing someone - their job title, or " +
      "whether they are internal or external - and closing the directory left the speaker rows showing the " +
      "details they had before. The edit had saved; the recording was still showing the copy it was opened " +
      "with, which is indistinguishable from a save that failed. Closing the directory now refreshes the " +
      "recording underneath it, whichever way you opened it and whatever you changed - including deleting " +
      "someone, erasing a voiceprint, or merging two records.",
    fixed: [
      "A person edited through the People directory kept their old job title, company and internal or external marker on the recording's Speakers tab until the page was reloaded.",
    ],
  },
```

Check the copy for em dashes before saving - the constraint is a plain hyphen throughout.

- [ ] **Step 3: Run the version and release guards**

```bash
cd apps/web && npm test -- src/lib/versionMirrors.test.ts src/lib/releases.test.ts
```

Expected: PASS. `versionMirrors.test.ts` reads all five files and fails on any drift; `releases.test.ts` asserts `RELEASES[0].version` equals `version.json`.

- [ ] **Step 4: Run the whole web suite once more**

```bash
cd apps/web && npm test
```

Expected: all files pass, no warnings.

- [ ] **Step 5: Commit**

```bash
git add version.json apps/web/package.json apps/desktop/package.json integrations/n8n-nodes-diariz/package.json src/Diariz.Api/Diariz.Api.csproj apps/web/src/lib/releases.ts
git commit -m "chore(release): 0.205.2"
```

- [ ] **Step 6: Push and open the PR**

```bash
git push -u origin fix/people-modal-refreshes-speakers
```

Then open the PR with `gh pr create`. The body must state the deployment surface, because CLAUDE.md requires it in every PR:

> **Deployment surface: server redeploy only.** Web-only change (`apps/web`), no desktop shell files touched, so no desktop release is needed.

- [ ] **Step 7: Confirm the PR number matches the release entry**

```bash
gh pr view --json number --jq .number
```

If it is not `506`, correct the `pr:` field in the `0.205.2` entry in `apps/web/src/lib/releases.ts`, then commit and push:

```bash
git add apps/web/src/lib/releases.ts
git commit -m "chore(release): correct the PR number on 0.205.2"
git push
```

No test catches a wrong `pr:` number, so this step is the only check on it.

---

## Manual verification (optional, after CI is green)

The automated test covers the wiring. To see it in the real app, run the stack, open a recording with an identified speaker, go to Speakers, click **Manage people**, change that person's job title, close the modal, and confirm the row updates without a page reload. Repeat with the internal/external tick to confirm the chip flips.
