# Auto-merge Speaker Segments Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a per-user preference (default off) that automatically collapses consecutive same-speaker transcript segments once a recording's transcription and voiceprint identification have completed.

**Architecture:** The merge logic currently living inside `RecordingsController.MergeSegments` is extracted into a static, EF-aware helper `TranscriptSegmentMerge` (mirroring the existing `SpeakerLabeling` helper, which is shared between the same controller and the same worker callback). The controller keeps its ownership checks and delegates; `WorkerCallbackController.Result` calls the same helper when the recording owner has opted in, positioned after speaker identification is saved and before any downstream job is enqueued. There is exactly one merge implementation.

**Tech Stack:** ASP.NET Core (.NET 10), EF Core + Postgres, xUnit + Testcontainers, React 19 + TypeScript + Vite + Tailwind v4, vitest + @testing-library/react, i18next.

**Spec:** `docs/superpowers/specs/2026-08-19-auto-merge-speaker-segments-design.md`

## Global Constraints

- **TDD is mandatory.** Write the failing test, run it, watch it fail with a real error message, then write the minimal code to pass. No production code without a preceding failing test.
- **Test output must be pristine.** A passing run has no errors or warnings.
- **No em/en dashes in user-facing text.** Use a plain hyphen `-`, never `—` or `–`. Applies to UI strings, all i18n catalogs, release notes, and help articles. Code, comments, and internal docs are unaffected.
- **Help articles are ASCII only.**
- **Never commit to `main`.** All work lands on branch `feat/auto-merge-speaker-segments` and merges via a PR.
- **Never `git add -A` in this repository.** It sweeps hundreds of untracked agent scratch files into the commit. Stage explicit paths only.
- **Do not add a `jest-dom` dependency.** None of the 230+ existing web test files use its matchers; use plain assertions (`expect(x).toBe(true)`, `expect(el).toBeTruthy()`).
- **Do not use `--filter "Name=X"` with `dotnet test`.** It matches nothing in this repository despite what CLAUDE.md says. Use `--filter "FullyQualifiedName~X"`.
- **Build `Diariz.slnx` before pushing.** Unit-only test runs miss compile breaks in the integration project.
- Version target: `0.228.4` -> **`0.229.0`** (functional enhancement).

---

### Task 1: The setting - domain column, migration, API surface

Adds `UserSettings.AutoMergeSpeakerSegments` and exposes it through `GET`/`PUT /api/user/settings` using the established tri-state pattern, so the Recordings preferences tab can save it without disturbing other tabs' fields.

**Files:**
- Modify: `src/Diariz.Domain/Entities/UserSettings.cs` (append after `CalendarSilenceStopSeconds`, before the `const` block)
- Create: `src/Diariz.Domain/Migrations/<timestamp>_AddAutoMergeSpeakerSegments.cs` (generated)
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs:637` (`UserSettingsDto`) and `:670` (`UpdateUserSettingsRequest`)
- Modify: `src/Diariz.Api/Controllers/UserSettingsController.cs:78` (GET) and `:151` (PUT)
- Modify: `docs/Data_Schema.md` (migration-history table after the `AddTranscriptionLanguage` row at line 111; `UserSettings` column table after line 767)
- Test: `tests/Diariz.Api.Tests/UserSettingsControllerTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `UserSettings.AutoMergeSpeakerSegments` (`bool`, default `false`); `UserSettingsDto.AutoMergeSpeakerSegments` (`bool`, default `false`); `UpdateUserSettingsRequest.AutoMergeSpeakerSegments` (`bool?`, default `null`). Task 3 reads the entity property; Task 5 consumes the DTO fields as `autoMergeSpeakerSegments`.

- [ ] **Step 1: Write the three failing tests**

Append to `tests/Diariz.Api.Tests/UserSettingsControllerTests.cs`, inside the existing test class. The file already has a static `Build(DiarizDbContext db, Guid userId, ...)` helper - use it. Note the two controller signatures: `Get()` returns `Task<UserSettingsDto>` **directly** (no `ActionResult` wrapper, so no `.Value`), and the update action is named **`Update`**, not `Put`:

```csharp
    // ---- Auto-merge speaker segments ----

    [Fact]
    public async Task Get_AutoMergeSpeakerSegments_DefaultsToFalse_WhenThereIsNoSettingsRow()
    {
        using var db = TestDb.Create();

        var dto = await Build(db, Guid.NewGuid()).Get();

        Assert.False(dto.AutoMergeSpeakerSegments);
    }

    [Fact]
    public async Task Update_AutoMergeSpeakerSegments_RoundTrips()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();

        await Build(db, userId).Update(new UpdateUserSettingsRequest(AutoMergeSpeakerSegments: true));

        Assert.True((await Build(db, userId).Get()).AutoMergeSpeakerSegments);
    }

    [Fact]
    public async Task Update_OmittingAutoMergeSpeakerSegments_LeavesItAlone()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        await Build(db, userId).Update(new UpdateUserSettingsRequest(AutoMergeSpeakerSegments: true));

        // Another preferences tab saving its own fields must not clear this one (the tri-state rule).
        await Build(db, userId).Update(new UpdateUserSettingsRequest(CalendarAutoStopEnabled: true));

        Assert.True((await Build(db, userId).Get()).AutoMergeSpeakerSegments);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~AutoMergeSpeakerSegments"
```

Expected: **compile failure** - `'UpdateUserSettingsRequest' does not contain a definition for 'AutoMergeSpeakerSegments'` and `'UserSettingsDto' does not contain a definition for 'AutoMergeSpeakerSegments'`. A compile failure is a legitimate red here; do not proceed until you have seen it.

- [ ] **Step 3: Add the entity property**

In `src/Diariz.Domain/Entities/UserSettings.cs`, immediately after the `CalendarSilenceStopSeconds` property and before the two `public const int Default...` lines:

```csharp
    // ---- Transcript presentation ----

    /// <summary>Whether consecutive same-speaker segments are collapsed into single blocks automatically,
    /// once a recording has been transcribed and its speakers identified - the same collapse the transcript
    /// toolbar's Merge action performs on demand. Off by default: it is permanent for that transcription
    /// version, so it is opted into rather than assumed. Turning it off and re-transcribing is the way back
    /// to granular segments.</summary>
    public bool AutoMergeSpeakerSegments { get; set; }
```

- [ ] **Step 4: Add the DTO fields**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, change the final parameter of `UserSettingsDto` (currently `int CalendarSilenceStopSeconds = ...;` at line 637) so it is no longer last, and append:

```csharp
    /// <summary>Seconds of continuous silence that also ends such a recording.</summary>
    int CalendarSilenceStopSeconds = UserSettings.DefaultCalendarSilenceStopSeconds,
    /// <summary>Whether consecutive same-speaker segments are collapsed automatically once a recording
    /// finishes transcribing. Off by default.</summary>
    bool AutoMergeSpeakerSegments = false);
```

Then in `UpdateUserSettingsRequest`, change the last parameter (line 670) the same way:

```csharp
    /// <summary>Seconds of continuous silence that ends such a recording. Null leaves it unchanged; a
    /// non-positive value resets to the default.</summary>
    int? CalendarSilenceStopSeconds = null,
    /// <summary>Whether transcripts are auto-merged by speaker. Null leaves it unchanged.</summary>
    bool? AutoMergeSpeakerSegments = null);
```

- [ ] **Step 5: Wire the controller**

In `src/Diariz.Api/Controllers/UserSettingsController.cs`, change the closing line of the `UserSettingsDto` construction (line 78) from `... UserSettings.DefaultCalendarSilenceStopSeconds);` to:

```csharp
            CalendarSilenceStopSeconds:
                s?.CalendarSilenceStopSeconds ?? UserSettings.DefaultCalendarSilenceStopSeconds,
            AutoMergeSpeakerSegments: s?.AutoMergeSpeakerSegments ?? false);
```

In the `Put` handler, immediately after the `CalendarSilenceStopSeconds` block (line 151-153) and before `await _db.SaveChangesAsync();`:

```csharp
        // No clamping: it is a plain switch, unlike the two durations above.
        if (req.AutoMergeSpeakerSegments is { } autoMerge) s.AutoMergeSpeakerSegments = autoMerge;
```

- [ ] **Step 6: Generate the migration**

```bash
dotnet ef migrations add AddAutoMergeSpeakerSegments --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Open the generated `.cs` file and confirm the `Up` method adds a `boolean` column with `nullable: false, defaultValue: false`. EF's default for a non-nullable `bool` is already `false`, which is the correct value for existing rows - unlike the `AddCalendarRecordingPreferences` migration, no hand-written column default is needed here. Do **not** bump `MaintenanceController.CurrentFormat`: this is an additive, defaulted column and an older backup restores into it cleanly.

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~UserSettings"
```

Expected: PASS, including the three new tests and every pre-existing `UserSettings` test.

- [ ] **Step 8: Update the schema doc**

In `docs/Data_Schema.md`, add a row to the migration-history table immediately after the `AddTranscriptionLanguage` row (line 111):

```markdown
| `AddAutoMergeSpeakerSegments` | `UserSettings.AutoMergeSpeakerSegments` (boolean NOT NULL DEFAULT false) - whether consecutive same-speaker segments are collapsed automatically once a recording finishes transcribing. Additive and defaulted, forward-restore-safe (no `MaintenanceController.CurrentFormat` bump) |
```

And add a row to the `UserSettings` column table, immediately after the `CalendarSilenceStopSeconds` row (line 767):

```markdown
| `AutoMergeSpeakerSegments` | bool | whether consecutive same-speaker segments are collapsed into single blocks automatically after transcription and speaker identification, the same collapse `POST /api/recordings/{id}/merge-segments` performs on demand; **default false**. Permanent for that transcription version |
```

- [ ] **Step 9: Commit**

```bash
git add src/Diariz.Domain/Entities/UserSettings.cs src/Diariz.Domain/Migrations src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/UserSettingsController.cs tests/Diariz.Api.Tests/UserSettingsControllerTests.cs docs/Data_Schema.md
git commit -m "feat: add the AutoMergeSpeakerSegments user setting

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: Extract the merge into a shared `TranscriptSegmentMerge` helper

Moves the merge body out of the controller so the worker callback can run the identical code. Behaviour must not change: the four existing `MergeSegments_*` controller tests are the regression guard and must pass **without edits**.

**Files:**
- Create: `src/Diariz.Api/Services/TranscriptSegmentMerge.cs`
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs:633-693` (the `MergeSegments` body)
- Test: `tests/Diariz.Api.Tests/TranscriptSegmentMergeTests.cs` (new)

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `Diariz.Api.Services.TranscriptSegmentMerge.ApplyAsync(DiarizDbContext db, Guid recordingId, Guid transcriptionId, CancellationToken ct = default) -> Task<bool>`. Returns `true` when segments were collapsed (caller must save), `false` when nothing was adjacent (change tracker untouched). It does **not** call `SaveChangesAsync`. Task 3 calls this.

- [ ] **Step 1: Write the failing helper test**

Create `tests/Diariz.Api.Tests/TranscriptSegmentMergeTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>The merge helper called directly, without a controller. It is shared by the on-demand Merge
/// action and the worker callback, so it is tested at its own seam rather than only through one caller.</summary>
public class TranscriptSegmentMergeTests
{
    private static async Task<(Guid recordingId, Guid transcriptionId)> Seed(
        Diariz.Domain.DiarizDbContext db, params (string label, long startMs, long endMs, string text)[] segments)
    {
        var userId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        var transcriptionId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = recordingId, UserId = userId, Title = "t", BlobKey = "k" });
        db.Transcriptions.Add(new Transcription { Id = transcriptionId, RecordingId = recordingId, Model = "m", Version = 1 });
        var ordinal = 0;
        foreach (var (label, startMs, endMs, text) in segments)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = transcriptionId, SpeakerLabel = label,
                StartMs = startMs, EndMs = endMs, Original = text, Ordinal = ordinal++,
            });
        await db.SaveChangesAsync();
        return (recordingId, transcriptionId);
    }

    [Fact]
    public async Task ApplyAsync_CollapsesConsecutiveSameSpeaker_AndReportsTheChange()
    {
        using var db = TestDb.Create();
        var (recordingId, transcriptionId) = await Seed(db,
            ("SPEAKER_00", 0, 1000, "Hello"),
            ("SPEAKER_00", 1000, 2000, "World"));

        var changed = await TranscriptSegmentMerge.ApplyAsync(db, recordingId, transcriptionId);
        await db.SaveChangesAsync();

        Assert.True(changed);
        var seg = Assert.Single(await db.Segments.Where(s => s.TranscriptionId == transcriptionId).ToListAsync());
        Assert.Equal("Hello\nWorld", seg.EffectiveText);
        Assert.Equal(0, seg.StartMs);
        Assert.Equal(2000, seg.EndMs);
        Assert.Equal(0, seg.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_WithNothingAdjacent_ReportsNoChange_AndLeavesSegmentsAlone()
    {
        using var db = TestDb.Create();
        var (recordingId, transcriptionId) = await Seed(db,
            ("SPEAKER_00", 0, 1000, "Hello"),
            ("SPEAKER_01", 1000, 2000, "Hi there"));

        var changed = await TranscriptSegmentMerge.ApplyAsync(db, recordingId, transcriptionId);

        Assert.False(changed);
        Assert.Equal(2, await db.Segments.CountAsync(s => s.TranscriptionId == transcriptionId));
    }

    [Fact]
    public async Task ApplyAsync_WithNoSegments_ReportsNoChange()
    {
        using var db = TestDb.Create();
        var (recordingId, transcriptionId) = await Seed(db);

        Assert.False(await TranscriptSegmentMerge.ApplyAsync(db, recordingId, transcriptionId));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~TranscriptSegmentMerge"
```

Expected: **compile failure** - `The name 'TranscriptSegmentMerge' does not exist in the current context`.

- [ ] **Step 3: Create the helper**

Create `src/Diariz.Api/Services/TranscriptSegmentMerge.cs`. This is the body of `RecordingsController.MergeSegments` from line 645 (`var segments = ...`) to line 692, moved verbatim with `_db` renamed to `db`, `id` renamed to `recordingId`, `current.Id` renamed to `transcriptionId`, and the two early exits turned into `return false`:

```csharp
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Collapses consecutive same-speaker segments of one transcription into single blocks, in the
/// change tracker. Shared by the on-demand "Merge rows" action and - when the recording's owner has opted
/// in - the worker callback, so both produce an identical transcript from identical input. Mirrors
/// <see cref="SpeakerLabeling"/>: it mutates and leaves the save to the caller.</summary>
public static class TranscriptSegmentMerge
{
    /// <returns>True when segments were collapsed and the caller should save; false when nothing was
    /// adjacent to merge (or there were no segments), in which case the change tracker is untouched.</returns>
    public static async Task<bool> ApplyAsync(
        DiarizDbContext db, Guid recordingId, Guid transcriptionId, CancellationToken ct = default)
    {
        var segments = await db.Segments.Where(s => s.TranscriptionId == transcriptionId)
            .OrderBy(s => s.Ordinal).ToListAsync(ct);
        if (segments.Count == 0) return false;

        // Group by the speaker's effective identity (assigned person, else display name), not the raw
        // diarization label - so two labels reassigned to the same person merge together.
        var speakers = await db.Speakers.Where(s => s.RecordingId == recordingId)
            .ToDictionaryAsync(s => s.Label, s => s, ct);
        string KeyFor(string label)
        {
            if (!speakers.TryGetValue(label, out var sp)) return $"l:{label}";
            if (sp.PersonId is Guid pid) return $"p:{pid}";
            return string.IsNullOrEmpty(sp.DisplayName) ? $"l:{label}" : $"n:{sp.DisplayName}";
        }

        // A note or a screenshot sits between two segments; don't let a same-speaker merge swallow that
        // boundary (the note or image would jump to after the whole merged block). Flag the segment after
        // each anchor. Both kinds of capture use the same rule, so they share one break set.
        var noteTimes = await db.MeetingNotes
            .Where(n => n.RecordingId == recordingId && n.CapturedAtMs != null)
            .Select(n => n.CapturedAtMs!.Value)
            .ToListAsync(ct);
        var shotTimes = await db.MeetingScreenshots
            .Where(s => s.RecordingId == recordingId)
            .Select(s => s.CapturedAtMs)
            .ToListAsync(ct);
        var breakBefore = TranscriptNoteAnchor.BreakBeforeIndices(
            segments.Select(s => s.StartMs).ToList(), noteTimes.Concat(shotTimes));

        var merged = SegmentMerger.Merge(segments
            .Select((s, i) => new SegmentMerger.Part(
                KeyFor(s.SpeakerLabel), s.SpeakerLabel, s.StartMs, s.EndMs, s.EffectiveText, breakBefore.Contains(i)))
            .ToList());
        if (merged.Count == segments.Count) return false; // nothing adjacent to merge

        db.Segments.RemoveRange(segments);
        var ordinal = 0;
        foreach (var p in merged)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(),
                TranscriptionId = transcriptionId,
                SpeakerLabel = p.SpeakerLabel,
                StartMs = p.StartMs,
                EndMs = p.EndMs,
                // Merge consolidates the displayed (effective) text; the per-segment original/revised split
                // is intentionally collapsed into a fresh Original on the merged row.
                Original = p.Text,
                Ordinal = ordinal++
            });
        return true;
    }
}
```

- [ ] **Step 4: Run the new test to verify it passes**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~TranscriptSegmentMerge"
```

Expected: PASS (3 tests).

- [ ] **Step 5: Make the controller delegate**

In `src/Diariz.Api/Controllers/RecordingsController.cs`, replace the entire body of `MergeSegments` (everything from `var owned = ...` to the closing brace) with:

```csharp
        var owned = await _db.Recordings.AnyAsync(r => r.Id == id && r.UserId == UserId);
        if (!owned) return NotFound();

        var current = await _db.Transcriptions.Where(t => t.RecordingId == id)
            .OrderByDescending(t => t.Version).FirstOrDefaultAsync();
        if (current is null) return NotFound();

        // Preserved from before the merge itself moved to TranscriptSegmentMerge: a transcription with no
        // segments is a 404 here, whereas the helper simply reports "nothing changed".
        if (!await _db.Segments.AnyAsync(s => s.TranscriptionId == current.Id)) return NotFound();

        if (await TranscriptSegmentMerge.ApplyAsync(_db, id, current.Id))
            await _db.SaveChangesAsync();
        return NoContent();
```

Delete the now-unused local `KeyFor` function and everything it used. Do not change the `[HttpPost]`, `[EndpointSummary]` or `[EndpointDescription]` attributes in this task - the description text changes in Task 6.

- [ ] **Step 6: Run the regression guard**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~MergeSegments"
```

Expected: PASS - all four pre-existing `MergeSegments_*` tests, **unedited**. If any of them needed editing, the extraction changed behaviour and is wrong; revert and redo it.

- [ ] **Step 7: Run the whole unit suite**

```bash
dotnet test tests/Diariz.Api.Tests
```

Expected: PASS, no warnings.

- [ ] **Step 8: Commit**

```bash
git add src/Diariz.Api/Services/TranscriptSegmentMerge.cs src/Diariz.Api/Controllers/RecordingsController.cs tests/Diariz.Api.Tests/TranscriptSegmentMergeTests.cs
git commit -m "refactor: extract the segment merge into a shared TranscriptSegmentMerge helper

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Run the merge from the worker callback

Hooks the helper into `WorkerCallbackController.Result` when the recording owner has opted in.

**Files:**
- Modify: `src/Diariz.Api/Controllers/WorkerCallbackController.cs` (constructor + the `Result` body around line 138)
- Modify: `tests/Diariz.Api.Tests/WorkerCallbackControllerTests.cs` (harness + four new tests)
- Modify: `tests/Diariz.Api.Tests/RecordingWebhookEmitTests.cs:39` (the other hand-construction site)

**Interfaces:**
- Consumes: `UserSettings.AutoMergeSpeakerSegments` (Task 1); `TranscriptSegmentMerge.ApplyAsync` (Task 2).
- Produces: `WorkerCallbackController`'s constructor gains a **final** parameter `ILogger<WorkerCallbackController> logger`. Both hand-construction sites must append `NullLogger<WorkerCallbackController>.Instance`.

- [ ] **Step 1: Extend the test harness to seed the setting**

In `tests/Diariz.Api.Tests/WorkerCallbackControllerTests.cs`, add this helper method to the class (place it next to `SeedQueuedRecording`):

```csharp
    /// <summary>Give the recording's owner an explicit auto-merge preference. Absence of a row is itself a
    /// case under test, so this is opt-in rather than part of SeedQueuedRecording.</summary>
    private static async Task SeedAutoMerge(DiarizDbContext db, Guid userId, bool enabled)
    {
        db.UserSettings.Add(new UserSettings { UserId = userId, AutoMergeSpeakerSegments = enabled });
        await db.SaveChangesAsync();
    }
```

- [ ] **Step 2: Write the four failing tests**

Append to the same class:

```csharp
    // ---- Auto-merge (UserSettings.AutoMergeSpeakerSegments) ----

    /// <summary>The whole feature: with the owner opted in, the transcript arrives already collapsed.</summary>
    [Fact]
    public async Task Result_WithAutoMergeOn_CollapsesConsecutiveSameSpeakerSegments()
    {
        var (controller, db, _) = Build(presentedSecret: Secret);
        var userId = Guid.NewGuid();
        var (_, transcriptionId) = await SeedQueuedRecording(db, userId);
        await SeedAutoMerge(db, userId, enabled: true);

        await controller.Result(new TranscriptionResult(transcriptionId, "en",
        [
            new SegmentResult("SPEAKER_00", 0, 1000, "Hello"),
            new SegmentResult("SPEAKER_00", 1000, 2000, "World"),
            new SegmentResult("SPEAKER_01", 2000, 3000, "Hi there"),
        ]));

        var segs = await db.Segments.Where(s => s.TranscriptionId == transcriptionId)
            .OrderBy(s => s.Ordinal).ToListAsync();
        Assert.Equal(2, segs.Count);
        Assert.Equal("Hello\nWorld", segs[0].EffectiveText);
        Assert.Equal(0, segs[0].StartMs);
        Assert.Equal(2000, segs[0].EndMs);
        Assert.Equal("Hi there", segs[1].EffectiveText);
    }

    /// <summary>Pins the default. Every existing user must see byte-identical behaviour to before.</summary>
    [Fact]
    public async Task Result_WithAutoMergeOff_KeepsSegmentsGranular()
    {
        var (controller, db, _) = Build(presentedSecret: Secret);
        var userId = Guid.NewGuid();
        var (_, transcriptionId) = await SeedQueuedRecording(db, userId);
        await SeedAutoMerge(db, userId, enabled: false);

        await controller.Result(new TranscriptionResult(transcriptionId, "en",
        [
            new SegmentResult("SPEAKER_00", 0, 1000, "Hello"),
            new SegmentResult("SPEAKER_00", 1000, 2000, "World"),
        ]));

        Assert.Equal(2, await db.Segments.CountAsync(s => s.TranscriptionId == transcriptionId));
    }

    /// <summary>The settings row is created lazily, so plenty of owners have none. Absent means off.</summary>
    [Fact]
    public async Task Result_WithNoUserSettingsRow_KeepsSegmentsGranular()
    {
        var (controller, db, _) = Build(presentedSecret: Secret);
        var (_, transcriptionId) = await SeedQueuedRecording(db, Guid.NewGuid());

        await controller.Result(new TranscriptionResult(transcriptionId, "en",
        [
            new SegmentResult("SPEAKER_00", 0, 1000, "Hello"),
            new SegmentResult("SPEAKER_00", 1000, 2000, "World"),
        ]));

        Assert.Equal(2, await db.Segments.CountAsync(s => s.TranscriptionId == transcriptionId));
    }

    /// <summary>Pins where the hook sits: after voiceprint identification, not before it. Two diarization
    /// labels that the identifier resolved to one person are the same speaker, so they must merge - which
    /// only holds if the merge runs once SpeakerLabeling has assigned PersonId.</summary>
    [Fact]
    public async Task Result_WithAutoMergeOn_MergesTwoLabelsIdentifiedAsTheSamePerson()
    {
        var identifier = new FakeSpeakerIdentifier
        {
            // SpeakerMatch(Guid PersonId, string Name, double Distance) - the distance is unused here.
            Match = new SpeakerMatch(Guid.NewGuid(), "Alice", 0.1),
        };
        var (controller, db, _, _) = BuildEx(Secret, summarizationEnabled: false, identifier);
        var userId = Guid.NewGuid();
        var (_, transcriptionId) = await SeedQueuedRecording(db, userId);
        await SeedAutoMerge(db, userId, enabled: true);

        // Both labels carry an embedding, so both go through identification and both land on Alice.
        var embedding = new float[192];
        await controller.Result(new TranscriptionResult(transcriptionId, "en",
            [
                new SegmentResult("SPEAKER_00", 0, 1000, "Hello"),
                new SegmentResult("SPEAKER_01", 1000, 2000, "World"),
            ],
            Speakers:
            [
                new SpeakerEmbeddingResult("SPEAKER_00", embedding),
                new SpeakerEmbeddingResult("SPEAKER_01", embedding),
            ]));

        var seg = Assert.Single(await db.Segments.Where(s => s.TranscriptionId == transcriptionId).ToListAsync());
        Assert.Equal("Hello\nWorld", seg.EffectiveText);
        Assert.Equal("SPEAKER_00", seg.SpeakerLabel); // the first label of the run is kept
    }
```

The contract records these tests use, for reference (`src/Diariz.Api/Contracts/WorkerContracts.cs`):

```csharp
public record SegmentResult(string Speaker, long StartMs, long EndMs, string Text);
public record SpeakerEmbeddingResult(string Speaker, float[] Embedding);
public record TranscriptionResult(
    Guid TranscriptionId, string? Language, IReadOnlyList<SegmentResult> Segments,
    IReadOnlyList<SpeakerEmbeddingResult>? Speakers = null, long? DurationMs = null, long? ProcessingMs = null);
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~AutoMerge"
```

Expected: the three merge-behaviour tests **fail on assertion** (`Assert.Equal() Failure: Expected: 2, Actual: 3` for the first; `Assert.Single` failure for the fourth), and `Result_WithAutoMergeOff_KeepsSegmentsGranular` / `Result_WithNoUserSettingsRow_KeepsSegmentsGranular` **pass already** - they pin behaviour that must not change. That split is correct; do not "fix" the two that pass.

- [ ] **Step 4: Add the logger to the controller**

In `src/Diariz.Api/Controllers/WorkerCallbackController.cs`, add a field beside the others:

```csharp
    private readonly ILogger<WorkerCallbackController> _logger;
```

Append `ILogger<WorkerCallbackController> logger` as the **final** constructor parameter (after `IOptions<AppPublicOptions> appOpts`) and assign `_logger = logger;` in the body. If the file does not already resolve `ILogger<>`, add `using Microsoft.Extensions.Logging;` at the top.

- [ ] **Step 5: Add the hook**

In `Result`, between `await _db.SaveChangesAsync();` (line 138, the one that follows the `autoSummarise` status assignment) and `if (autoSummarise)` (line 140), insert:

```csharp
        // Collapse consecutive same-speaker segments when the owner has asked for it. Deliberately here:
        // after SpeakerLabeling has been saved, so two diarization labels resolved to one person merge as
        // one speaker; and before every enqueue and the SignalR notify below, so the summary, actions,
        // tags, embeddings, the browser and the webhook all see one final shape rather than a reshuffle.
        // FirstOrDefaultAsync over a bool projection yields false when the owner has no settings row.
        var autoMerge = await _db.UserSettings
            .Where(x => x.UserId == transcription.Recording.UserId)
            .Select(x => x.AutoMergeSpeakerSegments)
            .FirstOrDefaultAsync();
        if (autoMerge)
        {
            try
            {
                if (await TranscriptSegmentMerge.ApplyAsync(_db, transcription.RecordingId, transcription.Id))
                    await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // Swallowed on purpose: an unmerged transcript is perfectly valid, but throwing here would
                // leave the recording committed as Summarizing with no summarization job enqueued - stranded
                // in "Summarising..." with nothing left to clear it, which is exactly what the enqueue guard
                // immediately below exists to prevent.
                _logger.LogError(ex, "Auto-merge failed for transcription {TranscriptionId}", transcription.Id);
            }
        }
```

- [ ] **Step 6: Fix both hand-construction sites**

In `tests/Diariz.Api.Tests/WorkerCallbackControllerTests.cs` (line ~36) and `tests/Diariz.Api.Tests/RecordingWebhookEmitTests.cs` (line ~39), append a final constructor argument:

```csharp
            new CapturingWebhookPublisher(), Options.Create(new AppPublicOptions()),
            NullLogger<WorkerCallbackController>.Instance)
```

Add `using Microsoft.Extensions.Logging.Abstractions;` to both files.

- [ ] **Step 7: Run the tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~WorkerCallback"
```

Expected: PASS, all tests in both callback test classes.

- [ ] **Step 8: Run the whole unit suite**

```bash
dotnet test tests/Diariz.Api.Tests
```

Expected: PASS, no warnings.

- [ ] **Step 9: Commit**

```bash
git add src/Diariz.Api/Controllers/WorkerCallbackController.cs tests/Diariz.Api.Tests/WorkerCallbackControllerTests.cs tests/Diariz.Api.Tests/RecordingWebhookEmitTests.cs
git commit -m "feat: auto-merge same-speaker segments in the worker callback when enabled

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: Integration test against real Postgres

The unit tests run on the EF in-memory provider, which does not faithfully translate relational queries - and this path depends on `OrderBy(s => s.Ordinal)` inside the helper. Prove it against a real database.

**Files:**
- Create: `tests/Diariz.Api.IntegrationTests/AutoMergeCallbackTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-3.
- Produces: nothing.

- [ ] **Step 1: Confirm Docker is running**

```bash
docker info
```

Expected: engine details, not an error. The integration project needs Testcontainers.

- [ ] **Step 2: Write the failing test**

Create `tests/Diariz.Api.IntegrationTests/AutoMergeCallbackTests.cs`. Model the controller construction on `WorkerCallbackControllerTests.BuildEx` (in the unit project) and the fixture usage on `ScreenshotMergeBreakTests`:

```csharp
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Services.Llm;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real-Postgres check of the auto-merge hook in <see cref="WorkerCallbackController.Result"/>.
/// The unit tests for it run on the EF in-memory provider, which does not faithfully translate relational
/// queries; the merge depends on reading segments back in <c>Ordinal</c> order, so it is pinned here too.</summary>
[Collection(IntegrationCollection.Name)]
public class AutoMergeCallbackTests(ContainersFixture fx)
{
    private const string Secret = "shared-secret";

    private static WorkerCallbackController Build(Diariz.Domain.DiarizDbContext db)
    {
        var resolver = new LlmSettingsResolver(
            db, Options.Create(new LlmDefaultsOptions()),
            Options.Create(new SummarizationOptions { ApiBase = "" }), new FakeApiKeyProtector());
        var embedding = new EmbeddingSettingsResolver(db, Options.Create(new EmbeddingOptions()), resolver);
        return new WorkerCallbackController(
            db, new FakeHubContext(), new FakeJobQueue(), resolver, embedding, new FakeSpeakerIdentifier(),
            Options.Create(new WorkerOptions { CallbackSecret = Secret }),
            new CapturingWebhookPublisher(), Options.Create(new AppPublicOptions()),
            NullLogger<WorkerCallbackController>.Instance)
        {
            ControllerContext = Http.Context(headers: ("X-Worker-Secret", Secret))
        };
    }

    [Fact]
    public async Task Result_WithAutoMergeOn_CollapsesSameSpeakerRunsInOrdinalOrder()
    {
        Guid userId = Guid.NewGuid(), recordingId = Guid.NewGuid(), transcriptionId = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            db.Users.Add(new ApplicationUser { Id = userId, Email = $"{userId}@t.test", UserName = $"{userId}@t.test" });
            db.Recordings.Add(new Recording { Id = recordingId, UserId = userId, Title = "t", BlobKey = "k", Status = RecordingStatus.Queued });
            db.Transcriptions.Add(new Transcription { Id = transcriptionId, RecordingId = recordingId, Model = "m", Version = 1 });
            db.UserSettings.Add(new UserSettings { UserId = userId, AutoMergeSpeakerSegments = true });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
            await Build(db).Result(new TranscriptionResult(transcriptionId, "en",
            [
                new SegmentResult("SPEAKER_00", 0, 1000, "one"),
                new SegmentResult("SPEAKER_00", 1000, 2000, "two"),
                new SegmentResult("SPEAKER_01", 2000, 3000, "three"),
                new SegmentResult("SPEAKER_01", 3000, 4000, "four"),
                new SegmentResult("SPEAKER_00", 4000, 5000, "five"),
            ]));

        await using var verify = fx.CreateDbContext();
        var segs = await verify.Segments.Where(s => s.TranscriptionId == transcriptionId)
            .OrderBy(s => s.Ordinal).ToListAsync();

        Assert.Equal(3, segs.Count);
        Assert.Equal("one\ntwo", segs[0].EffectiveText);
        Assert.Equal("three\nfour", segs[1].EffectiveText);
        Assert.Equal("five", segs[2].EffectiveText);
        Assert.Equal([0, 1, 2], segs.Select(s => s.Ordinal).ToArray());
        Assert.Equal(0, segs[0].StartMs);
        Assert.Equal(2000, segs[0].EndMs);
    }
}
```

- [ ] **Step 3: Run the test**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~AutoMergeCallback"
```

Expected: PASS. If it fails to compile, fix the constructor argument list against the real `WorkerCallbackController` signature; if it fails an assertion, the ordering assumption is wrong and the helper needs fixing (not the test).

Because Tasks 1-3 are already implemented, this test is green on first run. That is acceptable for a cross-boundary confirmation test - to prove it can fail, temporarily change `AutoMergeSpeakerSegments = true` to `false` in the seed, re-run, confirm it fails with `Expected: 3, Actual: 5`, then change it back.

- [ ] **Step 4: Build the whole solution**

```bash
dotnet build Diariz.slnx
```

Expected: build succeeded, 0 warnings. Unit-only runs miss compile breaks elsewhere in the solution.

- [ ] **Step 5: Commit**

```bash
git add tests/Diariz.Api.IntegrationTests/AutoMergeCallbackTests.cs
git commit -m "test: pin the auto-merge callback hook against real Postgres

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: The preference in the web UI

Adds the switch to Preferences -> Recordings, the type definitions, and the four locale catalogs.

**Files:**
- Modify: `apps/web/src/lib/types.ts:531` (settings type) and `:963` (update type)
- Modify: `apps/web/src/components/icons.tsx` (add `MergeIcon`)
- Modify: `apps/web/src/components/RecordingsSection.tsx`
- Modify: `apps/web/src/locales/en/account.json`, `de/account.json`, `es/account.json`, `fr/account.json`
- Test: `apps/web/src/components/RecordingsSection.test.tsx`

**Interfaces:**
- Consumes: the DTO fields from Task 1, serialised as `autoMergeSpeakerSegments`.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Write the failing tests**

In `apps/web/src/components/RecordingsSection.test.tsx`:

(a) Add `autoMergeSpeakerSegments: false,` to the shared `settings` fixture object (after `calendarSilenceStopSeconds: 30,` at line 20).

(b) Append inside the `describe("RecordingsSection", ...)` block:

```tsx
  describe("auto-merge", () => {
    it("shows the switch off when the setting is off", async () => {
      renderSection();
      const sw = (await screen.findByRole("switch", {
        name: "Merge each speaker's turn into one block",
      })) as HTMLButtonElement;
      expect(sw.getAttribute("aria-checked")).toBe("false");
    });

    it("shows the switch on when the setting is on", async () => {
      (api.getUserSettings as ReturnType<typeof vi.fn>).mockResolvedValue({
        ...settings, autoMergeSpeakerSegments: true,
      });
      renderSection();
      const sw = (await screen.findByRole("switch", {
        name: "Merge each speaker's turn into one block",
      })) as HTMLButtonElement;
      expect(sw.getAttribute("aria-checked")).toBe("true");
    });

    it("marks the footer unsaved when flipped, and saves the new value", async () => {
      renderSection();
      const sw = await screen.findByRole("switch", {
        name: "Merge each speaker's turn into one block",
      });
      fireEvent.click(sw);
      expect(screen.getByText("Unsaved changes")).toBeTruthy();

      fireEvent.click(saveButton());

      // Asserting the call, not relying on the method being absent from the vi.mock factory - an
      // absent-method guard is destroyed silently the moment anything else needs that method.
      await waitFor(() =>
        expect(api.updateUserSettings).toHaveBeenCalledWith(
          expect.objectContaining({ autoMergeSpeakerSegments: true }),
        ),
      );
    });
  });
```

(c) **Two existing tests will break.** Lines 193 and 206 assert `updateUserSettings` was called with an exact object literal. Add `autoMergeSpeakerSegments: false` to both of those expected objects. Do not weaken them to `objectContaining` - their exactness is what proves the payload carries no stray fields.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd apps/web && npx vitest run src/components/RecordingsSection.test.tsx
```

Expected: the three new tests fail with `Unable to find an accessible element with the role "switch" and name "Merge each speaker's turn into one block"`; the two edited exact-payload tests also fail, because the component does not send the field yet.

- [ ] **Step 3: Add the type fields**

In `apps/web/src/lib/types.ts`, after `calendarSilenceStopSeconds: number;` (line 531):

```ts
  /// Whether consecutive same-speaker segments are collapsed automatically once a recording finishes
  /// transcribing. Off by default.
  autoMergeSpeakerSegments: boolean;
```

And after `calendarSilenceStopSeconds?: number;` (line 963):

```ts
  /// Whether transcripts are auto-merged by speaker; omit to leave unchanged.
  autoMergeSpeakerSegments?: boolean;
```

- [ ] **Step 4: Add the icon**

In `apps/web/src/components/icons.tsx`, append (the glyph is the same path as the transcript toolbar's `MergeIcon` in `detail/icons.tsx`, redrawn here as a sizeable component - that file exports ReactElements with a fixed size, which does not compose with this file's `size` prop):

```tsx
/// Two strands joining into one - Feather-style. Marks the auto-merge preference, matching the transcript
/// toolbar's Merge glyph so the setting and the manual action read as the same thing.
export const MergeIcon = (p: IconProps) => (
  <svg {...svgProps(p)}>
    <path d="M6 3v6a6 6 0 0 0 6 6 6 6 0 0 0 6-6V3" />
    <line x1="12" y1="15" x2="12" y2="21" />
  </svg>
);
```

- [ ] **Step 5: Wire the component state**

In `apps/web/src/components/RecordingsSection.tsx`:

(a) Extend the import from `./icons` to `import { CalendarIcon, FolderIcon, MergeIcon } from "./icons";`

(b) In the `Baseline` interface, add `autoMergeSpeakerSegments: boolean;` after `silenceSeconds: number;`, and correct the doc comment above it: change "The five values" to "The six values".

(c) Add the state hook after `const [silenceSeconds, setSilenceSeconds] = useState(...)`:

```tsx
  const [autoMerge, setAutoMerge] = useState(false);
```

(d) In the seeding block, add to the `next` object literal after `silenceSeconds: ...`:

```tsx
      autoMergeSpeakerSegments: data.autoMergeSpeakerSegments ?? false,
```

and add `setAutoMerge(next.autoMergeSpeakerSegments);` beside the other setters.

(e) In `current`, add after `silenceSeconds: ...`:

```tsx
    autoMergeSpeakerSegments: autoMerge,
```

and correct the comment above `current`: change "The exact five fields Save sends" to "The exact six fields Save sends".

(f) In `dirty`, add a clause:

```tsx
      current.silenceSeconds !== baseline.silenceSeconds ||
      current.autoMergeSpeakerSegments !== baseline.autoMergeSpeakerSegments);
```

(g) In `onSave`'s `api.updateUserSettings({...})` payload, add:

```tsx
        autoMergeSpeakerSegments: current.autoMergeSpeakerSegments,
```

- [ ] **Step 6: Add the card**

In `RecordingsSection.tsx`, immediately after the closing `</div>` of the calendar auto-stop card and before the closing `</div>` of the `space-y-3` wrapper:

```tsx
        {/* How a finished transcript is shaped. Runs the same collapse as the transcript toolbar's Merge
            action, automatically, once a recording has been transcribed and its speakers identified. */}
        <div className="overflow-hidden rounded-lg border dark:border-gray-700">
          <div className="flex items-start justify-between gap-5 px-4 py-3.5">
            <div className="min-w-0">
              <div className="flex items-center gap-2">
                <span className="shrink-0 text-gray-500 dark:text-gray-400">
                  <MergeIcon size={14} />
                </span>
                <h3 className="text-[15px] font-semibold dark:text-gray-100">{t("autoMergeHeading")}</h3>
              </div>
              <p className="mt-1 text-[13px] text-pretty text-gray-500 dark:text-gray-400">{t("autoMergeBody")}</p>
            </div>
            {/* Same control as the switch above: role="switch" on a button, because a native checkbox
                cannot be styled as a track and knob without hiding it and losing the focus ring. The
                heading is its accessible name - it has no visible label of its own. */}
            <button
              type="button"
              role="switch"
              aria-checked={autoMerge}
              aria-label={t("autoMergeHeading")}
              onClick={() => setAutoMerge((on) => !on)}
              className={`relative h-6 w-11 shrink-0 rounded-full transition-colors ${
                autoMerge ? "bg-blue-600" : "bg-gray-300 dark:bg-gray-700"
              }`}
            >
              <span
                aria-hidden
                className={`absolute top-[3px] h-[18px] w-[18px] rounded-full bg-white transition-[left] ${
                  autoMerge ? "left-[23px]" : "left-[3px]"
                }`}
              />
            </button>
          </div>
        </div>
```

- [ ] **Step 7: Add the locale strings**

Add two keys to each of the four `account.json` catalogs, next to the `calendarAutoStop*` keys. **Plain hyphens only.**

`apps/web/src/locales/en/account.json`:

```json
  "autoMergeHeading": "Merge each speaker's turn into one block",
  "autoMergeBody": "When a recording finishes transcribing, consecutive rows from the same person are joined automatically - the same thing the transcript's Merge button does, without the click. Notes and screenshots stay as boundaries. It is permanent for that transcript: to get short rows back, turn this off and transcribe it again.",
```

`apps/web/src/locales/de/account.json`:

```json
  "autoMergeHeading": "Aufeinanderfolgende Beitraege derselben Person zu einem Block zusammenfassen",
  "autoMergeBody": "Sobald eine Aufnahme transkribiert ist, werden aufeinanderfolgende Zeilen derselben Person automatisch zusammengefasst - dasselbe, was die Schaltflaeche Zusammenfuehren im Transkript tut, nur ohne Klick. Notizen und Screenshots bleiben Trennstellen. Fuer dieses Transkript ist das dauerhaft: Um wieder kurze Zeilen zu erhalten, schalten Sie diese Option aus und transkribieren Sie erneut.",
```

Write the German with real umlauts (`Beiträge`, `Schaltfläche`, `Zusammenführen`, `Für`) - the catalogs are UTF-8 and the surrounding entries use them. The ASCII spellings above are only to survive this plan file; **do not** paste them as-is.

`apps/web/src/locales/es/account.json`:

```json
  "autoMergeHeading": "Unir en un solo bloque las intervenciones seguidas de cada persona",
  "autoMergeBody": "Cuando una grabacion termina de transcribirse, las filas consecutivas de la misma persona se unen automaticamente: lo mismo que hace el boton Unir de la transcripcion, sin el clic. Las notas y las capturas siguen siendo limites. Es permanente para esa transcripcion: para recuperar las filas cortas, desactiva esta opcion y vuelve a transcribir.",
```

Again, write it with real accents (`grabación`, `automáticamente`, `botón`, `transcripción`, `límites`, `opción`).

`apps/web/src/locales/fr/account.json`:

```json
  "autoMergeHeading": "Regrouper en un seul bloc les prises de parole consecutives d'une meme personne",
  "autoMergeBody": "Lorsqu'un enregistrement finit d'etre transcrit, les lignes consecutives d'une meme personne sont regroupees automatiquement - ce que fait le bouton Fusionner de la transcription, sans le clic. Les notes et les captures d'ecran restent des separations. C'est definitif pour cette transcription : pour retrouver des lignes courtes, desactivez cette option et relancez la transcription.",
```

Again, real accents (`consécutives`, `même`, `d'être`, `regroupées`, `d'écran`, `séparations`, `définitif`, `désactivez`).

- [ ] **Step 8: Run the tests to verify they pass**

```bash
cd apps/web && npx vitest run src/components/RecordingsSection.test.tsx src/locales.test.ts
```

Expected: PASS, including the two edited exact-payload tests and the locale key-parity gate.

- [ ] **Step 9: Typecheck and run the whole web suite**

```bash
cd apps/web && npm run build && npx vitest run
```

Expected: build succeeds (`tsc` clean) and the full vitest suite passes.

- [ ] **Step 10: Commit**

```bash
git add apps/web/src/lib/types.ts apps/web/src/components/icons.tsx apps/web/src/components/RecordingsSection.tsx apps/web/src/components/RecordingsSection.test.tsx apps/web/src/locales/en/account.json apps/web/src/locales/de/account.json apps/web/src/locales/es/account.json apps/web/src/locales/fr/account.json
git commit -m "feat: add the auto-merge switch to Preferences > Recordings

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 6: Documentation - endpoint description, help article, feature inventories

The endpoint currently tells users to re-transcribe to undo a merge. With auto-merge on, that is wrong, so the copy has to change alongside the feature.

**Files:**
- Modify: `src/Diariz.Api/Controllers/RecordingsController.cs` (the `[EndpointDescription]` on `MergeSegments`)
- Modify: `apps/web/src/content/help/en/transcription-and-speakers.md` (the **Merge** bullet, line ~89)
- Modify: `README.md:26` (Features table)
- Modify: `docs/features.md` (the Transcribe + diarize bullet, line ~64)
- Modify: `apps/web/src/lib/releases.ts` (the `CAPABILITIES` "Transcribe & diarize" row)
- Modify: `docs/Overall_Synopsis_of_Platform.md` (the transcription flow, around line 444)
- Possibly modify: the OpenAPI snapshot file (regenerated)

**Interfaces:**
- Consumes: everything from Tasks 1-5.
- Produces: nothing.

- [ ] **Step 1: Update the endpoint description**

In `RecordingsController.cs`, in the `[EndpointDescription]` on `MergeSegments`, replace the final paragraph:

```csharp
        "**Permanent for this transcription version** - there is no un-merge. Re-transcribe to get granular " +
        "segments back (that creates a new version and leaves this one intact). If you have turned on " +
        "**auto-merge** in your recording preferences, a re-transcription is merged too - turn that off first.")]
```

- [ ] **Step 2: Update the help article**

In `apps/web/src/content/help/en/transcription-and-speakers.md`, replace the **Merge** bullet (line ~89) with:

```markdown
- **Merge** joins consecutive rows by the same speaker. Notes and screenshots act as boundaries, so
  text either side of them stays separate. Merging is permanent for that transcript. If you always end
  up doing it, turn on **Merge each speaker's turn into one block** in Preferences - Recordings, and
  every recording is merged for you as soon as it finishes transcribing. While that is on, transcribing
  a recording again produces a merged transcript too, so turn it off first if you want short rows back.
```

ASCII only - no accented characters, no em dashes. Then check whether line ~35 ("If two speakers were merged... **re-transcribe**") needs qualifying; it is about pyannote speaker-count hints, not row merging, so it most likely does not. Leave it alone unless it actually reads as a promise about row granularity.

- [ ] **Step 3: Verify the help content gate**

```bash
cd apps/web && npx vitest run src/content/help/helpContent.test.ts
```

Expected: PASS (it enforces ASCII and the front-matter block).

- [ ] **Step 4: Update the three feature inventories in lockstep**

`README.md` line 26 - append to the **Transcribe & diarize** cell, before the closing `|`:

```
 Optionally merge each speaker's consecutive rows automatically as soon as a recording finishes transcribing, rather than pressing Merge on every one.
```

`docs/features.md` - in the Transcribe + diarize bullet, after "**merge** consecutive same-speaker rows", add:

```
(or set it to happen automatically for every recording, from Preferences - Recordings, so a transcript
arrives already in speaker-sized blocks)
```

`apps/web/src/lib/releases.ts` - append the same idea to the `CAPABILITIES` table's **Transcribe & diarize** row:

```
 Optionally have every recording's consecutive same-speaker rows merged automatically once it finishes transcribing.
```

- [ ] **Step 5: Update the architecture doc**

In `docs/Overall_Synopsis_of_Platform.md`, in the paragraph after the numbered transcription flow (around line 444-448, the "**Re-transcribe** bumps the `Transcription.Version`..." paragraph), append:

```markdown
When the recording's owner has `UserSettings.AutoMergeSpeakerSegments` set, the callback runs
`TranscriptSegmentMerge.ApplyAsync` - the same helper behind `POST /api/recordings/{id}/merge-segments` -
after speaker identification is saved and before any downstream job is enqueued, so the summary, actions,
tags, embeddings, the SignalR notification and the webhook all see the merged shape. The call is wrapped in
a swallowed try/catch: an unmerged transcript is valid, but throwing there would strand the recording in
`Summarizing` with nothing queued to clear it. Because the merge runs before embedding, retrieval chunks are
built from merged segments and are therefore coarser (`TranscriptChunker` never splits a segment) - an
accepted trade-off of the setting.
```

- [ ] **Step 5b: Verify the doc claim you just wrote**

Re-read the paragraph against `WorkerCallbackController.Result` as it now stands. Every clause must be literally true of the code. If the hook ended up somewhere else, fix the doc, not the memory of it.

- [ ] **Step 6: Regenerate the OpenAPI snapshot**

The endpoint description changed, so the snapshot is stale. The snapshot test rewrites its own snapshot file, so the first run fails and the second passes with no code change - this is expected, not a flake.

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApiSnapshot"
```

Run it a second time and confirm PASS. Then `git status` and stage the regenerated snapshot file - committing it is required, or CI fails on the same first-run rewrite.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Controllers/RecordingsController.cs apps/web/src/content/help/en/transcription-and-speakers.md README.md docs/features.md apps/web/src/lib/releases.ts docs/Overall_Synopsis_of_Platform.md
git status --short
```

Inspect the `git status --short` output and `git add` the regenerated OpenAPI snapshot file by its explicit path. **Never `git add -A`.** Then:

```bash
git commit -m "docs: document auto-merge across help, features, and the architecture synopsis

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 7: Version bump, release notes, and the PR

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`, `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts` (new `RELEASES[0]` entry)

**Interfaces:**
- Consumes: everything from Tasks 1-6.
- Produces: nothing.

- [ ] **Step 1: Run the version tests to see them fail**

First bump `version.json` only:

```bash
node -e "require('fs').writeFileSync('version.json', JSON.stringify({version:'0.229.0'})+'\n')"
cd apps/web && npx vitest run src/lib/versionMirrors.test.ts src/lib/releases.test.ts
```

Expected: FAIL - the mirrors still say `0.228.4`, and `RELEASES[0].version` does not equal `version.json`. This is the red for this task; the two tests exist because the n8n node silently sat at `0.1.0` for ~70 releases and a published npm version cannot be corrected.

- [ ] **Step 2: Bump all four mirrors**

Set `"version": "0.229.0"` in `apps/web/package.json`, `apps/desktop/package.json`, and `integrations/n8n-nodes-diariz/package.json`; set `<Version>0.229.0</Version>` in `src/Diariz.Api/Diariz.Api.csproj`.

- [ ] **Step 3: Add the release entry**

Insert as the new first element of `RELEASES` in `apps/web/src/lib/releases.ts`. Leave `pr` as `0` for now - the real number is filled in at Step 7, once the PR exists:

```ts
  {
    version: "0.229.0",
    date: "2026-08-19",
    pr: 0,
    headline: "Transcripts that arrive already merged by speaker",
    summary:
      "A transcript arrives split into a new row every time the speaker changes and again on every pause, so one person's uninterrupted answer can land as twenty rows. The transcript toolbar has always had a Merge button that joins them back up - and if you use it, you use it on every single recording.\n\nThis release lets you stop pressing it. In Preferences, on the Recordings tab, a new switch merges each speaker's consecutive rows automatically as soon as a recording finishes transcribing and its speakers have been identified. It is the same merge the button does, so notes and screenshots still act as boundaries, and two voices that Diariz recognised as the same person are joined as one speaker.\n\nIt is off by default and nothing changes until you turn it on. One thing to know before you do: merging is permanent for that transcript, and with this on, transcribing a recording again produces a merged transcript too - so if you want the short rows back, turn the switch off first. Merged transcripts also give the assistant larger blocks of text to search, which can make it less precise about which moment it points you to.",
    added: [
      "Preferences - Recordings: Merge each speaker's turn into one block, off by default. When on, every recording's consecutive same-speaker rows are joined as soon as it finishes transcribing.",
    ],
  },
```

- [ ] **Step 4: Run the version tests to verify they pass**

```bash
cd apps/web && npx vitest run src/lib/versionMirrors.test.ts src/lib/releases.test.ts
```

Expected: PASS.

- [ ] **Step 5: Full verification before pushing**

```bash
dotnet build Diariz.slnx
```

```bash
dotnet test
```

```bash
cd apps/web && npm run build && npx vitest run
```

Expected: all three green, no warnings. `dotnet test` needs Docker for the integration project.

- [ ] **Step 6: Commit and push**

```bash
git add version.json apps/web/package.json apps/desktop/package.json src/Diariz.Api/Diariz.Api.csproj integrations/n8n-nodes-diariz/package.json apps/web/src/lib/releases.ts
git commit -m "chore: release 0.229.0 - auto-merge speaker segments

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
git push -u origin feat/auto-merge-speaker-segments
```

- [ ] **Step 7: Open the PR, then backfill its number**

```bash
gh pr create --title "feat: auto-merge speaker segments after transcription (0.229.0)" --body "$(cat <<'BODY'
Adds a per-user preference (default off) that collapses consecutive same-speaker transcript segments automatically, once transcription and voiceprint identification have finished. It runs the same `TranscriptSegmentMerge` helper as the transcript toolbar's Merge action, extracted from `RecordingsController` so there is exactly one merge implementation.

- **Preferences - Recordings** gains a switch; `UserSettings.AutoMergeSpeakerSegments` (additive, defaulted, forward-restore-safe - no `CurrentFormat` bump).
- The hook sits in `WorkerCallbackController.Result` after speaker identification is saved and before every downstream enqueue, so summary, actions, tags, embeddings, SignalR and webhooks all see one final shape.
- Known trade-off, stated in the UI and the help article: `TranscriptChunker` never splits a segment, so merged segments make RAG retrieval chunks coarser.
- With the setting on, re-transcribing also produces a merged transcript. Turning the setting off and re-transcribing is the route back to granular segments; the endpoint description and help article now say so.

**Deployment surface:** server redeploy only (API + web). No desktop release - no `apps/desktop/src`, `build`, or `electron-builder.config.js` file is touched.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
BODY
)"
```

Read the PR number from the command's output URL. Do **not** guess it as "last + 1": issues and Dependabot PRs share the same sequence, and no test catches a wrong `pr:` value. Then:

```bash
node -e "const f='apps/web/src/lib/releases.ts';const s=require('fs').readFileSync(f,'utf8');require('fs').writeFileSync(f,s.replace('pr: 0,','pr: <THE-REAL-NUMBER>,'))"
cd apps/web && npx vitest run src/lib/releases.test.ts
```

```bash
git add apps/web/src/lib/releases.ts
git commit -m "chore: record the PR number in the 0.229.0 release entry

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
git push
```

- [ ] **Step 8: Confirm CI**

```bash
gh pr checks --watch
```

Expected: all required checks green. `main` uses a strict up-to-date policy, so if other PRs merge first this branch must be rebased before it can land.

---

## Verification summary

Before claiming the work complete, all of these must have been run and seen to pass:

```bash
dotnet build Diariz.slnx
```

```bash
dotnet test
```

```bash
cd apps/web && npm run build && npx vitest run
```

Plus the specific evidence each task calls for: the four existing `MergeSegments_*` tests passing **unedited** after Task 2's extraction, and the deliberate temporary flip in Task 4 Step 3 proving the integration test can fail.
