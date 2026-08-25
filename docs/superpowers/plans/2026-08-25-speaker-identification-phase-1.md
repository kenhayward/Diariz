# Speaker Identification Quality, Phase 1 - Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user hear the audio behind any voiceprint, and see and control exactly which speakers train it.

**Architecture:** A new `ManageVoiceprints` platform permission gates cross-owner assessment. A new clip endpoint cuts a span out of a recording with ffmpeg (added to the API image) driven by a presigned internal MinIO URL, so no whole-file token ever reaches the browser from the voiceprint surfaces. The Voiceprint tab stops listing only hand-enrolled samples and instead lists **every speaker attributed to the person**, each togglable into or out of training.

**Tech Stack:** ASP.NET Core (.NET 10), EF Core + Npgsql/pgvector, React 19 + TypeScript + Vite + Tailwind v4, vitest, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-08-25-speaker-identification-quality-design.md` (sections 3.6, 4.2, 4.7, 7.2, 10 Phase 1).

## Global Constraints

- **TDD is mandatory.** Failing test first, watch it fail, minimal code to pass. No production code without a preceding red test.
- **Mutation-verify every new assertion.** Break the production code, confirm the test goes red, restore. Restoring a `.cs` from a backup preserves its mtime and MSBuild will skip the rebuild - `touch` the file or edit in place.
- **No em or en dashes in user-facing text.** Plain hyphen `-` only, in UI strings, i18n catalogs, release notes and help articles.
- **All four locale catalogs** (`en`/`de`/`fr`/`es`) must stay at exact key parity and have no empty values; `src/locales.test.ts` enforces both. Translations use normal accented characters - **the ASCII-only rule applies to `apps/web/src/content/help/**`, not to the catalogs**, and writing unaccented German or French here would be worse than the surrounding text.
- **Never `git add -A`** in this repository - it sweeps agent scratch files into the commit. Stage explicit paths.
- **`--filter "Name=X"` does not work here.** Use `--filter "FullyQualifiedName~X"`.
- **No `InternalsVisibleTo`.** Reach internals through public seams with TestSupport fakes; never widen visibility.
- **Build `Diariz.slnx`** (not just the unit test project) before pushing - unit-only runs miss integration and CodeQL compile breaks.
- **Split queries are the app-wide default.** Just write the `Include`s; do not add `.AsSplitQuery()`.
- `Person` maps to the `SpeakerProfiles` table and `VoiceSample` to `ProfileContributions`; `Speaker.PersonId` maps to the `ProfileId` column. Table names are deliberately unchanged for backup safety - **do not rename them**.

---

## File Structure

**Created**

| File | Responsibility |
|---|---|
| `src/Diariz.Api/Services/AudioClipper.cs` | `IAudioClipper` + `FfmpegAudioClipper`. Pure `Args()` builder plus the process run |
| `src/Diariz.Api/Services/PersonAttributions.cs` | Pure projection from speakers + samples to the attribution rows the tab renders |
| `apps/web/src/components/PersonAttributionRow.tsx` | One attributed speaker: training toggle, distance, playback |
| `apps/web/src/lib/clipPlayback.ts` | Pure helpers for driving one shared `<audio>` across a queue of clip URLs |
| `tests/Diariz.Api.Tests/AudioClipperArgsTests.cs` | ffmpeg argument construction |
| `tests/Diariz.Api.Tests/PersonAttributionsTests.cs` | The pure projection |
| `tests/Diariz.Api.Tests/PeopleAttributionEndpointTests.cs` | List + toggle endpoints |
| `tests/Diariz.Api.Tests/PeopleClipEndpointTests.cs` | Clip authorisation |
| `tests/Diariz.Api.Tests/SeederVoiceprintPermissionTests.cs` | The new permission is grantable |
| `tests/Diariz.Api.IntegrationTests/PersonAttributionIntegrationTests.cs` | Real-Postgres attribution query and toggle |
| `apps/web/src/components/PersonAttributionRow.test.tsx` | Row behaviour |
| `apps/web/src/lib/clipPlayback.test.ts` | Playback helpers |

**Modified**

| File | Change |
|---|---|
| `src/Diariz.Domain/Entities/PlatformPermission.cs` | `ManageVoiceprints = 32` |
| `src/Diariz.Domain/Entities/VoiceSample.cs` | `ExcludedAt` |
| `src/Diariz.Api/Services/Seeder.cs` | Grant the new permission to PlatformAdmins only |
| `src/Diariz.Api/Program.cs` | `ManageVoiceprints` policy + `IAudioClipper` registration |
| `src/Diariz.Api/Contracts/ApiDtos.cs` | `PermissionsDto`, attribution DTOs |
| `src/Diariz.Api/Controllers/UserProfileController.cs` | Map the new flag |
| `src/Diariz.Api/Controllers/PeopleController.cs` | Attribution list, training toggle, clip endpoint |
| `src/Diariz.Api/Services/AudioStorage.cs` | `GetPresignedReadUrlAsync` |
| `src/Diariz.Api/Dockerfile` | ffmpeg |
| `apps/web/src/lib/types.ts` | `Permissions.manageVoiceprints`, `PersonAttribution` |
| `apps/web/src/lib/api.ts` | Three client methods |
| `apps/web/src/auth.tsx` | `canManageVoiceprints` |
| `apps/web/src/components/users/permissions.ts` | Bit 32 |
| `apps/web/src/components/users/permissions.test.ts` | `EXPECTED` gains 32 |
| `apps/web/src/components/PersonVoiceprintTab.tsx` | Attributions replace the sample-only list |
| `apps/web/src/locales/{en,de,fr,es}/{admin,people}.json` | New keys |

---

## Task 1: The `ManageVoiceprints` permission

**Files:**
- Modify: `src/Diariz.Domain/Entities/PlatformPermission.cs`
- Modify: `src/Diariz.Api/Services/Seeder.cs:86-92`
- Modify: `src/Diariz.Api/Program.cs:205`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs:627`
- Modify: `src/Diariz.Api/Controllers/UserProfileController.cs:45`
- Test: `tests/Diariz.Api.Tests/SeederVoiceprintPermissionTests.cs`

**Interfaces:**
- Produces: `PlatformPermission.ManageVoiceprints` (value 32); authorization policy name `"ManageVoiceprints"`; `PermissionsDto.ManageVoiceprints`.

**Why PlatformAdmins only:** `Administrators` deliberately lacks `ManagePlatform` (backup/restore, which is already whole-instance audio access). Granting `Administrators` cross-owner audio would be a real escalation for that role. The two existing seeded groups map exactly onto the split the spec argues for: `Administrators` keeps directory hygiene without audio.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/SeederVoiceprintPermissionTests.cs`:

```csharp
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>ManageVoiceprints must be grantable, and must land on the platform administrators group only.
///
/// <para>Mirrors <see cref="SeederPeoplePermissionTests"/>, which exists because ManagePeople once shipped
/// defined, enforced and documented but held by nobody. The asymmetry here is deliberate: this permission
/// confers playback of audio from recordings the holder does not own, and the Administrators group has
/// never carried whole-instance data access.</para></summary>
public class SeederVoiceprintPermissionTests
{
    [Fact]
    public async Task SeedGroupsAsync_grants_ManageVoiceprints_to_platform_administrators()
    {
        using var db = TestDb.Create();

        await Seeder.SeedGroupsAsync(db);

        var group = await db.UserGroups.SingleAsync(g => g.Name == Seeder.PlatformAdminsGroup);
        Assert.True(group.Permissions.HasFlag(PlatformPermission.ManageVoiceprints));
    }

    [Fact]
    public async Task SeedGroupsAsync_does_not_grant_ManageVoiceprints_to_administrators()
    {
        // Administrators do directory hygiene without cross-owner audio. If this ever flips, it should be a
        // deliberate decision with its own reasoning, not a copy-paste from the ManagePeople line above it.
        using var db = TestDb.Create();

        await Seeder.SeedGroupsAsync(db);

        var group = await db.UserGroups.SingleAsync(g => g.Name == Seeder.AdminsGroup);
        Assert.False(group.Permissions.HasFlag(PlatformPermission.ManageVoiceprints));
        Assert.True(group.Permissions.HasFlag(PlatformPermission.ManagePeople));
    }

    [Fact]
    public async Task SeedGroupsAsync_adds_ManageVoiceprints_to_an_existing_group()
    {
        // An already-deployed platform must pick the flag up on its next boot, which is why this needs no
        // migration.
        using var db = TestDb.Create();
        db.UserGroups.Add(new UserGroup
        {
            Id = Guid.NewGuid(), Name = Seeder.PlatformAdminsGroup, IsSystem = true,
            Permissions = PlatformPermission.ManageRooms | PlatformPermission.ManageUsers
                | PlatformPermission.ManagePlatform | PlatformPermission.ManageFormulas
                | PlatformPermission.ManagePeople,
        });
        await db.SaveChangesAsync();

        await Seeder.SeedGroupsAsync(db);

        var group = await db.UserGroups.SingleAsync(g => g.Name == Seeder.PlatformAdminsGroup);
        Assert.True(group.Permissions.HasFlag(PlatformPermission.ManageVoiceprints));
        Assert.True(group.Permissions.HasFlag(PlatformPermission.ManagePlatform));
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SeederVoiceprintPermission"
```

Expected: compile error, `'PlatformPermission' does not contain a definition for 'ManageVoiceprints'`.

- [ ] **Step 3: Add the enum value**

Append to `src/Diariz.Domain/Entities/PlatformPermission.cs`, after `ManagePeople = 16`:

```csharp
    /// <summary>Assess and tune voice identification: the confirmation queue, the diagnostics bench, the
    /// re-scan, and <b>playback of segments attributed to the person under assessment, in recordings the
    /// holder does not own</b>.
    ///
    /// <para>That last clause is the whole reason this is separate from <see cref="ManagePeople"/>. Merging
    /// two duplicate contacts is routine directory hygiene that should stay widely delegable; listening to
    /// another user's meeting audio is not. Narrower than <see cref="ManagePlatform"/>, which already
    /// confers whole-instance audio via backup - this grants short clips of one person's speech, and logs
    /// every cross-owner access.</para></summary>
    ManageVoiceprints = 32,
```

- [ ] **Step 4: Seed it to platform administrators only**

In `src/Diariz.Api/Services/Seeder.cs`, extend the **first** `EnsureGroup` call only:

```csharp
        await EnsureGroup(db, PlatformAdminsGroup, isSystem: true,
            PlatformPermission.ManageRooms | PlatformPermission.ManageUsers | PlatformPermission.ManagePlatform
                | PlatformPermission.ManageFormulas | PlatformPermission.ManagePeople
                | PlatformPermission.ManageVoiceprints);
```

Leave the `AdminsGroup` call untouched.

- [ ] **Step 5: Run the test and watch it pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~SeederVoiceprintPermission"
```

Expected: 3 passed.

- [ ] **Step 6: Mutation-verify**

Temporarily add `| PlatformPermission.ManageVoiceprints` to the `AdminsGroup` call. Re-run: `does_not_grant_ManageVoiceprints_to_administrators` must fail. Revert **by editing in place** (not by restoring a backup - MSBuild would skip the rebuild).

- [ ] **Step 7: Wire the policy and the DTO**

`src/Diariz.Api/Program.cs`, beside the existing policies:

```csharp
    o.AddPolicy("ManageVoiceprints", p => p.AddRequirements(new PermissionRequirement(PlatformPermission.ManageVoiceprints)));
```

`src/Diariz.Api/Contracts/ApiDtos.cs`, extend `PermissionsDto` (append only - the order is positional):

```csharp
public record PermissionsDto(
    bool ManageRooms, bool ManageUsers, bool ManagePlatform, bool ManageFormulas, bool ManagePeople,
    bool ManageVoiceprints);
```

`src/Diariz.Api/Controllers/UserProfileController.cs:45`, add the mapping as the last argument:

```csharp
        p.HasFlag(PlatformPermission.ManageVoiceprints));
```

- [ ] **Step 8: Build and run the whole unit suite**

```bash
dotnet build Diariz.slnx
```

```bash
dotnet test tests/Diariz.Api.Tests
```

Expected: green, no warnings.

- [ ] **Step 9: Commit**

```bash
git add src/Diariz.Domain/Entities/PlatformPermission.cs src/Diariz.Api/Services/Seeder.cs src/Diariz.Api/Program.cs src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Controllers/UserProfileController.cs tests/Diariz.Api.Tests/SeederVoiceprintPermissionTests.cs
git commit -m "feat: add ManageVoiceprints platform permission"
```

---

## Task 2: Mirror the permission in the web console

**Files:**
- Modify: `apps/web/src/components/users/permissions.ts`
- Modify: `apps/web/src/components/users/permissions.test.ts`
- Modify: `apps/web/src/lib/types.ts:666-674`
- Modify: `apps/web/src/auth.tsx:26,59,144`
- Modify: `apps/web/src/locales/{en,de,fr,es}/admin.json`

**Interfaces:**
- Consumes: `PlatformPermission.ManageVoiceprints = 32` from Task 1.
- Produces: `Permissions.manageVoiceprints` on the web auth context; `useAuth().canManageVoiceprints`.

**Why this task is separate:** the groups console is the only way to grant a permission. A bit missing here is a permission nobody can hold - which is exactly how `ManagePeople` shipped with the People page unreachable.

- [ ] **Step 1: Write the failing test**

In `apps/web/src/components/users/permissions.test.ts`, change the expected set:

```ts
  const EXPECTED = [1, 2, 4, 8, 16, 32];
```

and update the `permissionCount` case that assumed five bits:

```ts
    expect(permissionCount(63)).toBe(6);
```

- [ ] **Step 2: Run it and watch it fail**

```bash
cd apps/web && npx vitest run src/components/users/permissions.test.ts
```

Expected: FAIL, `expected [1,2,4,8,16] to equal [1,2,4,8,16,32]`.

- [ ] **Step 3: Add the bit**

`apps/web/src/components/users/permissions.ts`, append to `PERMISSION_BITS`:

```ts
  { bit: 32, key: "permManageVoiceprints", hint: "permManageVoiceprintsHint", grant: "grantManageVoiceprints" },
```

- [ ] **Step 4: Run it and watch it pass**

```bash
cd apps/web && npx vitest run src/components/users/permissions.test.ts
```

Expected: PASS.

- [ ] **Step 5: Add the catalogue keys to all four locales**

`apps/web/src/locales/en/admin.json` - beside the existing `permManagePeople` entries:

```json
  "grantManageVoiceprints": "assess and tune voice identification",
  "permManageVoiceprints": "Manage voiceprints",
  "permManageVoiceprintsHint": "Assess and tune voice identification. Includes listening to short clips of a person's speech in recordings you do not own, which is logged. Does not grant access to those recordings otherwise.",
```

Add the same three keys to `de`, `fr` and `es` `admin.json`, properly translated with normal accented characters (the ASCII rule is for help articles, not catalogs). Plain hyphens, never em or en dashes.

- [ ] **Step 6: Extend the web permission type and auth context**

`apps/web/src/lib/types.ts`, in `interface Permissions`:

```ts
  /// Assess and tune voice identification, including clipped playback of a person's speech in recordings
  /// you do not own. Deliberately separate from `managePeople`, which is directory hygiene and grants no
  /// cross-owner audio.
  manageVoiceprints: boolean;
```

`apps/web/src/auth.tsx` - add to the context interface beside `canManagePeople`, derive it, and pass it through the provider value:

```ts
  canManageVoiceprints: boolean;
```

```ts
  const canManageVoiceprints = permissions.manageVoiceprints;
```

Add `canManageVoiceprints,` to the object passed to the provider. Also add `manageVoiceprints: false` to `NO_PERMISSIONS`.

- [ ] **Step 7: Run the web suite**

```bash
cd apps/web && npm test
```

Expected: green. If `locales.test.ts` fails, a key is missing from one of the four catalogs.

- [ ] **Step 8: Commit**

```bash
git add apps/web/src/components/users/permissions.ts apps/web/src/components/users/permissions.test.ts apps/web/src/lib/types.ts apps/web/src/auth.tsx apps/web/src/locales
git commit -m "feat: surface ManageVoiceprints in the groups console"
```

---

## Task 3: `VoiceSample.ExcludedAt`

**Files:**
- Modify: `src/Diariz.Domain/Entities/VoiceSample.cs`
- Create: migration via `dotnet ef`
- Test: `tests/Diariz.Api.IntegrationTests/PersonAttributionIntegrationTests.cs` (first test only)

**Interfaces:**
- Produces: `VoiceSample.ExcludedAt` (`DateTimeOffset?`). Null means the sample trains the voiceprint; non-null means it was dropped from training but the record of the enrolment is kept.

**Why not just delete the row:** the sample records that a human once asserted this speaker was this person. Deleting it loses that assertion and lets a later re-scan silently re-add it. Excluding keeps the history and gives Phase 2's rejected-pair guard something to read.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.IntegrationTests/PersonAttributionIntegrationTests.cs`:

```csharp
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Diariz.Api.IntegrationTests;

/// <summary>Attribution state against real Postgres. The in-memory provider Ignores the vector(192) column
/// and does not enforce FKs, so neither the storage nor the cascade can be proven there.</summary>
[Collection(IntegrationCollection.Name)]
public class PersonAttributionIntegrationTests(ContainersFixture fx)
{
    private static Vector Unit() { var v = new float[192]; v[0] = 1f; return new Vector(v); }

    [Fact]
    public async Task ExcludedAt_round_trips_as_a_timestamptz()
    {
        var sampleId = Guid.NewGuid();
        var when = DateTimeOffset.UtcNow;

        await using (var db = fx.CreateDbContext())
        {
            var person = new Person { Id = Guid.NewGuid(), Name = "Alice" };
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test" };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
            var speaker = new Speaker
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
                DisplayName = "Alice", PersonId = person.Id, Embedding = Unit(),
            };
            db.AddRange(user, person, rec, speaker, new VoiceSample
            {
                Id = sampleId, PersonId = person.Id, SpeakerId = speaker.Id, RecordingId = rec.Id,
                Embedding = Unit(), ExcludedAt = when,
            });
            await db.SaveChangesAsync();
        }

        await using var read = fx.CreateDbContext();
        var stored = await read.VoiceSamples.SingleAsync(v => v.Id == sampleId);
        Assert.NotNull(stored.ExcludedAt);
        Assert.Equal(when.ToUniversalTime(), stored.ExcludedAt!.Value.ToUniversalTime(), TimeSpan.FromSeconds(1));
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~PersonAttributionIntegration"
```

Expected: compile error, `'VoiceSample' does not contain a definition for 'ExcludedAt'`.

- [ ] **Step 3: Add the property**

In `src/Diariz.Domain/Entities/VoiceSample.cs`, after `UsedMs`:

```csharp
    /// <summary>When a user dropped this sample from training, or null while it still trains the voiceprint.
    ///
    /// <para>Excluded rather than deleted on purpose. The row records that a human once asserted this
    /// speaker was this person; deleting it loses that assertion, and a later re-scan would be free to
    /// silently re-add what someone deliberately removed.</para>
    ///
    /// <para><b>Store UTC.</b> Npgsql rejects a non-zero-offset DateTimeOffset for a timestamptz column and
    /// throws at SaveChanges - the in-memory provider will not catch it.</para></summary>
    public DateTimeOffset? ExcludedAt { get; set; }
```

- [ ] **Step 4: Create the migration**

```bash
dotnet ef migrations add AddVoiceSampleExcludedAt --project src/Diariz.Domain --startup-project src/Diariz.Api
```

Read the generated file and confirm it is a single additive nullable column and nothing else. If it contains any other change, the model has drifted - stop and investigate rather than committing it.

- [ ] **Step 5: Run the test and watch it pass**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~PersonAttributionIntegration"
```

Expected: PASS. (Needs Docker.)

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Domain/Entities/VoiceSample.cs src/Diariz.Domain/Migrations tests/Diariz.Api.IntegrationTests/PersonAttributionIntegrationTests.cs
git commit -m "feat: add ExcludedAt to voice samples"
```

---

## Task 4: The attribution projection

**Files:**
- Create: `src/Diariz.Api/Services/PersonAttributions.cs`
- Create: `tests/Diariz.Api.Tests/PersonAttributionsTests.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`

**Interfaces:**
- Produces:
  ```csharp
  public record PersonAttributionDto(
      Guid SpeakerId, Guid RecordingId, string RecordingName, string SpeakerLabel,
      string LinkedBy, bool IsTraining, Guid? VoiceSampleId, long SpeechMs, bool CanAccessRecording);

  public static class PersonAttributions
  {
      public static IReadOnlyList<PersonAttributionDto> Build(
          IReadOnlyList<AttributionInput> speakers, IReadOnlyList<VoiceSample> samples,
          IReadOnlyDictionary<Guid, string> recordingNames, IReadOnlySet<Guid> accessibleRecordings);
  }

  public record AttributionInput(
      Guid SpeakerId, Guid RecordingId, string Label, bool IdentifiedAuto, bool IsMultiSpeaker, long SpeechMs);
  ```
- `LinkedBy` is one of `"manual"`, `"auto"`. (Phase 2 adds `"confirmed"`; the string is deliberately open so adding a value needs no contract change.)

**Why a pure function:** the controller has to stitch recordings, speakers, samples and segment durations in memory anyway (there is no FK from `VoiceSample` to `Recording`). Separating the stitching from the querying is what makes the rules testable without a database.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/PersonAttributionsTests.cs`:

```csharp
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>Turning a person's attributed speakers into the rows the Voiceprint tab renders.
///
/// <para>The point of this projection is that the tab used to list only <c>ProfileContributions</c> - what
/// was enrolled by hand - which made the set look arbitrary, because auto-identification never creates one.
/// Every attributed speaker appears here; training is a flag on the row, not the reason it exists.</para></summary>
public class PersonAttributionsTests
{
    private static readonly Guid Rec = Guid.NewGuid();
    private static readonly Dictionary<Guid, string> Names = new() { [Rec] = "Standup" };

    private static AttributionInput Speaker(Guid id, bool auto = false, long speechMs = 30000) =>
        new(id, Rec, "SPEAKER_00", auto, IsMultiSpeaker: false, speechMs);

    [Fact]
    public void Build_includes_a_speaker_with_no_voice_sample()
    {
        // The case that made the old list look random: auto-identification links a speaker without ever
        // creating a contribution row.
        var id = Guid.NewGuid();

        var rows = PersonAttributions.Build([Speaker(id, auto: true)], [], Names, new HashSet<Guid> { Rec });

        var row = Assert.Single(rows);
        Assert.Equal(id, row.SpeakerId);
        Assert.False(row.IsTraining);
        Assert.Null(row.VoiceSampleId);
        Assert.Equal("auto", row.LinkedBy);
    }

    [Fact]
    public void Build_marks_a_speaker_with_a_sample_as_training()
    {
        var id = Guid.NewGuid();
        var sampleId = Guid.NewGuid();
        var samples = new List<VoiceSample> { new() { Id = sampleId, SpeakerId = id, RecordingId = Rec } };

        var row = Assert.Single(PersonAttributions.Build([Speaker(id)], samples, Names, new HashSet<Guid> { Rec }));

        Assert.True(row.IsTraining);
        Assert.Equal(sampleId, row.VoiceSampleId);
        Assert.Equal("manual", row.LinkedBy);
    }

    [Fact]
    public void Build_treats_an_excluded_sample_as_not_training_but_keeps_its_id()
    {
        // Excluded is not deleted: the row still points at the sample so re-including it is a toggle rather
        // than a fresh enrolment, and the original assertion is not lost.
        var id = Guid.NewGuid();
        var sampleId = Guid.NewGuid();
        var samples = new List<VoiceSample>
        {
            new() { Id = sampleId, SpeakerId = id, RecordingId = Rec, ExcludedAt = DateTimeOffset.UtcNow },
        };

        var row = Assert.Single(PersonAttributions.Build([Speaker(id)], samples, Names, new HashSet<Guid> { Rec }));

        Assert.False(row.IsTraining);
        Assert.Equal(sampleId, row.VoiceSampleId);
    }

    [Fact]
    public void Build_flags_a_recording_the_caller_cannot_read()
    {
        // The directory is platform-wide but recordings are ownership-filtered, and this is live, not
        // theoretical. The row must still appear - it is genuinely part of what trained the voiceprint -
        // but the UI has to know not to offer a transcript or a play button.
        var id = Guid.NewGuid();

        var row = Assert.Single(PersonAttributions.Build([Speaker(id)], [], Names, new HashSet<Guid>()));

        Assert.False(row.CanAccessRecording);
    }

    [Fact]
    public void Build_names_a_recording_that_no_longer_exists()
    {
        var id = Guid.NewGuid();

        var row = Assert.Single(PersonAttributions.Build([Speaker(id)], [], new Dictionary<Guid, string>(), new HashSet<Guid>()));

        Assert.Equal("(deleted recording)", row.RecordingName);
    }

    [Fact]
    public void Build_excludes_multi_speaker_slots()
    {
        // Overlapping audio is a mix of people. It can never train a single-person voiceprint, so offering
        // it as a candidate would be offering something the server will refuse.
        var id = Guid.NewGuid();
        var multi = new AttributionInput(id, Rec, "SPEAKER_01", false, IsMultiSpeaker: true, 30000);

        Assert.Empty(PersonAttributions.Build([multi], [], Names, new HashSet<Guid> { Rec }));
    }

    [Fact]
    public void Build_orders_by_recording_name_then_label()
    {
        var recB = Guid.NewGuid();
        var names = new Dictionary<Guid, string> { [Rec] = "Zulu", [recB] = "Alpha" };
        var a = new AttributionInput(Guid.NewGuid(), recB, "SPEAKER_00", false, false, 1000);
        var z = new AttributionInput(Guid.NewGuid(), Rec, "SPEAKER_00", false, false, 1000);

        var rows = PersonAttributions.Build([z, a], [], names, new HashSet<Guid> { Rec, recB });

        Assert.Equal(["Alpha", "Zulu"], rows.Select(r => r.RecordingName));
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PersonAttributions"
```

Expected: compile error, `PersonAttributions` does not exist.

- [ ] **Step 3: Add the DTO**

In `src/Diariz.Api/Contracts/ApiDtos.cs`, near the other person DTOs:

```csharp
/// <summary>One speaker attributed to a person, whether or not it trains their voiceprint.
///
/// <para><paramref name="IsTraining"/> and <paramref name="VoiceSampleId"/> are independent: an excluded
/// sample is not training but still has an id, because re-including it is a toggle rather than a fresh
/// enrolment.</para>
///
/// <para><paramref name="CanAccessRecording"/> is false when the caller neither owns the recording nor
/// holds ManageVoiceprints. The row is still returned, because it is part of what trained the
/// voiceprint - the client renders it without a transcript or a play button.</para></summary>
public record PersonAttributionDto(
    Guid SpeakerId,
    Guid RecordingId,
    string RecordingName,
    string SpeakerLabel,
    string LinkedBy,
    bool IsTraining,
    Guid? VoiceSampleId,
    long SpeechMs,
    bool CanAccessRecording);
```

- [ ] **Step 4: Write the implementation**

Create `src/Diariz.Api/Services/PersonAttributions.cs`:

```csharp
using Diariz.Api.Contracts;
using Diariz.Domain.Entities;

namespace Diariz.Api.Services;

/// <summary>What a person's Voiceprint tab lists: every speaker attributed to them, with whether it
/// currently trains the voiceprint.
///
/// <para>Pure, because the controller has to stitch speakers, samples, recording names and segment
/// durations in memory anyway - <see cref="VoiceSample"/> deliberately has no FK to its recording - and the
/// rules are worth testing without a database.</para></summary>
public record AttributionInput(
    Guid SpeakerId, Guid RecordingId, string Label, bool IdentifiedAuto, bool IsMultiSpeaker, long SpeechMs);

public static class PersonAttributions
{
    private const string DeletedRecording = "(deleted recording)";

    public static IReadOnlyList<PersonAttributionDto> Build(
        IReadOnlyList<AttributionInput> speakers,
        IReadOnlyList<VoiceSample> samples,
        IReadOnlyDictionary<Guid, string> recordingNames,
        IReadOnlySet<Guid> accessibleRecordings)
    {
        var bySpeaker = samples
            .GroupBy(s => s.SpeakerId)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.CreatedAt).First());

        return speakers
            // Overlapping audio is a mix of people and can never train a single-person voiceprint, so it is
            // not a candidate at all rather than a candidate the server would refuse.
            .Where(s => !s.IsMultiSpeaker)
            .Select(s =>
            {
                var sample = bySpeaker.TryGetValue(s.SpeakerId, out var v) ? v : null;
                return new PersonAttributionDto(
                    s.SpeakerId,
                    s.RecordingId,
                    recordingNames.TryGetValue(s.RecordingId, out var name) ? name : DeletedRecording,
                    s.Label,
                    s.IdentifiedAuto ? "auto" : "manual",
                    sample is { ExcludedAt: null },
                    sample?.Id,
                    s.SpeechMs,
                    accessibleRecordings.Contains(s.RecordingId));
            })
            .OrderBy(r => r.RecordingName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.SpeakerLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
```

- [ ] **Step 5: Run the tests and watch them pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PersonAttributions"
```

Expected: 7 passed.

- [ ] **Step 6: Mutation-verify two of them**

Change `sample is { ExcludedAt: null }` to `sample is not null`. Re-run: `Build_treats_an_excluded_sample_as_not_training_but_keeps_its_id` must fail. Restore by editing in place.

Remove the `.Where(s => !s.IsMultiSpeaker)` line. Re-run: `Build_excludes_multi_speaker_slots` must fail. Restore.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Services/PersonAttributions.cs src/Diariz.Api/Contracts/ApiDtos.cs tests/Diariz.Api.Tests/PersonAttributionsTests.cs
git commit -m "feat: project a person's attributed speakers"
```

---

## Task 5: The attribution list endpoint

**Files:**
- Modify: `src/Diariz.Api/Controllers/PeopleController.cs`
- Create: `tests/Diariz.Api.Tests/PeopleAttributionEndpointTests.cs`

**Interfaces:**
- Consumes: `PersonAttributions.Build` and `PersonAttributionDto` from Task 4; `CanManagePeopleAsync()` from the controller.
- Produces: `GET /api/people/{id}/attributions` -> `IReadOnlyList<PersonAttributionDto>`.

**Note:** `api/people` **is** in the published OpenAPI document (only `api/admin`, `api/platform`, `api/oauth` and `api/maintenance` are excluded), so Task 11 must regenerate the snapshot **and** the n8n node.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/PeopleAttributionEndpointTests.cs`:

```csharp
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests;

public class PeopleAttributionEndpointTests
{
    private static (PeopleController controller, Guid personId, Guid speakerId, Guid recordingId) Seed(
        DiarizDbContext db, Guid userId, bool ownedByCaller = true)
    {
        var person = new Person { Id = Guid.NewGuid(), Name = "Alice" };
        var owner = ownedByCaller ? userId : Guid.NewGuid();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = owner, Title = "Standup", BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Version = 1 };
        var speaker = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = "Alice", PersonId = person.Id, IdentifiedAuto = true,
        };
        db.AddRange(person, rec, tr, speaker, new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 12000, Original = "hello", Ordinal = 0,
        });
        db.SaveChanges();
        Perms.Grant(db, userId, PlatformPermission.ManagePeople);
        db.SaveChanges();

        var controller = People.Build(db, userId);
        return (controller, person.Id, speaker.Id, rec.Id);
    }

    [Fact]
    public async Task Attributions_lists_an_auto_identified_speaker_that_has_no_sample()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId);

        var result = await controller.Attributions(personId);

        var rows = Assert.IsAssignableFrom<IReadOnlyList<PersonAttributionDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        var row = Assert.Single(rows);
        Assert.Equal(speakerId, row.SpeakerId);
        Assert.False(row.IsTraining);
        Assert.Equal(12000, row.SpeechMs);
        Assert.True(row.CanAccessRecording);
    }

    [Fact]
    public async Task Attributions_marks_another_users_recording_inaccessible_without_ManageVoiceprints()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, _, _) = Seed(db, userId, ownedByCaller: false);

        var result = await controller.Attributions(personId);

        var rows = Assert.IsAssignableFrom<IReadOnlyList<PersonAttributionDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(Assert.Single(rows).CanAccessRecording);
    }

    [Fact]
    public async Task Attributions_marks_another_users_recording_accessible_with_ManageVoiceprints()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, _, _) = Seed(db, userId, ownedByCaller: false);
        Perms.Grant(db, userId, PlatformPermission.ManageVoiceprints);
        await db.SaveChangesAsync();

        var result = await controller.Attributions(personId);

        var rows = Assert.IsAssignableFrom<IReadOnlyList<PersonAttributionDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(Assert.Single(rows).CanAccessRecording);
    }

    [Fact]
    public async Task Attributions_without_ManagePeople_is_forbidden()
    {
        using var db = TestDb.Create();
        var (controller, personId, _, _) = Seed(db, Guid.NewGuid());
        var stranger = People.Build(db, Guid.NewGuid());

        Assert.IsType<ForbidResult>((await stranger.Attributions(personId)).Result);
    }

    [Fact]
    public async Task Attributions_for_an_unknown_person_is_not_found()
    {
        using var db = TestDb.Create();
        var (controller, _, _, _) = Seed(db, Guid.NewGuid());

        Assert.IsType<NotFoundResult>((await controller.Attributions(Guid.NewGuid())).Result);
    }
}
```

If a `People.Build(db, userId)` helper does not already exist in `Diariz.Api.Tests`, add one to the test project (not TestSupport, unless the integration project needs it too) that constructs `PeopleController` with `FakeJobQueue` and the real `PeopleDirectory`, `RoomScope` and `UserPermissions`, and a `ControllerContext` from `Http.Context(userId)`. Copy the construction from an existing People test class so the argument list stays in one place.

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PeopleAttributionEndpoint"
```

Expected: compile error, no `Attributions` method.

- [ ] **Step 3: Add the endpoint**

In `src/Diariz.Api/Controllers/PeopleController.cs`, after `Get`:

```csharp
    [HttpGet("{id:guid}/attributions")]
    [EndpointSummary("List the speakers attributed to a person")]
    [EndpointDescription(
        "Every recording-speaker currently identified as this person, whether or not it trains their " +
        "voiceprint. Auto-identification links a speaker without creating a voice sample, so this is a " +
        "strictly larger set than the samples returned by `GET /api/people/{id}`.\n\n" +
        "`canAccessRecording` is false when you neither own the recording nor hold **Manage voiceprints**. " +
        "The row is still listed - it is part of what the voiceprint learned from - but the transcript and " +
        "audio are not available to you.")]
    public async Task<ActionResult<IReadOnlyList<PersonAttributionDto>>> Attributions(Guid id)
    {
        if (!await CanManagePeopleAsync()) return Forbid();
        if (!await _db.People.AnyAsync(p => p.Id == id)) return NotFound();

        var speakers = await _db.Speakers
            .Where(s => s.PersonId == id)
            .Select(s => new { s.Id, s.RecordingId, s.Label, s.IdentifiedAuto, s.IsMultiSpeaker })
            .ToListAsync();

        var recIds = speakers.Select(s => s.RecordingId).Distinct().ToList();

        var recordings = await _db.Recordings
            .Where(r => recIds.Contains(r.Id))
            .Select(r => new { r.Id, r.UserId, Display = r.Name ?? r.Title })
            .ToListAsync();

        // Cross-owner audio is exactly what ManageVoiceprints grants; without it the row is listed but inert.
        var canAssess = await _permissions.HasAsync(UserId, PlatformPermission.ManageVoiceprints);
        var accessible = recordings
            .Where(r => canAssess || r.UserId == UserId)
            .Select(r => r.Id)
            .ToHashSet();

        // Speech per speaker comes from the current transcription's segments, which the API already stores -
        // no worker involvement, and the same figure the min-speech gate will read in Phase 2.
        var currentTr = (await _db.Transcriptions
                .Where(t => recIds.Contains(t.RecordingId))
                .Select(t => new { t.Id, t.RecordingId, t.Version }).ToListAsync())
            .GroupBy(t => t.RecordingId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.Version).First().Id);

        var trIds = currentTr.Values.ToList();
        var speech = (await _db.Segments
                .Where(s => trIds.Contains(s.TranscriptionId))
                .Select(s => new { s.TranscriptionId, s.SpeakerLabel, s.StartMs, s.EndMs }).ToListAsync())
            .GroupBy(s => (s.TranscriptionId, s.SpeakerLabel))
            .ToDictionary(g => g.Key, g => g.Sum(s => Math.Max(0, s.EndMs - s.StartMs)));

        long SpeechFor(Guid recordingId, string label) =>
            currentTr.TryGetValue(recordingId, out var trId) && speech.TryGetValue((trId, label), out var ms)
                ? ms : 0;

        var samples = await _db.VoiceSamples.Where(v => v.PersonId == id).ToListAsync();

        return Ok(PersonAttributions.Build(
            speakers
                .Select(s => new AttributionInput(
                    s.Id, s.RecordingId, s.Label, s.IdentifiedAuto, s.IsMultiSpeaker,
                    SpeechFor(s.RecordingId, s.Label)))
                .ToList(),
            samples,
            recordings.ToDictionary(r => r.Id, r => r.Display),
            accessible));
    }
```

- [ ] **Step 4: Run the tests and watch them pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PeopleAttributionEndpoint"
```

Expected: 5 passed.

- [ ] **Step 5: Mutation-verify the permission branch**

Change `canAssess || r.UserId == UserId` to `true`. Re-run: `Attributions_marks_another_users_recording_inaccessible_without_ManageVoiceprints` must fail. Restore in place.

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Controllers/PeopleController.cs tests/Diariz.Api.Tests/PeopleAttributionEndpointTests.cs
git commit -m "feat: list every speaker attributed to a person"
```

---

## Task 6: The training toggle endpoint

**Files:**
- Modify: `src/Diariz.Api/Controllers/PeopleController.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Modify: `tests/Diariz.Api.Tests/PeopleAttributionEndpointTests.cs`

**Interfaces:**
- Produces: `PUT /api/people/{id}/attributions/{speakerId}/training` with body `SetTrainingRequest(bool Training)` -> `204`.

**Behaviour:** turning training **on** for a speaker with no sample creates one from `Speaker.Embedding` (which already exists from transcription, so no worker is needed); for an excluded sample it clears `ExcludedAt`. Turning it **off** sets `ExcludedAt`. Either way the person's centroid is recomputed.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Diariz.Api.Tests/PeopleAttributionEndpointTests.cs`:

```csharp
    [Fact]
    public async Task SetTraining_on_creates_a_sample_from_the_speakers_existing_embedding()
    {
        // No worker round trip: the speaker's embedding was computed at transcription time, so enrolling a
        // whole speaker is a database write. Only a span subset needs the re-embed job.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId);

        Assert.IsType<NoContentResult>(
            await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true)));

        var sample = Assert.Single(db.VoiceSamples.Where(v => v.PersonId == personId));
        Assert.Equal(speakerId, sample.SpeakerId);
        Assert.Null(sample.ExcludedAt);
    }

    [Fact]
    public async Task SetTraining_off_excludes_rather_than_deletes()
    {
        // The row records that a human asserted this speaker was this person. Deleting it loses that, and
        // frees a later re-scan to silently re-add what someone deliberately removed.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId);
        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true));

        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(false));

        var sample = Assert.Single(db.VoiceSamples.Where(v => v.PersonId == personId));
        Assert.NotNull(sample.ExcludedAt);
    }

    [Fact]
    public async Task SetTraining_on_again_reuses_the_excluded_sample()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId);
        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true));
        var sampleId = db.VoiceSamples.Single().Id;
        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(false));

        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true));

        var sample = Assert.Single(db.VoiceSamples.Where(v => v.PersonId == personId));
        Assert.Equal(sampleId, sample.Id);
        Assert.Null(sample.ExcludedAt);
    }

    [Fact]
    public async Task SetTraining_stores_ExcludedAt_in_utc()
    {
        // Npgsql throws at SaveChanges on a non-zero-offset DateTimeOffset for a timestamptz column, and the
        // in-memory provider will not catch it. Assert the offset here so the failure is a unit test rather
        // than a 500 on real Postgres.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId);
        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true));

        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(false));

        Assert.Equal(TimeSpan.Zero, db.VoiceSamples.Single().ExcludedAt!.Value.Offset);
    }

    [Fact]
    public async Task SetTraining_on_a_speaker_not_attributed_to_this_person_is_not_found()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, _, _) = Seed(db, userId);
        var other = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = Guid.NewGuid(), Label = "SPEAKER_09", DisplayName = "Bob",
        };
        db.Add(other);
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>(
            await controller.SetTraining(personId, other.Id, new SetTrainingRequest(true)));
    }

    [Fact]
    public async Task SetTraining_on_an_opted_out_person_is_a_conflict()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId);
        db.People.Single(p => p.Id == personId).VoiceprintOptOut = true;
        await db.SaveChangesAsync();

        Assert.IsType<ConflictObjectResult>(
            await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true)));
    }
```

- [ ] **Step 2: Run and watch them fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PeopleAttributionEndpoint"
```

Expected: compile error, no `SetTraining` and no `SetTrainingRequest`.

- [ ] **Step 3: Add the request record**

In `src/Diariz.Api/Contracts/ApiDtos.cs`:

```csharp
/// <summary>Whether a speaker attributed to a person should train their voiceprint.</summary>
public record SetTrainingRequest(bool Training);
```

- [ ] **Step 4: Add the endpoint**

In `src/Diariz.Api/Controllers/PeopleController.cs`, after `Attributions`:

```csharp
    [HttpPut("{id:guid}/attributions/{speakerId:guid}/training")]
    [EndpointSummary("Include or exclude a speaker from a person's voiceprint")]
    [EndpointDescription(
        "Adds a speaker attributed to this person into their voiceprint training set, or removes it. " +
        "Adding needs no re-transcription: the speaker's embedding was computed when the recording was " +
        "transcribed.\n\n" +
        "Removing **excludes rather than deletes** the sample, so the record that someone identified this " +
        "speaker as this person survives, and re-including it is a toggle. **409 when the person has opted " +
        "out** of voice-printing.")]
    public async Task<IActionResult> SetTraining(Guid id, Guid speakerId, SetTrainingRequest req)
    {
        var person = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (person is null) return NotFound();
        if (!await CanManageBiometricsAsync(person)) return Forbid();

        var speaker = await _db.Speakers.FirstOrDefaultAsync(s => s.Id == speakerId && s.PersonId == id);
        if (speaker is null) return NotFound();

        var sample = await _db.VoiceSamples
            .FirstOrDefaultAsync(v => v.PersonId == id && v.SpeakerId == speakerId);

        if (req.Training)
        {
            if (person.VoiceprintOptOut) return Conflict("This person has opted out of voice-printing.");
            if (speaker.IsMultiSpeaker)
                return Conflict("Overlapping speech cannot train a single person's voiceprint.");
            if (sample is null)
            {
                if (speaker.Embedding is null)
                    return BadRequest("This speaker has no voice embedding yet (re-transcribe to compute one).");
                _db.VoiceSamples.Add(new VoiceSample
                {
                    Id = Guid.NewGuid(), PersonId = id, SpeakerId = speakerId,
                    RecordingId = speaker.RecordingId, Embedding = speaker.Embedding,
                });
            }
            else
            {
                sample.ExcludedAt = null;
            }
        }
        else
        {
            if (sample is null) return NoContent(); // already not training; nothing to record
            // UTC, or Npgsql rejects it for the timestamptz column at SaveChanges.
            sample.ExcludedAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync();
        await _people.RecomputeVoiceprintAsync(id);
        return NoContent();
    }
```

- [ ] **Step 5: Make the centroid ignore excluded samples**

`RecomputeVoiceprintAsync` currently averages every sample. In `src/Diariz.Api/Services/PeopleDirectory.cs`, add `.Where(v => v.ExcludedAt == null)` to the sample query it builds the centroid from. Read the method first - if it also sets `SampleCount`, that count must exclude them too, or the UI reports training on samples it is not using.

- [ ] **Step 6: Write the test that pins step 5**

```csharp
    [Fact]
    public async Task Excluding_a_sample_removes_it_from_the_centroid()
    {
        // Without this the toggle is cosmetic: the row reads "not training" while the vector it contributed
        // is still in the average.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (controller, personId, speakerId, _) = Seed(db, userId);
        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(true));

        await controller.SetTraining(personId, speakerId, new SetTrainingRequest(false));

        Assert.Equal(0, db.People.Single(p => p.Id == personId).SampleCount);
    }
```

- [ ] **Step 7: Run the whole class and watch it pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PeopleAttributionEndpoint"
```

Expected: 12 passed.

- [ ] **Step 8: Mutation-verify**

Change `sample.ExcludedAt = DateTimeOffset.UtcNow;` to `_db.VoiceSamples.Remove(sample);`. Re-run: `SetTraining_off_excludes_rather_than_deletes` and `SetTraining_on_again_reuses_the_excluded_sample` must both fail. Restore in place.

- [ ] **Step 9: Commit**

```bash
git add src/Diariz.Api/Controllers/PeopleController.cs src/Diariz.Api/Contracts/ApiDtos.cs src/Diariz.Api/Services/PeopleDirectory.cs tests/Diariz.Api.Tests/PeopleAttributionEndpointTests.cs
git commit -m "feat: toggle a speaker in or out of voiceprint training"
```

---

## Task 7: Presigned read URLs

**Files:**
- Modify: `src/Diariz.Api/Services/AudioStorage.cs`
- Test: `tests/Diariz.Api.IntegrationTests/` (add to an existing storage test class if one exists, else create `AudioStoragePresignTests.cs`)

**Interfaces:**
- Produces: `Task<string> GetPresignedReadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct = default)` on `IAudioStorage`.

**Why:** ffmpeg needs to seek into a 29 MB (worst case 206 MB) object to cut a few seconds out of it. Given an HTTP URL, ffmpeg issues range requests and transfers only what it reads. Downloading the whole blob per clip would make auditioning a dozen segments painful.

**Security note:** the URL points at the API's *internal* MinIO endpoint (`minio:9000` in compose), which is not resolvable outside the container network, and it never leaves the API process. It is an input to a local subprocess, not something handed to a client.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task GetPresignedReadUrlAsync_returns_a_url_that_serves_the_object()
{
    var key = $"test/{Guid.NewGuid():N}.bin";
    var bytes = new byte[] { 1, 2, 3, 4 };
    await _storage.UploadAsync(key, new MemoryStream(bytes), "application/octet-stream");

    var url = await _storage.GetPresignedReadUrlAsync(key, TimeSpan.FromMinutes(5));

    using var http = new HttpClient();
    Assert.Equal(bytes, await http.GetByteArrayAsync(url));
}
```

Build `_storage` the way the existing integration storage tests do, from `ContainersFixture`'s MinIO connection details.

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~Presign"
```

Expected: compile error, no `GetPresignedReadUrlAsync`.

- [ ] **Step 3: Add it to the interface and implementation**

`IAudioStorage`:

```csharp
    /// <summary>A time-limited URL the API's own subprocesses can read the blob from, so ffmpeg can range-seek
    /// into a large recording instead of the API downloading all of it.
    ///
    /// <para><b>Internal only.</b> It points at the object store's in-network endpoint and must never be
    /// returned to a client - clients get audio through the API's own streaming endpoints.</para></summary>
    Task<string> GetPresignedReadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct = default);
```

`AudioStorage`:

```csharp
    public Task<string> GetPresignedReadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct = default) =>
        _s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _opts.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),
        });
```

Match the existing field names for the options/bucket in that class. Do **not** change any other request option - the MinIO payload-signing quirk documented on `PutObject` means S3 request options in this file are fragile.

- [ ] **Step 4: Add it to `FakeAudioStorage`**

In `tests/Diariz.Api.TestSupport`, implement the new member on the fake so every existing test still compiles:

```csharp
    public Task<string> GetPresignedReadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct = default) =>
        Task.FromResult($"https://fake.invalid/{key}");
```

- [ ] **Step 5: Run and watch it pass**

```bash
dotnet test tests/Diariz.Api.IntegrationTests --filter "FullyQualifiedName~Presign"
```

- [ ] **Step 6: Commit**

```bash
git add src/Diariz.Api/Services/AudioStorage.cs tests/Diariz.Api.TestSupport tests/Diariz.Api.IntegrationTests
git commit -m "feat: presigned internal read URLs for audio blobs"
```

---

## Task 8: The ffmpeg clipper

**Files:**
- Create: `src/Diariz.Api/Services/AudioClipper.cs`
- Create: `tests/Diariz.Api.Tests/AudioClipperArgsTests.cs`
- Modify: `src/Diariz.Api/Dockerfile`
- Modify: `src/Diariz.Api/Program.cs` (DI registration)

**Interfaces:**
- Produces:
  ```csharp
  public interface IAudioClipper
  {
      Task<byte[]> ClipAsync(string sourceUrl, long fromMs, long toMs, CancellationToken ct = default);
  }
  public static IReadOnlyList<string> FfmpegAudioClipper.Args(string sourceUrl, long fromMs, long toMs);
  ```

**Design notes that matter:**
- **`-ss` before `-i`, and `-t` (duration) after.** `-ss` before the input is a fast input seek; expressing the end as a duration rather than `-to` avoids the ambiguity of whether `-to` is relative to the seek point.
- **Write to a temp file, not a pipe.** ffmpeg cannot backfill the RIFF length field when writing WAV to a pipe, so a piped WAV carries a bogus size that browsers handle unpredictably. Clips are seconds long, so a temp file then read into memory is small, correct, and gives a real `Content-Length`.
- **`ArgumentList`, never a joined string.** Every value is either server-derived or numeric, and it stays that way.
- **Cap the clip.** `MaxClipMs = 120_000`; a caller asking for more gets truncated, not a 200 MB response.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/AudioClipperArgsTests.cs`:

```csharp
using System.Globalization;
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The ffmpeg command line. Extracted as a pure function precisely so the argument order - which is
/// where ffmpeg semantics live - can be asserted without ffmpeg being installed.</summary>
public class AudioClipperArgsTests
{
    private const string Url = "http://minio:9000/diariz/audio/abc.webm?X-Amz-Signature=deadbeef";

    [Fact]
    public void Args_seek_before_input_so_the_seek_is_fast()
    {
        // -ss after -i decodes from the start of the file and throws the result away, which on a 200 MB
        // recording is the difference between a clip and a timeout.
        var args = FfmpegAudioClipper.Args(Url, 65_000, 70_000);

        var ss = args.ToList().IndexOf("-ss");
        var i = args.ToList().IndexOf("-i");
        Assert.True(ss >= 0 && i >= 0);
        Assert.True(ss < i, "-ss must precede -i");
    }

    [Fact]
    public void Args_express_the_end_as_a_duration()
    {
        var args = FfmpegAudioClipper.Args(Url, 65_000, 70_000).ToList();

        Assert.Equal("5", args[args.IndexOf("-t") + 1]);
        Assert.Equal("65", args[args.IndexOf("-ss") + 1]);
    }

    [Fact]
    public void Args_format_seconds_invariantly()
    {
        // A comma decimal separator under a de-DE culture would make ffmpeg reject the offset.
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var args = FfmpegAudioClipper.Args(Url, 1_500, 3_250).ToList();
            Assert.Equal("1.5", args[args.IndexOf("-ss") + 1]);
            Assert.Equal("1.75", args[args.IndexOf("-t") + 1]);
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }

    [Fact]
    public void Args_pass_the_url_as_its_own_argument()
    {
        // A presigned URL contains & and =. Joined into a shell string it would be split; as its own
        // ArgumentList entry it cannot be.
        var args = FfmpegAudioClipper.Args(Url, 0, 1000).ToList();

        Assert.Equal(Url, args[args.IndexOf("-i") + 1]);
    }

    [Fact]
    public void Args_cap_an_over_long_request()
    {
        var args = FfmpegAudioClipper.Args(Url, 0, 10_000_000).ToList();

        Assert.Equal("120", args[args.IndexOf("-t") + 1]);
    }

    [Fact]
    public void Args_produce_mono_16k_wav()
    {
        var args = FfmpegAudioClipper.Args(Url, 0, 1000).ToList();

        Assert.Equal("1", args[args.IndexOf("-ac") + 1]);
        Assert.Equal("16000", args[args.IndexOf("-ar") + 1]);
        Assert.Contains("-vn", args);
    }
}
```

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~AudioClipperArgs"
```

Expected: compile error, no `FfmpegAudioClipper`.

- [ ] **Step 3: Write the implementation**

Create `src/Diariz.Api/Services/AudioClipper.cs`:

```csharp
using System.Diagnostics;
using System.Globalization;

namespace Diariz.Api.Services;

/// <summary>Cuts a span out of a stored recording as 16 kHz mono WAV.
///
/// <para>Given a presigned URL, ffmpeg range-seeks into the object and transfers only what it reads, so a
/// five-second clip out of a 200 MB recording costs a few hundred kilobytes rather than the whole file.
/// The URL is an input to a local subprocess and never reaches a client.</para></summary>
public interface IAudioClipper
{
    Task<byte[]> ClipAsync(string sourceUrl, long fromMs, long toMs, CancellationToken ct = default);
}

public class FfmpegAudioClipper : IAudioClipper
{
    /// <summary>Longest clip this will produce. Assessment plays seconds of speech; without a cap a caller
    /// could ask for a whole meeting as uncompressed WAV.</summary>
    public const long MaxClipMs = 120_000;

    /// <summary>The command line, pure so its argument <em>order</em> - where ffmpeg's semantics live - is
    /// testable without ffmpeg installed.
    ///
    /// <para><c>-ss</c> precedes <c>-i</c> so the seek happens on input rather than by decoding and
    /// discarding, and the end is a duration (<c>-t</c>) rather than <c>-to</c>, which would otherwise be
    /// ambiguous about whether it is relative to the seek point.</para></summary>
    public static IReadOnlyList<string> Args(string sourceUrl, long fromMs, long toMs)
    {
        var start = Math.Max(0, fromMs);
        var duration = Math.Clamp(toMs - start, 0, MaxClipMs);
        return
        [
            "-nostdin", "-loglevel", "error", "-y",
            "-ss", Seconds(start),
            "-i", sourceUrl,
            "-t", Seconds(duration),
            "-vn", "-ac", "1", "-ar", "16000",
            "-f", "wav",
        ];
    }

    private static string Seconds(long ms) =>
        (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

    public async Task<byte[]> ClipAsync(string sourceUrl, long fromMs, long toMs, CancellationToken ct = default)
    {
        // A temp file rather than a pipe: writing WAV to a pipe leaves the RIFF length field unset, because
        // ffmpeg cannot seek back to fill it in, and browsers handle that inconsistently. Clips are seconds
        // long, so the file is small and the response gets a real Content-Length.
        var temp = Path.Combine(Path.GetTempPath(), $"clip-{Guid.NewGuid():N}.wav");
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var a in Args(sourceUrl, fromMs, toMs)) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(temp);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("ffmpeg did not start");
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}: {stderr}");

            return await File.ReadAllBytesAsync(temp, ct);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { /* best effort */ }
        }
    }
}
```

- [ ] **Step 4: Run and watch the tests pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~AudioClipperArgs"
```

Expected: 6 passed.

- [ ] **Step 5: Mutation-verify the seek order**

Move `"-ss", Seconds(start),` to after the `"-i", sourceUrl,` pair. Re-run: `Args_seek_before_input_so_the_seek_is_fast` must fail. Restore in place.

- [ ] **Step 6: Add ffmpeg to the API image**

In `src/Diariz.Api/Dockerfile`, extend the existing runtime `apt-get install` line and its comment:

```dockerfile
# postgresql-client gives pg_dump/pg_restore for the platform backup/restore (MaintenanceController). The
# client major must be >= the Postgres server major (PG16) - a newer client is fine, which the distro's
# default package satisfies. curl is for the container HEALTHCHECK (hits GET /health). ffmpeg cuts the
# short assessment clips served by the people clip endpoint: WAV/webm/m4a cannot be safely byte-sliced, so
# a real decoder is needed, and it range-seeks the object store rather than downloading whole recordings.
RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client curl ffmpeg \
    && rm -rf /var/lib/apt/lists/*
```

- [ ] **Step 7: Register the service**

In `src/Diariz.Api/Program.cs`, beside the other singletons:

```csharp
builder.Services.AddSingleton<IAudioClipper, FfmpegAudioClipper>();
```

- [ ] **Step 8: Build and commit**

```bash
dotnet build Diariz.slnx
```

```bash
git add src/Diariz.Api/Services/AudioClipper.cs src/Diariz.Api/Dockerfile src/Diariz.Api/Program.cs tests/Diariz.Api.Tests/AudioClipperArgsTests.cs
git commit -m "feat: clip audio spans with ffmpeg"
```

---

## Task 9: The clip endpoint

**Files:**
- Modify: `src/Diariz.Api/Controllers/PeopleController.cs`
- Create: `tests/Diariz.Api.Tests/PeopleClipEndpointTests.cs`

**Interfaces:**
- Consumes: `IAudioClipper` (Task 8), `IAudioStorage.GetPresignedReadUrlAsync` (Task 7).
- Produces: `GET /api/people/{id}/clip?speakerId={guid}&fromMs={n}&toMs={n}` -> `audio/wav`.

**Authorisation, in order:**
1. `ManagePeople`, or you are that person.
2. The speaker must be attributed to this person.
3. You own the recording, **or** you hold `ManageVoiceprints`.
4. The requested span must fall inside a segment belonging to that speaker in the recording's current transcription.

Rule 4 is what keeps the grant narrow: the permission does not let you ask for arbitrary offsets, only for audio the person actually spoke.

The controller needs `IAudioClipper` and `IAudioStorage` injected. **There is a second construction site** for `PeopleController` in the integration tests - update both, or the integration project stops compiling and unit-only runs will not tell you.

- [ ] **Step 1: Write the failing test**

Create `tests/Diariz.Api.Tests/PeopleClipEndpointTests.cs` covering, with a `FakeAudioClipper` recording the `(fromMs, toMs)` it was asked for:

```csharp
    [Fact] public async Task Clip_serves_wav_for_a_segment_the_person_spoke() { }
    [Fact] public async Task Clip_for_a_span_outside_any_of_that_speakers_segments_is_not_found() { }
    [Fact] public async Task Clip_from_another_users_recording_without_ManageVoiceprints_is_forbidden() { }
    [Fact] public async Task Clip_from_another_users_recording_with_ManageVoiceprints_is_served() { }
    [Fact] public async Task Clip_for_a_speaker_not_attributed_to_this_person_is_not_found() { }
    [Fact] public async Task Clip_when_the_audio_has_been_deleted_is_not_found() { }
    [Fact] public async Task Clip_passes_the_requested_span_through_to_the_clipper() { }
```

Fill each body following the pattern established in Task 5's `Seed` helper. Add `FakeAudioClipper` to `tests/Diariz.Api.TestSupport` (a fake, not a mocking library - this codebase has none):

```csharp
/// <summary>Records what the controller asked for, so a test can assert the span without ffmpeg.</summary>
public sealed class FakeAudioClipper : IAudioClipper
{
    public List<(string Url, long FromMs, long ToMs)> Calls { get; } = [];
    public byte[] Bytes { get; set; } = [0x52, 0x49, 0x46, 0x46]; // "RIFF"

    public Task<byte[]> ClipAsync(string sourceUrl, long fromMs, long toMs, CancellationToken ct = default)
    {
        Calls.Add((sourceUrl, fromMs, toMs));
        return Task.FromResult(Bytes);
    }
}
```

- [ ] **Step 2: Run and watch them fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PeopleClipEndpoint"
```

- [ ] **Step 3: Implement the endpoint**

```csharp
    [HttpGet("{id:guid}/clip")]
    [EndpointSummary("Play a clip of a person's speech")]
    [EndpointDescription(
        "Serves a short WAV clip of one span of audio, for judging whether a voice really is this person.\n\n" +
        "The span must fall inside a segment that this speaker spoke in the recording's current " +
        "transcription - you cannot request arbitrary offsets. Clips from a recording you do not own " +
        "require **Manage voiceprints**, and every such access is logged. Clips are capped at two minutes.")]
    [Produces("audio/wav")]
    public async Task<IActionResult> Clip(
        Guid id, [FromQuery] Guid speakerId, [FromQuery] long fromMs, [FromQuery] long toMs)
    {
        var person = await _db.People.FirstOrDefaultAsync(p => p.Id == id);
        if (person is null) return NotFound();
        if (!await CanManageBiometricsAsync(person)) return Forbid();

        var speaker = await _db.Speakers.FirstOrDefaultAsync(s => s.Id == speakerId && s.PersonId == id);
        if (speaker is null) return NotFound();

        var rec = await _db.Recordings.FirstOrDefaultAsync(r => r.Id == speaker.RecordingId);
        if (rec is null || rec.AudioDeletedAt is not null) return NotFound();

        var canAssess = await _permissions.HasAsync(UserId, PlatformPermission.ManageVoiceprints);
        if (rec.UserId != UserId && !canAssess) return Forbid();

        // The span must be audio this speaker actually produced. Without this the permission would grant
        // arbitrary offsets into someone else's meeting, which is precisely what it must not do.
        var currentTrId = await _db.Transcriptions
            .Where(t => t.RecordingId == rec.Id)
            .OrderByDescending(t => t.Version)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync();
        if (currentTrId is null) return NotFound();

        var covered = await _db.Segments.AnyAsync(s =>
            s.TranscriptionId == currentTrId
            && s.SpeakerLabel == speaker.Label
            && s.StartMs <= fromMs && s.EndMs >= toMs);
        if (!covered) return NotFound();

        if (rec.UserId != UserId)
            _logger.LogInformation(
                "Cross-owner assessment clip: user {UserId} played {FromMs}-{ToMs} of recording {RecordingId} as person {PersonId}",
                UserId, fromMs, toMs, rec.Id, id);

        var url = await _storage.GetPresignedReadUrlAsync(rec.BlobKey, TimeSpan.FromMinutes(5));
        return File(await _clipper.ClipAsync(url, fromMs, toMs), "audio/wav");
    }
```

Add `IAudioClipper _clipper`, `IAudioStorage _storage` and `ILogger<PeopleController> _logger` to the constructor, and update **both** construction sites (the unit test helper and `RbacIntegrationTests.cs`).

- [ ] **Step 4: Run and watch them pass**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~PeopleClipEndpoint"
```

- [ ] **Step 5: Mutation-verify the span guard**

Change `s.StartMs <= fromMs && s.EndMs >= toMs` to `true`. Re-run: `Clip_for_a_span_outside_any_of_that_speakers_segments_is_not_found` must fail. Restore in place.

- [ ] **Step 6: Build the whole solution**

```bash
dotnet build Diariz.slnx
```

Expected: no errors. A failure here is most likely the second `PeopleController` construction site in `RbacIntegrationTests.cs`.

- [ ] **Step 7: Commit**

```bash
git add src/Diariz.Api/Controllers/PeopleController.cs tests/Diariz.Api.Tests/PeopleClipEndpointTests.cs tests/Diariz.Api.TestSupport tests/Diariz.Api.IntegrationTests/RbacIntegrationTests.cs
git commit -m "feat: serve assessment clips of a person's speech"
```

---

## Task 10: Web client and playback helpers

**Files:**
- Modify: `apps/web/src/lib/types.ts`
- Modify: `apps/web/src/lib/api.ts`
- Create: `apps/web/src/lib/clipPlayback.ts`
- Create: `apps/web/src/lib/clipPlayback.test.ts`

**Interfaces:**
- Produces:
  ```ts
  export interface PersonAttribution {
    speakerId: string; recordingId: string; recordingName: string; speakerLabel: string;
    linkedBy: string; isTraining: boolean; voiceSampleId: string | null;
    speechMs: number; canAccessRecording: boolean;
  }
  api.getPersonAttributions(personId: string): Promise<PersonAttribution[]>
  api.setAttributionTraining(personId, speakerId, training: boolean): Promise<void>
  api.personClipUrl(personId, speakerId, fromMs, toMs): string
  // clipPlayback.ts
  export function clipQueue(segments: {startMs:number; endMs:number}[]): PlayRange[]
  ```

**Note:** the web client uses **axios**, not `fetch`. A test that stubs `fetch` will silently miss these calls.

- [ ] **Step 1: Write the failing test for the queue helper**

`apps/web/src/lib/clipPlayback.test.ts` - assert that `clipQueue` merges touching and overlapping ranges and sorts them, reusing the semantics already established by `speakerRanges`/`selectedRanges` in `segmentPlayback.ts`. If the merge logic is identical, **do not duplicate it** - export a shared `mergeRanges` from `segmentPlayback.ts` and have both call it, and let this test cover the shared function.

- [ ] **Step 2: Run and watch it fail**

```bash
cd apps/web && npx vitest run src/lib/clipPlayback.test.ts
```

- [ ] **Step 3: Implement the helper**

- [ ] **Step 4: Add the types**

`apps/web/src/lib/types.ts`:

```ts
/// One speaker attributed to a person, whether or not it trains their voiceprint. Strictly larger than
/// `PersonDetail.samples`: auto-identification links a speaker without creating a sample, which is why the
/// sample list alone looked arbitrary.
export interface PersonAttribution {
  speakerId: string;
  recordingId: string;
  recordingName: string;
  speakerLabel: string;
  /// "manual" or "auto" today; open so a new provenance needs no contract change.
  linkedBy: string;
  isTraining: boolean;
  /// Non-null even when `isTraining` is false - an excluded sample is not deleted, so re-including it is a
  /// toggle rather than a fresh enrolment.
  voiceSampleId: string | null;
  speechMs: number;
  /// False when you neither own the recording nor hold Manage voiceprints. The row is still listed.
  canAccessRecording: boolean;
}
```

- [ ] **Step 5: Add the client methods**

`apps/web/src/lib/api.ts`, near `getPerson`:

```ts
  async getPersonAttributions(personId: string): Promise<PersonAttribution[]> {
    const { data } = await http.get<PersonAttribution[]>(`/api/people/${personId}/attributions`);
    return data;
  },

  async setAttributionTraining(personId: string, speakerId: string, training: boolean): Promise<void> {
    await http.put(`/api/people/${personId}/attributions/${speakerId}/training`, { training });
  },
```

For the clip URL, follow whatever the existing `audioUrl` does about the bearer token: an `<audio src>` cannot send an `Authorization` header, so the clip endpoint needs the same `access_token` query-parameter treatment `Program.cs` already applies. Read `audioUrl` and `Program.cs`'s `OnMessageReceived` before writing this, and reuse the mechanism rather than inventing a second one.

- [ ] **Step 6: Run the web suite**

```bash
cd apps/web && npm test
```

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/lib
git commit -m "feat: web client for attributions and assessment clips"
```

---

## Task 11: The Voiceprint tab

**Files:**
- Modify: `apps/web/src/components/PersonVoiceprintTab.tsx`
- Create: `apps/web/src/components/PersonAttributionRow.tsx`
- Create: `apps/web/src/components/PersonAttributionRow.test.tsx`
- Modify: `apps/web/src/locales/{en,de,fr,es}/people.json`

**Interfaces:**
- Consumes: everything from Task 10.

**What changes:** the tab's list is driven by `getPersonAttributions` instead of `PersonDetail.samples`. Each row carries a training checkbox, the provenance, the speech duration, and a play button. The existing per-sample expand (segment tick boxes, "Recompute") stays, and is shown only for rows that **are** training and **can** be accessed.

- [ ] **Step 1: Write the failing component tests**

`apps/web/src/components/PersonAttributionRow.test.tsx`:

```tsx
  it("shows an auto-linked speaker that is not training", () => {});
  it("toggling the checkbox calls setAttributionTraining with the new value", async () => {});
  it("does not offer playback for a recording the caller cannot access", () => {});
  it("does not offer the training toggle without canManageBiometrics", async () => {});
  it("plays the speaker's first segment when the play button is pressed", async () => {});
```

Use `userEvent`, **not** `fireEvent`, for anything asserting a disabled control: `fireEvent.click` fires `onChange` on a disabled input, so a lock test written with it passes for a reason the browser never reproduces.

Mock `../lib/api` with `vi.mock`, render inside `MemoryRouter` + `QueryClientProvider` + `SelectionProvider`, following `components/RecordingsPanel.test.tsx`. Assert with plain expectations - **`jest-dom` is not installed here** and none of the 230+ existing web test files use its matchers.

- [ ] **Step 2: Run and watch them fail**

```bash
cd apps/web && npx vitest run src/components/PersonAttributionRow.test.tsx
```

- [ ] **Step 3: Build the row component, then rewire the tab**

Keep the existing `SampleRow` expand behaviour intact; it moves inside the new row rather than being rewritten.

- [ ] **Step 4: Add the locale keys to all four catalogs**

New keys in `people.json` (en, then de/fr/es properly translated with accents):

```json
  "attributionsTitle": "Recordings this person appears in",
  "attributionTraining": "Trains the voiceprint",
  "attributionLinkedAuto": "Recognised automatically",
  "attributionLinkedManual": "Named by hand",
  "attributionNoAccess": "In a recording you cannot access",
  "attributionPlay": "Play",
  "attributionStop": "Stop",
  "errTrainingFailed": "Could not change the training selection.",
```

- [ ] **Step 5: Run the web suite**

```bash
cd apps/web && npm test
```

- [ ] **Step 6: Verify in the browser**

The class-presence of a Tailwind utility proves nothing about layout, and jsdom computes no geometry. Start the dev server and check the real thing:

```bash
cd apps/web && npm run dev
```

Confirm: the list shows more rows than before (auto-linked speakers now appear); a toggle persists across a reload; playback plays only the speaker's own audio; a long recording name truncates rather than pushing the row off-screen (`truncate` on an inline span only sets `white-space: nowrap` - it needs a block-level element and `min-w-0` on the flex parent).

- [ ] **Step 7: Commit**

```bash
git add apps/web/src/components apps/web/src/locales
git commit -m "feat: list and manage every attributed speaker in the Voiceprint tab"
```

---

## Task 12: Contract regeneration and release

**Files:**
- Modify: the OpenAPI snapshot (regenerated)
- Modify: `integrations/n8n-nodes-diariz/**` (regenerated)
- Modify: `version.json` + all five mirrors
- Modify: `apps/web/src/lib/releases.ts`
- Modify: `README.md`, `docs/features.md`
- Modify: `docs/Overall_Synopsis_of_Platform.md`, `docs/Data_Schema.md`
- Modify: `apps/web/src/content/help/people-directory.md`

**This task exists because three of these are guarded by tests that fail the build, and one is not guarded at all.**

- [ ] **Step 1: Regenerate the OpenAPI snapshot**

`api/people` is in the published document (only `api/admin`, `api/platform`, `api/oauth` and `api/maintenance` are excluded), and three endpoints were added.

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~OpenApi"
```

The snapshot test **self-heals**: run 1 fails and rewrites the file, run 2 passes with no code change. Run it twice and commit the regenerated snapshot.

- [ ] **Step 2: Regenerate the n8n node**

`generated/index.ts` does **not** self-heal, and a stale one reds the "n8n community node" check - which stayed broken across three merged PRs because that check is not required.

```bash
cd integrations/n8n-nodes-diariz && npm run generate
```

- [ ] **Step 3: Bump the version and all five mirrors**

This is a **functional enhancement**, so Minor +1 and Build reset: `0.249.x` -> `0.250.0`.

`version.json`, `apps/web/package.json`, `apps/desktop/package.json`, `src/Diariz.Api/Diariz.Api.csproj` (`<Version>`), `integrations/n8n-nodes-diariz/package.json`, **and `apps/web/package-lock.json` (two places)** - CLAUDE.md names four mirrors but `versionMirrors.test.ts` asserts the lock file too.

- [ ] **Step 4: Add the release entry**

Prepend to `RELEASES` in `apps/web/src/lib/releases.ts` with `version: "0.250.0"`, today's date, the PR number, a headline, a PR-level prose `summary`, and `added`/`changed` bullets. Plain hyphens only.

**The `pr` field must be written before `gh pr create` exists to report the real number.** Do not guess "last + 1" - issues and Dependabot share the sequence. Check the current highest number first:

```bash
gh issue list --state all --limit 1 --json number && gh pr list --state all --limit 1 --json number
```

- [ ] **Step 5: Update the inventories in lockstep**

The README Features table row, the matching `docs/features.md` prose bullet, and the About-box `CAPABILITIES` table row in `releases.ts`. **All three or none** - no test reads `features.md`, so it is the one that silently goes stale.

- [ ] **Step 6: Update the reference docs**

`docs/Data_Schema.md`: the `ProfileContributions.ExcludedAt` column and a migration-history row.
`docs/Overall_Synopsis_of_Platform.md`: the new `ManageVoiceprints` permission, the clip endpoint, and **ffmpeg becoming an API-image dependency** (it is a deployment fact, not just a code one).

- [ ] **Step 7: Update the help article**

`apps/web/src/content/help/people-directory.md` - a user relies on the behaviour that the Voiceprint tab now lists every recording the person appears in and lets them choose which train the voiceprint. ASCII only; keep the front-matter `summary` to two or three sentences.

- [ ] **Step 8: Run everything**

```bash
dotnet build Diariz.slnx && dotnet test tests/Diariz.Api.Tests
```

```bash
cd apps/web && npm test && npm run build
```

```bash
dotnet test tests/Diariz.Api.IntegrationTests
```

- [ ] **Step 9: Open the issue-free PR**

This is a **feature**, not a fix, so no GitHub issue is needed unless the user asks. Push and open a PR against `main`:

```bash
git push -u origin feat/speaker-identification-quality
```

The PR body must state the **deployment surface**: server redeploy **with an API image rebuild** (ffmpeg is a new runtime dependency). No desktop release - nothing under `apps/desktop/src` changed. No worker rebuild in this phase.

---

## Self-review notes

**Spec coverage.** Phase 1 of the spec lists: ffmpeg in the API image (Task 8), the clipped-segment endpoint (Task 9), `ManageVoiceprints` (Tasks 1-2), playback in the Voiceprint tab (Tasks 10-11), the full candidate set with add/remove (Tasks 4-6, 11), and the cross-owner fix (Tasks 4, 5, 11). All covered.

**Deliberately deferred to later phases,** so an implementer does not build them here: the confirmation band and suggestions, the re-scan, the decision log, `ProfileVoiceprints` and clustering, `Segments.VoiceEmbedding`, the `segment-embed-jobs` stream, and the admin bench.

**Known risk.** Task 9's four-step authorisation is the security boundary of the whole feature, and rule 4 (the span must fall inside one of that speaker's segments) is the only thing keeping `ManageVoiceprints` from being arbitrary access to other users' audio. Its mutation check in Task 9 Step 5 is not optional.
