# Voiceprint management: identity, segment split, and trained-from selection

Date: 2026-08-24
Status: approved design, ready for planning
Baseline version: 0.248.2

## Problem

Three related complaints, all about not being able to see or control what a voiceprint is made of.

1. **Two people with the same name are indistinguishable.** The duplicates banner reports
   `Same name: Ada Lovelace, Ada Lovelace` and the merge dialog refuses the merge with "these records each
   have a Diariz account" - without saying *which* accounts, so there is no way to tell which row is you.
2. **Nothing shows what trained a voiceprint.** A voiceprint drifts and the user cannot see, let alone
   change, the audio behind it.
3. **A mixed segment poisons training.** A block attributed to one speaker sometimes contains a second
   voice; identifying the dominant speaker enrols the whole block, second voice included.

## What is actually true today (the premises this design corrects)

- **A voiceprint is not trained from segments.** A `VoiceSample` row is one *whole speaker in one
  recording*. `pipeline._speaker_embeddings` pools that speaker's segment audio in transcript order,
  **stops at the first 30 seconds** (`EMBED_MAX_SECONDS`), embeds once, and the API snapshots that single
  vector. Which segments trained it therefore answers as "whichever came first until 30s ran out", and
  nobody records which those were.
- **A per-person sample list already exists and is rendered nowhere.** `GET /api/people/{id}` returns each
  sample's recording name, speaker label and first-segment timestamp, and
  `DELETE /api/people/{id}/voiceprint/samples/{sampleId}` drops one. `EditPersonModal` fetches the payload
  and renders only the contact fields.
- **Segments already carry their own `SpeakerLabel`**, so splitting one and reassigning a piece is
  structurally cheap - but no API or UI edits it, and no word-level timings are stored
  (`pipeline._shape_segments` discards whisperx's aligned words), so a split has nothing exact to cut on.
- **For a linked person, `Person.Email` is the account email** - `PeopleDirectory` keeps them in sync - so
  identity disambiguation needs no new API field and exposes nothing new.

## Decisions taken

| Question | Decision |
|---|---|
| Selection granularity | Segment-level, recomputed on demand by a worker job |
| What a split fixes | The transcript **and** the training selection, as one action |
| Cut point | Store word-level timings; snap the cut to a word boundary |
| Identity shown | Linked account email, a "your account" badge, and an explicit no-account state |
| The 30s cap | Raised but still bounded, and always stated in the UI |
| Splitting an edited segment | Confirm, then discard the revision |
| Delivery | One spec, one PR |

Rejected: falling back to company/sample-count to tell two unlinked same-named people apart; estimating a
cut point from text position; per-segment `VoiceSample` rows (see "Why spans" below).

**Noted concern, overruled by the user and recorded here rather than re-argued:** one PR lands worker,
migration, API and web changes together. The build order in this spec is strictly sequential so the work
can still be cut into three PRs if review asks for it.

---

## Section A - Identity disambiguation

**No API change.** `PersonDto` already carries `email`, `linkedUserId` and `isSelf`.

A pure presenter, `apps/web/src/lib/personIdentity.ts`, maps a `Person` to one identity line so the three
surfaces cannot drift:

| State | Renders as |
|---|---|
| `isSelf` | `ada@example.com - your account` |
| linked, not you | `ken.hayward@acme.com - Diariz account` |
| not linked | `no Diariz account` (muted) |

Three call sites:

1. **`PeopleModal` list rows** - a second line under the name. The row is a `min-w-0` flex, so the identity
   line takes `truncate` on its own block element; `truncate` on an inline span sets only
   `white-space: nowrap` and overflows the row.
2. **The duplicates banner** - today it joins bare names. Becomes one line per person with its identity, so
   a suggestion is decidable without opening the dialog.
3. **`MergePeopleDialog`** - identity under both the *Keep* and *Delete* names, so "Swap which one is kept"
   is a meaningful choice. The `bothLinked` refusal names the two accounts it will not fold together.

Copy uses plain hyphens, never em or en dashes.

**Tests.** Unit tests on `personIdentity` for all three states. Component tests asserting the rendered
identity in each of the three surfaces, including the both-linked refusal naming both emails.

---

## Section B - Word timings, segment split, per-segment speaker

### Storage

New nullable `jsonb` column `Segment.Words`: `[{"w":"Hello","s":1234,"e":1450}]`, ms integers.

Not a `Words` table: a 60-minute meeting is roughly 10k words, which would put 10k rows and another
split-query behind every transcript read. Nullable means "no word timings" - every pre-existing recording,
and the 14 languages with no alignment model (0.228.0). Adding a nullable column is forward-restore-safe,
so **no `MaintenanceController.CurrentFormat` bump**.

### Worker

`pipeline._shape_segments` stops discarding whisperx's aligned words. Words lacking `start`/`end` are
dropped rather than guessed. `TranscriptionResult`'s segment shape gains `Words` (PascalCase, as the
cross-boundary contract requires); `WorkerCallbackController` persists it.

**`TranscriptSegmentMerge` must concatenate word lists too.** Auto-merge runs on every transcription for
users who enabled it (0.229.0); without this, merging silently destroys splittability for exactly the
users most likely to want it. This gets its own test.

### API

Words are **not** added to the public segment DTO - roughly 10k words per recording would balloon
`GET /api/recordings/{id}`, which feeds exports, MCP, webhooks and n8n. The segment DTO gains
`hasWords: bool`; the editor fetches one segment's words on demand.

Three new endpoints under `api/recordings` (which **is** in the published OpenAPI document, so this needs
the snapshot regenerated **and** `npm run generate` in `integrations/n8n-nodes-diariz` - that check has
stayed red across merged PRs before):

| Endpoint | Behaviour |
|---|---|
| `GET /{id}/segments/{sid}/words` | The segment's words, for the split UI |
| `POST /{id}/segments/{sid}/split` | `{ wordIndex }` - cuts *before* that word |
| `PUT /{id}/segments/{sid}/speaker` | `{ label }` - per-segment reassignment |

All three keep the app-wide ownership check: the segment's recording must belong to the caller.

**Split arithmetic lives in a pure `TranscriptSegmentSplit` helper** (matching the existing
`TranscriptSegmentMerge`) so it is unit-testable without a database:

- left `EndMs = Words[i-1].e`, right `StartMs = Words[i].s`. The inter-word gap belongs to neither, which
  is what voiceprint training wants.
- `Words`, `Original` and ordinals divide at the same index; survivors renumber contiguously, following
  `DeleteSegment`'s existing precedent.
- **A segment with a non-null `Revised` loses it.** Both halves take their text from `Original`. The web
  confirms first, naming what will be discarded; the API does not silently drop an edit on an unconfirmed
  call, so the request carries an explicit `discardRevision: true` and returns 409 without it.
- A segment with `Words == null` returns 409: re-transcribe to split it.

`PUT .../speaker` accepts any label already on the recording. When the interrupting voice has no
diarization slot of its own, the **API** mints one rather than letting the client invent a label into the
worker's namespace: the request sends `{ label: null }`, and the API allocates the next free
`SPEAKER_NN` for that recording and creates the `Speaker` row (`DisplayName = label`, `Embedding = null`),
returning the label it chose. If a reassignment leaves a label with no segments, that speaker drops off
the recording - the same rule `DeleteSegment` already applies.

### Staleness, made visible rather than silent

Any split or reassignment invalidates the affected `Speaker.Embedding`: it was computed from spans that no
longer describe that speaker. New `Speaker.EmbeddingStale` bool, set here, surfaced in Section C's tab and
on the speaker row, cleared by the re-embed job. **Nothing recomputes silently** - it needs the worker and
the audio.

**Which speakers are marked stale:**

- A split alone marks nothing - the same audio is still attributed to the same speaker, only divided.
- A reassignment marks **both** the label losing the segment and the label gaining it.
- A `VoiceSample` snapshotted from a stale `Speaker` reads as stale too, since the centroid now averages a
  vector taken from audio that has been re-attributed. This is **derived by joining to the speaker**, not
  stored - two columns saying the same thing would eventually disagree.

### Web

A segment in the transcript gains a **Split** affordance, permission-gated exactly as the existing segment
edit and delete are. Activating it re-renders that one segment word by word with clickable gaps; clicking a
gap splits there. The new right-hand piece opens an inline speaker picker - the recording's existing
speakers, "Multiple Speakers", or a new speaker - so removing an interloper is one continuous gesture.

A segment with `hasWords: false` shows the affordance **disabled with an explanation** ("re-transcribe this
recording to split segments"), never a silent absence.

### Tests

- Worker pytest: word extraction in `_shape_segments`; words surviving `TranscriptSegmentMerge`.
- Unit: `TranscriptSegmentSplit` boundary arithmetic, ordinal renumbering, revision discard, the
  `Words == null` refusal.
- Integration: the `jsonb` round-trip. Byte-comparing a jsonb column's text never matches on real Postgres
  (it reformats JSON) and the in-memory provider hides it - parse and re-serialise both sides.
- Web: `userEvent`, not `fireEvent` - `fireEvent.click` fires handlers on a disabled control, so a
  disabled-split test would pass for a reason the browser never reproduces.

---

## Section C - The Voiceprint tab

### Why spans, not segment ids

Segment rows belong to a transcription *version*; a re-transcribe replaces every one of them, and a stored
list of segment ids would dangle. `Speaker` rows survive re-transcription, and so do wall-clock spans -
which are also exactly what the worker needs in order to slice audio.

`VoiceSample` gains a nullable `jsonb Spans`: `[{"s":1234,"e":5678}]`. **Null means "the whole speaker"**,
which is precisely today's behaviour, so the migration backfills nothing, every existing voiceprint keeps
working untouched, and there is no `CurrentFormat` bump.

### Why the row still means "one speaker in one recording"

Making `VoiceSample` one row per segment would change what a row in the `ProfileContributions` table
*means* - a semantic reshape, which per CLAUDE.md forces a backup-format bump and hard-rejects every
archive taken before it. Not worth it.

The consequence, stated plainly rather than buried: **the centroid still averages per recording.** A
recording trimmed to 8 seconds weighs the same as one contributing 90. That is deliberate - trimming a
noisy recording should not also silently demote it.

### Reconciling spans against the current transcript

A pure helper resolves a sample's spans against the current transcription's segments for display:

- **included** - the segment is fully covered by a span
- **excluded** - no overlap
- **partly included** - overlapped but not covered, which arises only after a re-transcribe moved the
  boundaries

Ticking or unticking anything rewrites the spans from the current segment boundaries, so the partial state
is transient and self-healing.

### The re-embed job

A third Redis stream, following the `audio-merge-jobs` precedent exactly - dispatch in `worker.run_loop`,
`ensure_group` at startup, `reclaim_stale` on an idle poll.

| | |
|---|---|
| Stream | `voiceprint-jobs`, consumer group `workers` |
| Enqueue | `IJobQueue.EnqueueVoiceprintAsync` |
| Job payload | `{ VoiceSampleId, RecordingId, BlobKey, Spans: [{ StartMs, EndMs }] }` (PascalCase) |
| Worker | download blob, load audio, slice spans, pool to the cap, ECAPA embed, L2-normalise |
| Callback | `POST internal/people/voiceprint-result`, header `X-Worker-Secret` |
| Callback body | `{ VoiceSampleId, Embedding, UsedMs, SelectedMs }` |
| Failure | `POST internal/people/voiceprint-failure`, `{ VoiceSampleId, Error }` |
| API | writes `VoiceSample.Embedding` and `UsedMs`, clears `Speaker.EmbeddingStale`, calls `RecomputeVoiceprintAsync` |

The job needs only the ECAPA embedder, not Whisper or pyannote, so it is seconds of work - but it shares
the worker process, so a re-embed can queue behind an in-flight transcription. The tab shows it as pending
rather than pretending it is instant.

The slicing function is pure and takes the embedder as a parameter, matching `_speaker_embeddings`, so it
is unit-testable with a stub.

### The cap

`EMBED_MAX_SECONDS` 30 -> 120, for **both** paths - one cap, not two that drift. ECAPA on 120s versus 30s
is a rounding error on GPU, and the transcription-time embeddings stay comparable with existing centroids.

Per the no-silent-caps rule, the tab always states the truth: *"using 1:20 of the 4:12 selected"*.

### UI

`PersonEditor` (231 lines) splits into a tab shell plus `PersonProfileTab` (today's fields, unchanged) and
`PersonVoiceprintTab`.

The voiceprint tab lists the contributing samples - recording name, speaker label, play from `startMs`,
selected-versus-total duration, a stale badge from Section B, and the existing per-sample remove.
Expanding one reveals that speaker's segments with tick boxes, each playable, each showing its text and
duration.

Ticking marks the sample dirty; a single **Recompute voiceprint** button queues the job, so a run of clicks
is one job and not fifteen.

**Progress is polled, not pushed.** The client wires only `RecordingStatusChanged` today, so a new hub event
would mean changing `createHub`'s signature and every caller for one modal. Instead a queued sample is
`spansJson != null && usedMs == null`, and the open tab refetches the person every 3s while any sample is in
that state. Server-derived, so it survives a reload - which a client-only pending flag would not.

`EditPersonModal` - opened from a speaker in a transcript, which is the moment you notice a voiceprint is
wrong - gets the tab too, while erase and delete stay hidden there as they are now.

An opted-out person gets the tab explaining there is nothing to select. Permission comes from the server's
`canManageBiometrics`; the rule is not recomputed on the client.

### Tests

- Unit: the span/segment reconciliation helper, including the partly-covered case.
- Worker pytest: span slicing against a stub embedder; the cap applied across a multi-span selection.
- Integration: the callback contract, the `Spans` jsonb round-trip, and the centroid recompute.
- Web: the tab, dirty batching (many ticks queue one job), the pending state, and the opted-out state.

---

## Build order

Strictly sequential, so the work can be cut into separate PRs if review asks.

1. Section A - presentation only, no API or schema change, independently shippable.
2. Worker word timings + `Segment.Words` migration + merge concatenation.
3. Split / per-segment speaker endpoints + `Speaker.EmbeddingStale` + the transcript UI.
4. `VoiceSample.Spans` migration + `voiceprint-jobs` stream + callback + reconciliation helper.
5. The Voiceprint tab.
6. Docs, release notes, version bump.

## Migrations

One migration, three nullable/defaulted additions - all forward-restore-safe, so **no `CurrentFormat`
bump**:

- `Segment.WordsJson` - `jsonb`, nullable
- `VoiceSample.SpansJson` - `jsonb`, nullable (null = the whole speaker)
- `VoiceSample.UsedMs` - `integer`, nullable - how much audio the last embed actually consumed. Also the
  **pending marker**: the enqueue clears it, the callback sets it, so "recompute in flight" survives a page
  reload instead of living only in component state.
- `Speaker.EmbeddingStale` - `boolean`, not null, default false

JSON columns follow the codebase's established convention - a `string` property with
`HasColumnType("jsonb")` behind the `isNpgsql` guard, plain text under the in-memory provider - not an
owned-type mapping.

**A `VoiceSample` has no staleness column of its own.** It is derived by joining to its `Speaker`'s
`EmbeddingStale`, so the two can never disagree.

## Release checklist for this PR

Functional enhancement, so **Minor +1, Build reset**: 0.248.2 -> 0.249.0.

1. `version.json` and its mirrors: `apps/web/package.json`, `apps/web/package-lock.json` (two places),
   `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`,
   `integrations/n8n-nodes-diariz/package.json`.
2. `RELEASES[0]` in `apps/web/src/lib/releases.ts` - the `pr:` number must be confirmed, not guessed from
   "last + 1"; issues and Dependabot share the sequence and no test catches a wrong one.
3. About-box `CAPABILITIES` row - the voiceprint scope genuinely changes.
4. README Features table row.
5. `docs/features.md` prose bullet - always in lockstep with the README row.
6. `docs/Overall_Synopsis_of_Platform.md` - the third worker stream, its job and callback contracts, and
   the raised embedding cap.
7. `docs/Data_Schema.md` - the three columns and the migration-history row.
8. Help articles whose **behaviour** changed: `people-directory.md`, `merging-people.md`,
   `transcription-and-speakers.md`. ASCII only, front matter intact.
9. OpenAPI snapshot regenerated (it self-heals on a second run - commit the regenerated file) and
   `npm run generate` in `integrations/n8n-nodes-diariz`, whose `generated/index.ts` does **not** self-heal.

**Deployment surface:** server redeploy plus a **worker image redeploy** (new stream and new job handler).
No desktop release - nothing under `apps/desktop/src` is touched.

## Out of scope

- Re-running identification across other recordings after a voiceprint is recomputed. The existing
  `POST /api/recordings/{id}/reidentify` already covers this per recording, on demand.
- Weighting the centroid by how much audio each sample contributes.
- A waveform editor. Selection is by segment; sub-segment precision comes from splitting.
- A separate worker process for the cheap embedding jobs.
