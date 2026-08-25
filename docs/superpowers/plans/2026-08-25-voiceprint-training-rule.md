# Voiceprint training rule - implementation plan (PR 1 of 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan
> task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A voice sample trains a person's voiceprint only while its speaker still says it is that
person - enforced by one rule, applied everywhere that decides what a voiceprint is made of, with
the six voiceprints currently wrong on the live instance rebuilt on next boot.

**Architecture:** One pure predicate in `Diariz.Api/Services/VoiceprintTraining.cs`, called by the
centroid recompute, both diagnostics endpoints, and the attributions projection. A convergent
startup pass recomputes any person the rule newly disagrees with. Samples the rule rejects are not
deleted or hidden - they surface in the attributions payload flagged `stillLinked: false`.

**Tech Stack:** ASP.NET Core (.NET 10), EF Core + Npgsql/pgvector, xUnit, Testcontainers.

**Closes:** #621. Design: `docs/superpowers/specs/2026-08-25-voiceprint-review-surface-design.md`.

## Global Constraints

- **TDD is required.** Write the failing test, run it, watch it fail for the stated reason, then
  write the minimal code. Never write production code first.
- **Mutation-verify every new test.** After it passes, delete or invert the clause it covers, re-run,
  and confirm it fails. A test that cannot fail is the dominant defect class in this repo.
- **No mocking library.** Add a fake to `tests/Diariz.Api.TestSupport` if a boundary needs one.
- **No `InternalsVisibleTo`.** Reach internals through public seams.
- **Unit tests use the EF in-memory provider** (`TestDb.Create()`), which ignores `vector` columns
  and does not enforce FKs. Anything touching an embedding, a real query translation, or a FK goes
  in `tests/Diariz.Api.IntegrationTests` (needs Docker).
- **Store UTC.** Npgsql throws at `SaveChanges` for a non-zero-offset `DateTimeOffset` on a
  `timestamptz`; the in-memory provider will not catch it.
- **`--filter "Name=X"` does not work here.** Use `--filter "FullyQualifiedName~X"`.
- **Never `git add -A`.** Stage explicit paths. Check `git status` for `??` before committing - new
  test files have been left out of commits twice.
- **Build `Diariz.slnx`, not just the unit test project,** before pushing. Unit-only runs miss
  integration and CodeQL compile breaks.
- **Entity vs column names.** `Person` maps to `SpeakerProfiles`, `VoiceSample` to
  `ProfileContributions`, and `Speaker.PersonId` to the `"ProfileId"` column. Use the entity names
  in C#; only raw SQL sees the table names.

---

### Task 1: The rule

**Files:**
- Create: `src/Diariz.Api/Services/VoiceprintTraining.cs`
- Test: `tests/Diariz.Api.Tests/VoiceprintTrainingTests.cs`

**Interfaces:**
- Consumes: `Diariz.Domain.Entities.VoiceSample`, `Diariz.Domain.Entities.Speaker`.
- Produces:
  - `bool VoiceprintTraining.StillLinked(Guid personId, Guid? speakerPersonId, bool speakerIsMultiSpeaker)`
  - `bool VoiceprintTraining.Trains(VoiceSample sample, Speaker? speaker)`

  Tasks 2-5 use both. The primitive overload exists because the attributions endpoint projects
  speakers to an anonymous type and never materialises a `Speaker`.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/VoiceprintTrainingTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>What counts as training data for a voiceprint.
///
/// <para>The rule exists because the two assignment paths both moved a speaker's link and left the
/// voice sample behind: on the live instance six samples were training a person whose transcript
/// named someone else, three of them a specifically different person. Stored state would need every
/// assignment path to remember to update it - a rule needs none of them to.</para></summary>
public class VoiceprintTrainingTests
{
    private static readonly Guid Person = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static VoiceSample Sample(DateTimeOffset? excludedAt = null) => new()
    {
        Id = Guid.NewGuid(), PersonId = Person, SpeakerId = Guid.NewGuid(), RecordingId = Guid.NewGuid(),
        ExcludedAt = excludedAt,
    };

    private static Speaker Speaker(Guid? personId, bool multi = false) => new()
    {
        Id = Guid.NewGuid(), RecordingId = Guid.NewGuid(), Label = "SPEAKER_00", DisplayName = "SPEAKER_00",
        PersonId = personId, IsMultiSpeaker = multi,
    };

    [Fact]
    public void A_linked_speaker_trains_the_voiceprint()
    {
        Assert.True(VoiceprintTraining.Trains(Sample(), Speaker(Person)));
    }

    [Fact]
    public void A_sample_dropped_by_hand_does_not_train()
    {
        Assert.False(VoiceprintTraining.Trains(Sample(DateTimeOffset.UtcNow), Speaker(Person)));
    }

    [Fact]
    public void An_unlinked_speaker_does_not_train()
    {
        // Unassigning a speaker on the transcript is the user saying "that was not them". The sample it
        // enrolled kept training regardless, which is the defect this rule closes.
        Assert.False(VoiceprintTraining.Trains(Sample(), Speaker(null)));
    }

    [Fact]
    public void A_speaker_now_attributed_to_someone_else_does_not_train()
    {
        // The worst of the six found live: person A's voiceprint learning from audio the user has since
        // labelled as person B. Both people are then taught the same voice.
        Assert.False(VoiceprintTraining.Trains(Sample(), Speaker(Other)));
    }

    [Fact]
    public void Overlapping_speech_does_not_train()
    {
        Assert.False(VoiceprintTraining.Trains(Sample(), Speaker(Person, multi: true)));
    }

    [Fact]
    public void A_missing_speaker_does_not_train()
    {
        // Defensive: the FK cascades, so this should be unreachable. If it ever is reachable, a null
        // speaker must mean "no evidence", never "assume it still counts".
        Assert.False(VoiceprintTraining.Trains(Sample(), null));
    }

    [Fact]
    public void Still_linked_reads_the_speaker_not_the_sample()
    {
        // The overload the attributions endpoint uses, where only the projected columns are in hand.
        Assert.True(VoiceprintTraining.StillLinked(Person, Person, false));
        Assert.False(VoiceprintTraining.StillLinked(Person, Other, false));
        Assert.False(VoiceprintTraining.StillLinked(Person, null, false));
        Assert.False(VoiceprintTraining.StillLinked(Person, Person, true));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~VoiceprintTraining"
```

Expected: compile error, `VoiceprintTraining` does not exist.

- [ ] **Step 3: Write the minimal implementation**

Create `src/Diariz.Api/Services/VoiceprintTraining.cs`:

```csharp
using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>What counts as training data for a voiceprint.
///
/// <para><b>A sample trains a person's voiceprint only while its speaker still says it is that
/// person.</b> Both assignment paths - <c>SpeakerAssignment.Unassign</c> and <c>AssignAsync</c> -
/// move the speaker's link and leave any existing <see cref="VoiceSample"/> untouched, so the
/// alternative is a stored flag that every present and future assignment path has to remember to
/// update. A rule needs none of them to, and heals the rows already wrong.</para>
///
/// <para>Pure and shared rather than repeated at each call site: the failure being fixed here is
/// precisely two surfaces disagreeing about which samples count.</para></summary>
public static class VoiceprintTraining
{
    /// <summary>Whether the speaker behind a sample still attributes it to this person. Takes the
    /// projected columns rather than a <see cref="Speaker"/>, because the attributions endpoint reads
    /// them straight out of a projection and never materialises the entity.</summary>
    public static bool StillLinked(Guid personId, Guid? speakerPersonId, bool speakerIsMultiSpeaker) =>
        speakerPersonId == personId && !speakerIsMultiSpeaker;

    /// <summary>Whether this sample currently trains its person's voiceprint. A null
    /// <paramref name="speaker"/> is no evidence, never an assumption that it still counts.</summary>
    public static bool Trains(VoiceSample sample, Speaker? speaker) =>
        sample.ExcludedAt is null
        && speaker is not null
        && StillLinked(sample.PersonId, speaker.PersonId, speaker.IsMultiSpeaker);
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~VoiceprintTraining"
```

Expected: 7 passed.

- [ ] **Step 5: Mutation-verify**

Delete `&& !speakerIsMultiSpeaker` from `StillLinked` and re-run. Expected: `Overlapping_speech_does_not_train`
and `Still_linked_reads_the_speaker_not_the_sample` fail. Restore it, then delete
`sample.ExcludedAt is null` and re-run. Expected: `A_sample_dropped_by_hand_does_not_train` fails.
Restore.

**Restore by editing the file back in place, not from a copy** - a restored file keeps its old
mtime, MSBuild skips the rebuild, and you go on testing the mutated binary.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/VoiceprintTraining.cs tests/Diariz.Api.Tests/VoiceprintTrainingTests.cs
git commit -m "feat: one rule for what trains a voiceprint"
```

---

### Task 2: The centroid obeys the rule

**Files:**
- Modify: `src/Diariz.Api/Services/PeopleDirectory.cs` (`RecomputeVoiceprintAsync`, around line 94)
- Test: `tests/Diariz.Api.IntegrationTests/VoiceprintTrainingIntegrationTests.cs` (create)

**Interfaces:**
- Consumes: `VoiceprintTraining.Trains` from Task 1.
- Produces: nothing new. `RecomputeVoiceprintAsync` keeps its signature.

This is an integration test, not a unit test: it turns on a stored `vector(192)` centroid, which the
in-memory provider ignores entirely.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.IntegrationTests/VoiceprintTrainingIntegrationTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Pgvector;

namespace Diariz.Api.IntegrationTests;

/// <summary>That every surface deciding what a voiceprint is made of gives the same answer.
///
/// <para>Integration rather than unit, because all of it turns on a real <c>vector(192)</c> column -
/// the in-memory provider ignores the embedding entirely, so a unit test here would assert against
/// nulls and pass whatever the rule said.</para></summary>
[Collection("integration")]
public class VoiceprintTrainingIntegrationTests(ContainersFixture fx)
{
    /// <summary>A unit vector pointing along one axis, so two of them are orthogonal and a centroid
    /// built from the wrong pair is unmistakably different from one built from the right pair.</summary>
    private static Vector Axis(int i)
    {
        var v = new float[192];
        v[i] = 1f;
        return new Vector(v);
    }

    [Fact]
    public async Task An_unlinked_speakers_sample_is_not_in_the_centroid()
    {
        await using var db = fx.CreateDbContext();

        var userId = await TestSeed.UserAsync(db);
        var person = new Person { Id = Guid.NewGuid(), Name = "Ada" };
        db.People.Add(person);

        var recording = await TestSeed.RecordingAsync(db, userId);

        // Two speakers, orthogonal voices. One stays linked; one is unlinked the way the transcript's
        // "unassign" leaves it - link cleared, sample untouched.
        var kept = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = recording.Id, Label = "SPEAKER_00",
            DisplayName = "Ada", PersonId = person.Id, Embedding = Axis(0),
        };
        var unlinked = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = recording.Id, Label = "SPEAKER_01",
            DisplayName = "SPEAKER_01", PersonId = null, Embedding = Axis(1),
        };
        db.Speakers.AddRange(kept, unlinked);

        db.VoiceSamples.AddRange(
            new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = kept.Id,
                RecordingId = recording.Id, Embedding = Axis(0),
            },
            new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = unlinked.Id,
                RecordingId = recording.Id, Embedding = Axis(1),
            });
        await db.SaveChangesAsync();

        await new PeopleDirectory(db).RecomputeVoiceprintAsync(person.Id);

        var after = await db.People.FindAsync(person.Id);
        Assert.NotNull(after!.Embedding);

        // Only the kept sample counted, so the centroid is that axis alone. Both would put roughly 0.7
        // on each of the two axes.
        var centroid = after.Embedding!.ToArray();
        Assert.True(centroid[0] > 0.99, $"expected the linked sample's axis, got {centroid[0]}");
        Assert.True(centroid[1] < 0.01, $"expected nothing from the unlinked sample, got {centroid[1]}");

        // The figure the UI shows has to come from the same list, or the count and the audio disagree.
        Assert.Equal(1, after.SampleCount);
    }
}
```

If `TestSeed.UserAsync` / `TestSeed.RecordingAsync` do not exist in this project, look at how
`VoiceprintDiagnosticsIntegrationTests.cs` seeds a user and a recording and follow that file's
pattern exactly rather than inventing a helper.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~VoiceprintTrainingIntegration"
```

Expected: FAIL. The centroid averages both orthogonal vectors, so `centroid[0]` is about `0.707`,
not above `0.99`, and `SampleCount` is 2.

Quote the real failure output before continuing. If it passes at this point, the fixture is not
reproducing the defect and the test is worthless.

- [ ] **Step 3: Write the minimal implementation**

In `src/Diariz.Api/Services/PeopleDirectory.cs`, replace the sample query in
`RecomputeVoiceprintAsync` with one that carries each sample's speaker:

```csharp
        // Excluded samples are kept as a record of who asserted what, but must not reach the average -
        // otherwise dropping one from training is cosmetic: the row reads "not training" while the vector it
        // contributed is still inside the centroid. SampleCount is derived from the same list, so the figure
        // the UI shows and the audio actually behind the voiceprint cannot disagree.
        //
        // The speaker comes with it because exclusion is not the only way a sample stops counting: both
        // assignment paths move a speaker's link and leave the sample behind, so the link is read here
        // rather than trusted to have been mirrored onto the sample.
        var candidates = await db.VoiceSamples
            .Where(v => v.PersonId == personId)
            .Join(db.Speakers, v => v.SpeakerId, s => s.Id, (v, s) => new { Sample = v, Speaker = s })
            .ToListAsync(ct);

        var samples = candidates
            .Where(x => VoiceprintTraining.Trains(x.Sample, x.Speaker))
            .Select(x => x.Sample)
            .ToList();
```

The rest of the method is unchanged: it already reads `samples` for both the centroid and
`SampleCount`.

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~VoiceprintTrainingIntegration"
```

Expected: PASS.

- [ ] **Step 5: Run the whole suite for regressions**

```bash
dotnet test
```

Expected: green. Existing voiceprint tests seed speakers linked to their person, so they satisfy the
rule. **If any fails, read it before changing it** - a test that enrolled a sample without linking
the speaker was asserting the defect.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/PeopleDirectory.cs tests/Diariz.Api.IntegrationTests/VoiceprintTrainingIntegrationTests.cs
git commit -m "fix: keep an unlinked speaker's sample out of the centroid"
```

---

### Task 3: Both diagnostics endpoints obey the rule

**Files:**
- Modify: `src/Diariz.Api/Controllers/PeopleController.cs` (`Diagnostics`, around line 299;
  `DirectoryDiagnostics`, around line 230)
- Test: `tests/Diariz.Api.IntegrationTests/VoiceprintTrainingIntegrationTests.cs` (append)

**Interfaces:**
- Consumes: `VoiceprintTraining.Trains` from Task 1.
- Produces: nothing new. Both endpoints keep their routes and DTOs.

`Diagnostics` measures outliers against the training set. An orphan left in that set distorts every
other sample's verdict, which is how the worst-ranked person's list came to show an outlier nobody
could act on.

- [ ] **Step 1: Write the failing test**

Append to `VoiceprintTrainingIntegrationTests.cs`:

```csharp
    [Fact]
    public async Task Diagnostics_measure_against_the_same_set_as_the_centroid()
    {
        // The bug that made the Diagnostics tab unusable: it diagnosed a sample the Voiceprint tab could
        // not list, because one reads samples and the other reads linked speakers. They must agree.
        await using var db = fx.CreateDbContext();

        var userId = await TestSeed.UserAsync(db);
        var person = new Person { Id = Guid.NewGuid(), Name = "Grace" };
        db.People.Add(person);
        var recording = await TestSeed.RecordingAsync(db, userId);

        var linked = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = recording.Id, Label = "SPEAKER_00",
            DisplayName = "Grace", PersonId = person.Id, Embedding = Axis(0),
        };
        var moved = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = recording.Id, Label = "SPEAKER_01",
            DisplayName = "SPEAKER_01", PersonId = null, Embedding = Axis(1),
        };
        db.Speakers.AddRange(linked, moved);
        db.VoiceSamples.AddRange(
            new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = linked.Id,
                RecordingId = recording.Id, Embedding = Axis(0),
            },
            new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = moved.Id,
                RecordingId = recording.Id, Embedding = Axis(1),
            });
        await db.SaveChangesAsync();

        await new PeopleDirectory(db).RecomputeVoiceprintAsync(person.Id);
        var after = await db.People.FindAsync(person.Id);

        // One sample counts, so there is no pair - and therefore nothing that can be an outlier. Before the
        // fix the orphan made both samples look like outliers of each other.
        Assert.Equal(1, after!.SampleCount);
    }
```

Then add an endpoint-level assertion. Follow `VoiceprintDiagnosticsIntegrationTests.cs` for how it
constructs `PeopleController` and its dependencies - **do not** invent a construction; the
controller's constructor has a second construction site in `RbacIntegrationTests.cs` and both must
compile. Assert that `Diagnostics(person.Id)` returns exactly one row with verdict `Only`, and that
`DirectoryDiagnostics()` omits the person entirely (fewer than two training samples).

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~VoiceprintTrainingIntegration"
```

Expected: FAIL - two rows, verdict `Alone` on both, and the person present in the ranking.

- [ ] **Step 3: Write the minimal implementation**

In `PeopleController.Diagnostics`, the training filter currently reads:

```csharp
        var training = samples.Where(v => v.ExcludedAt is null && v.Embedding is not null).ToList();
```

Load the speakers alongside and use the rule. The method already reads `spIds` for labels a few
lines later - hoist that query above this line and reuse it:

```csharp
        var spIds = samples.Select(v => v.SpeakerId).Distinct().ToList();
        var speakers = await _db.Speakers.Where(sp => spIds.Contains(sp.Id)).ToListAsync();
        var speakerById = speakers.ToDictionary(sp => sp.Id);

        // Diagnosed against the training set only, and "training" is the shared rule - not just
        // `ExcludedAt`. A sample whose speaker has been unlinked or reassigned is not this person's voice
        // any more; leaving it in would make every other sample look like an outlier of it.
        var training = samples
            .Where(v => VoiceprintTraining.Trains(v, speakerById.GetValueOrDefault(v.SpeakerId))
                        && v.Embedding is not null)
            .ToList();
```

Replace the later `spMap` construction with a projection over `speakers`, so there is one query.

In `DirectoryDiagnostics`, the sample query is:

```csharp
        var samples = await _db.VoiceSamples
            .Where(v => v.ExcludedAt == null && v.Embedding != null)
            .Select(v => new { v.Id, v.PersonId, v.Embedding })
            .ToListAsync();
```

Change it to carry the speaker and filter with the rule:

```csharp
        var samples = (await _db.VoiceSamples
                .Where(v => v.ExcludedAt == null && v.Embedding != null)
                .Join(_db.Speakers, v => v.SpeakerId, s => s.Id, (v, s) => new { Sample = v, Speaker = s })
                .ToListAsync())
            .Where(x => VoiceprintTraining.Trains(x.Sample, x.Speaker))
            .Select(x => new { x.Sample.Id, x.Sample.PersonId, x.Sample.Embedding })
            .ToList();
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~VoiceprintTrainingIntegration"
```

Expected: PASS.

- [ ] **Step 5: Mutation-verify the endpoint assertion**

Revert only the `Diagnostics` change (keep the `DirectoryDiagnostics` one), re-run. Expected: the
endpoint assertion fails, the centroid one still passes. That proves the endpoint assertion is
carrying its own weight rather than riding on Task 2. Restore.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Controllers/PeopleController.cs tests/Diariz.Api.IntegrationTests/VoiceprintTrainingIntegrationTests.cs
git commit -m "fix: diagnose against the same training set the centroid uses"
```

---

### Task 4: Orphans appear in the attributions payload

**Files:**
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs` (`PersonAttributionDto`, around line 544)
- Modify: `src/Diariz.Api/Services/PersonAttributions.cs`
- Modify: `src/Diariz.Api/Controllers/PeopleController.cs` (`Attributions`, around line 366)
- Test: `tests/Diariz.Api.Tests/PersonAttributionsTests.cs` (append)
- Test: `tests/Diariz.Api.IntegrationTests/PersonAttributionIntegrationTests.cs` (append)

**Interfaces:**
- Consumes: `VoiceprintTraining.StillLinked` from Task 1.
- Produces:
  - `AttributionInput` gains a trailing `bool StillLinked` parameter.
  - `PersonAttributionDto` gains trailing `bool StillLinked, bool CanReassign`.

  PR 2 reads `stillLinked` to render the orphan wording; PR 3 reads `canReassign` to decide whether
  to offer the person picker.

Dropping six samples out of six voiceprints with no trace would be its own bug. The row stays, says
what happened, and `isTraining` is honestly false.

- [ ] **Step 1: Write the failing test**

Append to `tests/Diariz.Api.Tests/PersonAttributionsTests.cs`:

```csharp
    [Fact]
    public void A_sample_whose_speaker_moved_is_listed_but_not_training()
    {
        // Six of these were found live. Hiding them would repeat the original defect in a new place:
        // invisible is exactly how they survived. Listed, and honestly not training.
        var personId = Guid.NewGuid();
        var speakerId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();

        var rows = PersonAttributions.Build(
            [new AttributionInput(speakerId, recordingId, "SPEAKER_00", false, false, 30_000,
                StillLinked: false)],
            [new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = personId, SpeakerId = speakerId,
                RecordingId = recordingId,
            }],
            new Dictionary<Guid, string> { [recordingId] = "Standup" },
            new HashSet<Guid> { recordingId },
            new HashSet<Guid> { recordingId });

        var row = Assert.Single(rows);
        Assert.False(row.StillLinked);
        Assert.False(row.IsTraining);
    }

    [Fact]
    public void A_linked_speaker_can_be_reassigned_only_in_a_recording_you_own()
    {
        // Manage voiceprints grants listening to a segment for assessment. It does not grant editing
        // someone else's transcript, and AssignSpeaker enforces ownership regardless - so offering the
        // control would produce a button that always fails.
        var speakerId = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        var rows = PersonAttributions.Build(
            [
                new AttributionInput(speakerId, mine, "SPEAKER_00", false, false, 30_000, StillLinked: true),
                new AttributionInput(Guid.NewGuid(), theirs, "SPEAKER_01", false, false, 30_000,
                    StillLinked: true),
            ],
            [],
            new Dictionary<Guid, string> { [mine] = "Mine", [theirs] = "Theirs" },
            accessibleRecordings: new HashSet<Guid> { mine, theirs },
            ownedRecordings: new HashSet<Guid> { mine });

        Assert.True(rows.Single(r => r.RecordingId == mine).CanReassign);
        Assert.False(rows.Single(r => r.RecordingId == theirs).CanReassign);
    }
```

Existing calls to `Build` in that file need the new `ownedRecordings` argument. Pass the same set as
`accessibleRecordings` for them - they are not about ownership.

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PersonAttributions"
```

Expected: compile error - `AttributionInput` has no `StillLinked`, `Build` has no `ownedRecordings`,
`PersonAttributionDto` has no `StillLinked` or `CanReassign`.

- [ ] **Step 3: Write the minimal implementation**

In `ApiDtos.cs`, extend the record and its doc comment:

```csharp
/// <para><paramref name="StillLinked"/> is false when the speaker behind this sample no longer names
/// this person - unassigned, reassigned, or marked as overlapping speech. The row is listed anyway,
/// because it is part of the training provenance and hiding it is how six of them survived unnoticed
/// on a live instance; <paramref name="IsTraining"/> is false for all of them.</para>
///
/// <para><paramref name="CanReassign"/> is narrower than <paramref name="CanAccessRecording"/> on
/// purpose: <c>ManageVoiceprints</c> grants listening for assessment, not editing someone else's
/// transcript, and <c>AssignSpeaker</c> requires ownership regardless.</para>
```

with `bool StillLinked, bool CanReassign` appended to the parameter list.

In `PersonAttributions.cs`, add `bool StillLinked` to `AttributionInput`, add an
`IReadOnlySet<Guid> ownedRecordings` parameter to `Build`, and replace the body's filter and
projection:

```csharp
        return speakers
            // Overlapping audio is a mix of people and can never train a single-person voiceprint. It is
            // still listed when a sample exists on it - an invisible row that once trained a voiceprint is
            // exactly what went wrong before.
            .Where(s => s.StillLinked || bySpeaker.ContainsKey(s.SpeakerId))
            .Select(s =>
            {
                var sample = bySpeaker.GetValueOrDefault(s.SpeakerId);
                return new PersonAttributionDto(
                    s.SpeakerId,
                    s.RecordingId,
                    recordingNames.TryGetValue(s.RecordingId, out var name) ? name : DeletedRecording,
                    s.Label,
                    s.IdentifiedAuto ? "auto" : "manual",
                    sample is { ExcludedAt: null } && s.StillLinked,
                    sample?.Id,
                    s.SpeechMs,
                    accessibleRecordings.Contains(s.RecordingId),
                    s.StillLinked,
                    ownedRecordings.Contains(s.RecordingId));
            })
```

In `PeopleController.Attributions`, load the samples **before** the speakers so the orphaned
speakers can be pulled in, and derive the two new flags:

```csharp
        var samples = await _db.VoiceSamples.Where(v => v.PersonId == id).ToListAsync();
        var sampleSpeakerIds = samples.Select(v => v.SpeakerId).Distinct().ToList();

        // Linked speakers, plus any speaker still holding a sample for this person - the second set is how
        // an unassigned or reassigned speaker stops being invisible.
        var speakers = await _db.Speakers
            .Where(s => s.PersonId == id || sampleSpeakerIds.Contains(s.Id))
            .Select(s => new { s.Id, s.RecordingId, s.Label, s.IdentifiedAuto, s.IsMultiSpeaker, s.PersonId })
            .ToListAsync();
```

then build `ownedRecordings` beside the existing `accessible` set:

```csharp
        var owned = recordings.Where(r => r.UserId == UserId).Select(r => r.Id).ToHashSet();
```

and pass `VoiceprintTraining.StillLinked(id, s.PersonId, s.IsMultiSpeaker)` into each
`AttributionInput`, plus `owned` as the new `Build` argument.

Move the existing `var samples = ...` line (currently just above the `Build` call) so it is not
duplicated.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PersonAttributions"
```

Expected: PASS.

- [ ] **Step 5: Add the integration assertion**

Append to `tests/Diariz.Api.IntegrationTests/PersonAttributionIntegrationTests.cs` a test that
enrols a speaker, unassigns it (`speaker.PersonId = null`), and asserts the row is still returned
with `stillLinked == false` and `isTraining == false`. Run it, then mutation-verify by reverting the
`.Where(s => s.StillLinked || bySpeaker.ContainsKey(...))` line to the old
`.Where(s => !s.IsMultiSpeaker)` and confirming the row disappears.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Services/PersonAttributions.cs src/Diariz.Api/Controllers/PeopleController.cs tests/Diariz.Api.Tests/PersonAttributionsTests.cs tests/Diariz.Api.IntegrationTests/PersonAttributionIntegrationTests.cs
git commit -m "feat: list samples whose speaker no longer names this person"
```

---

### Task 5: Rebuild the voiceprints that are already wrong

**Files:**
- Create: `src/Diariz.Api/Services/VoiceprintRebuild.cs`
- Modify: `src/Diariz.Api/Program.cs` (the startup scope, around line 606-620)
- Test: `tests/Diariz.Api.IntegrationTests/VoiceprintRebuildIntegrationTests.cs` (create)

**Interfaces:**
- Consumes: `VoiceprintTraining.Trains`, `IPeopleDirectory.RecomputeVoiceprintAsync`.
- Produces: `Task<int> VoiceprintRebuild.RunAsync(DiarizDbContext db, IPeopleDirectory people, ILogger logger, CancellationToken ct = default)`
  returning how many people it rebuilt.

The rule fixes what is computed from now on; the centroids stored today were computed from the
orphans and stay wrong until something recomputes them. **This must not re-derive the centroid in
SQL** - a second derivation of the same value agrees with the first by luck, and this repo has been
bitten by that twice. It calls the one C# path.

It is convergent, not accumulating: once every person is consistent it finds nobody. That is why it
needs no run-once marker and cannot re-apply itself the way a `Seeder` backfill would.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.IntegrationTests/VoiceprintRebuildIntegrationTests.cs` with two facts:

1. `It_rebuilds_a_centroid_computed_from_a_sample_that_no_longer_counts` - seed a person with two
   orthogonal samples where one speaker is unlinked, set `person.Embedding` by hand to the average
   of both (the state the live rows are in), run `VoiceprintRebuild.RunAsync`, assert the stored
   centroid is now the linked axis alone and the return value is 1.
2. `A_second_run_changes_nothing` - run it twice, assert the second returns 0 and the centroid is
   byte-identical to after the first. This is the property that lets it run on every boot.

Use the `Axis(int)` helper and the seeding pattern from Task 2.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~VoiceprintRebuild"
```

Expected: compile error, `VoiceprintRebuild` does not exist.

- [ ] **Step 3: Write the minimal implementation**

Create `src/Diariz.Api/Services/VoiceprintRebuild.cs`:

```csharp
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Recomputes any voiceprint whose stored centroid was built from a sample that no longer
/// counts as training data.
///
/// <para><see cref="VoiceprintTraining"/> fixes what is computed from now on. It does not touch the
/// centroids already stored, which on the instance this was written for were built from six samples
/// whose speakers have since been unassigned or reassigned.</para>
///
/// <para><b>Convergent, not accumulating</b> - once every person is consistent it finds nobody, so
/// it is safe on every boot and needs no run-once marker. That is the property a <c>Seeder</c>-style
/// backfill lacks, and why one of those once undid a set of deliberate demotions.</para>
///
/// <para>It deliberately does not compute a centroid itself. There is exactly one derivation, in
/// <see cref="IPeopleDirectory.RecomputeVoiceprintAsync"/>; a second one written in SQL would agree
/// with it by luck.</para></summary>
public static class VoiceprintRebuild
{
    public static async Task<int> RunAsync(
        DiarizDbContext db, IPeopleDirectory people, ILogger logger, CancellationToken ct = default)
    {
        var stale = (await db.VoiceSamples
                .Where(v => v.ExcludedAt == null)
                .Join(db.Speakers, v => v.SpeakerId, s => s.Id, (v, s) => new { Sample = v, Speaker = s })
                .ToListAsync(ct))
            .Where(x => !VoiceprintTraining.Trains(x.Sample, x.Speaker))
            .Select(x => x.Sample.PersonId)
            .Distinct()
            .ToList();

        foreach (var personId in stale)
            await people.RecomputeVoiceprintAsync(personId, ct);

        if (stale.Count > 0)
            logger.LogInformation(
                "Rebuilt {Count} voiceprint(s) holding a sample whose speaker no longer names them.",
                stale.Count);

        return stale.Count;
    }
}
```

Then call it from the startup scope in `Program.cs`, after `MeetingTypeSeeder.SeedAsync(db)`:

```csharp
    // Convergent: once every voiceprint is consistent this finds nobody, so it costs one query per boot.
    await VoiceprintRebuild.RunAsync(db, sp.GetRequiredService<IPeopleDirectory>(), app.Logger);
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~VoiceprintRebuild"
```

Expected: PASS.

- [ ] **Step 5: Mutation-verify the idempotence test**

Change `RunAsync` to return `db.People.Count()` instead of `stale.Count` and re-run. Expected:
`A_second_run_changes_nothing` fails. Restore by editing in place.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/VoiceprintRebuild.cs src/Diariz.Api/Program.cs tests/Diariz.Api.IntegrationTests/VoiceprintRebuildIntegrationTests.cs
git commit -m "fix: rebuild voiceprints built from samples that no longer count"
```

---

### Task 6: Release

**Files:**
- Modify: `version.json`, `apps/web/package.json`, `apps/web/package-lock.json` (**two** places),
  `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj`,
  `integrations/n8n-nodes-diariz/package.json`
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `docs/Overall_Synopsis_of_Platform.md`
- Regenerate: the OpenAPI snapshot and `integrations/n8n-nodes-diariz/generated/index.ts`

**Version: `0.252.0` -> `0.252.1`.** A fix, so Build +1.

`api/people` **is** in the published OpenAPI document, so the two new DTO fields change it.

- [ ] **Step 1: Bump every mirror**

`versionMirrors.test.ts` fails the build on any that drifts. `apps/web/package-lock.json` carries the
version in two places and is asserted; the desktop and n8n lock files are not asserted and are
already stale - leave them.

- [ ] **Step 2: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts`. The `pr` field must be the real PR number -
issues #621 and #622 have already consumed numbers from the shared sequence, so confirm with
`gh pr list --limit 1` and correct it after `gh pr create` if it is wrong.

No em or en dashes anywhere in the entry. Cover: samples stopped training a voiceprint once their
speaker is unassigned or reassigned; the six affected voiceprints rebuilt on startup; those samples
now visible in the person's list instead of invisible.

No `CAPABILITIES` or README change - this is a correctness fix, not a scope change. The Voiceprint
tab's row list gains rows, which is a behaviour change a user relies on, so check
`apps/web/src/content/help/**` for an article describing what trains a voiceprint and update it if
one exists.

- [ ] **Step 3: Update the architecture doc**

Add the rule to the speaker-identification section of `docs/Overall_Synopsis_of_Platform.md`: a
sample trains a voiceprint only while its speaker still names that person, enforced by
`VoiceprintTraining` and reconciled on boot by `VoiceprintRebuild`. No `Data_Schema.md` change -
there is no migration.

- [ ] **Step 4: Regenerate the two generated files**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
```

The snapshot test rewrites its own snapshot, so run 1 fails and run 2 passes with no code change.
Commit the regenerated file. Then:

```bash
cd integrations/n8n-nodes-diariz && npm run generate
```

`generated/index.ts` does **not** self-heal, and a stale one reds the "n8n community node" check.

- [ ] **Step 5: Full green build**

```bash
dotnet build Diariz.slnx && dotnet test
```

```bash
cd apps/web && npm run build && npm test
```

Both must be green with no warnings. **Make no edits after this run** - a comment-only edit made
post-green once shipped a blank page past a full green suite.

- [ ] **Step 6: Push and open the PR**

```bash
git push -u origin fix/voiceprint-training-rule
```

The PR body must contain `Fixes #621` on its own line, and state the deployment surface:
**server redeploy only** - nothing touches the desktop shell.
