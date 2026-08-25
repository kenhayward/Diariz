# Speaker Identification Quality, Phase 3 - Voiceprint Diagnostics

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show which enrolled samples do not belong to the person they are filed under, so a training set can be cleaned before anything is built on top of it.

**Architecture:** Pure pgvector arithmetic over data that already exists. Every voice sample carries a `vector(192)` embedding, so "does this sample look like the others?" is a leave-one-out distance calculation - **no worker, no GPU, no new job stream, no new table**. The verdicts read off the thresholds Phase 2 already calibrated rather than inventing new numbers. Acting on a verdict needs nothing new either: the training toggle and clipped playback shipped in Phase 1.

**Tech Stack:** ASP.NET Core (.NET 10), EF Core + Npgsql/pgvector, React 19 + TypeScript + Vite + Tailwind v4, vitest, xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-08-25-speaker-identification-quality-design.md` section 10, Phase 3.

## Why this phase exists, and why it moved

Measured on the live instance, of 108 samples belonging to people with more than one:

| Nearest same-person sample is | Samples |
|---|---|
| Close (<= 0.30) - a real cluster | 33 |
| Loosely related (0.30 - 0.45) | 38 |
| **Alone (> 0.45)** | **37** |

The widest same-person pair is **1.134** - orthogonal, which two recordings of one human cannot be. The bulk
of within-person distances (0.5 - 0.9) overlaps the impostor range measured before Phase 2 (0.55 - 0.75).

Some of that is device variation. Some of it is **other people enrolled under one name**. Clustering cannot
tell the difference, and would promote a misattributed sample from a diluted nuisance into a sharp
false-accept. So this phase makes the difference visible first.

## Global Constraints

- **TDD is mandatory.** Failing test first, watch it fail, minimal code to pass.
- **Mutation-verify every new assertion**, restoring **by editing in place**. Use a **unique anchor**, not a short substring - a naive replace has corrupted a file twice in this project (`0.0` inside `0.050000000000000003`, and `db.Recordings` matching twice).
- **`git add -u` does not stage a new file.** Check `git status` for untracked files before every commit - a migration and a test file have each nearly shipped missing.
- **The build's `tsc` excludes test files**, so a component's new required prop will not fail the build from a test harness. Update harnesses by hand.
- **No em or en dashes in user-facing text.** Plain hyphen only.
- **All four locale catalogs** stay at exact key parity with no empty values (`src/locales.test.ts`). Translations use normal accented characters; the ASCII rule is for `content/help/**`.
- **`api/people` is in the published OpenAPI document**, so a new endpoint there needs the snapshot regenerated (run twice - it self-heals) **and** `npm run generate` in `integrations/n8n-nodes-diariz`. A new controller also needs a `TagDescriptions` entry in `OpenApi/OpenApiCuration.cs`.
- **Build `Diariz.slnx`**, not just the unit project, before pushing.
- `Person` maps to `SpeakerProfiles` and `VoiceSample` to `ProfileContributions`; `Speaker.PersonId` maps to `ProfileId`. **Do not rename them.**

---

## Task 1: The pure diagnosis

**Files:**
- Create: `src/Diariz.Api/Services/VoiceprintDiagnosis.cs`
- Create: `tests/Diariz.Api.Tests/VoiceprintDiagnosisTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum SampleVerdict { Core, Variant, Alone, Only }

  public record SampleDiagnosis(
      Guid SampleId, double? NearestSiblingDistance, double? DistanceToOthers, SampleVerdict Verdict);

  public static class VoiceprintDiagnosis
  {
      public static IReadOnlyList<SampleDiagnosis> Diagnose(
          IReadOnlyList<(Guid Id, float[] Embedding)> samples, IdentificationThresholds t);
  }
  ```

**The two numbers, and why both.**
- `NearestSiblingDistance` - the closest other sample of the same person. Answers "does this have company?"
- `DistanceToOthers` - distance to the centroid of **the person's other samples**, a true leave-one-out.
  Answers "would the rest of this voiceprint recognise this?"

They disagree in the case that matters most: a sample can sit close to one sibling (so its nearest distance
is small) while being far from the person's centre of mass. Reporting only one would hide half the picture.

**The verdicts reuse Phase 2's calibrated thresholds** rather than inventing new constants: within
`t.Accept` of its nearest sibling is `Core`, within `t.Suggest` is `Variant` (a different recording
condition), beyond that it is `Alone`. `Only` is the single-sample case, which has nothing to compare
against and must not be reported as an outlier.

- [ ] **Step 1: Write the failing test**

```csharp
using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>Which of a person's enrolled samples look like each other, and which do not.
///
/// <para>Measured on the live instance, a third of samples belonging to multi-sample people sit further than
/// 0.45 from their nearest sibling, and the widest same-person pair is 1.134 - orthogonal, which two
/// recordings of one human cannot be. Some of that is a phone versus a meeting room; some is a different
/// person enrolled under one name. This is what tells them apart.</para></summary>
public class VoiceprintDiagnosisTests
{
    private static readonly IdentificationThresholds T = new(0.30, 0.40, 0.05, 3000);

    /// <summary>A unit vector at a chosen angle in the first two dimensions, so distances are predictable.</summary>
    private static float[] At(double distance)
    {
        var cos = 1 - distance;
        var v = new float[192];
        v[0] = (float)cos;
        v[1] = (float)Math.Sqrt(Math.Max(0, 1 - (cos * cos)));
        return v;
    }

    private static float[] Origin() => At(0);

    [Fact]
    public void A_lone_sample_has_nothing_to_compare_against()
    {
        var id = Guid.NewGuid();

        var d = Assert.Single(VoiceprintDiagnosis.Diagnose([(id, Origin())], T));

        Assert.Equal(SampleVerdict.Only, d.Verdict);
        Assert.Null(d.NearestSiblingDistance);
        Assert.Null(d.DistanceToOthers);
    }

    [Fact]
    public void Two_close_samples_are_both_core()
    {
        var samples = new List<(Guid, float[])> { (Guid.NewGuid(), Origin()), (Guid.NewGuid(), At(0.1)) };

        var d = VoiceprintDiagnosis.Diagnose(samples, T);

        Assert.All(d, x => Assert.Equal(SampleVerdict.Core, x.Verdict));
    }

    [Fact]
    public void A_sample_within_the_band_is_a_variant_not_an_outlier()
    {
        // The same voice on a different microphone. It belongs, and saying otherwise would invite someone to
        // delete the very sample that teaches a second recording condition.
        var samples = new List<(Guid, float[])> { (Guid.NewGuid(), Origin()), (Guid.NewGuid(), At(0.35)) };

        Assert.All(VoiceprintDiagnosis.Diagnose(samples, T), x => Assert.Equal(SampleVerdict.Variant, x.Verdict));
    }

    [Fact]
    public void A_sample_beyond_the_band_sits_alone()
    {
        var lone = Guid.NewGuid();
        var samples = new List<(Guid, float[])>
        {
            (Guid.NewGuid(), Origin()), (Guid.NewGuid(), At(0.05)), (lone, At(0.9)),
        };

        var d = VoiceprintDiagnosis.Diagnose(samples, T);

        Assert.Equal(SampleVerdict.Alone, d.Single(x => x.SampleId == lone).Verdict);
        Assert.All(d.Where(x => x.SampleId != lone), x => Assert.Equal(SampleVerdict.Core, x.Verdict));
    }

    [Fact]
    public void Nearest_sibling_is_the_closest_one_not_the_average()
    {
        // A pair inside a scattered set is still a pair. Averaging would drown that signal and report three
        // outliers where there are two plus a couple.
        var a = Guid.NewGuid();
        var samples = new List<(Guid, float[])>
        {
            (a, Origin()), (Guid.NewGuid(), At(0.05)), (Guid.NewGuid(), At(0.95)),
        };

        var d = VoiceprintDiagnosis.Diagnose(samples, T).Single(x => x.SampleId == a);

        Assert.True(d.NearestSiblingDistance < 0.1);
    }

    [Fact]
    public void Distance_to_others_excludes_the_sample_itself()
    {
        // Leave-one-out or it is not a test. Including the sample pulls the centroid toward it, and every
        // sample then looks like it belongs - the exact failure this whole phase exists to avoid.
        var odd = Guid.NewGuid();
        var samples = new List<(Guid, float[])>
        {
            (Guid.NewGuid(), Origin()), (Guid.NewGuid(), Origin()), (Guid.NewGuid(), Origin()), (odd, At(0.8)),
        };

        var d = VoiceprintDiagnosis.Diagnose(samples, T).Single(x => x.SampleId == odd);

        Assert.True(d.DistanceToOthers > 0.7, $"expected the odd sample to be far from the rest, got {d.DistanceToOthers}");
    }

    [Fact]
    public void Nothing_at_all_diagnoses_nothing()
    {
        Assert.Empty(VoiceprintDiagnosis.Diagnose([], T));
    }
}
```

- [ ] **Step 2: Run and watch it fail**

```bash
dotnet test tests/Diariz.Api.Tests --filter "FullyQualifiedName~VoiceprintDiagnosis"
```

- [ ] **Step 3: Implement.** Reuse `Voiceprints.Centroid` for the leave-one-out centre rather than writing
  a second mean - one implementation of "the average of these vectors" is the point.

- [ ] **Step 4: Run and watch them pass**

- [ ] **Step 5: Mutation-verify three**
  - Include the sample in its own centroid -> `Distance_to_others_excludes_the_sample_itself` fails.
  - Use the average sibling distance instead of the minimum -> `Nearest_sibling_is_the_closest_one_not_the_average` fails.
  - Collapse `Variant` into `Alone` -> `A_sample_within_the_band_is_a_variant_not_an_outlier` fails.

- [ ] **Step 6: Commit**

---

## Task 2: The per-person endpoint

**Files:**
- Modify: `src/Diariz.Api/Controllers/PeopleController.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Create: `tests/Diariz.Api.IntegrationTests/VoiceprintDiagnosticsIntegrationTests.cs`

**Interfaces:**
- Produces: `GET /api/people/{id}/diagnostics` ->
  ```csharp
  public record VoiceprintDiagnosticsDto(
      IReadOnlyList<SampleDiagnosisDto> Samples, int AloneCount, double? WidestPair);

  public record SampleDiagnosisDto(
      Guid VoiceSampleId, Guid SpeakerId, Guid RecordingId, string RecordingName, string SpeakerLabel,
      double? NearestSiblingDistance, double? DistanceToOthers, string Verdict, bool IsTraining);
  ```

**Integration, not unit.** The embeddings are `vector(192)`, which the in-memory provider `Ignore`s - a unit
test would diagnose a set of nulls and prove nothing.

**Excluded samples are diagnosed but marked**, not hidden: seeing that a sample you already dropped was the
outlier is the confirmation that dropping it was right.

- [ ] **Step 1: Write the failing integration test** covering: a person whose samples are all close reports
  no outliers; a person with one distant sample reports it as `Alone` and names its recording; a person with
  one sample reports `Only`; `ManagePeople` is required; an unknown person is 404.
- [ ] **Steps 2-6:** fail, implement, pass, mutation-verify the leave-one-out and the permission, commit.

---

## Task 3: The directory ranking

**Files:**
- Modify: `src/Diariz.Api/Controllers/PeopleController.cs`
- Modify: `src/Diariz.Api/Contracts/ApiDtos.cs`
- Modify: `tests/Diariz.Api.IntegrationTests/VoiceprintDiagnosticsIntegrationTests.cs`

**Interfaces:**
- Produces: `GET /api/people/diagnostics` -> `IReadOnlyList<PersonDiagnosticsSummaryDto>` ordered worst first.

**Why this task is not optional.** There are 91 people. A per-person view alone means opening 91 cards to
find the 13 with a problem, which nobody will do - so the diagnosis would exist and go unread.

Ordered by outlier count, then by widest pair. People with one sample or none sort last: they have nothing
wrong, only nothing to say.

- [ ] Steps as Task 2.

---

## Task 4: The Diagnostics tab

**Files:**
- Create: `apps/web/src/components/PersonDiagnosticsTab.tsx` + test
- Modify: `apps/web/src/components/PersonEditor.tsx` (third tab)
- Modify: `apps/web/src/lib/api.ts`, `types.ts`
- Modify: the four locale catalogs

`PersonEditor` already keeps panels **hidden rather than unmounted** so a half-typed edit survives a tab
switch; the third tab follows that rule, and the tab shell already has a test asserting it.

Each row shows the recording, the verdict in words rather than a number, the two distances, and - reusing
Phase 1 - a **play button** and the **training toggle**. Diagnosing without being able to listen or act would
be a report rather than a tool.

**Wording matters more than usual here.** "Alone" must not read as "wrong": the honest sentence is that this
sample does not resemble the others and is worth listening to, because it is either another recording
condition or another person, and only the user can tell.

- [ ] Steps as Phase 2 Task 10, including `userEvent` rather than `fireEvent` for any disabled control, and
  a browser check that a long recording name truncates rather than pushing the row off-screen.

---

## Task 5: The directory view

**Files:**
- Modify: `apps/web/src/components/PeopleModal.tsx` + test

A "Voiceprint health" affordance listing people worst-first, each row opening that person's Diagnostics tab.
Hidden entirely when nothing has an outlier - an empty problem list is noise.

- [ ] Steps as Task 4.

---

## Task 6: Release

- [ ] **Step 1: Regenerate** the OpenAPI snapshot (twice) and the n8n node.
- [ ] **Step 2: Version 0.251.x -> 0.252.0** (functional enhancement) across **six** mirrors, including
      `apps/web/package-lock.json` in both places npm writes it.
- [ ] **Step 3: `RELEASES[0]`** - check the real PR number first; issues and Dependabot share the sequence,
      and main may have moved while this branch was open.
- [ ] **Step 4: README Features row, `docs/features.md` bullet, CAPABILITIES row - all three or none.**
- [ ] **Step 5: `docs/Overall_Synopsis_of_Platform.md`** - the diagnosis, its two numbers, and why it runs
      before clustering. **No `Data_Schema.md` change: this phase adds no columns and no tables.**
- [ ] **Step 6: Help article** - `people-directory.md`, on what "does not resemble the others" means and what
      to do about it. ASCII only.
- [ ] **Step 7: Full suites**, then `git status` for untracked files, then PR.
      Deployment surface: **server redeploy only**.

---

## Self-review notes

**Scope.** Deliberately no new tables, no new columns, no migration, no worker change. Everything here is
arithmetic over embeddings that already exist, which is why this phase can precede clustering rather than
depend on it.

**Deferred to Phase 4/5, so an implementer does not build them here:** `ProfileVoiceprints` and clustering,
`Segments.VoiceEmbedding`, the `segment-embed-jobs` stream, and the admin threshold sweep. In particular this
phase diagnoses **whole samples**, not the lines inside one - segment-level crosstalk is Phase 5 and needs
the GPU.

**Known risk.** The verdict bands are borrowed from the identification thresholds, which were calibrated for
a different question (is this speaker that person?) than the one asked here (do these two recordings of one
person resemble each other?). They are a reasonable starting point and they keep the number of tunable
constants down, but if the live data makes `Variant` or `Alone` look consistently wrong, they should become
their own settings rather than being quietly redefined.
