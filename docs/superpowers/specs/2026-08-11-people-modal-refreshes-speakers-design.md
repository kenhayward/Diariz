# The People directory refreshes the recording it was opened over

**Date:** 2026-08-11
**Status:** approved, ready to plan

## The problem

On a recording's Speakers tab, the toolbar's **Manage people** button opens the People directory modal. Edit
someone there - change their job title, or flip them between internal and external - close the modal, and the
speaker row behind it still shows the old details. The save worked; the page is showing a stale copy. A user
reasonably concludes the edit did not save.

## Why it happens

The Speakers panel does not read the People directory. It renders from the recording's own payload
(`["recording", id]`), which carries a **snapshot** of each speaker's person: `displayName`, `title`,
`companyName`, `email`, `phone`, `isInternal`. Both the speaker row
(`RecordingDetail.tsx`, the `SpeakerRow` job-title text and Internal/External chip) and `SpeakerContactCard`
read that snapshot.

`PersonEditor.act()` invalidates `["people"]` after every mutation - the directory list, not the recording. So:

- The **pencil** on a speaker row is already correct. `EditPersonModal` takes an `onSaved`, and
  `RecordingDetail.tsx` uses it to invalidate `["recording", id]`.
- The **Manage people** toolbar button is not. `PeopleModal` mounts `PersonEditor` with no `onSaved`, so
  nothing tells the recording to re-read.

The modal can change a recording's speakers four ways, only one of which is a `PersonEditor` save:

| Mutation | Where it lives |
|---|---|
| Save a person's details | `PersonEditor.save` |
| Delete a person | `PersonEditor`, destructive actions |
| Erase a voiceprint | `PersonEditor`, destructive actions |
| Merge two people | `PeopleModal.merge` - not in the editor at all |

## The design

`PeopleModal` invalidates the `["recording"]` query prefix when it unmounts.

```tsx
useEffect(() => {
  return () => void qc.invalidateQueries({ queryKey: ["recording"] });
}, [qc]);
```

Three decisions worth recording, because each has a plausible-looking alternative:

**On unmount, not in `onClose`.** Every exit - the X, the footer Close button, Escape - funnels through
unmount, and so will any exit added later. A wrapped `onClose` covers only the paths someone remembered to
wrap.

**One close hook, not four per-mutation hooks.** Threading `onSaved` through the editor would still leave
merge, delete and erase-voiceprint to find separately, and a fifth mutation added later would silently join
the ones that forgot. The modal is better treated as a black box that may have changed anything.

**The `["recording"]` prefix, not `["recording", id]`.** The modal does not know which recording sits behind
it, and it is reachable from the account menu as well as the Speakers toolbar - both of which can have a
recording open. React Query only refetches **active** queries, so when no recording is mounted the invalidation
is a no-op.

The refresh is one cheap GET on close whether or not anything was edited. That is the right trade against a
user believing their edit was lost.

## Out of scope

The recordings **list** query (`["recordings"]`). The reported symptom is the speaker panel; widening the
invalidation on a guess is not justified here.

## Testing

A test in `RecordingDetail.test.tsx`, beside the existing pencil-path test, because the wiring between the
modal and the recording query is the only place this can go wrong:

1. Render a recording with one identified speaker who has no job title.
2. Open the Speakers tab, click **Manage people**.
3. Select the person in the directory, set a job title, save.
4. Close the modal.
5. Assert the recording refetched **and** the speaker row now shows the new title.

Asserting the visible row rather than only the refetch count is what makes this fail for the right reason: a
test that counted `getRecording` calls alone would pass against a component that refetched and rendered
nothing new. `api.getRecording` returns the un-titled speaker first and the titled one after, so the assertion
has something real to observe.

Requires `listPeople` and `findPersonDuplicates` on that file's `api` mock, which `PeopleModal` calls on mount.

Mutation check: remove the effect and confirm the test goes red.

## Release

A fix, so Build +1: `0.205.1` -> `0.205.2` in `version.json` and its four mirrors, plus one `RELEASES[0]`
entry. No scope change, so no README / `docs/features.md` / `CAPABILITIES` / architecture or schema doc edits.

Deployment surface: **server redeploy only**. Web-only change, no desktop release.
